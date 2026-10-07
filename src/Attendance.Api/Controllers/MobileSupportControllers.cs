using Asp.Versioning;
using Attendance.Api.Configuration;
using Attendance.Api.Contracts;
using Attendance.Api.Security;
using Attendance.Application.Abstractions;
using Attendance.Application.Features.Attendance;
using Attendance.Application.Features.Configuration;
using Attendance.Application.Features.Locations;
using Attendance.Domain.Enums;
using Attendance.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Attendance.Api.Controllers;

/// <summary>
/// The startup location check (§8.1, §9).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/mobile/location")]
[EnableRateLimiting(RateLimitPolicies.Read)]
public sealed class LocationController : MobileControllerBase
{
    private readonly ValidateLocationHandler _handler;

    /// <summary>Creates the controller.</summary>
    public LocationController(ValidateLocationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    /// <summary>Validates a reported position against the approved offices.</summary>
    /// <remarks>
    /// <para>
    /// Reachable before registration, because §8.1 runs this check before the
    /// employee has enrolled. That makes it the one mobile endpoint an
    /// unauthenticated caller can reach, so what comes back is deliberately thin:
    /// accepted or refused, with the matched office returned only to a device
    /// whose signature verified, and the measured distance returned to nobody
    /// (OPEN-44).
    /// </para>
    /// <para>
    /// This is a proximity control, not proof of presence. Passing it is a
    /// precondition for using the app, never evidence that someone was in the
    /// building (§65).
    /// </para>
    /// </remarks>
    [HttpPost("validate")]
    [AllowUnsignedRequest]
    [ProducesResponseType(typeof(ValidateLocationResponseBody), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ValidateAsync(
        [FromBody] ValidateLocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        AuthenticatedDevice? device = CurrentDevice;

        ValidateLocationResponse response = await _handler.HandleAsync(
            new ValidateLocationCommand(
                ToPosition(request.Position, device?.Platform ?? DevicePlatform.Unknown),
                device?.DevicePublicId,
                device is not null,
                request.AppVersion,
                CorrelationId,
                SourceAddressHash: null),
            cancellationToken).ConfigureAwait(false);

        if (!response.IsValid)
        {
            return Failure(response.ResultCode);
        }

        return Ok(new ValidateLocationResponseBody(
            true, response.OfficeLocationId, CorrelationId.ToString()));
    }
}

/// <summary>
/// Whether the employee is currently clocked in (§11).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/mobile/user")]
[EnableRateLimiting(RateLimitPolicies.Read)]
public sealed class UserController : MobileControllerBase
{
    private readonly UserStatusHandler _handler;

    /// <summary>Creates the controller.</summary>
    public UserController(UserStatusHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    /// <summary>Returns the employee's attendance state for the current day.</summary>
    /// <remarks>
    /// The server is the source of truth. The application must not decide from
    /// its own stored state whether someone is clocked in — a reinstall, a
    /// restored backup or a second device would each produce a confident wrong
    /// answer.
    /// </remarks>
    [HttpPost("status")]
    [ProducesResponseType(typeof(AttendanceResponseBody), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (CurrentDevice is not { } device)
        {
            return Failure(AttendanceResultCode.Unauthorized);
        }

        UserStatusResponse response = await _handler.HandleAsync(
            new UserStatusCommand(device.MobileUserId, device.DeviceId, device.AppVersion, null, CorrelationId),
            cancellationToken).ConfigureAwait(false);

        if (response.ResultCode != AttendanceResultCode.Success)
        {
            return Failure(response.ResultCode);
        }

        return Ok(new AttendanceResponseBody(
            true,
            response.State.ToString(),
            null,
            response.AttendanceDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            response.ClockInUtc,
            response.ClockOutUtc,
            response.DurationMinutes,
            null,
            null,
            response.ServerTimeUtc,
            CorrelationId.ToString()));
    }
}

/// <summary>
/// The runtime configuration a mobile client may read.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/mobile/app")]
[EnableRateLimiting(RateLimitPolicies.Read)]
public sealed class AppConfigController : MobileControllerBase
{
    private readonly GetMobileConfigurationHandler _handler;

    /// <summary>Creates the controller.</summary>
    public AppConfigController(GetMobileConfigurationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    /// <summary>Returns the allow-listed settings and the configured flags.</summary>
    /// <remarks>
    /// The configured flags let the app explain that attendance is not yet set up
    /// rather than offering a button that cannot work — a poor thing to discover
    /// at 08:00 with a queue behind you.
    /// </remarks>
    [HttpGet("config")]
    [AllowUnsignedRequest]
    [ProducesResponseType(typeof(AppConfigResponseBody), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
    {
        MobileConfigurationResponse response =
            await _handler.HandleAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new AppConfigResponseBody(
            response.Settings.ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal),
            response.ClockInConfigured,
            response.ClockOutConfigured,
            response.ServerTimeUtc,
            CorrelationId.ToString()));
    }
}
