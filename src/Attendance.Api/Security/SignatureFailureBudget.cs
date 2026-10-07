using Microsoft.Extensions.Caching.Memory;

namespace Attendance.Api.Security;

/// <summary>
/// How many signature failures one network address may produce before its
/// refusals stop being processed individually.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Signature verification runs before the rate limiter,
/// deliberately: a signed request must be counted against its verified device
/// rather than its network address, because an office shares one address and a
/// per-address limit would refuse the morning rush. The consequence is that a
/// request whose signature never verifies is refused <i>before</i> reaching any
/// limiter — and each refusal costs a device lookup and writes a row to
/// <c>audit.SecurityEvent</c>, which is an append-only ledger that is never
/// purged (DB-08). An unauthenticated caller could therefore make the audit
/// ledger grow without bound, cheaply, and bury real security events under the
/// noise (found in the Phase 24 review).
/// </para>
/// <para>
/// <b>Why it is keyed on failures rather than requests.</b> A guard that counted
/// every request from an address would re-introduce exactly the NAT problem that
/// per-device limiting solved. Legitimate traffic verifies, so it never spends
/// from this budget; only failures do.
/// </para>
/// <para>
/// <b>What it does not do.</b> It is not a brute-force control — signatures are
/// ECDSA over P-256 and are not guessable — and it is not authoritative across a
/// farm, because it counts per process (as the endpoint limiter does, TD-10).
/// It bounds ledger growth and wasted work from one address to
/// <c>limit + 1</c> rows a minute. A caller with many addresses still costs
/// that much per address, which is why an internet-facing deployment needs a
/// network-level control in front of it as well.
/// </para>
/// </remarks>
public interface ISignatureFailureBudget
{
    /// <summary>True when this address has spent its budget for the current window.</summary>
    bool IsExhausted(string address);

    /// <summary>
    /// Records one signature failure.
    /// </summary>
    /// <returns>
    /// True if this failure is the one that exhausted the budget, so the caller
    /// can record that fact once rather than on every subsequent request.
    /// </returns>
    bool RecordFailure(string address);
}

/// <summary>
/// In-memory implementation, one fixed window per address.
/// </summary>
/// <remarks>
/// <para>
/// The cache is this guard's own and carries a size limit, because the thing it
/// defends against is growth: a counter keyed by attacker-chosen addresses must
/// not become the memory leak it was written to prevent. When the limit is
/// reached the cache evicts, which loses counts rather than refusing requests.
/// </para>
/// <para>
/// Increments are interlocked, but creating a window is not, so two threads
/// racing on an address's first failure can lose one count. That is acceptable
/// in a guard whose purpose is to bound growth, and it errs towards letting a
/// request through rather than refusing one.
/// </para>
/// </remarks>
public sealed class SignatureFailureBudget : ISignatureFailureBudget, IDisposable
{
    private readonly MemoryCache _cache;
    private readonly int _limit;
    private readonly TimeSpan _window;

    /// <summary>Creates the budget.</summary>
    /// <param name="failuresPerWindow">Failures allowed per address per window.</param>
    /// <param name="window">The window length.</param>
    /// <param name="maxAddresses">How many addresses are tracked at once.</param>
    public SignatureFailureBudget(int failuresPerWindow, TimeSpan window, int maxAddresses = 50_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failuresPerWindow, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAddresses, 1);

        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxAddresses });
        _limit = failuresPerWindow;
        _window = window;
    }

    /// <summary>Releases the cache.</summary>
    public void Dispose() => _cache.Dispose();

    /// <inheritdoc />
    public bool IsExhausted(string address) =>
        _cache.TryGetValue(Key(address), out Window? window) && window is not null && window.Count >= _limit;

    /// <inheritdoc />
    public bool RecordFailure(string address)
    {
        Window window = _cache.GetOrCreate(Key(address), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _window;
            entry.Size = 1;
            return new Window();
        })!;

        return window.Increment() == _limit;
    }

    private static string Key(string address) => "sigfail:" + address;

    private sealed class Window
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public int Increment() => Interlocked.Increment(ref _count);
    }
}
