using System.Text;

namespace Attendance.Api.Security;

/// <summary>
/// Builds the RFC 9421 §2.5 signature base.
/// </summary>
/// <remarks>
/// <para>
/// The base is the exact byte sequence both sides sign. Each covered component
/// contributes one line of <c>"name": value</c>, and the final line is
/// <c>"@signature-params": </c> followed by the signature parameters — with
/// <b>no trailing newline</b>. That last detail is not cosmetic: an extra newline
/// changes every signature and produces a failure that looks like a wrong key.
/// </para>
/// <para>
/// <c>@authority</c> and <c>@path</c> must be the values the <em>client</em> saw.
/// Behind a reverse proxy that means forwarded headers, restricted to known proxy
/// addresses — never wildcard, because an attacker who can set
/// <c>X-Forwarded-Host</c> could otherwise make a signature verify against a
/// value the client never signed.
/// </para>
/// </remarks>
internal static class SignatureBase
{
    /// <summary>Builds the signature base for a request.</summary>
    /// <param name="input">The parsed signature input.</param>
    /// <param name="method">The HTTP method, uppercase.</param>
    /// <param name="authority">Host and, when non-default, port — as the client saw it.</param>
    /// <param name="path">The absolute path, without the query string.</param>
    /// <param name="contentDigest">
    /// The <c>Content-Digest</c> header value exactly as received, or
    /// <see langword="null"/> for a request whose signature does not cover it.
    /// </param>
    public static byte[] Build(
        SignatureInput input,
        string method,
        string authority,
        string path,
        string? contentDigest)
    {
        ArgumentNullException.ThrowIfNull(input);

        StringBuilder builder = new();

        foreach (string component in input.CoveredComponents)
        {
            string value = component switch
            {
                "@method" => method,
                "@authority" => authority,
                "@path" => path,
                "content-digest" => contentDigest ?? string.Empty,
                _ => throw new InvalidOperationException(
                    $"Component '{component}' is outside the profile and should have been refused before this point."),
            };

            builder.Append('"').Append(component).Append("\": ").Append(value).Append('\n');
        }

        // No trailing newline after the parameters line.
        builder.Append("\"@signature-params\": ").Append(input.RawParameters);

        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
