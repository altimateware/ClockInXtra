using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Domain.Services;
using Attendance.Domain.ValueObjects;

namespace Attendance.Application.Features.Attendance;

/// <summary>
/// The clock-out use case (Claude.md §14).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately simpler than clock-in: by the stated requirement, clock-out
/// needs no password and no authenticator code. A signed request from the bound
/// device, plus an acceptable location, is the whole of it (assumption ASM-04).
/// </para>
/// <para>
/// <b>The consequence is recorded, not hidden.</b> Anyone holding the employee's
/// unlocked phone can close their attendance record — residual risk RR-06. That
/// follows from the requirement as written; it is listed in the threat model for
/// the business owner to accept or to change the requirement.
/// </para>
/// </remarks>
public sealed class ClockOutHandler
{
    private readonly IAttendancePolicyProvider _policyProvider;
    private readonly IOfficeLocationRepository _offices;
    private readonly IIdempotencyStore _idempotency;
    private readonly IAttendanceRepository _attendance;
    private readonly ISecurityEventRecorder _securityEvents;

    /// <summary>Creates the handler.</summary>
    public ClockOutHandler(
        IAttendancePolicyProvider policyProvider,
        IOfficeLocationRepository offices,
        IIdempotencyStore idempotency,
        IAttendanceRepository attendance,
        ISecurityEventRecorder securityEvents)
    {
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(offices);
        ArgumentNullException.ThrowIfNull(idempotency);
        ArgumentNullException.ThrowIfNull(attendance);
        ArgumentNullException.ThrowIfNull(securityEvents);

        _policyProvider = policyProvider;
        _offices = offices;
        _idempotency = idempotency;
        _attendance = attendance;
        _securityEvents = securityEvents;
    }

    /// <summary>Handles a clock-out request.</summary>
    public async Task<ClockOutResponse> HandleAsync(ClockOutCommand command, CancellationToken cancellationToken)
    {
        AttendancePolicy policy = await _policyProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        LocationDecision location = LocationPolicy.Evaluate(
            command.Position,
            await _offices.GetActiveAsync(cancellationToken).ConfigureAwait(false),
            policy.AccuracyPolicy,
            policy.MaxAcceptedAccuracyMeters,
            policy.RejectUntrustedLocationSource);

        if (!location.IsAccepted)
        {
            await _securityEvents.RecordAsync(
                    new SecurityEvent(
                        "Location.Rejected",
                        SecurityEventSeverity.Warning,
                        SecurityEventSubject.Device,
                        command.DevicePublicId.ToString(),
                        ReasonCode: location.Reason switch
                        {
                            LocationRejectionReason.AccuracyInsufficient => "LOCATION_ACCURACY_INSUFFICIENT",
                            LocationRejectionReason.SourceUntrusted => "LOCATION_SOURCE_UNTRUSTED",
                            LocationRejectionReason.NoActiveOfficeLocations => "NO_ACTIVE_OFFICE_LOCATIONS",
                            _ => "LOCATION_NOT_ALLOWED",
                        },
                        SourceApplication: "Attendance.Api",
                        DevicePublicId: command.DevicePublicId,
                        SourceAddressHash: command.SourceAddressHash,
                        CorrelationId: command.CorrelationId),
                    cancellationToken)
                .ConfigureAwait(false);

            return ClockOutResponse.Failed(location.Reason switch
            {
                LocationRejectionReason.AccuracyInsufficient => AttendanceResultCode.LocationAccuracyInsufficient,
                LocationRejectionReason.SourceUntrusted => AttendanceResultCode.LocationSourceUntrusted,
                _ => AttendanceResultCode.LocationNotAllowed,
            });
        }

        IdempotencyClaim claim = await _idempotency
            .TryBeginAsync(
                command.DeviceId,
                command.IdempotencyKey,
                IdempotentEndpoint.ClockOut,
                command.RequestHash,
                cancellationToken)
            .ConfigureAwait(false);

        switch (claim.Disposition)
        {
            case IdempotencyDisposition.ReplayStoredResult:
                return ClockOutResponse.Replayed(
                    (AttendanceResultCode)(claim.StoredResultCode ?? (int)AttendanceResultCode.InternalError),
                    claim.StoredResponsePayload);

            case IdempotencyDisposition.InProgress:
                return ClockOutResponse.Failed(AttendanceResultCode.IdempotentInProgress);

            case IdempotencyDisposition.KeyReused:
                return ClockOutResponse.Failed(AttendanceResultCode.IdempotencyKeyReuse);

            case IdempotencyDisposition.Proceed:
            default:
                break;
        }

        ClockOutOutcome outcome = await _attendance
            .ClockOutAsync(
                new ClockOutRequest(
                    command.MobileUserId,
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

        return new ClockOutResponse(
            outcome.ResultCode,
            outcome.AttendancePublicId,
            outcome.AttendanceDate,
            outcome.ClockInUtc,
            outcome.ClockOutUtc,
            outcome.DurationMinutes,
            outcome.IsEarlyClockOut,
            WasReplayed: false,
            StoredResponsePayload: null);
    }
}

/// <summary>
/// A clock-out request, after the API has verified the signature and device.
/// </summary>
/// <param name="MobileUserId">The employee the device is bound to.</param>
/// <param name="DeviceId">The registered device that signed the request.</param>
/// <param name="DevicePublicId">Its external identifier, for the audit trail.</param>
/// <param name="Position">The position the device reported.</param>
/// <param name="IdempotencyKey">Client-generated key making retries safe.</param>
/// <param name="RequestHash">Hash of the meaningful request fields.</param>
/// <param name="CorrelationId">Ties logs and audit entries together.</param>
/// <param name="SourceAddressHash">Salted hash of the client address.</param>
public readonly record struct ClockOutCommand(
    int MobileUserId,
    int DeviceId,
    Guid DevicePublicId,
    ReportedPosition Position,
    Guid IdempotencyKey,
    ReadOnlyMemory<byte> RequestHash,
    Guid CorrelationId,
    byte[]? SourceAddressHash);

/// <summary>The outcome of a clock-out.</summary>
/// <param name="ResultCode">The outcome to map to an API response.</param>
/// <param name="AttendancePublicId">The record closed, on success.</param>
/// <param name="AttendanceDate">The business-local attendance date.</param>
/// <param name="ClockInUtc">When the record was opened.</param>
/// <param name="ClockOutUtc">The server-assigned closing timestamp.</param>
/// <param name="DurationMinutes">Duration computed by the database.</param>
/// <param name="IsEarlyClockOut">Early flag, or null when the rule is unconfigured.</param>
/// <param name="WasReplayed">Whether this is a stored answer to a retry.</param>
/// <param name="StoredResponsePayload">The original response, when replayed.</param>
public readonly record struct ClockOutResponse(
    AttendanceResultCode ResultCode,
    Guid? AttendancePublicId,
    DateOnly? AttendanceDate,
    DateTimeOffset? ClockInUtc,
    DateTimeOffset? ClockOutUtc,
    int? DurationMinutes,
    bool? IsEarlyClockOut,
    bool WasReplayed,
    string? StoredResponsePayload)
{
    /// <summary>A refusal carrying only its code.</summary>
    public static ClockOutResponse Failed(AttendanceResultCode resultCode) =>
        new(resultCode, null, null, null, null, null, null, false, null);

    /// <summary>The stored answer to a repeated request.</summary>
    public static ClockOutResponse Replayed(AttendanceResultCode resultCode, string? payload) =>
        new(resultCode, null, null, null, null, null, null, true, payload);
}
