using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Attendance.Api.Tests;

/// <summary>
/// The per-endpoint rate limits actually limit (§36).
/// </summary>
/// <remarks>
/// Written in Phase 23, when review found the limiter registered before routing:
/// endpoint policies are read from the matched endpoint, so before routing there
/// was none and every <c>[EnableRateLimiting]</c> attribute was inert. Nothing had
/// ever observed a 429.
/// </remarks>
public sealed class RateLimitTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = TestHost.Create(builder => builder
            .UseSetting("Api:RateLimits:RegistrationPerMinute", "3"));
        _client = _factory.Client();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task RegistrationChallengesBeyondTheLimitAreRefusedWith429()
    {
        List<HttpStatusCode> statuses = [];

        for (int i = 0; i < 6; i++)
        {
            HttpResponseMessage response = await _client.PostAsync(
                "/api/v1/mobile/device/registration/challenge", content: null, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(3, statuses.Count(status => status != HttpStatusCode.TooManyRequests));
        Assert.Equal(3, statuses.Count(status => status == HttpStatusCode.TooManyRequests));
    }
}
