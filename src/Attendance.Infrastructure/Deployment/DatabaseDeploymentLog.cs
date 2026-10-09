using Microsoft.Extensions.Logging;

namespace Attendance.Infrastructure.Deployment;

/// <summary>Source-generated log messages for automatic database deployment.</summary>
/// <remarks>
/// These are the only record of what a host did to the database before it
/// started serving, so each one names the database and says plainly whether
/// anything changed.
/// </remarks>
internal static partial class DatabaseDeploymentLog
{
    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Information,
        Message = "Automatic database deployment is off. The database must already contain its objects; "
            + "deploy with database/deploy/01_run_all.sql. Set Database:AutoDeploy to change this.")]
    public static partial void Disabled(this ILogger logger);

    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Information,
        Message = "Database {Database} is up to date; no deployment needed.")]
    public static partial void UpToDate(this ILogger logger, string database);

    [LoggerMessage(
        EventId = 5003,
        Level = LogLevel.Information,
        Message = "Database {Database} needs deployment; waiting for the deployment lock.")]
    public static partial void Waiting(this ILogger logger, string database);

    [LoggerMessage(
        EventId = 5004,
        Level = LogLevel.Information,
        Message = "Database {Database} was deployed by another host while this one waited.")]
    public static partial void DeployedElsewhere(this ILogger logger, string database);

    [LoggerMessage(
        EventId = 5005,
        Level = LogLevel.Information,
        Message = "Creating database {Database}.")]
    public static partial void Creating(this ILogger logger, string database);

    [LoggerMessage(
        EventId = 5006,
        Level = LogLevel.Information,
        Message = "Database {Database} exists; deploying objects only. Its database-level settings are left "
            + "alone, because applying them would disconnect every open session.")]
    public static partial void ObjectsOnly(this ILogger logger, string database);

    [LoggerMessage(
        EventId = 5007,
        Level = LogLevel.Information,
        Message = "Database {Database} deployed. Business settings that are still unset will refuse the "
            + "operations that need them, by design.")]
    public static partial void Deployed(this ILogger logger, string database);

    [LoggerMessage(
        EventId = 5008,
        Level = LogLevel.Debug,
        Message = "Database {Database} could not be inspected, so it is treated as needing deployment.")]
    public static partial void NotInspectable(this ILogger logger, Exception exception, string database);

    [LoggerMessage(
        EventId = 5010,
        Level = LogLevel.Warning,
        Message = "Database {Database} already existed and its options are not the documented ones: "
            + "{Differences}. Objects deploy regardless, but comparison and blocking behaviour will differ "
            + "from every environment this is tested in. These are set only when the database is created "
            + "(00_create_database.sql), because applying them disconnects every open session, so correcting "
            + "them means rebuilding the database \u2014 trivial while it is empty, disruptive later.")]
    public static partial void OptionsDiffer(this ILogger logger, string database, string differences);

    [LoggerMessage(
        EventId = 5009,
        Level = LogLevel.Warning,
        Message = "The deployment lock {Resource} could not be released explicitly. Closing the connection "
            + "releases it, so the next host is not blocked.")]
    public static partial void LockNotReleased(this ILogger logger, Exception exception, string resource);
}
