using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IMobileConfigurationRepository"/>.
/// </summary>
/// <remarks>
/// The procedure returns only rows flagged <c>IsMobileVisible</c>, and this class
/// adds no filtering of its own. There is exactly one place that decides what an
/// untrusted device may know; a second filter here would be another thing to keep
/// in step, and the one that fell behind would be the one that leaked.
/// </remarks>
public sealed class MobileConfigurationRepository : IMobileConfigurationRepository
{
    private const string GetRuntimeProcedure = "mobile.usp_ApplicationSetting_GetMobileRuntime";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public MobileConfigurationRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<MobileRuntimeConfiguration> GetRuntimeConfigurationAsync(CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await using SqlMapper.GridReader reader = await lease.Connection
            .QueryMultipleAsync(lease.StoredProcedure(
                GetRuntimeProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        List<MobileSetting> settings = (await reader.ReadAsync<SettingRow>().ConfigureAwait(false))
            .Select(row => new MobileSetting(row.SettingKey, row.SettingValue, row.DataType))
            .ToList();

        ConfiguredRow configured = await reader.ReadSingleAsync<ConfiguredRow>().ConfigureAwait(false);

        return new MobileRuntimeConfiguration(
            settings,
            configured.ClockInConfigured,
            configured.ClockOutConfigured,
            configured.ServerTimeUtc.ToUtcOffset());
    }

    private sealed class SettingRow
    {
        public string SettingKey { get; init; } = string.Empty;
        public string? SettingValue { get; init; }
        public string DataType { get; init; } = string.Empty;
    }

    private sealed class ConfiguredRow
    {
        public bool ClockInConfigured { get; init; }
        public bool ClockOutConfigured { get; init; }
        public DateTime ServerTimeUtc { get; init; }
    }
}
