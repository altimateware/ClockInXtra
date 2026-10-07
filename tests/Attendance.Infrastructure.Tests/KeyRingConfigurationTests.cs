using Attendance.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// The key ring that encrypts every TOTP secret must be shared and encrypted
/// outside Development, and a host must refuse to start otherwise.
/// </summary>
public sealed class KeyRingConfigurationTests
{
    [Fact]
    public void DevelopmentNeedsNeitherAShareNorACertificate()
    {
        Assert.Empty(KeyRingConfiguration.Validate(new KeyRingOptions(), isDevelopment: true));
    }

    [Fact]
    public void ProductionWithNothingConfiguredReportsBothProblems()
    {
        IReadOnlyList<string> problems = KeyRingConfiguration.Validate(new KeyRingOptions(), isDevelopment: false);

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, problem => problem.Contains("KeyRingPath", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("CertificateThumbprint", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionWithAShareButNoCertificateIsRefused()
    {
        // The dangerous half-configuration: the framework would write the keys
        // to the share as plain XML.
        KeyRingOptions options = new() { KeyRingPath = @"\\keys01\clockinxtra" };

        IReadOnlyList<string> problems = KeyRingConfiguration.Validate(options, isDevelopment: false);

        string problem = Assert.Single(problems);
        Assert.Contains("unencrypted", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionWithBothIsAccepted()
    {
        KeyRingOptions options = new()
        {
            KeyRingPath = @"\\keys01\clockinxtra",
            CertificateThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567",
        };

        Assert.Empty(KeyRingConfiguration.Validate(options, isDevelopment: false));
    }

    [Fact]
    public void AProductionHostWithoutAKeyRingRefusesToStart()
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            KeyRingConfiguration.AddSharedKeyRing(
                new ServiceCollection(),
                new ConfigurationBuilder().Build(),
                new TestEnvironment(Environments.Production)));

        Assert.Contains("DataProtection:KeyRingPath", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACertificateThatIsNotInstalledIsReportedByThumbprint()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:KeyRingPath"] = Path.Combine(Path.GetTempPath(), "clockinxtra-keyring-test"),
                ["DataProtection:CertificateThumbprint"] = "00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF 00 11 22 33",
            })
            .Build();

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            KeyRingConfiguration.AddSharedKeyRing(
                new ServiceCollection(), configuration, new TestEnvironment(Environments.Production)));

        // Spaces, as copied from the certificate manager, are tolerated; the
        // message names the thumbprint the operator has to go and find.
        Assert.Contains("00112233445566778899AABBCCDDEEFF00112233", refused.Message, StringComparison.Ordinal);
        Assert.Contains("not found", refused.Message, StringComparison.Ordinal);
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Attendance.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
