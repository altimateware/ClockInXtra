namespace Attendance.Application.Abstractions;

/// <summary>
/// Verifies a six-digit authenticator code against an enrolled TOTP secret.
/// </summary>
/// <remarks>
/// <para>
/// Implements the second factor required by Claude.md §12 and §13, using TOTP
/// (RFC 6238) rather than an invented scheme. Parameters are the Key URI Format
/// defaults — HMAC-SHA-1, 6 digits, 30-second step — which is what makes the
/// enrolment work with ordinary authenticator applications (TD-04). HMAC-SHA-1
/// is unaffected by SHA-1 collision weaknesses.
/// </para>
/// <para>
/// <b>Verification alone is not sufficient.</b> A code stays valid for its whole
/// time step, and with a ±1 step tolerance a single code is usable for about 90
/// seconds — long enough for someone who glanced at the screen. That is why
/// this interface returns the <em>matched time step</em>: the caller must then
/// consume it through the database
/// (<c>core.usp_MfaCredential_TryConsumeTimeStep</c>), which refuses any step at
/// or below the last accepted one. Verification and consumption are separate
/// because only the database can make "used exactly once" true across several
/// application servers.
/// </para>
/// <para>
/// The secret is handed over decrypted, for the duration of the call only. It is
/// never logged, never returned in a response, and never written anywhere except
/// the encrypted column it came from (§13, §33).
/// </para>
/// </remarks>
public interface ITotpVerifier
{
    /// <summary>
    /// Checks a code against a secret, within the given time-step tolerance.
    /// </summary>
    /// <param name="secret">The decrypted shared secret.</param>
    /// <param name="code">The code the employee typed.</param>
    /// <param name="parameters">Algorithm parameters recorded at enrolment.</param>
    /// <param name="stepTolerance">
    /// How many steps either side of the current one are accepted. One step
    /// allows for ordinary clock drift between the phone and the server.
    /// <para>
    /// <b>This is a security parameter, not a convenience setting.</b> Raising
    /// it lengthens the period in which a code observed over someone's shoulder
    /// is still worth something — replay protection means it can be spent
    /// only once, but the thief has longer to spend it first. The callers read
    /// it from <c>Security.TotpStepTolerance</c>, which the database bounds to
    /// 1–10, and a negative value is refused here.
    /// </para>
    /// </param>
    /// <returns>
    /// A result carrying the matched time step when the code is valid. The
    /// caller must consume that step before treating the factor as satisfied.
    /// </returns>
    TotpVerificationResult Verify(
        ReadOnlySpan<byte> secret,
        string code,
        TotpParameters parameters,
        int stepTolerance);
}

/// <summary>
/// Algorithm parameters for a TOTP credential.
/// </summary>
/// <param name="Algorithm">Hash algorithm recorded at enrolment.</param>
/// <param name="Digits">Number of digits in a code, normally 6.</param>
/// <param name="PeriodSeconds">Length of a time step, normally 30.</param>
/// <remarks>
/// Only what was recorded with the credential at enrolment. The accepted step
/// tolerance is deliberately <i>not</i> here: it is one policy for every
/// credential, it can be changed after enrolment, and holding it in a
/// per-credential record invited each repository to invent its own value —
/// which is exactly what happened before it became a setting. It is passed to
/// <see cref="ITotpVerifier.Verify"/> instead.
/// </remarks>
public readonly record struct TotpParameters(
    TotpAlgorithm Algorithm,
    int Digits,
    int PeriodSeconds)
{
    /// <summary>
    /// The parameters used for new enrolments: the Key URI Format defaults,
    /// which are what authenticator applications assume.
    /// </summary>
    public static TotpParameters Default => new(TotpAlgorithm.Sha1, 6, 30);
}

/// <summary>Hash algorithm used by a TOTP credential.</summary>
public enum TotpAlgorithm
{
    /// <summary>HMAC-SHA-1: the Key URI Format default and the interoperable choice.</summary>
    Sha1 = 0,

    /// <summary>HMAC-SHA-256.</summary>
    Sha256 = 1,

    /// <summary>HMAC-SHA-512.</summary>
    Sha512 = 2,
}

/// <summary>
/// The outcome of verifying an authenticator code.
/// </summary>
/// <param name="IsValid">Whether the code matched within tolerance.</param>
/// <param name="MatchedTimeStep">
/// The time step that matched. Meaningful only when <paramref name="IsValid"/>
/// is true, and it must be consumed in the database before the factor counts as
/// satisfied.
/// </param>
public readonly record struct TotpVerificationResult(bool IsValid, long MatchedTimeStep)
{
    /// <summary>A failed verification.</summary>
    public static TotpVerificationResult Invalid => new(false, 0L);

    /// <summary>A successful verification at the given time step.</summary>
    public static TotpVerificationResult Valid(long matchedTimeStep) => new(true, matchedTimeStep);
}
