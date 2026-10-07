using Attendance.Infrastructure.Persistence.Connection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Attendance.Infrastructure.Diagnostics;

/// <summary>
/// Readiness check: can this instance reach the database?
/// </summary>
/// <remarks>
/// <para>
/// Separated from liveness on purpose. <b>Liveness</b> asks whether the process
/// should be restarted; <b>readiness</b> asks whether it should receive traffic.
/// A database outage means every node fails readiness — restarting them all would
/// turn a recoverable outage into a longer one.
/// </para>
/// <para>
/// The check opens a connection and does nothing else. It deliberately runs no
/// business query: a readiness probe that exercised attendance logic would write
/// rows, and a probe with side effects is a probe nobody dares run often.
/// </para>
/// </remarks>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the health check.</summary>
    public DatabaseHealthCheck(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqlConnectionLease lease =
                await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy("Database reachable.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The message is for operators reading the health endpoint on an
            // internal network, never for a mobile client.
            return HealthCheckResult.Unhealthy("Database unreachable.", exception);
        }
    }
}
