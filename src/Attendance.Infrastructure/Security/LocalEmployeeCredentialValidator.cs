using Attendance.Application.Abstractions;

namespace Attendance.Infrastructure.Security;

/// <summary>
/// Validates employee passwords against the local credential store
/// (decision DEC-01).
/// </summary>
/// <remarks>
/// <para>
/// This is the first implementation of the identity seam. An Active Directory
/// provider can replace it later (OPEN-22) without changing the API contract,
/// the mobile application or the attendance procedures — which is the whole
/// reason the seam exists.
/// </para>
/// <para>
/// <b>Every path costs the same.</b> An unknown identifier, an employee with no
/// credential, an inactive employee and a wrong password all perform one PBKDF2
/// derivation before answering. Without that, an unknown user is refused in
/// microseconds while a real one costs the full work factor, and the difference
/// is measurable across the internet — which is how account enumeration is done
/// in practice (threat TH-14, DEC-03). The uniform error code alone does not
/// close it; the timing has to match too.
/// </para>
/// <para>
/// The password is never logged, stored, cached or returned, and does not
/// outlive the call (§24).
/// </para>
/// </remarks>
public sealed class LocalEmployeeCredentialValidator : IEmployeeCredentialValidator
{
    private readonly IMobileUserRepository _users;
    private readonly IPasswordHasher _passwordHasher;

    /// <summary>Creates the validator.</summary>
    public LocalEmployeeCredentialValidator(
        IMobileUserRepository users,
        IPasswordHasher passwordHasher)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(passwordHasher);

        _users = users;
        _passwordHasher = passwordHasher;
    }

    /// <inheritdoc />
    public async Task<CredentialValidationResult> ValidateAsync(
        string userId,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(password))
        {
            // Still spend the work: an empty submission must not be measurably
            // cheaper than a real attempt either.
            _passwordHasher.PerformDummyVerification();
            return CredentialValidationResult.Failure(CredentialValidationOutcome.UnknownUser);
        }

        MobileUserAuthenticationRecord? record =
            await _users.GetForAuthenticationAsync(userId, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {
            _passwordHasher.PerformDummyVerification();
            return CredentialValidationResult.Failure(CredentialValidationOutcome.UnknownUser);
        }

        MobileUserAuthenticationRecord found = record.Value;

        if (found.Credential is not { } credential)
        {
            // The employee exists but has no local credential. Reported
            // separately for the security event trail; the API still answers
            // INVALID_CREDENTIALS.
            _passwordHasher.PerformDummyVerification();
            return CredentialValidationResult.Failure(
                CredentialValidationOutcome.NoCredential, found.MobileUserId);
        }

        bool passwordMatches = _passwordHasher.Verify(password, credential);

        if (!passwordMatches)
        {
            return CredentialValidationResult.Failure(
                CredentialValidationOutcome.InvalidPassword, found.MobileUserId);
        }

        // The password check comes first deliberately. Answering "inactive"
        // without verifying the password would confirm that an account exists to
        // anyone who guessed the identifier.
        if (!found.IsActive)
        {
            return CredentialValidationResult.Failure(
                CredentialValidationOutcome.UserInactive, found.MobileUserId);
        }

        return CredentialValidationResult.Success(
            found.MobileUserId,
            requiresPasswordChange: _passwordHasher.NeedsRehash(credential));
    }
}
