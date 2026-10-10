using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// Revoking a device that is already in service.
/// </summary>
/// <remarks>
/// <para>
/// The gap these cover: <c>Revoke</c>, the <c>Device.Revoke</c> permission and
/// <c>admin.usp_Device_Revoke</c> all existed, but the portal's only device
/// list was the pending-approval page. No screen could reach an <b>active</b>
/// device, so one could not be revoked at all. The single route to revocation
/// was approving a replacement, which revokes the previous device as a side
/// effect — backwards for a lost or stolen handset, which has to be cut off
/// before anything replaces it.
/// </para>
/// <para>
/// The property under test is therefore not "the page renders" but "an active
/// device can be taken out of service from the portal, and is actually out of
/// service in the database afterwards".
/// </para>
/// </remarks>
public sealed partial class AuthenticatedPortalTests
{
    [Fact]
    public async Task ListsAnActiveDeviceAndRevokesIt()
    {
        const string userId = "device.listed";
        int mobileUserId = await CreateEmployeeAsync(userId);

        try
        {
            int deviceId = await CreateActiveDeviceAsync(mobileUserId);

            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            HttpResponseMessage list = await client.GetAsync(
                "/Devices", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, list.StatusCode);

            string html = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // The pending page would not have shown this device at all.
            Assert.Contains(userId, html, StringComparison.Ordinal);

            Match rowVersion = RowVersionField().Match(html);
            Assert.True(rowVersion.Success, "The device list carried no row version to revoke with.");

            HttpResponseMessage revoked = await PostAsync(
                client,
                "/Devices/Revoke",
                [
                    new KeyValuePair<string, string>("deviceId", deviceId.ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>("rowVersion", AttributeValue(rowVersion)),
                    new KeyValuePair<string, string>("reason", "Handset reported lost."),
                    new KeyValuePair<string, string>("returnTo", "Index"),
                ],
                formPath: "/Devices");

            await AssertRedirectedAsync(revoked, "revoking a device");

            // Back to the list it was submitted from, not the pending page.
            Assert.Contains(
                "/Devices",
                revoked.Headers.Location?.OriginalString ?? string.Empty,
                StringComparison.Ordinal);

            // The assertion that matters: the row, not the page.
            int status = await QueryAsync<int>(
                "SELECT Status FROM core.Device WHERE DeviceId = @deviceId;",
                new { deviceId });

            Assert.Equal(2, status);   // Revoked

            string? reason = await QueryAsync<string?>(
                "SELECT RevokedReason FROM core.Device WHERE DeviceId = @deviceId;",
                new { deviceId });

            // Shown to the employee in the app, so it has to survive the trip.
            Assert.Equal("Handset reported lost.", reason);
        }
        finally
        {
            await RemoveDevicesAsync(mobileUserId);
            await RemoveEmployeeAsync(mobileUserId);
        }
    }

    [Fact]
    public async Task RefusesToRevokeWithoutAReason()
    {
        const string userId = "device.noreason";
        int mobileUserId = await CreateEmployeeAsync(userId);

        try
        {
            int deviceId = await CreateActiveDeviceAsync(mobileUserId);

            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            HttpResponseMessage list = await client.GetAsync(
                "/Devices", TestContext.Current.CancellationToken);
            string html = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Match rowVersion = RowVersionField().Match(html);
            Assert.True(rowVersion.Success);

            await PostAsync(
                client,
                "/Devices/Revoke",
                [
                    new KeyValuePair<string, string>("deviceId", deviceId.ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>("rowVersion", AttributeValue(rowVersion)),
                    new KeyValuePair<string, string>("reason", "   "),
                    new KeyValuePair<string, string>("returnTo", "Index"),
                ],
                formPath: "/Devices");

            // The reason is what the employee is shown when their phone stops
            // working. A blank one leaves them with a dead app and no account
            // of why, so the device stays active rather than being revoked
            // anonymously.
            int status = await QueryAsync<int>(
                "SELECT Status FROM core.Device WHERE DeviceId = @deviceId;",
                new { deviceId });

            Assert.Equal(1, status);   // Active, untouched
        }
        finally
        {
            await RemoveDevicesAsync(mobileUserId);
            await RemoveEmployeeAsync(mobileUserId);
        }
    }

    [Fact]
    public async Task RefusesTheDeviceListToAnAdministratorWhoHoldsNoPermissions()
    {
        using HttpClient client = NewClient();
        await SignInAsync(client, _unprivilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await client.GetAsync(
            "/Devices", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/Denied",
            response.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAnUnknownStatusFilterRatherThanWideningIt()
    {
        // Silently showing everything when the filter is not understood is how
        // somebody revokes a device they did not mean to be looking at.
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        HttpResponseMessage response = await client.GetAsync(
            "/Devices?status=9", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Inserts an approved, in-service device for an employee.</summary>
    private static async Task<int> CreateActiveDeviceAsync(int mobileUserId)
    {
        // The key is never used here: these tests are about administration, not
        // signatures. It only has to satisfy the column's shape — an
        // uncompressed P-256 point, 0x04 followed by 64 bytes.
        byte[] publicKey = [0x04, .. Enumerable.Repeat((byte)0x01, 64)];
        byte[] thumbprint = SHA256.HashData(publicKey);

        return await QueryAsync<int>(
            """
            INSERT INTO core.Device
                (MobileUserId, PublicKey, PublicKeyThumbprint, Platform, AttestationLevel,
                 Status, DeviceModel, OsVersion, AppVersion, RegisteredUtc, ApprovedUtc, LastSeenUtc)
            VALUES
                (@mobileUserId, @publicKey, @thumbprint, 1, 2,
                 1, N'Test Handset', N'15', N'1.0.0', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """,
            new { mobileUserId, publicKey, thumbprint });
    }

    private static async Task RemoveDevicesAsync(int mobileUserId) =>
        await ExecuteAsync(
            "DELETE FROM core.Device WHERE MobileUserId = @mobileUserId;",
            new { mobileUserId });

    [GeneratedRegex(@"name=""rowVersion""\s+value=""([^""]*)""")]
    private static partial Regex RowVersionField();
}
