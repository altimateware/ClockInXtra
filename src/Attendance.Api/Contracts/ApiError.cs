using System.Text.Json.Serialization;
using Attendance.Domain.Enums;

namespace Attendance.Api.Contracts;

/// <summary>
/// The single error shape every endpoint returns (Claude.md §34).
/// </summary>
/// <param name="Success">Always false. Present so a client can branch on one field.</param>
/// <param name="Code">A stable machine-readable code from the catalogue.</param>
/// <param name="Message">A sentence safe to show a person.</param>
/// <param name="CorrelationId">Ties the response to the server's logs.</param>
/// <remarks>
/// <b>What never appears here:</b> stack traces, SQL text, constraint names,
/// internal class names, connection details or cryptographic particulars. When
/// something unexpected happens, the detail is logged against the correlation id
/// and the caller receives INTERNAL_ERROR — the correlation id is what lets
/// support join the two without publishing the internals to the internet.
/// </remarks>
public readonly record struct ApiError(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("correlationId")] string CorrelationId)
{
    /// <summary>Creates an error body.</summary>
    public static ApiError Create(string code, string message, Guid correlationId) =>
        new(false, code, message, correlationId.ToString());
}

/// <summary>
/// Maps result codes to the HTTP status and error code a client receives.
/// </summary>
/// <remarks>
/// <para>
/// <b>This mapping is where internal codes stop being internal.</b> Several
/// result codes must never reach a client: <see cref="AttendanceResultCode.InvalidOtp"/>
/// and <see cref="AttendanceResultCode.OtpReplayed"/> both become
/// INVALID_CREDENTIALS, because telling an attacker that the password was right
/// and only the code was wrong is precisely the confirmation an internet-facing
/// endpoint must not give (CON-09, OPEN-38). The precise reason is already in the
/// security event trail.
/// </para>
/// <para>
/// The messages are deliberately plain. A message is read by an employee at a
/// door, usually in a hurry.
/// </para>
/// </remarks>
public static class ApiErrorCatalogue
{
    /// <summary>Returns the HTTP status, error code and message for a result.</summary>
    public static (int Status, string Code, string Message) Map(AttendanceResultCode resultCode) =>
        resultCode switch
        {
            AttendanceResultCode.InvalidRequest =>
                (400, "INVALID_REQUEST", "The request was not valid."),
            AttendanceResultCode.Unauthorized =>
                (401, "UNAUTHORIZED", "The request could not be authenticated."),
            AttendanceResultCode.Forbidden =>
                (403, "FORBIDDEN", "This device is not permitted to perform that action."),
            AttendanceResultCode.RateLimited =>
                (429, "RATE_LIMITED", "Too many requests. Please wait and try again."),
            AttendanceResultCode.AppVersionUnsupported =>
                (426, "APP_VERSION_UNSUPPORTED", "This version of the app is no longer supported. Please update."),

            // Wrong user, wrong password, wrong code and a replayed code are one
            // answer on purpose.
            AttendanceResultCode.InvalidCredentials
                or AttendanceResultCode.InvalidOtp
                or AttendanceResultCode.OtpReplayed
                or AttendanceResultCode.UserInactive =>
                (401, "INVALID_CREDENTIALS", "The details entered were not correct."),

            AttendanceResultCode.AccountLocked =>
                (423, "ACCOUNT_LOCKED", "This account is temporarily locked. Please try again later or contact your administrator."),
            AttendanceResultCode.MfaNotEnrolled =>
                (403, "MFA_NOT_ENROLLED", "No authenticator is set up for this account. Please contact your administrator."),

            AttendanceResultCode.DeviceNotRegistered =>
                (401, "DEVICE_NOT_REGISTERED", "This device is not registered."),
            AttendanceResultCode.DeviceNotApproved =>
                (403, "DEVICE_NOT_APPROVED", "This device is waiting for administrator approval."),
            AttendanceResultCode.DeviceRevoked =>
                (403, "DEVICE_REVOKED", "This device has been revoked."),
            AttendanceResultCode.DeviceNotBoundToUser =>
                (403, "FORBIDDEN", "This device is not permitted to perform that action."),
            AttendanceResultCode.ActiveDeviceAlreadyExists =>
                (409, "ACTIVE_DEVICE_EXISTS", "This employee already has an active device. An administrator must approve a replacement."),
            AttendanceResultCode.AttestationRejected =>
                (403, "ATTESTATION_REJECTED", "This device could not be verified."),

            AttendanceResultCode.ReplayedRequest =>
                (401, "REPLAYED_REQUEST", "The request could not be authenticated."),
            AttendanceResultCode.ClockSkew =>
                (401, "CLOCK_SKEW", "This device's clock is too far from the server's. Please check its date and time."),
            AttendanceResultCode.IdempotencyKeyReuse =>
                (409, "IDEMPOTENCY_KEY_REUSED", "That request identifier was already used for a different request."),
            AttendanceResultCode.IdempotentInProgress =>
                (409, "REQUEST_IN_PROGRESS", "The previous attempt is still being processed. Please wait a moment."),

            AttendanceResultCode.LocationNotAllowed or AttendanceResultCode.OfficeLocationInactive =>
                (403, "LOCATION_NOT_ALLOWED", "You do not appear to be at an approved office location."),
            AttendanceResultCode.LocationAccuracyInsufficient =>
                (403, "LOCATION_ACCURACY_INSUFFICIENT", "Your location could not be determined accurately enough. Please move to an open area and try again."),
            AttendanceResultCode.LocationSourceUntrusted =>
                (403, "LOCATION_SOURCE_UNTRUSTED", "The reported location could not be trusted."),

            AttendanceResultCode.AlreadyClockedIn =>
                (409, "ALREADY_CLOCKED_IN", "You are already clocked in for today."),
            AttendanceResultCode.NotClockedIn or AttendanceResultCode.AlreadyClockedOut =>
                (409, "NOT_CLOCKED_IN", "There is no open attendance record to close."),
            AttendanceResultCode.AttendanceWindowClosed =>
                (409, "ATTENDANCE_WINDOW_CLOSED", "That action is outside the permitted hours."),
            AttendanceResultCode.AttendanceNotConfigured =>
                (503, "ATTENDANCE_NOT_CONFIGURED", "Attendance is not yet configured. Please contact your administrator."),
            AttendanceResultCode.AttendanceOperationFailed =>
                (500, "ATTENDANCE_OPERATION_FAILED", "The operation could not be completed. Please try again."),

            AttendanceResultCode.ChallengeInvalid or AttendanceResultCode.ChallengeExpired =>
                (400, "INVALID_REQUEST", "The registration attempt expired. Please start again."),
            AttendanceResultCode.PublicKeyAlreadyRegistered =>
                (409, "INVALID_REQUEST", "This device key is already registered."),
            AttendanceResultCode.MfaAlreadyEnrolled =>
                (409, "MFA_ALREADY_ENROLLED", "An authenticator is already enrolled for this account."),

            AttendanceResultCode.NotFound =>
                (404, "NOT_FOUND", "Not found."),
            AttendanceResultCode.ConcurrencyConflict =>
                (409, "CONCURRENCY_CONFLICT", "The record changed since it was loaded. Please reload and try again."),
            AttendanceResultCode.DuplicateName =>
                (409, "DUPLICATE_NAME", "That name is already in use."),
            AttendanceResultCode.SettingNotConfirmed =>
                (409, "SETTING_NOT_CONFIRMED", "That setting is awaiting business confirmation."),
            AttendanceResultCode.SeparationOfDutiesViolation
                or AttendanceResultCode.CorrectionsDisabled
                or AttendanceResultCode.LastAdministratorManager =>
                (403, "FORBIDDEN", "That action is not permitted."),
            AttendanceResultCode.InvalidTimeZone =>
                (400, "INVALID_REQUEST", "That time zone is not recognised by the server."),

            _ => (500, "INTERNAL_ERROR", "Something went wrong. Please try again."),
        };
}
