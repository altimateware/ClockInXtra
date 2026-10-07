using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IAttendanceRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every call is <see cref="CommandType.StoredProcedure"/> with typed
/// parameters. There is no SQL text in this file beyond procedure names, no
/// string concatenation and no dynamic SQL (Claude.md §4, §49). The procedure
/// names are constants so a typo fails at one obvious place rather than in a
/// string buried in a method.
/// </para>
/// <para>
/// Parameters declare their <see cref="DbType"/> and size explicitly. That is
/// not ceremony: an inferred <c>nvarchar(4000)</c> where the column is
/// <c>nvarchar(64)</c> produces a different execution plan and can prevent index
/// seeks, and being explicit is also what makes adopting Always Encrypted
/// possible later without rewriting every call.
/// </para>
/// <para>
/// Commands are issued through a <see cref="SqlConnectionLease"/>, which carries
/// any ambient transaction. That is what lets several repository calls be
/// composed into one unit of work — and the stored procedures are savepoint-aware
/// (decision DB-13) precisely so that a business rejection inside such a
/// composition does not discard the caller's work.
/// </para>
/// <para>
/// No business rules live here. The attendance day, the window rules, the
/// duplicate check and the authoritative timestamps are all decided inside the
/// stored procedure's transaction, because those guarantees must hold across
/// several application servers (§28, §45).
/// </para>
/// </remarks>
public sealed class AttendanceRepository : IAttendanceRepository
{
    private const string GetCurrentStatusProcedure = "mobile.usp_Attendance_GetCurrentStatus";
    private const string ClockInProcedure = "mobile.usp_Attendance_ClockIn";
    private const string ClockOutProcedure = "mobile.usp_Attendance_ClockOut";

    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public AttendanceRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<AttendanceStatus> GetCurrentStatusAsync(
        int mobileUserId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@MobileUserId", mobileUserId, DbType.Int32);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        StatusRow? row = await lease.Connection
            .QuerySingleOrDefaultAsync<StatusRow>(
                lease.StoredProcedure(
                    GetCurrentStatusProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        AttendanceResultCode resultCode = ReadResultCode(parameters);

        // A refusal (for example ATTENDANCE_NOT_CONFIGURED) returns no row at
        // all, so the caller gets the code and nothing that could be mistaken
        // for real attendance data.
        if (resultCode != AttendanceResultCode.Success || row is null)
        {
            return new AttendanceStatus(
                resultCode,
                AttendanceState.NotClockedIn,
                AttendanceDate: null,
                ServerTimeUtc: default,
                ClockInUtc: null,
                ClockOutUtc: null,
                DurationMinutes: null,
                ClockInCloseTime: null,
                ClockOutOpenTime: null);
        }

        return new AttendanceStatus(
            resultCode,
            (AttendanceState)row.AttendanceState,
            DateOnly.FromDateTime(row.AttendanceDate),
            row.ServerTimeUtc.ToUtcOffset(),
            row.ClockInUtc.ToUtcOffset(),
            row.ClockOutUtc.ToUtcOffset(),
            row.DurationMinutes,
            row.ClockInCloseTime.ToTimeOnly(),
            row.ClockOutOpenTime.ToTimeOnly());
    }

    /// <inheritdoc />
    public async Task<ClockInOutcome> ClockInAsync(
        ClockInRequest request,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = BuildAttendanceParameters(
            request.MobileUserId, request.DeviceId, request.Evidence, request.CorrelationId);

        parameters.Add("@AttendancePublicId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add("@AttendanceDate", dbType: DbType.Date, direction: ParameterDirection.Output);
        parameters.Add("@ClockInUtc", dbType: DbType.DateTime2, direction: ParameterDirection.Output);
        parameters.Add("@IsLateClockIn", dbType: DbType.Boolean, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                ClockInProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return new ClockInOutcome(
            ReadResultCode(parameters),
            parameters.Get<Guid?>("@AttendancePublicId"),
            parameters.Get<DateTime?>("@AttendanceDate").ToDateOnly(),
            parameters.Get<DateTime?>("@ClockInUtc").ToUtcOffset(),
            parameters.Get<bool?>("@IsLateClockIn"));
    }

    /// <inheritdoc />
    public async Task<ClockOutOutcome> ClockOutAsync(
        ClockOutRequest request,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = BuildAttendanceParameters(
            request.MobileUserId, request.DeviceId, request.Evidence, request.CorrelationId);

        parameters.Add("@AttendancePublicId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add("@AttendanceDate", dbType: DbType.Date, direction: ParameterDirection.Output);
        parameters.Add("@ClockInUtc", dbType: DbType.DateTime2, direction: ParameterDirection.Output);
        parameters.Add("@ClockOutUtc", dbType: DbType.DateTime2, direction: ParameterDirection.Output);
        parameters.Add("@DurationMinutes", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@IsEarlyClockOut", dbType: DbType.Boolean, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                ClockOutProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return new ClockOutOutcome(
            ReadResultCode(parameters),
            parameters.Get<Guid?>("@AttendancePublicId"),
            parameters.Get<DateTime?>("@AttendanceDate").ToDateOnly(),
            parameters.Get<DateTime?>("@ClockInUtc").ToUtcOffset(),
            parameters.Get<DateTime?>("@ClockOutUtc").ToUtcOffset(),
            parameters.Get<int?>("@DurationMinutes"),
            parameters.Get<bool?>("@IsEarlyClockOut"));
    }

    private static DynamicParameters BuildAttendanceParameters(
        int mobileUserId,
        int deviceId,
        LocationEvidence evidence,
        Guid correlationId)
    {
        DynamicParameters parameters = new();

        parameters.Add("@MobileUserId", mobileUserId, DbType.Int32);
        parameters.Add("@DeviceId", deviceId, DbType.Int32);
        parameters.Add("@OfficeLocationId", evidence.OfficeLocationId, DbType.Int32);

        // decimal(8,2), matching core.AttendanceEvent. Stating precision and
        // scale keeps the value from being rounded on its way into the column.
        parameters.Add("@DistanceMeters", evidence.DistanceMeters, DbType.Decimal, precision: 8, scale: 2);
        parameters.Add("@ReportedAccuracyMeters", evidence.ReportedAccuracyMeters, DbType.Decimal, precision: 8, scale: 2);

        parameters.Add("@Platform", evidence.Platform, DbType.Byte);
        parameters.Add("@WasMockedLocation", evidence.WasMockedLocation, DbType.Boolean);
        parameters.Add("@CoordinatesProtected", evidence.CoordinatesProtected, DbType.Binary);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);

        return parameters;
    }

    /// <summary>
    /// Reads the procedure's result code.
    /// </summary>
    /// <remarks>
    /// A procedure that returned no result code has failed in a way nobody
    /// planned for, and treating that as success would let an attendance
    /// request appear to work while nothing was written. It is surfaced as
    /// <see cref="AttendanceResultCode.InternalError"/> instead.
    /// </remarks>
    private static AttendanceResultCode ReadResultCode(DynamicParameters parameters)
    {
        int? value = parameters.Get<int?>(ResultCodeParameter);

        return value is null
            ? AttendanceResultCode.InternalError
            : (AttendanceResultCode)value.Value;
    }

    /// <summary>
    /// Shape of the row returned by <c>mobile.usp_Attendance_GetCurrentStatus</c>.
    /// </summary>
    private sealed class StatusRow
    {
        public DateTime AttendanceDate { get; init; }
        public DateTime ServerTimeUtc { get; init; }
        public byte AttendanceState { get; init; }
        public DateTime? ClockInUtc { get; init; }
        public DateTime? ClockOutUtc { get; init; }
        public int? DurationMinutes { get; init; }
        public TimeSpan? ClockInCloseTime { get; init; }
        public TimeSpan? ClockOutOpenTime { get; init; }
    }
}
