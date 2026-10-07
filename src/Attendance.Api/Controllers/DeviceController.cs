using Asp.Versioning;
using Attendance.Api.Configuration;
using Attendance.Api.Contracts;
using Attendance.Api.Security;
using Attendance.Application.Abstractions;
using Attendance.Application.Features.Devices;
using Attendance.Domain.Enums;
using Attendance.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Attendance.Api.Controllers;

/// <summary>
/// Device registration and status (§18).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/mobile/device")]
[EnableRateLimiting(RateLimitPolicies.Registration)]
public sealed class DeviceController : MobileControllerBase
{
    private readonly DeviceRegistrationHandler _registration;
    private readonly DeviceStatusHandler _status;

    /// <summary>Creates the controller.</summary>
    public DeviceController(DeviceRegistrationHandler registration, DeviceStatusHandler status)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(status);

        _registration = registration;
        _status = status;
    }

    /// <summary>Issues a single-use registration challenge.</summary>
    /// <remarks>
    /// Unsigned, because the device has no key yet. No employee is referenced, so
    /// the endpoint cannot be used to discover whether an identifier exists.
    /// </remarks>
    [HttpPost("registration/challenge")]
    [AllowUnsignedRequest]
    [ProducesResponseType(typeof(ChallengeResponseBody), StatusCodes.Status200OK)]
    public async Task<IActionResult> IssueChallengeAsync(CancellationToken cancellationToken)
    {
        IssueChallengeResponse response = await _registration.IssueChallengeAsync(
            new IssueChallengeCommand(SourceAddressHash: null, CorrelationId), cancellationToken)
            .ConfigureAwait(false);

        if (response.ResultCode != AttendanceResultCode.Success)
        {
            return Failure(response.ResultCode);
        }

        return Ok(new ChallengeResponseBody(
            response.ChallengeId,
            Convert.ToBase64String(response.Challenge),
            response.ExpiresUtc,
            CorrelationId.ToString()));
    }

    /// <summary>Registers a device.</summary>
    /// <remarks>
    /// Signed with the key being registered (<c>keyid="unregistered"</c>), whose
    /// public key is in the body. The middleware checks the signature's shape,
    /// freshness and body digest; proof of possession is verified by the handler,
    /// which has the presented key.
    /// </remarks>
    [HttpPost("register")]
    [AllowUnregisteredDevice]
    [ProducesResponseType(typeof(RegisterDeviceResponseBody), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RegisterDeviceResponseBody), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegisterAsync(
        [FromBody] RegisterDeviceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (HttpContext.Items[UnregisteredSignature.ContextKey] is not UnregisteredSignature signature)
        {
            return Failure(AttendanceResultCode.Unauthorized);
        }

        if (!TryDecode(request.Challenge, out byte[] challenge)
            || !TryDecode(request.PublicKey, out byte[] publicKey)
            || !TryDecode(request.Attestation, out byte[] attestation))
        {
            return Failure(AttendanceResultCode.InvalidRequest);
        }

        TryDecode(request.AttestationKeyId, out byte[] attestationKeyId);

        DeviceRegistrationResponse response = await _registration.RegisterAsync(
            new DeviceRegistrationCommand(
                request.UserId,
                request.Password,
                request.AuthenticatorCode,
                request.ChallengeId,
                challenge,
                publicKey,
                (DevicePlatform)request.Platform,
                attestation,
                attestationKeyId,
                signature.SignatureBase,
                signature.Signature,
                request.DeviceModel,
                request.OsVersion,
                request.AppVersion,
                CorrelationId,
                SourceAddressHash: null),
            cancellationToken).ConfigureAwait(false);

        RegisterDeviceResponseBody body = new(
            true, response.DevicePublicId, response.RequiresApproval, CorrelationId.ToString());

        return response.ResultCode switch
        {
            AttendanceResultCode.Success => Ok(body),

            // Not an error: the device exists and is waiting for a person.
            AttendanceResultCode.RegistrationPendingApproval => Accepted(body),

            _ => Failure(response.ResultCode),
        };
    }

    /// <summary>Returns the calling device's own registration state.</summary>
    /// <remarks>
    /// Reachable by a device that is not yet active — a pending device has to be
    /// able to ask whether it has been approved. The signature is still verified,
    /// so only the real device can ask about itself.
    /// </remarks>
    [HttpPost("status")]
    [AllowInactiveDevice]
    [EnableRateLimiting(RateLimitPolicies.Read)]
    [ProducesResponseType(typeof(DeviceStatusResponseBody), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        // This endpoint accepts a pending or revoked device, so it reads the key
        // id rather than requiring a fully authenticated one.
        if (CurrentDevice is not { } device)
        {
            return Failure(AttendanceResultCode.Unauthorized);
        }

        DeviceStatusResponse response = await _status.HandleAsync(
            new DeviceStatusCommand(device.DevicePublicId, CorrelationId), cancellationToken)
            .ConfigureAwait(false);

        if (response.ResultCode != AttendanceResultCode.Success)
        {
            return Failure(response.ResultCode);
        }

        return Ok(new DeviceStatusResponseBody(
            response.Status?.ToString() ?? nameof(DeviceStatus.Revoked),
            response.EmployeeActive,
            response.RevokedReason,
            response.ServerTimeUtc ?? DateTimeOffset.UtcNow,
            CorrelationId.ToString()));
    }

    private static bool TryDecode(string? value, out byte[] decoded)
    {
        decoded = [];

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            decoded = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
