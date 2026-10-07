using System.Text;
using Attendance.Api.Security;
using Xunit;

namespace Attendance.Api.Tests;

/// <summary>
/// Tests for the RFC 9421 profile primitives.
/// </summary>
/// <remarks>
/// These headers arrive from the internet on every request, so the tests cover
/// two things: that a conforming signature is parsed exactly, and that everything
/// else is refused rather than accommodated. A parser generous enough to handle
/// any legal structured field would also accept a signature covering less than
/// this profile requires, which RFC 9421 §7.2.1 warns is worse than no signature
/// at all because it looks like protection.
/// </remarks>
public sealed class SignatureProfileTests
{
    private const string ValidHeader =
        """sig1=("@method" "@authority" "@path" "content-digest");created=1789459200;keyid="8f2c0f3e-0000-4000-8000-000000000001";alg="ecdsa-p256-sha256";nonce="Y2xvY2tpblh0cmE";tag="clockinxtra-mobile-v1" """;

    [Fact]
    public void ParsesAConformingHeader()
    {
        Assert.True(SignatureInput.TryParse(ValidHeader.Trim(), out SignatureInput? input));

        Assert.Equal("sig1", input!.Label);
        Assert.Equal(1789459200L, input.Created);
        Assert.Equal("ecdsa-p256-sha256", input.Algorithm);
        Assert.Equal("clockinxtra-mobile-v1", input.Tag);
        Assert.Equal("Y2xvY2tpblh0cmE", input.Nonce);
        Assert.Equal("8f2c0f3e-0000-4000-8000-000000000001", input.KeyId);
        Assert.Equal(
            ["@method", "@authority", "@path", "content-digest"],
            input.CoveredComponents);
        Assert.True(input.CoversContentDigest);
    }

    [Fact]
    public void PreservesTheParametersVerbatim()
    {
        // The signature base must reproduce exactly what the client signed.
        // Re-serialising from parsed values risks differing by a space or a
        // quote and failing every signature for a reason that is hard to see.
        Assert.True(SignatureInput.TryParse(ValidHeader.Trim(), out SignatureInput? input));

        Assert.StartsWith("(\"@method\"", input!.RawParameters, StringComparison.Ordinal);
        Assert.EndsWith("tag=\"clockinxtra-mobile-v1\"", input.RawParameters, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("sig1=no-component-list;created=1")]
    [InlineData("""sig1=("@method");created=1;keyid="k";alg="ecdsa-p256-sha256" """)]          // no tag
    [InlineData("""sig1=("@method");keyid="k";alg="ecdsa-p256-sha256";tag="t";nonce="n" """)]  // no created
    [InlineData("""sig1=();created=1;keyid="k";alg="a";nonce="n";tag="t" """)]                 // empty list
    [InlineData("""sig1=("@method");created=notanumber;keyid="k";alg="a";nonce="n";tag="t" """)]
    public void RefusesAnythingOutsideTheProfile(string? header)
    {
        Assert.False(SignatureInput.TryParse(header?.Trim(), out _));
    }

    [Fact]
    public void RefusesARepeatedParameter()
    {
        // Two values for one parameter is ambiguous, and ambiguity in a signature
        // header is exactly where a smuggling attack lives.
        const string header =
            """sig1=("@method");created=1;created=2;keyid="k";alg="ecdsa-p256-sha256";nonce="n";tag="clockinxtra-mobile-v1" """;

        Assert.False(SignatureInput.TryParse(header.Trim(), out _));
    }

    [Fact]
    public void RefusesAnUnquotedComponent()
    {
        const string header =
            """sig1=(@method);created=1;keyid="k";alg="ecdsa-p256-sha256";nonce="n";tag="clockinxtra-mobile-v1" """;

        Assert.False(SignatureInput.TryParse(header.Trim(), out _));
    }

    [Fact]
    public void RequiresExactlyTheProfilesComponentsForARequestWithABody()
    {
        Assert.True(SignatureInput.TryParse(ValidHeader.Trim(), out SignatureInput? input));

        Assert.True(input!.HasExpectedComponents(hasBody: true));

        // The same signature is not acceptable for a bodyless request: it claims
        // to cover a digest that is not there.
        Assert.False(input.HasExpectedComponents(hasBody: false));
    }

    [Fact]
    public void RefusesASignatureCoveringTooLittle()
    {
        const string header =
            """sig1=("@method" "@path");created=1;keyid="k";alg="ecdsa-p256-sha256";nonce="n";tag="clockinxtra-mobile-v1" """;

        Assert.True(SignatureInput.TryParse(header.Trim(), out SignatureInput? input));

        // Parses, but must not satisfy the profile: the authority is uncovered,
        // so the same signature would verify against a different host.
        Assert.False(input!.HasExpectedComponents(hasBody: false));
        Assert.False(input.HasExpectedComponents(hasBody: true));
    }

    [Fact]
    public void RefusesComponentsInTheWrongOrder()
    {
        const string header =
            """sig1=("@authority" "@method" "@path");created=1;keyid="k";alg="ecdsa-p256-sha256";nonce="n";tag="clockinxtra-mobile-v1" """;

        Assert.True(SignatureInput.TryParse(header.Trim(), out SignatureInput? input));
        Assert.False(input!.HasExpectedComponents(hasBody: false));
    }

    // ---- Signature base ----------------------------------------------------

    [Fact]
    public void BuildsTheSignatureBaseExactlyAsTheSpecificationRequires()
    {
        Assert.True(SignatureInput.TryParse(ValidHeader.Trim(), out SignatureInput? input));

        const string digest = "sha-256=:X48E9qOokqqrvdts8nOJRJN3OWDUoyWxBf7kbu9DBPE=:";

        byte[] signatureBase = SignatureBase.Build(
            input!, "POST", "attendance.example.com", "/api/v1/mobile/attendance/clock-out", digest);

        string expected =
            "\"@method\": POST\n" +
            "\"@authority\": attendance.example.com\n" +
            "\"@path\": /api/v1/mobile/attendance/clock-out\n" +
            $"\"content-digest\": {digest}\n" +
            $"\"@signature-params\": {input!.RawParameters}";

        Assert.Equal(expected, Encoding.ASCII.GetString(signatureBase));
    }

    [Fact]
    public void EndsTheSignatureBaseWithoutATrailingNewline()
    {
        // Not cosmetic. An extra newline changes every signature and produces a
        // failure that looks exactly like a wrong key.
        Assert.True(SignatureInput.TryParse(ValidHeader.Trim(), out SignatureInput? input));

        byte[] signatureBase = SignatureBase.Build(
            input!, "POST", "host", "/p", "sha-256=:AAAA:");

        Assert.NotEqual((byte)'\n', signatureBase[^1]);
    }

    // ---- Content digest ----------------------------------------------------

    [Fact]
    public void AcceptsAMatchingContentDigest()
    {
        byte[] body = "{\"userId\":\"jane.doe\"}"u8.ToArray();

        Assert.True(ContentDigest.Matches(ContentDigest.Compute(body), body));
    }

    [Fact]
    public void RefusesADigestForDifferentBytes()
    {
        byte[] body = "{\"userId\":\"jane.doe\"}"u8.ToArray();
        byte[] tampered = "{\"userId\":\"jane.does\"}"u8.ToArray();

        Assert.False(ContentDigest.Matches(ContentDigest.Compute(body), tampered));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha-256=notwrapped")]
    [InlineData("sha-512=:AAAA:")]                     // algorithm not negotiable
    [InlineData("sha-256=:not-base64!:")]
    [InlineData("sha-256=:QUJD:")]                     // right shape, wrong length
    public void RefusesAMalformedOrUnexpectedDigest(string? header)
    {
        Assert.False(ContentDigest.Matches(header, "body"u8));
    }
}
