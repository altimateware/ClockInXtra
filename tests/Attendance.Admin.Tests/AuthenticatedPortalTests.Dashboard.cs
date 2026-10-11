using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// The landing page, and the paging the grids gained with it.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard's own documentation said it showed what was outstanding, and
/// it rendered a list of the viewer's permissions — which answers a question
/// nobody arrives with. These assert the counts are real rather than that the
/// page returns 200: a dashboard whose numbers are wrong is worse than one
/// that is missing, because it is believed.
/// </para>
/// </remarks>
public sealed partial class AuthenticatedPortalTests
{
    [Fact]
    public async Task TheDashboardCountsWorkThatIsActuallyOutstanding()
    {
        const string userId = "dash.pending";
        int mobileUserId = await CreateEmployeeAsync(userId);

        try
        {
            // An employee with no password, no authenticator and a device
            // nobody has approved: one for each queue the page reports.
            await CreatePendingDeviceAsync(mobileUserId);

            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            HttpResponseMessage response = await client.GetAsync(
                "/Dashboard", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Contains("Needs attention", html, StringComparison.Ordinal);
            Assert.Contains("Devices awaiting approval", html, StringComparison.Ordinal);
            Assert.Contains("Cannot clock in", html, StringComparison.Ordinal);

            // The number ON THE RIGHT CARD. The first version of this looked
            // for the figure anywhere in the page and passed with the count
            // hard-coded to zero, because another card happened to show the
            // same digit. Anchoring it to the label is the whole assertion.
            int pendingDevices = await QueryAsync<int>(
                "SELECT COUNT(*) FROM core.Device WHERE Status = 0;", new { });

            Assert.True(pendingDevices >= 1, "the fixture should have left a device awaiting approval");
            Assert.Equal(pendingDevices, FigureBeside(html, "Devices awaiting approval"));
        }
        finally
        {
            await RemoveDevicesAsync(mobileUserId);
            await RemoveEmployeeAsync(mobileUserId);
        }
    }

    [Fact]
    public async Task TheDashboardStillRendersWithoutItsNumbers()
    {
        // Whatever else is true, the page an administrator lands on after
        // signing in must not be the error page.
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await client.GetAsync(
            "/Dashboard", TestContext.Current.CancellationToken);

        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Your permissions", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeviceListPagesRatherThanShowingEverything()
    {
        const string userId = "dash.paged";
        int mobileUserId = await CreateEmployeeAsync(userId);

        try
        {
            await CreatePendingDeviceAsync(mobileUserId);

            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            // A page of one proves the mechanism without needing a fixture of
            // hundreds: the count sentence and the controls are driven by the
            // same totals whatever the page size.
            HttpResponseMessage response = await client.GetAsync(
                "/Devices?pageSize=1", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Contains("Showing 1", html, StringComparison.Ordinal);

            int devices = await QueryAsync<int>("SELECT COUNT(*) FROM core.Device;", new { });

            if (devices > 1)
            {
                // More rows than the page holds, so there must be a way to them.
                Assert.Contains("page=2", html, StringComparison.Ordinal);
            }
        }
        finally
        {
            await RemoveDevicesAsync(mobileUserId);
            await RemoveEmployeeAsync(mobileUserId);
        }
    }

    [Fact]
    public async Task PagingCarriesTheFilterWithIt()
    {
        // A pager that rebuilds the URL from scratch turns page two of a search
        // into page two of everything — on a page whose buttons revoke devices,
        // that is worth asserting rather than assuming.
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await client.GetAsync(
            "/Employees?search=nobody-matches-this&onlyNotReady=false&pageSize=1",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The search box keeps what was typed, and the count sentence is about
        // the filtered set rather than the whole table.
        Assert.Contains("nobody-matches-this", html, StringComparison.Ordinal);
        Assert.Contains("No employees to show.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedPageIsClampedRatherThanObeyed()
    {
        // The page size arrives from a query string. Without a ceiling, asking
        // for ten million rows is a denial of service needing no more authority
        // than being signed in.
        //
        // Asserting only a 200 would prove nothing, and the first version of
        // this test did exactly that: unclamped, the procedure refuses the page
        // size and returns no result set at all, so the page still renders 200
        // with an empty grid. The device below has to be ON it.
        const string userId = "dash.clamped";
        int mobileUserId = await CreateEmployeeAsync(userId);

        try
        {
            await CreatePendingDeviceAsync(mobileUserId);

            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            HttpResponseMessage response = await client.GetAsync(
                "/Devices?pageSize=1000000", TestContext.Current.CancellationToken);

            // Clamped to the maximum the procedures accept, not refused: a silly
            // page size is a preference, not an error.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Contains(userId, html, StringComparison.Ordinal);
            Assert.DoesNotContain("No devices to show.", html, StringComparison.Ordinal);
        }
        finally
        {
            await RemoveDevicesAsync(mobileUserId);
            await RemoveEmployeeAsync(mobileUserId);
        }
    }

    /// <summary>
    /// The figure on the card carrying <paramref name="label"/>.
    /// </summary>
    /// <remarks>
    /// Anchored to the label rather than searching the page for a number,
    /// because several cards can legitimately show the same digit and a test
    /// that finds any of them is a test that cannot fail for its own reason.
    /// </remarks>
    private static int FigureBeside(string html, string label)
    {
        Match match = Regex.Match(
            html,
            @"card-figure""\s*>\s*(?<figure>\d+)\s*</p>\s*<p class=""card-label""\s*>\s*"
                + Regex.Escape(label),
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"no dashboard card was labelled '{label}'.");

        return int.Parse(match.Groups["figure"].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Inserts a device awaiting approval for an employee.</summary>
    private static async Task<int> CreatePendingDeviceAsync(int mobileUserId)
    {
        byte[] publicKey = [0x04, .. Enumerable.Repeat((byte)0x02, 64)];
        byte[] thumbprint = SHA256.HashData([.. publicKey, .. BitConverter.GetBytes(mobileUserId)]);

        return await QueryAsync<int>(
            """
            INSERT INTO core.Device
                (MobileUserId, PublicKey, PublicKeyThumbprint, Platform, AttestationLevel,
                 Status, DeviceModel, OsVersion, AppVersion, RegisteredUtc)
            VALUES
                (@mobileUserId, @publicKey, @thumbprint, 1, 2,
                 0, N'Pending Handset', N'15', N'1.0.0', SYSUTCDATETIME());
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """,
            new { mobileUserId, publicKey, thumbprint });
    }
}
