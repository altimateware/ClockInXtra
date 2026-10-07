using System.Globalization;
using Attendance.Admin.Models;
using Attendance.Admin.Security;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Admin.Controllers;

/// <summary>
/// The configuration screen — where the outstanding business decisions are made.
/// </summary>
/// <remarks>
/// <para>
/// <b>This screen is what unblocks attendance.</b> Several settings are seeded
/// with no value because they are business decisions nobody has taken, and the
/// attendance procedures refuse to operate while a mandatory one is missing
/// rather than inventing a rule (§15, §68). Until someone uses this page, clock-in
/// returns ATTENDANCE_NOT_CONFIGURED and the mobile app shows an explanation
/// instead of a button.
/// </para>
/// <para>
/// Clearing a value is permitted and returns the setting to undecided. That is
/// deliberate: a rule withdrawn should stop being applied, not linger as the last
/// value somebody happened to type.
/// </para>
/// </remarks>
[Authorize]
public sealed class SettingsController : Controller
{
    private readonly ISettingAdministrationRepository _settings;

    /// <summary>Creates the controller.</summary>
    public SettingsController(ISettingAdministrationRepository settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>Lists settings, outstanding decisions first.</summary>
    [HttpGet]
    [Authorize(Permissions.SettingView)]
    public async Task<IActionResult> Index(string? category, CancellationToken cancellationToken)
    {
        IReadOnlyList<ApplicationSettingRow> settings =
            await _settings.GetAllAsync(category, cancellationToken);

        // The zone list comes from the database, which is what validates the
        // value: offering the web server's own list could show a zone the
        // database then refuses.
        IReadOnlyList<TimeZoneOption> timeZones =
            settings.Any(s => s.DataType == SettingInput.TimeZoneType)
                ? await _settings.GetTimeZonesAsync(cancellationToken)
                : [];

        return View(new SettingsViewModel
        {
            Settings =
            [
                .. settings
                    .OrderByDescending(s => s.IsMandatoryForAttendance && s.IsUnset)
                    .ThenByDescending(s => s.IsUnset)
                    .ThenBy(s => s.Category, StringComparer.Ordinal)
                    .ThenBy(s => s.SettingKey, StringComparer.Ordinal),
            ],
            TimeZones = timeZones,
        });
    }

    /// <summary>Records a decision.</summary>
    [HttpPost]
    [Authorize(Permissions.SettingManage)]
    public async Task<IActionResult> Set(
        string settingKey,
        string? settingValue,
        bool confirm,
        string rowVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settingKey) || !TryDecodeRowVersion(rowVersion, out byte[] token))
        {
            TempData["Error"] = "That request was not valid. Please reload and try again.";
            return RedirectToAction(nameof(Index));
        }

        // An empty box means "undecided", not an empty string. The distinction
        // matters: an empty string would pass as a value and the system would
        // then apply a rule nobody chose.
        string? value = string.IsNullOrWhiteSpace(settingValue) ? null : settingValue.Trim();

        AttendanceResultCode result = await _settings.SetAsync(
            settingKey, value, confirm, token, CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        // Name the rule that was broken, rather than "invalid value": the
        // bounds are the setting's own, so they are looked up only on failure.
        string invalid = result == AttendanceResultCode.InvalidRequest
            ? await DescribeInvalidAsync(settingKey, cancellationToken)
            : string.Empty;

        TempData[result == AttendanceResultCode.Success ? "Message" : "Error"] = result switch
        {
            AttendanceResultCode.Success when value is null =>
                $"{SettingInput.Title(settingKey)} cleared. Unless blank is a valid choice for it, it is now undecided and anything that depends on it will refuse.",
            AttendanceResultCode.Success =>
                $"{SettingInput.Title(settingKey)} saved.",
            AttendanceResultCode.InvalidTimeZone =>
                "That time zone is not one the database recognises. Choose one from the list.",
            AttendanceResultCode.InvalidRequest => invalid,
            AttendanceResultCode.ConcurrencyConflict =>
                "Somebody else changed that setting while this page was open. Reload and check the current value.",
            AttendanceResultCode.NotFound =>
                "That setting does not exist.",
            _ =>
                "The setting could not be saved.",
        };

        return RedirectToAction(nameof(Index));
    }

    private async Task<string> DescribeInvalidAsync(string settingKey, CancellationToken cancellationToken)
    {
        IReadOnlyList<ApplicationSettingRow> all = await _settings.GetAllAsync(null, cancellationToken);
        ApplicationSettingRow? setting = all.FirstOrDefault(s => s.SettingKey == settingKey);

        return setting is { } found
            ? $"{SettingInput.Title(settingKey)} was not saved: {SettingInput.Rule(found)}."
            : "That value was not accepted.";
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

/// <summary>The settings page: every setting, and the lists its inputs offer.</summary>
public sealed class SettingsViewModel
{
    /// <summary>Settings, outstanding decisions first.</summary>
    public IReadOnlyList<ApplicationSettingRow> Settings { get; init; } = [];

    /// <summary>Time zones the database accepts, west to east.</summary>
    public IReadOnlyList<TimeZoneOption> TimeZones { get; init; } = [];
}
