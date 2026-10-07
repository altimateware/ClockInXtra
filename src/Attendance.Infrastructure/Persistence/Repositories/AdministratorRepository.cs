using System.Data;
using System.Globalization;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IAdministratorRepository"/>.
/// </summary>
public sealed class AdministratorRepository : IAdministratorRepository
{
    private const string GetForAuthenticationProcedure = "admin.usp_Administrator_GetForAuthentication";
    private const string RecordLoginProcedure = "admin.usp_Administrator_RecordLogin";
    private const string RecordLogoutProcedure = "admin.usp_Administrator_RecordLogout";
    private const string ConsumeTimeStepProcedure = "admin.usp_Administrator_TryConsumeTimeStep";
    private const string EnrolMfaProcedure = "admin.usp_Administrator_EnrolMfa";
    private const string ChangePasswordProcedure = "admin.usp_Administrator_ChangePassword";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public AdministratorRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<AdministratorRecord?> GetForAuthenticationAsync(
        string userName,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@UserName", userName, DbType.String, size: 64);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await using SqlMapper.GridReader reader = await lease.Connection
            .QueryMultipleAsync(lease.StoredProcedure(
                GetForAuthenticationProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        AdministratorRow? row = await reader.ReadSingleOrDefaultAsync<AdministratorRow>().ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        List<string> permissions = [.. await reader.ReadAsync<string>().ConfigureAwait(false)];

        // An inactive account returns 1014 and still returns its row, so the
        // caller can spend the same work and record a precise reason. Whether the
        // account may sign in is the result code's business, not the row's.
        bool isActive = parameters.Get<int?>(ResultCodeParameter) == (int)AttendanceResultCode.Success;

        PasswordHash? credential = row.PasswordHash is { Length: > 0 } hash && row.Salt is { Length: > 0 } salt
            ? new PasswordHash(row.HashFormat ?? string.Empty, row.Iterations, salt, hash)
            : null;

        return new AdministratorRecord(
            row.AdministratorId,
            row.AdministratorPublicId,
            row.UserName,
            row.DisplayName,
            row.Email,
            credential,
            row.MustChangePassword,
            row.SecurityStamp,
            row.MfaSecretProtected,
            (AdministratorMfaStatus)row.MfaStatus,
            isActive,
            permissions);
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> RecordLoginAsync(
        int administratorId,
        Guid correlationId,
        byte[]? sourceAddressHash,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add("@SourceAddressHash", sourceAddressHash, DbType.Binary, size: 32);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                RecordLoginProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? value = parameters.Get<int?>(ResultCodeParameter);

        return value is null ? AttendanceResultCode.InternalError : (AttendanceResultCode)value.Value;
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> RecordLogoutAsync(
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                RecordLogoutProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? value = parameters.Get<int?>(ResultCodeParameter);

        return value is null ? AttendanceResultCode.InternalError : (AttendanceResultCode)value.Value;
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> TryConsumeTimeStepAsync(
        int administratorId,
        long timeStep,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@TimeStep", timeStep, DbType.Int64);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                ConsumeTimeStepProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? value = parameters.Get<int?>(ResultCodeParameter);

        // A missing code must not read as a consumed step: that would let a
        // replayed code through on the most privileged account in the system.
        return value is null ? AttendanceResultCode.InternalError : (AttendanceResultCode)value.Value;
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> EnrolMfaAsync(
        int administratorId,
        byte[] secretProtected,
        bool replaceExisting,
        int enrolledByAdministratorId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secretProtected);

        DynamicParameters parameters = new();
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@SecretProtected", secretProtected, DbType.Binary, size: -1);
        parameters.Add("@ReplaceExisting", replaceExisting, DbType.Boolean);
        parameters.Add("@EnrolledByAdministratorId", enrolledByAdministratorId, DbType.Int32);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                EnrolMfaProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? value = parameters.Get<int?>(ResultCodeParameter);

        // A missing code must not read as success: the caller would then show an
        // enrolment URI for a secret the database never stored, and the account
        // would be unable to sign in.
        return value is null ? AttendanceResultCode.InternalError : (AttendanceResultCode)value.Value;
    }

    /// <inheritdoc />
    public async Task<PasswordChangeResult> ChangePasswordAsync(
        int administratorId,
        Guid expectedSecurityStamp,
        PasswordHash password,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@AdministratorId", administratorId, DbType.Int32);
        parameters.Add("@ExpectedSecurityStamp", expectedSecurityStamp, DbType.Guid);
        parameters.Add("@HashFormat", password.HashFormat, DbType.AnsiString, size: 32);
        parameters.Add("@Iterations", password.Iterations, DbType.Int32);
        parameters.Add("@Salt", password.Salt, DbType.Binary, size: 32);
        parameters.Add("@PasswordHash", password.Hash, DbType.Binary, size: 64);
        parameters.Add("@CorrelationId", correlationId, DbType.Guid);
        parameters.Add("@NewSecurityStamp", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                ChangePasswordProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? value = parameters.Get<int?>(ResultCodeParameter);
        Guid? newStamp = parameters.Get<Guid?>("@NewSecurityStamp");

        // Success without a stamp would leave the caller unable to re-issue its
        // own session, so it is treated as a failure rather than trusted.
        if (value is 0 && newStamp is { } stamp && stamp != Guid.Empty)
        {
            return new PasswordChangeResult(AttendanceResultCode.Success, stamp);
        }

        return new PasswordChangeResult(
            value is null or 0 ? AttendanceResultCode.InternalError : (AttendanceResultCode)value.Value,
            null);
    }

    private sealed class AdministratorRow
    {
        public int AdministratorId { get; init; }
        public Guid AdministratorPublicId { get; init; }
        public string UserName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? Email { get; init; }
        public string? HashFormat { get; init; }
        public int Iterations { get; init; }
        public byte[]? Salt { get; init; }
        public byte[]? PasswordHash { get; init; }
        public bool MustChangePassword { get; init; }
        public Guid SecurityStamp { get; init; }
        public byte[]? MfaSecretProtected { get; init; }
        public byte MfaStatus { get; init; }
        public byte Status { get; init; }
    }
}

/// <summary>
/// Reads the portal's policy settings from <c>admin.usp_ApplicationSetting_GetAll</c>.
/// </summary>
/// <remarks>
/// As with the mobile provider, every fallback is the restrictive one. A
/// configuration problem should make the portal stricter than intended, never
/// more permissive — and this portal can revoke devices and rewrite attendance
/// rules.
/// </remarks>
public sealed class AdministratorPolicyProvider : IAdministratorPolicyProvider, IDisposable
{
    private const string GetAllProcedure = "admin.usp_ApplicationSetting_GetAll";
    private const int CacheLifetimeMilliseconds = 30_000;

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private AdministratorPolicy? _cached;
    private long _cacheExpiresAt;

    /// <summary>Creates the provider.</summary>
    public AdministratorPolicyProvider(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<AdministratorPolicy> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } current && Environment.TickCount64 < Volatile.Read(ref _cacheExpiresAt))
        {
            return current;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_cached is { } refreshed && Environment.TickCount64 < Volatile.Read(ref _cacheExpiresAt))
            {
                return refreshed;
            }

            AdministratorPolicy loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);

            _cached = loaded;
            Volatile.Write(ref _cacheExpiresAt, Environment.TickCount64 + CacheLifetimeMilliseconds);

            return loaded;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Releases the refresh gate.</summary>
    public void Dispose() => _refreshGate.Dispose();

    private async Task<AdministratorPolicy> LoadAsync(CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@Category", "Security", DbType.String, size: 64);
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<SettingRow> rows = await lease.Connection
            .QueryAsync<SettingRow>(lease.StoredProcedure(
                GetAllProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        Dictionary<string, string?> settings = rows.ToDictionary(
            row => row.SettingKey, row => row.SettingValue, StringComparer.OrdinalIgnoreCase);

        return new AdministratorPolicy(
            ReadInt(settings, "Security.AdministratorLockoutThreshold", 5),
            ReadInt(settings, "Security.AdministratorLockoutMinutes", 15),
            ReadBool(settings, "Security.RequireAdministratorMfa", true),
            ReadInt(settings, "Security.TotpStepTolerance", 1));
    }

    private static int ReadInt(Dictionary<string, string?> settings, string key, int fallback) =>
        settings.TryGetValue(key, out string? value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;

    private static bool ReadBool(Dictionary<string, string?> settings, string key, bool fallback) =>
        settings.TryGetValue(key, out string? value) && bool.TryParse(value, out bool parsed)
            ? parsed
            : fallback;

    private sealed class SettingRow
    {
        public string SettingKey { get; init; } = string.Empty;
        public string? SettingValue { get; init; }
    }
}
