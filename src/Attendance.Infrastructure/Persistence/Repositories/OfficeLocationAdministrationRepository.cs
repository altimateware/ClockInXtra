using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IOfficeLocationAdministrationRepository"/>.
/// </summary>
/// <remarks>
/// <b>A change here is not visible to the API immediately.</b> The mobile API
/// caches active offices for thirty seconds in its own process, and this portal
/// runs in a different one, so a new or disabled office takes effect within that
/// window rather than instantly. That is acceptable for office configuration and
/// deliberately not how device revocation behaves — an office moved a moment ago
/// and still accepted briefly is an inconvenience; a revoked handset still
/// recording attendance is a security failure.
/// </remarks>
public sealed class OfficeLocationAdministrationRepository : IOfficeLocationAdministrationRepository
{
    private const string GetAllProcedure = "admin.usp_OfficeLocation_GetAll";
    private const string CreateProcedure = "admin.usp_OfficeLocation_Create";
    private const string UpdateProcedure = "admin.usp_OfficeLocation_Update";
    private const string SetStatusProcedure = "admin.usp_OfficeLocation_SetStatus";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public OfficeLocationAdministrationRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OfficeLocationDetail>> GetAllAsync(
        int statisticsDays,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@IncludeDisabled", true, DbType.Boolean);
        parameters.Add("@StatisticsDays", statisticsDays, DbType.Int32);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<OfficeRow> rows = await lease.Connection
            .QueryAsync<OfficeRow>(lease.StoredProcedure(
                GetAllProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new OfficeLocationDetail(
                row.OfficeLocationId,
                row.Name,
                row.Description,
                row.Latitude,
                row.Longitude,
                row.AllowedRadiusMeters,
                row.Status == 1,
                row.EventCount ?? 0,
                row.MedianAccuracyMeters,
                row.WorstAccuracyMeters,
                row.MaxDistanceMeters,
                row.RowVersion)),
        ];
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> CreateAsync(
        OfficeLocationInput input,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = BuildInput(input);
        parameters.Add("@Status", input.IsActive ? (byte)1 : (byte)0, DbType.Byte);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add("@OfficeLocationId", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@OfficeLocationPublicId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        return await ExecuteAsync(CreateProcedure, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> UpdateAsync(
        int officeLocationId,
        OfficeLocationInput input,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = BuildInput(input);
        parameters.Add("@OfficeLocationId", officeLocationId, DbType.Int32);
        parameters.Add("@RowVersion", rowVersion, DbType.Binary, size: 8);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        return await ExecuteAsync(UpdateProcedure, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> SetStatusAsync(
        int officeLocationId,
        bool isActive,
        byte[] rowVersion,
        string? reason,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@OfficeLocationId", officeLocationId, DbType.Int32);
        parameters.Add("@Status", isActive ? (byte)1 : (byte)0, DbType.Byte);
        parameters.Add("@RowVersion", rowVersion, DbType.Binary, size: 8);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@Reason", reason, DbType.String, size: 256);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        return await ExecuteAsync(SetStatusProcedure, parameters, cancellationToken).ConfigureAwait(false);
    }

    private static DynamicParameters BuildInput(OfficeLocationInput input)
    {
        DynamicParameters parameters = new();

        parameters.Add("@Name", input.Name, DbType.String, size: 120);
        parameters.Add("@Description", input.Description, DbType.String, size: 400);

        // decimal(9,6) for coordinates: about 0.1 m of resolution, far finer than
        // any phone reports. decimal(6,2) for the radius.
        parameters.Add("@Latitude", input.Latitude, DbType.Decimal, precision: 9, scale: 6);
        parameters.Add("@Longitude", input.Longitude, DbType.Decimal, precision: 9, scale: 6);
        parameters.Add("@AllowedRadiusMeters", input.AllowedRadiusMeters, DbType.Decimal, precision: 6, scale: 2);

        return parameters;
    }

    private async Task<AttendanceResultCode> ExecuteAsync(
        string procedure,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                procedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? value = parameters.Get<int?>(ResultCodeParameter);

        return value is null ? AttendanceResultCode.InternalError : (AttendanceResultCode)value.Value;
    }

    private sealed class OfficeRow
    {
        public int OfficeLocationId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public decimal Latitude { get; init; }
        public decimal Longitude { get; init; }
        public decimal AllowedRadiusMeters { get; init; }
        public byte Status { get; init; }
        public int? EventCount { get; init; }
        public decimal? MedianAccuracyMeters { get; init; }
        public decimal? WorstAccuracyMeters { get; init; }
        public decimal? MaxDistanceMeters { get; init; }
        public byte[] RowVersion { get; init; } = [];
    }
}
