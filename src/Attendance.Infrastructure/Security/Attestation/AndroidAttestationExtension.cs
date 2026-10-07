using System.Formats.Asn1;

namespace Attendance.Infrastructure.Security.Attestation;

/// <summary>
/// The parsed contents of Android's key attestation certificate extension.
/// </summary>
/// <remarks>
/// <para>
/// The extension carries OID <c>1.3.6.1.4.1.11129.2.1.17</c> and is written by
/// the device's keystore, not by the application. Its DER structure is Google's
/// published <c>KeyDescription</c>:
/// </para>
/// <code>
/// KeyDescription ::= SEQUENCE {
///     attestationVersion         INTEGER,          -- 400 for KeyMint 4.0
///     attestationSecurityLevel   SecurityLevel,
///     keyMintVersion             INTEGER,
///     keyMintSecurityLevel       SecurityLevel,
///     attestationChallenge       OCTET_STRING,
///     uniqueId                   OCTET_STRING,
///     softwareEnforced           AuthorizationList,
///     hardwareEnforced           AuthorizationList, -- "teeEnforced" in older revisions
/// }
/// </code>
/// <para>
/// Only the fields this system decides on are read: the challenge, the security
/// level, the root of trust and the attestation application id. Parsing fields
/// nobody checks would add code that can fail without adding a control.
/// </para>
/// <para>
/// <b>Every AuthorizationList field is EXPLICIT-tagged</b> in the published
/// schema (source.android.com, keystore attestation): <c>[709]</c> arrives as a
/// constructed context tag wrapping a universal OCTET STRING, not as an octet
/// string carrying the tag itself. An earlier version read it as implicit. Under
/// DER that throws, so every genuine attestation — first observed from a real
/// KeyMint 4.0 implementation on the Android emulator — was refused as malformed,
/// while synthetic test attestations built with the same misreading passed.
/// </para>
/// <para>
/// <b>Which list each field is taken from is a security decision, not a parsing
/// detail.</b>
/// </para>
/// <list type="bullet">
///   <item><c>rootOfTrust</c> is taken <b>only</b> from <c>hardwareEnforced</c>. A
///   boot-integrity claim is meaningful only when the secure environment makes it;
///   the same structure in the software list is the operating system describing
///   itself, which is precisely what cannot be believed on a compromised device.
///   The emulator puts its root of trust in the software list, and it is ignored.</item>
///   <item><c>attestationApplicationId</c> is accepted from <b>either</b> list,
///   preferring the hardware one. Real KeyMint output places it in
///   <c>softwareEnforced</c>: the package name and signing digest are supplied by
///   the Android keystore service rather than known to the secure environment.
///   Android's schema documentation does not state a placement, so requiring the
///   hardware list would refuse genuine devices on an assumption. The honest
///   consequence: on a device whose operating system is compromised, this field
///   is only as trustworthy as that operating system — which is why the verified
///   boot checks above it matter.</item>
/// </list>
/// </remarks>
internal sealed record AndroidAttestationExtension(
    int AttestationVersion,
    AndroidSecurityLevel AttestationSecurityLevel,
    byte[] AttestationChallenge,
    AndroidRootOfTrust? RootOfTrust,
    byte[]? AttestationApplicationId)
{
    /// <summary>The extension OID Google assigns to key attestation.</summary>
    public const string Oid = "1.3.6.1.4.1.11129.2.1.17";

    /// <summary>Context tag for <c>rootOfTrust</c> inside an AuthorizationList.</summary>
    private const int RootOfTrustTag = 704;

    /// <summary>Context tag for <c>attestationApplicationId</c>.</summary>
    private const int AttestationApplicationIdTag = 709;

    /// <summary>
    /// Parses the extension.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the bytes are not a well-formed
    /// KeyDescription. <b>Never throws:</b> this data arrives from an untrusted
    /// client, so malformed input is an expected outcome, and an exception here
    /// would be a denial-of-service reachable by anyone who can call the
    /// registration endpoint.
    /// </returns>
    public static AndroidAttestationExtension? TryParse(ReadOnlyMemory<byte> extensionValue)
    {
        try
        {
            AsnReader reader = new(extensionValue, AsnEncodingRules.DER);
            AsnReader description = reader.ReadSequence();

            if (!description.TryReadInt32(out int attestationVersion))
            {
                return null;
            }

            AndroidSecurityLevel attestationSecurityLevel = (AndroidSecurityLevel)ReadEnumeratedInt32(description);

            _ = description.ReadInteger();                                   // keyMintVersion
            _ = ReadEnumeratedInt32(description);                            // keyMintSecurityLevel

            byte[] challenge = description.ReadOctetString();
            _ = description.ReadOctetString();                               // uniqueId

            // softwareEnforced: its root of trust is discarded deliberately (see
            // the type's remarks); its application id is kept.
            ReadAuthorizationList(description, out _, out byte[]? softwareApplicationId);

            ReadAuthorizationList(
                description, out AndroidRootOfTrust? rootOfTrust, out byte[]? hardwareApplicationId);

            return new AndroidAttestationExtension(
                attestationVersion,
                attestationSecurityLevel,
                challenge,
                rootOfTrust,
                hardwareApplicationId ?? softwareApplicationId);
        }
        catch (AsnContentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // AsnReader raises this for a structurally valid but unexpected
            // encoding, for example a tag appearing where another was required.
            return null;
        }
    }

    /// <summary>
    /// Reads an ASN.1 ENUMERATED as a plain integer.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>ReadEnumeratedValue&lt;TEnum&gt;</c>. This data comes
    /// from an untrusted device, and a newer Android release may report a value
    /// this build has no name for. Reading the number and letting the explicit
    /// comparisons decide keeps an unknown level failing the security check
    /// rather than failing the parse.
    /// </remarks>
    private static int ReadEnumeratedInt32(AsnReader reader)
    {
        ReadOnlySpan<byte> bytes = reader.ReadEnumeratedBytes().Span;

        if (bytes.Length is 0 or > 4)
        {
            throw new AsnContentException("ENUMERATED value is not a 32-bit integer.");
        }

        int value = (sbyte)bytes[0] < 0 ? -1 : 0;

        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    /// <summary>
    /// Reads an AuthorizationList, extracting only the tagged fields this system
    /// checks and skipping everything else.
    /// </summary>
    /// <remarks>
    /// The list is a sequence of context-tagged optional fields whose membership
    /// grows with each Android release. Skipping unknown tags rather than
    /// enumerating them is what stops a newer device failing verification for a
    /// field that did not exist when this was written.
    /// </remarks>
    private static void ReadAuthorizationList(
        AsnReader parent,
        out AndroidRootOfTrust? rootOfTrust,
        out byte[]? attestationApplicationId)
    {
        rootOfTrust = null;
        attestationApplicationId = null;

        AsnReader list = parent.ReadSequence();

        while (list.HasData)
        {
            Asn1Tag tag = list.PeekTag();

            if (tag.TagClass != TagClass.ContextSpecific)
            {
                list.ReadEncodedValue();
                continue;
            }

            switch (tag.TagValue)
            {
                case RootOfTrustTag:
                    rootOfTrust = TryReadRootOfTrust(list.ReadSequence(tag));
                    break;

                case AttestationApplicationIdTag:
                    // [709] EXPLICIT OCTET_STRING: open the context wrapper, then
                    // read the universal OCTET STRING inside it.
                    AsnReader wrapper = list.ReadSequence(tag);
                    attestationApplicationId = wrapper.ReadOctetString();
                    wrapper.ThrowIfNotEmpty();
                    break;

                default:
                    list.ReadEncodedValue();
                    break;
            }
        }
    }

    /// <summary>
    /// Reads the RootOfTrust structure.
    /// </summary>
    /// <remarks>
    /// <code>
    /// RootOfTrust ::= SEQUENCE {
    ///     verifiedBootKey    OCTET_STRING,
    ///     deviceLocked       BOOLEAN,
    ///     verifiedBootState  ENUMERATED,
    ///     verifiedBootHash   OCTET_STRING OPTIONAL,   -- attestation version 3+
    /// }
    /// </code>
    /// </remarks>
    private static AndroidRootOfTrust? TryReadRootOfTrust(AsnReader reader)
    {
        try
        {
            AsnReader rootOfTrust = reader.ReadSequence();

            byte[] verifiedBootKey = rootOfTrust.ReadOctetString();
            bool deviceLocked = rootOfTrust.ReadBoolean();
            AndroidVerifiedBootState state = (AndroidVerifiedBootState)ReadEnumeratedInt32(rootOfTrust);

            return new AndroidRootOfTrust(verifiedBootKey, deviceLocked, state);
        }
        catch (AsnContentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Where a key is held, as the attestation reports it.</summary>
internal enum AndroidSecurityLevel
{
    /// <summary>In software. Not acceptable for device binding.</summary>
    Software = 0,

    /// <summary>In the Trusted Execution Environment.</summary>
    TrustedEnvironment = 1,

    /// <summary>In a discrete StrongBox secure element.</summary>
    StrongBox = 2,
}

/// <summary>Verified boot state, as the bootloader reports it.</summary>
internal enum AndroidVerifiedBootState
{
    /// <summary>Boot chain verified to the device manufacturer's root of trust.</summary>
    Verified = 0,

    /// <summary>Verified to a user-supplied key: the OS may have been replaced.</summary>
    SelfSigned = 1,

    /// <summary>Not verified at all.</summary>
    Unverified = 2,

    /// <summary>Verification ran and failed.</summary>
    Failed = 3,
}

/// <summary>The device's boot integrity claim.</summary>
internal sealed record AndroidRootOfTrust(
    byte[] VerifiedBootKey,
    bool DeviceLocked,
    AndroidVerifiedBootState VerifiedBootState);
