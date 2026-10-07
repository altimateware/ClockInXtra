using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;

namespace Attendance.Application.Services;

/// <summary>
/// Verifies an employee's password and authenticator code, in the one order the
/// system considers correct.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists as a service rather than as code inside each handler.</b>
/// Two endpoints authenticate an employee: clock-in, and device registration.
/// The database's own registration procedure states that its caller must already
/// have verified the password, verified the code, consumed the time step and
/// applied the lockout counters — the same sequence clock-in performs. Written
/// out twice, the two copies drift, and the copy that drifts is the one nobody
/// re-reads. Registration is also, in the procedure's own words, the system's
/// main brute-force surface (threat TH-14), so it is the worse of the two places
/// to have a subtly weaker sequence.
/// </para>
/// <para>
/// <b>The order is the security design.</b>
/// </para>
/// <list type="number">
///   <item><b>Password, then authenticator.</b> A failure of either produces one
///   collapsed answer, so the endpoint cannot confirm that a password was right
///   (CON-09).</item>
///   <item><b>Consume the time step before the caller acts.</b> The code is spent
///   whether or not the caller's own transaction then succeeds — otherwise a
///   refused operation would hand the code back for reuse.</item>
///   <item><b>Clear the failure counter only at the very end.</b> Clearing it
///   once the password is right would let someone who knows the password hold
///   the counter at zero while guessing codes indefinitely.</item>
/// </list>
/// <para>
/// The lockout check is deliberately <em>not</em> folded into
/// <see cref="AuthenticateAsync"/>. Clock-in has to evaluate location between the
/// lockout check and the password — cheap before expensive, so that flooding the
/// endpoint cannot force hundreds of milliseconds of PBKDF2 work per request
/// (§22). Callers therefore call <see cref="CheckLockoutAsync"/> first and decide
/// for themselves what belongs in between.
/// </para>
/// </remarks>
public sealed class EmployeeAuthenticator
{
    private readonly IAuthenticationAttemptRepository _lockout;
    private readonly IEmployeeCredentialValidator _credentials;
    private readonly IMobileUserRepository _users;
    private readonly ISecretProtector _secretProtector;
    private readonly ITotpVerifier _totpVerifier;
    private readonly IMfaCredentialRepository _mfaCredentials;
    private readonly ISecurityEventRecorder _securityEvents;

    /// <summary>Creates the authenticator.</summary>
    public EmployeeAuthenticator(
        IAuthenticationAttemptRepository lockout,
        IEmployeeCredentialValidator credentials,
        IMobileUserRepository users,
        ISecretProtector secretProtector,
        ITotpVerifier totpVerifier,
        IMfaCredentialRepository mfaCredentials,
        ISecurityEventRecorder securityEvents)
    {
        ArgumentNullException.ThrowIfNull(lockout);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(secretProtector);
        ArgumentNullException.ThrowIfNull(totpVerifier);
        ArgumentNullException.ThrowIfNull(mfaCredentials);
        ArgumentNullException.ThrowIfNull(securityEvents);

        _lockout = lockout;
        _credentials = credentials;
        _users = users;
        _secretProtector = secretProtector;
        _totpVerifier = totpVerifier;
        _mfaCredentials = mfaCredentials;
        _securityEvents = securityEvents;
    }

    /// <summary>
    /// Reports whether the account is locked, before any credential work is
    /// spent on the request.
    /// </summary>
    public Task<LockoutState> CheckLockoutAsync(string userId, CancellationToken cancellationToken) =>
        _lockout.CheckAsync(AuthenticationSubject.MobileUser, userId, cancellationToken);

    /// <summary>
    /// Verifies both factors and consumes the authenticator time step.
    /// </summary>
    public async Task<EmployeeAuthenticationResult> AuthenticateAsync(
        EmployeeAuthenticationRequest request,
        AttendancePolicy policy,
        CancellationToken cancellationToken)
    {
        // 1. Password. The validator spends equivalent work when the employee is
        //    unknown, so an invalid identifier is not measurably faster.
        CredentialValidationResult credential = await _credentials
            .ValidateAsync(request.UserId, request.Password, cancellationToken)
            .ConfigureAwait(false);

        if (!credential.IsValid)
        {
            return await FailAsync(request, policy, credential.Outcome.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }

        int mobileUserId = credential.MobileUserId!.Value;

        // 2. Authenticator: load the enrolment, decrypt the secret for the length
        //    of one verification, then consume the step that matched.
        MobileUserAuthenticationRecord? record = await _users
            .GetForAuthenticationAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        if (record?.Mfa is not { } enrolment)
        {
            await RecordAsync(request, "Mfa.Missing", SecurityEventSeverity.Warning, "MFA_NOT_ENROLLED", cancellationToken)
                .ConfigureAwait(false);

            return EmployeeAuthenticationResult.Failed(AttendanceResultCode.MfaNotEnrolled);
        }

        if (!_secretProtector.TryUnprotect(
                SecretPurposes.MobileUserTotpSecret, enrolment.SecretProtected, out byte[] secret))
        {
            // The key ring is missing or the row is unreadable. This is an
            // operational failure, not a wrong code, and must not be reported as
            // invalid credentials — the employee would be told they typed it
            // wrong when nothing they do can help.
            await RecordAsync(request, "Mfa.SecretUnreadable", SecurityEventSeverity.Critical, "SECRET_UNREADABLE", cancellationToken)
                .ConfigureAwait(false);

            return EmployeeAuthenticationResult.Failed(AttendanceResultCode.InternalError);
        }

        TotpVerificationResult totp;

        try
        {
            totp = _totpVerifier.Verify(
                secret, request.AuthenticatorCode, enrolment.Parameters, policy.TotpStepTolerance);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        }

        if (!totp.IsValid)
        {
            return await FailAsync(request, policy, "INVALID_OTP", cancellationToken).ConfigureAwait(false);
        }

        AttendanceResultCode consumed = await _mfaCredentials
            .TryConsumeTimeStepAsync(mobileUserId, totp.MatchedTimeStep, cancellationToken)
            .ConfigureAwait(false);

        if (consumed != AttendanceResultCode.Success)
        {
            // Most likely the same code presented twice — possibly to two
            // different servers at once, which is exactly what the database
            // check exists to catch.
            return await FailAsync(request, policy, consumed.ToString(), cancellationToken).ConfigureAwait(false);
        }

        // 3. Every factor has now passed, so the failure counter is cleared.
        await _lockout.ResetAsync(AuthenticationSubject.MobileUser, request.UserId, cancellationToken)
            .ConfigureAwait(false);

        return EmployeeAuthenticationResult.Authenticated(mobileUserId);
    }

    private async Task<EmployeeAuthenticationResult> FailAsync(
        EmployeeAuthenticationRequest request,
        AttendancePolicy policy,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        LockoutState state = await _lockout
            .RegisterFailureAsync(
                AuthenticationSubject.MobileUser,
                request.UserId,
                policy.MobileLockoutThreshold,
                policy.MobileLockoutMinutes,
                cancellationToken)
            .ConfigureAwait(false);

        // The precise reason goes to the append-only trail; the caller receives
        // the collapsed code so the endpoint cannot be used to confirm which
        // part of the credential was right (CON-09).
        await RecordAsync(request, "Auth.Failed", SecurityEventSeverity.Warning, reasonCode, cancellationToken)
            .ConfigureAwait(false);

        return EmployeeAuthenticationResult.Failed(
            state.IsLockedOut ? AttendanceResultCode.AccountLocked : AttendanceResultCode.InvalidCredentials);
    }

    private Task RecordAsync(
        EmployeeAuthenticationRequest request,
        string eventType,
        SecurityEventSeverity severity,
        string reasonCode,
        CancellationToken cancellationToken) =>
        _securityEvents.RecordAsync(
            new SecurityEvent(
                eventType,
                severity,
                SecurityEventSubject.MobileUser,
                request.UserId,
                reasonCode,
                SourceApplication: "Attendance.Api",
                DevicePublicId: request.DevicePublicId,
                SourceAddressHash: request.SourceAddressHash,
                CorrelationId: request.CorrelationId),
            cancellationToken);
}

/// <summary>
/// The credentials to verify, plus the context an event needs.
/// </summary>
/// <param name="UserId">The identifier the employee typed.</param>
/// <param name="Password">Verified and discarded; never stored or logged (§24).</param>
/// <param name="AuthenticatorCode">The six-digit code.</param>
/// <param name="DevicePublicId">
/// The device involved, where there is one. Absent during registration, because
/// the device does not exist yet.
/// </param>
/// <param name="SourceAddressHash">Salted hash of the client address.</param>
/// <param name="CorrelationId">Ties the events to the request that caused them.</param>
public readonly record struct EmployeeAuthenticationRequest(
    string UserId,
    string Password,
    string AuthenticatorCode,
    Guid? DevicePublicId,
    byte[]? SourceAddressHash,
    Guid CorrelationId);

/// <summary>The outcome of verifying an employee's two factors.</summary>
/// <param name="ResultCode">
/// <see cref="AttendanceResultCode.Success"/>, or the code to return to the
/// client — already collapsed where the design requires it.
/// </param>
/// <param name="MobileUserId">The employee, present only on success.</param>
public readonly record struct EmployeeAuthenticationResult(
    AttendanceResultCode ResultCode,
    int? MobileUserId)
{
    /// <summary>Whether both factors passed and the time step was consumed.</summary>
    public bool IsAuthenticated => ResultCode == AttendanceResultCode.Success;

    /// <summary>A fully authenticated employee.</summary>
    public static EmployeeAuthenticationResult Authenticated(int mobileUserId) =>
        new(AttendanceResultCode.Success, mobileUserId);

    /// <summary>A refusal carrying only its code.</summary>
    public static EmployeeAuthenticationResult Failed(AttendanceResultCode resultCode) =>
        new(resultCode, null);
}
