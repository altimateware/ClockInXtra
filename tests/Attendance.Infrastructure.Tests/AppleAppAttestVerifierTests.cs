using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Attendance.Application.Abstractions;
using Attendance.Domain.ValueObjects;
using Attendance.Infrastructure.Security.Attestation;
using Microsoft.Extensions.Options;
using Xunit;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AppleAppAttestVerifier"/> (DEC-05).
/// </summary>
public sealed class AppleAppAttestVerifierTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cix-{Guid.NewGuid():N}")).FullName;

    private readonly X509Certificate2 _root = AttestationTestBuilder.CreateRoot("CN=Apple Test Root");
    private readonly ECDsa _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _challenge = RandomNumberGenerator.GetBytes(32);

    public void Dispose()
    {
        _root.Dispose();
        _deviceKey.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AcceptsAWellFormedAttestation()
    {
        AttestationVerificationResult result = Verify(Build());

        Assert.True(result.IsAccepted);
        Assert.Equal(AttestationLevel.Hardware, result.Level);
    }

    [Fact]
    public void RefusesANonceDerivedFromADifferentChallenge()
    {
        // The nonce is SHA-256(authData ‖ SHA-256(challenge)), so a captured
        // attestation cannot be replayed against a fresh challenge.
        AttestationVerificationResult result = Verify(
            Build(nonceChallenge: RandomNumberGenerator.GetBytes(32)));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_NONCE_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void RefusesAnAttestationForADifferentKey()
    {
        using ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        AttestationVerificationResult result = Verify(
            Build(), publicKey: AttestationTestBuilder.UncompressedPoint(otherKey));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_KEY_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void RefusesAKeyIdentifierThatIsNotTheHashOfTheAttestedKey()
    {
        AttestationVerificationResult result = Verify(
            Build(), keyId: RandomNumberGenerator.GetBytes(32));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_KEY_ID_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void RefusesAnotherApplicationsBundle()
    {
        AttestationVerificationResult result = Verify(Build(bundleId: "com.attacker.app"));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_APPLICATION_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void RefusesAnotherTeam()
    {
        AttestationVerificationResult result = Verify(Build(teamId: "ZZZZZ99999"));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_APPLICATION_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void RefusesACounterThatIsNotZero()
    {
        // A freshly attested key has never been used. Anything else means this
        // is not a first attestation.
        AttestationVerificationResult result = Verify(Build(counter: 1));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_COUNTER_NOT_ZERO", result.ReasonCode);
    }

    [Fact]
    public void RefusesADevelopmentAttestationByDefault()
    {
        // A development attestation proves nothing about a production device.
        AttestationVerificationResult result = Verify(Build(development: true));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_DEVELOPMENT_ENVIRONMENT", result.ReasonCode);
    }

    [Fact]
    public void AcceptsADevelopmentAttestationOnlyWhenDeliberatelyEnabled()
    {
        AttestationVerificationResult result = Verify(
            Build(development: true),
            configure: o => o.Apple.AllowDevelopmentEnvironment = true);

        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void RefusesAChainThatDoesNotReachAppleRoot()
    {
        using X509Certificate2 foreignRoot = AttestationTestBuilder.CreateRoot("CN=Attacker Root");

        byte[] keyId = SHA256.HashData(AttestationTestBuilder.UncompressedPoint(_deviceKey));
        byte[] authenticatorData = AttestationTestBuilder.BuildAppleAuthenticatorData(keyId);
        byte[] nonce = AttestationTestBuilder.AppleNonce(authenticatorData, _challenge);

        using X509Certificate2 leaf = AttestationTestBuilder.IssueLeaf(
            foreignRoot, _deviceKey, AttestationTestBuilder.AppleNonceOid,
            AttestationTestBuilder.BuildAppleNonceExtension(nonce));

        byte[] attestation = AttestationTestBuilder.EncodeAppleAttestationObject(
            authenticatorData, "apple-appattest", leaf, foreignRoot);

        AttestationVerificationResult result = Verify(attestation);

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_CHAIN_UNTRUSTED", result.ReasonCode);
    }

    [Fact]
    public void RefusesAnUnexpectedAttestationFormat()
    {
        AttestationVerificationResult result = Verify(Build(format: "packed"));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_OBJECT_MALFORMED", result.ReasonCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(1024)]
    public void RefusesArbitraryBytesWithoutThrowing(int length)
    {
        AttestationVerificationResult result = Verify(RandomNumberGenerator.GetBytes(length));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_OBJECT_MALFORMED", result.ReasonCode);
    }

    [Fact]
    public void RefusesWhenTheVerifierIsNotConfigured()
    {
        AppleAppAttestVerifier verifier = new(Options.Create(new AttestationOptions()));

        AttestationVerificationResult result = verifier.Verify(
            new AttestationVerificationRequest(
                DevicePlatform.Ios, Build(), _challenge,
                AttestationTestBuilder.UncompressedPoint(_deviceKey), ReadOnlyMemory<byte>.Empty));

        Assert.False(result.IsAccepted);
        Assert.Equal("APPLE_ATTESTATION_NOT_CONFIGURED", result.ReasonCode);
    }

    // ---- Helpers -----------------------------------------------------------

    private byte[] Build(
        byte[]? nonceChallenge = null,
        string teamId = AttestationTestBuilder.TeamId,
        string bundleId = AttestationTestBuilder.BundleId,
        bool development = false,
        uint counter = 0,
        string format = "apple-appattest")
    {
        byte[] keyId = SHA256.HashData(AttestationTestBuilder.UncompressedPoint(_deviceKey));

        byte[] authenticatorData = AttestationTestBuilder.BuildAppleAuthenticatorData(
            keyId, teamId, bundleId, development, counter);

        byte[] nonce = AttestationTestBuilder.AppleNonce(authenticatorData, nonceChallenge ?? _challenge);

        using X509Certificate2 leaf = AttestationTestBuilder.IssueLeaf(
            _root, _deviceKey, AttestationTestBuilder.AppleNonceOid,
            AttestationTestBuilder.BuildAppleNonceExtension(nonce));

        return AttestationTestBuilder.EncodeAppleAttestationObject(
            authenticatorData, format, leaf, _root);
    }

    private AttestationVerificationResult Verify(
        byte[] attestation,
        byte[]? publicKey = null,
        byte[]? keyId = null,
        Action<AttestationOptions>? configure = null)
    {
        AttestationOptions options = new();
        options.Apple.RootCertificatePemPath = AttestationTestBuilder.WritePem(_directory, _root);
        options.Apple.TeamId = AttestationTestBuilder.TeamId;
        options.Apple.BundleId = AttestationTestBuilder.BundleId;

        configure?.Invoke(options);

        AppleAppAttestVerifier verifier = new(Options.Create(options));

        byte[] key = publicKey ?? AttestationTestBuilder.UncompressedPoint(_deviceKey);

        return verifier.Verify(new AttestationVerificationRequest(
            DevicePlatform.Ios,
            attestation,
            _challenge,
            key,
            keyId ?? SHA256.HashData(AttestationTestBuilder.UncompressedPoint(_deviceKey))));
    }
}
