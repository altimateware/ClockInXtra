using System.Globalization;
using Attendance.Admin.Security;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Admin.Controllers;

/// <summary>
/// The department and job title lists employees choose from (DEC-11).
/// </summary>
/// <remarks>
/// Viewing needs <c>MobileUser.View</c> and changing needs
/// <c>MobileUser.Manage</c>: the lists exist to describe employees, so the
/// people who maintain employees maintain them. Entries are renamed or
/// deactivated, never deleted — employees hold them.
/// </remarks>
[Authorize]
public sealed class ReferenceListsController : Controller
{
    private readonly IReferenceListRepository _lists;

    /// <summary>Creates the controller.</summary>
    public ReferenceListsController(IReferenceListRepository lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        _lists = lists;
    }

    /// <summary>Both lists.</summary>
    [HttpGet]
    [Authorize(Permissions.MobileUserView)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(new ReferenceListsViewModel
        {
            Departments = await _lists.GetAllAsync(ReferenceList.Department, cancellationToken),
            JobTitles = await _lists.GetAllAsync(ReferenceList.JobTitle, cancellationToken),
        });
    }

    /// <summary>Adds an entry.</summary>
    [HttpPost]
    [Authorize(Permissions.MobileUserManage)]
    public async Task<IActionResult> Add(ReferenceList list, string? name, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(list) || string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Type a name to add.";
            return RedirectToAction(nameof(Index));
        }

        AttendanceResultCode result = await _lists.AddAsync(
            list, name.Trim(), CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        Report(result, list, $"“{name.Trim()}” added to {Plural(list)}.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Renames an entry, and with it every employee who holds it.</summary>
    [HttpPost]
    [Authorize(Permissions.MobileUserManage)]
    public async Task<IActionResult> Rename(
        ReferenceList list,
        int entryId,
        string? name,
        string rowVersion,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(list) || string.IsNullOrWhiteSpace(name) || !TryDecode(rowVersion, out byte[] token))
        {
            TempData["Error"] = "That request was not valid. Please reload and try again.";
            return RedirectToAction(nameof(Index));
        }

        AttendanceResultCode result = await _lists.RenameAsync(
            list, entryId, name.Trim(), token, CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        Report(result, list, $"Renamed to “{name.Trim()}”. Every employee who held it now shows the new name.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Offers an entry for choice again, or withdraws it.</summary>
    [HttpPost]
    [Authorize(Permissions.MobileUserManage)]
    public async Task<IActionResult> SetActive(
        ReferenceList list,
        int entryId,
        bool isActive,
        string rowVersion,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(list) || !TryDecode(rowVersion, out byte[] token))
        {
            TempData["Error"] = "That request was not valid. Please reload and try again.";
            return RedirectToAction(nameof(Index));
        }

        AttendanceResultCode result = await _lists.SetActiveAsync(
            list, entryId, isActive, token, CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        Report(
            result,
            list,
            isActive
                ? "Offered for choice again."
                : "No longer offered. Employees who already hold it keep it.");

        return RedirectToAction(nameof(Index));
    }

    private void Report(AttendanceResultCode result, ReferenceList list, string success) =>
        TempData[result == AttendanceResultCode.Success ? "Message" : "Error"] = result switch
        {
            AttendanceResultCode.Success => success,
            AttendanceResultCode.DuplicateName => $"That name is already on the {Singular(list)} list (names are compared without regard to capitals).",
            AttendanceResultCode.ConcurrencyConflict => "Somebody else changed that entry while this page was open. Reload and try again.",
            AttendanceResultCode.NotFound => "That entry no longer exists.",
            AttendanceResultCode.InvalidRequest => "Names must be 1 to 120 characters.",
            _ => "The change could not be saved.",
        };

    private static string Plural(ReferenceList list) => list == ReferenceList.Department ? "departments" : "job titles";

    private static string Singular(ReferenceList list) => list == ReferenceList.Department ? "department" : "job title";

    private int CurrentAdministratorId() =>
        int.TryParse(
            User.FindFirst(AdministratorClaims.AdministratorId)?.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int id)
            ? id
            : throw new InvalidOperationException("The signed-in principal carries no administrator identifier.");

    private static bool TryDecode(string? value, out byte[] token)
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

/// <summary>Both reference lists.</summary>
public sealed class ReferenceListsViewModel
{
    /// <summary>Departments, with how many employees hold each.</summary>
    public IReadOnlyList<ReferenceListEntry> Departments { get; init; } = [];

    /// <summary>Job titles, with how many employees hold each.</summary>
    public IReadOnlyList<ReferenceListEntry> JobTitles { get; init; } = [];
}
