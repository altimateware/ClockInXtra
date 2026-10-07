using System.Formats.Asn1;

namespace Attendance.Infrastructure.Security;

/// <summary>
/// Converts ECDSA signatures between DER and the raw r‖s form.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> RFC 9421 §3.3.4 specifies that an
/// <c>ecdsa-p256-sha256</c> signature is "two integer values: r and s … both
/// encoded as big-endian unsigned integers, zero-padded to 32 octets each" and
/// concatenated into 64 octets. Apple's CryptoKit produces exactly that
/// (<c>ECDSASignature.rawRepresentation</c>), but Android's
/// <c>Signature.getInstance("SHA256withECDSA")</c> produces a <b>DER</b>
/// sequence instead.
/// </para>
/// <para>
/// So the two platforms send different bytes for the same signature. The Android
/// side of the mobile plugin converts before transmitting; this server-side
/// conversion exists so a DER signature is still understood rather than silently
/// rejected — a failure that would look like "iOS works, Android does not" and
/// would be maddening to diagnose from a rejection log.
/// </para>
/// <para>
/// The parser is <c>System.Formats.Asn1</c>, which ships with .NET. Hand-rolling
/// ASN.1 parsing over untrusted input is a well-known source of memory-safety
/// and malleability bugs, and there is no reason to write one here.
/// </para>
/// </remarks>
internal static class EcdsaSignatureFormat
{
    /// <summary>Field size of the P-256 curve in bytes.</summary>
    public const int P256FieldSizeBytes = 32;

    /// <summary>Length of a raw P-256 signature (r‖s).</summary>
    public const int RawSignatureLength = P256FieldSizeBytes * 2;

    /// <summary>
    /// Normalises a signature to the raw 64-byte r‖s form.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> and the raw signature when the input is already
    /// raw, or is a well-formed DER sequence for P-256; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool TryNormalizeToRaw(ReadOnlySpan<byte> signature, out byte[] raw)
    {
        if (signature.Length == RawSignatureLength)
        {
            raw = signature.ToArray();
            return true;
        }

        return TryConvertDerToRaw(signature, out raw);
    }

    private static bool TryConvertDerToRaw(ReadOnlySpan<byte> der, out byte[] raw)
    {
        raw = [];

        // A P-256 DER signature is around 70 bytes; the bounds keep obvious junk
        // away from the parser without pretending to validate it here.
        if (der.Length is < 8 or > 80)
        {
            return false;
        }

        try
        {
            AsnReader reader = new(der.ToArray(), AsnEncodingRules.DER);
            AsnReader sequence = reader.ReadSequence();

            System.Numerics.BigInteger r = sequence.ReadInteger();
            System.Numerics.BigInteger s = sequence.ReadInteger();

            // Reject trailing data: a signature with anything appended is not a
            // signature this system accepts, and tolerating it would make the
            // encoding malleable.
            sequence.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();

            if (r.Sign <= 0 || s.Sign <= 0)
            {
                return false;
            }

            byte[] result = new byte[RawSignatureLength];

            if (!TryWriteFixedWidth(r, result.AsSpan(0, P256FieldSizeBytes))
                || !TryWriteFixedWidth(s, result.AsSpan(P256FieldSizeBytes, P256FieldSizeBytes)))
            {
                return false;
            }

            raw = result;
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static bool TryWriteFixedWidth(System.Numerics.BigInteger value, Span<byte> destination)
    {
        // Unsigned, big-endian, left-padded with zeros to exactly the field size
        // — the encoding RFC 9421 requires. A value too large for the field is
        // not a P-256 signature component.
        return value.TryWriteBytes(destination, out int written, isUnsigned: true, isBigEndian: true)
            && written == destination.Length;
    }
}
