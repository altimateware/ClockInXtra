using Attendance.Domain.ValueObjects;

namespace Attendance.Api.Security;

/// <summary>
/// The device and employee a verified signature identifies.
/// </summary>
/// <remarks>
/// <para>
/// Placed on <c>HttpContext.Items</c> by the signature middleware once
/// verification has fully succeeded, and read by endpoints. <b>Endpoints must
/// take identity from here and nowhere else.</b>
/// </para>
/// <para>
/// In particular, a user identifier appearing in a request body is a claim, not
/// an identity. §6.2 step 9 is explicit: a body value is accepted only when it
/// matches this binding. The mobile application is an untrusted client (§65), and
/// the one thing it cannot forge is a signature from a key held in the device's
/// secure element.
/// </para>
/// </remarks>
/// <param name="DeviceId">Internal device identifier.</param>
/// <param name="DevicePublicId">External identifier, safe to log.</param>
/// <param name="MobileUserId">The employee the device is bound to.</param>
/// <param name="UserId">That employee's sign-in identifier.</param>
/// <param name="Platform">Android or iOS.</param>
/// <param name="AppVersion">Last reported version; never an authorization input.</param>
public sealed record AuthenticatedDevice(
    int DeviceId,
    Guid DevicePublicId,
    int MobileUserId,
    string UserId,
    DevicePlatform Platform,
    string? AppVersion)
{
    /// <summary>The <c>HttpContext.Items</c> key.</summary>
    public const string ContextKey = "ClockInXtra.AuthenticatedDevice";
}

/// <summary>
/// The correlation identifier for a request.
/// </summary>
public static class CorrelationContext
{
    /// <summary>The <c>HttpContext.Items</c> key.</summary>
    public const string ContextKey = "ClockInXtra.CorrelationId";

    /// <summary>The header clients may supply, and that is always echoed.</summary>
    public const string HeaderName = "X-Correlation-Id";
}
