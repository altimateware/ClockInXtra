using System.Security.Cryptography;

namespace Attendance.Api.Security;

/// <summary>
/// Verifies an RFC 9530 <c>Content-Digest</c> header.
/// </summary>
/// <remarks>
/// <para>
/// The header format is <c>sha-256=:&lt;base64&gt;:</c>. It is what lets the
/// signature cover the body without the body itself being part of the signature
/// base: the signature covers the digest, and the digest covers the bytes.
/// </para>
/// <para>
/// <b>Only SHA-256 is accepted.</b> RFC 9530 registers several algorithms
/// including some intended for integrity rather than security. Taking the
/// algorithm from the client would let an attacker nominate a weak one and forge
/// a body that matches — so the algorithm is fixed by this profile, not
/// negotiated.
/// </para>
/// </remarks>
internal static class ContentDigest
{
    private const string Prefix = "sha-256=:";

    /// <summary>
    /// Whether the header matches the body.
    /// </summary>
    /// <remarks>
    /// The comparison is fixed-time out of discipline rather than necessity: the
    /// digest is not a secret, but making constant-time comparison the habit on
    /// security paths is cheaper than deciding case by case.
    /// </remarks>
    public static bool Matches(string? headerValue, ReadOnlySpan<byte> body)
    {
        if (string.IsNullOrEmpty(headerValue)
            || !headerValue.StartsWith(Prefix, StringComparison.Ordinal)
            || !headerValue.EndsWith(':'))
        {
            return false;
        }

        string encoded = headerValue[Prefix.Length..^1];

        Span<byte> presented = stackalloc byte[32];

        if (!Convert.TryFromBase64String(encoded, presented, out int written) || written != 32)
        {
            return false;
        }

        Span<byte> computed = stackalloc byte[32];
        SHA256.HashData(body, computed);

        return CryptographicOperations.FixedTimeEquals(computed, presented);
    }

    /// <summary>Formats a digest header for a body, used by tests and clients.</summary>
    public static string Compute(ReadOnlySpan<byte> body) =>
        $"{Prefix}{Convert.ToBase64String(SHA256.HashData(body))}:";
}
