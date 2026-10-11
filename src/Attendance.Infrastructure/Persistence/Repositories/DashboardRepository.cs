using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IDashboardRepository"/>.
/// </summary>
/// <remarks>
/// One procedure returning one row, rather than a dozen queries to draw one
/// screen. The landing page is the most frequently loaded page in the portal
/// and the least important one to be fast at the expense of everything else.
/// </remarks>
public sealed class DashboardRepository : IDashboardRepository
{
    private const string SummaryProcedure = "admin.usp_Dashboard_GetSummary";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public DashboardRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        SummaryRow row = await lease.Connection
            .QuerySingleAsync<SummaryRow>(lease.StoredProcedure(
                SummaryProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return new DashboardSummary(
            row.DevicesAwaitingApproval,
            row.ActiveDevices,
            row.SettingsAwaitingConfirmation,
            row.CorrectionsAwaitingApproval,
            row.ActiveEmployees,
            row.EmployeesNotReady,
            row.ActiveOfficeLocations,
            row.ClockedInNow,
            row.CompletedToday,
            row.LateToday,
            row.MissingClockOuts,
            row.LocationRefusals,
            row.DeviceRefusals,
            row.CriticalEvents);
    }

    private sealed class SummaryRow
    {
        public int DevicesAwaitingApproval { get; init; }
        public int ActiveDevices { get; init; }
        public int SettingsAwaitingConfirmation { get; init; }
        public int CorrectionsAwaitingApproval { get; init; }
        public int ActiveEmployees { get; init; }
        public int EmployeesNotReady { get; init; }
        public int ActiveOfficeLocations { get; init; }
        public int ClockedInNow { get; init; }
        public int CompletedToday { get; init; }
        public int LateToday { get; init; }
        public int MissingClockOuts { get; init; }
        public int LocationRefusals { get; init; }
        public int DeviceRefusals { get; init; }
        public int CriticalEvents { get; init; }
    }
}
