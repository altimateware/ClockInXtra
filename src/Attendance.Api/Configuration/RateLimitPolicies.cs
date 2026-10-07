namespace Attendance.Api.Configuration;

/// <summary>
/// Rate limit policy names (§36).
/// </summary>
/// <remarks>
/// Named per endpoint class rather than one limit for everything, because the
/// abuse characteristics differ: registration is expensive and rare, status
/// polling is cheap and frequent, and attendance sits between them.
/// </remarks>
public static class RateLimitPolicies
{
    /// <summary>Clock-in and clock-out.</summary>
    public const string Attendance = "attendance";

    /// <summary>Device registration and challenge issue.</summary>
    public const string Registration = "registration";

    /// <summary>Status, configuration and location validation.</summary>
    public const string Read = "read";
}
