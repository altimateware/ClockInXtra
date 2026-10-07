using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Attendance.Application.Abstractions;
using Attendance.Domain.ValueObjects;
using Attendance.Infrastructure.Security.Attestation;
using Microsoft.Extensions.Options;
using Xunit;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AndroidKeyAttestationVerifier"/>.
/// </summary>
/// <remarks>
/// Every input here is attacker-controlled in production, so two properties are
/// tested throughout: the verifier reaches the right decision, and it never
/// throws. An exception on this path would be a denial of service reachable by
/// anyone who can call the registration endpoint.
/// </remarks>
public sealed class AndroidKeyAttestationVerifierTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cix-{Guid.NewGuid():N}")).FullName;

    private readonly X509Certificate2 _root = AttestationTestBuilder.CreateRoot();
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
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public void AcceptsAWellFormedHardwareAttestation()
    {
        AttestationVerificationResult result = Verify(Build());

        Assert.True(result.IsAccepted);
        Assert.Equal(AttestationLevel.Hardware, result.Level);
        Assert.Null(result.ReasonCode);
    }

    [Fact]
    public void RefusesAChallengeThatDoesNotMatch()
    {
        // The replay defence. Without it a captured attestation would work
        // forever (threat TH-10).
        byte[] attestation = Build(challenge: RandomNumberGenerator.GetBytes(32));

        AttestationVerificationResult result = Verify(attestation);

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_CHALLENGE_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void RefusesAnAttestationForADifferentKey()
    {
        // A genuine attestation presented alongside somebody else's public key.
        using ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        AttestationVerificationResult result = Verify(
            Build(), publicKey: AttestationTestBuilder.UncompressedPoint(otherKey));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_KEY_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void RefusesASoftwareBackedKey()
    {
        AttestationVerificationResult result = Verify(Build(securityLevel: 0));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_SECURITY_LEVEL_INSUFFICIENT", result.ReasonCode);
    }

    [Fact]
    public void AcceptsStrongBoxWhenOnlyTheTrustedEnvironmentIsRequired()
    {
        AttestationVerificationResult result = Verify(Build(securityLevel: 2));

        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void RefusesTheTrustedEnvironmentWhenStrongBoxIsRequired()
    {
        AttestationVerificationResult result = Verify(
            Build(securityLevel: 1), configure: o => o.Android.RequireStrongBox = true);

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_SECURITY_LEVEL_INSUFFICIENT", result.ReasonCode);
    }

    [Fact]
    public void RefusesAnUnlockedBootloader()
    {
        // An unlocked bootloader means the operating system may have been
        // replaced, so nothing the application reports about itself is credible.
        AttestationVerificationResult result = Verify(Build(deviceLocked: false));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_BOOTLOADER_UNLOCKED", result.ReasonCode);
    }

    [Theory]
    [InlineData(1)]   // SelfSigned
    [InlineData(2)]   // Unverified
    [InlineData(3)]   // Failed
    public void RefusesAnythingOtherThanAVerifiedBootState(int state)
    {
        AttestationVerificationResult result = Verify(Build(verifiedBootState: state));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_BOOT_STATE_UNVERIFIED", result.ReasonCode);
    }

    [Fact]
    public void RefusesAMissingRootOfTrustWhenVerifiedBootIsRequired()
    {
        AttestationVerificationResult result = Verify(Build(includeRootOfTrust: false));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_ROOT_OF_TRUST_MISSING", result.ReasonCode);
    }

    [Fact]
    public void RefusesAnotherApplicationsPackage()
    {
        // Without this, an attestation from any application on any Android
        // device would satisfy registration.
        AttestationVerificationResult result = Verify(Build(packageName: "com.attacker.app"));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_PACKAGE_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void RefusesTheRightPackageSignedByTheWrongKey()
    {
        // Anyone can publish an app using a chosen package name; the signing
        // certificate digest is what ties it to this organisation.
        string otherDigest = Convert.ToHexString(SHA256.HashData("someone-elses-key"u8));

        AttestationVerificationResult result = Verify(Build(signingDigestHex: otherDigest));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_SIGNING_CERTIFICATE_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void RefusesAChainThatDoesNotReachTheConfiguredRoot()
    {
        // An attacker with their own CA can produce a structurally perfect
        // attestation. The root is what makes it worthless.
        using X509Certificate2 foreignRoot = AttestationTestBuilder.CreateRoot("CN=Attacker Root");
        using X509Certificate2 leaf = AttestationTestBuilder.IssueLeaf(
            foreignRoot, _deviceKey, AttestationTestBuilder.AttestationOid,
            AttestationTestBuilder.BuildAndroidExtension(_challenge));

        AttestationVerificationResult result = Verify(AttestationTestBuilder.EncodeChain(leaf, foreignRoot));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_CHAIN_UNTRUSTED", result.ReasonCode);
    }

    [Fact]
    public void RefusesACertificateWithoutTheAttestationExtension()
    {
        using X509Certificate2 leaf = AttestationTestBuilder.IssueLeaf(
            _root, _deviceKey, "1.3.6.1.4.1.11129.2.1.30", [0x05, 0x00]);

        AttestationVerificationResult result = Verify(AttestationTestBuilder.EncodeChain(leaf, _root));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_EXTENSION_MISSING", result.ReasonCode);
    }

    [Fact]
    public void RefusesAMalformedExtensionWithoutThrowing()
    {
        using X509Certificate2 leaf = AttestationTestBuilder.IssueLeaf(
            _root, _deviceKey, AttestationTestBuilder.AttestationOid,
            RandomNumberGenerator.GetBytes(48));

        AttestationVerificationResult result = Verify(AttestationTestBuilder.EncodeChain(leaf, _root));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_EXTENSION_MALFORMED", result.ReasonCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(512)]
    public void RefusesArbitraryBytesWithoutThrowing(int length)
    {
        AttestationVerificationResult result = Verify(RandomNumberGenerator.GetBytes(length));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_CHAIN_MALFORMED", result.ReasonCode);
    }

    [Fact]
    public void RefusesWhenTheVerifierIsNotConfigured()
    {
        // A verifier without a root certificate cannot tell this organisation's
        // application from any other, so it refuses rather than passing.
        AndroidKeyAttestationVerifier verifier = new(
            Options.Create(new AttestationOptions()), new StubRevocationList());

        AttestationVerificationResult result = verifier.Verify(NewRequest(Build(), DefaultKey(), _challenge));

        Assert.False(result.IsAccepted);
        Assert.Equal("ANDROID_ATTESTATION_NOT_CONFIGURED", result.ReasonCode);
    }

    [Fact]
    public void RefusesWhenNoSigningDigestIsConfigured()
    {
        AttestationVerificationResult result = Verify(
            Build(), configure: o => o.Android.ExpectedSigningCertificateDigests.Clear());

        Assert.False(result.IsAccepted);
        Assert.Equal("ANDROID_ATTESTATION_NOT_CONFIGURED", result.ReasonCode);
    }

    [Fact]
    public void RefusesARevokedAttestationKey()
    {
        AttestationVerificationResult result = Verify(
            Build(), revocationList: new StubRevocationList(revoked: true));

        Assert.False(result.IsAccepted);
        Assert.StartsWith("ATTESTATION_KEY_REVOKED", result.ReasonCode, StringComparison.Ordinal);
    }

    // ---- Helpers -----------------------------------------------------------

    private byte[] DefaultKey() => AttestationTestBuilder.UncompressedPoint(_deviceKey);

    // ---- Golden sample: real KeyMint output --------------------------------

    /// <summary>
    /// A <c>KeyDescription</c> produced by a real KeyMint 4.0 implementation, captured
    /// byte for byte from the Android emulator on 2026-09-13.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other test here uses <see cref="AttestationTestBuilder"/>, which encodes
    /// the published schema as it was read. That is exactly the weakness this sample
    /// closes: the builder and the parser once shared a misreading of the
    /// <c>[709]</c> tag, so they agreed with each other while a real device's
    /// attestation was refused as malformed. Only output from the platform itself can
    /// catch that class of mistake.
    /// </para>
    /// <para>
    /// What it contains, decoded: attestation version 400; security level Software
    /// (0); a 32-byte challenge; an empty hardware-enforced list; and, in the
    /// software-enforced list, a root of trust reporting an unlocked, unverified boot
    /// plus the application id for <c>com.contoso.clockinxtra</c>. It is not secret —
    /// an attestation extension is part of a public certificate.
    /// </para>
    /// <para>
    /// Only the extension is kept, embedded in a synthetic chain. The emulator's real
    /// chain roots to a Google test CA that expires in November 2026, and a test that
    /// depended on it would start failing on a date rather than on a defect.
    /// </para>
    /// </remarks>
    private const string EmulatorKeyDescriptionHex =
        "3082015D020201900A0100020201900A010004209C2D51D7DB1AA8250ACFB916" +
        "F010CE746CC3D36A18962FB8F308F4FB3E452929040030820125A10531030201" +
        "02A203020103A30402020100A5053103020104AA03020101BF8377020500BF83" +
        "7D020500BF853D08020601A09B8E950ABF853E03020100BF85404C304A042000" +
        "0000000000000000000000000000000000000000000000000000000000000001" +
        "01000A0102042000000000000000000000000000000000000000000000000000" +
        "00000000000000BF8541050203027100BF854205020303170BBF854548044630" +
        "44311E301C0417636F6D2E636F6E746F736F2E636C6F636B696E787472610201" +
        "0131220420B9049E85960C672A40164B3A2A6D4514C08AD4588D28C731EF523E" +
        "31270392C2BF854E06020401350051BF854F0602040135004DBF8554220420F0" +
        "3D75986EDB3A13023B7D2C2F06DEA1CED124786EAA09FEE219B952DA1976F630" +
        "00";

    /// <summary>The challenge the emulator attestation above was bound to.</summary>
    private static readonly byte[] EmulatorChallenge =
        Convert.FromHexString("9C2D51D7DB1AA8250ACFB916F010CE746CC3D36A18962FB8F308F4FB3E452929");

    [Fact]
    public void ParsesARealKeyMintAttestationAndRefusesItOnTheMerits()
    {
        // Reaching the security-level check proves the parse, the challenge binding
        // and the key match all succeeded against genuine platform output. The
        // refusal itself is correct: the emulator's key is software-backed.
        using X509Certificate2 leaf = AttestationTestBuilder.IssueLeaf(
            _root,
            _deviceKey,
            AttestationTestBuilder.AttestationOid,
            Convert.FromHexString(EmulatorKeyDescriptionHex));

        AttestationVerificationResult result = Verify(
            AttestationTestBuilder.EncodeChain(leaf, _root),
            challenge: EmulatorChallenge);

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_SECURITY_LEVEL_INSUFFICIENT", result.ReasonCode);
    }

    [Fact]
    public void RefusesTheRealAttestationIfItsChallengeIsSubstituted()
    {
        // The same genuine extension presented against a different server challenge
        // — a captured attestation replayed into a new registration.
        using X509Certificate2 leaf = AttestationTestBuilder.IssueLeaf(
            _root,
            _deviceKey,
            AttestationTestBuilder.AttestationOid,
            Convert.FromHexString(EmulatorKeyDescriptionHex));

        AttestationVerificationResult result = Verify(AttestationTestBuilder.EncodeChain(leaf, _root));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_CHALLENGE_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void AcceptsTheApplicationIdFromTheHardwareListToo()
    {
        // Real KeyMint output puts it in the software list, and the schema
        // documentation states no placement, so a vendor that uses the hardware list
        // must not be refused.
        AttestationVerificationResult result = Verify(Build(applicationIdInHardwareList: true));

        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void IgnoresARootOfTrustClaimedOnlyBySoftware()
    {
        // A boot-integrity claim in the software list is the operating system
        // vouching for itself. It must be treated as absent, even when it reports a
        // locked, verified boot — otherwise a compromised OS could simply say so.
        AttestationVerificationResult result = Verify(Build(rootOfTrustInSoftwareList: true));

        Assert.False(result.IsAccepted);
        Assert.Equal("ATTESTATION_ROOT_OF_TRUST_MISSING", result.ReasonCode);
    }

    private byte[] Build(
        byte[]? challenge = null,
        int securityLevel = 1,
        bool includeRootOfTrust = true,
        bool deviceLocked = true,
        int verifiedBootState = 0,
        string packageName = AttestationTestBuilder.PackageName,
        string? signingDigestHex = null,
        bool applicationIdInHardwareList = false,
        bool rootOfTrustInSoftwareList = false)
    {
        byte[] extension = AttestationTestBuilder.BuildAndroidExtension(
            challenge ?? _challenge, securityLevel, includeRootOfTrust, deviceLocked,
            verifiedBootState, packageName, signingDigestHex,
            applicationIdInHardwareList: applicationIdInHardwareList,
            rootOfTrustInSoftwareList: rootOfTrustInSoftwareList);

        using X509Certificate2 leaf = AttestationTestBuilder.IssueLeaf(
            _root, _deviceKey, AttestationTestBuilder.AttestationOid, extension);

        return AttestationTestBuilder.EncodeChain(leaf, _root);
    }

    private AttestationVerificationResult Verify(
        byte[] attestation,
        byte[]? publicKey = null,
        Action<AttestationOptions>? configure = null,
        IAttestationRevocationList? revocationList = null,
        byte[]? challenge = null)
    {
        AttestationOptions options = new();
        options.Android.RootCertificatePemPath = AttestationTestBuilder.WritePem(_directory, _root);
        options.Android.ExpectedPackageName = AttestationTestBuilder.PackageName;
        options.Android.ExpectedSigningCertificateDigests.Add(AttestationTestBuilder.SigningDigestHex);

        configure?.Invoke(options);

        AndroidKeyAttestationVerifier verifier = new(
            Options.Create(options), revocationList ?? new StubRevocationList());

        return verifier.Verify(NewRequest(attestation, publicKey ?? DefaultKey(), challenge ?? _challenge));
    }

    private static AttestationVerificationRequest NewRequest(byte[] attestation, byte[] publicKey, byte[] challenge) =>
        new(DevicePlatform.Android, attestation, challenge, publicKey, ReadOnlyMemory<byte>.Empty);

    private sealed class StubRevocationList : IAttestationRevocationList
    {
        private readonly bool _revoked;

        public StubRevocationList(bool revoked = false) => _revoked = revoked;

        public bool IsRevoked(X509Certificate2Collection chain, out string? revokedSerial)
        {
            revokedSerial = _revoked ? "deadbeef" : null;
            return _revoked;
        }
    }
}
