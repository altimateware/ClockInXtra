using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Attendance.Admin.Security;
using Attendance.Application.Abstractions;
using Attendance.Application.Services;
using Attendance.Domain.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Admin.Controllers;

/// <summary>
/// Administrator sign-in, sign-out and password change (§41).
/// </summary>
public sealed class AccountController : Controller
{
    private readonly AdministratorAuthenticator _authenticator;
    private readonly AdministratorPasswordChanger _passwordChanger;
    private readonly IAdministratorRepository _administrators;
    private readonly IAdministratorPolicyProvider _policy;

    /// <summary>Creates the controller.</summary>
    public AccountController(
        AdministratorAuthenticator authenticator,
        AdministratorPasswordChanger passwordChanger,
        IAdministratorRepository administrators,
        IAdministratorPolicyProvider policy)
    {
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(passwordChanger);
        ArgumentNullException.ThrowIfNull(administrators);
        ArgumentNullException.ThrowIfNull(policy);

        _authenticator = authenticator;
        _passwordChanger = passwordChanger;
        _administrators = administrators;
        _policy = policy;
    }

    /// <summary>Shows the sign-in form.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Login(string? returnUrl, CancellationToken cancellationToken) =>
        View(new LoginViewModel
        {
            ReturnUrl = returnUrl,
            RequireAuthenticatorCode = await RequiresMfaAsync(cancellationToken),
        });

    /// <summary>Attempts a sign-in.</summary>
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Carried on every path that re-renders the form, or a failed attempt
        // would redraw it with the field back.
        model.RequireAuthenticatorCode = await RequiresMfaAsync(cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        AdministratorSignInResult result = await _authenticator.SignInAsync(
            new AdministratorSignInRequest(
                model.UserName,
                model.Password,
                model.AuthenticatorCode,
                Guid.NewGuid(),
                SourceAddressHash: null),
            cancellationToken);

        if (!result.IsAuthenticated || result.Administrator is not { } administrator)
        {
            // One message for every failure. Distinguishing "no such account"
            // from "wrong password" would let an insider enumerate administrator
            // accounts, and they are the attacker this portal most has to worry
            // about (CON-09 applied to the portal).
            ModelState.AddModelError(
                string.Empty,
                result.ResultCode == AttendanceResultCode.AccountLocked
                    ? "This account is temporarily locked. Please try again later."
                    : "The details entered were not correct.");

            return View(model);
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            AdministratorClaims.CreatePrincipal(administrator, CookieAuthenticationDefaults.AuthenticationScheme),
            new AuthenticationProperties { IsPersistent = false });

        // Only local redirects. An open redirect here would let a phishing link
        // bounce a signed-in administrator to an attacker's page.
        return Url.IsLocalUrl(model.ReturnUrl)
            ? Redirect(model.ReturnUrl!)
            : RedirectToAction("Index", "Dashboard");
    }

    /// <summary>Signs out, and ends every other session of this administrator.</summary>
    /// <remarks>
    /// The sign-out is recorded and the security stamp rotated before the cookie
    /// is removed, so a copy of this cookie held anywhere else stops working on
    /// its next request instead of at expiry. The browser's own cookie is removed
    /// even if recording fails: an administrator who asked to sign out is signed
    /// out here regardless, and the failure goes to the log.
    /// </remarks>
    [HttpPost]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (TryReadSession(out int administratorId, out _, out _))
        {
            try
            {
                await _administrators.RecordLogoutAsync(administratorId, Guid.NewGuid(), cancellationToken);
            }
            finally
            {
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        }
        else
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        return RedirectToAction(nameof(Login));
    }

    /// <summary>Shown when an administrator lacks the required permission.</summary>
    [HttpGet]
    public IActionResult Denied() => View();

    /// <summary>Shows the change-password form.</summary>
    [HttpGet]
    [Authorize]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult ChangePassword() =>
        View(new ChangePasswordViewModel { IsRequired = MustChangePassword() });

    /// <summary>Changes the signed-in administrator's own password.</summary>
    /// <remarks>
    /// <para>
    /// Identity comes from the session, never the form: the administrator id, user
    /// name and security stamp are read from the cookie's claims, so this action
    /// cannot be pointed at another account.
    /// </para>
    /// <para>
    /// <b>The caller's own session survives; every other one ends.</b> The change
    /// rotates the security stamp, which the cookie validator compares on every
    /// request. Re-issuing this cookie with the new stamp keeps the person who just
    /// proved their password signed in, while any other browser holding the old
    /// stamp — including one an attacker might be using — is rejected on its next
    /// request.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Authorize]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!ModelState.IsValid)
        {
            return View(BlankForm());
        }

        if (!TryReadSession(out int administratorId, out string userName, out Guid stamp))
        {
            return await EndSessionAsync();
        }

        PasswordChangeOutcome outcome = await _passwordChanger.ChangeAsync(
            new PasswordChangeRequest(
                administratorId,
                userName,
                stamp,
                model.CurrentPassword,
                model.NewPassword,
                Guid.NewGuid(),
                SourceAddressHash: null),
            cancellationToken);

        if (outcome.IsSuccess)
        {
            if (await _administrators.GetForAuthenticationAsync(userName, cancellationToken) is not { } refreshed)
            {
                return await EndSessionAsync();
            }

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                AdministratorClaims.CreatePrincipal(refreshed, CookieAuthenticationDefaults.AuthenticationScheme),
                new AuthenticationProperties { IsPersistent = false });

            TempData["Message"] = "Your password has been changed. Any other signed-in sessions have been ended.";
            return RedirectToAction("Index", "Dashboard");
        }

        switch (outcome.ResultCode)
        {
            // The session no longer matches the account: disabled, re-roled, or
            // the password already changed elsewhere. Not something to retry.
            case AttendanceResultCode.ConcurrencyConflict:
            case AttendanceResultCode.Unauthorized:
            case AttendanceResultCode.UserInactive:
                return await EndSessionAsync();

            case AttendanceResultCode.AccountLocked:
                ModelState.AddModelError(string.Empty, "Too many incorrect attempts. This account is temporarily locked.");
                break;

            case AttendanceResultCode.InvalidCredentials:
                ModelState.AddModelError(nameof(ChangePasswordViewModel.CurrentPassword), "Your current password was not correct.");
                break;

            case AttendanceResultCode.InvalidRequest:
                ModelState.AddModelError(nameof(ChangePasswordViewModel.NewPassword), Describe(outcome.Violation));
                break;

            default:
                ModelState.AddModelError(string.Empty, "The password could not be changed. Please try again.");
                break;
        }

        return View(BlankForm());
    }

    /// <summary>A sentence for each policy rule, shown only after the current password was proven.</summary>
    private static string Describe(PasswordPolicyViolation violation) => violation switch
    {
        PasswordPolicyViolation.Blank => "Enter a new password.",
        PasswordPolicyViolation.TooShort => string.Format(
            CultureInfo.InvariantCulture,
            "Use at least {0} characters. A few ordinary words with spaces between them is fine.",
            AdministratorPasswordPolicy.MinimumLength),
        PasswordPolicyViolation.TooLong => string.Format(
            CultureInfo.InvariantCulture,
            "Use no more than {0} characters.",
            AdministratorPasswordPolicy.MaximumLength),
        PasswordPolicyViolation.SameAsCurrent => "The new password must be different from the current one.",
        PasswordPolicyViolation.ContainsUserName => "The new password must not contain your user name.",
        _ => "That password cannot be used.",
    };

    /// <summary>
    /// The form with nothing typed into it. A password is never rendered back into
    /// a page, even the one it was typed on (§24).
    /// </summary>
    private ChangePasswordViewModel BlankForm() => new() { IsRequired = MustChangePassword() };

    private bool MustChangePassword() =>
        string.Equals(User.FindFirst(AdministratorClaims.MustChangePassword)?.Value, "true", StringComparison.Ordinal);

    private bool TryReadSession(out int administratorId, out string userName, out Guid stamp)
    {
        userName = User.Identity?.Name ?? string.Empty;
        stamp = Guid.Empty;
        administratorId = 0;

        return userName.Length > 0
            && int.TryParse(
                User.FindFirst(AdministratorClaims.AdministratorId)?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out administratorId)
            && Guid.TryParse(User.FindFirst(AdministratorClaims.SecurityStamp)?.Value, out stamp);
    }

    private async Task<IActionResult> EndSessionAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    /// <summary>Whether this deployment asks administrators for a code.</summary>
    /// <remarks>
    /// <para>
    /// Only ever used to decide whether to <em>draw</em> the field. The setting
    /// is read again, server-side, by
    /// <see cref="AdministratorAuthenticator"/> when the form is submitted, so a
    /// caller who posts a code to a deployment that does not want one — or omits
    /// one from a deployment that does — is judged by the policy and not by what
    /// the page happened to render.
    /// </para>
    /// <para>
    /// A failure to read it is treated as "required". Drawing a field nobody
    /// needs is a confusing form; hiding one that is needed is a sign-in page
    /// that cannot be used, and on a dead database neither works anyway.
    /// </para>
    /// </remarks>
    private async Task<bool> RequiresMfaAsync(CancellationToken cancellationToken)
    {
        try
        {
            AdministratorPolicy policy = await _policy.GetAsync(cancellationToken);

            return policy.RequireMfa;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }
    }
}

/// <summary>The sign-in form.</summary>
public sealed class LoginViewModel
{
    /// <summary>The administrator's user name.</summary>
    [Required(ErrorMessage = "Enter your user name.")]
    [StringLength(64, MinimumLength = 1)]
    [Display(Name = "User name")]
    public string UserName { get; init; } = string.Empty;

    /// <summary>The password. Never rendered back into the form.</summary>
    [Required(ErrorMessage = "Enter your password.")]
    [StringLength(256, MinimumLength = 1)]
    [DataType(DataType.Password)]
    public string Password { get; init; } = string.Empty;

    /// <summary>The six-digit authenticator code, where one is required.</summary>
    [RegularExpression("^[0-9]{6}$", ErrorMessage = "The code is six digits.")]
    [Display(Name = "Authenticator code")]
    public string? AuthenticatorCode { get; init; }

    /// <summary>Where to go after signing in. Only local paths are honoured.</summary>
    public string? ReturnUrl { get; init; }

    /// <summary>
    /// Whether to draw the authenticator field.
    /// </summary>
    /// <remarks>
    /// Presentation only, and never trusted on the way in: it is assigned by the
    /// controller from the policy on every render, so a posted value is
    /// overwritten before it is read. Whether a code is actually required is
    /// decided server-side when the form is submitted.
    /// </remarks>
    public bool RequireAuthenticatorCode { get; set; } = true;
}

/// <summary>The change-password form.</summary>
/// <remarks>
/// Length rules are deliberately not repeated here as validation attributes. The
/// policy lives in one place, <see cref="AdministratorPasswordPolicy"/>, and is
/// applied only after the current password has been proven.
/// </remarks>
public sealed class ChangePasswordViewModel
{
    /// <summary>The password being replaced.</summary>
    [Required(ErrorMessage = "Enter your current password.")]
    [DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string? CurrentPassword { get; init; }

    /// <summary>The new password.</summary>
    [Required(ErrorMessage = "Enter a new password.")]
    [DataType(DataType.Password)]
    [Display(Name = "New password")]
    public string? NewPassword { get; init; }

    /// <summary>The new password again, to catch a typing mistake.</summary>
    [Required(ErrorMessage = "Enter the new password again.")]
    [Compare(nameof(NewPassword), ErrorMessage = "The two new passwords do not match.")]
    [DataType(DataType.Password)]
    [Display(Name = "New password again")]
    public string? ConfirmPassword { get; init; }

    /// <summary>Whether the change is required before anything else can be done.</summary>
    public bool IsRequired { get; init; }
}
