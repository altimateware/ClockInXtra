using Attendance.Application.Abstractions;
using OtpNet;

namespace Attendance.Infrastructure.Security;

/// <summary>
/// RFC 6238 TOTP verification, built on Otp.NET (decision TD-04).
/// </summary>
/// <remarks>
/// <para>
/// A standards-based implementation rather than a bespoke one, as Claude.md §13
/// requires: employees use ordinary authenticator applications, and the Key URI
/// Format defaults (HMAC-SHA-1, 6 digits, 30-second step) are what those
/// applications assume. HMAC-SHA-1 is unaffected by SHA-1 collision weaknesses.
/// </para>
/// <para>
/// <b>Verification is only half of the control.</b> A code stays valid for its
/// whole time step, and with the default ±1 step tolerance one code is usable for
/// about 90 seconds — longer if <c>Security.TotpStepTolerance</c> is raised.
/// This class therefore reports which time step matched, and the caller
/// must consume it through <c>core.usp_MfaCredential_TryConsumeTimeStep</c>
/// before treating the factor as satisfied. Only the database can make "used
/// exactly once" true across several application servers.
/// </para>
/// <para>
/// The clock is injected so that tests can verify behaviour at specific instants
/// — including the boundaries of the tolerance window — instead of waiting for
/// real time to pass.
/// </para>
/// </remarks>
public sealed class TotpVerifier : ITotpVerifier
{
    private readonly IClock _clock;

    /// <summary>Creates the verifier.</summary>
    public TotpVerifier(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <inheritdoc />
    public TotpVerificationResult Verify(
        ReadOnlySpan<byte> secret,
        string code,
        TotpParameters parameters,
        int stepTolerance)
    {
        // Reject anything that is not exactly the expected number of ASCII
        // digits before doing any cryptographic work. This costs nothing, keeps
        // malformed input away from the library's parsing, and means a flood of
        // junk codes cannot make the server do HMAC work on each one.
        if (!IsWellFormedCode(code, parameters.Digits) || secret.IsEmpty)
        {
            return TotpVerificationResult.Invalid;
        }

        if (parameters.PeriodSeconds <= 0 || stepTolerance < 0)
        {
            return TotpVerificationResult.Invalid;
        }

        // Otp.NET needs a byte[]; the copy is zeroed as soon as verification
        // finishes so the decrypted secret is not left lying in memory longer
        // than the operation needs it (§13, §38).
        byte[] secretCopy = secret.ToArray();

        try
        {
            Totp totp = new(
                secretCopy,
                step: parameters.PeriodSeconds,
                mode: ToHashMode(parameters.Algorithm),
                totpSize: parameters.Digits);

            VerificationWindow window = new(
                previous: stepTolerance,
                future: stepTolerance);

            bool isValid = totp.VerifyTotp(
                _clock.UtcNow.UtcDateTime,
                code,
                out long matchedTimeStep,
                window);

            return isValid
                ? TotpVerificationResult.Valid(matchedTimeStep)
                : TotpVerificationResult.Invalid;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secretCopy);
        }
    }

    private static bool IsWellFormedCode(string? code, int expectedDigits)
    {
        if (string.IsNullOrEmpty(code) || code.Length != expectedDigits)
        {
            return false;
        }

        foreach (char character in code)
        {
            // Deliberately not char.IsDigit: that accepts digits from other
            // scripts, which an authenticator never produces and which would
            // only widen what reaches the parser.
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static OtpHashMode ToHashMode(TotpAlgorithm algorithm) => algorithm switch
    {
        TotpAlgorithm.Sha256 => OtpHashMode.Sha256,
        TotpAlgorithm.Sha512 => OtpHashMode.Sha512,
        _ => OtpHashMode.Sha1,
    };
}
