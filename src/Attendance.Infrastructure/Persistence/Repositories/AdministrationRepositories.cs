using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Domain.ValueObjects;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IDeviceAdministrationRepository"/>.
/// </summary>
public sealed class DeviceAdministrationRepository : IDeviceAdministrationRepository
{
    private const string GetPendingProcedure = "admin.usp_Device_GetPendingApprovals";
    private const string GetRegisteredProcedure = "admin.usp_Device_GetRegistered";
    private const string ApproveProcedure = "admin.usp_Device_Approve";
    private const string RevokeProcedure = "admin.usp_Device_Revoke";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public DeviceAdministrationRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingDeviceApproval>> GetPendingApprovalsAsync(
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<PendingRow> rows = await lease.Connection
            .QueryAsync<PendingRow>(lease.StoredProcedure(
                GetPendingProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new PendingDeviceApproval(
                row.DeviceId,
                row.DevicePublicId,
                row.RowVersion,
                row.RegisteredUtc.ToUtcOffset(),
                (DevicePlatform)row.Platform,
                (AttestationLevel)row.AttestationLevel,
                row.DeviceModel,
                row.OsVersion,
                row.AppVersion,
                row.UserId,
                $"{row.FirstName} {row.LastName}".Trim(),
                row.Department,
                row.CurrentActiveDeviceModel,
                row.CurrentActiveDeviceLastSeenUtc.ToUtcOffset(),
                row.RecentFailedAttempts)),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegisteredDevice>> GetRegisteredAsync(
        DeviceStatus? status,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@Status", status is null ? null : (byte)status.Value, DbType.Byte);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<RegisteredRow> rows = await lease.Connection
            .QueryAsync<RegisteredRow>(lease.StoredProcedure(
                GetRegisteredProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new RegisteredDevice(
                row.DeviceId,
                row.DevicePublicId,
                row.RowVersion,
                (DeviceStatus)row.Status,
                (DevicePlatform)row.Platform,
                (AttestationLevel)row.AttestationLevel,
                row.DeviceModel,
                row.OsVersion,
                row.AppVersion,
                row.RegisteredUtc.ToUtcOffset(),
                row.ApprovedUtc.ToUtcOffset(),
                row.LastSeenUtc.ToUtcOffset(),
                row.RevokedUtc.ToUtcOffset(),
                row.RevokedReason,
                row.UserId,
                $"{row.FirstName} {row.LastName}".Trim(),
                row.Department)),
        ];
    }

    /// <inheritdoc />
    public async Task<DeviceApprovalOutcome> ApproveAsync(
        int deviceId,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@DeviceId", deviceId, DbType.Int32);
        parameters.Add("@RowVersion", rowVersion, DbType.Binary, size: 8);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add("@DevicePublicId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add("@RevokedPreviousDevicePublicId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                ApproveProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? code = parameters.Get<int?>(ResultCodeParameter);

        return new DeviceApprovalOutcome(
            code is null ? AttendanceResultCode.InternalError : (AttendanceResultCode)code.Value,
            parameters.Get<Guid?>("@DevicePublicId"),
            parameters.Get<Guid?>("@RevokedPreviousDevicePublicId"));
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> RevokeAsync(
        int deviceId,
        byte[] rowVersion,
        string reason,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@DeviceId", deviceId, DbType.Int32);
        parameters.Add("@RowVersion", rowVersion, DbType.Binary, size: 8);
        parameters.Add("@Reason", reason, DbType.String, size: 256);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add("@DevicePublicId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                RevokeProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? code = parameters.Get<int?>(ResultCodeParameter);

        return code is null ? AttendanceResultCode.InternalError : (AttendanceResultCode)code.Value;
    }

    private sealed class PendingRow
    {
        public int DeviceId { get; init; }
        public Guid DevicePublicId { get; init; }
        public byte[] RowVersion { get; init; } = [];
        public DateTime RegisteredUtc { get; init; }
        public byte Platform { get; init; }
        public byte AttestationLevel { get; init; }
        public string? DeviceModel { get; init; }
        public string? OsVersion { get; init; }
        public string? AppVersion { get; init; }
        public string UserId { get; init; } = string.Empty;
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public string? Department { get; init; }
        public string? CurrentActiveDeviceModel { get; init; }
        public DateTime? CurrentActiveDeviceLastSeenUtc { get; init; }
        public int RecentFailedAttempts { get; init; }
    }

    private sealed class RegisteredRow
    {
        public int DeviceId { get; init; }
        public Guid DevicePublicId { get; init; }
        public byte[] RowVersion { get; init; } = [];
        public byte Status { get; init; }
        public byte Platform { get; init; }
        public byte AttestationLevel { get; init; }
        public string? DeviceModel { get; init; }
        public string? OsVersion { get; init; }
        public string? AppVersion { get; init; }
        public DateTime RegisteredUtc { get; init; }
        public DateTime? ApprovedUtc { get; init; }
        public DateTime? LastSeenUtc { get; init; }
        public DateTime? RevokedUtc { get; init; }
        public string? RevokedReason { get; init; }
        public string UserId { get; init; } = string.Empty;
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public string? Department { get; init; }
    }
}

/// <summary>
/// Dapper implementation of <see cref="ISettingAdministrationRepository"/>.
/// </summary>
/// <remarks>
/// The value is validated by the stored procedure as well as by the portal. That
/// is not duplication for its own sake: these settings change how attendance is
/// calculated for everyone, and an unrecognised timezone would make every
/// clock-in fail at <c>AT TIME ZONE</c> rather than being refused at entry (§44).
/// </remarks>
public sealed class SettingAdministrationRepository : ISettingAdministrationRepository
{
    private const string GetAllProcedure = "admin.usp_ApplicationSetting_GetAll";
    private const string SetProcedure = "admin.usp_ApplicationSetting_Set";
    private const string TimeZonesProcedure = "admin.usp_TimeZone_GetAll";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public SettingAdministrationRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApplicationSettingRow>> GetAllAsync(
        string? category,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@Category", category, DbType.String, size: 64);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<SettingRow> rows = await lease.Connection
            .QueryAsync<SettingRow>(lease.StoredProcedure(
                GetAllProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new ApplicationSettingRow(
                row.SettingKey,
                row.SettingValue,
                row.DataType,
                row.Category,
                row.Description,
                row.AllowedValues,
                row.MinValue,
                row.MaxValue,
                row.Unit,
                row.BlankMeaning,
                row.RequiresBusinessConfirmation,
                row.IsUnset,
                row.IsMandatoryForAttendance,
                row.ConfirmedByUserName,
                row.UpdatedUtc.ToUtcOffset(),
                row.RowVersion)),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimeZoneOption>> GetTimeZonesAsync(CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<TimeZoneRow> rows = await lease.Connection
            .QueryAsync<TimeZoneRow>(lease.StoredProcedure(
                TimeZonesProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new TimeZoneOption(
                row.TimeZoneId, row.CurrentUtcOffset, row.IsCurrentlyDaylightSaving)),
        ];
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> SetAsync(
        string settingKey,
        string? value,
        bool confirm,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@SettingKey", settingKey, DbType.AnsiString, size: 100);
        parameters.Add("@SettingValue", value, DbType.String, size: 400);
        parameters.Add("@Confirm", confirm, DbType.Boolean);
        parameters.Add("@RowVersion", rowVersion, DbType.Binary, size: 8);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                SetProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? code = parameters.Get<int?>(ResultCodeParameter);

        return code is null ? AttendanceResultCode.InternalError : (AttendanceResultCode)code.Value;
    }

    private sealed class SettingRow
    {
        public string SettingKey { get; init; } = string.Empty;
        public string? SettingValue { get; init; }
        public string DataType { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? AllowedValues { get; init; }
        public decimal? MinValue { get; init; }
        public decimal? MaxValue { get; init; }
        public string? Unit { get; init; }
        public string? BlankMeaning { get; init; }
        public bool RequiresBusinessConfirmation { get; init; }
        public bool IsUnset { get; init; }
        public bool IsMandatoryForAttendance { get; init; }
        public string? ConfirmedByUserName { get; init; }
        public DateTime? UpdatedUtc { get; init; }
        public byte[] RowVersion { get; init; } = [];
    }

    private sealed class TimeZoneRow
    {
        public string TimeZoneId { get; init; } = string.Empty;
        public string CurrentUtcOffset { get; init; } = string.Empty;
        public bool IsCurrentlyDaylightSaving { get; init; }
    }
}
