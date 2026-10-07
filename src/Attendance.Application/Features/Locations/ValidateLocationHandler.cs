using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Domain.Services;
using Attendance.Domain.ValueObjects;

namespace Attendance.Application.Features.Locations;

/// <summary>
/// The startup location check (Claude.md §8.1 and §9).
/// </summary>
/// <remarks>
/// <para>
/// The application calls this before showing any attendance functionality. It is
/// a <b>proximity control, not proof of presence</b>: a device can report
/// manipulated coordinates, and consumer GPS cannot reliably distinguish five
/// metres in all environments (§65). Passing it is a precondition for using the
/// app, never evidence that an employee was in the building.
/// </para>
/// <para>
/// <b>This endpoint can be reached before a device is registered</b>, because the
/// spec's startup sequence runs it before the employee has enrolled. That makes
/// it the one mobile endpoint an unauthenticated caller can reach, and it shapes
/// what comes back:
/// </para>
/// <list type="bullet">
///   <item>An unauthenticated caller is told only whether the position was
///   accepted. It never receives the office identifier or the measured distance —
///   with either, the endpoint becomes a search oracle: submit coordinates, watch
///   the answer, and walk the boundary until you have located every office to
///   within a few metres. Publishing an organisation's site coordinates to
///   anonymous callers is not something this check needs to do (§9, §63).</item>
///   <item>A caller whose request was signed by a registered device does receive
///   the office identifier, because it already has a relationship with the system
///   and the app uses it to show which office it matched.</item>
/// </list>
/// <para>
/// The distance is never returned to anyone. It exists in the security trail and
/// in the attendance record, where it is evidence, not in a response where it
/// would be a range-finder.
/// </para>
/// </remarks>
public sealed class ValidateLocationHandler
{
    private readonly IAttendancePolicyProvider _policyProvider;
    private readonly IOfficeLocationRepository _offices;
    private readonly ISecurityEventRecorder _securityEvents;

    /// <summary>Creates the handler.</summary>
    public ValidateLocationHandler(
        IAttendancePolicyProvider policyProvider,
        IOfficeLocationRepository offices,
        ISecurityEventRecorder securityEvents)
    {
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(offices);
        ArgumentNullException.ThrowIfNull(securityEvents);

        _policyProvider = policyProvider;
        _offices = offices;
        _securityEvents = securityEvents;
    }

    /// <summary>Evaluates a reported position against the approved offices.</summary>
    public async Task<ValidateLocationResponse> HandleAsync(
        ValidateLocationCommand command,
        CancellationToken cancellationToken)
    {
        AttendancePolicy policy = await _policyProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        LocationDecision decision = LocationPolicy.Evaluate(
            command.Position,
            await _offices.GetActiveAsync(cancellationToken).ConfigureAwait(false),
            policy.AccuracyPolicy,
            policy.MaxAcceptedAccuracyMeters,
            policy.RejectUntrustedLocationSource);

        if (!decision.IsAccepted)
        {
            await _securityEvents
                .RecordAsync(
                    new SecurityEvent(
                        "Location.ValidationFailed",
                        // A mocked position is somebody trying, not somebody lost.
                        decision.Reason == LocationRejectionReason.SourceUntrusted
                            ? SecurityEventSeverity.Critical
                            : SecurityEventSeverity.Warning,
                        command.DevicePublicId is null
                            ? SecurityEventSubject.Unknown
                            : SecurityEventSubject.Device,
                        command.DevicePublicId?.ToString(),
                        ToReasonCode(decision.Reason),
                        SourceApplication: "Attendance.Api",
                        DevicePublicId: command.DevicePublicId,
                        SourceAddressHash: command.SourceAddressHash,
                        CorrelationId: command.CorrelationId),
                    cancellationToken)
                .ConfigureAwait(false);

            return new ValidateLocationResponse(ToResultCode(decision.Reason), false, null);
        }

        return new ValidateLocationResponse(
            AttendanceResultCode.Success,
            true,
            command.IsAuthenticatedDevice ? decision.OfficeLocationId : null);
    }

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
/// A position to validate.
/// </summary>
/// <param name="Position">The position the device reported.</param>
/// <param name="DevicePublicId">
/// The device, when the caller has one. Absent on first run, before registration.
/// </param>
/// <param name="IsAuthenticatedDevice">
/// Whether the API verified the request's signature against a registered, active
/// device. Only then is the matched office identifier returned.
/// </param>
/// <param name="AppVersion">Untrusted client metadata.</param>
/// <param name="CorrelationId">Ties logs and audit entries together.</param>
/// <param name="SourceAddressHash">Salted hash of the client address.</param>
public readonly record struct ValidateLocationCommand(
    ReportedPosition Position,
    Guid? DevicePublicId,
    bool IsAuthenticatedDevice,
    string? AppVersion,
    Guid CorrelationId,
    byte[]? SourceAddressHash);

/// <summary>
/// The outcome of a location check.
/// </summary>
/// <param name="ResultCode">The outcome to map to an API response.</param>
/// <param name="IsValid">Whether the position was accepted.</param>
/// <param name="OfficeLocationId">
/// The matched office, returned only to an authenticated device. <c>null</c>
/// otherwise — see the remarks on <see cref="ValidateLocationHandler"/>.
/// </param>
public readonly record struct ValidateLocationResponse(
    AttendanceResultCode ResultCode,
    bool IsValid,
    int? OfficeLocationId);
