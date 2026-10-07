using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IAuthenticationAttemptRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// The counters live in SQL Server rather than in process memory for a specific
/// reason: the ASP.NET Core rate limiter counts per process, so with two or more
/// IIS nodes an attacker spreads attempts across them and the limit never fires
/// (decision TD-10, §45). Lockout is a security control, so it is counted where
/// every node sees the same number.
/// </para>
/// <para>
/// The subject key is the identifier the employee typed, which is why the
/// parameter is sized to match the column: an unknown identifier must be counted
/// exactly like a known one, or the difference in behaviour reveals which
/// identifiers are real.
/// </para>
/// </remarks>
public sealed class AuthenticationAttemptRepository : IAuthenticationAttemptRepository
{
    private const string CheckProcedure = "core.usp_AuthenticationAttempt_Check";
    private const string RegisterFailureProcedure = "core.usp_AuthenticationAttempt_RegisterFailure";
    private const string ResetProcedure = "core.usp_AuthenticationAttempt_Reset";

    private const string LockedUntilParameter = "@LockedUntilUtc";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public AuthenticationAttemptRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<LockoutState> CheckAsync(
        AuthenticationSubject subject,
        string subjectKey,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = BuildSubjectParameters(subject, subjectKey);
        parameters.Add(LockedUntilParameter, dbType: DbType.DateTime2, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await ExecuteAsync(CheckProcedure, parameters, cancellationToken).ConfigureAwait(false);

        return new LockoutState(
            IsLockedOut(parameters),
            parameters.Get<DateTime?>(LockedUntilParameter).ToUtcOffset(),
            FailedCount: 0);
    }

    /// <inheritdoc />
    public async Task<LockoutState> RegisterFailureAsync(
        AuthenticationSubject subject,
        string subjectKey,
        int threshold,
        int lockoutMinutes,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = BuildSubjectParameters(subject, subjectKey);
        parameters.Add("@Threshold", threshold, DbType.Int32);
        parameters.Add("@LockoutMinutes", lockoutMinutes, DbType.Int32);
        parameters.Add("@FailedCount", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add(LockedUntilParameter, dbType: DbType.DateTime2, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await ExecuteAsync(RegisterFailureProcedure, parameters, cancellationToken).ConfigureAwait(false);

        return new LockoutState(
            IsLockedOut(parameters),
            parameters.Get<DateTime?>(LockedUntilParameter).ToUtcOffset(),
            parameters.Get<int?>("@FailedCount") ?? 0);
    }

    /// <inheritdoc />
    public async Task ResetAsync(
        AuthenticationSubject subject,
        string subjectKey,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = BuildSubjectParameters(subject, subjectKey);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await ExecuteAsync(ResetProcedure, parameters, cancellationToken).ConfigureAwait(false);
    }

    private static DynamicParameters BuildSubjectParameters(AuthenticationSubject subject, string subjectKey)
    {
        DynamicParameters parameters = new();
        parameters.Add("@SubjectType", (byte)subject, DbType.Byte);
        parameters.Add("@SubjectKey", subjectKey, DbType.String, size: 128);
        return parameters;
    }

    /// <summary>
    /// Whether the procedure reported a lock.
    /// </summary>
    /// <remarks>
    /// A missing result code is treated as locked. This is the one place where
    /// failing closed is right: the alternative is that an unexplained database
    /// condition silently disables brute-force protection.
    /// </remarks>
    private static bool IsLockedOut(DynamicParameters parameters)
    {
        int? value = parameters.Get<int?>(ResultCodeParameter);
        return value is null || value.Value == (int)AttendanceResultCode.AccountLocked;
    }

    private async Task ExecuteAsync(
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
    }
}
