using System.Diagnostics;
using System.Globalization;
using Attendance.Application.Abstractions;
using Attendance.Infrastructure.Security;

namespace Attendance.LoadTest;

/// <summary>
/// ClockInXtra capacity harness.
/// </summary>
/// <remarks>
/// <para>
/// What it answers: how many clock-ins a second one application node and this
/// database sustain, and which of the two is the limit. Attendance has one shape
/// of load that matters — everybody arriving at once — and the only honest way
/// to size for it is to measure it.
/// </para>
/// <para>
/// What it is not: a correctness test. That is the test suites' job, and this
/// project is deliberately outside the test run.
/// </para>
/// <para>
/// <c>dotnet run --project tests/Attendance.LoadTest -- --employees 200 --concurrency 32</c>
/// </para>
/// </remarks>
internal static class Runner
{
    /// <summary>Runs one capacity measurement.</summary>
    /// <returns>0 when every clock-in was accepted, 1 for a bad argument, 2 otherwise.</returns>
    public static async Task<int> Main(string[] args)
    {
        LoadTestOptions options;

        try
        {
            options = LoadTestOptions.Parse(args);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(LoadTestOptions.Usage);
            return 1;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(LoadTestOptions.Usage);
            return 0;
        }

        Console.WriteLine("ClockInXtra capacity harness");
        Console.WriteLine("============================");
        Write($"Machine     : {Environment.MachineName}, {Environment.ProcessorCount} logical processors");
        Write($"Employees   : {options.Employees}");
        Write($"Concurrency : {options.Concurrency}");
        Console.WriteLine();

        MeasurePasswordCost();

        await using LoadTestFixture fixture = new(options);

        try
        {
            Console.WriteLine("Provisioning employees, devices and credentials...");
            await fixture.ProvisionAsync().ConfigureAwait(false);

            Console.WriteLine("Clocking in...");

            Stopwatch clock = Stopwatch.StartNew();
            RunResult result = await fixture.RunAsync().ConfigureAwait(false);
            clock.Stop();

            Report(options, result, clock.Elapsed);

            return result.Accepted == options.Employees ? 0 : 2;
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("Cleaning up...");
            await fixture.CleanUpAsync().ConfigureAwait(false);
            Console.WriteLine("Done. Settings restored.");
        }
    }

    /// <summary>
    /// Measures one password verification, which sets the per-core ceiling.
    /// </summary>
    /// <remarks>
    /// Every clock-in verifies a password with PBKDF2-HMAC-SHA512 at 220,000
    /// iterations. That cost is deliberate — it is what makes a stolen credential
    /// table expensive to attack — and it is the largest fixed piece of CPU in
    /// the request, so it caps throughput before anything else does.
    /// </remarks>
    private static void MeasurePasswordCost()
    {
        Console.WriteLine("Measuring password verification...");

        Pbkdf2PasswordHasher hasher = new();
        const string password = "Capacity-Probe-Password-1!";
        PasswordHash stored = hasher.Hash(password);

        // Warm up: the first call pays for JIT and for pages not yet resident.
        _ = hasher.Verify(password, stored);

        const int iterations = 20;
        Stopwatch clock = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            _ = hasher.Verify(password, stored);
        }

        clock.Stop();

        double perVerification = clock.Elapsed.TotalMilliseconds / iterations;
        double perCore = 1000.0 / perVerification;

        Write($"  One verification : {perVerification:F1} ms of CPU");
        Write($"  Ceiling          : ~{perCore:F1} clock-ins/second/core, ~{perCore * Environment.ProcessorCount:F0} on this machine");
        Console.WriteLine();
    }

    private static void Report(LoadTestOptions options, RunResult result, TimeSpan elapsed)
    {
        double seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        double perSecond = result.Accepted / seconds;

        Console.WriteLine();
        Console.WriteLine("Result");
        Console.WriteLine("------");
        Write($"  Clock-ins accepted : {result.Accepted} of {options.Employees}");
        Write($"  Elapsed            : {elapsed.TotalSeconds:F1} s");
        Write($"  Throughput         : {perSecond:F1} clock-ins/second");
        Write($"  Latency p50        : {result.Percentile(50):F0} ms");
        Write($"  Latency p95        : {result.Percentile(95):F0} ms");
        Write($"  Latency p99        : {result.Percentile(99):F0} ms");
        Write($"  Slowest            : {result.Percentile(100):F0} ms");

        if (result.Refusals.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Refused:");

            foreach ((string reason, int count) in result.Refusals.OrderByDescending(entry => entry.Value))
            {
                Write($"    {reason,-34} {count}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("What this means for sizing");
        Console.WriteLine("--------------------------");
        Console.WriteLine("  Headcount   Arrival window   Needed/second   Nodes at this rate");

        foreach (int headcount in (int[])[100, 250, 500, 1000, 2500, 5000])
        {
            foreach (int windowMinutes in (int[])[30, 10])
            {
                double needed = headcount / (windowMinutes * 60.0);
                int nodes = (int)Math.Max(1, Math.Ceiling(needed / perSecond));

                Write($"  {headcount,9}   {windowMinutes,10} min   {needed,13:F2}   {nodes,18}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  The 10-minute rows are the ones to plan against: a shift that starts on");
        Console.WriteLine("  the hour produces its whole day's clock-ins in a few minutes.");
        Console.WriteLine();
        Console.WriteLine("  Measured in process, against a development database: a production node");
        Console.WriteLine("  adds TLS, the reverse proxy and the network, while a production database");
        Console.WriteLine("  is usually faster than a workstation, and this one was also running SQL");
        Console.WriteLine("  Server and the harness itself. Re-run this on the real servers before");
        Console.WriteLine("  committing to a topology.");
    }

    private static void Write(FormattableString line) =>
        Console.WriteLine(line.ToString(CultureInfo.InvariantCulture));
}
