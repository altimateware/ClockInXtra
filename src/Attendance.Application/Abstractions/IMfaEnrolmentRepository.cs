using Attendance.Domain.Enums;

namespace Attendance.Application.Abstractions;

/// <summary>
/// Authenticator enrolment for employees (§13, §17).
/// </summary>
/// <remarks>
/// <para>
/// <b>Enrolment does not activate.</b> A credential is created pending, and
/// becomes usable only once the employee has produced a working code. Activating
/// on creation would leave somebody unable to clock in if the secret was never
/// successfully scanned — and the failure would surface the next morning, at a
/// door, with a queue behind them.
/// </para>
/// <para>
/// Who performs enrolment and through what process is OPEN-2. This supports
/// administrator-led enrolment; it does not assume that is the final answer.
/// </para>
/// </remarks>
public interface IMfaEnrolmentRepository
{
    /// <summary>Creates a pending enrolment from an already-protected secret.</summary>
    /// <param name="replaceExisting">
    /// Required to displace an existing authenticator. Replacing one silently is
    /// how an employee loses access, or how somebody with portal access quietly
    /// moves the second factor to a device they control — so it is an explicit
    /// act, and the revocation is audited alongside the new enrolment.
    /// </param>
    Task<AttendanceResultCode> EnrolAsync(
        int mobileUserId,
        byte[] secretProtected,
        bool replaceExisting,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the pending enrolment's secret so one code can be verified.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when no enrolment is awaiting activation. An
    /// <em>active</em> credential is never returned: the portal has no
    /// legitimate reason to read the second factor of somebody already enrolled.
    /// </returns>
    Task<PendingEnrolment?> GetForActivationAsync(int mobileUserId, CancellationToken cancellationToken);

    /// <summary>Activates the enrolment, consuming the time step that proved it.</summary>
    Task<AttendanceResultCode> ActivateAsync(
        int mobileUserId,
        long timeStep,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>Revokes an employee's authenticator.</summary>
    Task<AttendanceResultCode> RevokeAsync(
        int mobileUserId,
        string reason,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Employee administration for the portal (§17).
/// </summary>
public interface IMobileUserAdministrationRepository
{
    /// <summary>Searches employees.</summary>
    /// <param name="onlyNotReady">
    /// Restrict to employees who <b>cannot currently clock in</b> — missing a
    /// credential, an authenticator or an active device. That is the list an
    /// administrator actually needs: everyone else is already working.
    /// </param>
    Task<IReadOnlyList<MobileUserSummary>> SearchAsync(
        string? searchTerm,
        bool onlyNotReady,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates an employee together with their initial credential.
    /// </summary>
    /// <param name="password">
    /// The <b>already hashed</b> initial password. No plaintext reaches this
    /// layer or the database (§24); the hashing parameters live in the
    /// application so the work factor can be raised without a schema change.
    /// </param>
    /// <remarks>
    /// The employee row and the credential are written in one transaction. An
    /// employee with no credential could never sign in and a credential with no
    /// employee is orphaned, so a half-applied create is not a state the system
    /// should be able to reach.
    ///
    /// A new employee still cannot clock in: an authenticator has to be enrolled
    /// and a device approved. That is deliberate (OPEN-02) and the portal shows
    /// it as outstanding work rather than creating a half-usable account
    /// silently.
    /// </remarks>
    Task<CreateEmployeeResult> CreateAsync(
        NewEmployee employee,
        PasswordHash password,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>One employee's editable details, or null when there is no such employee.</summary>
    Task<EmployeeDetail?> GetEmployeeAsync(int mobileUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces an employee's details. The user id is not among them: it is what
    /// the employee signs in with, and changing it is not an edit.
    /// </summary>
    Task<AttendanceResultCode> UpdateEmployeeAsync(
        int mobileUserId,
        NewEmployee details,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

/// <summary>An employee's editable details.</summary>
/// <param name="MobileUserId">Internal identifier.</param>
/// <param name="UserId">What they sign in with; shown, not edited.</param>
/// <param name="EmployeeNumber">Payroll or HR number.</param>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Email">Contact address.</param>
/// <param name="PhoneNumber">Contact number.</param>
/// <param name="Department">From the department list.</param>
/// <param name="JobTitle">From the job title list.</param>
/// <param name="IsActive">Whether the account is active.</param>
/// <param name="RowVersion">Concurrency token.</param>
public readonly record struct EmployeeDetail(
    int MobileUserId,
    string UserId,
    string? EmployeeNumber,
    string FirstName,
    string LastName,
    string? Email,
    string? PhoneNumber,
    string? Department,
    string? JobTitle,
    bool IsActive,
    byte[] RowVersion);

/// <summary>
/// The profile fields an employee is created with (§17).
/// </summary>
/// <param name="UserId">The identifier they sign in with. Compared ordinally.</param>
/// <param name="EmployeeNumber">Payroll or HR number.</param>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Email">Contact address.</param>
/// <param name="PhoneNumber">Contact number.</param>
/// <param name="Department">An active entry of the department list.</param>
/// <param name="JobTitle">An active entry of the job title list.</param>
/// <remarks>
/// Every field is required (DEC-11): the business decided that no employee
/// field is optional, and that department and job title are chosen from
/// maintained lists. The types stay nullable because records created before
/// that decision may lack them; the database refuses a new or edited record
/// without them.
/// </remarks>
public readonly record struct NewEmployee(
    string UserId,
    string? EmployeeNumber,
    string FirstName,
    string LastName,
    string? Email,
    string? PhoneNumber,
    string? Department,
    string? JobTitle);

/// <summary>The outcome of creating an employee.</summary>
/// <param name="ResultCode">Success, or why not.</param>
/// <param name="MobileUserId">Internal identifier, on success.</param>
/// <param name="MobileUserPublicId">External identifier, on success.</param>
public readonly record struct CreateEmployeeResult(
    AttendanceResultCode ResultCode,
    int MobileUserId,
    Guid MobileUserPublicId);

/// <summary>
/// An employee, as the portal lists them.
/// </summary>
/// <param name="MobileUserId">Internal identifier.</param>
/// <param name="UserId">The identifier they sign in with.</param>
/// <param name="FullName">Their name.</param>
/// <param name="Department">Their department, where recorded.</param>
/// <param name="IsActive">Whether the account is active.</param>
/// <param name="HasCredential">Whether a password has been set.</param>
/// <param name="HasActiveMfa">Whether an authenticator is enrolled and active.</param>
/// <param name="HasActiveDevice">Whether an approved device exists.</param>
/// <param name="HasDeviceAwaitingApproval">Whether a registration is pending.</param>
/// <param name="MissingDetails">
/// Required details this record lacks (DEC-11), by their field names. Empty for
/// a complete record; records created before every field became required may
/// have some, and the portal asks for them to be completed.
/// </param>
/// <param name="CanClockIn">
/// Whether all three prerequisites are in place. <b>This is the only field that
/// answers the question anybody is really asking</b> — an employee missing any
/// one of them will be refused at the door.
/// </param>
public readonly record struct MobileUserSummary(
    int MobileUserId,
    string UserId,
    string FullName,
    string? Department,
    bool IsActive,
    bool HasCredential,
    bool HasActiveMfa,
    bool HasActiveDevice,
    bool HasDeviceAwaitingApproval,
    bool CanClockIn,
    IReadOnlyList<string> MissingDetails);

/// <summary>An enrolment awaiting its first successful code.</summary>
/// <param name="MfaCredentialId">Identifier of the enrolment.</param>
/// <param name="SecretProtected">Data Protection ciphertext.</param>
/// <param name="Parameters">Algorithm parameters recorded at enrolment.</param>
public readonly record struct PendingEnrolment(
    int MfaCredentialId,
    byte[] SecretProtected,
    TotpParameters Parameters);
