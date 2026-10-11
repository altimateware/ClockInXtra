namespace Attendance.Application.Abstractions;

/// <summary>
/// The counts behind the portal's landing page.
/// </summary>
public interface IDashboardRepository
{
    /// <summary>Reads the dashboard summary.</summary>
    Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What the dashboard shows.
/// </summary>
/// <param name="DevicesAwaitingApproval">Registrations nobody has decided on.</param>
/// <param name="ActiveDevices">Devices currently able to record attendance.</param>
/// <param name="SettingsAwaitingConfirmation">
/// Business settings with no confirmed value. Attendance cannot operate until
/// these are decided, and nobody but an administrator can decide them
/// (Claude.md section 15 and section 68).
/// </param>
/// <param name="CorrectionsAwaitingApproval">Attendance corrections requested, not yet approved.</param>
/// <param name="ActiveEmployees">Employees whose account is active.</param>
/// <param name="EmployeesNotReady">
/// Active employees who would be refused at a door right now, for want of a
/// password, an authenticator or an approved device. The number matters because
/// nobody discovers it until somebody is standing at the door.
/// </param>
/// <param name="ActiveOfficeLocations">
/// Approved locations. Zero is worth saying out loud: with none, every
/// clock-in fails the location check, however well everything else is set up.
/// </param>
/// <param name="ClockedInNow">Open attendance records for today.</param>
/// <param name="CompletedToday">Attendance records closed today.</param>
/// <param name="LateToday">Today's clock-ins the rules counted as late.</param>
/// <param name="MissingClockOuts">
/// Records still open from an earlier day — a clock-in with no clock-out. It
/// will not resolve itself, and it needs a correction.
/// </param>
/// <param name="LocationRefusals">Location checks refused in the last 24 hours.</param>
/// <param name="DeviceRefusals">Device and signature checks refused in the last 24 hours.</param>
/// <param name="CriticalEvents">Critical security events in the last 24 hours.</param>
public readonly record struct DashboardSummary(
    int DevicesAwaitingApproval,
    int ActiveDevices,
    int SettingsAwaitingConfirmation,
    int CorrectionsAwaitingApproval,
    int ActiveEmployees,
    int EmployeesNotReady,
    int ActiveOfficeLocations,
    int ClockedInNow,
    int CompletedToday,
    int LateToday,
    int MissingClockOuts,
    int LocationRefusals,
    int DeviceRefusals,
    int CriticalEvents);
