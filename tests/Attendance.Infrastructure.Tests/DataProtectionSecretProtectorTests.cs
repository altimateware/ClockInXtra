using System.Text;
using Attendance.Application.Abstractions;
using Attendance.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="DataProtectionSecretProtector"/>.
/// </summary>
/// <remarks>
/// An ephemeral provider is used, so keys live only for the test run. That is
/// appropriate here because what is being tested is the protector's behaviour —
/// round-tripping, purpose isolation, and how it reacts to a payload it cannot
/// read — not Data Protection's own cryptography, which is Microsoft's.
/// </remarks>
public sealed class DataProtectionSecretProtectorTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("a-totp-shared-secret");

    private static DataProtectionSecretProtector CreateProtector(
        IDataProtectionProvider? provider = null) =>
        new(provider ?? new EphemeralDataProtectionProvider(),
            NullLogger<DataProtectionSecretProtector>.Instance);

    [Fact]
    public void Protect_ThenUnprotect_ReturnsTheOriginalSecret()
    {
        DataProtectionSecretProtector protector = CreateProtector();

        byte[] ciphertext = protector.Protect(SecretPurposes.MobileUserTotpSecret, Secret);

        Assert.True(protector.TryUnprotect(SecretPurposes.MobileUserTotpSecret, ciphertext, out byte[] plaintext));
        Assert.Equal(Secret, plaintext);
    }

    [Fact]
    public void Protect_DoesNotStoreTheSecretInTheClear()
    {
        // The stored column must not contain the secret. This is the whole
        // point of §13's "secrets must never be stored in plaintext".
        DataProtectionSecretProtector protector = CreateProtector();

        byte[] ciphertext = protector.Protect(SecretPurposes.MobileUserTotpSecret, Secret);

        Assert.DoesNotContain(Encoding.UTF8.GetString(Secret), Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);
        Assert.NotEqual(Secret, ciphertext);
    }

    [Fact]
    public void Protect_ProducesDifferentCiphertextEachTime()
    {
        // Identical secrets must not produce identical stored values: that would
        // reveal which accounts share a secret, and would leak whether an
        // enrolment actually changed after a reset.
        DataProtectionSecretProtector protector = CreateProtector();

        byte[] first = protector.Protect(SecretPurposes.MobileUserTotpSecret, Secret);
        byte[] second = protector.Protect(SecretPurposes.MobileUserTotpSecret, Secret);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TryUnprotect_RefusesAPayloadProtectedForADifferentPurpose()
    {
        // Purpose isolation is what stops a stored coordinate blob being fed
        // into the authenticator path, or vice versa, and producing nonsense
        // instead of a clear failure.
        DataProtectionSecretProtector protector = CreateProtector();

        byte[] ciphertext = protector.Protect(SecretPurposes.AttendanceCoordinates, Secret);

        Assert.False(protector.TryUnprotect(SecretPurposes.MobileUserTotpSecret, ciphertext, out byte[] plaintext));
        Assert.Empty(plaintext);
    }

    [Fact]
    public void TryUnprotect_RefusesATamperedPayload()
    {
        DataProtectionSecretProtector protector = CreateProtector();

        byte[] ciphertext = protector.Protect(SecretPurposes.MobileUserTotpSecret, Secret);
        ciphertext[^1] ^= 0xFF;

        Assert.False(protector.TryUnprotect(SecretPurposes.MobileUserTotpSecret, ciphertext, out _));
    }

    [Fact]
    public void TryUnprotect_RefusesAPayloadFromADifferentKeyRing()
    {
        // The realistic disaster: a database restored without its key ring. It
        // must fail cleanly and be diagnosable, not throw on the clock-in path.
        byte[] ciphertext = CreateProtector().Protect(SecretPurposes.MobileUserTotpSecret, Secret);

        DataProtectionSecretProtector otherKeyRing = CreateProtector(new EphemeralDataProtectionProvider());

        Assert.False(otherKeyRing.TryUnprotect(SecretPurposes.MobileUserTotpSecret, ciphertext, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void TryUnprotect_RefusesAnEmptyPayloadWithoutThrowing(byte[]? payload)
    {
        DataProtectionSecretProtector protector = CreateProtector();

        Assert.False(protector.TryUnprotect(SecretPurposes.MobileUserTotpSecret, payload!, out byte[] plaintext));
        Assert.Empty(plaintext);
    }

    [Fact]
    public void Protect_RejectsAnEmptySecretOrPurpose()
    {
        DataProtectionSecretProtector protector = CreateProtector();

        Assert.Throws<ArgumentException>(
            () => protector.Protect(SecretPurposes.MobileUserTotpSecret, ReadOnlySpan<byte>.Empty));
        Assert.Throws<ArgumentException>(
            () => protector.Protect(string.Empty, Secret));
    }

    [Fact]
    public void DifferentPurposes_ProduceIndependentCiphertext()
    {
        DataProtectionSecretProtector protector = CreateProtector();

        byte[] forUser = protector.Protect(SecretPurposes.MobileUserTotpSecret, Secret);
        byte[] forAdmin = protector.Protect(SecretPurposes.AdministratorTotpSecret, Secret);

        Assert.NotEqual(forUser, forAdmin);
        Assert.True(protector.TryUnprotect(SecretPurposes.AdministratorTotpSecret, forAdmin, out byte[] adminPlaintext));
        Assert.Equal(Secret, adminPlaintext);
    }
}
