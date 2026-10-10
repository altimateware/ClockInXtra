using Attendance.Domain.Enums;
using Attendance.Domain.ValueObjects;

namespace Attendance.Application.Abstractions;

/// <summary>
/// Device administration: the human half of DEC-04.
/// </summary>
/// <remarks>
/// Registration proves an employee knew a password and a code, and that the key
/// lives in device hardware. It does not prove the person holding the handset is
/// the employee. Approval is where a person decides that, which is why it is a
/// separate act performed by a different principal against a different schema.
/// </remarks>
public interface IDeviceAdministrationRepository
{
    /// <summary>Lists registrations awaiting approval, with decision context.</summary>
    Task<IReadOnlyList<PendingDeviceApproval>> GetPendingApprovalsAsync(CancellationToken cancellationToken);

    /// <summary>Lists registered devices, whatever their status.</summary>
    /// <param name="status">
    /// A single <see cref="DeviceStatus"/> to show, or <see langword="null"/> for
    /// all of them.
    /// </param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <remarks>
    /// Separate from <see cref="GetPendingApprovalsAsync"/>, which answers only
    /// "what is waiting for a decision". Without this, an <b>active</b> device
    /// could not be revoked at all — the action and the procedure existed, but
    /// no screen could reach one — so a lost handset could be cut off only by
    /// enrolling its replacement first.
    /// </remarks>
    Task<IReadOnlyList<RegisteredDevice>> GetRegisteredAsync(
        DeviceStatus? status,
        CancellationToken cancellationToken);

    /// <summary>
    /// Approves a registration, revoking the employee's previous device in the
    /// same transaction.
    /// </summary>
    Task<DeviceApprovalOutcome> ApproveAsync(
        int deviceId,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>Revokes a device.</summary>
    Task<AttendanceResultCode> RevokeAsync(
        int deviceId,
        byte[] rowVersion,
        string reason,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// A registration awaiting approval.
/// </summary>
/// <param name="DeviceId">Internal identifier.</param>
/// <param name="DevicePublicId">External identifier.</param>
/// <param name="RowVersion">Concurrency token; approval refuses a stale one.</param>
/// <param name="RegisteredUtc">When the employee registered it.</param>
/// <param name="Platform">Android or iOS.</param>
/// <param name="AttestationLevel">
/// How strongly the key was attested. <b>Shown because it is evidence.</b> A
/// level of None is not proof of fraud, but it is weaker evidence and the
/// administrator should see it before approving.
/// </param>
/// <param name="DeviceModel">Untrusted client metadata.</param>
/// <param name="OsVersion">Untrusted client metadata.</param>
/// <param name="AppVersion">Untrusted client metadata.</param>
/// <param name="UserId">The employee's sign-in identifier.</param>
/// <param name="EmployeeName">Their name, for recognition.</param>
/// <param name="Department">Their department, where recorded.</param>
/// <param name="CurrentActiveDeviceModel">
/// The device this approval will revoke, if any. Shown up front: an employee
/// whose working handset is about to stop clocking in deserves that to be a
/// conscious decision.
/// </param>
/// <param name="CurrentActiveDeviceLastSeenUtc">When that device was last used.</param>
/// <param name="RecentFailedAttempts">
/// Failed sign-ins against this account. A registration preceded by failures is
/// the shape of a phished-credential takeover (threat TH-08), and surfacing it is
/// what makes the human in the loop worth having.
/// </param>
public readonly record struct PendingDeviceApproval(
    int DeviceId,
    Guid DevicePublicId,
    byte[] RowVersion,
    DateTimeOffset RegisteredUtc,
    DevicePlatform Platform,
    AttestationLevel AttestationLevel,
    string? DeviceModel,
    string? OsVersion,
    string? AppVersion,
    string UserId,
    string EmployeeName,
    string? Department,
    string? CurrentActiveDeviceModel,
    DateTimeOffset? CurrentActiveDeviceLastSeenUtc,
    int RecentFailedAttempts);

/// <summary>
/// A registered device as the portal's device list shows it.
/// </summary>
/// <param name="DeviceId">Internal identifier.</param>
/// <param name="DevicePublicId">External identifier; the signature's key id.</param>
/// <param name="RowVersion">Concurrency token; revocation refuses a stale one.</param>
/// <param name="Status">Pending approval, active or revoked.</param>
/// <param name="Platform">Android or iOS.</param>
/// <param name="AttestationLevel">How strongly the key was attested.</param>
/// <param name="DeviceModel">Untrusted client metadata.</param>
/// <param name="OsVersion">Untrusted client metadata.</param>
/// <param name="AppVersion">Untrusted client metadata.</param>
/// <param name="RegisteredUtc">When the employee registered it.</param>
/// <param name="ApprovedUtc">When an administrator approved it, if ever.</param>
/// <param name="LastSeenUtc">
/// The last request this device signed. It is how an administrator tells a
/// handset in daily use from one abandoned months ago, which is the difference
/// between revoking carefully and revoking freely.
/// </param>
/// <param name="RevokedUtc">When it was revoked, if it was.</param>
/// <param name="RevokedReason">
/// The reason given, which is also what the employee is shown in the app.
/// </param>
/// <param name="UserId">The employee's sign-in identifier.</param>
/// <param name="EmployeeName">Their name, for recognition.</param>
/// <param name="Department">Their department, where recorded.</param>
public readonly record struct RegisteredDevice(
    int DeviceId,
    Guid DevicePublicId,
    byte[] RowVersion,
    DeviceStatus Status,
    DevicePlatform Platform,
    AttestationLevel AttestationLevel,
    string? DeviceModel,
    string? OsVersion,
    string? AppVersion,
    DateTimeOffset RegisteredUtc,
    DateTimeOffset? ApprovedUtc,
    DateTimeOffset? LastSeenUtc,
    DateTimeOffset? RevokedUtc,
    string? RevokedReason,
    string UserId,
    string EmployeeName,
    string? Department);

/// <summary>The outcome of an approval.</summary>
/// <param name="ResultCode">The procedure's outcome.</param>
/// <param name="DevicePublicId">The device approved.</param>
/// <param name="RevokedPreviousDevicePublicId">
/// The device this approval revoked, so the portal can say so plainly.
/// </param>
public readonly record struct DeviceApprovalOutcome(
    AttendanceResultCode ResultCode,
    Guid? DevicePublicId,
    Guid? RevokedPreviousDevicePublicId);

/// <summary>
/// Reading and recording configuration decisions.
/// </summary>
public interface ISettingAdministrationRepository
{
    /// <summary>Lists settings, showing which decisions are outstanding.</summary>
    Task<IReadOnlyList<ApplicationSettingRow>> GetAllAsync(
        string? category,
        CancellationToken cancellationToken);

    /// <summary>
    /// The time zones the database recognises — the only ones
    /// <c>Attendance.BusinessTimeZoneId</c> may be set to.
    /// </summary>
    Task<IReadOnlyList<TimeZoneOption>> GetTimeZonesAsync(CancellationToken cancellationToken);

    /// <summary>Records a value, and optionally confirms it as the business decision.</summary>
    Task<AttendanceResultCode> SetAsync(
        string settingKey,
        string? value,
        bool confirm,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// A configurable setting.
/// </summary>
/// <param name="SettingKey">The key.</param>
/// <param name="SettingValue">
/// The value, or <see langword="null"/> meaning the business has not decided.
/// <b>Null is not a blank field to be tidied away</b> — attendance refuses to
/// operate while a mandatory one is unset (§15, §68).
/// </param>
/// <param name="DataType">How to interpret and validate the value.</param>
/// <param name="Category">Grouping for display.</param>
/// <param name="Description">What the setting means, and which OPEN item it answers.</param>
/// <param name="AllowedValues">Permitted values for an enum, comma separated.</param>
/// <param name="MinValue">Inclusive lower bound for a number, enforced by the database.</param>
/// <param name="MaxValue">Inclusive upper bound for a number, enforced by the database.</param>
/// <param name="Unit">Shown beside a number: minutes, days, metres.</param>
/// <param name="BlankMeaning">
/// When set, a blank value is a valid decision and this says what it means
/// ("Keep indefinitely"); when <see langword="null"/>, blank means undecided.
/// </param>
/// <param name="RequiresBusinessConfirmation">Whether it still awaits confirmation.</param>
/// <param name="IsUnset">Whether no decision has been recorded.</param>
/// <param name="IsMandatoryForAttendance">Whether leaving it unset blocks attendance.</param>
/// <param name="ConfirmedByUserName">Who confirmed it.</param>
/// <param name="UpdatedUtc">When it last changed.</param>
/// <param name="RowVersion">Concurrency token.</param>
public readonly record struct ApplicationSettingRow(
    string SettingKey,
    string? SettingValue,
    string DataType,
    string Category,
    string Description,
    string? AllowedValues,
    decimal? MinValue,
    decimal? MaxValue,
    string? Unit,
    string? BlankMeaning,
    bool RequiresBusinessConfirmation,
    bool IsUnset,
    bool IsMandatoryForAttendance,
    string? ConfirmedByUserName,
    DateTimeOffset? UpdatedUtc,
    byte[] RowVersion);

/// <summary>A time zone the database accepts.</summary>
/// <param name="TimeZoneId">The Windows identifier stored in the setting.</param>
/// <param name="CurrentUtcOffset">Today's offset, e.g. <c>+01:00</c>.</param>
/// <param name="IsCurrentlyDaylightSaving">Whether that offset includes daylight saving now.</param>
public readonly record struct TimeZoneOption(
    string TimeZoneId,
    string CurrentUtcOffset,
    bool IsCurrentlyDaylightSaving);
