using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Attendance.Admin.Security;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OtpNet;

namespace Attendance.Admin.Controllers;

/// <summary>
/// Employee readiness and authenticator enrolment (§13, §17).
/// </summary>
/// <remarks>
/// <para>
/// An employee can clock in only with all three of a password, an active
/// authenticator and an approved device. Missing any one of them means being
/// refused at a door, so the list leads with who is <b>not</b> ready rather than
/// with everyone.
/// </para>
/// </remarks>
[Authorize]
public sealed class EmployeesController : Controller
{
    private readonly IMobileUserAdministrationRepository _employees;
    private readonly IMfaEnrolmentRepository _enrolment;
    private readonly ISecretProtector _secretProtector;
    private readonly ITotpVerifier _totpVerifier;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IReferenceListRepository _lists;
    private readonly IAdministratorPolicyProvider _policy;

    /// <summary>Creates the controller.</summary>
    public EmployeesController(
        IMobileUserAdministrationRepository employees,
        IMfaEnrolmentRepository enrolment,
        ISecretProtector secretProtector,
        ITotpVerifier totpVerifier,
        IPasswordHasher passwordHasher,
        IReferenceListRepository lists,
        IAdministratorPolicyProvider policy)
    {
        ArgumentNullException.ThrowIfNull(lists);
        _lists = lists;

        ArgumentNullException.ThrowIfNull(employees);
        ArgumentNullException.ThrowIfNull(enrolment);
        ArgumentNullException.ThrowIfNull(secretProtector);
        ArgumentNullException.ThrowIfNull(totpVerifier);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(policy);

        _employees = employees;
        _enrolment = enrolment;
        _secretProtector = secretProtector;
        _totpVerifier = totpVerifier;
        _passwordHasher = passwordHasher;
        _policy = policy;
    }

    /// <summary>Lists employees, those who cannot clock in first.</summary>
    [HttpGet]
    [Authorize(Permissions.MobileUserView)]
    public async Task<IActionResult> Index(
        string? search,
        bool onlyNotReady = true,
        CancellationToken cancellationToken = default)
    {
        return await IndexViewAsync(search, onlyNotReady, draft: null, cancellationToken);
    }

    /// <summary>
    /// The list page, with the choices its form offers, and — after a refused
    /// create — the values that were typed, so nobody has to type eight
    /// required fields twice.
    /// </summary>
    private async Task<ViewResult> IndexViewAsync(
        string? search,
        bool onlyNotReady,
        CreateEmployeeInput? draft,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MobileUserSummary> employees =
            await _employees.SearchAsync(search, onlyNotReady, cancellationToken);

        ViewData["Search"] = search;
        ViewData["OnlyNotReady"] = onlyNotReady;
        ViewData["Draft"] = draft;
        await LoadChoicesAsync(cancellationToken);

        return View(nameof(Index), employees);
    }

    /// <summary>
    /// Department and job title choices: active entries only (DEC-11). An
    /// employee who already holds a deactivated entry keeps it, and the edit
    /// form adds it back for them alone.
    /// </summary>
    private async Task LoadChoicesAsync(CancellationToken cancellationToken)
    {
        ViewData["Departments"] = (await _lists.GetAllAsync(ReferenceList.Department, cancellationToken))
            .Where(entry => entry.IsActive).Select(entry => entry.Name).ToList();
        ViewData["JobTitles"] = (await _lists.GetAllAsync(ReferenceList.JobTitle, cancellationToken))
            .Where(entry => entry.IsActive).Select(entry => entry.Name).ToList();
    }

    /// <summary>
    /// Creates an employee and shows their initial password once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The password is generated, not typed.</b> See
    /// <see cref="InitialPassword"/> for why. It is displayed on the page that
    /// follows and is recoverable from nowhere: the database holds only its
    /// PBKDF2 hash (§16, §24). If it is lost before it reaches the employee, the
    /// answer is a new one, not a lookup.
    /// </para>
    /// <para>
    /// The response is marked no-store, for the same reason the authenticator
    /// page is: a page carrying a credential must not sit in a browser's
    /// back-forward cache or an intermediate proxy.
    /// </para>
    /// <para>
    /// The new employee <b>cannot clock in yet</b> — an authenticator has to be
    /// enrolled and a device approved. The page says so, because an account that
    /// looks finished but is refused at a door the next morning is the outcome
    /// this whole screen exists to prevent.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Authorize(Permissions.MobileUserManage)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Create(
        CreateEmployeeInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!ModelState.IsValid)
        {
            ViewData["Error"] = ValidationMessages();
            return await IndexViewAsync(null, onlyNotReady: true, input, cancellationToken);
        }

        string password = InitialPassword.Generate();

        CreateEmployeeResult result = await _employees.CreateAsync(
            new NewEmployee(
                input.UserId.Trim(),
                input.EmployeeNumber,
                input.FirstName.Trim(),
                input.LastName.Trim(),
                input.Email,
                input.PhoneNumber,
                input.Department,
                input.JobTitle),
            _passwordHasher.Hash(password),
            CurrentAdministratorId(),
            Guid.NewGuid(),
            cancellationToken);

        if (result.ResultCode != AttendanceResultCode.Success)
        {
            ViewData["Error"] = result.ResultCode switch
            {
                AttendanceResultCode.DuplicateName =>
                    "That user identifier or employee number is already in use.",
                AttendanceResultCode.InvalidRequest =>
                    "Check the details: every field is required, and the department and job title must be chosen from their lists.",
                _ => "The employee could not be created.",
            };

            return await IndexViewAsync(null, onlyNotReady: true, input, cancellationToken);
        }

        return View("Created", new CreatedEmployeeViewModel
        {
            MobileUserId = result.MobileUserId,
            UserId = input.UserId.Trim(),
            FullName = $"{input.FirstName.Trim()} {input.LastName.Trim()}",
            InitialPassword = password,
        });
    }

    /// <summary>The form to edit an employee's details.</summary>
    [HttpGet]
    [Authorize(Permissions.MobileUserView)]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        EmployeeDetail? employee = await _employees.GetEmployeeAsync(id, cancellationToken);

        if (employee is not { } found)
        {
            return NotFound();
        }

        await LoadChoicesAsync(cancellationToken);
        return View(EmployeeEditViewModel.From(found));
    }

    /// <summary>Saves an employee's details.</summary>
    /// <remarks>
    /// Every field is required (DEC-11). A record created before that rule may
    /// arrive here incomplete; saving it is how it gets completed.
    /// </remarks>
    [HttpPost]
    [Authorize(Permissions.MobileUserManage)]
    public async Task<IActionResult> Edit(
        int id,
        EmployeeDetailsInput input,
        string rowVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        EmployeeDetail? current = await _employees.GetEmployeeAsync(id, cancellationToken);

        if (current is not { } employee)
        {
            return NotFound();
        }

        byte[] token;

        try
        {
            token = Convert.FromBase64String(rowVersion ?? string.Empty);
        }
        catch (FormatException)
        {
            token = [];
        }

        if (!ModelState.IsValid || token.Length != 8)
        {
            ViewData["Error"] = token.Length != 8
                ? "That request was not valid. Please reload and try again."
                : ValidationMessages();

            await LoadChoicesAsync(cancellationToken);
            return View(EmployeeEditViewModel.From(employee, input, token));
        }

        AttendanceResultCode result = await _employees.UpdateEmployeeAsync(
            id,
            new NewEmployee(
                employee.UserId,
                input.EmployeeNumber,
                input.FirstName,
                input.LastName,
                input.Email,
                input.PhoneNumber,
                input.Department,
                input.JobTitle),
            token,
            CurrentAdministratorId(),
            Guid.NewGuid(),
            cancellationToken);

        if (result != AttendanceResultCode.Success)
        {
            ViewData["Error"] = result switch
            {
                AttendanceResultCode.DuplicateName => "That employee number is already in use by someone else.",
                AttendanceResultCode.ConcurrencyConflict =>
                    "Somebody else changed this employee while the form was open. Reload to see their changes.",
                AttendanceResultCode.InvalidRequest =>
                    "Check the details: every field is required, and the department and job title must be chosen from their lists.",
                _ => "The employee could not be saved.",
            };

            await LoadChoicesAsync(cancellationToken);
            return View(EmployeeEditViewModel.From(employee, input, token));
        }

        TempData["Message"] = $"{input.FirstName.Trim()} {input.LastName.Trim()} saved.";
        return RedirectToAction(nameof(Index), new { onlyNotReady = false, search = employee.UserId });
    }

    private string ValidationMessages() =>
        string.Join(
            " ",
            ModelState.Values
                .SelectMany(state => state.Errors)
                .Select(error => error.ErrorMessage)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal));

    /// <summary>
    /// Creates a pending authenticator enrolment and shows the secret once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The secret is displayed exactly once and never stored in the clear.</b>
    /// It is generated here, protected immediately, and the plaintext lives only
    /// long enough to render this page. There is no way to see it again: if the
    /// employee does not scan it, enrolment is repeated and a fresh secret
    /// issued.
    /// </para>
    /// <para>
    /// The response is marked no-store. A page carrying an authenticator secret
    /// must not sit in a browser's back-forward cache or an intermediate proxy.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Authorize(Permissions.MfaEnrol)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Enrol(
        int mobileUserId,
        string userId,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        byte[] secret = KeyGeneration.GenerateRandomKey(20);

        AttendanceResultCode result = await _enrolment.EnrolAsync(
            mobileUserId,
            _secretProtector.Protect(SecretPurposes.MobileUserTotpSecret, secret),
            replaceExisting,
            CurrentAdministratorId(),
            Guid.NewGuid(),
            cancellationToken);

        if (result != AttendanceResultCode.Success)
        {
            TempData["Error"] = result switch
            {
                AttendanceResultCode.MfaAlreadyEnrolled =>
                    "That employee already has an authenticator. Replacing it revokes the existing one — tick the replace box if that is what you intend.",
                AttendanceResultCode.UserInactive => "That employee is not active.",
                AttendanceResultCode.NotFound => "That employee no longer exists.",
                _ => "The authenticator could not be enrolled.",
            };

            return RedirectToAction(nameof(Index));
        }

        string base32 = Base32Encoding.ToString(secret).TrimEnd('=');

        return View("Enrolled", new EnrolmentViewModel
        {
            MobileUserId = mobileUserId,
            UserId = userId,
            SharedSecret = base32,

            // The Key URI Format, which authenticator applications read directly.
            OtpAuthUri =
                $"otpauth://totp/ClockInXtra:{Uri.EscapeDataString(userId)}" +
                $"?secret={base32}&issuer=ClockInXtra&algorithm=SHA1&digits=6&period=30",
        });
    }

    /// <summary>
    /// Activates the enrolment once the employee produces a working code.
    /// </summary>
    /// <remarks>
    /// Enrolment and activation are separate on purpose. Activating on creation
    /// would leave an employee unable to clock in whenever the secret was never
    /// successfully scanned — and that failure would surface the next morning,
    /// at a door, rather than here while somebody can still help.
    /// </remarks>
    [HttpPost]
    [Authorize(Permissions.MfaEnrol)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Activate(
        int mobileUserId,
        string code,
        CancellationToken cancellationToken)
    {
        PendingEnrolment? pending = await _enrolment.GetForActivationAsync(mobileUserId, cancellationToken);

        if (pending is not { } enrolment)
        {
            TempData["Error"] = "There is no enrolment waiting to be activated for that employee.";
            return RedirectToAction(nameof(Index));
        }

        if (!_secretProtector.TryUnprotect(
                SecretPurposes.MobileUserTotpSecret, enrolment.SecretProtected, out byte[] secret))
        {
            // An unreadable key ring is an operational failure, not a wrong code.
            TempData["Error"] = "The stored secret could not be read. Check the Data Protection key ring before retrying.";
            return RedirectToAction(nameof(Index));
        }

        AdministratorPolicy policy = await _policy.GetAsync(HttpContext.RequestAborted);

        TotpVerificationResult verification;

        try
        {
            verification = _totpVerifier.Verify(
                secret, code ?? string.Empty, enrolment.Parameters, policy.TotpStepTolerance);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        }

        if (!verification.IsValid)
        {
            TempData["Error"] = "That code was not accepted. Check the phone's clock and try the next code.";
            return RedirectToAction(nameof(Index));
        }

        AttendanceResultCode result = await _enrolment.ActivateAsync(
            mobileUserId, verification.MatchedTimeStep, CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        TempData[result == AttendanceResultCode.Success ? "Message" : "Error"] =
            result == AttendanceResultCode.Success
                ? "Authenticator activated. The employee still needs an approved device before they can clock in."
                : "The authenticator could not be activated.";

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Revokes an employee's authenticator.</summary>
    [HttpPost]
    [Authorize(Permissions.MfaReset)]
    public async Task<IActionResult> RevokeMfa(
        int mobileUserId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "Give a reason: revoking an authenticator stops the employee clocking in.";
            return RedirectToAction(nameof(Index));
        }

        AttendanceResultCode result = await _enrolment.RevokeAsync(
            mobileUserId, reason.Trim(), CurrentAdministratorId(), Guid.NewGuid(), cancellationToken);

        TempData[result == AttendanceResultCode.Success ? "Message" : "Error"] =
            result == AttendanceResultCode.Success
                ? "Authenticator revoked. The employee cannot clock in until a new one is enrolled and activated."
                : "The authenticator could not be revoked.";

        return RedirectToAction(nameof(Index));
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

/// <summary>
/// The employee details an administrator types.
/// </summary>
/// <remarks>
/// Lengths mirror the columns exactly, so an over-long value is refused at the
/// boundary with a sentence rather than by the database with a truncation (§44).
/// Only the identifier and the two name parts are required: §17 is explicit that
/// organisational fields must not be invented as mandatory.
/// </remarks>
public sealed class CreateEmployeeInput : EmployeeDetailsInput
{
    /// <summary>The identifier the employee signs in with.</summary>
    [Required(ErrorMessage = "A user identifier is required.")]
    [StringLength(64, MinimumLength = 1)]
    public string UserId { get; init; } = string.Empty;
}

/// <summary>An employee's details: every one required (DEC-11).</summary>
/// <remarks>
/// The database checks all of these again, and that the department and job
/// title are active list entries; these attributes exist so that a missing
/// field is named on the form rather than refused as a whole.
/// </remarks>
public class EmployeeDetailsInput
{
    /// <summary>Payroll or HR number.</summary>
    [Required(ErrorMessage = "An employee number is required.")]
    [StringLength(32, MinimumLength = 1)]
    public string? EmployeeNumber { get; init; }

    /// <summary>Given name.</summary>
    [Required(ErrorMessage = "A first name is required.")]
    [StringLength(80, MinimumLength = 1)]
    public string FirstName { get; init; } = string.Empty;

    /// <summary>Family name.</summary>
    [Required(ErrorMessage = "A last name is required.")]
    [StringLength(80, MinimumLength = 1)]
    public string LastName { get; init; } = string.Empty;

    /// <summary>Contact address.</summary>
    [Required(ErrorMessage = "An email address is required.")]
    [EmailAddress(ErrorMessage = "That email address is not valid.")]
    [StringLength(256)]
    public string? Email { get; init; }

    /// <summary>Contact number: digits, spaces, + ( ) and -, at least seven digits.</summary>
    [Required(ErrorMessage = "A phone number is required.")]
    [RegularExpression(@"^\+?[0-9 ()-]{7,32}$", ErrorMessage = "That phone number is not valid. Use digits, with an optional leading +.")]
    [StringLength(32)]
    public string? PhoneNumber { get; init; }

    /// <summary>A department from the department list.</summary>
    [Required(ErrorMessage = "Choose a department.")]
    [StringLength(120)]
    public string? Department { get; init; }

    /// <summary>A job title from the job title list.</summary>
    [Required(ErrorMessage = "Choose a job title.")]
    [StringLength(120)]
    public string? JobTitle { get; init; }
}

/// <summary>The edit form: the employee as stored, or as last submitted.</summary>
public sealed class EmployeeEditViewModel
{
    /// <summary>Internal identifier.</summary>
    public int MobileUserId { get; init; }

    /// <summary>What they sign in with; not editable.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Payroll or HR number.</summary>
    public string? EmployeeNumber { get; init; }

    /// <summary>Given name.</summary>
    public string FirstName { get; init; } = string.Empty;

    /// <summary>Family name.</summary>
    public string LastName { get; init; } = string.Empty;

    /// <summary>Contact address.</summary>
    public string? Email { get; init; }

    /// <summary>Contact number.</summary>
    public string? PhoneNumber { get; init; }

    /// <summary>Department.</summary>
    public string? Department { get; init; }

    /// <summary>Job title.</summary>
    public string? JobTitle { get; init; }

    /// <summary>
    /// The department held as stored — offered even when deactivated, so the
    /// employee is not forced off it by an unrelated edit.
    /// </summary>
    public string? StoredDepartment { get; init; }

    /// <summary>The job title held as stored, on the same terms.</summary>
    public string? StoredJobTitle { get; init; }

    /// <summary>Concurrency token, Base64.</summary>
    public string RowVersion { get; init; } = string.Empty;

    /// <summary>From the stored record.</summary>
    public static EmployeeEditViewModel From(EmployeeDetail employee) => new()
    {
        MobileUserId = employee.MobileUserId,
        UserId = employee.UserId,
        EmployeeNumber = employee.EmployeeNumber,
        FirstName = employee.FirstName,
        LastName = employee.LastName,
        Email = employee.Email,
        PhoneNumber = employee.PhoneNumber,
        Department = employee.Department,
        JobTitle = employee.JobTitle,
        StoredDepartment = employee.Department,
        StoredJobTitle = employee.JobTitle,
        RowVersion = Convert.ToBase64String(employee.RowVersion),
    };

    /// <summary>What was submitted, kept after a refusal so it need not be retyped.</summary>
    public static EmployeeEditViewModel From(EmployeeDetail employee, EmployeeDetailsInput input, byte[] rowVersion)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new()
        {
            MobileUserId = employee.MobileUserId,
            UserId = employee.UserId,
            EmployeeNumber = input.EmployeeNumber,
            FirstName = input.FirstName,
            LastName = input.LastName,
            Email = input.Email,
            PhoneNumber = input.PhoneNumber,
            Department = input.Department,
            JobTitle = input.JobTitle,
            StoredDepartment = employee.Department,
            StoredJobTitle = employee.JobTitle,
            RowVersion = rowVersion.Length == 8 ? Convert.ToBase64String(rowVersion) : Convert.ToBase64String(employee.RowVersion),
        };
    }
}

/// <summary>What the page after creation shows, once.</summary>
public sealed class CreatedEmployeeViewModel
{
    /// <summary>The new employee.</summary>
    public int MobileUserId { get; init; }

    /// <summary>Their sign-in identifier.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Their name.</summary>
    public string FullName { get; init; } = string.Empty;

    /// <summary>
    /// The issued password. Held only for the duration of this response — the
    /// database has its hash and nothing else (§16, §24).
    /// </summary>
    public string InitialPassword { get; init; } = string.Empty;
}

/// <summary>What the enrolment page shows, once.</summary>
public sealed class EnrolmentViewModel
{
    /// <summary>The employee being enrolled.</summary>
    public int MobileUserId { get; init; }

    /// <summary>Their sign-in identifier.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>The shared secret in Base32, for manual entry.</summary>
    public string SharedSecret { get; init; } = string.Empty;

    /// <summary>The Key URI an authenticator application can consume.</summary>
    public string OtpAuthUri { get; init; } = string.Empty;
}
