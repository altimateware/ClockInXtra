using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Attendance.Api.Tests;
using Attendance.Application.Abstractions;
using Attendance.Domain.ValueObjects;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace Attendance.LoadTest;

/// <summary>
/// Everything the run needs: employees, devices, credentials, an office, and
/// attendance settings that permit a clock-in at whatever time the run happens.
/// </summary>
/// <remarks>
/// <para>
/// <b>It borrows the database and gives it back.</b> Every setting it changes is
/// read first and written back in <see cref="CleanUpAsync"/>, and every row it
/// creates is removed, except audit ledger rows, which are append-only by
/// design. Nothing here may be pointed at production: it creates employees, it
/// moves the clock-in window, and it leaves permanent audit entries.
/// </para>
/// <para>
/// <b>Why it moves the window.</b> Clock-in is open 00:00–08:30 Lagos time
/// (DEC-06). A capacity run at three in the afternoon would measure how fast the
/// system says no. The window is widened for the run and put back afterwards.
/// </para>
/// </remarks>
internal sealed class LoadTestFixture : IAsyncDisposable
{
    private const string Password = "Load-Test-Password-1!";
    private const string OfficeName = "LOADTEST office";

    private readonly LoadTestOptions _options;
    private readonly List<VirtualEmployee> _employees = [];
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    private readonly Dictionary<string, string?> _originalSettings = [];
    private int _officeLocationId;

    public LoadTestFixture(LoadTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder
                .UseEnvironment("Development")

                // A console project gets none of the content-root plumbing a
                // test project is given, so the API's own folder is named here.
                .UseContentRoot(ApiContentRoot())

                // The point is to measure the system, not the rate limiter: a
                // real morning has one request per phone, not two hundred.
                .UseSetting("Api:RateLimits:AttendancePerMinute", "100000")
                .UseSetting("Api:RateLimits:ReadPerMinute", "100000")

                // Development logs every request to the console at Debug, which
                // would be most of what the run measured.
                .UseSetting("Serilog:MinimumLevel:Default", "Warning")
                .UseSetting("Serilog:MinimumLevel:Override:Microsoft.AspNetCore", "Warning"));
    }

    /// <summary>Creates the fixture data and relaxes the attendance window.</summary>
    public async Task ProvisionAsync()
    {
        // Settings first, host second: the attendance policy is cached in
        // process for 30 seconds, so a host that starts before the window is
        // widened can serve a stale policy for the first part of the run.
        await SnapshotAndRelaxSettingsAsync().ConfigureAwait(false);

        IPasswordHasher hasher = _factory.Services.GetRequiredService<IPasswordHasher>();
        ISecretProtector protector = _factory.Services.GetRequiredService<ISecretProtector>();

        await using SqlConnection connection = new(_options.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        _officeLocationId = await connection.QuerySingleAsync<int>(
            """
            INSERT INTO core.OfficeLocation (Name, Latitude, Longitude, AllowedRadiusMeters, Status)
            VALUES (@name, 6.465422, 3.406448, 500.00, 1);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """,
            new { name = $"{OfficeName} {_tag}" }).ConfigureAwait(false);

        PasswordHash credential = hasher.Hash(Password);

        for (int i = 0; i < _options.Employees; i++)
        {
            ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] totpSecret = KeyGeneration.GenerateRandomKey(20);

            ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
            byte[] publicKey = new byte[65];
            publicKey[0] = 0x04;
            parameters.Q.X!.CopyTo(publicKey, 1);
            parameters.Q.Y!.CopyTo(publicKey, 33);

            string userId = $"loadtest.{_tag}.{i.ToString(CultureInfo.InvariantCulture)}";

            // One hash, reused: hashing 200 passwords at 220,000 iterations each
            // would take longer than the run it is preparing for, and the
            // measurement is of verification, not of setup.
            Guid devicePublicId = await connection.QuerySingleAsync<Guid>(
                """
                INSERT INTO core.MobileUser (UserId, FirstName, LastName, Status)
                VALUES (@userId, N'Load', N'Test', 1);
                DECLARE @mobileUserId INT = SCOPE_IDENTITY();

                INSERT INTO core.EmployeeCredential (MobileUserId, HashFormat, Iterations, Salt, PasswordHash, MustChange)
                VALUES (@mobileUserId, @format, @iterations, @salt, @hash, 0);

                INSERT INTO core.MfaCredential (MobileUserId, SecretProtected, Status, ActivatedUtc)
                VALUES (@mobileUserId, @secret, 1, SYSUTCDATETIME());

                INSERT INTO core.Device (MobileUserId, PublicKey, PublicKeyThumbprint, Platform,
                                         AttestationLevel, Status, DeviceModel, ApprovedUtc)
                VALUES (@mobileUserId, @publicKey, HASHBYTES('SHA2_256', @publicKey), 1, 2, 1,
                        N'LOADTEST', SYSUTCDATETIME());

                SELECT DevicePublicId FROM core.Device WHERE DeviceId = SCOPE_IDENTITY();
                """,
                new
                {
                    userId,
                    format = credential.HashFormat,
                    iterations = credential.Iterations,
                    salt = credential.Salt,
                    hash = credential.Hash,
                    secret = protector.Protect(SecretPurposes.MobileUserTotpSecret, totpSecret),
                    publicKey,
                }).ConfigureAwait(false);

            _employees.Add(new VirtualEmployee(userId, key, devicePublicId, new Totp(totpSecret)));
        }
    }

    /// <summary>Clocks everybody in, <see cref="LoadTestOptions.Concurrency"/> at a time.</summary>
    public async Task<RunResult> RunAsync()
    {
        ConcurrentBag<double> latencies = [];
        ConcurrentDictionary<string, int> refusals = new(StringComparer.Ordinal);
        int accepted = 0;

        using HttpClient client = _factory.CreateClient();
        using SemaphoreSlim gate = new(_options.Concurrency);

        await Task.WhenAll(_employees.Select(async employee =>
        {
            await gate.WaitAsync().ConfigureAwait(false);

            try
            {
                SigningHttpClient signer = new(client, employee.Key, employee.DevicePublicId.ToString());

                string json = JsonSerializer.Serialize(new
                {
                    userId = employee.UserId,
                    password = Password,
                    authenticatorCode = employee.Totp.ComputeTotp(),
                    position = new
                    {
                        latitude = 6.465422,
                        longitude = 3.406448,
                        accuracyMeters = 5.0,
                        isMocked = false,
                    },
                });

                Stopwatch clock = Stopwatch.StartNew();

                HttpResponseMessage response = await signer.SendAsync(
                    HttpMethod.Post,
                    "/api/v1/mobile/attendance/clock-in",
                    json,
                    CancellationToken.None,
                    Guid.NewGuid().ToString()).ConfigureAwait(false);

                clock.Stop();
                latencies.Add(clock.Elapsed.TotalMilliseconds);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    Interlocked.Increment(ref accepted);
                }
                else
                {
                    refusals.AddOrUpdate(
                        await DescribeAsync(response).ConfigureAwait(false), 1, (_, count) => count + 1);
                }

                response.Dispose();
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return new RunResult(accepted, [.. latencies], refusals.ToDictionary(StringComparer.Ordinal));
    }

    /// <summary>Removes everything this run created and restores every setting.</summary>
    public async Task CleanUpAsync()
    {
        foreach (VirtualEmployee employee in _employees)
        {
            employee.Key.Dispose();
        }

        await using SqlConnection connection = new(_options.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        // Children before parents; ledger rows stay, by design.
        await connection.ExecuteAsync(
            """
            DECLARE @users TABLE (MobileUserId INT PRIMARY KEY);
            INSERT INTO @users SELECT MobileUserId FROM core.MobileUser WHERE UserId LIKE @pattern;

            DELETE FROM core.RequestNonce       WHERE DeviceId IN (SELECT DeviceId FROM core.Device WHERE MobileUserId IN (SELECT MobileUserId FROM @users));
            DELETE FROM core.RequestIdempotency WHERE DeviceId IN (SELECT DeviceId FROM core.Device WHERE MobileUserId IN (SELECT MobileUserId FROM @users));
            DELETE FROM core.AttendanceEvent    WHERE MobileUserId IN (SELECT MobileUserId FROM @users);
            DELETE FROM core.Attendance         WHERE MobileUserId IN (SELECT MobileUserId FROM @users);
            DELETE FROM core.AuthenticationAttempt WHERE SubjectKey LIKE @pattern;
            DELETE FROM core.Device             WHERE MobileUserId IN (SELECT MobileUserId FROM @users);
            DELETE FROM core.MfaCredential      WHERE MobileUserId IN (SELECT MobileUserId FROM @users);
            DELETE FROM core.EmployeeCredential WHERE MobileUserId IN (SELECT MobileUserId FROM @users);
            DELETE FROM core.MobileUser         WHERE MobileUserId IN (SELECT MobileUserId FROM @users);
            DELETE FROM core.OfficeLocation     WHERE Name = @office;
            """,
            new { pattern = $"loadtest.{_tag}.%", office = $"{OfficeName} {_tag}" }).ConfigureAwait(false);

        foreach ((string key, string? value) in _originalSettings)
        {
            await connection.ExecuteAsync(
                "UPDATE core.ApplicationSetting SET SettingValue = @value WHERE SettingKey = @key",
                new { key, value }).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Widens the clock-in window for the run, remembering what was there.
    /// </summary>
    private async Task SnapshotAndRelaxSettingsAsync()
    {
        string[] keys =
        [
            "Attendance.ClockInOpenTime",
            "Attendance.ClockInCloseTime",
            "Attendance.ClockInAfterCloseAction",
            "Location.MaxAcceptedAccuracyMeters",
        ];

        await using SqlConnection connection = new(_options.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        foreach (string key in keys)
        {
            _originalSettings[key] = await connection.QuerySingleOrDefaultAsync<string?>(
                "SELECT SettingValue FROM core.ApplicationSetting WHERE SettingKey = @key",
                new { key }).ConfigureAwait(false);
        }

        // 23:59:59, not 23:59: a run that crosses the last minute of the day
        // would otherwise be refused in full and report zero throughput, which
        // looks like a capacity finding and is not one. The late-clock-in action
        // is relaxed for the same reason, in case the day turns mid-run.
        await connection.ExecuteAsync(
            """
            UPDATE core.ApplicationSetting SET SettingValue = N'00:00'    WHERE SettingKey = 'Attendance.ClockInOpenTime';
            UPDATE core.ApplicationSetting SET SettingValue = N'23:59:59' WHERE SettingKey = 'Attendance.ClockInCloseTime';
            UPDATE core.ApplicationSetting SET SettingValue = N'AcceptAndFlagLate' WHERE SettingKey = 'Attendance.ClockInAfterCloseAction';
            UPDATE core.ApplicationSetting SET SettingValue = N'50'       WHERE SettingKey = 'Location.MaxAcceptedAccuracyMeters';
            """).ConfigureAwait(false);
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        try
        {
            using JsonDocument body = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync().ConfigureAwait(false));

            return body.RootElement.TryGetProperty("code", out JsonElement code)
                ? code.GetString() ?? response.StatusCode.ToString()
                : response.StatusCode.ToString();
        }
        catch (JsonException)
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }

    /// <summary>
    /// The API project folder, found by walking up to the solution file, so the
    /// harness works from wherever it was started.
    /// </summary>
    private static string ApiContentRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClockInXtra.slnx")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("Run this from inside the ClockInXtra working copy.")
            : Path.Combine(directory.FullName, "src", "Attendance.Api");
    }

    private sealed record VirtualEmployee(string UserId, ECDsa Key, Guid DevicePublicId, Totp Totp);
}

/// <summary>What one run produced.</summary>
internal sealed record RunResult(int Accepted, IReadOnlyList<double> Latencies, IReadOnlyDictionary<string, int> Refusals)
{
    /// <summary>The latency at the given percentile, in milliseconds.</summary>
    public double Percentile(int percentile)
    {
        if (Latencies.Count == 0)
        {
            return 0;
        }

        double[] sorted = [.. Latencies.Order()];
        int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;

        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
