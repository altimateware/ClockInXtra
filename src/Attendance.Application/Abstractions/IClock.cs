namespace Attendance.Application.Abstractions;

/// <summary>
/// Supplies the current time to application code.
/// </summary>
/// <remarks>
/// <para>
/// <b>This clock is not authoritative for attendance.</b> Claude.md §31 requires
/// the official clock-in and clock-out timestamps to be decided by the server,
/// and they are taken with <c>SYSUTCDATETIME()</c> <em>inside</em> the stored
/// procedure's transaction. That is deliberate: several application servers can
/// disagree with each other by seconds, and an attendance record that depends on
/// which node answered would be indefensible in a dispute.
/// </para>
/// <para>
/// What this clock is for is everything that is not the attendance record:
/// evaluating whether a request signature's <c>created</c> value falls inside
/// the permitted skew window, deciding whether a registration challenge has
/// expired, stamping log entries, and letting tests control time instead of
/// waiting for it.
/// </para>
/// <para>
/// Values are always UTC. The business-local date is derived in SQL from the
/// configured timezone (decision DB-05), never from the application server's
/// regional settings.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>The current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}
