using System.Formats.Asn1;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// Builds synthetic attestation material for the verifier tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is and is not.</b> A genuine attestation can only be produced by
/// a real handset with a real secure element, signed by Google or Apple. This
/// builder issues its own root and leaf and constructs the same structures, which
/// lets every decision the verifiers make be exercised: the challenge binding,
/// the security level, the boot state, the application identity, the nonce
/// derivation, the key identifier.
/// </para>
/// <para>
/// What it cannot prove is that a real device's output parses — the structures
/// here are built from the published specifications, and a specification can be
/// misread. That gap is recorded as an open verification item and closes the
/// first time the mobile application runs on hardware.
/// </para>
/// </remarks>
internal static class AttestationTestBuilder
{
    internal const string AttestationOid = "1.3.6.1.4.1.11129.2.1.17";
    internal const string AppleNonceOid = "1.2.840.113635.100.8.2";

    internal const string PackageName = "com.contoso.clockinxtra";
    internal const string TeamId = "ABCDE12345";
    internal const string BundleId = "com.contoso.clockinxtra";

    /// <summary>SHA-256 of a signing certificate, as the options expect it.</summary>
    internal static string SigningDigestHex { get; } = Convert.ToHexString(SHA256.HashData("signing-cert"u8));

    /// <summary>Creates a self-signed certificate authority.</summary>
    internal static X509Certificate2 CreateRoot(string subject = "CN=ClockInXtra Test Root")
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new(subject, key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>Issues a leaf certificate carrying the given extension.</summary>
    internal static X509Certificate2 IssueLeaf(
        X509Certificate2 issuer,
        ECDsa leafKey,
        string extensionOid,
        byte[] extensionValue)
    {
        CertificateRequest request = new("CN=ClockInXtra Test Device", leafKey, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509Extension(new Oid(extensionOid), extensionValue, critical: false));

        byte[] serial = new byte[8];
        RandomNumberGenerator.Fill(serial);

        return request.Create(
            issuer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(29), serial);
    }

    /// <summary>Writes certificates to a PEM file and returns the path.</summary>
    internal static string WritePem(string directory, params X509Certificate2[] certificates)
    {
        string path = Path.Combine(directory, $"{Guid.NewGuid():N}.pem");
        StringBuilder pem = new();

        foreach (X509Certificate2 certificate in certificates)
        {
            pem.AppendLine(certificate.ExportCertificatePem());
        }

        File.WriteAllText(path, pem.ToString());
        return path;
    }

    /// <summary>Encodes a chain as the DER <c>SEQUENCE OF Certificate</c> the API expects.</summary>
    internal static byte[] EncodeChain(params X509Certificate2[] certificates)
    {
        AsnWriter writer = new(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            foreach (X509Certificate2 certificate in certificates)
            {
                writer.WriteEncodedValue(certificate.RawData);
            }
        }

        return writer.Encode();
    }

    /// <summary>The uncompressed P-256 point for a key, as stored on the device row.</summary>
    internal static byte[] UncompressedPoint(ECDsa key)
    {
        ECParameters parameters = key.ExportParameters(includePrivateParameters: false);

        byte[] point = new byte[65];
        point[0] = 0x04;
        parameters.Q.X!.CopyTo(point, 1);
        parameters.Q.Y!.CopyTo(point, 33);

        return point;
    }

    // ---- Android -----------------------------------------------------------

    /// <summary>
    /// Builds an Android <c>KeyDescription</c> extension value.
    /// </summary>
    internal static byte[] BuildAndroidExtension(
        byte[] challenge,
        int securityLevel = 1,
        bool includeRootOfTrust = true,
        bool deviceLocked = true,
        int verifiedBootState = 0,
        string packageName = PackageName,
        string? signingDigestHex = null,
        bool includeApplicationId = true,
        bool applicationIdInHardwareList = false,
        bool rootOfTrustInSoftwareList = false)
    {
        AsnWriter writer = new(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            writer.WriteInteger(400);                      // attestationVersion (KeyMint 4.0)
            WriteEnumerated(writer, securityLevel);        // attestationSecurityLevel
            writer.WriteInteger(400);                      // keyMintVersion
            WriteEnumerated(writer, securityLevel);        // keyMintSecurityLevel
            writer.WriteOctetString(challenge);            // attestationChallenge
            writer.WriteOctetString([]);                   // uniqueId

            // softwareEnforced. The application id belongs here by default
            // because that is where a real KeyMint 4.0 implementation put it
            // (captured from the Android emulator; see the golden-sample test).
            // An earlier version of this builder put it in the hardware list and
            // tagged it implicitly — matching the parser's misreading of the
            // schema rather than the platform, so both agreed and both were wrong.
            using (writer.PushSequence())
            {
                if (includeRootOfTrust && rootOfTrustInSoftwareList)
                {
                    WriteRootOfTrust(writer, deviceLocked, verifiedBootState);
                }

                if (includeApplicationId && !applicationIdInHardwareList)
                {
                    WriteApplicationId(writer, packageName, signingDigestHex);
                }
            }

            // hardwareEnforced (teeEnforced in older schema revisions)
            using (writer.PushSequence())
            {
                if (includeRootOfTrust && !rootOfTrustInSoftwareList)
                {
                    WriteRootOfTrust(writer, deviceLocked, verifiedBootState);
                }

                if (includeApplicationId && applicationIdInHardwareList)
                {
                    WriteApplicationId(writer, packageName, signingDigestHex);
                }
            }
        }

        return writer.Encode();
    }

    /// <summary><c>rootOfTrust [704] EXPLICIT RootOfTrust</c>.</summary>
    private static void WriteRootOfTrust(AsnWriter writer, bool deviceLocked, int verifiedBootState)
    {
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 704)))
        using (writer.PushSequence())
        {
            writer.WriteOctetString(SHA256.HashData("boot-key"u8));
            writer.WriteBoolean(deviceLocked);
            WriteEnumerated(writer, verifiedBootState);
            writer.WriteOctetString(SHA256.HashData("boot-hash"u8));
        }
    }

    /// <summary>
    /// <c>attestationApplicationId [709] EXPLICIT OCTET_STRING</c>: a constructed
    /// context tag wrapping a universal OCTET STRING, per the published schema.
    /// </summary>
    private static void WriteApplicationId(AsnWriter writer, string packageName, string? signingDigestHex)
    {
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 709)))
        {
            writer.WriteOctetString(BuildApplicationId(packageName, signingDigestHex ?? SigningDigestHex));
        }
    }

    private static byte[] BuildApplicationId(string packageName, string signingDigestHex)
    {
        AsnWriter writer = new(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            using (writer.PushSetOf())
            using (writer.PushSequence())
            {
                writer.WriteOctetString(Encoding.UTF8.GetBytes(packageName));
                writer.WriteInteger(1);
            }

            using (writer.PushSetOf())
            {
                writer.WriteOctetString(Convert.FromHexString(signingDigestHex));
            }
        }

        return writer.Encode();
    }

    /// <summary>ASN.1 ENUMERATED, written directly because AsnWriter's overload takes an enum.</summary>
    private static void WriteEnumerated(AsnWriter writer, int value) =>
        writer.WriteEncodedValue([0x0A, 0x01, (byte)value]);

    // ---- Apple -------------------------------------------------------------

    /// <summary>Builds authenticator data for App Attest.</summary>
    internal static byte[] BuildAppleAuthenticatorData(
        byte[] keyId,
        string teamId = TeamId,
        string bundleId = BundleId,
        bool development = false,
        uint counter = 0)
    {
        byte[] relyingPartyIdHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{teamId}.{bundleId}"));
        byte[] aaguid = development
            ? Encoding.ASCII.GetBytes("appattestdevelop")
            : [.. Encoding.ASCII.GetBytes("appattest"), 0, 0, 0, 0, 0, 0, 0];

        byte[] data = new byte[32 + 1 + 4 + 16 + 2 + keyId.Length];
        int offset = 0;

        relyingPartyIdHash.CopyTo(data, offset);
        offset += 32;

        data[offset++] = 0x40;   // flags: attested credential data present

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), counter);
        offset += 4;

        aaguid.CopyTo(data, offset);
        offset += 16;

        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), (ushort)keyId.Length);
        offset += 2;

        keyId.CopyTo(data, offset);

        return data;
    }

    /// <summary>Builds Apple's nonce extension value.</summary>
    internal static byte[] BuildAppleNonceExtension(byte[] nonce)
    {
        AsnWriter writer = new(AsnEncodingRules.DER);

        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
        {
            writer.WriteOctetString(nonce);
        }

        return writer.Encode();
    }

    /// <summary>Encodes the CBOR attestation object.</summary>
    internal static byte[] EncodeAppleAttestationObject(
        byte[] authenticatorData,
        string format = "apple-appattest",
        params X509Certificate2[] chain)
    {
        CborWriter writer = new();

        writer.WriteStartMap(3);

        writer.WriteTextString("fmt");
        writer.WriteTextString(format);

        writer.WriteTextString("attStmt");
        writer.WriteStartMap(2);
        writer.WriteTextString("x5c");
        writer.WriteStartArray(chain.Length);

        foreach (X509Certificate2 certificate in chain)
        {
            writer.WriteByteString(certificate.RawData);
        }

        writer.WriteEndArray();
        writer.WriteTextString("receipt");
        writer.WriteByteString("receipt-not-parsed"u8);
        writer.WriteEndMap();

        writer.WriteTextString("authData");
        writer.WriteByteString(authenticatorData);

        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>The nonce Apple's specification derives.</summary>
    internal static byte[] AppleNonce(byte[] authenticatorData, byte[] challenge) =>
        SHA256.HashData([.. authenticatorData, .. SHA256.HashData(challenge)]);
}
