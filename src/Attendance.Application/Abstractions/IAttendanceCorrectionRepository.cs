using Attendance.Domain.Enums;

namespace Attendance.Application.Abstractions;

/// <summary>
/// Attendance corrections: request, approve or reject, and list (§15, §20;
/// enabled by DEC-08).
/// </summary>
/// <remarks>
/// Every rule lives in the stored procedures — one correction pending per
/// record, times within the record's own attendance day and not in the future,
/// the approver never the requester, the original times kept forever. This
/// port only carries requests to them.
/// </remarks>
public interface IAttendanceCorrectionRepository
{
    /// <summary>One attendance record, for the request form.</summary>
    Task<AttendanceForCorrection?> GetForCorrectionAsync(Guid attendancePublicId, CancellationToken cancellationToken);

    /// <summary>
    /// Requests a correction. Times are business-local; the database converts
    /// them with the configured time zone. A null time leaves that time as it is.
    /// </summary>
    Task<CorrectionRequestResult> RequestAsync(
        Guid attendancePublicId,
        DateTime? correctedClockInLocal,
        DateTime? correctedClockOutLocal,
        string reason,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>Approves (and applies) or rejects a pending correction.</summary>
    Task<AttendanceResultCode> DecideAsync(
        long attendanceCorrectionId,
        bool approve,
        string? decisionNote,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>Corrections, awaiting approval first.</summary>
    Task<IReadOnlyList<AttendanceCorrectionSummary>> SearchAsync(
        AttendanceCorrectionStatus? status,
        CancellationToken cancellationToken);
}

/// <summary>Where a correction is in its life.</summary>
public enum AttendanceCorrectionStatus : byte
{
    /// <summary>Awaiting a second administrator's decision.</summary>
    Requested = 1,

    /// <summary>Approved (and applied at the same moment).</summary>
    Approved = 2,

    /// <summary>Refused; the record was not changed.</summary>
    Rejected = 3,

    /// <summary>Applied without approval (only when approval is not required).</summary>
    Applied = 4,
}

/// <summary>An attendance record as the correction form needs it.</summary>
/// <param name="AttendancePublicId">The record.</param>
/// <param name="AttendanceDate">Its business-local attendance date.</param>
/// <param name="UserId">The employee's sign-in identifier.</param>
/// <param name="EmployeeName">Their name.</param>
/// <param name="Status">Open, closed or corrected.</param>
/// <param name="ClockInLocal">Clock-in, business-local.</param>
/// <param name="ClockOutLocal">Clock-out, business-local, if any.</param>
/// <param name="HasPendingCorrection">Whether a correction already awaits approval.</param>
public sealed record AttendanceForCorrection(
    Guid AttendancePublicId,
    DateOnly AttendanceDate,
    string UserId,
    string EmployeeName,
    AttendanceRecordStatus Status,
    DateTime ClockInLocal,
    DateTime? ClockOutLocal,
    bool HasPendingCorrection);

/// <summary>The outcome of a correction request.</summary>
/// <param name="ResultCode">Success or the reason for refusal.</param>
/// <param name="Applied">Whether it took effect immediately (approval not required).</param>
public readonly record struct CorrectionRequestResult(AttendanceResultCode ResultCode, bool Applied);

/// <summary>One correction in the list.</summary>
/// <param name="AttendanceCorrectionId">The correction.</param>
/// <param name="AttendancePublicId">The record it corrects.</param>
/// <param name="AttendanceDate">That record's attendance date.</param>
/// <param name="UserId">The employee.</param>
/// <param name="EmployeeName">Their name.</param>
/// <param name="OriginalClockInLocal">Clock-in before the correction.</param>
/// <param name="OriginalClockOutLocal">Clock-out before the correction.</param>
/// <param name="CorrectedClockInLocal">The corrected clock-in, if changed.</param>
/// <param name="CorrectedClockOutLocal">The corrected clock-out, if changed.</param>
/// <param name="Reason">Why it was requested.</param>
/// <param name="Status">Where it is.</param>
/// <param name="RequestedByAdministratorId">Who asked — never allowed to approve it.</param>
/// <param name="RequestedByName">Their name.</param>
/// <param name="RequestedUtc">When.</param>
/// <param name="DecidedByName">Who decided, if decided.</param>
/// <param name="DecidedUtc">When.</param>
/// <param name="DecisionNote">The approver's note.</param>
/// <param name="RowVersion">For the decision, so two approvers cannot both decide it.</param>
public sealed record AttendanceCorrectionSummary(
    long AttendanceCorrectionId,
    Guid AttendancePublicId,
    DateOnly AttendanceDate,
    string UserId,
    string EmployeeName,
    DateTime? OriginalClockInLocal,
    DateTime? OriginalClockOutLocal,
    DateTime? CorrectedClockInLocal,
    DateTime? CorrectedClockOutLocal,
    string Reason,
    AttendanceCorrectionStatus Status,
    int RequestedByAdministratorId,
    string RequestedByName,
    DateTimeOffset RequestedUtc,
    string? DecidedByName,
    DateTimeOffset? DecidedUtc,
    string? DecisionNote,
    byte[] RowVersion);
