namespace Attendance.Infrastructure.Security.Attestation;

/// <summary>
/// Configuration for platform attestation verification.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here has a usable default.</b> The root certificates, the
/// application identifiers and the team identifier are deployment facts, and a
/// verifier configured with a guess would either reject every genuine device or —
/// far worse — accept an attestation that proves nothing. Each verifier refuses
/// to operate until its own options are supplied, which surfaces as a rejected
/// registration rather than a silently weakened one.
/// </para>
/// <para>
/// Root certificates are provisioned as PEM files by the operator. They are not
/// committed to source control and not fetched at runtime: a root certificate
/// downloaded over the network on demand is a root certificate an attacker on the
/// path can choose.
/// </para>
/// </remarks>
public sealed class AttestationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Attestation";

    /// <summary>Android key attestation settings.</summary>
    public AndroidAttestationOptions Android { get; set; } = new();

    /// <summary>Apple App Attest settings.</summary>
    public AppleAttestationOptions Apple { get; set; } = new();
}

/// <summary>
/// Android key attestation settings.
/// </summary>
public sealed class AndroidAttestationOptions
{
    /// <summary>
    /// Path to a PEM file holding the Google hardware attestation root
    /// certificate(s).
    /// </summary>
    /// <remarks>
    /// Google publishes these; the operator downloads them once, verifies them
    /// out of band and places them on the server. Deliberately not fetched at
    /// runtime.
    /// </remarks>
    public string? RootCertificatePemPath { get; set; }

    /// <summary>
    /// The application package name the attestation must be bound to, for
    /// example <c>com.altimateware.clockinxtra</c>.
    /// </summary>
    /// <remarks>
    /// Without this check, an attestation produced by <em>any</em> application on
    /// <em>any</em> Android device would satisfy registration. The package name
    /// and its signing-certificate digest appear in the attestation's
    /// <c>attestationApplicationId</c> field, which the platform fills in and the
    /// application cannot forge.
    /// </remarks>
    public string? ExpectedPackageName { get; set; }

    /// <summary>
    /// Hex-encoded SHA-256 digests of the signing certificates permitted for that
    /// package. Several are allowed, so a signing-key rotation does not lock
    /// every employee out.
    /// </summary>
    public IList<string> ExpectedSigningCertificateDigests { get; } = [];

    /// <summary>
    /// Whether the key must be held in a StrongBox secure element rather than
    /// only in the Trusted Execution Environment.
    /// </summary>
    /// <remarks>
    /// StrongBox is a discrete security chip and is not present on every device,
    /// so requiring it excludes hardware that is otherwise perfectly acceptable.
    /// Default is false: a TEE-backed key already satisfies "hardware-backed and
    /// non-exportable".
    /// </remarks>
    public bool RequireStrongBox { get; set; }

    /// <summary>
    /// Whether the device must report a locked bootloader and a verified boot
    /// state of <c>Verified</c>.
    /// </summary>
    /// <remarks>
    /// This is the check that makes root detection more than a client-side
    /// courtesy (§25). An unlocked bootloader means the operating system can have
    /// been replaced wholesale, and nothing the application reports about itself
    /// can be trusted on such a device.
    /// </remarks>
    public bool RequireVerifiedBoot { get; set; } = true;

    /// <summary>
    /// Path to a locally provisioned copy of Google's attestation revocation
    /// status list (JSON).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Google serves this list over the internet. Fetching it live would make
    /// device registration depend on an external service being reachable, which
    /// §2.1 rules out. The operator refreshes the file on a schedule instead, so
    /// the check runs entirely on-premises.
    /// </para>
    /// <para>
    /// When no file is configured, revocation is not checked and that fact is
    /// recorded on every accepted registration rather than passing silently.
    /// </para>
    /// </remarks>
    public string? RevocationStatusListPath { get; set; }
}

/// <summary>
/// Apple App Attest settings (DEC-05).
/// </summary>
public sealed class AppleAttestationOptions
{
    /// <summary>
    /// Path to a PEM file holding the Apple App Attest root certificate.
    /// </summary>
    public string? RootCertificatePemPath { get; set; }

    /// <summary>The Apple team identifier, for example <c>ABCDE12345</c>.</summary>
    public string? TeamId { get; set; }

    /// <summary>The bundle identifier, for example <c>com.altimateware.clockinxtra</c>.</summary>
    public string? BundleId { get; set; }

    /// <summary>
    /// Whether attestations produced by the development environment are
    /// accepted.
    /// </summary>
    /// <remarks>
    /// <b>Must be false in production.</b> A development attestation is issued by
    /// Apple's development environment and proves nothing about a production
    /// device. It is separated here so that enabling it is a visible
    /// configuration act rather than a code path somebody forgets to remove.
    /// </remarks>
    public bool AllowDevelopmentEnvironment { get; set; }
}
