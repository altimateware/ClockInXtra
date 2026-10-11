namespace Attendance.Application.Abstractions;

/// <summary>
/// Attendance reporting and the audit trail (§20, §32).
/// </summary>
public interface IReportingRepository
{
    /// <summary>Daily attendance over a date range, one page at a time.</summary>
    /// <remarks>
    /// Paged because this grows every working day. It previously returned up
    /// to a thousand rows with no total and no way to ask for the rest, so a
    /// wide date range silently stopped at a thousand.
    /// </remarks>
    Task<PagedResult<AttendanceReportRow>> GetDailyAttendanceAsync(
        AttendanceReportFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Refused location, device, identity and request-integrity checks (§20),
    /// read from the security event ledger.
    /// </summary>
    Task<ValidationFailureReport> GetValidationFailuresAsync(
        ValidationFailureFilter filter,
        CancellationToken cancellationToken);

    /// <summary>The audit trail and the security event timeline.</summary>
    /// <summary>
    /// Values report filters are offered as suggestions: employees, the
    /// departments in use, and optionally the audit event types recorded.
    /// </summary>
    Task<ReportFilterOptions> GetFilterOptionsAsync(bool includeEventTypes, CancellationToken cancellationToken);

    Task<AuditSearchResult> SearchAuditAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? eventType,
        Guid? correlationId,
        CancellationToken cancellationToken);
}

/// <summary>Which attendance records to report.</summary>
/// <param name="FromDate">First business-local attendance date, inclusive.</param>
/// <param name="ToDate">Last business-local attendance date, inclusive.</param>
/// <param name="UserId">One employee, by sign-in identifier.</param>
/// <param name="Department">One department, exactly as recorded.</param>
/// <param name="OfficeLocationId">Records clocked in or out at this office.</param>
/// <param name="Status">Record status.</param>
/// <param name="Exception">Only rows showing this exception.</param>
public readonly record struct AttendanceReportFilter(
    DateOnly FromDate,
    DateOnly ToDate,
    string? UserId = null,
    string? Department = null,
    int? OfficeLocationId = null,
    AttendanceRecordStatus? Status = null,
    AttendanceExceptionKind? Exception = null);

/// <summary>Attendance record status, as stored.</summary>
public enum AttendanceRecordStatus : byte
{
    /// <summary>Clocked in, not yet out.</summary>
    Open = 1,

    /// <summary>Clocked in and out.</summary>
    Closed = 2,

    /// <summary>Changed by an administrative correction.</summary>
    Corrected = 3,
}

/// <summary>The exception rows §20 asks to be reportable on their own.</summary>
public enum AttendanceExceptionKind : byte
{
    /// <summary>An open record from a business day already past.</summary>
    MissingClockOut = 1,

    /// <summary>Clocked in after the configured close (where configured).</summary>
    LateClockIn = 2,

    /// <summary>Clocked out before the configured opening (where configured).</summary>
    EarlyClockOut = 3,

    /// <summary>The platform flagged the clock-in position as artificial.</summary>
    MockedLocation = 4,
}

/// <summary>
/// One attendance record, with the evidence behind it.
/// </summary>
/// <param name="AttendancePublicId">The record, as the portal refers to it (for corrections).</param>
/// <param name="AttendanceDate">The business-local attendance date.</param>
/// <param name="UserId">The employee's sign-in identifier.</param>
/// <param name="EmployeeName">Their name.</param>
/// <param name="Department">Their department, where recorded.</param>
/// <param name="Status">Open, closed or corrected.</param>
/// <param name="ClockInUtc">When the record was opened.</param>
/// <param name="ClockOutUtc">When it was closed, if it was.</param>
/// <param name="DurationMinutes">Duration computed by the database.</param>
/// <param name="IsLateClockIn">Late flag, or null where the rule is unconfigured.</param>
/// <param name="IsEarlyClockOut">Early flag, or null where unconfigured.</param>
/// <param name="IsMissingClockOut">
/// An open record from a day already past. These are the rows somebody has to
/// act on: the employee left without clocking out, and no amount of reporting
/// fixes it without a correction.
/// </param>
/// <param name="ClockInOfficeName">Where the clock-in was accepted.</param>
/// <param name="ClockInDistanceMeters">Measured distance at clock-in.</param>
/// <param name="ClockInAccuracyMeters">Accuracy the device reported.</param>
/// <param name="ClockInWasMockedLocation">
/// Whether the platform flagged the position as artificial. Recorded even when
/// policy allowed it through, because a pattern of these is worth seeing.
/// </param>
public readonly record struct AttendanceReportRow(
    Guid AttendancePublicId,
    DateOnly AttendanceDate,
    string UserId,
    string EmployeeName,
    string? Department,
    AttendanceRecordStatus Status,
    DateTimeOffset? ClockInUtc,
    DateTimeOffset? ClockOutUtc,
    int? DurationMinutes,
    bool? IsLateClockIn,
    bool? IsEarlyClockOut,
    bool IsMissingClockOut,
    string? ClockInOfficeName,
    decimal? ClockInDistanceMeters,
    decimal? ClockInAccuracyMeters,
    bool ClockInWasMockedLocation);

/// <summary>Audit entries and security events over the same window.</summary>
/// <param name="AuditEntries">Administrative and attendance actions.</param>
/// <param name="SecurityEvents">
/// The precise failure reasons, including the ones the mobile API collapses to
/// INVALID_CREDENTIALS so an internet-facing endpoint cannot confirm a correct
/// password (CON-09). <b>This is the only place that distinction survives.</b>
/// </param>
public readonly record struct AuditSearchResult(
    IReadOnlyList<AuditEntry> AuditEntries,
    IReadOnlyList<SecurityEventEntry> SecurityEvents);

/// <summary>One audit entry.</summary>
/// <param name="EventType">What happened.</param>
/// <param name="OccurredUtc">When.</param>
/// <param name="ActorDisplay">Who did it.</param>
/// <param name="SubjectType">What kind of thing it was done to.</param>
/// <param name="SubjectId">Which one.</param>
/// <param name="Succeeded">Whether it succeeded.</param>
/// <param name="ReasonCode">Why, where there is a reason.</param>
/// <param name="SourceApplication">Which application observed it.</param>
/// <param name="CorrelationId">Ties it to a request.</param>
/// <param name="Details">Small JSON object with context.</param>
/// <param name="LedgerCommitTime">
/// When the ledger committed the row. Supplied by SQL Server rather than by the
/// application, so it cannot be back-dated by whoever wrote the entry.
/// </param>
/// <param name="LedgerPrincipalName">
/// The database principal that committed it — including, importantly, when that
/// is not the application.
/// </param>
public readonly record struct AuditEntry(
    string EventType,
    DateTimeOffset OccurredUtc,
    string? ActorDisplay,
    string? SubjectType,
    string? SubjectId,
    bool Succeeded,
    string? ReasonCode,
    string? SourceApplication,
    Guid? CorrelationId,
    string? Details,
    DateTimeOffset? LedgerCommitTime,
    string? LedgerPrincipalName);

/// <summary>One security event.</summary>
/// <param name="EventType">Stable category.</param>
/// <param name="OccurredUtc">When.</param>
/// <param name="Severity">1 Information, 2 Warning, 3 Critical.</param>
/// <param name="SubjectKey">The account or device concerned.</param>
/// <param name="ReasonCode">The precise internal reason.</param>
/// <param name="SourceApplication">Which application observed it.</param>
/// <param name="CorrelationId">Ties it to a request.</param>
public readonly record struct SecurityEventEntry(
    string EventType,
    DateTimeOffset OccurredUtc,
    byte Severity,
    string? SubjectKey,
    string ReasonCode,
    string? SourceApplication,
    Guid? CorrelationId);

/// <summary>How a refused check is classified.</summary>
public enum ValidationFailureCategory : byte
{
    /// <summary>The position was refused or could not be judged.</summary>
    Location = 1,

    /// <summary>The device was unregistered, unapproved, revoked or failed attestation.</summary>
    Device = 2,

    /// <summary>Password, authenticator code, lockout or an inactive employee.</summary>
    Identity = 3,

    /// <summary>A malformed, replayed, stale or forged request signature.</summary>
    Integrity = 4,
}

/// <summary>Which refusals to report.</summary>
/// <param name="FromUtc">Start of the window.</param>
/// <param name="ToUtc">End of the window.</param>
/// <param name="Category">One category, or all.</param>
/// <param name="SubjectKey">
/// An employee's user id or an account name. Also finds refusals of any device
/// bound to that employee, which name the device rather than the person.
/// </param>
/// <param name="BeforeSecurityEventId">Continues a page.</param>
public readonly record struct ValidationFailureFilter(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    ValidationFailureCategory? Category = null,
    string? SubjectKey = null,
    long? BeforeSecurityEventId = null);

/// <summary>A page of refusals, and the pattern across the whole window.</summary>
/// <param name="Failures">Newest first.</param>
/// <param name="Summary">Counts by category and reason over every matching refusal.</param>
/// <param name="HasMore">Whether an older page exists.</param>
public readonly record struct ValidationFailureReport(
    IReadOnlyList<ValidationFailureEntry> Failures,
    IReadOnlyList<ValidationFailureSummary> Summary,
    bool HasMore);

/// <summary>One refused check.</summary>
/// <param name="SecurityEventId">Ledger row; also the paging cursor.</param>
/// <param name="OccurredUtc">When.</param>
/// <param name="Category">How it is classified.</param>
/// <param name="EventType">The writer's event type.</param>
/// <param name="Severity">1 Information, 2 Warning, 3 Critical.</param>
/// <param name="ReasonCode">The precise internal reason.</param>
/// <param name="SubjectKey">The account or device named by the event.</param>
/// <param name="DevicePublicId">The device, where one was involved.</param>
/// <param name="EmployeeUserId">The employee behind it, where one can be identified.</param>
/// <param name="EmployeeName">Their name.</param>
/// <param name="SourceApplication">Which application observed it.</param>
/// <param name="CorrelationId">Ties it to a request and the server log.</param>
/// <param name="LedgerCommitTime">When the ledger committed the row.</param>
public readonly record struct ValidationFailureEntry(
    long SecurityEventId,
    DateTimeOffset OccurredUtc,
    ValidationFailureCategory Category,
    string EventType,
    byte Severity,
    string ReasonCode,
    string? SubjectKey,
    Guid? DevicePublicId,
    string? EmployeeUserId,
    string? EmployeeName,
    string? SourceApplication,
    Guid? CorrelationId,
    DateTimeOffset? LedgerCommitTime);

/// <summary>How often one reason occurred in the window.</summary>
/// <param name="Category">Its category.</param>
/// <param name="ReasonCode">The reason.</param>
/// <param name="EventCount">How many refusals.</param>
/// <param name="DistinctSubjects">How many different accounts or devices.</param>
/// <param name="FirstOccurredUtc">Earliest in the window.</param>
/// <param name="LastOccurredUtc">Latest in the window.</param>
public readonly record struct ValidationFailureSummary(
    ValidationFailureCategory Category,
    string ReasonCode,
    int EventCount,
    int DistinctSubjects,
    DateTimeOffset FirstOccurredUtc,
    DateTimeOffset LastOccurredUtc);

/// <summary>Row limits shared by the reports and the pages that render them.</summary>
public static class ReportLimits
{
    /* DailyAttendanceRows is gone. It capped the daily report at a thousand
       rows and told the reader to narrow the range, which is an odd thing to
       ask of somebody running a month-end report: the records exist and they
       wanted them. The report is paged now, so the rest is a page away and
       Paging.MaximumPageSize is the only ceiling left. */

    /// <summary>Refusals per page of the validation-failure report.</summary>
    public const int ValidationFailuresPerPage = 200;
}

/// <summary>An employee as a report filter suggestion.</summary>
/// <param name="UserId">What the filter matches on.</param>
/// <param name="DisplayName">Shown beside it, so the right person is picked.</param>
public readonly record struct EmployeeOption(string UserId, string DisplayName);

/// <summary>Suggestions for report filters.</summary>
/// <param name="Employees">Every employee, leavers included: reports cover past days.</param>
/// <param name="Departments">Departments in use.</param>
/// <param name="EventTypes">Audit event types recorded; empty unless asked for.</param>
public sealed record ReportFilterOptions(
    IReadOnlyList<EmployeeOption> Employees,
    IReadOnlyList<string> Departments,
    IReadOnlyList<string> EventTypes)
{
    /// <summary>No suggestions: the filters still work as plain text boxes.</summary>
    public static ReportFilterOptions None { get; } = new([], [], []);
}
