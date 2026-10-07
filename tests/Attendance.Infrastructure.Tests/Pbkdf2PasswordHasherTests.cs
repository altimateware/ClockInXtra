using Attendance.Application.Abstractions;
using Attendance.Infrastructure.Security;
using Xunit;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="Pbkdf2PasswordHasher"/>.
/// </summary>
public sealed class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void Hash_ProducesTheConfiguredParameters()
    {
        PasswordHash result = _hasher.Hash("correct horse battery staple");

        Assert.Equal(Pbkdf2PasswordHasher.CurrentHashFormat, result.HashFormat);
        Assert.Equal(Pbkdf2PasswordHasher.CurrentIterations, result.Iterations);
        Assert.Equal(32, result.Salt.Length);
        Assert.Equal(64, result.Hash.Length);
    }

    [Fact]
    public void Hash_UsesADifferentSaltEveryTime()
    {
        // Two employees with the same password must not produce the same stored
        // hash: identical hashes would tell anyone who read the table which
        // accounts share a password.
        PasswordHash first = _hasher.Hash("same password");
        PasswordHash second = _hasher.Hash("same password");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Verify_AcceptsTheCorrectPassword()
    {
        PasswordHash stored = _hasher.Hash("Tr0ub4dor&3");

        Assert.True(_hasher.Verify("Tr0ub4dor&3", stored));
    }

    [Theory]
    [InlineData("wrong password")]
    [InlineData("Tr0ub4dor&4")]
    [InlineData("tr0ub4dor&3")]   // case matters
    [InlineData("Tr0ub4dor&3 ")]  // trailing space matters
    public void Verify_RejectsAnIncorrectPassword(string attempt)
    {
        PasswordHash stored = _hasher.Hash("Tr0ub4dor&3");

        Assert.False(_hasher.Verify(attempt, stored));
    }

    [Fact]
    public void Verify_RejectsATamperedSalt()
    {
        PasswordHash stored = _hasher.Hash("a password");
        stored.Salt[0] ^= 0xFF;

        Assert.False(_hasher.Verify("a password", stored));
    }

    [Fact]
    public void Verify_HandlesNonAsciiPasswords()
    {
        // The encoding is part of the credential's meaning. If it were left to a
        // platform default, a password with non-ASCII characters could verify on
        // one machine and fail on another.
        const string password = "pa55wörd-ñ-日本語";
        PasswordHash stored = _hasher.Hash(password);

        Assert.True(_hasher.Verify(password, stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Verify_RejectsAnEmptyPasswordWithoutThrowing(string? attempt)
    {
        PasswordHash stored = _hasher.Hash("a password");

        Assert.False(_hasher.Verify(attempt!, stored));
    }

    [Fact]
    public void Verify_TreatsAMalformedCredentialAsAFailureRatherThanAnError()
    {
        // A corrupt or unrecognised row must not throw: an exception would
        // distinguish a broken credential from a wrong password, and that
        // difference is observable from outside.
        PasswordHash unknownFormat = new("scrypt", 220_000, new byte[32], new byte[64]);
        PasswordHash emptySalt = new(Pbkdf2PasswordHasher.CurrentHashFormat, 220_000, [], new byte[64]);
        PasswordHash zeroIterations = new(Pbkdf2PasswordHasher.CurrentHashFormat, 0, new byte[32], new byte[64]);

        Assert.False(_hasher.Verify("anything", unknownFormat));
        Assert.False(_hasher.Verify("anything", emptySalt));
        Assert.False(_hasher.Verify("anything", zeroIterations));
    }

    [Fact]
    public void Hash_RejectsAnEmptyPassword()
    {
        Assert.Throws<ArgumentException>(() => _hasher.Hash(string.Empty));
        Assert.Throws<ArgumentNullException>(() => _hasher.Hash(null!));
    }

    [Fact]
    public void NeedsRehash_IsFalseForACredentialAtCurrentParameters()
    {
        PasswordHash current = _hasher.Hash("a password");

        Assert.False(_hasher.NeedsRehash(current));
    }

    [Theory]
    [InlineData("pbkdf2-sha256", 220_000)]   // older algorithm
    [InlineData("pbkdf2-sha512", 100_000)]   // weaker work factor
    public void NeedsRehash_IsTrueForWeakerStoredParameters(string format, int iterations)
    {
        PasswordHash stored = new(format, iterations, new byte[32], new byte[64]);

        Assert.True(_hasher.NeedsRehash(stored));
    }

    [Fact]
    public void LegacyFormatStillVerifies_SoCredentialsCanBeUpgradedOnSignIn()
    {
        // A credential written under the older algorithm must keep working until
        // its owner next signs in, otherwise raising the work factor would lock
        // everyone out.
        PasswordHash legacy = CreateLegacySha256Hash("legacy password");

        Assert.True(_hasher.Verify("legacy password", legacy));
        Assert.True(_hasher.NeedsRehash(legacy));
    }

    [Fact]
    public void PerformDummyVerification_Completes()
    {
        // Its purpose is spending comparable time for an unknown user; the only
        // behaviour to assert is that it runs and returns nothing.
        _hasher.PerformDummyVerification();
    }

    private static PasswordHash CreateLegacySha256Hash(string password)
    {
        byte[] salt = new byte[32];
        Random.Shared.NextBytes(salt);

        byte[] hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(password),
            salt,
            220_000,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            64);

        return new PasswordHash("pbkdf2-sha256", 220_000, salt, hash);
    }
}
