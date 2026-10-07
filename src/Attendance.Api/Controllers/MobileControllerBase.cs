using System.Security.Cryptography;
using Attendance.Api.Contracts;
using Attendance.Api.Security;
using Attendance.Domain.Enums;
using Attendance.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Api.Controllers;

/// <summary>
/// Shared behaviour for the mobile endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity comes from the signature, never from the body.</b> Every endpoint
/// reads the authenticated device from <see cref="CurrentDevice"/>, which the
/// signature middleware populated only after a signature verified against a
/// registered, active device. A user identifier in a request body is a claim; it
/// is accepted only when it matches that binding (§6.2 step 9, §65).
/// </para>
/// </remarks>
[ApiController]
public abstract class MobileControllerBase : ControllerBase
{
    /// <summary>The correlation identifier assigned to this request.</summary>
    protected Guid CorrelationId =>
        HttpContext.Items[CorrelationContext.ContextKey] is Guid id ? id : Guid.Empty;

    /// <summary>
    /// The device the verified signature identifies, or <see langword="null"/>
    /// for an endpoint reachable without one.
    /// </summary>
    protected AuthenticatedDevice? CurrentDevice =>
        HttpContext.Items[AuthenticatedDevice.ContextKey] as AuthenticatedDevice;

    /// <summary>Converts a result code into the standard error response.</summary>
    protected IActionResult Failure(AttendanceResultCode resultCode)
    {
        (int status, string code, string message) = ApiErrorCatalogue.Map(resultCode);

        return StatusCode(status, ApiError.Create(code, message, CorrelationId));
    }

    /// <summary>
    /// Refuses a body whose user identifier does not match the device binding.
    /// </summary>
    /// <remarks>
    /// The comparison is ordinal. A case-insensitive match here would let one
    /// employee's device act as another whose identifier differs only in case —
    /// which the database permits, because identifiers are stored as given.
    /// </remarks>
    protected IActionResult? RefuseIfNotBoundTo(string userId, AuthenticatedDevice device) =>
        string.Equals(userId, device.UserId, StringComparison.Ordinal)
            ? null
            : Failure(AttendanceResultCode.Forbidden);

    /// <summary>
    /// The idempotency key a retry-safe endpoint requires.
    /// </summary>
    protected bool TryGetIdempotencyKey(out Guid key) =>
        Guid.TryParse(Request.Headers["Idempotency-Key"], out key) && key != Guid.Empty;

    /// <summary>
    /// A hash of the request body, distinguishing an honest retry from a key
    /// reused for a different request.
    /// </summary>
    /// <remarks>
    /// The body was buffered by the signature middleware so it could be digested,
    /// which is what makes reading it a second time here possible.
    /// </remarks>
    protected async Task<byte[]> ComputeRequestHashAsync(CancellationToken cancellationToken)
    {
        if (!Request.Body.CanSeek)
        {
            return SHA256.HashData(ReadOnlySpan<byte>.Empty);
        }

        Request.Body.Position = 0;

        using MemoryStream buffer = new();
        await Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        Request.Body.Position = 0;

        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>Converts a request position into the domain value object.</summary>
    protected static ReportedPosition ToPosition(PositionRequest position, DevicePlatform platform) =>
        new(Coordinates.Create(position.Latitude, position.Longitude),
            position.AccuracyMeters,
            platform,
            position.IsMocked);
}
