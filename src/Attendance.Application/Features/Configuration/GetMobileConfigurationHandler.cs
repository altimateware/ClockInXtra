using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;

namespace Attendance.Application.Features.Configuration;

/// <summary>
/// Returns the runtime configuration the mobile application is allowed to read.
/// </summary>
/// <remarks>
/// <para>
/// The settings come from a database allow-list, so this handler adds no
/// filtering of its own — there is exactly one place that decides what an
/// untrusted device may know, and duplicating the decision here would create a
/// second one to keep in step.
/// </para>
/// <para>
/// The configured flags matter more than they look. Thirteen business settings
/// are deliberately unset until the business owner decides them, and attendance
/// refuses rather than assuming a value (§15, §68). Without these flags the app
/// could only discover that by attempting a clock-in and receiving
/// ATTENDANCE_NOT_CONFIGURED; with them it can explain the situation before
/// somebody is standing at a door relying on it.
/// </para>
/// </remarks>
public sealed class GetMobileConfigurationHandler
{
    private readonly IMobileConfigurationRepository _configuration;

    /// <summary>Creates the handler.</summary>
    public GetMobileConfigurationHandler(IMobileConfigurationRepository configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    /// <summary>Returns the mobile runtime configuration.</summary>
    public async Task<MobileConfigurationResponse> HandleAsync(CancellationToken cancellationToken)
    {
        MobileRuntimeConfiguration configuration = await _configuration
            .GetRuntimeConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);

        return new MobileConfigurationResponse(
            AttendanceResultCode.Success,
            configuration.Settings,
            configuration.ClockInConfigured,
            configuration.ClockOutConfigured,
            configuration.ServerTimeUtc);
    }
}

/// <summary>The configuration a mobile client may read.</summary>
/// <param name="ResultCode">The outcome to map to an API response.</param>
/// <param name="Settings">The mobile-visible settings.</param>
/// <param name="ClockInConfigured">Whether clock-in can operate at all.</param>
/// <param name="ClockOutConfigured">Whether clock-out can operate at all.</param>
/// <param name="ServerTimeUtc">Authoritative server time.</param>
public readonly record struct MobileConfigurationResponse(
    AttendanceResultCode ResultCode,
    IReadOnlyList<MobileSetting> Settings,
    bool ClockInConfigured,
    bool ClockOutConfigured,
    DateTimeOffset ServerTimeUtc);
