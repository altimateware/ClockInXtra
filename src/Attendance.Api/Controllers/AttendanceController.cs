using Asp.Versioning;
using Attendance.Api.Configuration;
using Attendance.Api.Contracts;
using Attendance.Api.Security;
using Attendance.Application.Abstractions;
using Attendance.Application.Features.Attendance;
using Attendance.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Attendance.Api.Controllers;

/// <summary>
/// Clock-in and clock-out (§12, §14).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/mobile/attendance")]
[EnableRateLimiting(RateLimitPolicies.Attendance)]
public sealed class AttendanceController : MobileControllerBase
{
    private readonly ClockInHandler _clockIn;
    private readonly ClockOutHandler _clockOut;

    /// <summary>Creates the controller.</summary>
    public AttendanceController(ClockInHandler clockIn, ClockOutHandler clockOut)
    {
        ArgumentNullException.ThrowIfNull(clockIn);
        ArgumentNullException.ThrowIfNull(clockOut);

        _clockIn = clockIn;
        _clockOut = clockOut;
    }

    /// <summary>Records a clock-in.</summary>
    /// <remarks>
    /// Requires the password and a six-digit authenticator code in addition to
    /// the signed request, and an <c>Idempotency-Key</c> header so a retry after
    /// a timeout cannot create a second record (§53).
    /// </remarks>
    [HttpPost("clock-in")]
    [ProducesResponseType(typeof(AttendanceResponseBody), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ClockInAsync(
        [FromBody] ClockInRequestBody request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CurrentDevice is not { } device)
        {
            return Failure(AttendanceResultCode.Unauthorized);
        }

        if (RefuseIfNotBoundTo(request.UserId, device) is { } refusal)
        {
            return refusal;
        }

        if (!TryGetIdempotencyKey(out Guid idempotencyKey))
        {
            return Failure(AttendanceResultCode.InvalidRequest);
        }

        ClockInResponse response = await _clockIn.HandleAsync(
            new ClockInCommand(
                request.UserId,
                request.Password,
                request.AuthenticatorCode,
                device.DeviceId,
                device.DevicePublicId,
                ToPosition(request.Position, device.Platform),
                idempotencyKey,
                await ComputeRequestHashAsync(cancellationToken).ConfigureAwait(false),
                CorrelationId,
                SourceAddressHash: null),
            cancellationToken).ConfigureAwait(false);

        if (response.ResultCode != AttendanceResultCode.Success)
        {
            return Failure(response.ResultCode);
        }

        return Ok(new AttendanceResponseBody(
            true,
            nameof(AttendanceState.ClockedIn),
            response.AttendancePublicId,
            response.AttendanceDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            response.ClockInUtc,
            null,
            null,
            response.IsLateClockIn,
            null,
            DateTimeOffset.UtcNow,
            CorrelationId.ToString()));
    }

    /// <summary>Records a clock-out.</summary>
    /// <remarks>
    /// By the stated requirement this needs neither password nor authenticator
    /// code — a signed request from the bound device plus an acceptable location
    /// is the whole of it (ASM-04). The consequence is recorded as residual risk
    /// RR-06 rather than quietly mitigated here.
    /// </remarks>
    [HttpPost("clock-out")]
    [ProducesResponseType(typeof(AttendanceResponseBody), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ClockOutAsync(
        [FromBody] ClockOutRequestBody request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CurrentDevice is not { } device)
        {
            return Failure(AttendanceResultCode.Unauthorized);
        }

        if (!TryGetIdempotencyKey(out Guid idempotencyKey))
        {
            return Failure(AttendanceResultCode.InvalidRequest);
        }

        ClockOutResponse response = await _clockOut.HandleAsync(
            new ClockOutCommand(
                device.MobileUserId,
                device.DeviceId,
                device.DevicePublicId,
                ToPosition(request.Position, device.Platform),
                idempotencyKey,
                await ComputeRequestHashAsync(cancellationToken).ConfigureAwait(false),
                CorrelationId,
                SourceAddressHash: null),
            cancellationToken).ConfigureAwait(false);

        if (response.ResultCode != AttendanceResultCode.Success)
        {
            return Failure(response.ResultCode);
        }

        return Ok(new AttendanceResponseBody(
            true,
            nameof(AttendanceState.Completed),
            response.AttendancePublicId,
            response.AttendanceDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            response.ClockInUtc,
            response.ClockOutUtc,
            response.DurationMinutes,
            null,
            response.IsEarlyClockOut,
            DateTimeOffset.UtcNow,
            CorrelationId.ToString()));
    }
}
