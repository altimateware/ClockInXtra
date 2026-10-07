using System.Formats.Asn1;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Attendance.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Attendance.Infrastructure.Security.Attestation;

/// <summary>
/// Verifies an Apple App Attest attestation object (DEC-05).
/// </summary>
/// <remarks>
/// <para>
/// The device asks Apple's App Attest service to certify a key generated in the
/// Secure Enclave, binding it to a nonce derived from the server's challenge.
/// Apple returns a CBOR attestation object; this class verifies it against
/// Apple's root certificate <b>on our own servers</b>, so no attendance decision
/// depends on Apple being reachable. Only registration does, and only at the
/// moment the device first enrols.
/// </para>
/// <para>
/// The checks follow Apple's published validation sequence: chain to the App
/// Attest root, nonce derived as <c>SHA-256(authenticatorData ‖
/// SHA-256(challenge))</c> and compared against the certificate's App Attest
/// extension, the key identifier compared against the SHA-256 of the certified
/// public key, and the authenticator data's relying-party hash, counter and
/// environment compared against this deployment's identifiers.
/// </para>
/// <para>
/// App Attest is unavailable on simulators and in app extensions, which is
/// consistent with the iOS 15 floor proposed in OPEN-31.
/// </para>
/// </remarks>
public sealed class AppleAppAttestVerifier
{
    /// <summary>Apple's App Attest certificate extension, carrying the nonce.</summary>
    private const string NonceExtensionOid = "1.2.840.113635.100.8.2";

    private const string AttestationFormat = "apple-appattest";

    /// <summary>Authenticator data layout: 32 + 1 + 4 + 16 + 2 bytes before the credential id.</summary>
    private const int RelyingPartyIdHashLength = 32;
    private const int FlagsLength = 1;
    private const int CounterLength = 4;
    private const int AaguidLength = 16;
    private const int CredentialIdLengthLength = 2;

    private const int MinimumAuthenticatorDataLength =
        RelyingPartyIdHashLength + FlagsLength + CounterLength + AaguidLength + CredentialIdLengthLength;

    private static ReadOnlySpan<byte> ProductionAaguid => "appattest\0\0\0\0\0\0\0"u8;
    private static ReadOnlySpan<byte> DevelopmentAaguid => "appattestdevelop"u8;

    private readonly AppleAttestationOptions _options;

    /// <summary>Creates the verifier.</summary>
    public AppleAppAttestVerifier(IOptions<AttestationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Apple;
    }

    /// <summary>Verifies an App Attest attestation.</summary>
    public AttestationVerificationResult Verify(AttestationVerificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(_options.RootCertificatePemPath)
            || string.IsNullOrWhiteSpace(_options.TeamId)
            || string.IsNullOrWhiteSpace(_options.BundleId))
        {
            return AttestationVerificationResult.Rejected("APPLE_ATTESTATION_NOT_CONFIGURED");
        }

        if (!TryParseAttestationObject(request.Attestation, out AppleAttestationObject parsed))
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_OBJECT_MALFORMED");
        }

        X509Certificate2Collection chain = parsed.Certificates;

        try
        {
            if (chain.Count == 0)
            {
                return AttestationVerificationResult.Rejected("ATTESTATION_CHAIN_MALFORMED");
            }

            if (!TryValidateChain(chain, out string? chainFailure))
            {
                return AttestationVerificationResult.Rejected(chainFailure!);
            }

            return VerifyContents(parsed, request);
        }
        finally
        {
            foreach (X509Certificate2 certificate in chain)
            {
                certificate.Dispose();
            }
        }
    }

    private AttestationVerificationResult VerifyContents(
        AppleAttestationObject parsed,
        AttestationVerificationRequest request)
    {
        X509Certificate2 credentialCertificate = parsed.Certificates[0];

        // 1. The nonce ties the attestation to this server's challenge.
        byte[] clientDataHash = SHA256.HashData(request.Challenge.Span);

        byte[] expectedNonce = SHA256.HashData(
            [.. parsed.AuthenticatorData, .. clientDataHash]);

        if (!TryReadNonce(credentialCertificate, out byte[] presentedNonce)
            || !CryptographicOperations.FixedTimeEquals(presentedNonce, expectedNonce))
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_NONCE_MISMATCH");
        }

        // 2. The certified key must be the key being registered, and its
        //    identifier must be the one the device claimed.
        if (!AndroidKeyAttestationVerifier.TryGetUncompressedPoint(credentialCertificate, out byte[] attestedKey))
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_KEY_UNREADABLE");
        }

        if (!attestedKey.AsSpan().SequenceEqual(request.PublicKey.Span))
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_KEY_MISMATCH");
        }

        byte[] keyIdentifier = SHA256.HashData(attestedKey);

        if (!CryptographicOperations.FixedTimeEquals(keyIdentifier, request.KeyId.Span))
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_KEY_ID_MISMATCH");
        }

        // 3. Authenticator data: this application, this environment, first use.
        return VerifyAuthenticatorData(parsed.AuthenticatorData, keyIdentifier);
    }

    private AttestationVerificationResult VerifyAuthenticatorData(byte[] authenticatorData, byte[] keyIdentifier)
    {
        if (authenticatorData.Length < MinimumAuthenticatorDataLength)
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_AUTHENTICATOR_DATA_MALFORMED");
        }

        ReadOnlySpan<byte> data = authenticatorData;

        // The relying party is this application: "<teamId>.<bundleId>". Without
        // this check, an attestation from any App Attest application would pass.
        byte[] expectedRelyingParty = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{_options.TeamId}.{_options.BundleId}"));

        if (!CryptographicOperations.FixedTimeEquals(
                data[..RelyingPartyIdHashLength], expectedRelyingParty))
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_APPLICATION_MISMATCH");
        }

        int offset = RelyingPartyIdHashLength + FlagsLength;

        // A freshly attested key has never been used, so its counter is zero.
        // Anything else means this is not a first attestation.
        uint counter = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, CounterLength));

        if (counter != 0)
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_COUNTER_NOT_ZERO");
        }

        offset += CounterLength;

        ReadOnlySpan<byte> aaguid = data.Slice(offset, AaguidLength);

        bool isProduction = aaguid.SequenceEqual(ProductionAaguid);
        bool isDevelopment = aaguid.SequenceEqual(DevelopmentAaguid);

        if (!isProduction && !isDevelopment)
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_ENVIRONMENT_UNKNOWN");
        }

        if (isDevelopment && !_options.AllowDevelopmentEnvironment)
        {
            // A development attestation proves nothing about a production device.
            return AttestationVerificationResult.Rejected("ATTESTATION_DEVELOPMENT_ENVIRONMENT");
        }

        offset += AaguidLength;

        int credentialIdLength =
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, CredentialIdLengthLength));

        offset += CredentialIdLengthLength;

        if (credentialIdLength != keyIdentifier.Length || data.Length < offset + credentialIdLength)
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_CREDENTIAL_ID_MISMATCH");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                data.Slice(offset, credentialIdLength), keyIdentifier))
        {
            return AttestationVerificationResult.Rejected("ATTESTATION_CREDENTIAL_ID_MISMATCH");
        }

        return AttestationVerificationResult.Accepted(AttestationLevel.Hardware);
    }

    /// <summary>
    /// Reads the nonce from Apple's App Attest certificate extension.
    /// </summary>
    /// <remarks>
    /// <code>
    /// SEQUENCE {
    ///     [1] EXPLICIT SEQUENCE {
    ///         OCTET STRING  -- SHA-256 nonce
    ///     }
    /// }
    /// </code>
    /// </remarks>
    private static bool TryReadNonce(X509Certificate2 certificate, out byte[] nonce)
    {
        nonce = [];

        X509Extension? extension = certificate.Extensions
            .FirstOrDefault(e => string.Equals(e.Oid?.Value, NonceExtensionOid, StringComparison.Ordinal));

        if (extension is null)
        {
            return false;
        }

        try
        {
            AsnReader reader = new(extension.RawData, AsnEncodingRules.DER);
            AsnReader outer = reader.ReadSequence();
            AsnReader tagged = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1));

            nonce = tagged.ReadOctetString();

            return nonce.Length == 32;
        }
        catch (AsnContentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryValidateChain(X509Certificate2Collection chain, out string? failure)
    {
        failure = null;

        using X509Chain validator = new();

        validator.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        validator.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        try
        {
            X509Certificate2Collection roots = [];
            roots.ImportFromPemFile(_options.RootCertificatePemPath!);
            validator.ChainPolicy.CustomTrustStore.AddRange(roots);
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

    /// <summary>
    /// Decodes the CBOR attestation object.
    /// </summary>
    /// <remarks>
    /// Shape: <c>{ "fmt": "apple-appattest", "attStmt": { "x5c": [...],
    /// "receipt": bytes }, "authData": bytes }</c>. Every failure path returns
    /// false rather than throwing — this is attacker-supplied input.
    /// </remarks>
    private static bool TryParseAttestationObject(
        ReadOnlyMemory<byte> attestation,
        out AppleAttestationObject parsed)
    {
        parsed = default;

        X509Certificate2Collection certificates = [];

        try
        {
            CborReader reader = new(attestation, CborConformanceMode.Lax);

            int? entries = reader.ReadStartMap();
            byte[]? authenticatorData = null;
            bool formatVerified = false;

            for (int i = 0; entries is null ? reader.PeekState() != CborReaderState.EndMap : i < entries; i++)
            {
                string key = reader.ReadTextString();

                switch (key)
                {
                    case "fmt":
                        formatVerified = string.Equals(reader.ReadTextString(), AttestationFormat, StringComparison.Ordinal);
                        break;

                    case "authData":
                        authenticatorData = reader.ReadByteString();
                        break;

                    case "attStmt":
                        ReadStatement(reader, certificates);
                        break;

                    default:
                        reader.SkipValue();
                        break;
                }
            }

            reader.ReadEndMap();

            if (!formatVerified || authenticatorData is null || certificates.Count == 0)
            {
                return Discard(certificates);
            }

            parsed = new AppleAttestationObject(authenticatorData, certificates);
            return true;
        }
        catch (CborContentException)
        {
            return Discard(certificates);
        }
        catch (InvalidOperationException)
        {
            return Discard(certificates);
        }
        catch (CryptographicException)
        {
            return Discard(certificates);
        }

        static bool Discard(X509Certificate2Collection partial)
        {
            foreach (X509Certificate2 certificate in partial)
            {
                certificate.Dispose();
            }

            return false;
        }
    }

    private static void ReadStatement(CborReader reader, X509Certificate2Collection certificates)
    {
        int? statementEntries = reader.ReadStartMap();

        for (int i = 0; statementEntries is null ? reader.PeekState() != CborReaderState.EndMap : i < statementEntries; i++)
        {
            string key = reader.ReadTextString();

            if (string.Equals(key, "x5c", StringComparison.Ordinal))
            {
                int? count = reader.ReadStartArray();

                for (int c = 0; count is null ? reader.PeekState() != CborReaderState.EndArray : c < count; c++)
                {
                    certificates.Add(X509CertificateLoader.LoadCertificate(reader.ReadByteString()));
                }

                reader.ReadEndArray();
            }
            else
            {
                // The receipt is Apple's own signed artefact, used for fraud
                // metrics rather than for this decision. It is not parsed here.
                reader.SkipValue();
            }
        }

        reader.ReadEndMap();
    }

    private readonly record struct AppleAttestationObject(
        byte[] AuthenticatorData,
        X509Certificate2Collection Certificates);
}
