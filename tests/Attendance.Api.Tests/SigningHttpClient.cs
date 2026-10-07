using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Attendance.Api.Tests;

/// <summary>
/// Signs requests the way the mobile application will.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only thing that proves the server's signature base is right.</b>
/// The server builds a byte sequence from the request and checks a signature over
/// it; the client builds what it believes is the same sequence and signs it. If
/// the two disagree by a single byte — an extra newline, a differently spelled
/// authority — every genuine request fails with a symptom that looks exactly like
/// a wrong key. The only way to catch that is to have an independent
/// implementation produce a signature and watch the server accept it.
/// </para>
/// <para>
/// It is written from the profile in the architecture document rather than by
/// calling the server's own builder, which would prove nothing at all.
/// </para>
/// </remarks>
internal sealed class SigningHttpClient
{
    private const string Algorithm = "ecdsa-p256-sha256";
    private const string Tag = "clockinxtra-mobile-v1";

    private readonly HttpClient _client;
    private readonly ECDsa _key;
    private readonly string _keyId;

    public SigningHttpClient(HttpClient client, ECDsa key, string keyId)
    {
        _client = client;
        _key = key;
        _keyId = keyId;
    }

    /// <summary>Overrides the signature timestamp, to exercise the skew check.</summary>
    public DateTimeOffset? CreatedOverride { get; set; }

    /// <summary>Overrides the nonce, to exercise replay detection.</summary>
    public string? NonceOverride { get; set; }

    /// <summary>Replaces the tag, to exercise the profile allow-list.</summary>
    public string TagOverride { get; set; } = Tag;

    /// <summary>Drops components from the covered set, to exercise coverage checks.</summary>
    public string[]? ComponentsOverride { get; set; }

    /// <summary>Body sent after the digest is computed, to exercise digest mismatch.</summary>
    public string? TamperedBody { get; set; }

    /// <summary>Sends a signed request.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? json,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        byte[] body = json is null ? [] : Encoding.UTF8.GetBytes(json);
        bool hasBody = body.Length > 0;

        HttpRequestMessage request = new(method, path);

        string? digest = null;

        if (hasBody)
        {
            digest = $"sha-256=:{Convert.ToBase64String(SHA256.HashData(body))}:";

            // The tampered body is sent *after* the digest is computed over the
            // honest one, which is what a body-swapping attacker does.
            byte[] transmitted = TamperedBody is null ? body : Encoding.UTF8.GetBytes(TamperedBody);

            request.Content = new ByteArrayContent(transmitted);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content.Headers.TryAddWithoutValidation("Content-Digest", digest);
        }

        string[] components = ComponentsOverride
            ?? (hasBody
                ? ["@method", "@authority", "@path", "content-digest"]
                : ["@method", "@authority", "@path"]);

        long created = (CreatedOverride ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();

        string nonce = NonceOverride
            ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        string componentList = string.Join(' ', components.Select(c => $"\"{c}\""));

        string parameters =
            $"({componentList});created={created};keyid=\"{_keyId}\";alg=\"{Algorithm}\";nonce=\"{nonce}\";tag=\"{TagOverride}\"";

        string authority = _client.BaseAddress!.Authority;

        StringBuilder signatureBase = new();

        foreach (string component in components)
        {
            string value = component switch
            {
                "@method" => method.Method,
                "@authority" => authority,
                "@path" => path,
                "content-digest" => digest ?? string.Empty,
                _ => string.Empty,
            };

            signatureBase.Append('"').Append(component).Append("\": ").Append(value).Append('\n');
        }

        signatureBase.Append("\"@signature-params\": ").Append(parameters);

        // .NET's ECDsa.SignData produces IEEE P1363 (raw r‖s), which is exactly
        // what RFC 9421 §3.3.4 specifies for this algorithm — no conversion.
        byte[] signature = _key.SignData(
            Encoding.ASCII.GetBytes(signatureBase.ToString()), HashAlgorithmName.SHA256);

        request.Headers.TryAddWithoutValidation("Signature-Input", $"sig1={parameters}");
        request.Headers.TryAddWithoutValidation("Signature", $"sig1=:{Convert.ToBase64String(signature)}:");

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await _client.SendAsync(request, cancellationToken);
    }
}
