using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Attendance.Admin.Security;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Admin.Controllers;

/// <summary>
/// Office location administration (§19).
/// </summary>
/// <remarks>
/// Until at least one active office exists, every location check refuses and
/// nobody can clock in anywhere.
/// </remarks>
[Authorize]
public sealed class LocationsController : Controller
{
    private readonly IOfficeLocationAdministrationRepository _locations;

    /// <summary>Creates the controller.</summary>
    public LocationsController(IOfficeLocationAdministrationRepository locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        _locations = locations;
    }

    /// <summary>Lists offices with the accuracy observed at each.</summary>
    [HttpGet]
    [Authorize(Permissions.OfficeLocationView)]
    public async Task<IActionResult> Index(int statisticsDays = 30, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OfficeLocationDetail> locations =
            await _locations.GetAllAsync(Math.Clamp(statisticsDays, 1, 3650), cancellationToken);

        ViewData["StatisticsDays"] = statisticsDays;

        return View(locations);
    }

    /// <summary>Creates an office location.</summary>
    [HttpPost]
    [Authorize(Permissions.OfficeLocationManage)]
    public async Task<IActionResult> Create(OfficeLocationForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (!ModelState.IsValid)
        {
            TempData["Error"] = FirstError();
            return RedirectToAction(nameof(Index));
        }

        AttendanceResultCode result = await _locations.CreateAsync(
            form.ToInput(), CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        SetOutcome(result, $"'{form.Name}' created.");

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Updates an office location.</summary>
    [HttpPost]
    [Authorize(Permissions.OfficeLocationManage)]
    public async Task<IActionResult> Update(
        int officeLocationId,
        string rowVersion,
        OfficeLocationForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (!ModelState.IsValid || !TryDecodeRowVersion(rowVersion, out byte[] token))
        {
            TempData["Error"] = FirstError() ?? "That request was not valid. Please reload and try again.";
            return RedirectToAction(nameof(Index));
        }

        AttendanceResultCode result = await _locations.UpdateAsync(
            officeLocationId, form.ToInput(), token, CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        SetOutcome(result, $"'{form.Name}' updated. The change reaches the mobile API within about thirty seconds.");

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Enables or disables an office location.</summary>
    [HttpPost]
    [Authorize(Permissions.OfficeLocationManage)]
    public async Task<IActionResult> SetStatus(
        int officeLocationId,
        bool isActive,
        string rowVersion,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!TryDecodeRowVersion(rowVersion, out byte[] token))
        {
            TempData["Error"] = "That request was not valid. Please reload and try again.";
            return RedirectToAction(nameof(Index));
        }

        // Disabling an office stops everyone clocking in there. The reason goes
        // into the audit trail, and "why could nobody clock in at Ikeja on
        // Monday?" deserves an answer there.
        if (!isActive && string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "Give a reason for disabling the office. It is recorded in the audit trail.";
            return RedirectToAction(nameof(Index));
        }

        AttendanceResultCode result = await _locations.SetStatusAsync(
            officeLocationId, isActive, token, reason, CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        SetOutcome(
            result,
            isActive
                ? "Office enabled."
                : "Office disabled. Nobody can clock in or out there once the change reaches the API.");

        return RedirectToAction(nameof(Index));
    }

    private void SetOutcome(AttendanceResultCode result, string success)
    {
        TempData[result == AttendanceResultCode.Success ? "Message" : "Error"] = result switch
        {
            AttendanceResultCode.Success => success,
            AttendanceResultCode.DuplicateName => "An office with that name already exists.",
            AttendanceResultCode.ConcurrencyConflict =>
                "Somebody else changed that office while this page was open. Reload and check the current values.",
            AttendanceResultCode.InvalidRequest =>
                "Those values were refused: latitude must be −90 to 90, longitude −180 to 180, and the radius 1 to 10,000 metres.",
            AttendanceResultCode.NotFound => "That office no longer exists.",
            _ => "The office could not be saved.",
        };
    }

    private string? FirstError() =>
        ModelState.Values
            .SelectMany(state => state.Errors)
            .Select(error => error.ErrorMessage)
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));

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

/// <summary>The office location form.</summary>
/// <remarks>
/// Ranges are enforced here and again in the stored procedure. That is not
/// duplication for its own sake: a latitude outside its real range would make
/// every distance calculation at that office meaningless, so it is refused at
/// both boundaries (§19, §44).
/// </remarks>
public sealed class OfficeLocationForm
{
    /// <summary>A name people recognise.</summary>
    [Required(ErrorMessage = "Give the office a name.")]
    [StringLength(120, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional detail.</summary>
    [StringLength(400)]
    public string? Description { get; init; }

    /// <summary>Degrees, -90 to 90.</summary>
    [Range(-90d, 90d, ErrorMessage = "Latitude must be between -90 and 90.")]
    public decimal Latitude { get; init; }

    /// <summary>Degrees, -180 to 180.</summary>
    [Range(-180d, 180d, ErrorMessage = "Longitude must be between -180 and 180.")]
    public decimal Longitude { get; init; }

    /// <summary>The proximity threshold in metres.</summary>
    [Range(1d, 10000d, ErrorMessage = "The radius must be between 1 and 10000 metres.")]
    public decimal AllowedRadiusMeters { get; init; } = 5m;

    /// <summary>Whether attendance may be recorded here.</summary>
    public bool IsActive { get; init; }

    /// <summary>Converts the form into the repository's input.</summary>
    public OfficeLocationInput ToInput() =>
        new(Name.Trim(), string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Latitude, Longitude, AllowedRadiusMeters, IsActive);
}
