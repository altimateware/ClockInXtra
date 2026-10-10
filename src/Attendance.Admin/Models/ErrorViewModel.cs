namespace Attendance.Admin.Models;

/// <summary>
/// What the error page shows: a correlation identifier and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// There was a second type of this name in the <c>Controllers</c> namespace, a
/// leftover of the project template alongside this one. The view resolved this
/// one through <c>_ViewImports</c> while <c>HomeController</c> passed the other,
/// so rendering the error page threw:
/// </para>
/// <para>
/// <c>The model item passed into the ViewDataDictionary is of type
/// 'Attendance.Admin.Controllers.ErrorViewModel', but this ViewDataDictionary
/// instance requires a model item of type
/// 'Attendance.Admin.Models.ErrorViewModel'.</c>
/// </para>
/// <para>
/// The error page had therefore never worked, and because
/// <c>UseExceptionHandler</c> is registered only outside Development, no test
/// ever rendered it: a developer sees the developer exception page instead. The
/// duplicate is gone so the mismatch cannot return, and
/// <c>PortalSecurityTests</c> now requests the page directly, which works in any
/// environment because it is an ordinary action.
/// </para>
/// </remarks>
public sealed class ErrorViewModel
{
    /// <summary>
    /// Identifies the request in the log. It is the only thing the page shows
    /// about a failure: the detail stays server-side (Claude.md §34).
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>Whether there is an identifier worth printing.</summary>
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
