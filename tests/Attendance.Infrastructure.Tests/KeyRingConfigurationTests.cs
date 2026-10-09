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

        // What follows differs by platform, and both answers have to be
        // actionable. On Windows the store opens and the certificate simply is
        // not in it. On Linux it cannot be opened at all — .NET limits
        // LocalMachine to the Root and CertificateAuthority stores — so a
        // thumbprint can never resolve there however it is spelled, and the
        // message has to say what to use instead rather than send an operator
        // looking for a certificate that could not have been found.
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("not found", refused.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("cannot be opened on this platform", refused.Message, StringComparison.Ordinal);
            Assert.Contains("DataProtection:CertificatePath", refused.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The route a Linux host has to take: a PKCS#12 file instead of a store
    /// lookup. Exercised here because a thumbprint cannot work there at all.
    /// </summary>
    [Fact]
    public void ACertificateFileThatDoesNotExistIsReportedByPath()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"clockinxtra-absent-{Guid.NewGuid():N}.pfx");

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:KeyRingPath"] = Path.Combine(Path.GetTempPath(), "clockinxtra-keyring-test"),
                ["DataProtection:CertificatePath"] = missing,
            })
            .Build();

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            KeyRingConfiguration.AddSharedKeyRing(
                new ServiceCollection(), configuration, new TestEnvironment(Environments.Production)));

        Assert.Contains(missing, refused.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACertificatePathSatisfiesTheRequirementThatOneExists()
    {
        // Validation must accept either route. If it only knew about
        // thumbprints, a correctly configured Linux host would be refused at
        // startup for a certificate it had in fact supplied.
        KeyRingOptions options = new()
        {
            KeyRingPath = "/var/lib/clockinxtra/keyring",
            CertificatePath = "/etc/clockinxtra/keyring.pfx",
        };

        Assert.Empty(KeyRingConfiguration.Validate(options, isDevelopment: false));
    }

    [Fact]
    public void NeitherAThumbprintNorAPathIsRefused()
    {
        KeyRingOptions options = new() { KeyRingPath = "/var/lib/clockinxtra/keyring" };

        string problem = Assert.Single(KeyRingConfiguration.Validate(options, isDevelopment: false));

        Assert.Contains("CertificatePath", problem, StringComparison.Ordinal);
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Attendance.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
