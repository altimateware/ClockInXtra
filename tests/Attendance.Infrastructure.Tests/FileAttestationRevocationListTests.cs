using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Attendance.Infrastructure.Security.Attestation;
using Microsoft.Extensions.Options;
using Xunit;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="FileAttestationRevocationList"/>, the real implementation.
/// </summary>
/// <remarks>
/// <para>
/// Every verifier test substitutes a stub for this class, which is why a defect
/// in it survived until a registration was attempted from a real emulator: an
/// empty configured path — what <c>appsettings.json</c> actually ships — made
/// every Android registration return 500. These tests exercise the class itself.
/// </para>
/// <para>
/// It sits on the registration path, which any caller can reach, so the property
/// tested throughout is that it never throws: a bad file must degrade to the
/// documented "nothing is revoked" behaviour, not to an error.
/// </para>
/// </remarks>
public sealed class FileAttestationRevocationListTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cix-rev-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnconfiguredPathRevokesNothingAndDoesNotThrow(string? path)
    {
        // "" is the case that failed in practice: configuration binding produces
        // an empty string from the shipped appsettings.json, never null.
        FileAttestationRevocationList list = Create(path);
        using X509Certificate2 certificate = CreateCertificate([0x2C, 0x8C, 0xDD]);

        bool revoked = list.IsRevoked([certificate], out string? serial);

        Assert.False(revoked);
        Assert.Null(serial);
        Assert.False(list.IsConfigured);
    }

    [Fact]
    public void ReportsARevokedCertificate()
    {
        string path = WriteList("""{ "entries": { "2c8cdd": { "status": "REVOKED", "reason": "KEY_COMPROMISE" } } }""");
        using X509Certificate2 certificate = CreateCertificate([0x2C, 0x8C, 0xDD]);

        bool revoked = Create(path).IsRevoked([certificate], out string? serial);

        Assert.True(revoked);
        Assert.Equal("2c8cdd", serial);
    }

    [Fact]
    public void MatchesSerialsDespiteLeadingZerosAndCase()
    {
        // X509Certificate2 renders 0x0A,0xBC as "0ABC"; Google keys the same
        // serial as "abc". A naive comparison would never match anything.
        string path = WriteList("""{ "entries": { "0ABC": { "status": "REVOKED" } } }""");
        using X509Certificate2 certificate = CreateCertificate([0x0A, 0xBC]);

        Assert.True(Create(path).IsRevoked([certificate], out _));
    }

    [Fact]
    public void AnyCertificateInTheChainCounts()
    {
        // A revoked intermediate condemns every key issued beneath it.
        string path = WriteList("""{ "entries": { "77": { "status": "REVOKED" } } }""");
        using X509Certificate2 leaf = CreateCertificate([0x11]);
        using X509Certificate2 intermediate = CreateCertificate([0x77]);

        Assert.True(Create(path).IsRevoked([leaf, intermediate], out string? serial));
        Assert.Equal("77", serial);
    }

    [Fact]
    public void ACertificateNotOnTheListPasses()
    {
        string path = WriteList("""{ "entries": { "ffff": { "status": "REVOKED" } } }""");
        using X509Certificate2 certificate = CreateCertificate([0x12, 0x34]);

        Assert.False(Create(path).IsRevoked([certificate], out _));
    }

    [Fact]
    public void AMissingFileRevokesNothingAndDoesNotThrow()
    {
        FileAttestationRevocationList list = Create(Path.Combine(_directory, "not-there.json"));
        using X509Certificate2 certificate = CreateCertificate([0x2C]);

        Assert.False(list.IsRevoked([certificate], out _));

        // Configured but unavailable is still configured: the gap is reported,
        // not hidden by pretending the setting is absent.
        Assert.True(list.IsConfigured);
    }

    [Fact]
    public void AMalformedFileRevokesNothingAndDoesNotThrow()
    {
        string path = WriteList("{ this is not json");
        using X509Certificate2 certificate = CreateCertificate([0x2C]);

        Assert.False(Create(path).IsRevoked([certificate], out _));
    }

    [Fact]
    public void PicksUpARefreshWithoutARestart()
    {
        // The operator refreshes the file on a schedule (§2.1: no live fetch), so
        // a newly revoked key must take effect on the next registration.
        string path = WriteList("""{ "entries": {} }""");
        using X509Certificate2 certificate = CreateCertificate([0x5E, 0xED]);
        FileAttestationRevocationList list = Create(path);

        Assert.False(list.IsRevoked([certificate], out _));

        File.WriteAllText(path, """{ "entries": { "5eed": { "status": "REVOKED" } } }""");

        // Set explicitly rather than relying on the clock: file systems record
        // write times coarsely, and two writes in one tick would look unchanged.
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        Assert.True(list.IsRevoked([certificate], out _));
    }

    [Fact]
    public void KeepsTheLastGoodListWhenARefreshIsCorrupt()
    {
        // A half-written refresh must not silently un-revoke every key that the
        // previous, valid copy had condemned.
        string path = WriteList("""{ "entries": { "bad1": { "status": "REVOKED" } } }""");
        using X509Certificate2 certificate = CreateCertificate([0xBA, 0xD1]);
        FileAttestationRevocationList list = Create(path);

        Assert.True(list.IsRevoked([certificate], out _));

        File.WriteAllText(path, "{ truncated");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        Assert.True(list.IsRevoked([certificate], out _));
    }

    private static FileAttestationRevocationList Create(string? path)
    {
        AttestationOptions options = new();
        options.Android.RevocationStatusListPath = path;

        return new FileAttestationRevocationList(Options.Create(options));
    }

    private string WriteList(string json)
    {
        string path = Path.Combine(_directory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static X509Certificate2 CreateCertificate(byte[] serial)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=revocation test", key, HashAlgorithmName.SHA256);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        return request.Create(
            request.SubjectName,
            X509SignatureGenerator.CreateForECDsa(key),
            now.AddDays(-1),
            now.AddDays(1),
            serial);
    }
}
