namespace Attendance.Application.Abstractions;

/// <summary>
/// Reads the settings the mobile application is allowed to know.
/// </summary>
/// <remarks>
/// <para>
/// Maps to <c>mobile.usp_ApplicationSetting_GetMobileRuntime</c>, which returns
/// only rows explicitly flagged as mobile-visible. <b>It is an allow-list, not a
/// filter of known-bad keys</b>, so a setting added later is invisible to the app
/// until somebody deliberately marks it visible.
/// </para>
/// <para>
/// That matters because the same table holds security parameters. An attacker who
/// learns the lockout threshold knows how many guesses are free; one who learns
/// the signature skew window knows how long a captured request stays replayable.
/// None of it is cryptographically secret, and none of it needs to be on an
/// untrusted device either.
/// </para>
/// </remarks>
public interface IMobileConfigurationRepository
{
    /// <summary>Returns the mobile-visible settings and the configured flags.</summary>
    Task<MobileRuntimeConfiguration> GetRuntimeConfigurationAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What the mobile application is told about its own configuration.
/// </summary>
/// <param name="Settings">The mobile-visible settings.</param>
/// <param name="ClockInConfigured">
/// Whether every business setting clock-in requires has a value. The app shows an
/// explanatory message instead of a button that cannot work — a poor thing to
/// discover at 08:00 with a queue behind you.
/// </param>
/// <param name="ClockOutConfigured">The same, for clock-out.</param>
/// <param name="ServerTimeUtc">Authoritative server time.</param>
public readonly record struct MobileRuntimeConfiguration(
    IReadOnlyList<MobileSetting> Settings,
    bool ClockInConfigured,
    bool ClockOutConfigured,
    DateTimeOffset ServerTimeUtc);

/// <summary>One setting the mobile application may read.</summary>
/// <param name="Key">The setting key.</param>
/// <param name="Value">Its value, or <c>null</c> when the business has not set it.</param>
/// <param name="DataType">How to interpret the value: bool, int, string, time.</param>
public readonly record struct MobileSetting(string Key, string? Value, string DataType);
