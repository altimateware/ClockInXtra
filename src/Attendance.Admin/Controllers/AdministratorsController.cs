using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using Attendance.Admin.Security;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OtpNet;

namespace Attendance.Admin.Controllers;

/// <summary>
/// Administrator accounts, their roles, and the credentials issued to them
/// (§17, §42).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the rules are.</b> Every change is refused by the database if the
/// acting administrator is changing their own account, is changing someone who
/// holds a permission they lack, or would leave nobody able to manage
/// administrators. This controller hides actions that would obviously be refused
/// and turns the refusal codes into sentences; it does not decide anything that
/// matters for security.
/// </para>
/// <para>
/// <b>Credentials are shown once.</b> Issued passwords and authenticator secrets
/// appear on a single no-store page and are recoverable from nowhere afterwards —
/// the database holds a hash and Data Protection ciphertext respectively.
/// </para>
/// </remarks>
[Authorize]
public sealed class AdministratorsController : Controller
{
    private readonly IAdministratorManagementRepository _management;
    private readonly IAdministratorRepository _administrators;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ISecretProtector _secretProtector;

    /// <summary>Creates the controller.</summary>
    public AdministratorsController(
        IAdministratorManagementRepository management,
        IAdministratorRepository administrators,
        IPasswordHasher passwordHasher,
        ISecretProtector secretProtector)
    {
        ArgumentNullException.ThrowIfNull(management);
        ArgumentNullException.ThrowIfNull(administrators);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(secretProtector);

        _management = management;
        _administrators = administrators;
        _passwordHasher = passwordHasher;
        _secretProtector = secretProtector;
    }

    /// <summary>Lists administrators.</summary>
    [HttpGet]
    [Authorize(Permissions.AdministratorView)]
    public async Task<IActionResult> Index(string? search, CancellationToken cancellationToken)
    {
        int actingId = CurrentAdministratorId();

        IReadOnlyList<AdministratorSummary> administrators = await _management.SearchAsync(search, cancellationToken);
        IReadOnlyList<RoleSummary> roles = await _management.GetRolesAsync(actingId, cancellationToken);

        return View(new AdministratorsViewModel
        {
            Search = search,
            CurrentAdministratorId = actingId,
            CanManage = User.HasClaim(Permissions.ClaimType, Permissions.AdministratorManage),
            Administrators = administrators,
            Roles = roles,
        });
    }

    /// <summary>Lists every role and exactly what it grants.</summary>
    [HttpGet]
    [Authorize(Permissions.AdministratorView)]
    public async Task<IActionResult> Roles(CancellationToken cancellationToken) =>
        View(await _management.GetRolesAsync(CurrentAdministratorId(), cancellationToken));

    /// <summary>Creates an administrator, issuing a password and an authenticator.</summary>
    /// <remarks>
    /// Two writes: the account, then its authenticator. If the second fails, the
    /// account exists but cannot sign in, and the page says so and offers the
    /// authenticator again — recoverable, because the creator is still signed in.
    /// </remarks>
    [HttpPost]
    [Authorize(Permissions.AdministratorManage)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Create(CreateAdministratorInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!ModelState.IsValid)
        {
            TempData["Error"] = string.Join(
                " ",
                ModelState.Values.SelectMany(state => state.Errors).Select(error => error.ErrorMessage));

            return RedirectToAction(nameof(Index));
        }

        int actingId = CurrentAdministratorId();
        string password = InitialPassword.Generate();
        string userName = input.UserName.Trim();
        string displayName = input.DisplayName.Trim();

        CreateAdministratorResult created = await _management.CreateAsync(
            new NewAdministrator(userName, displayName, input.Email),
            _passwordHasher.Hash(password),
            input.RoleId,
            actingId,
            Guid.NewGuid(),
            cancellationToken);

        if (created.ResultCode != AttendanceResultCode.Success)
        {
            TempData["Error"] = created.ResultCode switch
            {
                AttendanceResultCode.DuplicateName => "That user name is already in use.",
                AttendanceResultCode.Forbidden => "That role grants permissions you do not hold, so you cannot give it.",
                AttendanceResultCode.NotFound => "That role no longer exists, or your own account is no longer active.",
                _ => "The administrator could not be created.",
            };

            return RedirectToAction(nameof(Index));
        }

        (AttendanceResultCode enrolled, string? secret) =
            await EnrolAuthenticatorAsync(created.AdministratorId, replaceExisting: false, actingId, cancellationToken);

        return View("Issued", new IssuedCredentialsViewModel
        {
            Heading = $"Created {displayName}",
            AdministratorId = created.AdministratorId,
            UserName = userName,
            Password = password,
            SharedSecret = secret,
            OtpAuthUri = secret is null ? null : OtpAuthUri(userName, secret),
            Warning = enrolled == AttendanceResultCode.Success
                ? null
                : "The account was created, but its authenticator could not be enrolled. It cannot sign in until one is — use \"Issue a new authenticator\" on the administrators page.",
        });
    }

    /// <summary>Activates or deactivates an administrator.</summary>
    [HttpPost]
    [Authorize(Permissions.AdministratorManage)]
    public async Task<IActionResult> SetStatus(
        int administratorId,
        bool active,
        string? reason,
        CancellationToken cancellationToken)
    {
        AttendanceResultCode result = await _management.SetStatusAsync(
            administratorId, active, reason, CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        Report(
            result,
            active
                ? "Administrator activated."
                : "Administrator deactivated. Their open sessions end on their next click.",
            invalidRequest: "Give a reason: deactivating an administrator is recorded in the audit trail.");

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Grants or removes a role.</summary>
    [HttpPost]
    [Authorize(Permissions.AdministratorManage)]
    public async Task<IActionResult> SetRole(
        int administratorId,
        int roleId,
        bool grant,
        CancellationToken cancellationToken)
    {
        AttendanceResultCode result = await _management.SetRoleAsync(
            administratorId, roleId, grant, CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        Report(
            result,
            grant
                ? "Role granted. It applies to their open sessions immediately."
                : "Role removed. It stops applying to their open sessions immediately.",
            forbidden: grant
                ? "That role grants permissions you do not hold, or that administrator holds permissions you do not."
                : null);

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Issues another administrator a new password.</summary>
    [HttpPost]
    [Authorize(Permissions.AdministratorManage)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ResetPassword(
        int administratorId,
        string userName,
        CancellationToken cancellationToken)
    {
        string password = InitialPassword.Generate();

        AttendanceResultCode result = await _management.ResetPasswordAsync(
            administratorId, _passwordHasher.Hash(password), CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        if (result != AttendanceResultCode.Success)
        {
            Report(result, success: string.Empty);
            return RedirectToAction(nameof(Index));
        }

        return View("Issued", new IssuedCredentialsViewModel
        {
            Heading = $"New password for {userName}",
            AdministratorId = administratorId,
            UserName = userName,
            Password = password,
            Note = "Their open sessions have ended, any sign-in lockout has been cleared, and they must change this password when they next sign in. Their authenticator is unchanged.",
        });
    }

    /// <summary>Issues another administrator a new authenticator secret.</summary>
    [HttpPost]
    [Authorize(Permissions.AdministratorManage)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ResetAuthenticator(
        int administratorId,
        string userName,
        CancellationToken cancellationToken)
    {
        (AttendanceResultCode result, string? secret) = await EnrolAuthenticatorAsync(
            administratorId, replaceExisting: true, CurrentAdministratorId(), cancellationToken);

        if (result != AttendanceResultCode.Success || secret is null)
        {
            Report(result, success: string.Empty);
            return RedirectToAction(nameof(Index));
        }

        return View("Issued", new IssuedCredentialsViewModel
        {
            Heading = $"New authenticator for {userName}",
            AdministratorId = administratorId,
            UserName = userName,
            SharedSecret = secret,
            OtpAuthUri = OtpAuthUri(userName, secret),
            Note = "The previous authenticator no longer works. This one activates on the first code that succeeds at sign-in.",
        });
    }

    /// <summary>
    /// Generates, protects and stores a secret, returning the plaintext only on
    /// success so it can be shown once.
    /// </summary>
    private async Task<(AttendanceResultCode Result, string? Base32Secret)> EnrolAuthenticatorAsync(
        int administratorId,
        bool replaceExisting,
        int actingId,
        CancellationToken cancellationToken)
    {
        // 160 bits: the RFC 4226 §4 recommendation, and what authenticator apps expect.
        byte[] secret = KeyGeneration.GenerateRandomKey(20);

        try
        {
            AttendanceResultCode result = await _administrators.EnrolMfaAsync(
                administratorId,
                _secretProtector.Protect(SecretPurposes.AdministratorTotpSecret, secret),
                replaceExisting,
                actingId,
                Guid.NewGuid(),
                cancellationToken);

            return result == AttendanceResultCode.Success
                ? (result, Base32Encoding.ToString(secret).TrimEnd('='))
                : (result, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static string OtpAuthUri(string userName, string base32Secret) =>
        $"otpauth://totp/ClockInXtra:{Uri.EscapeDataString(userName)}" +
        $"?secret={base32Secret}&issuer=ClockInXtra&algorithm=SHA1&digits=6&period=30";

    /// <summary>Turns a result code into the message the next page shows.</summary>
    private void Report(
        AttendanceResultCode result,
        string success,
        string? invalidRequest = null,
        string? forbidden = null)
    {
        if (result == AttendanceResultCode.Success)
        {
            if (success.Length > 0)
            {
                TempData["Message"] = success;
            }

            return;
        }

        TempData["Error"] = result switch
        {
            AttendanceResultCode.SeparationOfDutiesViolation =>
                "You cannot do that to your own account. Ask another administrator.",
            AttendanceResultCode.Forbidden =>
                forbidden ?? "That administrator holds permissions you do not, so you cannot change their account.",
            AttendanceResultCode.LastAdministratorManager =>
                "That would leave nobody able to manage administrators, so it was refused.",
            AttendanceResultCode.NotFound =>
                "That administrator or role no longer exists, or your own account is no longer active.",
            AttendanceResultCode.UserInactive =>
                "That administrator is deactivated. Activate them first.",
            AttendanceResultCode.InvalidRequest =>
                invalidRequest ?? "The request was not valid.",
            _ => "The change could not be made.",
        };
    }

    private int CurrentAdministratorId() =>
        int.TryParse(
            User.FindFirst(AdministratorClaims.AdministratorId)?.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int id)
            ? id
            : throw new InvalidOperationException("The signed-in principal carries no administrator identifier.");
}

/// <summary>The administrators page.</summary>
public sealed class AdministratorsViewModel
{
    /// <summary>The search term, echoed into the form.</summary>
    public string? Search { get; init; }

    /// <summary>The viewer, so their own row can offer nothing.</summary>
    public int CurrentAdministratorId { get; init; }

    /// <summary>Whether the viewer holds Administrator.Manage.</summary>
    public bool CanManage { get; init; }

    /// <summary>The listed administrators.</summary>
    public IReadOnlyList<AdministratorSummary> Administrators { get; init; } = [];

    /// <summary>Every role, with whether the viewer could grant it.</summary>
    public IReadOnlyList<RoleSummary> Roles { get; init; } = [];
}

/// <summary>The details an administrator types to create another.</summary>
/// <remarks>
/// The user name is restricted to letters, digits and <c>. _ @ -</c>. It is typed
/// at every sign-in and appears in the audit trail, where a space, a look-alike
/// Unicode character or a control character would make two accounts hard to tell
/// apart (§44).
/// </remarks>
public sealed class CreateAdministratorInput
{
    /// <summary>The name they will sign in with.</summary>
    [Required(ErrorMessage = "A user name is required.")]
    [StringLength(64, MinimumLength = 3, ErrorMessage = "A user name is 3 to 64 characters.")]
    [RegularExpression("^[A-Za-z0-9._@-]+$", ErrorMessage = "A user name may contain only letters, digits and . _ @ -")]
    public string UserName { get; init; } = string.Empty;

    /// <summary>Shown in the portal and the audit trail.</summary>
    [Required(ErrorMessage = "A display name is required.")]
    [StringLength(160, MinimumLength = 1)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Contact address, where recorded.</summary>
    [EmailAddress(ErrorMessage = "That email address is not valid.")]
    [StringLength(256)]
    public string? Email { get; init; }

    /// <summary>An initial role, or none.</summary>
    public int? RoleId { get; init; }
}

/// <summary>A page showing credentials exactly once.</summary>
public sealed class IssuedCredentialsViewModel
{
    /// <summary>The page heading.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>The account the credentials belong to.</summary>
    public int AdministratorId { get; init; }

    /// <summary>Their user name.</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>An issued password, when one was issued.</summary>
    public string? Password { get; init; }

    /// <summary>An authenticator secret in Base32, when one was issued.</summary>
    public string? SharedSecret { get; init; }

    /// <summary>The Key URI for the secret.</summary>
    public string? OtpAuthUri { get; init; }

    /// <summary>Something that went partly wrong.</summary>
    public string? Warning { get; init; }

    /// <summary>What happened as a consequence.</summary>
    public string? Note { get; init; }
}
