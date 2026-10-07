namespace Attendance.Application.Abstractions;

/// <summary>
/// Verifies an employee's password against the organisation's identity source.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam created by DEC-01. The first implementation validates
/// against the local credential store in SQL Server; an Active Directory
/// provider can replace it later (OPEN-22) without any change to the API
/// contract, the mobile application or the attendance procedures.
/// </para>
/// <para>
/// <b>Implementations must take the same time whether or not the employee
/// exists.</b> An unknown user that answers faster than a known one lets an
/// attacker enumerate valid identifiers, which matters especially because this
/// API is internet-facing (DEC-03, threat TH-14). The local implementation
/// performs a dummy hash with the same parameters when no credential is found.
/// </para>
/// <para>
/// The password is passed as a <see cref="string"/> because that is what the
/// request deserialiser produces; .NET strings cannot be reliably zeroed, so
/// the mitigation is lifetime, not scrubbing — it is never logged, never
/// stored, never placed in a query string, and never held beyond the call
/// (§24).
/// </para>
/// </remarks>
public interface IEmployeeCredentialValidator
{
    /// <summary>
    /// Validates an employee's password.
    /// </summary>
    /// <param name="userId">The identifier the employee signs in with.</param>
    /// <param name="password">The presented password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The outcome. Implementations must not throw to signal a wrong password:
    /// a failed sign-in is an expected result, not an exceptional condition.
    /// </returns>
    Task<CredentialValidationResult> ValidateAsync(
        string userId,
        string password,
        CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of validating an employee's password.
/// </summary>
/// <remarks>
/// The distinctions here exist for the security event trail, not for the
/// response: the API collapses them into INVALID_CREDENTIALS so that an
/// internet-facing endpoint cannot confirm which part was correct
/// (conflict CON-09, OPEN-38).
/// </remarks>
public enum CredentialValidationOutcome
{
    /// <summary>The password matched an active employee's credential.</summary>
    Valid = 0,

    /// <summary>No employee matches that identifier.</summary>
    UnknownUser = 1,

    /// <summary>The employee exists but the password did not match.</summary>
    InvalidPassword = 2,

    /// <summary>The employee exists but is inactive or suspended.</summary>
    UserInactive = 3,

    /// <summary>The employee has no credential in this identity source.</summary>
    NoCredential = 4,
}

/// <summary>
/// The result of a credential validation attempt.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="MobileUserId">
/// The internal employee identifier, present only when the employee was found.
/// Callers must not treat a non-null value as proof of authentication — check
/// <see cref="Outcome"/>.
/// </param>
/// <param name="RequiresPasswordChange">
/// Whether the credential is flagged as needing to be changed. Recorded for the
/// future self-service flow (OPEN-40); it does not block attendance today.
/// </param>
public readonly record struct CredentialValidationResult(
    CredentialValidationOutcome Outcome,
    int? MobileUserId,
    bool RequiresPasswordChange)
{
    /// <summary>Whether the password was accepted.</summary>
    public bool IsValid => Outcome == CredentialValidationOutcome.Valid;

    /// <summary>Creates a successful result.</summary>
    public static CredentialValidationResult Success(int mobileUserId, bool requiresPasswordChange) =>
        new(CredentialValidationOutcome.Valid, mobileUserId, requiresPasswordChange);

    /// <summary>Creates a failed result.</summary>
    public static CredentialValidationResult Failure(CredentialValidationOutcome outcome, int? mobileUserId = null) =>
        new(outcome, mobileUserId, false);
}
