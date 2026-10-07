using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;

namespace Attendance.Application.Features.Attendance;

/// <summary>
/// The user status use case (Claude.md §11): whether the app shows Clock-In or
/// Clock-Out.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server is the source of truth.</b> The application must not decide from
/// its own stored state whether an employee is clocked in — a reinstalled app, a
/// restored backup, or a second device would each produce a confident wrong
/// answer. It asks, every time.
/// </para>
/// <para>
/// The server's own time is returned alongside the state so the app can render a
/// consistent clock and notice that its own device time is wrong, without ever
/// being trusted to supply a timestamp (§31).
/// </para>
/// <para>
/// Touching the device's last-seen marker is done after the status is read and is
/// deliberately not allowed to fail the request: it supports the administrative
/// view of dormant devices, and losing one update matters far less than refusing
/// to tell an employee whether they are clocked in.
/// </para>
/// </remarks>
public sealed class UserStatusHandler
{
    private readonly IAttendanceRepository _attendance;
    private readonly IDeviceRepository _devices;

    /// <summary>Creates the handler.</summary>
    public UserStatusHandler(IAttendanceRepository attendance, IDeviceRepository devices)
    {
        ArgumentNullException.ThrowIfNull(attendance);
        ArgumentNullException.ThrowIfNull(devices);

        _attendance = attendance;
        _devices = devices;
    }

    /// <summary>Returns the employee's attendance state for the current day.</summary>
    public async Task<UserStatusResponse> HandleAsync(
        UserStatusCommand command,
        CancellationToken cancellationToken)
    {
        AttendanceStatus status = await _attendance
            .GetCurrentStatusAsync(command.MobileUserId, cancellationToken)
            .ConfigureAwait(false);

        await _devices
            .TouchLastSeenAsync(command.DeviceId, command.AppVersion, command.OsVersion, cancellationToken)
            .ConfigureAwait(false);

        return new UserStatusResponse(
            status.ResultCode,
            status.State,
            status.AttendanceDate,
            status.ServerTimeUtc,
            status.ClockInUtc,
            status.ClockOutUtc,
            status.DurationMinutes,
            status.ClockInCloseTime,
            status.ClockOutOpenTime);
    }
}

/// <summary>
/// A status request, after the API has verified the signature and device.
/// </summary>
/// <param name="MobileUserId">The employee the device is bound to.</param>
/// <param name="DeviceId">The registered device that signed the request.</param>
/// <param name="AppVersion">Untrusted metadata, refreshed opportunistically.</param>
/// <param name="OsVersion">Untrusted metadata, refreshed opportunistically.</param>
/// <param name="CorrelationId">Ties logs and audit entries together.</param>
public readonly record struct UserStatusCommand(
    int MobileUserId,
    int DeviceId,
    string? AppVersion,
    string? OsVersion,
    Guid CorrelationId);

/// <summary>The employee's attendance position.</summary>
/// <param name="ResultCode">The outcome to map to an API response.</param>
/// <param name="State">Whether the app shows Clock-In or Clock-Out.</param>
/// <param name="AttendanceDate">The business-local attendance date.</param>
/// <param name="ServerTimeUtc">Authoritative server time.</param>
/// <param name="ClockInUtc">When the open record was opened, if any.</param>
/// <param name="ClockOutUtc">When it was closed, if it was.</param>
/// <param name="DurationMinutes">Duration of a completed record.</param>
/// <param name="ClockInCloseTime">
/// When clock-in closes, or <c>null</c> where the business has not decided it
/// (OPEN-6). The app shows the limit only when one actually exists.
/// </param>
/// <param name="ClockOutOpenTime">When clock-out opens, or <c>null</c> (OPEN-7).</param>
public readonly record struct UserStatusResponse(
    AttendanceResultCode ResultCode,
    AttendanceState State,
    DateOnly? AttendanceDate,
    DateTimeOffset ServerTimeUtc,
    DateTimeOffset? ClockInUtc,
    DateTimeOffset? ClockOutUtc,
    int? DurationMinutes,
    TimeOnly? ClockInCloseTime,
    TimeOnly? ClockOutOpenTime);
