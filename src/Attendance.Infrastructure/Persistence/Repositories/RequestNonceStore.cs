using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="INonceStore"/>.
/// </summary>
public sealed class RequestNonceStore : INonceStore
{
    private const string TryInsertProcedure = "mobile.usp_RequestNonce_TryInsert";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the store.</summary>
    public RequestNonceStore(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<AttendanceResultCode> TryClaimAsync(
        int deviceId,
        ReadOnlyMemory<byte> nonce,
        DateTimeOffset signatureCreatedUtc,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@DeviceId", deviceId, DbType.Int32);
        parameters.Add("@Nonce", nonce.ToArray(), DbType.Binary, size: 32);
        parameters.Add("@SignatureCreatedUtc", signatureCreatedUtc.UtcDateTime, DbType.DateTime2);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                TryInsertProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        int? value = parameters.Get<int?>(ResultCodeParameter);

        // No code means the procedure failed unexpectedly. Treating that as a
        // fresh nonce would disable replay protection, so it is reported as a
        // replay — failing closed on the one control that has no second line.
        return value is null
            ? AttendanceResultCode.ReplayedRequest
            : (AttendanceResultCode)value.Value;
    }
}
