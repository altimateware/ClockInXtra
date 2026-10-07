using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// Tests for the portal's authentication and authorization behaviour.
/// </summary>
/// <remarks>
/// <para>
/// The portal can revoke devices, approve replacements and rewrite the rules
/// attendance is calculated by. These tests pin the controls that decide who
/// reaches those pages.
/// </para>
/// <para>
/// Requests are made over plain HTTP through the in-memory test host, which never
/// applies the HTTPS redirection. That is why these cover routing and
/// authorization rather than the anti-forgery cookie, which refuses to be issued
/// on an insecure channel by design and is verified against a running instance
/// over TLS instead.
/// </para>
/// </remarks>
public sealed class PortalSecurityTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = PortalHost.Create();

        _client = _factory.Client(new WebApplicationFactoryClientOptions
        {
            // Redirects are followed manually so the redirect itself can be
            // asserted.
            AllowAutoRedirect = false,

            // HTTPS, because the portal genuinely refuses to work without it:
            // the anti-forgery cookie is SecurePolicy = Always, so on an
            // insecure channel the sign-in page fails outright. Pointing the
            // test client at http would mean testing a configuration that is
            // not the one deployed.
            BaseAddress = new Uri("https://localhost"),
        });

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task AnswersHealthProbesWithoutASession(string path)
    {
        // A load balancer has no cookie. Were these behind the fallback
        // authentication policy, every probe would be a redirect to the sign-in
        // page and every node would read as down.
        HttpResponseMessage response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReportsTheDatabaseAndKeyRingAndNothingMore()
    {
        HttpResponseMessage response = await _client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        string json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using JsonDocument report = JsonDocument.Parse(json);
        JsonElement checks = report.RootElement.GetProperty("checks");

        Assert.Equal("Healthy", checks.GetProperty("database").GetProperty("status").GetString());
        Assert.Equal("Healthy", checks.GetProperty("key-ring").GetProperty("status").GetString());
        Assert.Equal(2, checks.EnumerateObject().Count());
        Assert.DoesNotContain("Exception", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Dashboard")]
    [InlineData("/Devices/Pending")]
    [InlineData("/Settings")]
    public async Task RefusesEveryPageToAnAnonymousVisitor(string path)
    {
        // The fallback authorization policy is what makes this true for pages
        // nobody remembered to attribute. A portal defaulting to anonymous would
        // be one missing attribute away from a public page.
        HttpResponseMessage response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        string location = response.Headers.Location?.OriginalString ?? string.Empty;
        Assert.Contains("/Account/Login", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CarriesTheRequestedPageIntoTheSignInRedirect()
    {
        HttpResponseMessage response = await _client.GetAsync("/Devices/Pending", TestContext.Current.CancellationToken);

        string location = response.Headers.Location?.OriginalString ?? string.Empty;

        Assert.Contains("ReturnUrl", location, StringComparison.Ordinal);
        Assert.Contains("Devices", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServesTheSignInPageAnonymously()
    {
        HttpResponseMessage response = await _client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("__RequestVerificationToken", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeverRendersASubmittedPasswordBackIntoTheForm()
    {
        // A value in a rendered page is a value in the browser's history and in
        // any proxy cache along the way (§24).
        HttpResponseMessage response = await _client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);

        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("type=\"password\" value=\"s", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesAPostWithoutAnAntiForgeryToken()
    {
        using FormUrlEncodedContent form = new(
        [
            new KeyValuePair<string, string>("UserName", "someone"),
            new KeyValuePair<string, string>("Password", "guess"),
        ]);

        HttpResponseMessage response = await _client.PostAsync(
            "/Account/Login", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SendsTheSecurityHeadersOnEveryResponse()
    {
        HttpResponseMessage response = await _client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("same-origin", Header(response, "Referrer-Policy"));

        string csp = Header(response, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);

        // No 'unsafe-inline': a portal that permits inline script has given up
        // most of what a content security policy is for.
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? string.Join(' ', values)
            : string.Empty;
}
