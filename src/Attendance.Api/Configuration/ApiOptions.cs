namespace Attendance.Api.Configuration;

/// <summary>
/// Hosting configuration for the mobile API.
/// </summary>
public sealed class ApiOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Api";

    /// <summary>
    /// Reverse proxy addresses whose forwarded headers are trusted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never wildcard.</b> The signature base includes <c>@authority</c> and
    /// <c>@path</c> as the client saw them, which behind a proxy means the
    /// forwarded headers. If any caller could set <c>X-Forwarded-Host</c>, an
    /// attacker could make a signature verify against a value the client never
    /// signed.
    /// </para>
    /// <para>
    /// Empty means forwarded headers are not processed at all, which is the safe
    /// default for a directly exposed host.
    /// </para>
    /// </remarks>
    public IList<string> KnownProxies { get; } = [];

    /// <summary>Rate limits, by endpoint class (§36).</summary>
    public RateLimitOptions RateLimits { get; set; } = new();
}

/// <summary>
/// Per-endpoint rate limits.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not one limit for everything. §36 asks for limits matched to each
/// endpoint's abuse characteristics: registration is the costliest and rarest,
/// status polling is cheap and frequent.
/// </para>
/// <para>
/// <b>These are first-line throttling, not the security control.</b> The limiter
/// counts per process, so with several IIS nodes an attacker can spread attempts
/// across them. Account lockout is counted in the database for exactly that
/// reason (decision TD-10, §45). This keeps obvious floods away from the
/// database; it does not replace the control.
/// </para>
/// </remarks>
public sealed class RateLimitOptions
{
    /// <summary>Requests per minute for clock-in and clock-out.</summary>
    public int AttendancePerMinute { get; set; } = 10;

    /// <summary>Requests per minute for device registration.</summary>
    public int RegistrationPerMinute { get; set; } = 5;

    /// <summary>Requests per minute for location validation and status.</summary>
    public int ReadPerMinute { get; set; } = 60;
}
