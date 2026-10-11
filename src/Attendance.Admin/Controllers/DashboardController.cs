using System.Security.Claims;
using Attendance.Admin.Security;
using Attendance.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Admin.Controllers;

/// <summary>
/// The portal's landing page.
/// </summary>
/// <remarks>
/// <para>
/// Shows what is <b>outstanding</b> rather than a wall of statistics. Two
/// things stop this system working, and both are administrative: business
/// settings nobody has decided yet, and device registrations nobody has
/// approved. An employee standing at a door can resolve neither, so they
/// belong on the first page an administrator sees.
/// </para>
/// <para>
/// The page previously said so in this very comment and then rendered only the
/// viewer's permission list, which answered a question nobody arrives with.
/// </para>
/// <para>
/// <b>A count is not an authorisation.</b> Each section links to the page that
/// resolves it, and the links are hidden from an administrator who lacks the
/// permission to open them — but the counts themselves are deliberately shown
/// to anyone signed in. They disclose how much work is outstanding, never
/// whose, and a dashboard that renders differently for every role is a
/// dashboard nobody can describe to a colleague.
/// </para>
/// </remarks>
public sealed class DashboardController : Controller
{
    private readonly IDashboardRepository _dashboard;

    /// <summary>Creates the controller.</summary>
    public DashboardController(IDashboardRepository dashboard)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        _dashboard = dashboard;
    }

    /// <summary>Shows the dashboard.</summary>
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        DashboardSummary? summary;

        try
        {
            summary = await _dashboard.GetSummaryAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The landing page is where an administrator arrives after signing
            // in. It renders without its numbers rather than sending them to
            // the error page, which would make the portal look wholly broken
            // when one query failed.
            summary = null;
        }

        return View(new DashboardViewModel
        {
            DisplayName = User.FindFirst("displayName")?.Value
                ?? User.Identity?.Name
                ?? "administrator",
            Permissions = [.. User.FindAll(Permissions.ClaimType).Select(c => c.Value).Order(StringComparer.Ordinal)],
            Summary = summary,
        });
    }
}

/// <summary>What the dashboard shows.</summary>
public sealed class DashboardViewModel
{
    /// <summary>Who is signed in.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The permissions this administrator actually holds.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>The counts, or <see langword="null"/> when they could not be read.</summary>
    public DashboardSummary? Summary { get; init; }
}
