namespace Attendance.Domain.Enums;

/// <summary>
/// The outcome of an attendance, device or administration operation.
/// </summary>
/// <remarks>
/// <para>
/// These values are the <c>@ResultCode</c> returned by every stored procedure,
/// and they are part of the contract between the database and the application.
/// The catalogue is documented in <c>docs/architecture/05-result-codes.md</c>,
/// which also maps each one to its API error code and HTTP status.
/// </para>
/// <para>
/// <b>Values never change once released.</b> Mobile clients already in the field
/// depend on them (Claude.md §35), so the catalogue is additive only.
/// </para>
/// <para>
/// Several values are internal and must never reach a mobile client:
/// <see cref="InvalidOtp"/> and <see cref="OtpReplayed"/> are reported to the
/// caller as <see cref="InvalidCredentials"/>, because distinguishing them on an
/// internet-facing endpoint would confirm to an attacker that a password was
/// correct. The precise reason is written to the security event trail instead.
/// </para>
/// </remarks>
public enum AttendanceResultCode
{
    /// <summary>The operation completed.</summary>
    Success = 0,

    // ---- Request level -----------------------------------------------------

    /// <summary>Validation failed, or the body digest did not match.</summary>
    InvalidRequest = 1001,

    /// <summary>Missing, malformed or invalid signature, or an unknown key id.</summary>
    Unauthorized = 1002,

    /// <summary>The authenticated device may not act for this subject.</summary>
    Forbidden = 1003,

    /// <summary>The endpoint rate limit was exceeded.</summary>
    RateLimited = 1004,

    /// <summary>The application version is below the configured minimum.</summary>
    AppVersionUnsupported = 1005,

    // ---- Identity and credentials ------------------------------------------

    /// <summary>Wrong user, password or OTP. Deliberately indistinguishable.</summary>
    InvalidCredentials = 1010,

    /// <summary>The lockout threshold has been reached.</summary>
    AccountLocked = 1011,

    /// <summary>Internal only: the authenticator code was wrong.</summary>
    InvalidOtp = 1012,

    /// <summary>Internal only: that authenticator time step was already used.</summary>
    OtpReplayed = 1013,

    /// <summary>The employee is inactive or suspended.</summary>
    UserInactive = 1014,

    /// <summary>No active authenticator enrolment exists for the employee.</summary>
    MfaNotEnrolled = 1015,

    // ---- Device -------------------------------------------------------------

    /// <summary>No device matches the presented key id.</summary>
    DeviceNotRegistered = 1020,

    /// <summary>The registration is still awaiting administrator approval.</summary>
    DeviceNotApproved = 1021,

    /// <summary>The device has been revoked.</summary>
    DeviceRevoked = 1022,

    /// <summary>The device is bound to a different employee.</summary>
    DeviceNotBoundToUser = 1023,

    /// <summary>The employee already has an active device (DEC-04).</summary>
    ActiveDeviceAlreadyExists = 1024,

    /// <summary>Attestation was missing, malformed, or below the required level.</summary>
    AttestationRejected = 1025,

    // ---- Request integrity --------------------------------------------------

    /// <summary>The signature nonce has already been used.</summary>
    ReplayedRequest = 1030,

    /// <summary>The signature timestamp is outside the accepted skew window.</summary>
    ClockSkew = 1031,

    /// <summary>The idempotency key was reused for a different request.</summary>
    IdempotencyKeyReuse = 1032,

    /// <summary>Not an error: the original request's stored result is returned.</summary>
    IdempotentReplay = 1033,

    /// <summary>The original request carrying this key is still running.</summary>
    IdempotentInProgress = 1034,

    // ---- Location -----------------------------------------------------------

    /// <summary>No approved office lies within its configured radius.</summary>
    LocationNotAllowed = 1040,

    /// <summary>Reported accuracy is worse than policy allows.</summary>
    LocationAccuracyInsufficient = 1041,

    /// <summary>The position was mocked or software-simulated.</summary>
    LocationSourceUntrusted = 1042,

    /// <summary>The matched office was disabled before the transaction ran.</summary>
    OfficeLocationInactive = 1043,

    // ---- Attendance ---------------------------------------------------------

    /// <summary>A record already exists for this employee and attendance day.</summary>
    AlreadyClockedIn = 1050,

    /// <summary>There is no open record to close.</summary>
    NotClockedIn = 1051,

    /// <summary>Outside the configured window, with the action set to Reject.</summary>
    AttendanceWindowClosed = 1052,

    /// <summary>
    /// A mandatory business setting is unset, so the system refuses rather than
    /// assuming a value (Claude.md §15 and §68).
    /// </summary>
    AttendanceNotConfigured = 1053,

    /// <summary>The attendance transaction could not be completed.</summary>
    AttendanceOperationFailed = 1054,

    /// <summary>The record for the day is already closed.</summary>
    AlreadyClockedOut = 1055,

    // ---- Registration and enrolment -----------------------------------------

    /// <summary>Unknown or already consumed registration challenge.</summary>
    ChallengeInvalid = 1060,

    /// <summary>The challenge lifetime has passed.</summary>
    ChallengeExpired = 1061,

    /// <summary>Not an error: registration accepted, awaiting approval.</summary>
    RegistrationPendingApproval = 1062,

    /// <summary>That public key is already bound to a device.</summary>
    PublicKeyAlreadyRegistered = 1063,

    /// <summary>The employee already has a pending or active authenticator.</summary>
    MfaAlreadyEnrolled = 1064,

    // ---- Administration -----------------------------------------------------

    /// <summary>The entity does not exist.</summary>
    NotFound = 1070,

    /// <summary>The row changed since it was loaded (rowversion mismatch).</summary>
    ConcurrencyConflict = 1071,

    /// <summary>A unique name is already in use.</summary>
    DuplicateName = 1072,

    /// <summary>The setting value still awaits business confirmation.</summary>
    SettingNotConfirmed = 1073,

    /// <summary>
    /// An administrator may not act on their own request or their own account —
    /// approving their own correction, or changing their own status, roles,
    /// password (other than by self-service change) or authenticator.
    /// </summary>
    SeparationOfDutiesViolation = 1074,

    /// <summary>Attendance corrections are not enabled (OPEN-10).</summary>
    CorrectionsDisabled = 1075,

    /// <summary>The value is not a time zone known to the database.</summary>
    InvalidTimeZone = 1076,

    /// <summary>
    /// The change would leave no active administrator able to manage
    /// administrators. Refused because setup will not run again once an
    /// administrator exists, so that state could only be escaped by editing the
    /// database directly.
    /// </summary>
    LastAdministratorManager = 1077,

    // ---- Internal -----------------------------------------------------------

    /// <summary>Unexpected failure. Details are logged, never returned.</summary>
    InternalError = 1090,
}
