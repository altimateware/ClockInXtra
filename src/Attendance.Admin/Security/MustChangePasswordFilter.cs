using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Attendance.Admin.Security;

/// <summary>
/// Sends an administrator who still owes a password change to the change page,
/// whatever they asked for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why enforce it rather than display it.</b> A password issued at setup, at
/// account creation or by a reset has been seen by someone other than its owner.
/// The dashboard used to say it "must be changed before you continue" while
/// letting the administrator continue anyway — a banner is not a control, and
/// the issued password stayed in use for as long as nobody felt like acting on
/// it.
/// </para>
/// <para>
/// The claim is refreshed from the database on every request by
/// <see cref="SecurityStampCookieValidator"/>, so this reads current state: the
/// moment the password is changed the gate opens, and the moment a colleague
/// resets it the gate closes.
/// </para>
/// <para>
/// Allowed through: changing the password, signing out, the access-denied page,
/// and anything marked <see cref="AllowAnonymousAttribute"/> (the sign-in page).
/// A POST to any other action is redirected too. Model binding has already run by
/// the time an action filter executes, but binding has no side effects; setting
/// the result here means the action method itself never runs.
/// </para>
/// </remarks>
public sealed class MustChangePasswordFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> AllowedAccountActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ChangePassword",
        "Logout",
        "Denied",
    };

    /// <inheritdoc />
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        bool mustChange = context.HttpContext.User.Identity?.IsAuthenticated == true
            && string.Equals(
                context.HttpContext.User.FindFirst(AdministratorClaims.MustChangePassword)?.Value,
                "true",
                StringComparison.Ordinal);

        if (!mustChange || IsExempt(context))
        {
            return next();
        }

        context.Result = new RedirectToActionResult("ChangePassword", "Account", routeValues: null);
        return Task.CompletedTask;
    }

    private static bool IsExempt(ActionExecutingContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            return true;
        }

        string? controller = context.RouteData.Values["controller"] as string;
        string? action = context.RouteData.Values["action"] as string;

        return string.Equals(controller, "Account", StringComparison.OrdinalIgnoreCase)
            && action is not null
            && AllowedAccountActions.Contains(action);
    }
}
