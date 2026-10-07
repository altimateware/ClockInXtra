using Attendance.Domain.Enums;

namespace Attendance.Application.Abstractions;

/// <summary>
/// Reads administrator identity and records successful sign-ins.
/// </summary>
/// <remarks>
/// Backed by the <c>admin</c> stored procedures, which the portal's database
/// account alone may execute. The mobile API is explicitly denied that schema, so
/// a compromised internet-facing process cannot reach administrator credentials
/// at all (threat TH-28).
/// </remarks>
public interface IAdministratorRepository
{
    /// <summary>
    /// Loads an administrator's credential material, MFA state, security stamp
    /// and effective permissions in one round trip.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when no administrator matches. Callers must still
    /// spend a dummy password verification in that case, or an unknown user name
    /// answers measurably faster than a real one.
    /// </returns>
    Task<AdministratorRecord?> GetForAuthenticationAsync(string userName, CancellationToken cancellationToken);

    /// <summary>
    /// Records a fully successful sign-in: timestamp, lockout reset, audit and
    /// security event, in one transaction.
    /// </summary>
    /// <remarks>
    /// Called only after <b>every</b> factor has passed. Clearing the lockout
    /// after a correct password but before the authenticator code would let
    /// someone who knows the password hold the counter at zero while guessing
    /// codes indefinitely.
    /// </remarks>
    Task<AttendanceResultCode> RecordLoginAsync(
        int administratorId,
        Guid correlationId,
        byte[]? sourceAddressHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a sign-out and ends every session the administrator holds.
    /// </summary>
    /// <remarks>
    /// Rotates the security stamp, so a copy of the session cookie taken before
    /// the sign-out stops working on its next request rather than at expiry.
    /// </remarks>
    Task<AttendanceResultCode> RecordLogoutAsync(
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks an administrator's authenticator time step as used.
    /// </summary>
    /// <remarks>
    /// Without this, a single code stays usable for its whole window — about 90
    /// seconds with the ±1 step tolerance — on the accounts that can revoke
    /// devices and rewrite attendance rules.
    /// </remarks>
    Task<AttendanceResultCode> TryConsumeTimeStepAsync(
        int administratorId,
        long timeStep,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores an administrator's TOTP secret, pending activation.
    /// </summary>
    /// <param name="administratorId">The account being enrolled.</param>
    /// <param name="secretProtected">
    /// The secret, already encrypted under
    /// <see cref="SecretPurposes.AdministratorTotpSecret"/>. The plaintext must
    /// never reach this layer's logs or an audit entry (§13, §33).
    /// </param>
    /// <param name="replaceExisting">
    /// Required to overwrite an existing authenticator. Silently replacing one is
    /// how somebody loses access to their own account, and how an attacker with
    /// portal access quietly moves the second factor to a device they control.
    /// </param>
    /// <param name="enrolledByAdministratorId">
    /// Who is performing the enrolment. Required: the database refuses an
    /// enrolment on yourself, or on anyone holding a permission you lack, because
    /// moving another administrator's second factor is half of an account
    /// takeover. (The first administrator is enrolled by setup, in the same
    /// statement that creates the account, not through here.)
    /// </param>
    /// <remarks>
    /// The credential is stored <see cref="AdministratorMfaStatus.PendingActivation"/>
    /// and becomes active on the first code that actually verifies — see
    /// <see cref="TryConsumeTimeStepAsync"/>, which performs both in one
    /// statement. Storing it active would mean a secret that was never
    /// successfully scanned locks the account out permanently, and for the first
    /// administrator there is no second administrator to undo that.
    /// </remarks>
    Task<AttendanceResultCode> EnrolMfaAsync(
        int administratorId,
        byte[] secretProtected,
        bool replaceExisting,
        int enrolledByAdministratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces an administrator's own password and ends their other sessions.
    /// </summary>
    /// <param name="administratorId">The account changing its password.</param>
    /// <param name="expectedSecurityStamp">
    /// The stamp carried by the session making the request. A session that is no
    /// longer current — the account was disabled, a role changed, the password
    /// already changed elsewhere — is refused with
    /// <see cref="AttendanceResultCode.ConcurrencyConflict"/>.
    /// </param>
    /// <param name="password">
    /// The new password, already hashed. The current password must have been
    /// verified by the caller before this is invoked; the database cannot run
    /// PBKDF2 and never sees plaintext (§16, §24).
    /// </param>
    /// <returns>
    /// The outcome and, on success, the rotated stamp — which the caller uses to
    /// re-issue its own session so that only the <em>other</em> sessions end.
    /// </returns>
    Task<PasswordChangeResult> ChangePasswordAsync(
        int administratorId,
        Guid expectedSecurityStamp,
        PasswordHash password,
        Guid correlationId,
        CancellationToken cancellationToken);
}

/// <summary>The outcome of a password change.</summary>
/// <param name="ResultCode">Success, or why not.</param>
/// <param name="NewSecurityStamp">The rotated stamp, on success.</param>
public readonly record struct PasswordChangeResult(
    AttendanceResultCode ResultCode,
    Guid? NewSecurityStamp);

/// <summary>Whether an administrator's authenticator is usable.</summary>
public enum AdministratorMfaStatus
{
    /// <summary>No authenticator enrolled.</summary>
    None = 0,

    /// <summary>Enrolled but not yet activated by a first successful code.</summary>
    PendingActivation = 1,

    /// <summary>Active.</summary>
    Active = 2,
}

/// <summary>
/// An administrator, as stored.
/// </summary>
/// <param name="AdministratorId">Internal identifier.</param>
/// <param name="AdministratorPublicId">External identifier, safe to log.</param>
/// <param name="UserName">The name they sign in with.</param>
/// <param name="DisplayName">Shown in the portal.</param>
/// <param name="Email">Contact address.</param>
/// <param name="Credential">Password material.</param>
/// <param name="MustChangePassword">Whether a change is required before use.</param>
/// <param name="SecurityStamp">
/// Rotated whenever the password, status or role membership changes. The portal
/// revalidates it on every request so those changes invalidate live sessions
/// immediately, rather than leaving a signed cookie usable until it expires
/// (threat TH-35).
/// </param>
/// <param name="MfaSecretProtected">Data Protection ciphertext, never plaintext.</param>
/// <param name="MfaStatus">Whether the authenticator is usable.</param>
/// <param name="IsActive">Whether the account may sign in at all.</param>
/// <param name="Permissions">Effective permission codes, flattened across roles.</param>
public readonly record struct AdministratorRecord(
    int AdministratorId,
    Guid AdministratorPublicId,
    string UserName,
    string DisplayName,
    string? Email,
    PasswordHash? Credential,
    bool MustChangePassword,
    Guid SecurityStamp,
    byte[]? MfaSecretProtected,
    AdministratorMfaStatus MfaStatus,
    bool IsActive,
    IReadOnlyList<string> Permissions);

/// <summary>
/// Reads the settings the administration portal needs to apply policy.
/// </summary>
/// <remarks>
/// Separate from the mobile policy provider because the two run as different
/// database principals against different schemas. Sharing one implementation
/// would require one of them to hold permissions it must not have.
/// </remarks>
public interface IAdministratorPolicyProvider
{
    /// <summary>Returns the current portal policy.</summary>
    Task<AdministratorPolicy> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Configurable policy applied by the administration portal.
/// </summary>
/// <param name="LockoutThreshold">Failures before an administrator account locks.</param>
/// <param name="LockoutMinutes">How long that lock lasts.</param>
/// <param name="RequireMfa">
/// Whether an authenticator code is required to sign in (OPEN-34). Defaults to
/// required: the portal can revoke devices, approve replacements and change
/// attendance rules, so a stolen password alone must not be enough.
/// </param>
/// <param name="TotpStepTolerance">
/// Time steps either side of the current one accepted from an authenticator
/// (<c>Security.TotpStepTolerance</c>). See <see cref="ITotpVerifier.Verify"/>
/// for why raising it is a security decision.
/// </param>
public readonly record struct AdministratorPolicy(
    int LockoutThreshold,
    int LockoutMinutes,
    bool RequireMfa,
    int TotpStepTolerance);
