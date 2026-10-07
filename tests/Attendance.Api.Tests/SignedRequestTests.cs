using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Attendance.Api.Tests;

/// <summary>
/// End-to-end tests of the request-signing pipeline, against the real
/// application and a real database.
/// </summary>
/// <remarks>
/// <para>
/// These are the tests that matter most in the API project. Everything else
/// verifies a component in isolation; these verify that a request signed the way
/// the mobile application will sign it is <b>accepted</b>, and that each way of
/// tampering with it is refused.
/// </para>
/// <para>
/// The fixture rows are committed rather than rolled back, because the
/// application runs on its own connection and cannot see an uncommitted
/// transaction. They are removed afterwards — with the documented exception of
/// audit ledger rows, which are append-only by design and cannot be deleted by
/// anyone, including this test.
/// </para>
/// </remarks>
public sealed class SignedRequestTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestHost.ConnectionString;

    private readonly ECDsa _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private int _mobileUserId;
    private int _deviceId;
    private Guid _devicePublicId;

    public async ValueTask InitializeAsync()
    {
        await CreateFixtureAsync();

        _factory = TestHost.Create();

        _client = _factory.Client();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        _deviceKey.Dispose();

        await RemoveFixtureAsync();
    }

    // ---- The case that proves the profile ----------------------------------

    [Fact]
    public async Task AcceptsARequestSignedTheWayTheMobileAppWillSignIt()
    {
        // If the server's signature base differed from the client's by one byte,
        // this would be 401 — and every genuine request in production would be
        // too. Anything other than 401 means the signature verified and the
        // request reached the handler.
        HttpResponseMessage response = await Client().SendAsync(
            HttpMethod.Post, "/api/v1/mobile/user/status", null, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        await AssertAuthenticatedAsync(response, expectedWhenConfigured: "NotClockedIn");
    }

    [Fact]
    public async Task AcceptsASignedRequestCarryingABody()
    {
        const string json = """{"position":{"latitude":6.465440,"longitude":3.406448,"accuracyMeters":3.0,"isMocked":false}}""";

        HttpResponseMessage response = await Client().SendAsync(
            HttpMethod.Post, "/api/v1/mobile/location/validate", json, TestContext.Current.CancellationToken);

        // Either outcome proves the signature and digest verified: the request
        // reached the handler. Whether the position matches an office depends on
        // what offices this database happens to hold.
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden,
            $"expected the request to be authenticated, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task CountsSignedRequestsPerDeviceNotPerNetworkAddress()
    {
        // Employees in one office reach the API from one NAT address. Counted by
        // address, the whole office would share one quota and the morning rush
        // would be refused; counted by verified device, each phone has its own.
        await using WebApplicationFactory<Program> factory = TestHost.Create(builder => builder
            .UseSetting("Api:RateLimits:ReadPerMinute", "2"));
        using HttpClient client = factory.Client();

        // Use up the address's quota with unsigned reads.
        List<HttpStatusCode> unsigned = [];
        for (int i = 0; i < 3; i++)
        {
            HttpResponseMessage response = await client.GetAsync(
                "/api/v1/mobile/app/config", TestContext.Current.CancellationToken);
            unsigned.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, unsigned[^1]);

        // The same address, but a verified device: its own quota.
        SigningHttpClient device = new(client, _deviceKey, _devicePublicId.ToString());
        List<HttpStatusCode> signed = [];
        for (int i = 0; i < 3; i++)
        {
            HttpResponseMessage response = await device.SendAsync(
                HttpMethod.Post, "/api/v1/mobile/user/status", null, TestContext.Current.CancellationToken);
            signed.Add(response.StatusCode);
        }

        Assert.NotEqual(HttpStatusCode.TooManyRequests, signed[0]);
        Assert.NotEqual(HttpStatusCode.Unauthorized, signed[0]);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, signed[1]);

        // And that quota is enforced too.
        Assert.Equal(HttpStatusCode.TooManyRequests, signed[2]);
    }

    // ---- Every way of getting it wrong -------------------------------------

    [Fact]
    public async Task RefusesAnUnsignedRequest()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/mobile/user/status");
        using HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertErrorCodeAsync(response, "UNAUTHORIZED");
    }

    [Fact]
    public async Task RefusesAReplayedNonce()
    {
        SigningHttpClient client = Client();
        client.NonceOverride = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        HttpResponseMessage first = await client.SendAsync(
            HttpMethod.Post, "/api/v1/mobile/user/status", null, TestContext.Current.CancellationToken);

        // The nonce is claimed before the handler runs, so the first request
        // consumes it whatever the business outcome turns out to be.
        Assert.NotEqual(HttpStatusCode.Unauthorized, first.StatusCode);

        // The same nonce a second time is a captured request being sent again.
        HttpResponseMessage second = await client.SendAsync(
            HttpMethod.Post, "/api/v1/mobile/user/status", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        await AssertErrorCodeAsync(second, "REPLAYED_REQUEST");
    }

    [Fact]
    public async Task RefusesAStaleSignature()
    {
        SigningHttpClient client = Client();
        client.CreatedOverride = DateTimeOffset.UtcNow.AddMinutes(-30);

        HttpResponseMessage response = await client.SendAsync(
            HttpMethod.Post, "/api/v1/mobile/user/status", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertErrorCodeAsync(response, "CLOCK_SKEW");
    }

    [Fact]
    public async Task RefusesASignatureFromTheFuture()
    {
        // Skew is checked in both directions: a client whose clock runs fast
        // would otherwise widen its own replay window.
        SigningHttpClient client = Client();
        client.CreatedOverride = DateTimeOffset.UtcNow.AddMinutes(30);

        HttpResponseMessage response = await client.SendAsync(
            HttpMethod.Post, "/api/v1/mobile/user/status", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertErrorCodeAsync(response, "CLOCK_SKEW");
    }

    [Fact]
    public async Task RefusesABodySwappedAfterTheDigestWasComputed()
    {
        const string honest = """{"position":{"latitude":6.465440,"longitude":3.406448,"accuracyMeters":3.0,"isMocked":false}}""";

        SigningHttpClient client = Client();
        client.TamperedBody = """{"position":{"latitude":0.0,"longitude":0.0,"accuracyMeters":3.0,"isMocked":false}}""";

        HttpResponseMessage response = await client.SendAsync(
            HttpMethod.Post, "/api/v1/mobile/location/validate", honest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorCodeAsync(response, "INVALID_REQUEST");
    }

    [Fact]
    public async Task RefusesASignatureCarryingAnotherApplicationsTag()
    {
        SigningHttpClient client = Client();
        client.TagOverride = "some-other-application";

        HttpResponseMessage response = await client.SendAsync(
            HttpMethod.Post, "/api/v1/mobile/user/status", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RefusesASignatureThatDoesNotCoverTheAuthority()
    {
        // Otherwise the same signature would verify against a different host.
        SigningHttpClient client = Client();
        client.ComponentsOverride = ["@method", "@path"];

        HttpResponseMessage response = await client.SendAsync(
            HttpMethod.Post, "/api/v1/mobile/user/status", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RefusesASignatureMadeWithADifferentKey()
    {
        using ECDsa impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        SigningHttpClient client = new(_client, impostor, _devicePublicId.ToString());

        HttpResponseMessage response = await client.SendAsync(
            HttpMethod.Post, "/api/v1/mobile/user/status", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RefusesARevokedDevice()
    {
        await ExecuteAsync($"""
            UPDATE core.Device
            SET Status = 2, RevokedUtc = SYSUTCDATETIME(), RevokedReason = N'API test'
            WHERE DeviceId = {_deviceId};
            """);

        try
        {
            HttpResponseMessage response = await Client().SendAsync(
                HttpMethod.Post, "/api/v1/mobile/user/status", null, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertErrorCodeAsync(response, "DEVICE_REVOKED");
        }
        finally
        {
            await ExecuteAsync($"""
                UPDATE core.Device SET Status = 1, RevokedUtc = NULL, RevokedReason = NULL
                WHERE DeviceId = {_deviceId};
                """);
        }
    }

    [Fact]
    public async Task LetsAPendingDeviceAskWhetherItHasBeenApproved()
    {
        // A pending device must be able to learn that it was approved — otherwise
        // an employee waiting for approval can never find out it happened, and
        // re-registering would create a second pending device.
        await ExecuteAsync($"UPDATE core.Device SET Status = 0 WHERE DeviceId = {_deviceId};");

        try
        {
            HttpResponseMessage response = await Client().SendAsync(
                HttpMethod.Post, "/api/v1/mobile/device/status", null, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await AssertJsonPropertyAsync(response, "status", "PendingApproval");
        }
        finally
        {
            await ExecuteAsync($"UPDATE core.Device SET Status = 1 WHERE DeviceId = {_deviceId};");
        }
    }

    [Fact]
    public async Task LetsARevokedDeviceLearnThatItWasRevoked()
    {
        await ExecuteAsync($"""
            UPDATE core.Device
            SET Status = 2, RevokedUtc = SYSUTCDATETIME(), RevokedReason = N'Handset lost'
            WHERE DeviceId = {_deviceId};
            """);

        try
        {
            HttpResponseMessage response = await Client().SendAsync(
                HttpMethod.Post, "/api/v1/mobile/device/status", null, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await AssertJsonPropertyAsync(response, "status", "Revoked");
        }
        finally
        {
            await ExecuteAsync($"""
                UPDATE core.Device SET Status = 1, RevokedUtc = NULL, RevokedReason = NULL
                WHERE DeviceId = {_deviceId};
                """);
        }
    }

    [Fact]
    public async Task StillRefusesAPendingDeviceEverywhereElse()
    {
        // The exemption is for asking about itself, and nothing more.
        await ExecuteAsync($"UPDATE core.Device SET Status = 0 WHERE DeviceId = {_deviceId};");

        try
        {
            HttpResponseMessage response = await Client().SendAsync(
                HttpMethod.Post, "/api/v1/mobile/user/status", null, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertErrorCodeAsync(response, "DEVICE_NOT_APPROVED");
        }
        finally
        {
            await ExecuteAsync($"UPDATE core.Device SET Status = 1 WHERE DeviceId = {_deviceId};");
        }
    }

    private static async Task AssertJsonPropertyAsync(HttpResponseMessage response, string property, string expected)
    {
        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using JsonDocument body = JsonDocument.Parse(content);

        Assert.Equal(expected, body.RootElement.GetProperty(property).GetString());
    }

    [Fact]
    public async Task RefusesClockInWithoutAnIdempotencyKey()
    {
        const string json = """
            {"userId":"%USER%","password":"x","authenticatorCode":"123456",
             "position":{"latitude":6.465440,"longitude":3.406448,"accuracyMeters":3.0,"isMocked":false}}
            """;

        HttpResponseMessage response = await Client().SendAsync(
            HttpMethod.Post, "/api/v1/mobile/attendance/clock-in",
            json.Replace("%USER%", UserId, StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RefusesABodyClaimingAnotherEmployee()
    {
        // §6.2 step 9: a user identifier in a body is a claim, accepted only
        // when it matches the device binding.
        const string json = """
            {"userId":"someone.else","password":"x","authenticatorCode":"123456",
             "position":{"latitude":6.465440,"longitude":3.406448,"accuracyMeters":3.0,"isMocked":false}}
            """;

        HttpResponseMessage response = await Client().SendAsync(
            HttpMethod.Post, "/api/v1/mobile/attendance/clock-in", json,
            TestContext.Current.CancellationToken, idempotencyKey: Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorCodeAsync(response, "FORBIDDEN");
    }

    [Fact]
    public async Task NeverDisclosesInternalDetailInAnErrorBody()
    {
        HttpResponseMessage response = await Client().SendAsync(
            HttpMethod.Post, "/api/v1/mobile/user/status", null,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        // And on a refusal, the body carries a code, a sentence and a
        // correlation id — nothing else (§34).
        using HttpRequestMessage unsigned = new(HttpMethod.Post, "/api/v1/mobile/user/status");
        using HttpResponseMessage refused = await _client.SendAsync(unsigned, TestContext.Current.CancellationToken);

        using JsonDocument body = JsonDocument.Parse(
            await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(4, body.RootElement.EnumerateObject().Count());
        Assert.True(body.RootElement.TryGetProperty("correlationId", out _));

        string text = body.RootElement.GetRawText();
        Assert.DoesNotContain("Exception", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("core.", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Fixture -----------------------------------------------------------

    private string UserId { get; set; } = string.Empty;

    private SigningHttpClient Client() => new(_client, _deviceKey, _devicePublicId.ToString());

    /// <summary>
    /// Asserts an authenticated request reached the handler.
    /// </summary>
    /// <remarks>
    /// Thirteen business settings are deliberately unset until the business owner
    /// decides them, and attendance refuses rather than assuming a value (§15,
    /// §68). On a database in that state the correct answer to a status request
    /// is ATTENDANCE_NOT_CONFIGURED — which is itself worth asserting, because it
    /// proves the safeguard holds all the way from the endpoint to the stored
    /// procedure. Once the settings are configured the same request returns the
    /// attendance state instead, and both are accepted here.
    /// </remarks>
    private static async Task AssertAuthenticatedAsync(
        HttpResponseMessage response,
        string expectedWhenConfigured)
    {
        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using JsonDocument body = JsonDocument.Parse(content);

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            Assert.Equal("ATTENDANCE_NOT_CONFIGURED", body.RootElement.GetProperty("code").GetString());
            return;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(expectedWhenConfigured, body.RootElement.GetProperty("state").GetString());
    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expected)
    {
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected, body.RootElement.GetProperty("code").GetString());
    }

    private async Task CreateFixtureAsync()
    {
        ECParameters parameters = _deviceKey.ExportParameters(includePrivateParameters: false);

        byte[] publicKey = new byte[65];
        publicKey[0] = 0x04;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);

        UserId = $"apitest.{Guid.NewGuid():N}";

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        FixtureRow row = await connection.QuerySingleAsync<FixtureRow>(new CommandDefinition("""
            INSERT INTO core.MobileUser (UserId, FirstName, LastName, Status)
            VALUES (@userId, N'Api', N'Test', 1);
            DECLARE @mobileUserId INT = SCOPE_IDENTITY();

            INSERT INTO core.Device (MobileUserId, PublicKey, PublicKeyThumbprint, Platform,
                                     AttestationLevel, Status, DeviceModel, ApprovedUtc)
            VALUES (@mobileUserId, @publicKey, HASHBYTES('SHA2_256', @publicKey), 1, 2, 1,
                    N'API TEST', SYSUTCDATETIME());
            DECLARE @deviceId INT = SCOPE_IDENTITY();

            SELECT @mobileUserId AS MobileUserId,
                   @deviceId     AS DeviceId,
                   (SELECT DevicePublicId FROM core.Device WHERE DeviceId = @deviceId) AS DevicePublicId;
            """,
            new { userId = UserId, publicKey },
            cancellationToken: TestContext.Current.CancellationToken));

        _mobileUserId = row.MobileUserId;
        _deviceId = row.DeviceId;
        _devicePublicId = row.DevicePublicId;
    }

    private async Task RemoveFixtureAsync()
    {
        if (_mobileUserId == 0)
        {
            return;
        }

        // Order matters: children before parents. Audit ledger rows are
        // append-only and deliberately left behind.
        await ExecuteAsync($"""
            DELETE FROM core.RequestNonce WHERE DeviceId = {_deviceId};
            DELETE FROM core.RequestIdempotency WHERE DeviceId = {_deviceId};
            DELETE FROM core.AttendanceEvent WHERE AttendanceId IN
                (SELECT AttendanceId FROM core.Attendance WHERE MobileUserId = {_mobileUserId});
            DELETE FROM core.Attendance WHERE MobileUserId = {_mobileUserId};
            DELETE FROM core.AuthenticationAttempt WHERE SubjectKey = N'{UserId}';
            DELETE FROM core.Device WHERE DeviceId = {_deviceId};
            DELETE FROM core.MobileUser WHERE MobileUserId = {_mobileUserId};
            """);
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed class FixtureRow
    {
        public int MobileUserId { get; init; }
        public int DeviceId { get; init; }
        public Guid DevicePublicId { get; init; }
    }
}
