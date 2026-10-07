using Attendance.Domain.ValueObjects;

namespace Attendance.Application.Abstractions;

/// <summary>
/// Verifies a platform attestation presented during device registration.
/// </summary>
/// <remarks>
/// <para>
/// Registration is the moment a key becomes trusted for every future attendance
/// transaction, so it is the one place worth spending a platform attestation on.
/// Two mechanisms are in scope, both first-party and documented by their vendors:
/// </para>
/// <list type="bullet">
///   <item><b>Android Key Attestation.</b> The device returns an X.509 chain for
///   the freshly generated key; the attestation extension (OID
///   1.3.6.1.4.1.11129.2.1.17) carries the challenge, the security level and the
///   verified-boot state. Verification is chain validation against Google's
///   published root plus checks on those fields.</item>
///   <item><b>Apple App Attest (DEC-05).</b> The device produces a CBOR
///   attestation object over a nonce derived from our challenge. Verification is
///   performed on our servers against Apple's root, so no attendance decision
///   depends on Apple being reachable at clock-in time — only registration does.
///   It is unavailable on simulators and in app extensions.</item>
/// </list>
/// <para>
/// <b>What this establishes, and what it does not.</b> A verified hardware
/// attestation shows the private key was generated in a secure element and cannot
/// be exported, which is what makes device binding meaningful. It does not prove
/// the handset is free of malware, and it is not a substitute for the server-side
/// device registration and revocation controls (§25, §65).
/// </para>
/// <para>
/// Implementations live in Infrastructure and must not throw on malformed input:
/// every byte here arrives from an untrusted client, so a bad blob is an expected
/// outcome, not an exception.
/// </para>
/// </remarks>
public interface IDeviceAttestationVerifier
{
    /// <summary>
    /// Verifies an attestation and reports the level it establishes.
    /// </summary>
    Task<AttestationVerificationResult> VerifyAsync(
        AttestationVerificationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// An attestation to verify.
/// </summary>
/// <param name="Platform">Which mechanism applies.</param>
/// <param name="Attestation">
/// The platform blob: an X.509 chain for Android, a CBOR attestation object for
/// iOS.
/// </param>
/// <param name="Challenge">
/// The server-issued challenge the attestation must be bound to. Checking this is
/// what makes a captured attestation useless (threat TH-10).
/// </param>
/// <param name="PublicKey">
/// The key being registered. The attestation must cover <em>this</em> key, or an
/// attacker could present a genuine attestation for a key they do not hold.
/// </param>
/// <param name="KeyId">
/// Apple's key identifier, required for App Attest and unused on Android.
/// </param>
public readonly record struct AttestationVerificationRequest(
    DevicePlatform Platform,
    ReadOnlyMemory<byte> Attestation,
    ReadOnlyMemory<byte> Challenge,
    ReadOnlyMemory<byte> PublicKey,
    ReadOnlyMemory<byte> KeyId);

/// <summary>
/// The outcome of verifying an attestation.
/// </summary>
/// <param name="IsAccepted">Whether it satisfied the configured requirement.</param>
/// <param name="Level">The level established.</param>
/// <param name="ReasonCode">
/// Why it was refused, for the security event trail. Never returned to the
/// client, which sees only ATTESTATION_REJECTED: the detail would tell an
/// attacker which part of their forgery to fix.
/// </param>
public readonly record struct AttestationVerificationResult(
    bool IsAccepted,
    AttestationLevel Level,
    string? ReasonCode)
{
    /// <summary>A verified attestation at the given level.</summary>
    public static AttestationVerificationResult Accepted(AttestationLevel level) =>
        new(true, level, null);

    /// <summary>A refused attestation.</summary>
    public static AttestationVerificationResult Rejected(string reasonCode) =>
        new(false, AttestationLevel.None, reasonCode);
}
