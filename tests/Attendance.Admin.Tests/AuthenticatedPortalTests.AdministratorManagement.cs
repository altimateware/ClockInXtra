using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Attendance.Application.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// Password change and administrator management, end to end.
/// </summary>
/// <remarks>
/// <para>
/// Same fixture as the rest of this class: real accounts, a real key ring and
/// real TOTP codes. One constraint shapes several tests — a code's time step can
/// be used once per account, so an account signs in at most once per test. Where a
/// test needs a second browser holding the same session, it copies the first
/// client's cookie, which is exactly what another browser signed in before a
/// change looks like to the server.
/// </para>
/// <para>
/// The authority rules themselves (not yourself, not someone more privileged,
/// never the last manager) are tested exhaustively in the database suite. These
/// tests check that the portal reaches them and presents the outcome correctly.
/// </para>
/// </remarks>
public sealed partial class AuthenticatedPortalTests
{
    private const string NewPassword = "a longer passphrase for the new password";

    // ---- Change password ---------------------------------------------------

    [Fact]
    public async Task SendsAnAdministratorWithAnIssuedPasswordToChangeItFirst()
    {
        await ExecuteAsync(
            "UPDATE core.Administrator SET MustChangePassword = 1 WHERE UserName = @userName;",
            new { userName = _privilegedUser });

        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        // A page the account is fully permitted to see is withheld anyway.
        HttpResponseMessage devices = await client.GetAsync("/Devices/Pending", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, devices.StatusCode);
        Assert.Contains("/Account/ChangePassword", devices.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);

        HttpResponseMessage form = await client.GetAsync("/Account/ChangePassword", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
    }

    [Fact]
    public async Task ChangesThePasswordKeepingThisSessionAndEndingEveryOther()
    {
        using HttpClient client = NewClient();
        HttpResponseMessage signIn = await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        // A second browser signed in before the change.
        using HttpClient other = ClientWithCookieFrom(signIn);
        Assert.Equal(HttpStatusCode.OK,
            (await other.GetAsync("/Dashboard", TestContext.Current.CancellationToken)).StatusCode);

        HttpResponseMessage changed = await ChangePasswordAsync(client, Password, NewPassword);

        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);

        // This session was re-issued with the new stamp and carries on.
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/Dashboard", TestContext.Current.CancellationToken)).StatusCode);

        // The other one still holds the old stamp and is ended.
        HttpResponseMessage otherAfter = await other.GetAsync("/Dashboard", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, otherAfter.StatusCode);
        Assert.Contains("/Account/Login", otherAfter.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);

        // And the stored hash is genuinely the new password, not merely changed.
        AdministratorRecord stored = await ReadAdministratorAsync(_privilegedUser);
        IPasswordHasher hasher = _factory.Services.GetRequiredService<IPasswordHasher>();

        Assert.True(hasher.Verify(NewPassword, stored.Credential!.Value));
        Assert.False(hasher.Verify(Password, stored.Credential!.Value));
        Assert.False(stored.MustChangePassword);
    }

    [Fact]
    public async Task RefusesAPasswordChangeWithoutTheCurrentPassword()
    {
        // A session alone must not be enough to take the account away from its owner.
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await ChangePasswordAsync(client, "not the current password", NewPassword);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Your current password was not correct.", html, StringComparison.Ordinal);

        AdministratorRecord stored = await ReadAdministratorAsync(_privilegedUser);
        Assert.True(_factory.Services.GetRequiredService<IPasswordHasher>().Verify(Password, stored.Credential!.Value));

        // The form is a password oracle, so a wrong guess counts toward lockout.
        int failures = await QueryAsync<int>(
            "SELECT ISNULL(MAX(FailedCount), 0) FROM core.AuthenticationAttempt WHERE SubjectType = 2 AND SubjectKey = @userName;",
            new { userName = _privilegedUser });

        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task ExplainsAShortPasswordOnceTheCurrentOneIsProven()
    {
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await ChangePasswordAsync(client, Password, "short");
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Use at least 12 characters", html, StringComparison.Ordinal);

        // Nothing typed is echoed back into the page.
        Assert.DoesNotContain(Password, html, StringComparison.Ordinal);
    }

    // ---- Administrator management ------------------------------------------

    [Fact]
    public async Task RefusesAdministratorManagementWithoutThePermission()
    {
        using HttpClient client = NewClient();
        await SignInAsync(client, _unprivilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await client.GetAsync("/Administrators", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Denied", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatesAnAdministratorWhoCanSignInAndMustChangeTheIssuedPassword()
    {
        // The whole chain in one test: create, issue a password and an
        // authenticator shown once, sign in with exactly what was shown, activate
        // the authenticator on first use, and be held at the change-password page.
        string newUser = $"itest.new.{Guid.NewGuid():N}"[..40];

        try
        {
            using HttpClient admin = NewClient();
            await SignInAsync(admin, _privilegedUser, Password, CurrentCode());

            HttpResponseMessage created = await PostAsync(admin, "/Administrators/Create",
            [
                new("UserName", newUser),
                new("DisplayName", "Created By Test"),
                new("RoleId", (await RoleIdAsync("Report Viewer")).ToString(CultureInfo.InvariantCulture)),
            ], formPath: "/Administrators");

            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            string html = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Match password = IssuedPassword().Match(html);
            Match secret = SharedSecret().Match(html);

            Assert.True(password.Success, "the page did not show the issued password");
            Assert.True(secret.Success, "the page did not show the authenticator secret");

            using HttpClient newcomer = NewClient();
            string code = new Totp(Base32Encoding.ToBytes(secret.Groups[1].Value)).ComputeTotp(DateTime.UtcNow);

            HttpResponseMessage signIn = await SignInAsync(newcomer, newUser, password.Groups[1].Value, code);
            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);

            HttpResponseMessage reports = await newcomer.GetAsync("/Reports/Attendance", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, reports.StatusCode);
            Assert.Contains("/Account/ChangePassword", reports.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);

            AdministratorRecord stored = await ReadAdministratorAsync(newUser);
            Assert.Equal(AdministratorMfaStatus.Active, stored.MfaStatus);
            Assert.Contains("Report.View", stored.Permissions);
        }
        finally
        {
            await RemoveAdministratorAsync(newUser);
        }
    }

    [Fact]
    public async Task RefusesToDeactivateYourOwnAccount()
    {
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        int ownId = await AdministratorIdAsync(_privilegedUser);

        HttpResponseMessage response = await PostAsync(client, "/Administrators/SetStatus",
        [
            new("administratorId", ownId.ToString(CultureInfo.InvariantCulture)),
            new("active", "false"),
            new("reason", "trying to lock myself out"),
        ], formPath: "/Administrators");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        HttpResponseMessage page = await client.GetAsync("/Administrators", TestContext.Current.CancellationToken);
        string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("You cannot do that to your own account.", html, StringComparison.Ordinal);
        Assert.True((await ReadAdministratorAsync(_privilegedUser)).IsActive);
    }

    [Fact]
    public async Task DeactivatingAnAdministratorEndsTheirOpenSession()
    {
        using HttpClient target = NewClient();
        await SignInAsync(target, _unprivilegedUser, Password, CurrentCode());
        Assert.Equal(HttpStatusCode.OK,
            (await target.GetAsync("/Dashboard", TestContext.Current.CancellationToken)).StatusCode);

        using HttpClient admin = NewClient();
        await SignInAsync(admin, _privilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await PostAsync(admin, "/Administrators/SetStatus",
        [
            new("administratorId", (await AdministratorIdAsync(_unprivilegedUser)).ToString(CultureInfo.InvariantCulture)),
            new("active", "false"),
            new("reason", "Integration test"),
        ], formPath: "/Administrators");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        HttpResponseMessage after = await target.GetAsync("/Dashboard", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/Account/Login", after.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- Helpers -----------------------------------------------------------

    private static async Task<HttpResponseMessage> ChangePasswordAsync(HttpClient client, string current, string replacement) =>
        await PostAsync(client, "/Account/ChangePassword",
        [
            new("CurrentPassword", current),
            new("NewPassword", replacement),
            new("ConfirmPassword", replacement),
        ], formPath: "/Account/ChangePassword");

    /// <summary>
    /// A second client presenting the session cookie the first one was issued —
    /// another browser, signed in before whatever the test does next.
    /// </summary>
    private HttpClient ClientWithCookieFrom(HttpResponseMessage signInResponse)
    {
        string cookie = signInResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.Select(value => value.Split(';')[0]).First(value => value.StartsWith("ClockInXtra.Admin=", StringComparison.Ordinal))
            : throw new InvalidOperationException("The sign-in response issued no session cookie.");

        HttpClient client = _factory.Client(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private async Task<AdministratorRecord> ReadAdministratorAsync(string userName) =>
        await _factory.Services.GetRequiredService<IAdministratorRepository>()
            .GetForAuthenticationAsync(userName, TestContext.Current.CancellationToken)
        ?? throw new InvalidOperationException($"Administrator {userName} does not exist.");

    private static Task<int> AdministratorIdAsync(string userName) =>
        QueryAsync<int>("SELECT AdministratorId FROM core.Administrator WHERE UserName = @userName;", new { userName });

    private static Task<int> RoleIdAsync(string name) =>
        QueryAsync<int>("SELECT RoleId FROM core.Role WHERE Name = @name;", new { name });

    private static Task RemoveAdministratorAsync(string userName) =>
        ExecuteAsync("""
            DELETE FROM core.AuthenticationAttempt WHERE SubjectType = 2 AND SubjectKey = @userName;
            DELETE FROM core.Administrator WHERE UserName = @userName;
            """,
            new { userName });

    [GeneratedRegex("""<dd class="secret"><code>([A-Za-z0-9-]+)</code></dd>""")]
    private static partial Regex IssuedPassword();
}
