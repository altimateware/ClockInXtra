using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IMfaCredentialRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// One call, one conditional UPDATE. The check and the write cannot be separated
/// by a race, which is the whole reason consumption lives in the database rather
/// than in application memory: remembering "this step was used" in one process
/// would not stop the same code being presented to a second IIS node a moment
/// later.
/// </para>
/// <para>
/// The TOTP secret is never read here. It leaves its encrypted column only
/// through the Data Protection services, and only for the length of one
/// verification.
/// </para>
/// </remarks>
public sealed class MfaCredentialRepository : IMfaCredentialRepository
{
    private const string ConsumeProcedure = "core.usp_MfaCredential_TryConsumeTimeStep";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public MfaCredentialRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> TryConsumeTimeStepAsync(
        int mobileUserId,
        long timeStep,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@MobileUserId", mobileUserId, DbType.Int32);
        parameters.Add("@TimeStep", timeStep, DbType.Int64);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                ConsumeProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? value = parameters.Get<int?>(ResultCodeParameter);

        // A procedure that returned no code has failed unexpectedly. Treating
        // that as a consumed step would let a replayed code through, so it is
        // reported as an internal error instead.
        return value is null
            ? AttendanceResultCode.InternalError
            : (AttendanceResultCode)value.Value;
    }
}
