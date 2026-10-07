using System.Security.Claims;
using Attendance.Admin.Security;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Admin.Controllers;

/// <summary>
/// The portal's landing page.
/// </summary>
/// <remarks>
/// Deliberately shows what is <b>outstanding</b> rather than a set of statistics.
/// Two things stop this system working, and both are administrative: business
/// settings nobody has decided yet, and device registrations nobody has approved.
/// An employee standing at a door cannot resolve either, so they belong on the
/// first page an administrator sees.
/// </remarks>
public sealed class DashboardController : Controller
{
    /// <summary>Shows the dashboard.</summary>
    [HttpGet]
    public IActionResult Index() =>
        View(new DashboardViewModel
        {
            DisplayName = User.FindFirst("displayName")?.Value
                ?? User.Identity?.Name
                ?? "administrator",
            Permissions = [.. User.FindAll(Permissions.ClaimType).Select(c => c.Value).Order(StringComparer.Ordinal)],
        });
}

/// <summary>What the dashboard shows.</summary>
public sealed class DashboardViewModel
{
    /// <summary>Who is signed in.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The permissions this administrator actually holds.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];
}
