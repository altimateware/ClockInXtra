namespace Attendance.Application.Abstractions;

/// <summary>
/// Verifies the signature on a mobile request.
/// </summary>
/// <remarks>
/// <para>
/// This is the mechanism chosen in DEC-02 instead of application-layer payload
/// encryption, and it is what authenticates the device on every request (§40).
/// TLS already provides confidentiality; what it does not provide is proof that
/// a request came from a specific registered device, integrity after the reverse
/// proxy terminates TLS, or protection against replay of a captured request.
/// A signature over the request, with a nonce and a timestamp, addresses all
/// three.
/// </para>
/// <para>
/// The profile is a constrained subset of <b>RFC 9421 HTTP Message
/// Signatures</b> with <b>RFC 9530 Content-Digest</b>, specified in
/// <c>docs/architecture/03-solution-architecture.md</c> §6. No bespoke scheme is
/// invented (§23).
/// </para>
/// <para>
/// Verification here is cryptographic only. Whether the nonce has been seen
/// before, and whether the device is still active, are decided by the database —
/// a replay check that lived in application memory would not hold across several
/// servers (§45).
/// </para>
/// </remarks>
public interface IRequestSignatureVerifier
{
    /// <summary>
    /// Verifies a signature against a registered public key.
    /// </summary>
    /// <param name="signatureBase">
    /// The canonical signature base assembled from the covered components, as
    /// defined by RFC 9421 §2.5.
    /// </param>
    /// <param name="signature">
    /// The signature bytes, expected as the raw 64-byte r‖s form that RFC 9421
    /// §3.3.4 specifies for <c>ecdsa-p256-sha256</c>.
    /// </param>
    /// <param name="publicKey">
    /// The registered uncompressed P-256 public point (65 bytes, leading 0x04),
    /// exactly as stored in <c>core.Device.PublicKey</c>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> only when the signature verifies. Malformed input
    /// returns <see langword="false"/> rather than throwing: every one of these
    /// values arrives from an untrusted client, so bad input is an expected
    /// condition, not an exceptional one.
    /// </returns>
    bool Verify(ReadOnlySpan<byte> signatureBase, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey);
}
