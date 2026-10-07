using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IAttendanceCorrectionRepository"/>.
/// </summary>
public sealed class AttendanceCorrectionRepository : IAttendanceCorrectionRepository
{
    private const string GetForCorrectionProcedure = "admin.usp_Attendance_GetForCorrection";
    private const string RequestProcedure = "admin.usp_Attendance_RequestCorrection";
    private const string DecideProcedure = "admin.usp_Attendance_ApproveCorrection";
    private const string SearchProcedure = "admin.usp_AttendanceCorrection_Search";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public AttendanceCorrectionRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<AttendanceForCorrection?> GetForCorrectionAsync(
        Guid attendancePublicId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@AttendancePublicId", attendancePublicId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        RecordRow? row = await lease.Connection
            .QuerySingleOrDefaultAsync<RecordRow>(lease.StoredProcedure(
                GetForCorrectionProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return row is null
            ? null
            : new AttendanceForCorrection(
                row.AttendancePublicId,
                DateOnly.FromDateTime(row.AttendanceDate),
                row.UserId,
                $"{row.FirstName} {row.LastName}".Trim(),
                (AttendanceRecordStatus)row.Status,
                row.ClockInLocal,
                row.ClockOutLocal,
                row.HasPendingCorrection);
    }

    /// <inheritdoc />
    public async Task<CorrectionRequestResult> RequestAsync(
        Guid attendancePublicId,
        DateTime? correctedClockInLocal,
        DateTime? correctedClockOutLocal,
        string reason,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@AttendancePublicId", attendancePublicId, DbType.Guid);
        parameters.Add("@CorrectedClockInLocal", correctedClockInLocal, DbType.DateTime2);
        parameters.Add("@CorrectedClockOutLocal", correctedClockOutLocal, DbType.DateTime2);
        parameters.Add("@Reason", reason, DbType.String, size: 512);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add("@AttendanceCorrectionId", dbType: DbType.Int64, direction: ParameterDirection.Output);
        parameters.Add("@Applied", dbType: DbType.Boolean, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                RequestProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return new CorrectionRequestResult(
            ResultOf(parameters),
            parameters.Get<bool?>("@Applied") ?? false);
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> DecideAsync(
        long attendanceCorrectionId,
        bool approve,
        string? decisionNote,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@AttendanceCorrectionId", attendanceCorrectionId, DbType.Int64);
        parameters.Add("@Approve", approve, DbType.Boolean);
        parameters.Add("@DecisionNote", string.IsNullOrWhiteSpace(decisionNote) ? null : decisionNote.Trim(), DbType.String, size: 512);
        parameters.Add("@RowVersion", rowVersion, DbType.Binary, size: 8);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                DecideProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return ResultOf(parameters);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttendanceCorrectionSummary>> SearchAsync(
        AttendanceCorrectionStatus? status,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@Status", (byte?)status, DbType.Byte);
        parameters.Add("@PageSize", 200, DbType.Int32);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<SummaryRow> rows = await lease.Connection
            .QueryAsync<SummaryRow>(lease.StoredProcedure(
                SearchProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new AttendanceCorrectionSummary(
                row.AttendanceCorrectionId,
                row.AttendancePublicId,
                DateOnly.FromDateTime(row.AttendanceDate),
                row.UserId,
                $"{row.FirstName} {row.LastName}".Trim(),
                row.OriginalClockInLocal,
                row.OriginalClockOutLocal,
                row.CorrectedClockInLocal,
                row.CorrectedClockOutLocal,
                row.Reason,
                (AttendanceCorrectionStatus)row.Status,
                row.RequestedByAdministratorId,
                row.RequestedByName,
                row.RequestedUtc.ToUtcOffset(),
                row.DecidedByName,
                row.DecidedUtc.ToUtcOffset(),
                row.DecisionNote,
                row.RowVersion)),
        ];
    }

    private static AttendanceResultCode ResultOf(DynamicParameters parameters)
    {
        int? value = parameters.Get<int?>(ResultCodeParameter);
        return value is null ? AttendanceResultCode.InternalError : (AttendanceResultCode)value.Value;
    }

    private sealed class RecordRow
    {
        public Guid AttendancePublicId { get; init; }
        public DateTime AttendanceDate { get; init; }
        public string UserId { get; init; } = string.Empty;
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public byte Status { get; init; }
        public DateTime ClockInLocal { get; init; }
        public DateTime? ClockOutLocal { get; init; }
        public bool HasPendingCorrection { get; init; }
    }

    private sealed class SummaryRow
    {
        public long AttendanceCorrectionId { get; init; }
        public Guid AttendancePublicId { get; init; }
        public DateTime AttendanceDate { get; init; }
        public string UserId { get; init; } = string.Empty;
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public DateTime? OriginalClockInLocal { get; init; }
        public DateTime? OriginalClockOutLocal { get; init; }
        public DateTime? CorrectedClockInLocal { get; init; }
        public DateTime? CorrectedClockOutLocal { get; init; }
        public string Reason { get; init; } = string.Empty;
        public byte Status { get; init; }
        public int RequestedByAdministratorId { get; init; }
        public string RequestedByName { get; init; } = string.Empty;
        public DateTime RequestedUtc { get; init; }
        public string? DecidedByName { get; init; }
        public DateTime? DecidedUtc { get; init; }
        public string? DecisionNote { get; init; }
        public byte[] RowVersion { get; init; } = [];
    }
}
