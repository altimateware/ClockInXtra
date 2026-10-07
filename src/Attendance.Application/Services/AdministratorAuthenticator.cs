using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;

namespace Attendance.Application.Services;

/// <summary>
/// Verifies an administrator's password and authenticator code.
/// </summary>
/// <remarks>
/// <para>
/// The same ordering discipline as the employee path, for the same reasons:
/// lockout before any password work, both factors before the counter is cleared,
/// and the time step consumed so a code cannot be reused. The differences are
/// that an administrator's authenticator lives on the account row rather than in
/// a separate credential table, and that the portal reports one message for every
/// failure.
/// </para>
/// <para>
/// <b>Why the portal collapses its errors too.</b> The administration portal is
/// internal-only (ASM-01), which lowers but does not remove the enumeration risk:
/// an insider is exactly the attacker who benefits from learning which
/// administrator accounts exist. The response says only that the details were not
/// correct; the precise reason goes to the security trail.
/// </para>
/// </remarks>
public sealed class AdministratorAuthenticator
{
    private readonly IAdministratorRepository _administrators;
    private readonly IAdministratorPolicyProvider _policyProvider;
    private readonly IAuthenticationAttemptRepository _lockout;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ISecretProtector _secretProtector;
    private readonly ITotpVerifier _totpVerifier;
    private readonly ISecurityEventRecorder _securityEvents;

    /// <summary>Creates the authenticator.</summary>
    public AdministratorAuthenticator(
        IAdministratorRepository administrators,
        IAdministratorPolicyProvider policyProvider,
        IAuthenticationAttemptRepository lockout,
        IPasswordHasher passwordHasher,
        ISecretProtector secretProtector,
        ITotpVerifier totpVerifier,
        ISecurityEventRecorder securityEvents)
    {
        ArgumentNullException.ThrowIfNull(administrators);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(lockout);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(secretProtector);
        ArgumentNullException.ThrowIfNull(totpVerifier);
        ArgumentNullException.ThrowIfNull(securityEvents);

        _administrators = administrators;
        _policyProvider = policyProvider;
        _lockout = lockout;
        _passwordHasher = passwordHasher;
        _secretProtector = secretProtector;
        _totpVerifier = totpVerifier;
        _securityEvents = securityEvents;
    }

    /// <summary>Signs an administrator in.</summary>
    public async Task<AdministratorSignInResult> SignInAsync(
        AdministratorSignInRequest request,
        CancellationToken cancellationToken)
    {
        AdministratorPolicy policy = await _policyProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        // 1. Lockout, before any password work is spent.
        LockoutState lockout = await _lockout
            .CheckAsync(AuthenticationSubject.Administrator, request.UserName, cancellationToken)
            .ConfigureAwait(false);

        if (lockout.IsLockedOut)
        {
            await RecordAsync(request, "Auth.Locked", SecurityEventSeverity.Warning, "ACCOUNT_LOCKED", cancellationToken)
                .ConfigureAwait(false);

            return AdministratorSignInResult.Failed(AttendanceResultCode.AccountLocked);
        }

        AdministratorRecord? found = await _administrators
            .GetForAuthenticationAsync(request.UserName, cancellationToken).ConfigureAwait(false);

        // 2. Password. An unknown account must cost the same as a known one, or
        //    the timing difference enumerates administrators.
        if (found is not { } administrator || administrator.Credential is not { } credential)
        {
            _passwordHasher.PerformDummyVerification();

            return await FailAsync(request, policy, "UNKNOWN_ADMINISTRATOR", cancellationToken).ConfigureAwait(false);
        }

        if (!_passwordHasher.Verify(request.Password, credential))
        {
            return await FailAsync(request, policy, "INVALID_PASSWORD", cancellationToken).ConfigureAwait(false);
        }

        // An inactive account is refused after the password work, not before, so
        // that disabled accounts are indistinguishable from wrong passwords.
        if (!administrator.IsActive)
        {
            return await FailAsync(request, policy, "ADMINISTRATOR_INACTIVE", cancellationToken).ConfigureAwait(false);
        }

        // 3. Authenticator, where policy requires one.
        if (policy.RequireMfa)
        {
            if (administrator.MfaStatus == AdministratorMfaStatus.None
                || administrator.MfaSecretProtected is not { Length: > 0 } protectedSecret)
            {
                await RecordAsync(request, "Mfa.Missing", SecurityEventSeverity.Warning, "MFA_NOT_ENROLLED", cancellationToken)
                    .ConfigureAwait(false);

                return AdministratorSignInResult.Failed(AttendanceResultCode.MfaNotEnrolled);
            }

            if (string.IsNullOrWhiteSpace(request.AuthenticatorCode))
            {
                return await FailAsync(request, policy, "MFA_CODE_MISSING", cancellationToken).ConfigureAwait(false);
            }

            if (!_secretProtector.TryUnprotect(
                    SecretPurposes.AdministratorTotpSecret, protectedSecret, out byte[] secret))
            {
                // An unreadable key ring is an operational failure, not a wrong
                // code, and must not be reported as invalid credentials.
                await RecordAsync(request, "Mfa.SecretUnreadable", SecurityEventSeverity.Critical, "SECRET_UNREADABLE", cancellationToken)
                    .ConfigureAwait(false);

                return AdministratorSignInResult.Failed(AttendanceResultCode.InternalError);
            }

            TotpVerificationResult totp;

            try
            {
                totp = _totpVerifier.Verify(
                    secret, request.AuthenticatorCode, TotpParameters.Default, policy.TotpStepTolerance);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
            }

            if (!totp.IsValid)
            {
                return await FailAsync(request, policy, "INVALID_OTP", cancellationToken).ConfigureAwait(false);
            }

            AttendanceResultCode consumed = await _administrators
                .TryConsumeTimeStepAsync(administrator.AdministratorId, totp.MatchedTimeStep, cancellationToken)
                .ConfigureAwait(false);

            if (consumed != AttendanceResultCode.Success)
            {
                return await FailAsync(request, policy, consumed.ToString(), cancellationToken).ConfigureAwait(false);
            }
        }

        // 4. Every factor has passed: record the sign-in, which also clears the
        //    lockout counter and writes both trail entries in one transaction.
        await _administrators
            .RecordLoginAsync(administrator.AdministratorId, request.CorrelationId, request.SourceAddressHash, cancellationToken)
            .ConfigureAwait(false);

        return AdministratorSignInResult.Succeeded(administrator);
    }

    private async Task<AdministratorSignInResult> FailAsync(
        AdministratorSignInRequest request,
        AdministratorPolicy policy,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        LockoutState state = await _lockout
            .RegisterFailureAsync(
                AuthenticationSubject.Administrator,
                request.UserName,
                policy.LockoutThreshold,
                policy.LockoutMinutes,
                cancellationToken)
            .ConfigureAwait(false);

        await RecordAsync(request, "Auth.Failed", SecurityEventSeverity.Warning, reasonCode, cancellationToken)
            .ConfigureAwait(false);

        return AdministratorSignInResult.Failed(
            state.IsLockedOut ? AttendanceResultCode.AccountLocked : AttendanceResultCode.InvalidCredentials);
    }

    private Task RecordAsync(
        AdministratorSignInRequest request,
        string eventType,
        SecurityEventSeverity severity,
        string reasonCode,
        CancellationToken cancellationToken) =>
        _securityEvents.RecordAsync(
            new SecurityEvent(
                eventType,
                severity,
                SecurityEventSubject.Administrator,
                request.UserName,
                reasonCode,
                SourceApplication: "Attendance.Admin",
                DevicePublicId: null,
                SourceAddressHash: request.SourceAddressHash,
                CorrelationId: request.CorrelationId),
            cancellationToken);
}

/// <summary>A sign-in attempt.</summary>
/// <param name="UserName">The name typed.</param>
/// <param name="Password">Verified and discarded; never stored or logged (§16, §24).</param>
/// <param name="AuthenticatorCode">The six-digit code, where MFA is required.</param>
/// <param name="CorrelationId">Ties the events to the request.</param>
/// <param name="SourceAddressHash">Salted hash of the client address.</param>
public readonly record struct AdministratorSignInRequest(
    string UserName,
    string Password,
    string? AuthenticatorCode,
    Guid CorrelationId,
    byte[]? SourceAddressHash);

/// <summary>The outcome of a sign-in.</summary>
/// <param name="ResultCode">The outcome, already collapsed for display.</param>
/// <param name="Administrator">The account, on success.</param>
public readonly record struct AdministratorSignInResult(
    AttendanceResultCode ResultCode,
    AdministratorRecord? Administrator)
{
    /// <summary>Whether every factor passed.</summary>
    public bool IsAuthenticated => ResultCode == AttendanceResultCode.Success;

    /// <summary>A successful sign-in.</summary>
    public static AdministratorSignInResult Succeeded(AdministratorRecord administrator) =>
        new(AttendanceResultCode.Success, administrator);

    /// <summary>A refusal carrying only its code.</summary>
    public static AdministratorSignInResult Failed(AttendanceResultCode resultCode) =>
        new(resultCode, null);
}
