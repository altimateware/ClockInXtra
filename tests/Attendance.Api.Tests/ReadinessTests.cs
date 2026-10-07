using System.Net;
using System.Text.Json;
using Attendance.Api.Diagnostics;
using Attendance.Application.Abstractions;
using Attendance.Domain.Services;
using Attendance.Domain.ValueObjects;
using Attendance.Infrastructure.Diagnostics;
using Attendance.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Attendance.Api.Tests;

/// <summary>
/// The readiness probe reports what stops attendance, and what stops the node.
/// </summary>
public sealed class ReadinessTests
{
    private static readonly string[] ServingStatuses = ["Healthy", "Degraded"];

    private static readonly OfficeLocationCandidate Office =
        new(1, Coordinates.Create(6.465422, 3.406448), 5);

    [Fact]
    public async Task ConfiguredSettingsAndAnActiveOfficeAreHealthy()
    {
        HealthCheckResult result = await ConfigurationCheck(clockIn: true, clockOut: true, [Office]);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task UnsetSettingsAreDegradedNotUnhealthy()
    {
        // Every node shares the database; Unhealthy would pull them all out of the
        // load balancer and turn a configuration gap into an outage.
        HealthCheckResult result = await ConfigurationCheck(clockIn: false, clockOut: true, [Office]);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("clock-in", result.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("clock-out", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoActiveOfficeIsDegraded()
    {
        HealthCheckResult result = await ConfigurationCheck(clockIn: true, clockOut: true, []);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("no office location is active", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUsableKeyRingIsHealthy()
    {
        KeyRingHealthCheck check = new(new DataProtectionSecretProtector(
            new EphemeralDataProtectionProvider(), NullLogger<DataProtectionSecretProtector>.Instance));

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task AnUnusableKeyRingIsUnhealthy()
    {
        KeyRingHealthCheck check = new(new UnavailableProtector());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Key ring unavailable.", result.Description);
    }

    [Fact]
    public async Task TheProbeNamesEachCheckAndNeverAnException()
    {
        // The attendance-configuration status depends on shared settings other
        // test projects change while running, so only its presence is asserted.
        await using WebApplicationFactory<Program> factory = TestHost.Create();
        using HttpClient client = factory.Client();

        HttpResponseMessage response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        string json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument report = JsonDocument.Parse(json);
        JsonElement checks = report.RootElement.GetProperty("checks");

        Assert.Equal("Healthy", checks.GetProperty("database").GetProperty("status").GetString());
        Assert.Equal("Healthy", checks.GetProperty("key-ring").GetProperty("status").GetString());
        Assert.Contains(
            checks.GetProperty("attendance-configuration").GetProperty("status").GetString(),
            ServingStatuses);
        Assert.DoesNotContain("Exception", json, StringComparison.Ordinal);
    }

    private static Task<HealthCheckResult> ConfigurationCheck(
        bool clockIn, bool clockOut, IReadOnlyList<OfficeLocationCandidate> offices) =>
        new AttendanceConfigurationHealthCheck(
                new FixedConfiguration(new MobileRuntimeConfiguration([], clockIn, clockOut, DateTimeOffset.UtcNow)),
                new FixedOffices(offices))
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    private sealed class FixedConfiguration(MobileRuntimeConfiguration configuration) : IMobileConfigurationRepository
    {
        public Task<MobileRuntimeConfiguration> GetRuntimeConfigurationAsync(CancellationToken cancellationToken) =>
            Task.FromResult(configuration);
    }

    private sealed class FixedOffices(IReadOnlyList<OfficeLocationCandidate> offices) : IOfficeLocationRepository
    {
        public Task<IReadOnlyList<OfficeLocationCandidate>> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(offices);

        public void InvalidateCache()
        {
        }
    }

    private sealed class UnavailableProtector : ISecretProtector
    {
        public byte[] Protect(string purpose, ReadOnlySpan<byte> plaintext) =>
            throw new IOException(@"The network path \\keys01\clockinxtra-keyring was not found.");

        public bool TryUnprotect(string purpose, byte[] protectedPayload, out byte[] plaintext) =>
            throw new InvalidOperationException("unreachable");
    }
}
