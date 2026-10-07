using System.Security.Cryptography;
using System.Text;
using Attendance.Infrastructure.Security;
using Xunit;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="EcdsaRequestSignatureVerifier"/>.
/// </summary>
/// <remarks>
/// These cover both signature encodings the two mobile platforms produce, and
/// the rejection cases that matter for a verifier sitting on an internet-facing
/// endpoint: tampered content, a signature from another key, malformed input,
/// and a public point that is not on the curve.
/// </remarks>
public sealed class EcdsaRequestSignatureVerifierTests
{
    private static readonly byte[] SignatureBase = Encoding.UTF8.GetBytes(
        "\"@method\": POST\n" +
        "\"@authority\": attendance.example.com\n" +
        "\"@path\": /api/v1/mobile/attendance/clock-in\n" +
        "\"content-digest\": sha-256=:X48E9qOokqqrvdts8nOJRJN3OWDUoyWxBf7kbu9DBPE=:\n" +
        "\"@signature-params\": (\"@method\" \"@authority\" \"@path\" \"content-digest\")");

    private readonly EcdsaRequestSignatureVerifier _verifier = new();

    [Fact]
    public void Verify_AcceptsARawSignature_AsProducedByIos()
    {
        // CryptoKit's ECDSASignature.rawRepresentation is the 64-byte r‖s form
        // RFC 9421 specifies.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = key.SignData(SignatureBase, HashAlgorithmName.SHA256);

        Assert.Equal(64, signature.Length);
        Assert.True(_verifier.Verify(SignatureBase, signature, ExportPublicPoint(key)));
    }

    [Fact]
    public void Verify_AcceptsADerSignature_AsProducedByAndroidKeystore()
    {
        // Android's "SHA256withECDSA" emits DER. A verifier that only understood
        // the raw form would reject every Android device while iOS worked.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] derSignature = key.SignData(
            SignatureBase, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        Assert.True(_verifier.Verify(SignatureBase, derSignature, ExportPublicPoint(key)));
    }

    [Fact]
    public void Verify_RejectsASignatureOverDifferentContent()
    {
        // The whole point of covering the content digest: changing the body must
        // invalidate the signature.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = key.SignData(SignatureBase, HashAlgorithmName.SHA256);

        byte[] tampered = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(SignatureBase).Replace("clock-in", "clock-out", StringComparison.Ordinal));

        Assert.False(_verifier.Verify(tampered, signature, ExportPublicPoint(key)));
    }

    [Fact]
    public void Verify_RejectsASignatureFromAnotherDevicesKey()
    {
        using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa otherDeviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        byte[] signature = signingKey.SignData(SignatureBase, HashAlgorithmName.SHA256);

        Assert.False(_verifier.Verify(SignatureBase, signature, ExportPublicPoint(otherDeviceKey)));
    }

    [Fact]
    public void Verify_RejectsATamperedSignature()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = key.SignData(SignatureBase, HashAlgorithmName.SHA256);
        signature[0] ^= 0xFF;

        Assert.False(_verifier.Verify(SignatureBase, signature, ExportPublicPoint(key)));
    }

    [Theory]
    [InlineData(0)]    // empty
    [InlineData(32)]   // half a signature
    [InlineData(63)]   // one byte short of raw
    [InlineData(65)]   // one byte over raw, but not valid DER
    [InlineData(200)]  // far too long
    public void Verify_RejectsMalformedSignatureLengths(int length)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.False(_verifier.Verify(SignatureBase, new byte[length], ExportPublicPoint(key)));
    }

    [Fact]
    public void Verify_RejectsAnEmptySignatureBase()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = key.SignData(SignatureBase, HashAlgorithmName.SHA256);

        Assert.False(_verifier.Verify(ReadOnlySpan<byte>.Empty, signature, ExportPublicPoint(key)));
    }

    [Theory]
    [InlineData(64)]   // too short for an uncompressed point
    [InlineData(66)]   // too long
    public void Verify_RejectsAPublicKeyOfTheWrongLength(int length)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = key.SignData(SignatureBase, HashAlgorithmName.SHA256);

        byte[] malformedKey = new byte[length];
        malformedKey[0] = 0x04;

        Assert.False(_verifier.Verify(SignatureBase, signature, malformedKey));
    }

    [Fact]
    public void Verify_RejectsACompressedPublicPoint()
    {
        // core.Device.PublicKey is constrained to the uncompressed form, and the
        // verifier enforces the same shape rather than trusting the column.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = key.SignData(SignatureBase, HashAlgorithmName.SHA256);

        byte[] compressedPrefix = ExportPublicPoint(key);
        compressedPrefix[0] = 0x02;

        Assert.False(_verifier.Verify(SignatureBase, signature, compressedPrefix));
    }

    [Fact]
    public void Verify_RejectsAPointThatIsNotOnTheCurve()
    {
        // Garbage coordinates make ECDsa.Create throw. That must surface as a
        // failed verification, not an unhandled exception on a public endpoint.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = key.SignData(SignatureBase, HashAlgorithmName.SHA256);

        byte[] invalidPoint = new byte[65];
        invalidPoint[0] = 0x04;
        for (int i = 1; i < invalidPoint.Length; i++)
        {
            invalidPoint[i] = 0xAA;
        }

        Assert.False(_verifier.Verify(SignatureBase, signature, invalidPoint));
    }

    [Fact]
    public void Verify_RejectsADerSignatureWithTrailingData()
    {
        // Tolerating trailing bytes would make the encoding malleable: the same
        // logical signature could be presented in many forms, which is exactly
        // what a replay-detection nonce should not have to compensate for.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] der = key.SignData(SignatureBase, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        byte[] withTrailer = [.. der, 0x00];

        Assert.False(_verifier.Verify(SignatureBase, withTrailer, ExportPublicPoint(key)));
    }

    /// <summary>
    /// Exports the public key in the uncompressed form stored in
    /// <c>core.Device.PublicKey</c>: 0x04 ‖ X(32) ‖ Y(32).
    /// </summary>
    private static byte[] ExportPublicPoint(ECDsa key)
    {
        ECParameters parameters = key.ExportParameters(includePrivateParameters: false);

        byte[] point = new byte[65];
        point[0] = 0x04;
        parameters.Q.X!.CopyTo(point, 1);
        parameters.Q.Y!.CopyTo(point, 33);

        return point;
    }
}
