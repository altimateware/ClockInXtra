using System.Security.Cryptography;
using System.Text;
using Attendance.Application.Abstractions;

namespace Attendance.Infrastructure.Security;

/// <summary>
/// PBKDF2 password hashing (decision TD-05).
/// </summary>
/// <remarks>
/// <para>
/// PBKDF2-HMAC-SHA512 at 220,000 iterations, which is the figure the OWASP
/// Password Storage Cheat Sheet gives for that combination. The salt is 32
/// random bytes and the derived key is 64 bytes.
/// </para>
/// <para>
/// <b>Why not Argon2id.</b> OWASP prefers Argon2id, and it would be the choice if
/// .NET provided it. It does not: .NET delegates cryptographic primitives to the
/// platform, and no Windows primitive implements Argon2. The managed packages
/// that do are third-party, and the most used one has had no release since 2024
/// and does not target .NET 10. For a credential path in a security-sensitive
/// system, a first-party primitive backed by the operating system is worth more
/// than a marginally better KDF carried by an unmaintained dependency. The stored
/// format records the algorithm, so migrating later is a re-hash on next sign-in,
/// not a data migration.
/// </para>
/// <para>
/// <b>No pepper.</b> A pepper only helps if it lives somewhere the database
/// attacker cannot reach, which means another secret to provision, rotate and
/// lose. Introducing one would be a decision for the organisation's key
/// management, not a default — and a pepper that sits in the same configuration
/// as the connection string buys nothing.
/// </para>
/// </remarks>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    /// <summary>The format identifier written to new credentials.</summary>
    public const string CurrentHashFormat = "pbkdf2-sha512";

    /// <summary>Iteration count for new credentials (OWASP guidance).</summary>
    public const int CurrentIterations = 220_000;

    private const string LegacySha256Format = "pbkdf2-sha256";
    private const int SaltSizeBytes = 32;
    private const int DerivedKeySizeBytes = 64;

    // Used only to spend comparable time when no credential exists. The value is
    // irrelevant; it is never compared against anything.
    private static readonly byte[] DummySalt = new byte[SaltSizeBytes];
    private const string DummyPassword = "dummy-password-for-uniform-timing";

    /// <inheritdoc />
    public PasswordHash Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] hash = Derive(password, salt, CurrentIterations, CurrentHashFormat, DerivedKeySizeBytes);

        return new PasswordHash(CurrentHashFormat, CurrentIterations, salt, hash);
    }

    /// <inheritdoc />
    public bool Verify(string password, PasswordHash stored)
    {
        if (string.IsNullOrEmpty(password)
            || stored.Salt is null or { Length: 0 }
            || stored.Hash is null or { Length: 0 }
            || stored.Iterations <= 0
            || !IsKnownFormat(stored.HashFormat))
        {
            // A malformed or unrecognised credential is a failed verification,
            // never an exception: an exception here would be a way to tell a
            // corrupt row apart from a wrong password.
            return false;
        }

        byte[] candidate = Derive(
            password, stored.Salt, stored.Iterations, stored.HashFormat, stored.Hash.Length);

        // Fixed-time comparison: a byte-by-byte compare leaks how much of the
        // hash matched, which is enough to reconstruct it given enough attempts.
        return CryptographicOperations.FixedTimeEquals(candidate, stored.Hash);
    }

    /// <inheritdoc />
    public bool NeedsRehash(PasswordHash stored) =>
        !string.Equals(stored.HashFormat, CurrentHashFormat, StringComparison.Ordinal)
        || stored.Iterations < CurrentIterations
        || stored.Hash is null
        || stored.Hash.Length < DerivedKeySizeBytes;

    /// <inheritdoc />
    public void PerformDummyVerification()
    {
        byte[] result = Derive(
            DummyPassword, DummySalt, CurrentIterations, CurrentHashFormat, DerivedKeySizeBytes);

        // Consume the result so the work cannot be optimised away, without
        // branching on anything meaningful.
        CryptographicOperations.ZeroMemory(result);
    }

    private static bool IsKnownFormat(string? format) =>
        string.Equals(format, CurrentHashFormat, StringComparison.Ordinal)
        || string.Equals(format, LegacySha256Format, StringComparison.Ordinal);

    private static byte[] Derive(
        string password,
        byte[] salt,
        int iterations,
        string format,
        int outputLength)
    {
        HashAlgorithmName algorithm = string.Equals(format, LegacySha256Format, StringComparison.Ordinal)
            ? HashAlgorithmName.SHA256
            : HashAlgorithmName.SHA512;

        // UTF-8 explicitly: the encoding is part of the stored credential's
        // meaning, and a default that differed between platforms would make
        // every non-ASCII password unverifiable elsewhere.
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);

        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, algorithm, outputLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}
