using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Attendance.Infrastructure.Deployment;

/// <summary>
/// Creates the application database and every object in it, when a host starts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two hosts starting at once.</b> The API and the portal are separate
/// processes, often started together by IIS or by a deployment, and either may
/// find the database missing. Both race to create it. The race is settled by
/// <c>sp_getapplock</c> taken in <c>master</c>: the application database may
/// not exist yet, and an application lock lives in the database it is taken in,
/// so <c>master</c> is the only place both hosts can queue. The lock is
/// session-scoped and held for the whole deployment — across
/// <c>CREATE DATABASE</c> on one connection and the object scripts on another,
/// which no single transaction could span. Whichever host loses the race waits,
/// then finds the work already done and does nothing.
/// </para>
/// <para>
/// Three things make that safe rather than merely sequential. The scripts are
/// idempotent by design (tables guard their own objects, procedures use
/// <c>CREATE OR ALTER</c>, seeds insert only what is missing), so a second run
/// is a no-op rather than an error. The fingerprint is written <b>last</b>, so a
/// deployment that fails half way leaves the old one recorded and the next start
/// tries again. And closing the connection releases the lock, so a host killed
/// mid-deployment does not block the next one forever.
/// </para>
/// <para>
/// <b>What it will not do to a database that already exists.</b>
/// <c>00_create_database.sql</c> sets <c>READ_COMMITTED_SNAPSHOT ON WITH
/// ROLLBACK IMMEDIATE</c>, which disconnects every open session. That is
/// correct when run by hand on a database nobody is using, and unacceptable at
/// the startup of one host among several serving traffic. It therefore runs
/// only when the database genuinely does not exist. An existing database gets
/// its objects, never its settings.
/// </para>
/// <para>
/// <b>Claude.md §4.</b> This is the one component that does not reach the
/// database through a repository, Dapper and a stored procedure, because it is
/// what creates the stored procedures. No SQL is written in C# even so: the
/// control statements are parameterised scripts under
/// <c>database/deploy/bootstrap/</c>, embedded alongside the schema.
/// </para>
/// </remarks>
public sealed class DatabaseDeployer
{
    /// <summary>
    /// Reserved <c>core.SchemaVersion</c> row recording what was deployed
    /// automatically. Nothing else writes it.
    /// </summary>
    private const string FingerprintScriptName = "deployment/auto";

    private readonly DatabaseDeploymentOptions _options;
    private readonly string _applicationConnectionString;
    private readonly ILogger<DatabaseDeployer> _logger;

    /// <summary>Creates the deployer.</summary>
    /// <param name="options">Deployment configuration.</param>
    /// <param name="applicationConnectionString">
    /// The application's own connection string, used when no separate
    /// deployment identity is configured, and to check that the database being
    /// created is the one that will be used.
    /// </param>
    /// <param name="logger">Startup log.</param>
    public DatabaseDeployer(
        IOptions<DatabaseDeploymentOptions> options,
        string applicationConnectionString,
        ILogger<DatabaseDeployer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationConnectionString);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _applicationConnectionString = applicationConnectionString;
        _logger = logger;
    }

    /// <summary>
    /// Brings the database up to the script set this build carries. Does
    /// nothing if it is already there, or if automatic deployment is off.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The configuration is inconsistent, or another host held the lock for
    /// longer than <see cref="DatabaseDeploymentOptions.LockTimeoutSeconds"/>.
    /// </exception>
    public async Task DeployAsync(CancellationToken cancellationToken)
    {
        if (!_options.AutoDeploy)
        {
            _logger.Disabled();
            return;
        }

        SqlConnectionStringBuilder application = new(_applicationConnectionString);

        if (!string.Equals(application.InitialCatalog, _options.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Database:Name is '{_options.Name}' but SqlServer:ConnectionString uses "
                + $"'{application.InitialCatalog}'. Automatic deployment would create one database and the "
                + "application would use another. Make them the same, or switch Database:AutoDeploy off.");
        }

        SqlScriptSet scripts = SqlScriptSet.Load();

        // The fast path, which is what nearly every start takes: one query, and
        // if the fingerprint matches, nothing else happens at all.
        if (await IsCurrentAsync(scripts, cancellationToken).ConfigureAwait(false))
        {
            _logger.UpToDate(_options.Name);
            return;
        }

        await DeployUnderLockAsync(scripts, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeployUnderLockAsync(SqlScriptSet scripts, CancellationToken cancellationToken)
    {
        string resource = $"ClockInXtra:deploy:{_options.Name}";

        await using SqlConnection master = new(DeploymentConnectionString("master"));
        await master.OpenAsync(cancellationToken).ConfigureAwait(false);

        _logger.Waiting(_options.Name);

        int lockResult = await AcquireLockAsync(master, scripts, resource, cancellationToken)
            .ConfigureAwait(false);

        if (lockResult < 0)
        {
            throw new InvalidOperationException(
                $"Could not take the database deployment lock '{resource}' (sp_getapplock returned "
                + $"{lockResult.ToString(CultureInfo.InvariantCulture)}). Another host may still be deploying. "
                + "Result -1 is a timeout: raise Database:LockTimeoutSeconds if deployment legitimately takes "
                + "longer than it allows.");
        }

        try
        {
            // Another host may have finished while this one queued. Checking
            // again under the lock is what turns "both hosts deploy" into
            // "one deploys, the other looks".
            if (await IsCurrentAsync(scripts, cancellationToken).ConfigureAwait(false))
            {
                _logger.DeployedElsewhere(_options.Name);
                return;
            }

            bool exists = await DatabaseExistsAsync(master, scripts, cancellationToken).ConfigureAwait(false);

            if (!exists)
            {
                _logger.Creating(_options.Name);

                await RunAsync(
                    master, scripts, "deploy/00_create_database.sql", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.ObjectsOnly(_options.Name);
            }

            await using SqlConnection target = new(DeploymentConnectionString(_options.Name));

            await target.OpenAsync(cancellationToken).ConfigureAwait(false);

            await RunAsync(target, scripts, "deploy/01_run_all.sql", cancellationToken).ConfigureAwait(false);

            await WriteFingerprintAsync(target, scripts, cancellationToken).ConfigureAwait(false);

            _logger.Deployed(_options.Name);
        }
        finally
        {
            await ReleaseLockAsync(master, scripts, resource).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the database already carries this script set. False if the
    /// database, the version table or the row is missing, and false if the
    /// database cannot be opened at all — which on a fresh instance is
    /// exactly the case deployment exists to fix.
    /// </summary>
    private async Task<bool> IsCurrentAsync(SqlScriptSet scripts, CancellationToken cancellationToken)
    {
        try
        {
            await using SqlConnection connection = new(DeploymentConnectionString(_options.Name));

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqlCommand command = Command(
                connection, scripts, "deploy/bootstrap/03_fingerprint_read.sql");

            command.Parameters.Add("@ScriptName", SqlDbType.NVarChar, 260).Value = FingerprintScriptName;

            object? deployed = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return deployed is byte[] recorded && recorded.AsSpan().SequenceEqual(scripts.Fingerprint);
        }
        catch (SqlException exception)
        {
            _logger.NotInspectable(exception, _options.Name);
            return false;
        }
    }

    private async Task<bool> DatabaseExistsAsync(
        SqlConnection master, SqlScriptSet scripts, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(master, scripts, "deploy/bootstrap/00_database_exists.sql");

        command.Parameters.Add("@DatabaseName", SqlDbType.NVarChar, 128).Value = _options.Name;

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
    }

    private async Task<int> AcquireLockAsync(
        SqlConnection master, SqlScriptSet scripts, string resource, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(master, scripts, "deploy/bootstrap/01_acquire_lock.sql");

        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;
        command.Parameters.Add("@LockTimeoutMs", SqlDbType.Int).Value =
            Math.Max(1, _options.LockTimeoutSeconds) * 1000;

        // The command must outlast the wait it is asking for.
        command.CommandTimeout = Math.Max(1, _options.LockTimeoutSeconds) + 30;

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private async Task ReleaseLockAsync(SqlConnection master, SqlScriptSet scripts, string resource)
    {
        try
        {
            await using SqlCommand command = Command(master, scripts, "deploy/bootstrap/02_release_lock.sql");

            command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;

            await command.ExecuteScalarAsync().ConfigureAwait(false);
        }
        catch (SqlException exception)
        {
            // Closing the connection releases it regardless, so this is worth
            // knowing about but not worth failing a started host for.
            _logger.LockNotReleased(exception, resource);
        }
    }

    private async Task WriteFingerprintAsync(
        SqlConnection target, SqlScriptSet scripts, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(target, scripts, "deploy/bootstrap/04_fingerprint_write.sql");

        command.Parameters.Add("@ScriptName", SqlDbType.NVarChar, 260).Value = FingerprintScriptName;
        command.Parameters.Add("@Fingerprint", SqlDbType.VarBinary, 32).Value = scripts.Fingerprint;
        command.Parameters.Add("@Notes", SqlDbType.NVarChar, 400).Value =
            $"Deployed automatically at host startup by {Environment.MachineName}.";

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs every batch of a script, in order, stopping at the first failure.</summary>
    private async Task RunAsync(
        SqlConnection connection, SqlScriptSet scripts, string path, CancellationToken cancellationToken)
    {
        Dictionary<string, string> variables = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DatabaseName"] = _options.Name,
        };

        foreach (SqlBatch batch in scripts.Expand(path, variables))
        {
            await using SqlCommand command = connection.CreateCommand();

            command.CommandText = batch.Text;
            command.CommandTimeout = Math.Max(1, _options.CommandTimeoutSeconds);

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException exception)
            {
                throw new InvalidOperationException(
                    $"Database deployment failed in 'database/{batch.Origin}': {exception.Message}", exception);
            }
        }
    }

    private SqlCommand Command(SqlConnection connection, SqlScriptSet scripts, string path)
    {
        SqlCommand command = connection.CreateCommand();

        command.CommandText = scripts.Text(path);
        command.CommandTimeout = Math.Max(1, _options.CommandTimeoutSeconds);

        return command;
    }

    /// <summary>
    /// The deployment identity, pointed at one database, with pooling off.
    /// </summary>
    /// <param name="database">
    /// <c>master</c> for the lock and <c>CREATE DATABASE</c> — the database
    /// being created cannot be the one connected to — or the application
    /// database for the object scripts.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Pooling is off, and that is not a micro-optimisation in reverse.</b>
    /// The fast path probes the application database before it is known to
    /// exist, and that probe fails. With pooling on, SqlClient's <i>blocking
    /// period</i> then caches the failure against that pool and replays it for
    /// the next five seconds — so the open that follows <c>CREATE
    /// DATABASE</c> fails with "Cannot open database ... the login failed",
    /// naming a database that now exists. Deployment opens a handful of
    /// connections once at startup, so a pool buys nothing here and costs
    /// exactly this.
    /// </para>
    /// <para>
    /// Setting the blocking period alone would be enough today, but it would
    /// leave the deployer's correctness resting on a connection-string option
    /// the caller supplies — which is how this was missed in the first
    /// place: the test fixture set <c>NeverBlock</c>, so the tests passed while
    /// a real host could not start. Owning the setting here is what makes it
    /// true regardless of how the application is configured (TD-16).
    /// </para>
    /// </remarks>
    private string DeploymentConnectionString(string database)
    {
        SqlConnectionStringBuilder builder = new(_options.ConnectionString.Length > 0
            ? _options.ConnectionString
            : _applicationConnectionString)
        {
            InitialCatalog = database,
            Pooling = false,
            PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock,
        };

        return builder.ConnectionString;
    }
}
