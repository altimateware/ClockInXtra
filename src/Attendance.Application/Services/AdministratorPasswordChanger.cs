using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;

namespace Attendance.Application.Services;

/// <summary>
/// Changes a signed-in administrator's own password.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the current password is required even though the caller is signed
/// in.</b> A session is a bearer credential: a workstation left unlocked, or a
/// cookie lifted from one, would otherwise be enough to set a password the owner
/// does not know and lock them out of their own account. Asking for the current
/// password turns "holds a session" into "knows the secret".
/// </para>
/// <para>
/// <b>Failures count toward the sign-in lockout.</b> This form is a password
/// oracle — it answers whether a guess is right — so it must be throttled
/// exactly like the sign-in page, and share its counter. A separate counter would
/// double the guesses an attacker gets.
/// </para>
/// <para>
/// Order, cheapest and most decisive first: lockout; the account and the session
/// still being current; the current password; the new password's policy; then
/// the write. The policy is checked <em>after</em> the current password, so a
/// wrong guess is always counted toward the lockout regardless of what was typed
/// as the new password, and the policy's answers are only ever shown to someone
/// who has already proven the current one.
/// </para>
/// </remarks>
public sealed class AdministratorPasswordChanger
{
    private readonly IAdministratorRepository _administrators;
    private readonly IAdministratorPolicyProvider _policyProvider;
    private readonly IAuthenticationAttemptRepository _lockout;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ISecurityEventRecorder _securityEvents;

    /// <summary>Creates the service.</summary>
    public AdministratorPasswordChanger(
        IAdministratorRepository administrators,
        IAdministratorPolicyProvider policyProvider,
        IAuthenticationAttemptRepository lockout,
        IPasswordHasher passwordHasher,
        ISecurityEventRecorder securityEvents)
    {
        ArgumentNullException.ThrowIfNull(administrators);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(lockout);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(securityEvents);

        _administrators = administrators;
        _policyProvider = policyProvider;
        _lockout = lockout;
        _passwordHasher = passwordHasher;
        _securityEvents = securityEvents;
    }

    /// <summary>Attempts the change.</summary>
    public async Task<PasswordChangeOutcome> ChangeAsync(
        PasswordChangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.UserName);

        // 1. Lockout, before any password work is spent.
        LockoutState lockout = await _lockout
            .CheckAsync(AuthenticationSubject.Administrator, request.UserName, cancellationToken)
            .ConfigureAwait(false);

        if (lockout.IsLockedOut)
        {
            await RecordAsync(request, "Auth.PasswordChangeLocked", SecurityEventSeverity.Warning, "ACCOUNT_LOCKED", cancellationToken)
                .ConfigureAwait(false);

            return PasswordChangeOutcome.Failed(AttendanceResultCode.AccountLocked);
        }

        // 2. The account, and the session asking, must both still be current.
        AdministratorRecord? found = await _administrators
            .GetForAuthenticationAsync(request.UserName, cancellationToken).ConfigureAwait(false);

        if (found is not { } administrator
            || administrator.AdministratorId != request.AdministratorId
            || administrator.Credential is not { } credential)
        {
            // The cookie named an account that no longer matches. Not a wrong
            // password — a session that should already have ended.
            _passwordHasher.PerformDummyVerification();
            return PasswordChangeOutcome.Failed(AttendanceResultCode.Unauthorized);
        }

        if (!administrator.IsActive)
        {
            return PasswordChangeOutcome.Failed(AttendanceResultCode.UserInactive);
        }

        if (administrator.SecurityStamp != request.SecurityStamp)
        {
            return PasswordChangeOutcome.Failed(AttendanceResultCode.ConcurrencyConflict);
        }

        // 3. The current password.
        if (!_passwordHasher.Verify(request.CurrentPassword ?? string.Empty, credential))
        {
            AdministratorPolicy policy = await _policyProvider.GetAsync(cancellationToken).ConfigureAwait(false);

            LockoutState state = await _lockout
                .RegisterFailureAsync(
                    AuthenticationSubject.Administrator,
                    request.UserName,
                    policy.LockoutThreshold,
                    policy.LockoutMinutes,
                    cancellationToken)
                .ConfigureAwait(false);

            await RecordAsync(request, "Auth.PasswordChangeFailed", SecurityEventSeverity.Warning, "INVALID_PASSWORD", cancellationToken)
                .ConfigureAwait(false);

            return PasswordChangeOutcome.Failed(
                state.IsLockedOut ? AttendanceResultCode.AccountLocked : AttendanceResultCode.InvalidCredentials);
        }

        // 4. The new password.
        PasswordPolicyViolation violation = AdministratorPasswordPolicy.Check(
            request.NewPassword, request.CurrentPassword, administrator.UserName);

        if (violation != PasswordPolicyViolation.None)
        {
            return PasswordChangeOutcome.Refused(violation);
        }

        // 5. The write, bound to the session's stamp.
        PasswordChangeResult result = await _administrators
            .ChangePasswordAsync(
                administrator.AdministratorId,
                request.SecurityStamp,
                _passwordHasher.Hash(request.NewPassword!),
                request.CorrelationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.ResultCode != AttendanceResultCode.Success)
        {
            return PasswordChangeOutcome.Failed(result.ResultCode);
        }

        // The correct current password was just proven, so a partial run of
        // earlier failures must not linger and lock the account later.
        await _lockout
            .ResetAsync(AuthenticationSubject.Administrator, request.UserName, cancellationToken)
            .ConfigureAwait(false);

        await RecordAsync(request, "Administrator.PasswordChanged", SecurityEventSeverity.Information, "SELF_SERVICE", cancellationToken)
            .ConfigureAwait(false);

        return PasswordChangeOutcome.Succeeded(result.NewSecurityStamp!.Value);
    }

    private Task RecordAsync(
        PasswordChangeRequest request,
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

/// <summary>A request to change one's own password.</summary>
/// <param name="AdministratorId">From the session, never from the form.</param>
/// <param name="UserName">From the session, never from the form.</param>
/// <param name="SecurityStamp">The stamp carried by the session.</param>
/// <param name="CurrentPassword">Verified and discarded; never stored or logged (§16, §24).</param>
/// <param name="NewPassword">Hashed and discarded; never stored or logged.</param>
/// <param name="CorrelationId">Ties the events to the request.</param>
/// <param name="SourceAddressHash">Salted hash of the client address.</param>
public readonly record struct PasswordChangeRequest(
    int AdministratorId,
    string UserName,
    Guid SecurityStamp,
    string? CurrentPassword,
    string? NewPassword,
    Guid CorrelationId,
    byte[]? SourceAddressHash);

/// <summary>The outcome of a password change.</summary>
/// <param name="ResultCode">Success, or why not.</param>
/// <param name="Violation">
/// The policy rule broken, when the new password was refused. Only ever set
/// after the current password was proven, so it reveals nothing to someone who
/// does not already know it.
/// </param>
/// <param name="NewSecurityStamp">The rotated stamp, on success.</param>
public readonly record struct PasswordChangeOutcome(
    AttendanceResultCode ResultCode,
    PasswordPolicyViolation Violation,
    Guid? NewSecurityStamp)
{
    /// <summary>Whether the password was changed.</summary>
    public bool IsSuccess => ResultCode == AttendanceResultCode.Success;

    /// <summary>A successful change.</summary>
    public static PasswordChangeOutcome Succeeded(Guid newStamp) =>
        new(AttendanceResultCode.Success, PasswordPolicyViolation.None, newStamp);

    /// <summary>A failure unrelated to the new password's content.</summary>
    public static PasswordChangeOutcome Failed(AttendanceResultCode resultCode) =>
        new(resultCode, PasswordPolicyViolation.None, null);

    /// <summary>The new password broke a policy rule.</summary>
    public static PasswordChangeOutcome Refused(PasswordPolicyViolation violation) =>
        new(AttendanceResultCode.InvalidRequest, violation, null);
}
