namespace Attendance.Infrastructure.Deployment;

/// <summary>
/// Whether, and with what rights, a host creates the database it needs when it
/// starts.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is off unless it is switched on, outside Development.</b> Creating a
/// database and its objects needs rights the application accounts must never
/// hold: <c>CREATE DATABASE</c>, DDL across every schema, and <c>ENABLE
/// LEDGER</c> for the append-only audit tables. Claude.md §48 requires the
/// application logins to hold <c>EXECUTE</c> on one procedure schema and
/// nothing else. Defaulting this on in production would push an operator into
/// granting the API's own login enough privilege to drop the database it is
/// protecting, which is a worse outcome than running a deployment script by
/// hand.
/// </para>
/// <para>
/// Where it is wanted on a server, give it <see cref="ConnectionString"/> — a
/// separate deployment identity, used for the few seconds of startup and never
/// for a request. The application's own least-privilege connection string is
/// untouched.
/// </para>
/// </remarks>
public sealed class DatabaseDeploymentOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// Whether a host deploys the database when it starts. Defaults to true in
    /// Development and false everywhere else; see the remarks on this class for
    /// why the default is not the same in both.
    /// </summary>
    public bool AutoDeploy { get; set; }

    /// <summary>
    /// The database to create. Must match the <c>Initial Catalog</c> of
    /// <c>SqlServer:ConnectionString</c>, and is checked against it at startup
    /// rather than trusted — a mismatch would create one database and use
    /// another.
    /// </summary>
    public string Name { get; set; } = "ClockInXtra";

    /// <summary>
    /// The identity deployment runs as. Empty means use
    /// <c>SqlServer:ConnectionString</c>, which is what a developer wants
    /// (their own Windows account owns the local instance) and what a server
    /// should not do.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// How long a host waits for another host that is already deploying, before
    /// giving up and failing to start.
    /// </summary>
    /// <remarks>
    /// Must outlast a full deployment, because the host that waits is waiting
    /// for exactly that. Five minutes against a deployment measured in seconds
    /// leaves room for a starved machine without hanging a service indefinitely
    /// on a lock nobody will release.
    /// </remarks>
    public int LockTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Seconds allowed for each batch. Creating the ledger tables and the
    /// filtered indexes is the slow part on a cold instance.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 180;
}
