using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Attendance.Api.Security;

/// <summary>
/// A parsed <c>Signature-Input</c> header, constrained to the profile in
/// <c>docs/architecture/03-solution-architecture.md</c> §6.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is deliberately not a general RFC 8941 structured-fields parser.</b>
/// The profile permits exactly one shape, and accepting only that shape is itself
/// a security property: a parser generous enough to handle every legal structured
/// field is also generous enough to accept a signature whose covered components
/// differ from the ones this system requires. RFC 9421 §7.2.1 warns specifically
/// about insufficient coverage, and the cheapest defence is to refuse anything
/// unfamiliar.
/// </para>
/// <para>
/// The raw parameter text is preserved verbatim, because the signature base's
/// <c>"@signature-params"</c> line must reproduce exactly what the client signed.
/// Re-serialising from parsed values would risk differing by a space or a quote
/// and failing every signature for reasons that are very hard to see.
/// </para>
/// </remarks>
internal sealed record SignatureInput(
    string Label,
    IReadOnlyList<string> CoveredComponents,
    string RawParameters,
    long Created,
    string Nonce,
    string KeyId,
    string Algorithm,
    string Tag)
{
    /// <summary>The only algorithm this profile accepts.</summary>
    public const string ExpectedAlgorithm = "ecdsa-p256-sha256";

    /// <summary>The tag that scopes a signature to this application.</summary>
    public const string ExpectedTag = "clockinxtra-mobile-v1";

    /// <summary>The key id a device uses before it is registered (§6.3).</summary>
    public const string UnregisteredKeyId = "unregistered";

    /// <summary>Covered components for a request carrying a body.</summary>
    public static readonly string[] ComponentsWithBody =
        ["@method", "@authority", "@path", "content-digest"];

    /// <summary>Covered components for a request without a body.</summary>
    public static readonly string[] ComponentsWithoutBody =
        ["@method", "@authority", "@path"];

    /// <summary>Whether the signature covers the body digest.</summary>
    public bool CoversContentDigest =>
        CoveredComponents.Contains("content-digest", StringComparer.Ordinal);

    /// <summary>
    /// Parses a <c>Signature-Input</c> header value.
    /// </summary>
    /// <remarks>
    /// Returns false for anything that does not match the profile. Every failure
    /// is a refusal, never an exception: this header arrives from the internet.
    /// </remarks>
    public static bool TryParse(string? headerValue, [NotNullWhen(true)] out SignatureInput? input)
    {
        input = null;

        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        // label=("comp" "comp");param=value;param="value"
        int equals = headerValue.IndexOf('=', StringComparison.Ordinal);
        int listStart = headerValue.IndexOf('(', StringComparison.Ordinal);
        int listEnd = headerValue.IndexOf(')', StringComparison.Ordinal);

        if (equals <= 0 || listStart < equals || listEnd < listStart)
        {
            return false;
        }

        string label = headerValue[..equals].Trim();

        if (label.Length == 0)
        {
            return false;
        }

        // Everything after "label=" is what the signature base must reproduce.
        string rawParameters = headerValue[(equals + 1)..].Trim();

        List<string> components = [];

        foreach (string token in headerValue[(listStart + 1)..listEnd]
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length < 2 || token[0] != '"' || token[^1] != '"')
            {
                return false;
            }

            components.Add(token[1..^1]);
        }

        if (components.Count == 0)
        {
            return false;
        }

        Dictionary<string, string> parameters = new(StringComparer.Ordinal);

        foreach (string parameter in headerValue[(listEnd + 1)..]
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = parameter.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                return false;
            }

            string name = parameter[..separator].Trim();
            string value = parameter[(separator + 1)..].Trim().Trim('"');

            // A repeated parameter is ambiguous, and ambiguity in a signature
            // header is exactly where a smuggling attack lives.
            if (!parameters.TryAdd(name, value))
            {
                return false;
            }
        }

        if (!parameters.TryGetValue("created", out string? createdText)
            || !long.TryParse(createdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long created)
            || !parameters.TryGetValue("nonce", out string? nonce)
            || !parameters.TryGetValue("keyid", out string? keyId)
            || !parameters.TryGetValue("alg", out string? algorithm)
            || !parameters.TryGetValue("tag", out string? tag))
        {
            return false;
        }

        if (nonce.Length == 0 || keyId.Length == 0)
        {
            return false;
        }

        input = new SignatureInput(label, components, rawParameters, created, nonce, keyId, algorithm, tag);
        return true;
    }

    /// <summary>
    /// Whether the covered components are exactly those the profile requires.
    /// </summary>
    /// <remarks>
    /// Exactly, and in order. A signature that covers fewer components than the
    /// profile leaves the uncovered ones free to be altered in transit; one that
    /// covers more is not something this server negotiated.
    /// </remarks>
    public bool HasExpectedComponents(bool hasBody)
    {
        string[] expected = hasBody ? ComponentsWithBody : ComponentsWithoutBody;

        return CoveredComponents.Count == expected.Length
            && CoveredComponents.Zip(expected).All(pair => string.Equals(pair.First, pair.Second, StringComparison.Ordinal));
    }
}
