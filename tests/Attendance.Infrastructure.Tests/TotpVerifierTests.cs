using Attendance.Application.Abstractions;
using Attendance.Infrastructure.Security;
using OtpNet;
using Xunit;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="TotpVerifier"/>.
/// </summary>
/// <remarks>
/// Codes are generated with the same library the verifier uses, at instants the
/// test controls. That is deliberate: the point of these tests is the policy
/// around verification — the tolerance window, the reported time step, and what
/// is rejected before any cryptography happens — not a re-implementation of
/// RFC 6238 arithmetic.
/// </remarks>
public sealed class TotpVerifierTests
{
    private static readonly byte[] Secret = KeyGeneration.GenerateRandomKey(20);
    private static readonly DateTime Instant = new(2026, 9, 12, 8, 30, 0, DateTimeKind.Utc);

    private static TotpParameters Defaults => TotpParameters.Default;

    /// <summary>
    /// The tolerance these tests run at: the seeded default of
    /// <c>Security.TotpStepTolerance</c>. It is passed explicitly now that it is
    /// a setting rather than part of the credential's parameters.
    /// </summary>
    private const int Tolerance = 1;

    [Fact]
    public void Verify_AcceptsTheCurrentCode()
    {
        string code = ComputeCode(Instant);
        TotpVerifier verifier = new(new FixedClock(Instant));

        TotpVerificationResult result = verifier.Verify(Secret, code, Defaults, Tolerance);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Verify_ReportsTheMatchedTimeStep()
    {
        // The matched step is what the database consumes to stop the same code
        // being replayed, so it has to be the real step, not a placeholder.
        string code = ComputeCode(Instant);
        TotpVerifier verifier = new(new FixedClock(Instant));

        TotpVerificationResult result = verifier.Verify(Secret, code, Defaults, Tolerance);

        long expectedStep = (long)Math.Floor(
            (Instant - DateTime.UnixEpoch).TotalSeconds / Defaults.PeriodSeconds);

        Assert.Equal(expectedStep, result.MatchedTimeStep);
    }

    [Theory]
    [InlineData(-30)]   // one step in the past
    [InlineData(30)]    // one step in the future
    public void Verify_AcceptsOneStepEitherSide(int offsetSeconds)
    {
        // Phones drift. A single step of tolerance — the seeded value of
        // Security.TotpStepTolerance — covers ordinary clock difference without
        // materially widening the window in which an observed code can be reused.
        string code = ComputeCode(Instant.AddSeconds(offsetSeconds));
        TotpVerifier verifier = new(new FixedClock(Instant));

        Assert.True(verifier.Verify(Secret, code, Defaults, Tolerance).IsValid);
    }

    [Theory]
    [InlineData(-90)]
    [InlineData(90)]
    public void Verify_RejectsCodesBeyondTheTolerance(int offsetSeconds)
    {
        string code = ComputeCode(Instant.AddSeconds(offsetSeconds));
        TotpVerifier verifier = new(new FixedClock(Instant));

        Assert.False(verifier.Verify(Secret, code, Defaults, Tolerance).IsValid);
    }

    [Fact]
    public void Verify_RejectsACodeForADifferentSecret()
    {
        byte[] otherSecret = KeyGeneration.GenerateRandomKey(20);
        string code = new Totp(otherSecret).ComputeTotp(Instant);
        TotpVerifier verifier = new(new FixedClock(Instant));

        Assert.False(verifier.Verify(Secret, code, Defaults, Tolerance).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12345")]        // too short
    [InlineData("1234567")]      // too long
    [InlineData("12345a")]       // not all digits
    [InlineData("12345 ")]       // trailing space
    [InlineData("١٢٣٤٥٦")]       // Arabic-Indic digits: never produced by an authenticator
    public void Verify_RejectsMalformedCodesWithoutCryptographicWork(string? code)
    {
        TotpVerifier verifier = new(new FixedClock(Instant));

        Assert.False(verifier.Verify(Secret, code!, Defaults, Tolerance).IsValid);
    }

    [Fact]
    public void Verify_RejectsAnEmptySecret()
    {
        TotpVerifier verifier = new(new FixedClock(Instant));

        Assert.False(verifier.Verify(ReadOnlySpan<byte>.Empty, "123456", Defaults, Tolerance).IsValid);
    }

    [Fact]
    public void Verify_RejectsNonsensicalParameters()
    {
        TotpVerifier verifier = new(new FixedClock(Instant));
        string code = ComputeCode(Instant);

        Assert.False(verifier.Verify(Secret, code, Defaults with { PeriodSeconds = 0 }, Tolerance).IsValid);
        Assert.False(verifier.Verify(Secret, code, Defaults, stepTolerance: -1).IsValid);
    }

    [Fact]
    public void Verify_HonoursAZeroTolerance()
    {
        // With tolerance switched off, only the current step is accepted. This
        // is the setting to reach for if the replay window ever has to shrink.
        const int strict = 0;
        string previousStepCode = ComputeCode(Instant.AddSeconds(-30));
        TotpVerifier verifier = new(new FixedClock(Instant));

        Assert.False(verifier.Verify(Secret, previousStepCode, Defaults, strict).IsValid);
        Assert.True(verifier.Verify(Secret, ComputeCode(Instant), Defaults, strict).IsValid);
    }

    [Fact]
    public void Verify_SupportsStrongerHashAlgorithms()
    {
        TotpParameters sha256 = Defaults with { Algorithm = TotpAlgorithm.Sha256 };
        string code = new Totp(Secret, mode: OtpHashMode.Sha256).ComputeTotp(Instant);
        TotpVerifier verifier = new(new FixedClock(Instant));

        Assert.True(verifier.Verify(Secret, code, sha256, Tolerance).IsValid);

        // The algorithm is part of the credential: a code computed with SHA-256
        // must not verify as SHA-1.
        Assert.False(verifier.Verify(Secret, code, Defaults, Tolerance).IsValid);
    }

    private static string ComputeCode(DateTime instant) => new Totp(Secret).ComputeTotp(instant);

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTime utcInstant) => UtcNow = new DateTimeOffset(utcInstant);

        public DateTimeOffset UtcNow { get; }
    }
}
