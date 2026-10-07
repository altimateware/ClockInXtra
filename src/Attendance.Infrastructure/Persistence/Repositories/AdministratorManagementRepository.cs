using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IAdministratorManagementRepository"/>.
/// </summary>
/// <remarks>
/// Every result code is read from the procedure and a missing one is reported as
/// <see cref="AttendanceResultCode.InternalError"/>, never as success. On this
/// class that matters more than most: a status change or role removal mistaken
/// for a success would leave someone believing an administrator had lost access
/// when they had not.
/// </remarks>
public sealed class AdministratorManagementRepository : IAdministratorManagementRepository
{
    private const string SearchProcedure = "admin.usp_Administrator_Search";
    private const string RolesProcedure = "admin.usp_Role_GetAll";
    private const string CreateProcedure = "admin.usp_Administrator_Create";
    private const string SetStatusProcedure = "admin.usp_Administrator_SetStatus";
    private const string SetRoleProcedure = "admin.usp_Administrator_SetRole";
    private const string ResetPasswordProcedure = "admin.usp_Administrator_ResetPassword";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public AdministratorManagementRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdministratorSummary>> SearchAsync(
        string? searchTerm,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@SearchTerm", string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm.Trim(), DbType.String, size: 64);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await using SqlMapper.GridReader reader = await lease.Connection
            .QueryMultipleAsync(lease.StoredProcedure(
                SearchProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        List<AdministratorRow> administrators = [.. await reader.ReadAsync<AdministratorRow>().ConfigureAwait(false)];
        List<AssignmentRow> assignments = [.. await reader.ReadAsync<AssignmentRow>().ConfigureAwait(false)];

        ILookup<int, RoleReference> rolesByAdministrator = assignments.ToLookup(
            row => row.AdministratorId,
            row => new RoleReference(row.RoleId, row.RoleName));

        return
        [
            .. administrators.Select(row => new AdministratorSummary(
                row.AdministratorId,
                row.AdministratorPublicId,
                row.UserName,
                row.DisplayName,
                row.Email,
                row.Status == 1,
                (AdministratorMfaStatus)row.MfaStatus,
                row.MustChangePassword,
                row.LastLoginUtc,
                row.CreatedUtc,
                row.CanSignIn,
                [.. rolesByAdministrator[row.AdministratorId]])),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleSummary>> GetRolesAsync(
        int? actingAdministratorId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@ActingAdministratorId", actingAdministratorId, DbType.Int32);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await using SqlMapper.GridReader reader = await lease.Connection
            .QueryMultipleAsync(lease.StoredProcedure(
                RolesProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        List<RoleRow> roles = [.. await reader.ReadAsync<RoleRow>().ConfigureAwait(false)];
        List<RolePermissionRow> grants = [.. await reader.ReadAsync<RolePermissionRow>().ConfigureAwait(false)];

        ILookup<int, PermissionSummary> permissionsByRole = grants.ToLookup(
            row => row.RoleId,
            row => new PermissionSummary(row.Code, row.Category, row.Description));

        return
        [
            .. roles.Select(row => new RoleSummary(
                row.RoleId,
                row.Name,
                row.Description,
                row.IsSystemRole,
                row.AssignableByActor,
                [.. permissionsByRole[row.RoleId]])),
        ];
    }

    /// <inheritdoc />
    public async Task<CreateAdministratorResult> CreateAsync(
        NewAdministrator administrator,
        PasswordHash password,
        int? roleId,
        int actingAdministratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@UserName", administrator.UserName, DbType.String, size: 64);
        parameters.Add("@DisplayName", administrator.DisplayName, DbType.String, size: 160);
        parameters.Add("@Email", string.IsNullOrWhiteSpace(administrator.Email) ? null : administrator.Email.Trim(), DbType.String, size: 256);
        parameters.Add("@HashFormat", password.HashFormat, DbType.AnsiString, size: 32);
        parameters.Add("@Iterations", password.Iterations, DbType.Int32);
        parameters.Add("@Salt", password.Salt, DbType.Binary, size: 32);
        parameters.Add("@PasswordHash", password.Hash, DbType.Binary, size: 64);
        parameters.Add("@RoleId", roleId, DbType.Int32);
        parameters.Add("@CreatedByAdministratorId", actingAdministratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add("@AdministratorId", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@AdministratorPublicId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await ExecuteAsync(CreateProcedure, parameters, cancellationToken).ConfigureAwait(false);

        AttendanceResultCode code = ReadCode(parameters);

        return code == AttendanceResultCode.Success
            ? new CreateAdministratorResult(
                code,
                parameters.Get<int?>("@AdministratorId") ?? 0,
                parameters.Get<Guid?>("@AdministratorPublicId") ?? Guid.Empty)
            : new CreateAdministratorResult(code, 0, Guid.Empty);
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> SetStatusAsync(
        int administratorId,
        bool active,
        string? reason,
        int actingAdministratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@Status", (byte)(active ? 1 : 0), DbType.Byte);
        parameters.Add("@Reason", string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), DbType.String, size: 256);
        parameters.Add("@ActingAdministratorId", actingAdministratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await ExecuteAsync(SetStatusProcedure, parameters, cancellationToken).ConfigureAwait(false);

        return ReadCode(parameters);
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> SetRoleAsync(
        int administratorId,
        int roleId,
        bool grant,
        int actingAdministratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@RoleId", roleId, DbType.Int32);
        parameters.Add("@Grant", grant, DbType.Boolean);
        parameters.Add("@ActingAdministratorId", actingAdministratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await ExecuteAsync(SetRoleProcedure, parameters, cancellationToken).ConfigureAwait(false);

        return ReadCode(parameters);
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> ResetPasswordAsync(
        int administratorId,
        PasswordHash password,
        int actingAdministratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@HashFormat", password.HashFormat, DbType.AnsiString, size: 32);
        parameters.Add("@Iterations", password.Iterations, DbType.Int32);
        parameters.Add("@Salt", password.Salt, DbType.Binary, size: 32);
        parameters.Add("@PasswordHash", password.Hash, DbType.Binary, size: 64);
        parameters.Add("@ActingAdministratorId", actingAdministratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await ExecuteAsync(ResetPasswordProcedure, parameters, cancellationToken).ConfigureAwait(false);

        return ReadCode(parameters);
    }

    private async Task ExecuteAsync(string procedure, DynamicParameters parameters, CancellationToken cancellationToken)
    {
        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                procedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);
    }

    private static AttendanceResultCode ReadCode(DynamicParameters parameters) =>
        parameters.Get<int?>(ResultCodeParameter) is { } value
            ? (AttendanceResultCode)value
            : AttendanceResultCode.InternalError;

    private sealed class AdministratorRow
    {
        public int AdministratorId { get; init; }
        public Guid AdministratorPublicId { get; init; }
        public string UserName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? Email { get; init; }
        public byte Status { get; init; }
        public byte MfaStatus { get; init; }
        public bool MustChangePassword { get; init; }
        public DateTime? LastLoginUtc { get; init; }
        public DateTime CreatedUtc { get; init; }
        public bool CanSignIn { get; init; }
    }

    private sealed class AssignmentRow
    {
        public int AdministratorId { get; init; }
        public int RoleId { get; init; }
        public string RoleName { get; init; } = string.Empty;
    }

    private sealed class RoleRow
    {
        public int RoleId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public bool IsSystemRole { get; init; }
        public bool AssignableByActor { get; init; }
    }

    private sealed class RolePermissionRow
    {
        public int RoleId { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
    }
}
