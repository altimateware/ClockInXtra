using Attendance.Application.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Attendance.Admin.Security;

/// <summary>
/// Revalidates the signed-in administrator on every request.
/// </summary>
/// <remarks>
/// <para>
/// An authentication cookie is a bearer token with an expiry: once issued, it
/// keeps working until it lapses, no matter what happens to the account behind
/// it. That is unacceptable for a portal that can revoke devices and rewrite
/// attendance rules — disabling an administrator has to take effect now, not at
/// the end of their session.
/// </para>
/// <para>
/// So the stamp stored against the account is compared with the one stamped into
/// the cookie. Any change to the password, the status or the role assignments
/// rotates the stored stamp, the comparison fails, and the principal is rejected
/// (threat TH-35). The cost is one indexed read per request, which is the same
/// read the procedure was built for.
/// </para>
/// <para>
/// Permissions are refreshed from the same read, so a revoked permission stops
/// applying immediately rather than at next sign-in.
/// </para>
/// </remarks>
public sealed class SecurityStampCookieValidator
{
    private readonly IAdministratorRepository _administrators;
    private readonly ILogger<SecurityStampCookieValidator> _logger;

    /// <summary>Creates the validator.</summary>
    public SecurityStampCookieValidator(
        IAdministratorRepository administrators,
        ILogger<SecurityStampCookieValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(administrators);
        ArgumentNullException.ThrowIfNull(logger);

        _administrators = administrators;
        _logger = logger;
    }

    /// <summary>Validates the principal carried by the cookie.</summary>
    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? userName = context.Principal?.Identity?.Name;
        string? stamp = context.Principal?.FindFirst(AdministratorClaims.SecurityStamp)?.Value;

        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(stamp))
        {
            await RejectAsync(context, "CLAIMS_MISSING").ConfigureAwait(false);
            return;
        }

        AdministratorRecord? found = await _administrators
            .GetForAuthenticationAsync(userName, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (found is not { } administrator)
        {
            await RejectAsync(context, "ADMINISTRATOR_REMOVED").ConfigureAwait(false);
            return;
        }

        if (!administrator.IsActive)
        {
            await RejectAsync(context, "ADMINISTRATOR_INACTIVE").ConfigureAwait(false);
            return;
        }

        if (!string.Equals(administrator.SecurityStamp.ToString(), stamp, StringComparison.Ordinal))
        {
            await RejectAsync(context, "SECURITY_STAMP_ROTATED").ConfigureAwait(false);
            return;
        }

        // Refresh the principal so a permission granted or withdrawn a moment ago
        // applies to this request, not to the next sign-in.
        context.ReplacePrincipal(
            AdministratorClaims.CreatePrincipal(administrator, CookieAuthenticationDefaults.AuthenticationScheme));

        context.ShouldRenew = true;
    }

    private async Task RejectAsync(CookieValidatePrincipalContext context, string reason)
    {
        _logger.SessionRejected(reason);

        context.RejectPrincipal();

        await context.HttpContext
            .SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
            .ConfigureAwait(false);
    }
}

/// <summary>Source-generated log messages for session validation.</summary>
internal static partial class SecurityStampLog
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Administrator session rejected: {Reason}.")]
    public static partial void SessionRejected(this ILogger logger, string reason);
}
