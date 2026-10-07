using System.Net;
using System.Text;
using System.Text.Json;
using Attendance.Api.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Attendance.Api.Tests;

/// <summary>
/// Oversized bodies are refused before anything reads them (§22), including on
/// the endpoints that accept unsigned requests and so never reach the signature
/// middleware's own buffer limit.
/// </summary>
/// <remarks>
/// The test server does not implement the per-request body-size feature, so the
/// chunked-body path (no Content-Length) is exercised only by Kestrel and IIS;
/// what is proven here is the declared-length check that runs first.
/// </remarks>
public sealed class RequestSizeLimitTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = TestHost.Create();
        _client = _factory.Client();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Theory]
    [InlineData("/api/v1/mobile/device/registration/challenge")]
    [InlineData("/api/v1/mobile/location/validate")]
    [InlineData("/api/v1/mobile/attendance/clock-in")]
    public async Task RefusesABodyOverTheLimitWith413(string path)
    {
        string padding = new('x', (int)RequestSizeLimitMiddleware.MaxBodyBytes + 1);
        using StringContent body = new($"{{\"padding\":\"{padding}\"}}", Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _client.PostAsync(path, body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        // The consistent error model (§34), with a correlation id to quote.
        using JsonDocument error = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("INVALID_REQUEST", error.RootElement.GetProperty("code").GetString());
        Assert.True(response.Headers.Contains("X-Correlation-Id"));

        // Error responses keep the security headers: they are applied as the
        // response starts, so the Response.Clear() on this path cannot drop them.
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task LetsAnOrdinaryRequestThrough()
    {
        using StringContent body = new("{}", Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _client.PostAsync(
            "/api/v1/mobile/device/registration/challenge", body, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
