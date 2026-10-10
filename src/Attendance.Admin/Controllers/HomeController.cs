using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Attendance.Admin.Models;
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
