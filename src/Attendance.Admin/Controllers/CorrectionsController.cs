using System.Globalization;
using Attendance.Admin.Security;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Admin.Controllers;

/// <summary>
/// Attendance corrections: request, approve or reject (§15, §20; DEC-08).
/// </summary>
/// <remarks>
/// <para>
/// The usual reason is a missing clock-out: the employee left without clocking
/// out, the day ended at midnight, and only a correction can close the record.
/// </para>
/// <para>
/// Every rule is enforced by the stored procedures, not here — one pending
/// correction per record, corrected times within the record's own attendance
/// day and not in the future, and above all that <b>the approver is never the
/// requester</b>, which a table constraint guarantees whatever calls it. This
/// controller turns their answers into sentences.
/// </para>
/// <para>
/// Times are entered and shown in the business time zone, because that is how
/// administrators think about a working day; the database converts them.
/// </para>
/// </remarks>
[Authorize]
public sealed class CorrectionsController : Controller
{
    private readonly IAttendanceCorrectionRepository _corrections;

    /// <summary>Creates the controller.</summary>
    public CorrectionsController(IAttendanceCorrectionRepository corrections)
    {
        ArgumentNullException.ThrowIfNull(corrections);
        _corrections = corrections;
    }

    /// <summary>The corrections list: awaiting approval first.</summary>
    [HttpGet]
    public async Task<IActionResult> Index(AttendanceCorrectionStatus? status, CancellationToken cancellationToken)
    {
        // Anyone who may request or approve corrections needs to see them.
        if (!CanRequest() && !CanApprove())
        {
            return Forbid();
        }

        IReadOnlyList<AttendanceCorrectionSummary> corrections = await _corrections.SearchAsync(
            status is { } s && Enum.IsDefined(s) ? s : null, cancellationToken);

        ViewData["Status"] = status;
        ViewData["CanApprove"] = CanApprove();
        ViewData["AdministratorId"] = CurrentAdministratorId();

        return View(corrections);
    }

    /// <summary>The form to request a correction to one attendance record.</summary>
    [HttpGet]
    [ActionName("Request")]
    [Authorize(Permissions.AttendanceCorrect)]
    public async Task<IActionResult> RequestForm(Guid id, CancellationToken cancellationToken)
    {
        AttendanceForCorrection? record = await _corrections.GetForCorrectionAsync(id, cancellationToken);

        return record is null ? NotFound() : View(record);
    }

    /// <summary>Submits a correction request.</summary>
    /// <param name="id">The attendance record.</param>
    /// <param name="clockIn">Corrected clock-in time of day (HH:mm), or blank to leave it.</param>
    /// <param name="clockOut">Corrected clock-out time of day (HH:mm), or blank to leave it.</param>
    /// <param name="reason">Why — kept with the correction and in the audit trail.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    [HttpPost]
    [ActionName("Request")]
    [Authorize(Permissions.AttendanceCorrect)]
    public async Task<IActionResult> RequestPost(
        Guid id,
        string? clockIn,
        string? clockOut,
        string? reason,
        CancellationToken cancellationToken)
    {
        AttendanceForCorrection? record = await _corrections.GetForCorrectionAsync(id, cancellationToken);

        if (record is null)
        {
            return NotFound();
        }

        if (!TryParseTime(clockIn, out TimeOnly? inTime) || !TryParseTime(clockOut, out TimeOnly? outTime))
        {
            return Refuse(record, "Enter times as hours and minutes, for example 08:15.");
        }

        if (inTime is null && outTime is null)
        {
            return Refuse(record, "Enter the corrected clock-in time, the corrected clock-out time, or both.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Refuse(record, "Give a reason. It stays with the record permanently and is what the approver decides on.");
        }

        // Both times belong to the record's own attendance day: the database
        // refuses anything else, and building them from that date here means
        // an administrator only ever types a time of day.
        DateTime? inLocal = inTime is { } i ? record.AttendanceDate.ToDateTime(i) : null;
        DateTime? outLocal = outTime is { } o ? record.AttendanceDate.ToDateTime(o) : null;

        CorrectionRequestResult result = await _corrections.RequestAsync(
            id, inLocal, outLocal, reason.Trim(), CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        if (result.ResultCode != AttendanceResultCode.Success)
        {
            return Refuse(record, result.ResultCode switch
            {
                AttendanceResultCode.CorrectionsDisabled =>
                    "Corrections are switched off (Settings: Attendance.AllowCorrections).",
                AttendanceResultCode.AttendanceNotConfigured =>
                    "Corrections cannot be requested until the approval rule and the business time zone are set.",
                AttendanceResultCode.ConcurrencyConflict =>
                    "A correction to this record is already awaiting approval. It must be approved or rejected first.",
                AttendanceResultCode.InvalidRequest =>
                    "Those times cannot be used: the clock-out must not be before the clock-in, and neither may be in the future.",
                _ => "The correction could not be requested.",
            });
        }

        TempData["Message"] = result.Applied
            ? "Correction applied."
            : "Correction requested. Another administrator must approve it before the record changes.";

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Approves or rejects a pending correction.</summary>
    [HttpPost]
    [Authorize(Permissions.AttendanceApproveCorrection)]
    public async Task<IActionResult> Decide(
        long id,
        bool approve,
        string rowVersion,
        string? note,
        CancellationToken cancellationToken)
    {
        if (!TryDecodeRowVersion(rowVersion, out byte[] token))
        {
            TempData["Error"] = "That request was not valid. Please reload the list and try again.";
            return RedirectToAction(nameof(Index));
        }

        if (!approve && string.IsNullOrWhiteSpace(note))
        {
            // The requester needs to know why, and so does anyone reading the
            // record later.
            TempData["Error"] = "Give a reason for rejecting the correction.";
            return RedirectToAction(nameof(Index));
        }

        AttendanceResultCode result = await _corrections.DecideAsync(
            id, approve, note, token, CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        TempData[result == AttendanceResultCode.Success ? "Message" : "Error"] = result switch
        {
            AttendanceResultCode.Success when approve => "Correction approved and applied to the record.",
            AttendanceResultCode.Success => "Correction rejected. The record is unchanged.",
            AttendanceResultCode.SeparationOfDutiesViolation =>
                "You requested this correction, so another administrator must decide it.",
            AttendanceResultCode.ConcurrencyConflict =>
                "That correction changed while you were looking at it. Please reload the list.",
            AttendanceResultCode.InvalidRequest => "That correction has already been decided.",
            AttendanceResultCode.CorrectionsDisabled => "Corrections are switched off.",
            AttendanceResultCode.NotFound => "That correction no longer exists.",
            _ => "The correction could not be decided.",
        };

        return RedirectToAction(nameof(Index));
    }

    private ViewResult Refuse(AttendanceForCorrection record, string message)
    {
        ViewData["Error"] = message;
        return View("Request", record);
    }

    private bool CanRequest() => User.HasClaim(Permissions.ClaimType, Permissions.AttendanceCorrect);

    private bool CanApprove() => User.HasClaim(Permissions.ClaimType, Permissions.AttendanceApproveCorrection);

    private static bool TryParseTime(string? value, out TimeOnly? time)
    {
        time = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly parsed))
        {
            time = parsed;
            return true;
        }

        return false;
    }

    private int CurrentAdministratorId() =>
        int.TryParse(
            User.FindFirst(AdministratorClaims.AdministratorId)?.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int id)
            ? id
            : throw new InvalidOperationException("The signed-in principal carries no administrator identifier.");

    private static bool TryDecodeRowVersion(string value, out byte[] token)
    {
        token = [];

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            token = Convert.FromBase64String(value);
            return token.Length == 8;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
