using System.Net;
using System.Text.RegularExpressions;
using Attendance.Application.Abstractions;
using Attendance.Tests;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// Sign-in and authorization, exercised against a real administrator account.
/// </summary>
/// <remarks>
/// <para>
/// These are the tests that cover the privileged path. Everything else in this
/// project checks that an anonymous visitor is turned away; these check what
/// happens once somebody is <em>through</em> the door — that both factors are
/// genuinely required, that a code cannot be used twice, that a permission
/// actually gates a page, and that rotating the security stamp ends a live
/// session.
/// </para>
/// <para>
/// The authenticator is real. The account's TOTP secret is protected with the
/// same Data Protection key ring the hosted portal resolves, so the portal
/// decrypts what the fixture wrote, and the codes are generated with the same
/// algorithm a phone would use. Disabling MFA for the tests would have left the
/// second factor entirely uncovered.
/// </para>
/// </remarks>
public sealed partial class AuthenticatedPortalTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestEnvironment.ConnectionString;

    private const string Password = "correct horse battery staple";

    /// <summary>
    /// The attendance settings these tests need in order to mean anything, and
    /// the values they are given when the database does not have them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is here.</b> A freshly deployed database leaves every one of
    /// these unset, on purpose: the system refuses to operate rather than invent
    /// a business rule (Claude.md §15, §68). The settings page test and the
    /// corrections test both read them — one renders the selected time zone,
    /// the other cannot raise a correction while corrections are switched off
    /// — so on a new database they failed while passing on a developer machine
    /// where the decisions had already been made. That is a defect in the tests,
    /// not in the deployment, and it surfaced the first time CI ran them against
    /// a database it had created itself.
    /// </para>
    /// <para>
    /// <b>Only what is missing is supplied.</b> A value the business has already
    /// decided is left exactly as it is, by a single statement that matches only
    /// unset rows — so this cannot repeat the incident where a test run quietly
    /// replaced the decided settings. Whatever is filled in is returned to unset
    /// on the way out. The values mirror DEC-06 and DEC-08, so the behaviour
    /// under test is the behaviour that was decided.
    /// </para>
    /// </remarks>
    private static readonly (string Key, string Value)[] RequiredSettings =
    [
        ("Attendance.BusinessTimeZoneId", "W. Central Africa Standard Time"),
        ("Attendance.ClockInOpenTime", "00:00"),
        ("Attendance.ClockInCloseTime", "08:30"),
        ("Attendance.ClockInAfterCloseAction", "Reject"),
        ("Attendance.ClockOutOpenTime", "00:00"),
        ("Attendance.ClockOutBeforeOpenAction", "Reject"),
        ("Attendance.MinimumMinutesBeforeClockOut", "1"),
        ("Attendance.AllowCorrections", "true"),
        ("Attendance.CorrectionsRequireApproval", "true"),
    ];

    private readonly List<string> _settingsSupplied = [];

    private WebApplicationFactory<Program> _factory = null!;
    private byte[] _totpSecret = null!;

    private string _privilegedUser = null!;
    private string _unprivilegedUser = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = PortalHost.Create();

        // Force the host to build so its services — including the Data
        // Protection key ring the portal will decrypt with — are available.
        _ = _factory.Services.GetRequiredService<IPasswordHasher>();

        _totpSecret = KeyGeneration.GenerateRandomKey(20);

        _privilegedUser = $"itest.super.{Guid.NewGuid():N}"[..40];
        _unprivilegedUser = $"itest.none.{Guid.NewGuid():N}"[..40];

        await CreateAdministratorAsync(_privilegedUser, "Super Administrator");
        await CreateAdministratorAsync(_unprivilegedUser, role: null);

        await SupplyMissingSettingsAsync();
    }

    /// <summary>
    /// Fills in the settings in <see cref="RequiredSettings"/> that have no
    /// value, recording which ones so they can be put back.
    /// </summary>
    private async Task SupplyMissingSettingsAsync()
    {
        foreach ((string key, string value) in RequiredSettings)
        {
            // All three fields move together, because
            // CK_ApplicationSetting_Confirmation ties them:
            // RequiresBusinessConfirmation = 1 requires ConfirmedUtc to be NULL
            // and 0 requires it not to be. Writing either on its own violates
            // the constraint, which is how an earlier attempt at this broke
            // every test in the class.
            //
            // The setting is left CONFIRMED rather than merely populated,
            // because the settings page renders a confirmed value differently
            // — its confirm box starts ticked so that editing the value does
            // not silently withdraw the business's confirmation — and one of
            // these tests asserts exactly that.
            //
            // The filter is the real definition of undecided, the same one the
            // deployment report uses: no value AND no confirmation. A blank
            // that has been confirmed is a decision (DEC-07 reads that way for
            // retention), so matching on SettingValue alone would overwrite
            // one.
            //
            // One statement, so a setting that already holds a decision is
            // never written to — not even briefly.
            int supplied = await ExecuteCountAsync(
                """
                UPDATE core.ApplicationSetting
                SET SettingValue                 = @value,
                    RequiresBusinessConfirmation = 0,
                    ConfirmedUtc                 = SYSUTCDATETIME()
                WHERE SettingKey = @key
                  AND SettingValue IS NULL
                  AND ConfirmedUtc IS NULL
                """,
                new { key, value });

            if (supplied > 0)
            {
                _settingsSupplied.Add(key);
            }
        }
    }

    /// <summary>Returns the settings this test supplied to unset.</summary>
    private async Task RestoreSuppliedSettingsAsync()
    {
        foreach (string key in _settingsSupplied)
        {
            await ExecuteAsync(
                """
                UPDATE core.ApplicationSetting
                SET SettingValue                 = NULL,
                    RequiresBusinessConfirmation = 1,
                    ConfirmedUtc                 = NULL
                WHERE SettingKey = @key
                """,
                new { key });
        }

        _settingsSupplied.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await RestoreSuppliedSettingsAsync();
        await RemoveAdministratorsAsync();
        await _factory.DisposeAsync();
    }

    // ---- Both factors ------------------------------------------------------

    [Fact]
    public async Task SignsInWithTheCorrectPasswordAndCode()
    {
        using HttpClient client = NewClient();

        HttpResponseMessage response = await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // The redirect target is "/" rather than "/Dashboard" because the default
        // route makes the dashboard the site root, so URL generation emits the
        // shortest form. Following it is a stronger assertion than matching the
        // path anyway: it proves the cookie authenticates the next request.
        HttpResponseMessage landing = await client.GetAsync(
            response.Headers.Location!, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);

        string html = await landing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Your permissions", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesTheCorrectPasswordWithoutACode()
    {
        // Security.RequireAdministratorMfa is true, so a password alone is not a
        // sign-in however correct it is.
        using HttpClient client = NewClient();

        HttpResponseMessage response = await SignInAsync(client, _privilegedUser, Password, code: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // redisplayed form
        await AssertNotSignedInAsync(client);
    }

    [Fact]
    public async Task RefusesAWrongPassword()
    {
        using HttpClient client = NewClient();

        HttpResponseMessage response = await SignInAsync(client, _privilegedUser, "not the password", CurrentCode());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertNotSignedInAsync(client);
    }

    [Fact]
    public async Task RefusesAnUnknownAccountWithTheSameMessageAsAWrongPassword()
    {
        // An insider is exactly the attacker who benefits from learning which
        // administrator accounts exist.
        using HttpClient client = NewClient();

        // A fresh name each run: failed attempts count towards lockout for names
        // that do not exist too (so lockout cannot reveal which ones do), and a
        // fixed name would be locked after a few runs.
        string stranger = $"itest.unknown.{Guid.NewGuid():N}"[..40];
        HttpResponseMessage unknown = await SignInAsync(client, stranger, Password, CurrentCode());
        string unknownHtml = await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using HttpClient second = NewClient();
        HttpResponseMessage wrongPassword = await SignInAsync(second, _privilegedUser, "wrong", CurrentCode());
        string wrongPasswordHtml = await wrongPassword.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        const string expected = "The details entered were not correct.";
        Assert.Contains(expected, unknownHtml, StringComparison.Ordinal);
        Assert.Contains(expected, wrongPasswordHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAnAuthenticatorCodeThatWasAlreadyUsed()
    {
        // This is what admin.usp_Administrator_TryConsumeTimeStep exists for.
        // Without it one code would stay usable for its whole window on the
        // account that can revoke devices and rewrite attendance rules.
        string code = CurrentCode();

        using HttpClient first = NewClient();
        HttpResponseMessage accepted = await SignInAsync(first, _privilegedUser, Password, code);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);

        using HttpClient second = NewClient();
        HttpResponseMessage replayed = await SignInAsync(second, _privilegedUser, Password, code);

        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        await AssertNotSignedInAsync(second);
    }

    [Fact]
    public async Task SigningOutEndsEverySessionAndIsAudited()
    {
        // Two live sessions for one administrator — two browsers, or one browser
        // and a copy of its cookie. The second signs in with the next time step's
        // code, because the first code is spent (replay protection).
        using HttpClient first = NewClient();
        using HttpClient second = NewClient();

        await SignInAsync(first, _privilegedUser, Password, CurrentCode());
        await SignInAsync(second, _privilegedUser, Password, CodeAt(DateTime.UtcNow.AddSeconds(30)));

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/Dashboard", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/Dashboard", TestContext.Current.CancellationToken)).StatusCode);

        HttpResponseMessage signOut = await PostAsync(first, "/Account/Logout", [], formPath: "/Dashboard");
        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);

        await AssertNotSignedInAsync(first);

        // The other session ends too: its cookie still decrypts, but the stamp
        // it carries is no longer the account's.
        await AssertNotSignedInAsync(second);

        int audited = await QueryAsync<int>(
            "SELECT COUNT(*) FROM audit.AuditLog WHERE EventType = 'Administrator.LoggedOut' AND ActorDisplay = @UserName",
            new { UserName = _privilegedUser });
        Assert.Equal(1, audited);
    }

    // ---- Authorization -----------------------------------------------------

    [Fact]
    public async Task LetsAPermittedAdministratorReachDeviceApproval()
    {
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await client.GetAsync(
            "/Devices/Pending", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RefusesAnAdministratorWhoHoldsNoPermissions()
    {
        // Authenticated is not authorized. An account with no role reaches the
        // dashboard and nothing else.
        using HttpClient client = NewClient();
        await SignInAsync(client, _unprivilegedUser, Password, CurrentCode());

        HttpResponseMessage dashboard = await client.GetAsync(
            "/Dashboard", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);

        HttpResponseMessage devices = await client.GetAsync(
            "/Devices/Pending", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, devices.StatusCode);
        Assert.Contains("/Account/Denied", devices.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesSettingsToAnAdministratorWhoHoldsNoPermissions()
    {
        using HttpClient client = NewClient();
        await SignInAsync(client, _unprivilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await client.GetAsync(
            "/Settings", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Denied", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- Session lifetime --------------------------------------------------

    [Fact]
    public async Task EndsALiveSessionWhenTheSecurityStampRotates()
    {
        // Rotating the stamp is what a password change, a disabling or a role
        // change does. The cookie is still perfectly valid and correctly signed —
        // and must stop working anyway (threat TH-35).
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/Dashboard", TestContext.Current.CancellationToken)).StatusCode);

        await ExecuteAsync(
            "UPDATE core.Administrator SET SecurityStamp = NEWID() WHERE UserName = @userName;",
            new { userName = _privilegedUser });

        HttpResponseMessage afterRotation = await client.GetAsync(
            "/Dashboard", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, afterRotation.StatusCode);
        Assert.Contains("/Account/Login", afterRotation.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndsALiveSessionWhenTheAccountIsDisabled()
    {
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        await ExecuteAsync(
            "UPDATE core.Administrator SET Status = 0 WHERE UserName = @userName;",
            new { userName = _privilegedUser });

        try
        {
            HttpResponseMessage response = await client.GetAsync(
                "/Dashboard", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }
        finally
        {
            await ExecuteAsync(
                "UPDATE core.Administrator SET Status = 1 WHERE UserName = @userName;",
                new { userName = _privilegedUser });
        }
    }

    // ---- Authenticator enrolment -------------------------------------------

    [Fact]
    public async Task EnrolsAnAuthenticatorAndActivatesItWithAWorkingCode()
    {
        // The whole chain: generate a secret, protect it, store it pending,
        // read it back through admin.usp_MfaCredential_GetForActivation, verify
        // one code and activate. An employee cannot clock in until this has
        // happened, so a break anywhere along it is a break in the product.
        string employeeUserId = $"itest.emp.{Guid.NewGuid():N}"[..40];
        int mobileUserId = await CreateEmployeeAsync(employeeUserId);

        try
        {
            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            string secret = await EnrolAsync(client, mobileUserId, employeeUserId);

            // A real authenticator would compute exactly this.
            string code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(DateTime.UtcNow);

            HttpResponseMessage activated = await PostAsync(client, "/Employees/Activate",
            [
                new("mobileUserId", mobileUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("code", code),
            ]);

            Assert.Equal(HttpStatusCode.Redirect, activated.StatusCode);

            bool isActive = await QueryAsync<bool>(
                "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM core.MfaCredential WHERE MobileUserId = @id AND Status = 1) THEN 1 ELSE 0 END AS BIT);",
                new { id = mobileUserId });

            Assert.True(isActive, "the authenticator should be active after a valid code");
        }
        finally
        {
            await RemoveEmployeeAsync(mobileUserId);
        }
    }

    [Fact]
    public async Task LeavesTheAuthenticatorUnusableUntilACodeProvesIt()
    {
        // Enrolment alone must not activate. Otherwise a secret that was never
        // successfully scanned leaves the employee stuck at a door the next
        // morning, with nobody able to tell why.
        string employeeUserId = $"itest.emp.{Guid.NewGuid():N}"[..40];
        int mobileUserId = await CreateEmployeeAsync(employeeUserId);

        try
        {
            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            await EnrolAsync(client, mobileUserId, employeeUserId);

            bool isActive = await QueryAsync<bool>(
                "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM core.MfaCredential WHERE MobileUserId = @id AND Status = 1) THEN 1 ELSE 0 END AS BIT);",
                new { id = mobileUserId });

            Assert.False(isActive, "enrolment must leave the credential pending, not active");
        }
        finally
        {
            await RemoveEmployeeAsync(mobileUserId);
        }
    }

    [Fact]
    public async Task RefusesToEnrolWithoutThePermission()
    {
        string employeeUserId = $"itest.emp.{Guid.NewGuid():N}"[..40];
        int mobileUserId = await CreateEmployeeAsync(employeeUserId);

        try
        {
            using HttpClient client = NewClient();
            await SignInAsync(client, _unprivilegedUser, Password, CurrentCode());

            HttpResponseMessage response = await PostAsync(client, "/Employees/Enrol",
                formIsReachable: false,
                fields:
            [
                new("mobileUserId", mobileUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("userId", employeeUserId),
            ]);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/Denied", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            await RemoveEmployeeAsync(mobileUserId);
        }
    }

    /// <summary>Enrols an authenticator and returns the secret shown once.</summary>
    private static async Task<string> EnrolAsync(HttpClient client, int mobileUserId, string userId)
    {
        HttpResponseMessage response = await PostAsync(client, "/Employees/Enrol",
        [
            new("mobileUserId", mobileUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("userId", userId),
        ]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Match secret = SharedSecret().Match(html);
        Assert.True(secret.Success, "the enrolment page did not show a shared secret");

        return secret.Groups[1].Value;
    }

    [GeneratedRegex("""<p class="secret"><code>([A-Z2-7]+)</code></p>""")]
    private static partial Regex SharedSecret();

    // ---- Office locations --------------------------------------------------

    [Fact]
    public async Task CreatesAnOfficeLocationAndListsIt()
    {
        // Without an active office every location check refuses, so this is the
        // last prerequisite before anybody can clock in anywhere.
        string name = $"ITEST {Guid.NewGuid():N}"[..24];

        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        HttpResponseMessage created = await PostAsync(client, "/Locations/Create",
        [
            new("Name", name),
            new("Latitude", "6.465422"),
            new("Longitude", "3.406448"),
            new("AllowedRadiusMeters", "5"),
            new("IsActive", "true"),
        ], formPath: "/Locations");

        try
        {
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

            bool exists = await QueryAsync<bool>(
                "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM core.OfficeLocation WHERE Name = @name AND Status = 1) THEN 1 ELSE 0 END AS BIT);",
                new { name });

            Assert.True(exists, "the office should exist and be active");

            HttpResponseMessage list = await client.GetAsync("/Locations", TestContext.Current.CancellationToken);
            string html = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Contains(name, html, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync("DELETE FROM core.OfficeLocation WHERE Name = @name;", new { name });
        }
    }

    [Fact]
    public async Task RefusesAnImpossibleLatitude()
    {
        // Refused at the boundary rather than reaching the geodesic calculation,
        // where it would silently produce a meaningless distance.
        string name = $"ITEST {Guid.NewGuid():N}"[..24];

        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        await PostAsync(client, "/Locations/Create",
        [
            new("Name", name),
            new("Latitude", "95"),
            new("Longitude", "3.406448"),
            new("AllowedRadiusMeters", "5"),
            new("IsActive", "true"),
        ], formPath: "/Locations");

        bool exists = await QueryAsync<bool>(
            "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM core.OfficeLocation WHERE Name = @name) THEN 1 ELSE 0 END AS BIT);",
            new { name });

        Assert.False(exists, "an office with latitude 95 must not be created");
    }

    [Fact]
    public async Task RefusesOfficeManagementWithoutThePermission()
    {
        using HttpClient client = NewClient();
        await SignInAsync(client, _unprivilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await client.GetAsync(
            "/Locations", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Denied", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- Reporting and audit -----------------------------------------------

    [Theory]
    [InlineData("/Reports/Attendance")]
    [InlineData("/Reports/Attendance?status=Open&exception=MissingClockOut&userId=nobody&officeLocationId=1")]
    [InlineData("/Reports/Attendance?exception=LateClockIn")]
    [InlineData("/Reports/Attendance?exception=EarlyClockOut&status=Closed")]
    [InlineData("/Reports/Attendance?exception=MockedLocation&department=Finance")]
    [InlineData("/Reports/Attendance?status=99&exception=77")]
    [InlineData("/Reports/Failures")]
    [InlineData("/Reports/Failures?category=Location")]
    [InlineData("/Reports/Failures?category=Device&subject=e.adeyemi")]
    [InlineData("/Reports/Failures?category=99&before=5")]
    [InlineData("/Reports/Audit")]
    public async Task ServesTheReportsToAPermittedAdministrator(string path)
    {
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/Reports/Attendance")]
    [InlineData("/Reports/Failures")]
    [InlineData("/Reports/Audit")]
    public async Task RefusesTheReportsWithoutThePermission(string path)
    {
        using HttpClient client = NewClient();
        await SignInAsync(client, _unprivilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Denied", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowsTheSignInItJustPerformedInTheAuditTrail()
    {
        // A successful sign-in writes both an audit entry and a security event.
        // This proves the trail is actually being written, not merely queryable.
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await client.GetAsync(
            "/Reports/Audit", TestContext.Current.CancellationToken);

        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Administrator.LoginSucceeded", html, StringComparison.Ordinal);
        Assert.Contains(_privilegedUser, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsARefusedSignInAsAnIdentityFailureAndNeverASuccess()
    {
        // The report once matched a list of client-facing reason codes the server
        // never writes, and with no category chosen it listed successful sign-ins
        // as failures. Both are checked against real events written here.
        string stranger = $"itest.unknown.{Guid.NewGuid():N}"[..40];

        using (HttpClient refused = NewClient())
        {
            await SignInAsync(refused, stranger, Password, CurrentCode());
        }

        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        string identity = await client.GetStringAsync(
            $"/Reports/Failures?category=Identity&subject={Uri.EscapeDataString(stranger)}",
            TestContext.Current.CancellationToken);

        Assert.Contains(stranger, identity, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN_ADMINISTRATOR", identity, StringComparison.Ordinal);

        // The same refusal is not a location, device or integrity failure.
        string location = await client.GetStringAsync(
            $"/Reports/Failures?category=Location&subject={Uri.EscapeDataString(stranger)}",
            TestContext.Current.CancellationToken);
        Assert.Contains("Nothing was refused in that window.", location, StringComparison.Ordinal);
        Assert.DoesNotContain("UNKNOWN_ADMINISTRATOR", location, StringComparison.Ordinal);

        // The privileged administrator has just signed in successfully; that is
        // not a failure, whichever filter is used.
        string all = await client.GetStringAsync(
            $"/Reports/Failures?subject={Uri.EscapeDataString(_privilegedUser)}",
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("LOGIN_OK", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Auth.Succeeded", all, StringComparison.Ordinal);
    }

    // ---- Helpers -----------------------------------------------------------

    private HttpClient NewClient() =>
        _factory.Client(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

    private string CurrentCode() =>
        new Totp(_totpSecret).ComputeTotp(DateTime.UtcNow);

    private string CodeAt(DateTime utc) =>
        new Totp(_totpSecret).ComputeTotp(utc);

    /// <summary>Performs a full sign-in, including the anti-forgery exchange.</summary>
    private static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client, string userName, string password, string? code)
    {
        HttpResponseMessage form = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        string html = await form.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Match token = AntiForgeryToken().Match(html);
        Assert.True(token.Success, "the sign-in form did not carry an anti-forgery token");

        List<KeyValuePair<string, string>> fields =
        [
            new("UserName", userName),
            new("Password", password),
            new("__RequestVerificationToken", AttributeValue(token)),
        ];

        if (code is not null)
        {
            fields.Add(new KeyValuePair<string, string>("AuthenticatorCode", code));
        }

        using FormUrlEncodedContent content = new(fields);

        return await client.PostAsync("/Account/Login", content, TestContext.Current.CancellationToken);
    }

    private static async Task AssertNotSignedInAsync(HttpClient client)
    {
        HttpResponseMessage dashboard = await client.GetAsync("/Dashboard", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode);
        Assert.Contains("/Account/Login", dashboard.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value read out of an HTML attribute, decoded the way a browser would
    /// decode it before posting it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not optional, and the reason is a bug this cost real time.</b> Razor
    /// HTML-encodes attribute values, and its encoder is deliberately
    /// aggressive: a <c>+</c> is written as <c>&amp;#x2B;</c>. Both a row
    /// version and an anti-forgery token are base64, so each contains a
    /// <c>+</c> roughly three times in ten.
    /// </para>
    /// <para>
    /// A browser parses the character reference back to <c>+</c> and posts the
    /// real value, so the portal is correct and users are unaffected. A test
    /// that scrapes the raw HTML with a regular expression is not a browser: it
    /// posted <c>AAAAAAAAC&amp;#x2B;I=</c>, which is not valid base64, and the
    /// server refused the form with "that request was not valid". The failure
    /// looked environmental — it appeared on Linux and not on Windows —
    /// because whether the value contains a <c>+</c> depends on the row version
    /// the database happens to be at, and CI rebuilds its database identically
    /// every run while a development one does not.
    /// </para>
    /// </remarks>
    private static string AttributeValue(Match match) =>
        WebUtility.HtmlDecode(match.Groups[1].Value);

    /// <summary>
    /// Asserts that a form POST redirected, and reports what the page objected
    /// to when it did not.
    /// </summary>
    /// <remarks>
    /// A bare <c>Assert.Equal(Redirect, response.StatusCode)</c> says only
    /// "expected Found, actual OK", which is the least useful thing it could
    /// say: a re-rendered form means the server rejected something, and the
    /// reason is in the body. Worth the few lines, because this failure shape
    /// has cost real time on a machine the tests could not be run on.
    /// </remarks>
    private static async Task AssertRedirectedAsync(HttpResponseMessage response, string what)
    {
        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        string complaints = string.Join(
            " | ",
            ValidationComplaint().Matches(body)
                .Select(match => match.Groups[1].Value.Trim())
                .Where(text => text.Length > 2)
                .Distinct(StringComparer.Ordinal));

        Assert.Fail(
            $"{what} did not redirect: {(int)response.StatusCode} {response.StatusCode}. "
            + (complaints.Length > 0
                ? $"The page objected: {complaints}"
                : $"No validation message in the body. First 600 characters: {body[..Math.Min(600, body.Length)]}"));
    }

    /// <summary>Validation text the portal renders, for failure messages only.</summary>
    [GeneratedRegex("""(?:field-validation-error[^>]*>|validation-summary-errors[\s\S]{0,200}?<li>|class="warning"[^>]*>)([^<]{3,300})""")]
    private static partial Regex ValidationComplaint();

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex AntiForgeryToken();

    private async Task CreateAdministratorAsync(string userName, string? role)
    {
        IPasswordHasher hasher = _factory.Services.GetRequiredService<IPasswordHasher>();
        ISecretProtector protector = _factory.Services.GetRequiredService<ISecretProtector>();

        PasswordHash hash = hasher.Hash(Password);

        // Protected with the host's own key ring, so the portal can read it back.
        byte[] protectedSecret = protector.Protect(SecretPurposes.AdministratorTotpSecret, _totpSecret);

        await ExecuteAsync("""
            INSERT INTO core.Administrator
                (UserName, DisplayName, HashFormat, Iterations, Salt, PasswordHash,
                 MustChangePassword, SecurityStamp, MfaSecretProtected, MfaStatus, Status, CreatedUtc)
            VALUES
                (@userName, N'Integration Test', @hashFormat, @iterations, @salt, @hash,
                 0, NEWID(), @secret, 2, 1, SYSUTCDATETIME());

            DECLARE @administratorId INT = SCOPE_IDENTITY();

            IF @role IS NOT NULL
            BEGIN
                INSERT INTO core.AdministratorRole (AdministratorId, RoleId, AssignedUtc)
                SELECT @administratorId, r.RoleId, SYSUTCDATETIME()
                FROM core.Role AS r WHERE r.Name = @role;
            END;
            """,
            new
            {
                userName,
                hashFormat = hash.HashFormat,
                iterations = hash.Iterations,
                salt = hash.Salt,
                hash = hash.Hash,
                secret = protectedSecret,
                role,
            });
    }

    private async Task RemoveAdministratorsAsync()
    {
        await ExecuteAsync("""
            DELETE FROM core.AdministratorRole
            WHERE AdministratorId IN (SELECT AdministratorId FROM core.Administrator WHERE UserName IN (@a, @b));

            DELETE FROM core.AuthenticationAttempt WHERE SubjectType = 2 AND (SubjectKey IN (@a, @b) OR SubjectKey LIKE N'itest.unknown.%');
            DELETE FROM core.Administrator WHERE UserName IN (@a, @b);
            """,
            new { a = _privilegedUser, b = _unprivilegedUser });
    }

    /// <summary>Posts a form, fetching an anti-forgery token first.</summary>
    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        List<KeyValuePair<string, string>> fields,
        string formPath = "/Employees",
        bool formIsReachable = true)
    {
        HttpResponseMessage page = await client.GetAsync(formPath, TestContext.Current.CancellationToken);
        string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Match token = AntiForgeryToken().Match(html);

        // Previously this posted without a token whenever the match failed, and
        // the POST was then refused for anti-forgery reasons that had nothing to
        // do with what the test was checking — several frames from the cause.
        // A caller that expects the form to render now says so, and finds out
        // here if it did not.
        //
        // formIsReachable: false is for the authorization tests, where the
        // caller holds no permission, cannot load the form, and the refusal is
        // the assertion.
        if (formIsReachable)
        {
            Assert.True(
                token.Success,
                $"{formPath} carried no anti-forgery token, so the POST to {path} could only have been "
                + $"refused. It answered {(int)page.StatusCode} {page.StatusCode}, first 400 characters: "
                + $"{html[..Math.Min(400, html.Length)]}");
        }

        if (token.Success)
        {
            fields.Add(new KeyValuePair<string, string>("__RequestVerificationToken", AttributeValue(token)));
        }

        using FormUrlEncodedContent content = new(fields);

        return await client.PostAsync(path, content, TestContext.Current.CancellationToken);
    }

    private static async Task<int> CreateEmployeeAsync(string userId)
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        return await connection.QuerySingleAsync<int>(new CommandDefinition("""
            INSERT INTO core.MobileUser (UserId, FirstName, LastName, Status)
            VALUES (@userId, N'Enrolment', N'Test', 1);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """,
            new { userId },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task RemoveEmployeeAsync(int mobileUserId)
    {
        await ExecuteAsync("""
            DELETE FROM core.MfaCredential WHERE MobileUserId = @id;
            DELETE FROM core.MobileUser WHERE MobileUserId = @id;
            """,
            new { id = mobileUserId });
    }

    private static async Task<T> QueryAsync<T>(string sql, object parameters)
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        return await connection.QuerySingleAsync<T>(new CommandDefinition(
            sql, parameters, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            sql, parameters, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Runs a statement and returns how many rows it affected, which is how
    /// SupplyMissingSettingsAsync knows whether it supplied a value or found one
    /// already there.
    /// </summary>
    private static async Task<int> ExecuteCountAsync(string sql, object? parameters = null)
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        return await connection.ExecuteAsync(new CommandDefinition(
            sql, parameters, cancellationToken: TestContext.Current.CancellationToken));
    }
}
