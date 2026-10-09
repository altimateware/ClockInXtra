using Attendance.Infrastructure.Deployment;
using Attendance.Tests;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// A host creating its own database on first run, including two hosts starting
/// at the same moment.
/// </summary>
/// <remarks>
/// <para>
/// These run against a throwaway database with a name of their own, created and
/// dropped by the test. They deliberately do not touch the development
/// database: the thing under test is what happens when the database is
/// <i>absent</i>, which cannot be arranged on a database everything else is
/// using.
/// </para>
/// <para>
/// The concurrency case is the reason this file exists. The API and the portal
/// are separate processes, usually started together, and either may find nothing
/// there. Two deployments racing could both run <c>CREATE DATABASE</c>, or
/// interleave the object scripts and leave half a schema. Each
/// <c>DeployAsync</c> opens its own connection, so running two here races two
/// real sessions against each other exactly as two processes would.
/// </para>
/// </remarks>
public sealed class DatabaseDeployerTests : IAsyncLifetime
{
    private readonly string _database = $"ClockInXtra_Deploy_{Guid.NewGuid():N}"[..40];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // Always drop it, including when an assertion failed: a leftover
        // database would make the next run start from somewhere unknown.
        await using SqlConnection master = new(MasterConnectionString());
        await master.OpenAsync();

        await master.ExecuteAsync(
            $"""
            IF DB_ID(@name) IS NOT NULL
            BEGIN
                ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_database}];
            END
            """,
            new { name = _database });
    }

    [Fact]
    public async Task CreatesTheDatabaseAndEveryObjectInItFromNothing()
    {
        Assert.False(await DatabaseExistsAsync(), "the test database existed before the test ran");

        await Deployer().DeployAsync(TestContext.Current.CancellationToken);

        Assert.True(await DatabaseExistsAsync());

        // The tables, the procedures and the seeded reference data: a database
        // that exists but is empty would be worse than none at all, because the
        // host would start and fail on the first request instead.
        Assert.NotEqual(0, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.tables WHERE schema_id IN (SCHEMA_ID('core'), SCHEMA_ID('audit'))"));
        Assert.NotEqual(0, await ScalarAsync<int>("SELECT COUNT(*) FROM sys.procedures"));
        Assert.NotEqual(0, await ScalarAsync<int>("SELECT COUNT(*) FROM core.Permission"));
        Assert.NotEqual(0, await ScalarAsync<int>("SELECT COUNT(*) FROM core.ApplicationSetting"));

        // The controls the deployment script itself checks for, asserted here
        // too so a silent regression in the runner cannot pass unnoticed.
        Assert.Equal(2, await ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM sys.tables
            WHERE object_id IN (OBJECT_ID('audit.AuditLog'), OBJECT_ID('audit.SecurityEvent'))
              AND ledger_type_desc = 'APPEND_ONLY_LEDGER_TABLE'
            """));

        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_Device_ActiveUser' AND has_filter = 1"));

        // And the record that lets the next start skip all of it.
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM core.SchemaVersion WHERE ScriptName = 'deployment/auto' AND ScriptChecksum IS NOT NULL"));
    }

    [Fact]
    public async Task ASecondStartDeploysNothing()
    {
        await Deployer().DeployAsync(TestContext.Current.CancellationToken);

        DateTime first = await ScalarAsync<DateTime>(
            "SELECT AppliedUtc FROM core.SchemaVersion WHERE ScriptName = 'deployment/auto'");

        await Deployer().DeployAsync(TestContext.Current.CancellationToken);

        DateTime second = await ScalarAsync<DateTime>(
            "SELECT AppliedUtc FROM core.SchemaVersion WHERE ScriptName = 'deployment/auto'");

        // Unchanged, which can only happen if the fingerprint matched and the
        // deployment was skipped entirely rather than re-run harmlessly.
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task TwoHostsStartingTogetherDeployItOnce()
    {
        // No awaiting between the two: both are in flight before either can
        // finish, which is the situation IIS creates when it starts the API and
        // the portal together.
        Task api = Deployer().DeployAsync(TestContext.Current.CancellationToken);
        Task portal = Deployer().DeployAsync(TestContext.Current.CancellationToken);

        // Neither is allowed to fail. Without the lock, one of them loses a race
        // on CREATE DATABASE or on an object that the other is creating.
        await Task.WhenAll(api, portal);

        Assert.True(await DatabaseExistsAsync());

        // Exactly one fingerprint row, because UQ_SchemaVersion_ScriptName would
        // have been violated by a second insert rather than silently duplicated.
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM core.SchemaVersion WHERE ScriptName = 'deployment/auto'"));

        // A complete schema, not an interleaved half of one.
        Assert.NotEqual(0, await ScalarAsync<int>("SELECT COUNT(*) FROM sys.procedures"));
        Assert.Equal(0, await ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM (VALUES
                ('core.MobileUser'), ('core.Attendance'), ('core.Device'), ('core.OfficeLocation'),
                ('core.ApplicationSetting'), ('core.SchemaVersion'), ('core.Department'), ('core.JobTitle'),
                ('audit.AuditLog'), ('audit.SecurityEvent')) AS t(Name)
            WHERE OBJECT_ID(t.Name, 'U') IS NULL
            """));
    }

    [Fact]
    public async Task SaysSoWhenAnExistingDatabaseWasNotCreatedWithTheDocumentedOptions()
    {
        // A database prepared by hand before the first deployment. This is not
        // hypothetical: the VPS this was first deployed to had exactly this
        // collation and no read-committed snapshot, and objects deploy into such
        // a database perfectly well — the difference surfaces later as
        // comparison and blocking behaviour that no test reproduces.
        await using (SqlConnection master = new(MasterConnectionString()))
        {
            await master.OpenAsync(TestContext.Current.CancellationToken);

            await master.ExecuteAsync(
                $"CREATE DATABASE [{_database}] COLLATE SQL_Latin1_General_CP1_CI_AS");
        }

        CapturingLogger log = new();

        await Deployer(log).DeployAsync(TestContext.Current.CancellationToken);

        // Deployed anyway: the options are reported, not enforced. Correcting a
        // collation means rebuilding the database, which is not a decision a
        // starting host should take.
        Assert.NotEqual(0, await ScalarAsync<int>("SELECT COUNT(*) FROM sys.procedures"));

        string warning = Assert.Single(log.Warnings);

        Assert.Contains("SQL_Latin1_General_CP1_CI_AS", warning, StringComparison.Ordinal);
        Assert.Contains("Latin1_General_100_CI_AS", warning, StringComparison.Ordinal);
        Assert.Contains("READ_COMMITTED_SNAPSHOT is off", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNothingWhenItIsSwitchedOff()
    {
        await Deployer(autoDeploy: false).DeployAsync(TestContext.Current.CancellationToken);

        Assert.False(await DatabaseExistsAsync());
    }

    [Fact]
    public async Task RefusesWhenTheConfiguredNameIsNotTheDatabaseTheHostWillUse()
    {
        // Deploying one database while the application talks to another is the
        // kind of mistake that looks like it worked: the host starts, the schema
        // exists somewhere, and every query fails.
        DatabaseDeployer deployer = new(
            Options.Create(new DatabaseDeploymentOptions
            {
                AutoDeploy = true,
                Name = "SomeOtherDatabase",
            }),
            ConnectionStringFor(_database),
            NullLogger<DatabaseDeployer>.Instance);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => deployer.DeployAsync(TestContext.Current.CancellationToken));

        Assert.Contains("SomeOtherDatabase", error.Message, StringComparison.Ordinal);
        Assert.False(await DatabaseExistsAsync());
    }

    private DatabaseDeployer Deployer(bool autoDeploy = true) =>
        Deployer(NullLogger<DatabaseDeployer>.Instance, autoDeploy);

    private DatabaseDeployer Deployer(ILogger<DatabaseDeployer> logger, bool autoDeploy = true) =>
        new(
            Options.Create(new DatabaseDeploymentOptions
            {
                AutoDeploy = autoDeploy,
                Name = _database,
                LockTimeoutSeconds = 300,
                CommandTimeoutSeconds = 180,
            }),
            ConnectionStringFor(_database),
            logger);

    /// <summary>Keeps the warnings, which is what one of these tests asserts on.</summary>
    private sealed class CapturingLogger : ILogger<DatabaseDeployer>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private async Task<bool> DatabaseExistsAsync()
    {
        await using SqlConnection master = new(MasterConnectionString());

        return await master.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END", new { name = _database }) == 1;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using SqlConnection connection = new(ConnectionStringFor(_database));

        return await connection.ExecuteScalarAsync<T>(sql)
            ?? throw new InvalidOperationException($"'{sql}' returned no value.");
    }

    /// <summary>
    /// The test environment's connection string, pointed at a database of this
    /// test's own, but with the suite's pool tolerances <b>removed</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server, the credentials and the command timeouts a loaded machine
    /// needs are shared with the rest of the suite. The pool settings are not,
    /// deliberately: <see cref="TestEnvironment"/> sets
    /// <c>Pool Blocking Period=NeverBlock</c> so that one slow login cannot fail
    /// a whole class, and that setting <b>hid a defect in the deployer</b>. The
    /// fast path probes a database that does not exist yet; with the blocking
    /// period at its default the failure is cached and replayed, so the open
    /// after <c>CREATE DATABASE</c> failed in a real host while these tests
    /// passed.
    /// </para>
    /// <para>
    /// Restoring the defaults here means these tests see what a deployment on a
    /// server sees. The deployer now sets its own pooling, so it no longer
    /// depends on what a caller supplies.
    /// </para>
    /// </remarks>
    private static string ConnectionStringFor(string database) =>
        new SqlConnectionStringBuilder(TestEnvironment.ConnectionString)
        {
            InitialCatalog = database,
            Pooling = true,
            PoolBlockingPeriod = PoolBlockingPeriod.Auto,
        }.ConnectionString;

    private static string MasterConnectionString() => ConnectionStringFor("master");
}
