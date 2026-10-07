using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="ISecurityEventRecorder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Writes to <c>audit.SecurityEvent</c>, an append-only ledger table. Rows cannot
/// be altered or removed afterwards, including by a database administrator, which
/// is the property that makes the trail worth keeping (threat TH-40).
/// </para>
/// <para>
/// <b>Recording an event must never be the reason a request fails.</b> If the
/// audit insert throws, the caller is in the middle of answering an employee who
/// is standing at a door. Losing one event row is bad; refusing a legitimate
/// clock-in because the trail was briefly unavailable is worse, and it would also
/// hand an attacker a way to disable the endpoint by filling a disk. The
/// exception is therefore contained here — and deliberately not swallowed
/// silently: it is rethrown as a logged failure only when the caller opted into
/// strict mode, which nothing does today.
/// </para>
/// <para>
/// What must never reach this method: passwords, authenticator codes, TOTP
/// secrets, signatures, private keys or exact coordinates. That is enforced by
/// review at every call site, because no runtime check can recognise a secret
/// inside a free-text reason code.
/// </para>
/// </remarks>
public sealed class SecurityEventRecorder : ISecurityEventRecorder
{
    private const string CreateProcedure = "core.usp_SecurityEvent_Create";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the recorder.</summary>
    public SecurityEventRecorder(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();

        parameters.Add("@EventType", securityEvent.EventType, DbType.AnsiString, size: 64);
        parameters.Add("@Severity", (byte)securityEvent.Severity, DbType.Byte);
        parameters.Add("@SubjectType", (byte)securityEvent.Subject, DbType.Byte);
        parameters.Add("@SubjectKey", securityEvent.SubjectKey, DbType.String, size: 128);
        parameters.Add("@DevicePublicId", securityEvent.DevicePublicId, DbType.Guid);
        parameters.Add("@ReasonCode", securityEvent.ReasonCode, DbType.AnsiString, size: 64);
        parameters.Add("@SourceApplication", securityEvent.SourceApplication, DbType.AnsiString, size: 32);
        parameters.Add("@SourceAddressHash", securityEvent.SourceAddressHash, DbType.Binary, size: 32);
        parameters.Add("@CorrelationId", securityEvent.CorrelationId, DbType.Guid);
        parameters.Add("@Details", securityEvent.Details, DbType.String, size: 2000);
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        await lease.Connection
            .ExecuteAsync(lease.StoredProcedure(
                CreateProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);
    }
}
