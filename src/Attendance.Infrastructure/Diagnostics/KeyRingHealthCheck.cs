using System.Text;
using Attendance.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Attendance.Infrastructure.Diagnostics;

/// <summary>
/// Readiness: can this node use the shared Data Protection key ring?
/// </summary>
/// <remarks>
/// Startup proves the key ring is <em>configured</em>; this proves it is still
/// <em>usable</em> — the share reachable and the certificate's private key
/// readable. Without it every authenticator code fails to verify, so the node
/// should not receive traffic: Unhealthy. The round trip uses its own purpose
/// string, so it cannot touch any stored secret.
/// </remarks>
public sealed class KeyRingHealthCheck : IHealthCheck
{
    private const string Purpose = "ClockInXtra.HealthCheck.v1";

    private static readonly byte[] Probe = Encoding.ASCII.GetBytes("readiness");

    private readonly ISecretProtector _protector;

    /// <summary>Creates the check.</summary>
    public KeyRingHealthCheck(ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        _protector = protector;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] protectedProbe = _protector.Protect(Purpose, Probe);

            bool usable = _protector.TryUnprotect(Purpose, protectedProbe, out byte[] recovered)
                && recovered.AsSpan().SequenceEqual(Probe);

            return Task.FromResult(usable
                ? HealthCheckResult.Healthy("Key ring usable.")
                : HealthCheckResult.Unhealthy("Key ring cannot decrypt what it encrypted."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Key ring unavailable.", exception));
        }
    }
}
