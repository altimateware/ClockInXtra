using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IMfaEnrolmentRepository"/>.
/// </summary>
public sealed class MfaEnrolmentRepository : IMfaEnrolmentRepository
{
    private const string EnrolProcedure = "admin.usp_MfaCredential_Enrol";
    private const string GetForActivationProcedure = "admin.usp_MfaCredential_GetForActivation";
    private const string ActivateProcedure = "admin.usp_MfaCredential_Activate";
    private const string RevokeProcedure = "admin.usp_MfaCredential_Revoke";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public MfaEnrolmentRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> EnrolAsync(
        int mobileUserId,
        byte[] secretProtected,
        bool replaceExisting,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@MobileUserId", mobileUserId, DbType.Int32);
        parameters.Add("@SecretProtected", secretProtected, DbType.Binary, size: -1);
        parameters.Add("@Algorithm", "SHA1", DbType.AnsiString, size: 8);
        parameters.Add("@Digits", (byte)6, DbType.Byte);
        parameters.Add("@PeriodSeconds", (short)30, DbType.Int16);
        parameters.Add("@ReplaceExisting", replaceExisting, DbType.Boolean);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add("@MfaCredentialId", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        return await ExecuteAsync(EnrolProcedure, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PendingEnrolment?> GetForActivationAsync(
        int mobileUserId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@MobileUserId", mobileUserId, DbType.Int32);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        EnrolmentRow? row = await lease.Connection
            .QuerySingleOrDefaultAsync<EnrolmentRow>(lease.StoredProcedure(
                GetForActivationProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        return new PendingEnrolment(
            row.MfaCredentialId,
            row.SecretProtected,
            new TotpParameters(
                Enum.TryParse(row.Algorithm, ignoreCase: true, out TotpAlgorithm algorithm)
                    ? algorithm
                    : TotpAlgorithm.Sha1,
                row.Digits,
                row.PeriodSeconds));
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> ActivateAsync(
        int mobileUserId,
        long timeStep,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@MobileUserId", mobileUserId, DbType.Int32);
        parameters.Add("@TimeStep", timeStep, DbType.Int64);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        return await ExecuteAsync(ActivateProcedure, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> RevokeAsync(
        int mobileUserId,
        string reason,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@MobileUserId", mobileUserId, DbType.Int32);
        parameters.Add("@Reason", reason, DbType.String, size: 256);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        return await ExecuteAsync(RevokeProcedure, parameters, cancellationToken).ConfigureAwait(false);
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

    private sealed class EnrolmentRow
    {
        public int MfaCredentialId { get; init; }
        public byte[] SecretProtected { get; init; } = [];
        public string Algorithm { get; init; } = "SHA1";
        public byte Digits { get; init; }
        public short PeriodSeconds { get; init; }
    }
}

/// <summary>
/// Dapper implementation of <see cref="IMobileUserAdministrationRepository"/>.
/// </summary>
public sealed class MobileUserAdministrationRepository : IMobileUserAdministrationRepository
{
    private const string SearchProcedure = "admin.usp_MobileUser_Search";
    private const string CreateProcedure = "admin.usp_MobileUser_Create";
    private const string GetByIdProcedure = "admin.usp_MobileUser_GetById";
    private const string UpdateProcedure = "admin.usp_MobileUser_Update";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public MobileUserAdministrationRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MobileUserSummary>> SearchAsync(
        string? searchTerm,
        bool onlyNotReady,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@SearchTerm", string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm, DbType.String, size: 128);
        parameters.Add("@OnlyNotReady", onlyNotReady, DbType.Boolean);
        parameters.Add("@PageNumber", 1, DbType.Int32);
        parameters.Add("@PageSize", 100, DbType.Int32);
        parameters.Add("@TotalCount", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<SearchRow> rows = await lease.Connection
            .QueryAsync<SearchRow>(lease.StoredProcedure(
                SearchProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new MobileUserSummary(
                row.MobileUserId,
                row.UserId,
                $"{row.FirstName} {row.LastName}".Trim(),
                row.Department,
                row.Status == 1,
                row.HasCredential,
                row.HasActiveMfa,
                row.HasActiveDevice,
                row.HasDeviceAwaitingApproval,
                row.CanClockIn,
                Missing(row))),
        ];
    }

    /// <inheritdoc />
    public async Task<EmployeeDetail?> GetEmployeeAsync(int mobileUserId, CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@MobileUserId", mobileUserId, DbType.Int32);
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        DetailRow? row = await lease.Connection
            .QuerySingleOrDefaultAsync<DetailRow>(lease.StoredProcedure(
                GetByIdProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        return row is null
            ? null
            : new EmployeeDetail(
                row.MobileUserId, row.UserId, row.EmployeeNumber, row.FirstName, row.LastName,
                row.Email, row.PhoneNumber, row.Department, row.JobTitle, row.IsActive, row.RowVersion);
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> UpdateEmployeeAsync(
        int mobileUserId,
        NewEmployee details,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@MobileUserId", mobileUserId, DbType.Int32);
        parameters.Add("@EmployeeNumber", Trimmed(details.EmployeeNumber), DbType.String, size: 32);
        parameters.Add("@FirstName", details.FirstName.Trim(), DbType.String, size: 80);
        parameters.Add("@LastName", details.LastName.Trim(), DbType.String, size: 80);
        parameters.Add("@Email", Trimmed(details.Email), DbType.String, size: 256);
        parameters.Add("@PhoneNumber", Trimmed(details.PhoneNumber), DbType.String, size: 32);
        parameters.Add("@Department", Trimmed(details.Department), DbType.String, size: 120);
        parameters.Add("@JobTitle", Trimmed(details.JobTitle), DbType.String, size: 120);
        parameters.Add("@RowVersion", rowVersion, DbType.Binary, size: 8);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                UpdateProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? code = parameters.Get<int?>("@ResultCode");
        return code is null ? AttendanceResultCode.InternalError : (AttendanceResultCode)code.Value;
    }

    /// <summary>The required details a record lacks, named as the form names them.</summary>
    private static List<string> Missing(SearchRow row)
    {
        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(row.EmployeeNumber))
        {
            missing.Add("employee number");
        }

        if (string.IsNullOrWhiteSpace(row.Email))
        {
            missing.Add("email");
        }

        if (string.IsNullOrWhiteSpace(row.PhoneNumber))
        {
            missing.Add("phone");
        }

        if (string.IsNullOrWhiteSpace(row.Department))
        {
            missing.Add("department");
        }

        if (string.IsNullOrWhiteSpace(row.JobTitle))
        {
            missing.Add("job title");
        }


        return missing;
    }

    /// <inheritdoc />
    public async Task<CreateEmployeeResult> CreateAsync(
        NewEmployee employee,
        PasswordHash password,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@UserId", employee.UserId, DbType.String, size: 64);
        parameters.Add("@EmployeeNumber", Trimmed(employee.EmployeeNumber), DbType.String, size: 32);
        parameters.Add("@FirstName", employee.FirstName, DbType.String, size: 80);
        parameters.Add("@LastName", employee.LastName, DbType.String, size: 80);
        parameters.Add("@Email", Trimmed(employee.Email), DbType.String, size: 256);
        parameters.Add("@PhoneNumber", Trimmed(employee.PhoneNumber), DbType.String, size: 32);
        parameters.Add("@Department", Trimmed(employee.Department), DbType.String, size: 120);
        parameters.Add("@JobTitle", Trimmed(employee.JobTitle), DbType.String, size: 120);
        parameters.Add("@Status", (byte)1, DbType.Byte);
        parameters.Add("@HashFormat", password.HashFormat, DbType.AnsiString, size: 32);
        parameters.Add("@Iterations", password.Iterations, DbType.Int32);
        parameters.Add("@Salt", password.Salt, DbType.Binary, size: 32);
        parameters.Add("@PasswordHash", password.Hash, DbType.Binary, size: 64);

        // The flag is recorded but nothing acts on it yet: the mobile app has no
        // password-change flow (OPEN-40). Passing 1 would mark every employee as
        // owing a change they have no way to make, which is worse than honest.
        parameters.Add("@MustChange", false, DbType.Boolean);
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add("@MobileUserId", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@MobileUserPublicId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                CreateProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? resultCode = parameters.Get<int?>("@ResultCode");

        // A missing code must not read as success: the portal would then show an
        // initial password for an employee who does not exist.
        if (resultCode is not 0)
        {
            return new CreateEmployeeResult(
                resultCode is null ? AttendanceResultCode.InternalError : (AttendanceResultCode)resultCode.Value,
                0,
                Guid.Empty);
        }

        return new CreateEmployeeResult(
            AttendanceResultCode.Success,
            parameters.Get<int?>("@MobileUserId") ?? 0,
            parameters.Get<Guid?>("@MobileUserPublicId") ?? Guid.Empty);
    }

    /// <summary>Whitespace-only optional input is absence, not a value.</summary>
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class SearchRow
    {
        public int MobileUserId { get; init; }
        public string UserId { get; init; } = string.Empty;
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public string? EmployeeNumber { get; init; }
        public string? Email { get; init; }
        public string? PhoneNumber { get; init; }
        public string? Department { get; init; }
        public string? JobTitle { get; init; }
        public byte Status { get; init; }
        public bool HasCredential { get; init; }
        public bool HasActiveMfa { get; init; }
        public bool HasActiveDevice { get; init; }
        public bool HasDeviceAwaitingApproval { get; init; }
        public bool CanClockIn { get; init; }
    }

    private sealed class DetailRow
    {
        public int MobileUserId { get; init; }
        public string UserId { get; init; } = string.Empty;
        public string? EmployeeNumber { get; init; }
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public string? Email { get; init; }
        public string? PhoneNumber { get; init; }
        public string? Department { get; init; }
        public string? JobTitle { get; init; }
        public bool IsActive { get; init; }
        public byte[] RowVersion { get; init; } = [];
    }
}
