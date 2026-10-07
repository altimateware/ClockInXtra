using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Attendance.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Attendance.Infrastructure.Security.Attestation;

/// <summary>
/// Verifies an Android key attestation certificate chain.
/// </summary>
/// <remarks>
/// <para>
/// The device generates the registration key inside its keystore with the
/// server's challenge embedded, and the keystore issues a certificate chain for
/// it rooting to Google. The leaf certificate carries an extension the
/// <em>platform</em> writes and the application cannot forge, stating where the
/// key lives, what challenge it was created for, which application asked, and
/// whether the device booted verified.
/// </para>
/// <para>
/// <b>Wire format.</b> The client sends the chain as a DER-encoded
/// <c>SEQUENCE OF Certificate</c>, leaf first. This is deliberately not PKCS#7:
/// a plain sequence is trivial to build on the device from the
/// <c>Certificate[]</c> the keystore returns, and needs no container parsing
/// here.
/// </para>
/// <para>
/// <b>What this proves and what it does not.</b> A passing verification shows the
/// private key was generated in hardware on a device that booted a verified
/// operating system, and cannot be exported. It does not prove the device is free
/// of malware, and it is not a substitute for server-side registration and
/// revocation controls (§25, §65).
/// </para>
/// </remarks>
public sealed class AndroidKeyAttestationVerifier
{
    private readonly AndroidAttestationOptions _options;
    private readonly IAttestationRevocationList _revocationList;

    /// <summary>Creates the verifier.</summary>
    public AndroidKeyAttestationVerifier(
        IOptions<AttestationOptions> options,
        IAttestationRevocationList revocationList)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(revocationList);

        _options = options.Value.Android;
        _revocationList = revocationList;
    }

    /// <summary>Verifies an Android attestation.</summary>
    public AttestationVerificationResult Verify(AttestationVerificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(_options.RootCertificatePemPath)
            || string.IsNullOrWhiteSpace(_options.ExpectedPackageName))
        {
            // Refusing is the only safe answer. A verifier without a root
            // certificate or an expected package cannot distinguish this
            // organisation's application from any other, and accepting would make
            // the whole control decorative.
            return AttestationVerificationResult.Rejected("ANDROID_ATTESTATION_NOT_CONFIGURED");
        }

        X509Certificate2Collection chain = ParseChain(request.Attestation);

        if (chain.Count == 0)
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_CHAIN_MALFORMED");
        }

        using X509Certificate2 leaf = chain[0];

        try
        {
            if (!TryValidateChain(chain, out string? chainFailure))
            {
                return AttestationVerificationResult.Rejected(chainFailure!);
            }

            if (_revocationList.IsRevoked(chain, out string? revokedSerial))
            {
                return AttestationVerificationResult.Rejected($"ATTESTATION_KEY_REVOKED:{revokedSerial}");
            }

            X509Extension? extension = leaf.Extensions
                .FirstOrDefault(e => string.Equals(e.Oid?.Value, AndroidAttestationExtension.Oid, StringComparison.Ordinal));

            if (extension is null)
            {
                return AttestationVerificationResult.Rejected("ATTESTATION_EXTENSION_MISSING");
            }

            if (AndroidAttestationExtension.TryParse(extension.RawData) is not { } attestation)
            {
                return AttestationVerificationResult.Rejected("ATTESTATION_EXTENSION_MALFORMED");
            }

            return VerifyAttestationContents(attestation, leaf, request);
        }
        finally
        {
            // The leaf is disposed by the using above; dispose the rest.
            for (int i = 1; i < chain.Count; i++)
            {
                chain[i].Dispose();
            }
        }
    }

    private AttestationVerificationResult VerifyAttestationContents(
        AndroidAttestationExtension attestation,
        X509Certificate2 leaf,
        AttestationVerificationRequest request)
    {
        // 1. The challenge binds this attestation to one server-issued value.
        //    Without it, a previously captured attestation would be replayable
        //    (threat TH-10). Compared in fixed time out of habit, not necessity.
        if (!CryptographicOperations.FixedTimeEquals(
                attestation.AttestationChallenge, request.Challenge.Span))
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_CHALLENGE_MISMATCH");
        }

        // 2. The attested key must be the key being registered. Otherwise a
        //    genuine attestation could be presented alongside somebody else's
        //    public key.
        if (!TryGetUncompressedPoint(leaf, out byte[] attestedKey)
            || !attestedKey.AsSpan().SequenceEqual(request.PublicKey.Span))
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_KEY_MISMATCH");
        }

        // 3. Where the key lives.
        AndroidSecurityLevel required = _options.RequireStrongBox
            ? AndroidSecurityLevel.StrongBox
            : AndroidSecurityLevel.TrustedEnvironment;

        if (attestation.AttestationSecurityLevel < required)
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_SECURITY_LEVEL_INSUFFICIENT");
        }

        // 4. Boot integrity. An unlocked bootloader means the operating system
        //    may have been replaced, and nothing the app says about itself can
        //    be believed on such a device.
        if (_options.RequireVerifiedBoot)
        {
            if (attestation.RootOfTrust is not { } rootOfTrust)
            {
                return AttestationVerificationResult.Rejected("ATTESTATION_ROOT_OF_TRUST_MISSING");
            }

            if (!rootOfTrust.DeviceLocked)
            {
                return AttestationVerificationResult.Rejected("ATTESTATION_BOOTLOADER_UNLOCKED");
            }

            if (rootOfTrust.VerifiedBootState != AndroidVerifiedBootState.Verified)
            {
                return AttestationVerificationResult.Rejected("ATTESTATION_BOOT_STATE_UNVERIFIED");
            }
        }

        // 5. Which application asked. The platform fills this in; the app cannot.
        if (!VerifyApplicationIdentity(attestation.AttestationApplicationId, out string? applicationFailure))
        {
            return AttestationVerificationResult.Rejected(applicationFailure!);
        }

        return AttestationVerificationResult.Accepted(AttestationLevel.Hardware);
    }

    /// <summary>
    /// Checks the package name and signing-certificate digest recorded in the
    /// attestation.
    /// </summary>
    /// <remarks>
    /// <code>
    /// AttestationApplicationId ::= SEQUENCE {
    ///     packageInfos      SET OF AttestationPackageInfo,
    ///     signatureDigests  SET OF OCTET_STRING,
    /// }
    /// AttestationPackageInfo ::= SEQUENCE {
    ///     packageName  OCTET_STRING,
    ///     version      INTEGER,
    /// }
    /// </code>
    /// Checking the package name alone would not be enough: anyone can publish an
    /// application using a chosen package name. The signing-certificate digest is
    /// what ties it to this organisation's signing key.
    /// </remarks>
    private bool VerifyApplicationIdentity(byte[]? attestationApplicationId, out string? failure)
    {
        failure = null;

        if (attestationApplicationId is null)
        {
            failure = "ATTESTATION_APPLICATION_ID_MISSING";
            return false;
        }

        try
        {
            AsnReader reader = new(attestationApplicationId, AsnEncodingRules.DER);
            AsnReader applicationId = reader.ReadSequence();

            AsnReader packageInfos = applicationId.ReadSetOf();
            bool packageMatched = false;

            while (packageInfos.HasData)
            {
                AsnReader packageInfo = packageInfos.ReadSequence();
                string packageName = Encoding.UTF8.GetString(packageInfo.ReadOctetString());
                _ = packageInfo.ReadInteger();

                if (string.Equals(packageName, _options.ExpectedPackageName, StringComparison.Ordinal))
                {
                    packageMatched = true;
                }
            }

            if (!packageMatched)
            {
                failure = "ATTESTATION_PACKAGE_MISMATCH";
                return false;
            }

            if (_options.ExpectedSigningCertificateDigests.Count == 0)
            {
                // Configured without any permitted digest: the package check
                // alone is not a control, so this is refused rather than passed.
                failure = "ANDROID_ATTESTATION_NOT_CONFIGURED";
                return false;
            }

            AsnReader digests = applicationId.ReadSetOf();

            while (digests.HasData)
            {
                string digest = Convert.ToHexString(digests.ReadOctetString());

                if (_options.ExpectedSigningCertificateDigests
                    .Any(expected => string.Equals(expected, digest, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            failure = "ATTESTATION_SIGNING_CERTIFICATE_MISMATCH";
            return false;
        }
        catch (AsnContentException)
        {
            failure = "ATTESTATION_APPLICATION_ID_MALFORMED";
            return false;
        }
        catch (InvalidOperationException)
        {
            failure = "ATTESTATION_APPLICATION_ID_MALFORMED";
            return false;
        }
    }

    private bool TryValidateChain(X509Certificate2Collection chain, out string? failure)
    {
        failure = null;

        using X509Chain validator = new();

        validator.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        validator.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        // Google's attestation certificates carry no CRL distribution point or
        // OCSP responder; revocation is published as a status list, checked
        // separately from a locally provisioned copy.
        validator.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        try
        {
            validator.ChainPolicy.CustomTrustStore.AddRange(
                LoadCertificates(_options.RootCertificatePemPath!));
        }
        catch (CryptographicException)
        {
            failure = "ATTESTATION_ROOT_CERTIFICATE_UNREADABLE";
            return false;
        }
        catch (IOException)
        {
            failure = "ATTESTATION_ROOT_CERTIFICATE_UNREADABLE";
            return false;
        }

        for (int i = 1; i < chain.Count; i++)
        {
            validator.ChainPolicy.ExtraStore.Add(chain[i]);
        }

        if (!validator.Build(chain[0]))
        {
            failure = "ATTESTATION_CHAIN_UNTRUSTED";
            return false;
        }

        return true;
    }

    private static X509Certificate2Collection LoadCertificates(string pemPath)
    {
        X509Certificate2Collection collection = [];
        collection.ImportFromPemFile(pemPath);
        return collection;
    }

    /// <summary>
    /// Parses a DER <c>SEQUENCE OF Certificate</c>, leaf first.
    /// </summary>
    private static X509Certificate2Collection ParseChain(ReadOnlyMemory<byte> attestation)
    {
        X509Certificate2Collection chain = [];

        try
        {
            AsnReader reader = new(attestation, AsnEncodingRules.DER);
            AsnReader sequence = reader.ReadSequence();

            while (sequence.HasData)
            {
                chain.Add(X509CertificateLoader.LoadCertificate(sequence.ReadEncodedValue().Span));
            }

            return chain;
        }
        catch (AsnContentException)
        {
            return Discard(chain);
        }
        catch (InvalidOperationException)
        {
            return Discard(chain);
        }
        catch (CryptographicException)
        {
            return Discard(chain);
        }

        static X509Certificate2Collection Discard(X509Certificate2Collection partial)
        {
            foreach (X509Certificate2 certificate in partial)
            {
                certificate.Dispose();
            }

            return [];
        }
    }

    /// <summary>
    /// Exports a certificate's P-256 public key as the uncompressed point form
    /// stored in <c>core.Device.PublicKey</c>: <c>0x04 ‖ X(32) ‖ Y(32)</c>.
    /// </summary>
    internal static bool TryGetUncompressedPoint(X509Certificate2 certificate, out byte[] point)
    {
        point = [];

        try
        {
            using ECDsa? key = certificate.GetECDsaPublicKey();

            if (key is null)
            {
                return false;
            }

            ECParameters parameters = key.ExportParameters(includePrivateParameters: false);

            if (parameters.Q.X is not { Length: 32 } x || parameters.Q.Y is not { Length: 32 } y)
            {
                return false;
            }

            point = new byte[65];
            point[0] = 0x04;
            x.CopyTo(point, 1);
            y.CopyTo(point, 33);

            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            // Windows CNG raises this rather than CryptographicException for a
            // key on an unsupported curve — the same trap recorded as defect 7.
            return false;
        }
    }
}
