using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Services;
using Attendance.Domain.ValueObjects;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IOfficeLocationRepository"/>, with a short
/// in-process cache.
/// </summary>
/// <remarks>
/// <para>
/// Office locations change rarely and are read on every location check, so they
/// are cached briefly. <b>Device revocation is never cached</b> for the opposite
/// reason: a revoked device must stop working immediately (§18). The difference
/// is deliberate — an office moved a moment ago and still accepted for a few more
/// seconds is an administrative inconvenience; a revoked handset that still
/// records attendance is a security failure.
/// </para>
/// <para>
/// The cache is invalidated explicitly when an administrator changes a location,
/// so the window only matters for changes made on another application server.
/// </para>
/// <para>
/// <b>No geometry is computed here.</b> The distance calculation lives in
/// <see cref="GeoDistance"/> so there is exactly one implementation, testable
/// against the boundary cases §51 requires.
/// </para>
/// </remarks>
public sealed class OfficeLocationRepository : IOfficeLocationRepository, IDisposable
{
    private const string GetActiveProcedure = "mobile.usp_OfficeLocation_GetActive";

    /// <summary>
    /// How long a cached list is reused. Short enough that an administrative
    /// change appears promptly, long enough to keep the read off the hot path.
    /// </summary>
    private const int CacheLifetimeMilliseconds = 30_000;

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private IReadOnlyList<OfficeLocationCandidate>? _cached;
    private long _cacheExpiresAt;

    /// <summary>Creates the repository.</summary>
    public OfficeLocationRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OfficeLocationCandidate>> GetActiveAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } current && Environment.TickCount64 < Volatile.Read(ref _cacheExpiresAt))
        {
            return current;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Another caller may have refreshed it while this one waited.
            if (_cached is { } refreshed && Environment.TickCount64 < Volatile.Read(ref _cacheExpiresAt))
            {
                return refreshed;
            }

            IReadOnlyList<OfficeLocationCandidate> loaded =
                await LoadAsync(cancellationToken).ConfigureAwait(false);

            _cached = loaded;
            Volatile.Write(ref _cacheExpiresAt, Environment.TickCount64 + CacheLifetimeMilliseconds);

            return loaded;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public void InvalidateCache()
    {
        Volatile.Write(ref _cacheExpiresAt, 0L);
        _cached = null;
    }

    /// <summary>Releases the refresh gate.</summary>
    /// <remarks>
    /// Registered as a singleton, so in practice this runs at shutdown. It exists
    /// because the class genuinely owns a disposable, and leaving that untracked
    /// is how handle leaks start in code that is later moved to a scoped lifetime.
    /// </remarks>
    public void Dispose() => _refreshGate.Dispose();

    private async Task<IReadOnlyList<OfficeLocationCandidate>> LoadAsync(CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<OfficeRow> rows = await lease.Connection
            .QueryAsync<OfficeRow>(lease.StoredProcedure(
                GetActiveProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        // An empty list is a valid answer — no offices are configured — and the
        // domain reports NoActiveOfficeLocations rather than accepting anything.
        return rows
            .Select(row => new OfficeLocationCandidate(
                row.OfficeLocationId,
                Coordinates.Create((double)row.Latitude, (double)row.Longitude),
                (double)row.AllowedRadiusMeters))
            .ToList();
    }

    /// <summary>Shape of the row returned by <c>mobile.usp_OfficeLocation_GetActive</c>.</summary>
    private sealed class OfficeRow
    {
        public int OfficeLocationId { get; init; }
        public decimal Latitude { get; init; }
        public decimal Longitude { get; init; }
        public decimal AllowedRadiusMeters { get; init; }
    }
}
