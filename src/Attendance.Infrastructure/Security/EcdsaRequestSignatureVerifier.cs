using System.Security.Cryptography;
using Attendance.Application.Abstractions;

namespace Attendance.Infrastructure.Security;

/// <summary>
/// Verifies <c>ecdsa-p256-sha256</c> request signatures (decision TD-07).
/// </summary>
/// <remarks>
/// <para>
/// The device holds a non-exportable P-256 private key in Android Keystore
/// (TEE or StrongBox) or the iOS Secure Enclave, and the server holds only the
/// public point. Nothing the server stores can be used to impersonate a device,
/// which is the property that makes this preferable to a bearer token on an
/// untrusted client (§40, §65).
/// </para>
/// <para>
/// <b>Algorithm agility is deliberately absent.</b> The algorithm is fixed at
/// <c>ecdsa-p256-sha256</c> and never read from the request. RFC 9421 §7.3.6
/// warns about algorithm and key specification downgrades, and the classic
/// failure of signature verification is trusting the attacker's own statement of
/// which algorithm to use.
/// </para>
/// </remarks>
public sealed class EcdsaRequestSignatureVerifier : IRequestSignatureVerifier
{
    private const int UncompressedPointLength = 65;
    private const byte UncompressedPointPrefix = 0x04;

    /// <inheritdoc />
    public bool Verify(
        ReadOnlySpan<byte> signatureBase,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> publicKey)
    {
        if (signatureBase.IsEmpty || signature.IsEmpty)
        {
            return false;
        }

        // The stored key must be an uncompressed P-256 point. This matches the
        // CHECK constraint on core.Device.PublicKey, so a row that somehow
        // violated it cannot become a verification path here either.
        if (publicKey.Length != UncompressedPointLength || publicKey[0] != UncompressedPointPrefix)
        {
            return false;
        }

        if (!EcdsaSignatureFormat.TryNormalizeToRaw(signature, out byte[] rawSignature))
        {
            return false;
        }

        ECParameters parameters = new()
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = publicKey.Slice(1, EcdsaSignatureFormat.P256FieldSizeBytes).ToArray(),
                Y = publicKey.Slice(1 + EcdsaSignatureFormat.P256FieldSizeBytes,
                                    EcdsaSignatureFormat.P256FieldSizeBytes).ToArray(),
            },
        };

        try
        {
            using ECDsa ecdsa = ECDsa.Create(parameters);

            // .NET's default signature format is IEEE P1363 — the raw r‖s
            // concatenation — which is exactly what RFC 9421 specifies, so the
            // normalised bytes are passed straight through.
            return ecdsa.VerifyData(signatureBase, rawSignature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            // A public point that is not actually on the curve is rejected when
            // the key is imported. That is an invalid signature attempt, not a
            // server fault: every one of these inputs came from an untrusted
            // client.
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            // The same off-curve point surfaces differently depending on the
            // platform's crypto provider: Windows CNG reports it as
            // PlatformNotSupportedException ("the specified curve or its
            // parameters are not valid for this platform") wrapping the real
            // CryptographicException, rather than throwing that directly.
            //
            // Caught here because the alternative is an unhandled exception —
            // and therefore a 500 — on an internet-facing endpoint, triggered by
            // a value an attacker chooses. Found by the off-curve test, which is
            // why that test exists.
            return false;
        }
    }
}
