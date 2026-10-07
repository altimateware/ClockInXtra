using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IReportingRepository"/>.
/// </summary>
public sealed class ReportingRepository : IReportingRepository
{
    private const string DailyReportProcedure = "admin.usp_Attendance_GetDailyReport";
    private const string AuditSearchProcedure = "admin.usp_AuditLog_Search";
    private const string ValidationFailuresProcedure = "admin.usp_Report_GetValidationFailures";
    private const string FilterOptionsProcedure = "admin.usp_Report_GetFilterOptions";

    /// <summary>
    /// A datalist of this many names is still usable in a browser; beyond it,
    /// typing the user id is the better tool anyway.
    /// </summary>
    private const int MaxEmployeeSuggestions = 5000;
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public ReportingRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttendanceReportRow>> GetDailyAttendanceAsync(
        AttendanceReportFilter filter,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@FromDate", filter.FromDate.ToDateTime(TimeOnly.MinValue), DbType.Date);
        parameters.Add("@ToDate", filter.ToDate.ToDateTime(TimeOnly.MinValue), DbType.Date);
        parameters.Add("@UserId", NullIfBlank(filter.UserId), DbType.String, size: 64);
        parameters.Add("@Department", NullIfBlank(filter.Department), DbType.String, size: 120);
        parameters.Add("@OfficeLocationId", filter.OfficeLocationId, DbType.Int32);
        parameters.Add("@Status", (byte?)filter.Status, DbType.Byte);
        parameters.Add("@Exception", (byte?)filter.Exception, DbType.Byte);
        parameters.Add("@PageSize", ReportLimits.DailyAttendanceRows, DbType.Int32);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<ReportRow> rows = await lease.Connection
            .QueryAsync<ReportRow>(lease.StoredProcedure(
                DailyReportProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new AttendanceReportRow(
                row.AttendancePublicId,
                DateOnly.FromDateTime(row.AttendanceDate),
                row.UserId,
                $"{row.FirstName} {row.LastName}".Trim(),
                row.Department,
                (AttendanceRecordStatus)row.Status,
                row.ClockInUtc.ToUtcOffset(),
                row.ClockOutUtc.ToUtcOffset(),
                row.DurationMinutes,
                row.IsLateClockIn,
                row.IsEarlyClockOut,
                row.IsMissingClockOut,
                row.ClockInOfficeName,
                row.ClockInDistanceMeters,
                row.ClockInAccuracyMeters,
                row.ClockInWasMockedLocation ?? false)),
        ];
    }

    /// <inheritdoc />
    public async Task<ValidationFailureReport> GetValidationFailuresAsync(
        ValidationFailureFilter filter,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@FromUtc", filter.FromUtc.UtcDateTime, DbType.DateTime2);
        parameters.Add("@ToUtc", filter.ToUtc.UtcDateTime, DbType.DateTime2);
        parameters.Add("@Category", (byte?)filter.Category, DbType.Byte);
        parameters.Add("@SubjectKey", NullIfBlank(filter.SubjectKey), DbType.String, size: 128);

        // One more than a page, so the caller learns whether an older page exists
        // without a second query.
        parameters.Add("@PageSize", ReportLimits.ValidationFailuresPerPage + 1, DbType.Int32);
        parameters.Add("@BeforeSecurityEventId", filter.BeforeSecurityEventId, DbType.Int64);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await using SqlMapper.GridReader reader = await lease.Connection
            .QueryMultipleAsync(lease.StoredProcedure(
                ValidationFailuresProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        List<FailureRow> failureRows = [.. await reader.ReadAsync<FailureRow>().ConfigureAwait(false)];
        List<FailureSummaryRow> summaryRows = [.. await reader.ReadAsync<FailureSummaryRow>().ConfigureAwait(false)];

        List<ValidationFailureEntry> failures =
        [
            .. failureRows.Take(ReportLimits.ValidationFailuresPerPage).Select(row => new ValidationFailureEntry(
                row.SecurityEventId,
                row.OccurredUtc.ToUtcOffset(),
                (ValidationFailureCategory)row.Category,
                row.EventType,
                row.Severity,
                row.ReasonCode,
                row.SubjectKey,
                row.DevicePublicId,
                row.EmployeeUserId,
                row.EmployeeUserId is null ? null : $"{row.FirstName} {row.LastName}".Trim(),
                row.SourceApplication,
                row.CorrelationId,
                row.LedgerCommitTime.ToUtcOffset())),
        ];

        List<ValidationFailureSummary> summary =
        [
            .. summaryRows.Select(row => new ValidationFailureSummary(
                (ValidationFailureCategory)row.Category,
                row.ReasonCode,
                row.EventCount,
                row.DistinctSubjects,
                row.FirstOccurredUtc.ToUtcOffset(),
                row.LastOccurredUtc.ToUtcOffset())),
        ];

        return new ValidationFailureReport(failures, summary, failureRows.Count > ReportLimits.ValidationFailuresPerPage);
    }

    /// <inheritdoc />
    public async Task<ReportFilterOptions> GetFilterOptionsAsync(bool includeEventTypes, CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@MaxEmployees", MaxEmployeeSuggestions, DbType.Int32);
        parameters.Add("@IncludeEventTypes", includeEventTypes, DbType.Boolean);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await using SqlMapper.GridReader reader = await lease.Connection
            .QueryMultipleAsync(lease.StoredProcedure(
                FilterOptionsProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        List<EmployeeOption> employees =
        [
            .. (await reader.ReadAsync<EmployeeOptionRow>().ConfigureAwait(false))
                .Select(row => new EmployeeOption(row.UserId, row.DisplayName)),
        ];

        List<string> departments = [.. await reader.ReadAsync<string>().ConfigureAwait(false)];

        List<string> eventTypes = includeEventTypes
            ? [.. await reader.ReadAsync<string>().ConfigureAwait(false)]
            : [];

        return new ReportFilterOptions(employees, departments, eventTypes);
    }

    /// <inheritdoc />
    public async Task<AuditSearchResult> SearchAuditAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? eventType,
        Guid? correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@FromUtc", fromUtc.UtcDateTime, DbType.DateTime2);
        parameters.Add("@ToUtc", toUtc.UtcDateTime, DbType.DateTime2);
        parameters.Add("@EventType", string.IsNullOrWhiteSpace(eventType) ? null : eventType, DbType.AnsiString, size: 64);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add("@PageSize", 200, DbType.Int32);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await using SqlMapper.GridReader reader = await lease.Connection
            .QueryMultipleAsync(lease.StoredProcedure(
                AuditSearchProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        List<AuditEntry> entries =
        [
            .. (await reader.ReadAsync<AuditRow>().ConfigureAwait(false))
                .Select(row => new AuditEntry(
                    row.EventType,
                    row.OccurredUtc.ToUtcOffset(),
                    row.ActorDisplay,
                    row.SubjectType,
                    row.SubjectId,
                    row.Result == 1,
                    row.ReasonCode,
                    row.SourceApplication,
                    row.CorrelationId,
                    row.Details,
                    row.LedgerCommitTime.ToUtcOffset(),
                    row.LedgerPrincipalName)),
        ];

        List<SecurityEventEntry> events =
        [
            .. (await reader.ReadAsync<SecurityRow>().ConfigureAwait(false))
                .Select(row => new SecurityEventEntry(
                    row.EventType,
                    row.OccurredUtc.ToUtcOffset(),
                    row.Severity,
                    row.SubjectKey,
                    row.ReasonCode,
                    row.SourceApplication,
                    row.CorrelationId)),
        ];

        return new AuditSearchResult(entries, events);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class EmployeeOptionRow
    {
        public string UserId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
    }

    private sealed class ReportRow
    {
        public Guid AttendancePublicId { get; init; }
        public DateTime AttendanceDate { get; init; }
        public string UserId { get; init; } = string.Empty;
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public string? Department { get; init; }
        public byte Status { get; init; }
        public DateTime? ClockInUtc { get; init; }
        public DateTime? ClockOutUtc { get; init; }
        public int? DurationMinutes { get; init; }
        public bool? IsLateClockIn { get; init; }
        public bool? IsEarlyClockOut { get; init; }
        public bool IsMissingClockOut { get; init; }
        public string? ClockInOfficeName { get; init; }
        public decimal? ClockInDistanceMeters { get; init; }
        public decimal? ClockInAccuracyMeters { get; init; }
        public bool? ClockInWasMockedLocation { get; init; }
    }

    private sealed class AuditRow
    {
        public string EventType { get; init; } = string.Empty;
        public DateTime OccurredUtc { get; init; }
        public string? ActorDisplay { get; init; }
        public string? SubjectType { get; init; }
        public string? SubjectId { get; init; }
        public byte Result { get; init; }
        public string? ReasonCode { get; init; }
        public string? SourceApplication { get; init; }
        public Guid? CorrelationId { get; init; }
        public string? Details { get; init; }
        public DateTime? LedgerCommitTime { get; init; }
        public string? LedgerPrincipalName { get; init; }
    }

    private sealed class SecurityRow
    {
        public string EventType { get; init; } = string.Empty;
        public DateTime OccurredUtc { get; init; }
        public byte Severity { get; init; }
        public string? SubjectKey { get; init; }
        public string ReasonCode { get; init; } = string.Empty;
        public string? SourceApplication { get; init; }
        public Guid? CorrelationId { get; init; }
    }

    private sealed class FailureRow
    {
        public long SecurityEventId { get; init; }
        public DateTime OccurredUtc { get; init; }
        public byte Category { get; init; }
        public string EventType { get; init; } = string.Empty;
        public byte Severity { get; init; }
        public string ReasonCode { get; init; } = string.Empty;
        public string? SubjectKey { get; init; }
        public Guid? DevicePublicId { get; init; }
        public string? EmployeeUserId { get; init; }
        public string? FirstName { get; init; }
        public string? LastName { get; init; }
        public string? SourceApplication { get; init; }
        public Guid? CorrelationId { get; init; }
        public DateTime? LedgerCommitTime { get; init; }
    }

    private sealed class FailureSummaryRow
    {
        public byte Category { get; init; }
        public string ReasonCode { get; init; } = string.Empty;
        public int EventCount { get; init; }
        public int DistinctSubjects { get; init; }
        public DateTime FirstOccurredUtc { get; init; }
        public DateTime LastOccurredUtc { get; init; }
    }
}
