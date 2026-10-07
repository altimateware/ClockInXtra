using Attendance.Application.Abstractions;
using Attendance.Application.Services;
using Attendance.Domain.Enums;
using Attendance.Domain.Services;
using Attendance.Domain.ValueObjects;

namespace Attendance.Application.Features.Attendance;

/// <summary>
/// The clock-in use case (Claude.md §12).
/// </summary>
/// <remarks>
/// <para>
/// <b>The order of these checks is the security design, not a style choice.</b>
/// Reading it top to bottom:
/// </para>
/// <list type="number">
///   <item><b>Lockout first.</b> A locked account must not be able to spend the
///   server's PBKDF2 work, and must not learn anything from trying.</item>
///   <item><b>Location before credentials.</b> Location evaluation is arithmetic
///   on data already in hand; password verification costs hundreds of
///   milliseconds of deliberate work. Doing the cheap check first means an
///   attacker flooding the endpoint cannot force expensive hashing (§22). This is
///   why the lockout check is a separate call on
///   <see cref="EmployeeAuthenticator"/> rather than folded into it — the
///   location test has to sit between the two.</item>
///   <item><b>Both factors, then the time step.</b> Delegated to
///   <see cref="EmployeeAuthenticator"/>, which is shared with device
///   registration so that the one authentication sequence has one
///   implementation.</item>
///   <item><b>Idempotency claim, then the transaction.</b> The database owns the
///   attendance day, the duplicate check and the timestamp.</item>
/// </list>
/// <para>
/// The signature, the nonce and the device binding were already verified by the
/// API before this handler is reached; this handler never sees a request that
/// did not come from an active registered device bound to the claimed employee.
/// </para>
/// </remarks>
public sealed class ClockInHandler
{
    private readonly IAttendancePolicyProvider _policyProvider;
    private readonly EmployeeAuthenticator _authenticator;
    private readonly IOfficeLocationRepository _offices;
    private readonly IIdempotencyStore _idempotency;
    private readonly IAttendanceRepository _attendance;
    private readonly ISecurityEventRecorder _securityEvents;

    /// <summary>Creates the handler.</summary>
    public ClockInHandler(
        IAttendancePolicyProvider policyProvider,
        EmployeeAuthenticator authenticator,
        IOfficeLocationRepository offices,
        IIdempotencyStore idempotency,
        IAttendanceRepository attendance,
        ISecurityEventRecorder securityEvents)
    {
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(offices);
        ArgumentNullException.ThrowIfNull(idempotency);
        ArgumentNullException.ThrowIfNull(attendance);
        ArgumentNullException.ThrowIfNull(securityEvents);

        _policyProvider = policyProvider;
        _authenticator = authenticator;
        _offices = offices;
        _idempotency = idempotency;
        _attendance = attendance;
        _securityEvents = securityEvents;
    }

    /// <summary>Handles a clock-in request.</summary>
    public async Task<ClockInResponse> HandleAsync(ClockInCommand command, CancellationToken cancellationToken)
    {
        AttendancePolicy policy = await _policyProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        // 1. Lockout, before any work is spent on this request.
        LockoutState lockout = await _authenticator
            .CheckLockoutAsync(command.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (lockout.IsLockedOut)
        {
            await RecordAsync(command, "Auth.Locked", SecurityEventSeverity.Warning, "ACCOUNT_LOCKED", cancellationToken)
                .ConfigureAwait(false);

            return ClockInResponse.Failed(AttendanceResultCode.AccountLocked);
        }

        // 2. Location: cheap, and it decides the office the record will carry.
        LocationDecision location = LocationPolicy.Evaluate(
            command.Position,
            await _offices.GetActiveAsync(cancellationToken).ConfigureAwait(false),
            policy.AccuracyPolicy,
            policy.MaxAcceptedAccuracyMeters,
            policy.RejectUntrustedLocationSource);

        if (!location.IsAccepted)
        {
            await RecordAsync(
                    command,
                    "Location.Rejected",
                    SecurityEventSeverity.Warning,
                    ToReasonCode(location.Reason),
                    cancellationToken)
                .ConfigureAwait(false);

            return ClockInResponse.Failed(ToResultCode(location.Reason));
        }

        // 3. Password, authenticator code, and consumption of the time step.
        EmployeeAuthenticationResult authentication = await _authenticator
            .AuthenticateAsync(
                new EmployeeAuthenticationRequest(
                    command.UserId,
                    command.Password,
                    command.AuthenticatorCode,
                    command.DevicePublicId,
                    command.SourceAddressHash,
                    command.CorrelationId),
                policy,
                cancellationToken)
            .ConfigureAwait(false);

        if (!authentication.IsAuthenticated)
        {
            return ClockInResponse.Failed(authentication.ResultCode);
        }

        int mobileUserId = authentication.MobileUserId!.Value;

        // 4. Idempotency, then the transaction.
        IdempotencyClaim claim = await _idempotency
            .TryBeginAsync(
                command.DeviceId,
                command.IdempotencyKey,
                IdempotentEndpoint.ClockIn,
                command.RequestHash,
                cancellationToken)
            .ConfigureAwait(false);

        switch (claim.Disposition)
        {
            case IdempotencyDisposition.ReplayStoredResult:
                return ClockInResponse.Replayed(
                    (AttendanceResultCode)(claim.StoredResultCode ?? (int)AttendanceResultCode.InternalError),
                    claim.StoredResponsePayload);

            case IdempotencyDisposition.InProgress:
                return ClockInResponse.Failed(AttendanceResultCode.IdempotentInProgress);

            case IdempotencyDisposition.KeyReused:
                return ClockInResponse.Failed(AttendanceResultCode.IdempotencyKeyReuse);

            case IdempotencyDisposition.Proceed:
            default:
                break;
        }

        ClockInOutcome outcome = await _attendance
            .ClockInAsync(
                new ClockInRequest(
                    mobileUserId,
                    command.DeviceId,
                    new LocationEvidence(
                        location.OfficeLocationId!.Value,
                        (decimal)Math.Round(location.DistanceMeters ?? 0d, 2),
                        (decimal)Math.Round(command.Position.AccuracyMeters ?? 0d, 2),
                        (byte)command.Position.Platform,
                        command.Position.IsSourceUntrusted,
                        CoordinatesProtected: null),
                    command.CorrelationId),
                cancellationToken)
            .ConfigureAwait(false);

        await _idempotency
            .CompleteAsync(command.DeviceId, command.IdempotencyKey, (int)outcome.ResultCode, null, cancellationToken)
            .ConfigureAwait(false);

        return new ClockInResponse(
            outcome.ResultCode,
            outcome.AttendancePublicId,
            outcome.AttendanceDate,
            outcome.ClockInUtc,
            outcome.IsLateClockIn,
            WasReplayed: false,
            StoredResponsePayload: null);
    }

    private Task RecordAsync(
        ClockInCommand command,
        string eventType,
        SecurityEventSeverity severity,
        string reasonCode,
        CancellationToken cancellationToken) =>
        _securityEvents.RecordAsync(
            new SecurityEvent(
                eventType,
                severity,
                SecurityEventSubject.MobileUser,
                command.UserId,
                reasonCode,
                SourceApplication: "Attendance.Api",
                DevicePublicId: command.DevicePublicId,
                SourceAddressHash: command.SourceAddressHash,
                CorrelationId: command.CorrelationId),
            cancellationToken);

    private static AttendanceResultCode ToResultCode(LocationRejectionReason reason) => reason switch
    {
        LocationRejectionReason.AccuracyInsufficient => AttendanceResultCode.LocationAccuracyInsufficient,
        LocationRejectionReason.SourceUntrusted => AttendanceResultCode.LocationSourceUntrusted,
        _ => AttendanceResultCode.LocationNotAllowed,
    };

    private static string ToReasonCode(LocationRejectionReason reason) => reason switch
    {
        LocationRejectionReason.AccuracyInsufficient => "LOCATION_ACCURACY_INSUFFICIENT",
        LocationRejectionReason.SourceUntrusted => "LOCATION_SOURCE_UNTRUSTED",
        LocationRejectionReason.NoActiveOfficeLocations => "NO_ACTIVE_OFFICE_LOCATIONS",
        _ => "LOCATION_NOT_ALLOWED",
    };
}

/// <summary>
/// A clock-in request, after the API has verified the signature and device.
/// </summary>
/// <param name="UserId">The identifier the employee typed.</param>
/// <param name="Password">Verified and discarded; never stored or logged (§24).</param>
/// <param name="AuthenticatorCode">The six-digit code.</param>
/// <param name="DeviceId">The registered device that signed the request.</param>
/// <param name="DevicePublicId">Its external identifier, for the audit trail.</param>
/// <param name="Position">The position the device reported.</param>
/// <param name="IdempotencyKey">Client-generated key making retries safe.</param>
/// <param name="RequestHash">Hash of the meaningful request fields.</param>
/// <param name="CorrelationId">Ties logs and audit entries together.</param>
/// <param name="SourceAddressHash">Salted hash of the client address.</param>
public readonly record struct ClockInCommand(
    string UserId,
    string Password,
    string AuthenticatorCode,
    int DeviceId,
    Guid DevicePublicId,
    ReportedPosition Position,
    Guid IdempotencyKey,
    ReadOnlyMemory<byte> RequestHash,
    Guid CorrelationId,
    byte[]? SourceAddressHash);

/// <summary>The outcome of a clock-in.</summary>
/// <param name="ResultCode">The outcome to map to an API response.</param>
/// <param name="AttendancePublicId">The record created, on success.</param>
/// <param name="AttendanceDate">The business-local attendance date.</param>
/// <param name="ClockInUtc">The server-assigned timestamp.</param>
/// <param name="IsLateClockIn">Late flag, or null when the rule is unconfigured.</param>
/// <param name="WasReplayed">Whether this is a stored answer to a retry.</param>
/// <param name="StoredResponsePayload">The original response, when replayed.</param>
public readonly record struct ClockInResponse(
    AttendanceResultCode ResultCode,
    Guid? AttendancePublicId,
    DateOnly? AttendanceDate,
    DateTimeOffset? ClockInUtc,
    bool? IsLateClockIn,
    bool WasReplayed,
    string? StoredResponsePayload)
{
    /// <summary>A refusal carrying only its code.</summary>
    public static ClockInResponse Failed(AttendanceResultCode resultCode) =>
        new(resultCode, null, null, null, null, false, null);

    /// <summary>The stored answer to a repeated request.</summary>
    public static ClockInResponse Replayed(AttendanceResultCode resultCode, string? payload) =>
        new(resultCode, null, null, null, null, true, payload);
}
