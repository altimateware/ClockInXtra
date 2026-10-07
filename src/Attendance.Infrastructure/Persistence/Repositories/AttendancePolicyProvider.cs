using System.Data;
using System.Globalization;
using Attendance.Application.Abstractions;
using Attendance.Domain.Services;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IAttendancePolicyProvider"/>, with a short
/// in-process cache.
/// </summary>
/// <remarks>
/// <para>
/// Reads <c>mobile.usp_ApplicationSetting_GetSecurityPolicy</c>, which returns an
/// explicit allow-list of the settings the server needs to enforce policy. These
/// are deliberately not the settings the mobile client receives: an attacker who
/// learns the lockout threshold knows how many guesses are free.
/// </para>
/// <para>
/// <b>Every fallback here is the restrictive one.</b> If a row is missing, or its
/// value cannot be parsed, the provider uses the safer value rather than the
/// permissive one — attestation required, mocked locations rejected, error codes
/// collapsed. A configuration problem should make the system stricter than
/// intended, never more permissive, because the permissive failure is silent and
/// the strict one gets reported within minutes.
/// </para>
/// <para>
/// <b>A NULL value is not a missing row.</b> Two settings are legitimately unset:
/// the maximum accepted accuracy (when unset, the matched office's own radius is
/// used, which is stricter) and the minimum app version (unset until the first
/// release). Those are carried through as <see langword="null"/> and interpreted
/// by the domain, not replaced with an invented default (§15, §68).
/// </para>
/// <para>
/// The <em>attendance business rules</em> — timezone, window times, window
/// actions — are deliberately absent. Those are read inside the stored
/// procedure's own transaction (decision DB-05), so the rule applied and the
/// record written cannot disagree.
/// </para>
/// </remarks>
public sealed class AttendancePolicyProvider : IAttendancePolicyProvider, IDisposable
{
    private const string GetPolicyProcedure = "mobile.usp_ApplicationSetting_GetSecurityPolicy";

    /// <summary>
    /// How long a policy snapshot is reused. A setting change takes effect within
    /// this window, which is acceptable for thresholds — and is why device
    /// revocation is never served from a cache.
    /// </summary>
    private const int CacheLifetimeMilliseconds = 30_000;

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private AttendancePolicy? _cached;
    private long _cacheExpiresAt;

    /// <summary>Creates the provider.</summary>
    public AttendancePolicyProvider(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<AttendancePolicy> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } current && Environment.TickCount64 < Volatile.Read(ref _cacheExpiresAt))
        {
            return current;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_cached is { } refreshed && Environment.TickCount64 < Volatile.Read(ref _cacheExpiresAt))
            {
                return refreshed;
            }

            AttendancePolicy loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);

            _cached = loaded;
            Volatile.Write(ref _cacheExpiresAt, Environment.TickCount64 + CacheLifetimeMilliseconds);

            return loaded;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Discards the cached snapshot, so the next read reloads it.</summary>
    public void InvalidateCache()
    {
        Volatile.Write(ref _cacheExpiresAt, 0L);
        _cached = null;
    }

    /// <summary>Releases the refresh gate.</summary>
    public void Dispose() => _refreshGate.Dispose();

    private async Task<AttendancePolicy> LoadAsync(CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<SettingRow> rows = await lease.Connection
            .QueryAsync<SettingRow>(lease.StoredProcedure(
                GetPolicyProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        Dictionary<string, string?> settings = rows.ToDictionary(
            row => row.SettingKey,
            row => row.SettingValue,
            StringComparer.OrdinalIgnoreCase);

        return new AttendancePolicy(
            ReadAccuracyPolicy(settings),
            ReadOptionalDouble(settings, "Location.MaxAcceptedAccuracyMeters"),
            ReadBool(settings, "Location.RejectMockedLocations", fallback: true),
            ReadInt(settings, "Security.MobileLockoutThreshold", fallback: 5),
            ReadInt(settings, "Security.MobileLockoutMinutes", fallback: 15),
            ReadInt(settings, "Security.SignatureSkewSeconds", fallback: 120),
            ReadBool(settings, "Security.CollapseCredentialErrorCodes", fallback: true),
            ReadBool(settings, "Security.RequireHardwareAttestationAndroid", fallback: true),
            ReadBool(settings, "Security.RequireHardwareAttestationIos", fallback: true),
            ReadBool(settings, "Security.DeviceRegistrationRequiresApproval", fallback: true),
            ReadInt(settings, "Security.ChallengeLifetimeSeconds", fallback: 300),
            ReadOptionalString(settings, "Mobile.MinimumAppVersion"),
            ReadInt(settings, "Security.TotpStepTolerance", fallback: 1));
    }

    private static LocationAccuracyPolicy ReadAccuracyPolicy(Dictionary<string, string?> settings) =>
        settings.TryGetValue("Location.AccuracyPolicy", out string? value)
        && Enum.TryParse(value, ignoreCase: true, out LocationAccuracyPolicy parsed)
            ? parsed
            // The stricter of the two: accuracy is considered as well as distance.
            : LocationAccuracyPolicy.DistanceAndAccuracyThreshold;

    private static bool ReadBool(Dictionary<string, string?> settings, string key, bool fallback) =>
        settings.TryGetValue(key, out string? value) && bool.TryParse(value, out bool parsed)
            ? parsed
            : fallback;

    private static int ReadInt(Dictionary<string, string?> settings, string key, int fallback) =>
        settings.TryGetValue(key, out string? value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;

    /// <summary>
    /// Reads a value that is legitimately allowed to be unset.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> means the business has not decided. It is carried
    /// through rather than replaced, because the domain's handling of "unset" is
    /// stricter than any value this class could invent.
    /// </remarks>
    private static double? ReadOptionalDouble(Dictionary<string, string?> settings, string key) =>
        settings.TryGetValue(key, out string? value)
        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;

    private static string? ReadOptionalString(Dictionary<string, string?> settings, string key) =>
        settings.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private sealed class SettingRow
    {
        public string SettingKey { get; init; } = string.Empty;
        public string? SettingValue { get; init; }
    }
}
