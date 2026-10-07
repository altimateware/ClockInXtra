using Microsoft.Data.SqlClient;

namespace Attendance.Tests;

/// <summary>
/// How every test that opens a SQL Server connection or hosts an application
/// reaches it, and the waiting limits that keep those tests honest on a loaded
/// machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Under CPU starvation — freshly built binaries being
/// scanned, SQL Server busy, six test projects run back to back — the API suite
/// failed in bulk, 21 of its 57 tests in one observed run, while the code under
/// test was sound. Reproduced by saturating every core with high-priority busy
/// loops, the failures came from four waiting limits. Not one of them is
/// something a test asserts:
/// </para>
/// <list type="number">
///   <item>SQL Server's default 15-second connect timeout. An observed login
///   took 37 seconds.</item>
///   <item>Dapper's default 30-second command timeout, exceeded by the tests'
///   own setup and verification queries.</item>
///   <item>SqlClient's pool <i>blocking period</i>: after one failed open,
///   every open on that pool fails instantly with the same cached error for
///   five seconds, doubling on each further failure up to a minute. One slow
///   login took down every fixture in a class with an identical message.</item>
///   <item><see cref="HttpClient"/>'s default 100-second timeout, which the
///   first request — the one that also builds the whole hosted application —
///   exceeded.</item>
/// </list>
/// <para>
/// Lengthening them weakens nothing: no test asserts how long a machine is
/// allowed to take. The alternative, retrying whatever failed, would have
/// hidden real defects, so nothing here retries.
/// </para>
/// <para>
/// This file is linked into every test project that opens a connection or hosts
/// an application, so the suite has one definition of the database it runs
/// against. It is deliberately <i>not</i> linked into
/// <c>Attendance.LoadTest</c>: a capacity harness has to feel the saturation it
/// is measuring.
/// </para>
/// <para>
/// <b>These timeouts are for tests only.</b> What a production connection
/// string should say is a separate decision, with the opposite trade-off —
/// there, a long connect timeout holds request threads during an outage. See
/// TD-16 in <c>docs/architecture/01-requirements-register.md</c>.
/// </para>
/// </remarks>
internal static class TestEnvironment
{
    /// <summary>
    /// The environment variable a build agent sets to point the integration
    /// tests at its own instance. Its value still gets the limits below.
    /// </summary>
    public const string ConnectionVariable = "CLOCKINXTRA_TEST_CONNECTION";

    /// <summary>
    /// Connect and command timeout, in seconds, for every test connection, and
    /// the ceiling a hosted application's configuration validator is given.
    /// </summary>
    public const int DatabaseTimeoutSeconds = 120;

    /// <summary>
    /// A request, including the one that starts a hosted application, may take
    /// this long on a starved machine before a test gives up on it.
    /// </summary>
    /// <remarks>
    /// Five minutes was not enough: a single portal sign-in POST was measured at
    /// <b>306 seconds</b> under starvation, and the test that hit it failed with
    /// "the client aborted the request" — an error that says nothing about the
    /// code under test. Ten minutes is twice the worst observed request. It is a
    /// ceiling on how long a hung request wastes before the run moves on, not a
    /// target, and no test asserts anything about it.
    /// </remarks>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The database the integration tests run against, tolerant of a slow
    /// server.
    /// </summary>
    public static readonly string ConnectionString = Tolerant(
        Environment.GetEnvironmentVariable(ConnectionVariable) ?? LocalInstance);

    /// <summary>
    /// The local developer instance. <c>TrustServerCertificate=True</c> is for
    /// a developer machine with a self-signed certificate only; production uses
    /// organisation PKI and must not set it.
    /// </summary>
    private const string LocalInstance =
        "Server=.;Database=ClockInXtra;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True";

    /// <summary>
    /// Applies the waiting limits to a connection string, whether it came from
    /// <see cref="LocalInstance"/> or from a build agent.
    /// </summary>
    private static string Tolerant(string connectionString) =>
        new SqlConnectionStringBuilder(connectionString)
        {
            ConnectTimeout = DatabaseTimeoutSeconds,
            CommandTimeout = DatabaseTimeoutSeconds,

            // Auto — the default — leaves the blocking period ON for every
            // endpoint that is not Azure SQL, which is every endpoint here.
            PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock,
        }.ConnectionString;
}
