using System.Net;
using Attendance.Application.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// What the sign-in form offers, against what the deployment actually asks for.
/// </summary>
/// <remarks>
/// <para>
/// A field that must be left blank is a field people try to fill, and an empty
/// authenticator box above the sign-in button reads as a broken page on a
/// deployment that does not use one.
/// </para>
/// <para>
/// <b>Hiding it decides nothing.</b> The policy is read again, server-side,
/// when the form is posted — so a code sent to a deployment that does not want
/// one, or omitted from one that does, is judged there rather than by whatever
/// the page happened to render. These tests cover the rendering; the
/// authenticator's own tests cover the rule.
/// </para>
/// <para>
/// The policy provider is substituted rather than the setting being changed in
/// the database: the real one caches for thirty seconds, so a test that wrote
/// the setting would be asserting against whichever side of that window it
/// happened to land on.
/// </para>
/// </remarks>
public sealed class LoginFormTests
{
    private const string FieldLabel = "Authenticator code";

    [Fact]
    public async Task DrawsTheAuthenticatorFieldWhereACodeIsRequired()
    {
        string html = await LoginPageAsync(requireMfa: true);

        Assert.Contains(FieldLabel, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmitsTheAuthenticatorFieldWhereNoCodeIsWanted()
    {
        string html = await LoginPageAsync(requireMfa: false);

        Assert.DoesNotContain(FieldLabel, html, StringComparison.Ordinal);

        // The rest of the form is still there: this hides one field, it does
        // not break the page.
        Assert.Contains("User name", html, StringComparison.Ordinal);
        Assert.Contains("Password", html, StringComparison.Ordinal);
    }

    private static async Task<string> LoginPageAsync(bool requireMfa)
    {
        using WebApplicationFactory<Program> factory = PortalHost.Create(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IAdministratorPolicyProvider>(
                    new FixedPolicy(requireMfa))));

        // https, because the antiforgery cookie is configured SecurePolicy.Always
        // and the form tag helper refuses to issue a token over plain http.
        using HttpClient client = factory.Client(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        // Anonymous: the sign-in page is the one page that must render without
        // signing in, which is what makes this cheap to assert.
        HttpResponseMessage response = await client.GetAsync(
            "/Account/Login", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FixedPolicy : IAdministratorPolicyProvider
    {
        private readonly AdministratorPolicy _policy;

        public FixedPolicy(bool requireMfa) =>
            _policy = new AdministratorPolicy(
                LockoutThreshold: 5,
                LockoutMinutes: 15,
                RequireMfa: requireMfa,
                TotpStepTolerance: 1);

        public Task<AdministratorPolicy> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_policy);
    }
}
