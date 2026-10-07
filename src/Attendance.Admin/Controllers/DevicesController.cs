using System.Globalization;
using Attendance.Admin.Security;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Admin.Controllers;

/// <summary>
/// Device approval and revocation (§18, DEC-04).
/// </summary>
/// <remarks>
/// <para>
/// <b>This controller is where DEC-04 is actually enforced by a person.</b>
/// Registration proves somebody knew an employee's password and a valid
/// authenticator code, and that the key lives in device hardware. It does not
/// prove who is holding the handset. If approval were automatic, a phished
/// password plus one code would silently move an employee's attendance to an
/// attacker's phone.
/// </para>
/// </remarks>
[Authorize]
public sealed class DevicesController : Controller
{
    private readonly IDeviceAdministrationRepository _devices;

    /// <summary>Creates the controller.</summary>
    public DevicesController(IDeviceAdministrationRepository devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
    }

    /// <summary>Lists registrations awaiting approval.</summary>
    [HttpGet]
    [Authorize(Permissions.DeviceView)]
    public async Task<IActionResult> Pending(CancellationToken cancellationToken)
    {
        IReadOnlyList<PendingDeviceApproval> pending =
            await _devices.GetPendingApprovalsAsync(cancellationToken);

        return View(pending);
    }

    /// <summary>Approves a registration.</summary>
    /// <remarks>
    /// The row version travels with the form. If the device changed since the
    /// list was rendered — approved by a colleague, or revoked — the procedure
    /// refuses rather than acting on a stale view of the world.
    /// </remarks>
    [HttpPost]
    [Authorize(Permissions.DeviceApprove)]
    public async Task<IActionResult> Approve(
        int deviceId,
        string rowVersion,
        CancellationToken cancellationToken)
    {
        if (!TryDecodeRowVersion(rowVersion, out byte[] token))
        {
            TempData["Error"] = "That request was not valid. Please reload the list and try again.";
            return RedirectToAction(nameof(Pending));
        }

        DeviceApprovalOutcome outcome = await _devices.ApproveAsync(
            deviceId, token, CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        TempData[outcome.ResultCode == AttendanceResultCode.Success ? "Message" : "Error"] =
            outcome.ResultCode switch
            {
                AttendanceResultCode.Success when outcome.RevokedPreviousDevicePublicId is not null =>
                    "Device approved. The employee's previous device has been revoked and will stop working immediately.",
                AttendanceResultCode.Success =>
                    "Device approved.",
                AttendanceResultCode.ConcurrencyConflict =>
                    "That device changed while you were looking at it. Please reload the list and check before approving.",
                AttendanceResultCode.InvalidRequest =>
                    "That device is no longer awaiting approval.",
                AttendanceResultCode.NotFound =>
                    "That device no longer exists.",
                _ =>
                    "The device could not be approved.",
            };

        return RedirectToAction(nameof(Pending));
    }

    /// <summary>Revokes a device.</summary>
    [HttpPost]
    [Authorize(Permissions.DeviceRevoke)]
    public async Task<IActionResult> Revoke(
        int deviceId,
        string rowVersion,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!TryDecodeRowVersion(rowVersion, out byte[] token))
        {
            TempData["Error"] = "That request was not valid. Please reload the list and try again.";
            return RedirectToAction(nameof(Pending));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            // The reason is shown to the employee whose device stopped working.
            // A blank one leaves them with an app that fails and no explanation.
            TempData["Error"] = "Give a reason: it is shown to the employee whose device this is.";
            return RedirectToAction(nameof(Pending));
        }

        AttendanceResultCode result = await _devices.RevokeAsync(
            deviceId, token, reason.Trim(), CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        TempData[result == AttendanceResultCode.Success ? "Message" : "Error"] =
            result == AttendanceResultCode.Success
                ? "Device revoked. It will stop working immediately."
                : "The device could not be revoked.";

        return RedirectToAction(nameof(Pending));
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
