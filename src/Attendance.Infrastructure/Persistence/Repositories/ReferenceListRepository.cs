using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IReferenceListRepository"/>.
/// </summary>
public sealed class ReferenceListRepository : IReferenceListRepository
{
    private const string GetAllProcedure = "admin.usp_ReferenceList_GetAll";
    private const string SaveProcedure = "admin.usp_ReferenceList_Save";
    private const string SetStatusProcedure = "admin.usp_ReferenceList_SetStatus";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public ReferenceListRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReferenceListEntry>> GetAllAsync(ReferenceList list, CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@List", ListName(list), DbType.AnsiString, size: 16);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<EntryRow> rows = await lease.Connection
            .QueryAsync<EntryRow>(lease.StoredProcedure(
                GetAllProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new ReferenceListEntry(
                row.EntryId, row.Name, row.IsActive, row.EmployeeCount, row.RowVersion)),
        ];
    }

    /// <inheritdoc />
    public Task<AttendanceResultCode> AddAsync(
        ReferenceList list,
        string name,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SaveAsync(list, null, name, null, administratorId, correlationId, cancellationToken);

    /// <inheritdoc />
    public Task<AttendanceResultCode> RenameAsync(
        ReferenceList list,
        int entryId,
        string name,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SaveAsync(list, entryId, name, rowVersion, administratorId, correlationId, cancellationToken);

    /// <inheritdoc />
    public async Task<AttendanceResultCode> SetActiveAsync(
        ReferenceList list,
        int entryId,
        bool isActive,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@List", ListName(list), DbType.AnsiString, size: 16);
        parameters.Add("@EntryId", entryId, DbType.Int32);
        parameters.Add("@IsActive", isActive, DbType.Boolean);
        parameters.Add("@RowVersion", rowVersion, DbType.Binary, size: 8);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        return await ExecuteAsync(SetStatusProcedure, parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AttendanceResultCode> SaveAsync(
        ReferenceList list,
        int? entryId,
        string name,
        byte[]? rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        DynamicParameters parameters = new();
        parameters.Add("@List", ListName(list), DbType.AnsiString, size: 16);
        parameters.Add("@EntryId", entryId, DbType.Int32);

        // Sized above the column so an over-long name reaches the procedure and
        // is refused there, rather than being silently cut to fit.
        parameters.Add("@Name", name, DbType.String, size: 200);
        parameters.Add("@RowVersion", rowVersion, DbType.Binary, size: 8);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add("@SavedEntryId", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        return await ExecuteAsync(SaveProcedure, parameters, cancellationToken).ConfigureAwait(false);
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

        int? code = parameters.Get<int?>(ResultCodeParameter);
        return code is null ? AttendanceResultCode.InternalError : (AttendanceResultCode)code.Value;
    }

    private static string ListName(ReferenceList list) =>
        list switch
        {
            ReferenceList.Department => "Department",
            ReferenceList.JobTitle => "JobTitle",
            _ => throw new ArgumentOutOfRangeException(nameof(list), list, "Unknown reference list."),
        };

    private sealed class EntryRow
    {
        public int EntryId { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public int EmployeeCount { get; init; }
        public byte[] RowVersion { get; init; } = [];
    }
}
