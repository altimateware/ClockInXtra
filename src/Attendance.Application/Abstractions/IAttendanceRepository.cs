using Attendance.Domain.Enums;

namespace Attendance.Application.Abstractions;

/// <summary>
/// Attendance operations, backed by stored procedures.
/// </summary>
/// <remarks>
/// <para>
/// Every member maps to exactly one stored procedure (Claude.md §4). The
/// implementation contains procedure names and typed parameters — no SQL text,
/// no string building, no EF Core.
/// </para>
/// <para>
/// <b>The business decisions live in the procedures, not here.</b> The attendance
/// day, the window rules, the duplicate check and the official timestamps are all
/// determined inside the database transaction, because those guarantees have to
/// hold across several application servers (§28, §45). This interface carries the
/// request in and the outcome back; it does not re-decide anything.
/// </para>
/// </remarks>
public interface IAttendanceRepository
{
    /// <summary>
    /// Returns whether the employee is currently clocked in, for the current
    /// attendance day as the server reckons it (§11).
    /// </summary>
    Task<AttendanceStatus> GetCurrentStatusAsync(int mobileUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a clock-in.
    /// </summary>
    /// <remarks>
    /// The caller must already have verified the signature, the device binding,
    /// the password, the authenticator code and the location, and claimed the
    /// idempotency key. Those checks are expensive or belong in the application;
    /// the procedure owns only what must be atomic.
    /// </remarks>
    Task<ClockInOutcome> ClockInAsync(ClockInRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Records a clock-out and returns the resulting duration.
    /// </summary>
    Task<ClockOutOutcome> ClockOutAsync(ClockOutRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The server's view of an employee's attendance for the current day.
/// </summary>
/// <param name="ResultCode">The procedure's outcome.</param>
/// <param name="State">Whether the employee is clocked in.</param>
/// <param name="AttendanceDate">The business-local attendance date.</param>
/// <param name="ServerTimeUtc">
/// The server's time, returned so the app can show a consistent clock and
/// detect that its own device time is wrong.
/// </param>
/// <param name="ClockInUtc">When the open record was opened, if any.</param>
/// <param name="ClockOutUtc">When it was closed, if it was.</param>
/// <param name="DurationMinutes">Duration of a completed record.</param>
/// <param name="ClockInCloseTime">
/// The configured local time after which clock-in closes, or <c>null</c> when
/// the business has not decided it (OPEN-6).
/// </param>
/// <param name="ClockOutOpenTime">
/// The configured local time from which clock-out is accepted, or <c>null</c>
/// when undecided (OPEN-7).
/// </param>
public readonly record struct AttendanceStatus(
    AttendanceResultCode ResultCode,
    AttendanceState State,
    DateOnly? AttendanceDate,
    DateTimeOffset ServerTimeUtc,
    DateTimeOffset? ClockInUtc,
    DateTimeOffset? ClockOutUtc,
    int? DurationMinutes,
    TimeOnly? ClockInCloseTime,
    TimeOnly? ClockOutOpenTime);

/// <summary>
/// Whether an employee is clocked in for the current attendance day.
/// </summary>
public enum AttendanceState
{
    /// <summary>No record for today: the app shows Clock-In.</summary>
    NotClockedIn = 0,

    /// <summary>An open record exists: the app shows Clock-Out.</summary>
    ClockedIn = 1,

    /// <summary>Today's record is already closed.</summary>
    Completed = 2,
}

/// <summary>
/// Evidence accompanying an attendance transaction.
/// </summary>
/// <param name="OfficeLocationId">The office the position matched.</param>
/// <param name="DistanceMeters">Distance to that office.</param>
/// <param name="ReportedAccuracyMeters">The accuracy the device claimed.</param>
/// <param name="Platform">The reporting platform.</param>
/// <param name="WasMockedLocation">Whether the platform flagged the position as artificial.</param>
/// <param name="CoordinatesProtected">
/// Encrypted coordinates, supplied only if OPEN-35 decides they must be
/// retained. Raw coordinates are never stored (assumption ASM-06).
/// </param>
public readonly record struct LocationEvidence(
    int OfficeLocationId,
    decimal DistanceMeters,
    decimal ReportedAccuracyMeters,
    byte Platform,
    bool WasMockedLocation,
    byte[]? CoordinatesProtected);

/// <summary>A request to record a clock-in.</summary>
/// <param name="MobileUserId">The authenticated employee.</param>
/// <param name="DeviceId">The registered device that signed the request.</param>
/// <param name="Evidence">Where the employee was, as measured.</param>
/// <param name="CorrelationId">Correlates the request across logs and audit.</param>
public readonly record struct ClockInRequest(
    int MobileUserId,
    int DeviceId,
    LocationEvidence Evidence,
    Guid CorrelationId);

/// <summary>A request to record a clock-out.</summary>
/// <param name="MobileUserId">The authenticated employee.</param>
/// <param name="DeviceId">The registered device that signed the request.</param>
/// <param name="Evidence">Where the employee was, as measured.</param>
/// <param name="CorrelationId">Correlates the request across logs and audit.</param>
public readonly record struct ClockOutRequest(
    int MobileUserId,
    int DeviceId,
    LocationEvidence Evidence,
    Guid CorrelationId);

/// <summary>The outcome of a clock-in.</summary>
/// <param name="ResultCode">
/// The procedure's outcome. Anything other than <see cref="AttendanceResultCode.Success"/>
/// means no record was created — there is never a partial one (§30).
/// </param>
/// <param name="AttendancePublicId">The new record's external identifier.</param>
/// <param name="AttendanceDate">The business-local attendance date.</param>
/// <param name="ClockInUtc">The server-assigned timestamp.</param>
/// <param name="IsLateClockIn">
/// Whether the clock-in was after the configured closing time. <c>null</c> means
/// the rule was not evaluated because the business has not configured it.
/// </param>
public readonly record struct ClockInOutcome(
    AttendanceResultCode ResultCode,
    Guid? AttendancePublicId,
    DateOnly? AttendanceDate,
    DateTimeOffset? ClockInUtc,
    bool? IsLateClockIn);

/// <summary>The outcome of a clock-out.</summary>
/// <param name="ResultCode">The procedure's outcome.</param>
/// <param name="AttendancePublicId">The record that was closed.</param>
/// <param name="AttendanceDate">The business-local attendance date.</param>
/// <param name="ClockInUtc">When the record was opened.</param>
/// <param name="ClockOutUtc">The server-assigned closing timestamp.</param>
/// <param name="DurationMinutes">
/// Whole minutes between the stored clock-in and the clock-out, computed by the
/// database from its own timestamps — never from anything the client sent.
/// </param>
/// <param name="IsEarlyClockOut">
/// Whether the clock-out preceded the configured opening time. <c>null</c> means
/// the rule was not evaluated because the business has not configured it.
/// </param>
public readonly record struct ClockOutOutcome(
    AttendanceResultCode ResultCode,
    Guid? AttendancePublicId,
    DateOnly? AttendanceDate,
    DateTimeOffset? ClockInUtc,
    DateTimeOffset? ClockOutUtc,
    int? DurationMinutes,
    bool? IsEarlyClockOut);
