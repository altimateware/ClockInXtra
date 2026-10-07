namespace Attendance.Application.Abstractions;

/// <summary>
/// Reads the employee identity material needed to authenticate a clock-in.
/// </summary>
/// <remarks>
/// Maps to <c>mobile.usp_MobileUser_GetForAuthentication</c>, which returns the
/// employee, their credential parameters and their active authenticator
/// enrolment in one round trip — the clock-in path needs all three, and three
/// separate queries would be three chances for the picture to change underneath
/// it.
/// </remarks>
public interface IMobileUserRepository
{
    /// <summary>
    /// Loads authentication material for the identifier an employee typed.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when no employee matches. Callers must still spend
    /// the cost of a password verification in that case — see
    /// <see cref="IPasswordHasher.PerformDummyVerification"/>.
    /// </returns>
    Task<MobileUserAuthenticationRecord?> GetForAuthenticationAsync(
        string userId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Everything needed to authenticate an employee, as stored.
/// </summary>
/// <param name="MobileUserId">Internal identifier.</param>
/// <param name="MobileUserPublicId">External identifier, safe to log.</param>
/// <param name="UserId">The identifier the employee signs in with.</param>
/// <param name="IsActive">Whether the employee may clock in at all.</param>
/// <param name="Credential">
/// Password material, or <see langword="null"/> when the employee has no local
/// credential — which happens if an account was created before a password was
/// set, and must be treated as a failed sign-in rather than an open door.
/// </param>
/// <param name="Mfa">
/// The active authenticator enrolment, or <see langword="null"/> when none is
/// active. Clock-in requires one (§12), so its absence is reported as
/// MFA_NOT_ENROLLED rather than silently skipping the second factor.
/// </param>
public readonly record struct MobileUserAuthenticationRecord(
    int MobileUserId,
    Guid MobileUserPublicId,
    string UserId,
    bool IsActive,
    PasswordHash? Credential,
    MfaEnrolment? Mfa);

/// <summary>
/// An employee's active authenticator enrolment.
/// </summary>
/// <param name="MfaCredentialId">Identifier of the enrolment.</param>
/// <param name="SecretProtected">
/// The shared secret as stored: Data Protection ciphertext. It is decrypted for
/// the duration of one verification and never persisted or logged in the clear
/// (§13, §33).
/// </param>
/// <param name="Parameters">Algorithm parameters recorded at enrolment.</param>
/// <param name="LastAcceptedTimeStep">
/// The most recent time step accepted for this credential. Present for
/// diagnostics only: the authoritative replay check is the conditional UPDATE in
/// <c>core.usp_MfaCredential_TryConsumeTimeStep</c>, because only the database
/// can enforce single use across several application servers.
/// </param>
public readonly record struct MfaEnrolment(
    int MfaCredentialId,
    byte[] SecretProtected,
    TotpParameters Parameters,
    long? LastAcceptedTimeStep);
