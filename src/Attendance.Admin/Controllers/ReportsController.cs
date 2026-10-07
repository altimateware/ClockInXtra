using Attendance.Admin.Security;
using Attendance.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Admin.Controllers;

/// <summary>
/// Attendance reporting and the audit trail (§20, §32).
/// </summary>
[Authorize]
public sealed class ReportsController : Controller
{
    private readonly IReportingRepository _reporting;
    private readonly IOfficeLocationAdministrationRepository _locations;

    /// <summary>Creates the controller.</summary>
    public ReportsController(IReportingRepository reporting, IOfficeLocationAdministrationRepository locations)
    {
        ArgumentNullException.ThrowIfNull(reporting);
        ArgumentNullException.ThrowIfNull(locations);
        _reporting = reporting;
        _locations = locations;
    }

    /// <summary>Daily attendance, with the evidence behind each record.</summary>
    /// <remarks>
    /// Defaults to the last seven days. Missing clock-outs can be isolated
    /// because they are the rows somebody actually has to act on — an employee
    /// left without closing their record, and only a correction fixes it.
    /// </remarks>
    [HttpGet]
    [Authorize(Permissions.ReportView)]
    public async Task<IActionResult> Attendance(
        DateOnly? from,
        DateOnly? to,
        string? userId,
        string? department,
        int? officeLocationId,
        AttendanceRecordStatus? status,
        AttendanceExceptionKind? exception,
        CancellationToken cancellationToken = default)
    {
        // Tomorrow in UTC is today or later in every time zone, so the default
        // range cannot miss today's records for a business ahead of UTC.
        DateOnly toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        DateOnly fromDate = from ?? toDate.AddDays(-7);

        if (fromDate > toDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
        }

        // An enum bound from a query string accepts any number; the procedure
        // would refuse it, but a value outside the enum is simply not a filter.
        AttendanceReportFilter filter = new(
            fromDate,
            toDate,
            userId,
            department,
            officeLocationId,
            status is { } s && Enum.IsDefined(s) ? s : null,
            exception is { } e && Enum.IsDefined(e) ? e : null);

        IReadOnlyList<AttendanceReportRow> rows = await _reporting.GetDailyAttendanceAsync(filter, cancellationToken);
        IReadOnlyList<OfficeLocationDetail> offices = await _locations.GetAllAsync(1, cancellationToken);

        ViewData["Filter"] = filter;
        ViewData["Offices"] = offices;
        ViewData["FilterOptions"] = await _reporting.GetFilterOptionsAsync(includeEventTypes: false, cancellationToken);

        return View(rows);
    }

    /// <summary>
    /// Refused location, device, identity and request-integrity checks (§20).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A refused clock-in leaves no attendance record, so this is the only
    /// report in which it appears. The summary above the list is the useful part:
    /// if accuracy refusals dominate, the office radius is refusing honest
    /// employees (CON-01), and that is an argument to be made with these numbers.
    /// </para>
    /// </remarks>
    [HttpGet]
    [Authorize(Permissions.ReportView)]
    public async Task<IActionResult> Failures(
        DateTimeOffset? from,
        DateTimeOffset? to,
        ValidationFailureCategory? category,
        string? subject,
        long? before,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset toUtc = to ?? DateTimeOffset.UtcNow;
        DateTimeOffset fromUtc = from ?? toUtc.AddDays(-7);

        if (fromUtc > toUtc)
        {
            (fromUtc, toUtc) = (toUtc, fromUtc);
        }

        ValidationFailureFilter filter = new(
            fromUtc,
            toUtc,
            category is { } c && Enum.IsDefined(c) ? c : null,
            string.IsNullOrWhiteSpace(subject) || subject.Length > 128 ? null : subject,
            before is > 0 ? before : null);

        ValidationFailureReport report = await _reporting.GetValidationFailuresAsync(filter, cancellationToken);

        ViewData["Filter"] = filter;
        ViewData["FilterOptions"] = await _reporting.GetFilterOptionsAsync(includeEventTypes: false, cancellationToken);

        return View(report);
    }

    /// <summary>
    /// The audit trail and the security event timeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are shown together deliberately. The audit log records what was
    /// done; the security events record what was <em>refused</em>, with the
    /// precise reason the API withheld from the caller. An investigation needs
    /// both in one window — a successful sign-in is only interesting next to the
    /// forty failures that preceded it.
    /// </para>
    /// </remarks>
    [HttpGet]
    [Authorize(Permissions.AuditView)]
    public async Task<IActionResult> Audit(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? eventType,
        Guid? correlationId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset toUtc = to ?? DateTimeOffset.UtcNow;
        DateTimeOffset fromUtc = from ?? toUtc.AddDays(-1);

        AuditSearchResult result = await _reporting.SearchAuditAsync(
            fromUtc, toUtc, eventType, correlationId, cancellationToken);

        ViewData["From"] = fromUtc;
        ViewData["To"] = toUtc;
        ViewData["EventType"] = eventType;
        ViewData["CorrelationId"] = correlationId;
        ViewData["FilterOptions"] = await _reporting.GetFilterOptionsAsync(includeEventTypes: true, cancellationToken);

        return View(result);
    }
}
