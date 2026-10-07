using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Admin.Controllers;

/// <summary>
/// The error page.
/// </summary>
/// <remarks>
/// Anonymous by necessity: an unhandled failure may well be a failure to
/// authenticate, and an error page that itself requires authentication produces a
/// redirect loop. It shows nothing about the failure — the detail is in the log,
/// against the request identifier (§34).
/// </remarks>
public sealed class HomeController : Controller
{
    /// <summary>Shows the error page.</summary>
    [HttpGet]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Error() =>
        View(new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
        });
}

/// <summary>What the error page shows.</summary>
public sealed class ErrorViewModel
{
    /// <summary>The request identifier, for support to correlate with the log.</summary>
    public string? RequestId { get; init; }

    /// <summary>Whether there is an identifier worth showing.</summary>
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
