using Attendance.Application.Abstractions;
using Attendance.Domain.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Attendance.Api.Diagnostics;

/// <summary>
/// Readiness: can attendance actually operate with the current business settings?
/// </summary>
/// <remarks>
/// <para>
/// Reports <b>Degraded</b>, not Unhealthy, when a required setting is unset or no
/// office is active. Every node reads the same database, so marking them all
/// unhealthy would take the whole API out of the load balancer — turning
/// "attendance refused with an explanation" (<c>ATTENDANCE_NOT_CONFIGURED</c>)
/// into an outage in which the app cannot even say why. Degraded answers 200
/// and tells the operator what is missing (architecture §13).
/// </para>
/// <para>
/// Uses the same procedure the mobile configuration endpoint uses, which mirrors
/// the conditions the attendance procedures check, so the probe and a real
/// clock-in cannot disagree.
/// </para>
/// </remarks>
public sealed class AttendanceConfigurationHealthCheck : IHealthCheck
{
    private readonly IMobileConfigurationRepository _configuration;
    private readonly IOfficeLocationRepository _offices;

    /// <summary>Creates the check.</summary>
    public AttendanceConfigurationHealthCheck(
        IMobileConfigurationRepository configuration,
        IOfficeLocationRepository offices)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(offices);
        _configuration = configuration;
        _offices = offices;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        MobileRuntimeConfiguration runtime =
            await _configuration.GetRuntimeConfigurationAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<OfficeLocationCandidate> offices =
            await _offices.GetActiveAsync(cancellationToken).ConfigureAwait(false);

        List<string> missing = [];

        if (!runtime.ClockInConfigured)
        {
            missing.Add("clock-in settings are not configured");
        }

        if (!runtime.ClockOutConfigured)
        {
            missing.Add("clock-out settings are not configured");
        }

        if (offices.Count == 0)
        {
            missing.Add("no office location is active");
        }

        return missing.Count == 0
            ? HealthCheckResult.Healthy("Attendance is configured.")
            : HealthCheckResult.Degraded("Attendance is refused: " + string.Join("; ", missing) + ".");
    }
}
