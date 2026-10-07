using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IIdempotencyStore"/>.
/// </summary>
/// <remarks>
/// The claim is an INSERT whose failure is the answer: a duplicate key means the
/// operation has been seen before. That is why the procedure turns off
/// <c>XACT_ABORT</c> around it — a duplicate here is an expected outcome, not a
/// fault, and must not doom a caller's transaction.
/// </remarks>
public sealed class IdempotencyStore : IIdempotencyStore
{
    private const string TryBeginProcedure = "mobile.usp_Idempotency_TryBegin";
    private const string CompleteProcedure = "mobile.usp_Idempotency_Complete";

    private const string StoredResultCodeParameter = "@StoredResultCode";
    private const string StoredPayloadParameter = "@StoredPayload";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the store.</summary>
    public IdempotencyStore(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<IdempotencyClaim> TryBeginAsync(
        int deviceId,
        Guid idempotencyKey,
        IdempotentEndpoint endpoint,
        ReadOnlyMemory<byte> requestHash,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@DeviceId", deviceId, DbType.Int32);
        parameters.Add("@IdempotencyKey", idempotencyKey, DbType.Guid);
        parameters.Add("@EndpointCode", (byte)endpoint, DbType.Byte);
        parameters.Add("@RequestHash", requestHash.ToArray(), DbType.Binary, size: 32);
        parameters.Add(StoredResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add(StoredPayloadParameter, dbType: DbType.String, direction: ParameterDirection.Output, size: -1);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                TryBeginProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? resultCode = parameters.Get<int?>(ResultCodeParameter);

        return new IdempotencyClaim(
            ToDisposition(resultCode),
            parameters.Get<int?>(StoredResultCodeParameter),
            parameters.Get<string?>(StoredPayloadParameter));
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        int deviceId,
        Guid idempotencyKey,
        int resultCode,
        string? responsePayload,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@DeviceId", deviceId, DbType.Int32);
        parameters.Add("@IdempotencyKey", idempotencyKey, DbType.Guid);
        parameters.Add("@OperationResult", resultCode, DbType.Int32);
        parameters.Add("@ResponsePayload", responsePayload, DbType.String, size: -1);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                CompleteProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Maps the procedure's result code to what the caller should do.
    /// </summary>
    /// <remarks>
    /// An unrecognised or missing code is treated as <b>in progress</b>, not as
    /// permission to proceed. Proceeding on an unknown answer is the one outcome
    /// that could create a duplicate attendance record; asking the client to
    /// retry costs a few seconds and creates nothing.
    /// </remarks>
    private static IdempotencyDisposition ToDisposition(int? resultCode) =>
        resultCode switch
        {
            (int)AttendanceResultCode.Success => IdempotencyDisposition.Proceed,
            (int)AttendanceResultCode.IdempotentReplay => IdempotencyDisposition.ReplayStoredResult,
            (int)AttendanceResultCode.IdempotencyKeyReuse => IdempotencyDisposition.KeyReused,
            _ => IdempotencyDisposition.InProgress,
        };
}
