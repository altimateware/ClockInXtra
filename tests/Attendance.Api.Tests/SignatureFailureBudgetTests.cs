using System.Net;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Attendance.Api.Tests;

/// <summary>
/// A caller whose signatures never verify is cut off, and stops writing to the
/// audit ledger.
/// </summary>
/// <remarks>
/// <para>
/// Signature verification runs before the rate limiter on purpose — a signed
/// request must be counted against its verified device, not the office address
/// it shares with everyone else. The cost, found in the Phase 24 review, is that
/// a request that never verifies never reaches a limiter, while every refusal
/// writes a row to an append-only ledger that is never purged. These tests hold
/// the guard in place: refusals are bounded per address, and legitimate traffic
/// (which verifies) never spends from the budget.
/// </para>
/// <para>
/// <b>Each run counts only its own rows.</b> Every request carries one
/// correlation id, unique to the test, and the ledger is counted by it. Counting
/// every row in the table — as this test once did — made the result depend on
/// whatever other test classes, running in parallel, happened to refuse in the
/// same moment; the slower the machine, the wider that overlap.
/// </para>
/// </remarks>
public sealed class SignatureFailureBudgetTests : IAsyncLifetime
{
    private const int Budget = 5;

    private readonly Guid _run = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        // TestHost lengthens the budget's window to an hour, so a slow run cannot
        // cross a window boundary and be granted a second budget midway.
        _factory = TestHost.Create(builder => builder
            .UseSetting("Api:Abuse:SignatureFailuresPerAddressPerMinute", Budget.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        _client = _factory.Client();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task UnverifiableRequestsAreCutOffAndStopWritingToTheLedger()
    {
        List<HttpStatusCode> statuses = [];

        for (int i = 0; i < Budget * 3; i++)
        {
            statuses.Add((await SendGarbageAsync()).StatusCode);
        }

        // The first few are refused as unauthorised, one at a time; the rest are
        // refused as rate limited, without a database write.
        Assert.Equal(Budget, statuses.Count(status => status == HttpStatusCode.Unauthorized));
        Assert.Equal(Budget * 2, statuses.Count(status => status == HttpStatusCode.TooManyRequests));

        // One row per refusal that was processed, plus the single row recording
        // that the budget was spent. Never one per request.
        Assert.Equal(Budget + 1, await RowsWrittenAsync(eventType: null));
    }

    [Fact]
    public async Task TheBudgetRecordsThatItWasExhausted()
    {
        for (int i = 0; i < Budget + 2; i++)
        {
            await SendGarbageAsync();
        }

        // Exactly once, however many requests followed.
        Assert.Equal(1, await RowsWrittenAsync("Signature.FailureBudgetExhausted"));
    }

    private async Task<HttpResponseMessage> SendGarbageAsync()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/mobile/user/status")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };

        // Well-formed enough to be taken seriously, invalid enough to be refused.
        request.Headers.TryAddWithoutValidation(
            "Signature-Input",
            """sig1=("@method" "@target-uri" "content-digest");created=1;keyid="unknown";alg="ecdsa-p256-sha256";nonce="AAAAAAAAAAAAAAAAAAAAAA==";tag="clockinxtra" """.Trim());
        request.Headers.TryAddWithoutValidation("Signature", "sig1=:AAAA:");
        request.Headers.TryAddWithoutValidation("Content-Digest", "sha-256=:AAAA:");

        // The API adopts a caller's correlation id, and records it on every
        // security event the request causes: this is how the test finds its own.
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", _run.ToString());

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<long> RowsWrittenAsync(string? eventType)
    {
        await using SqlConnection connection = new(TestHost.ConnectionString);

        return await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT_BIG(*) FROM audit.SecurityEvent
            WHERE CorrelationId = @run
              AND (@eventType IS NULL OR EventType = @eventType)
            """,
            new { run = _run, eventType });
    }
}
