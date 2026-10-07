using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Domain.ValueObjects;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IDeviceRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// Covers the <c>mobile</c> device procedures only. Approval and revocation live
/// in the <c>admin</c> schema, which the mobile application's database account is
/// explicitly denied (threat TH-28) — so they are not on this port, and cannot be
/// reached from the internet-facing process even by mistake.
/// </para>
/// <para>
/// <b>Nothing here is cached.</b> The signature-verification lookup runs on every
/// signed request precisely so that a revoked device stops working immediately
/// (§18). It is a single index seek on a unique key.
/// </para>
/// </remarks>
public sealed class DeviceRepository : IDeviceRepository
{
    private const string IssueChallengeProcedure = "mobile.usp_Device_IssueRegistrationChallenge";
    private const string RegisterProcedure = "mobile.usp_Device_Register";
    private const string GetStatusProcedure = "mobile.usp_Device_GetStatus";
    private const string GetForSignatureProcedure = "mobile.usp_Device_GetForSignatureVerification";
    private const string TouchLastSeenProcedure = "mobile.usp_Device_TouchLastSeen";

    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public DeviceRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<RegistrationChallenge> IssueRegistrationChallengeAsync(
        ReadOnlyMemory<byte> challenge,
        int lifetimeSeconds,
        byte[]? issuedToIpHash,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@Challenge", challenge.ToArray(), DbType.Binary, size: 32);
        parameters.Add("@LifetimeSeconds", lifetimeSeconds, DbType.Int32);
        parameters.Add("@IssuedToIpHash", issuedToIpHash, DbType.Binary, size: 32);
        parameters.Add("@ChallengeId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add("@ExpiresUtc", dbType: DbType.DateTime2, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await ExecuteAsync(IssueChallengeProcedure, parameters, cancellationToken).ConfigureAwait(false);

        return new RegistrationChallenge(
            ReadResultCode(parameters),
            parameters.Get<Guid?>("@ChallengeId") ?? Guid.Empty,
            parameters.Get<DateTime?>("@ExpiresUtc").ToUtcOffset() ?? default);
    }

    /// <inheritdoc />
    public async Task<DeviceRegistrationOutcome> RegisterAsync(
        DeviceRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@ChallengeId", request.ChallengeId, DbType.Guid);
        parameters.Add("@Challenge", request.Challenge.ToArray(), DbType.Binary, size: 32);
        parameters.Add("@MobileUserId", request.MobileUserId, DbType.Int32);
        parameters.Add("@PublicKey", request.PublicKey, DbType.Binary, size: 65);
        parameters.Add("@PublicKeyThumbprint", request.PublicKeyThumbprint, DbType.Binary, size: 32);
        parameters.Add("@Platform", (byte)request.Platform, DbType.Byte);
        parameters.Add("@AttestationLevel", (byte)request.AttestationLevel, DbType.Byte);
        parameters.Add("@DeviceModel", request.DeviceModel, DbType.String, size: 64);
        parameters.Add("@OsVersion", request.OsVersion, DbType.String, size: 32);
        parameters.Add("@AppVersion", request.AppVersion, DbType.String, size: 32);
        parameters.Add("@RequiresApproval", request.RequiresApproval, DbType.Boolean);
        parameters.Add("@CorrelationId", request.CorrelationId, DbType.Guid);
        parameters.Add("@DevicePublicId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add("@DeviceStatus", dbType: DbType.Byte, direction: ParameterDirection.Output);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await ExecuteAsync(RegisterProcedure, parameters, cancellationToken).ConfigureAwait(false);

        byte? status = parameters.Get<byte?>("@DeviceStatus");

        return new DeviceRegistrationOutcome(
            ReadResultCode(parameters),
            parameters.Get<Guid?>("@DevicePublicId"),
            status is null ? null : (DeviceStatus)status.Value);
    }

    /// <inheritdoc />
    public async Task<DeviceStatusRecord?> GetStatusAsync(
        Guid devicePublicId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@DevicePublicId", devicePublicId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        StatusRow? row = await lease.Connection
            .QuerySingleOrDefaultAsync<StatusRow>(lease.StoredProcedure(
                GetStatusProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        // No row means no such device: the procedure returns 1020 and selects
        // nothing, so the caller gets null rather than a half-populated record.
        if (row is null)
        {
            return null;
        }

        return new DeviceStatusRecord(
            ReadResultCode(parameters),
            row.DevicePublicId,
            (DeviceStatus)row.DeviceStatus,
            (AttestationLevel)row.AttestationLevel,
            row.RegisteredUtc.ToUtcOffset(),
            row.ApprovedUtc.ToUtcOffset(),
            row.RevokedUtc.ToUtcOffset(),
            row.RevokedReason,
            row.MobileUserPublicId,
            row.UserId,
            row.EmployeeActive,
            row.ServerTimeUtc.ToUtcOffset());
    }

    /// <inheritdoc />
    public async Task<DeviceVerificationRecord?> GetForSignatureVerificationAsync(
        Guid devicePublicId,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@DevicePublicId", devicePublicId, DbType.Guid);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        VerificationRow? row = await lease.Connection
            .QuerySingleOrDefaultAsync<VerificationRow>(lease.StoredProcedure(
                GetForSignatureProcedure, parameters, _connectionFactory.CommandTimeoutSeconds, cancellationToken))
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        // The row is returned even when the device may not act, so the API can
        // write a meaningful security event. The result code is the authority.
        return new DeviceVerificationRecord(
            ReadResultCode(parameters),
            row.DeviceId,
            row.DevicePublicId,
            row.MobileUserId,
            row.MobileUserPublicId,
            row.UserId,
            row.PublicKey,
            (DeviceStatus)row.DeviceStatus,
            (AttestationLevel)row.AttestationLevel,
            (DevicePlatform)row.Platform,
            row.AppVersion);
    }

    /// <inheritdoc />
    public async Task TouchLastSeenAsync(
        int deviceId,
        string? appVersion,
        string? osVersion,
        CancellationToken cancellationToken)
    {
        DynamicParameters parameters = new();
        parameters.Add("@DeviceId", deviceId, DbType.Int32);
        parameters.Add("@AppVersion", appVersion, DbType.String, size: 32);
        parameters.Add("@OsVersion", osVersion, DbType.String, size: 32);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await ExecuteAsync(TouchLastSeenProcedure, parameters, cancellationToken).ConfigureAwait(false);
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

    private static AttendanceResultCode ReadResultCode(DynamicParameters parameters)
    {
        int? value = parameters.Get<int?>(ResultCodeParameter);

        return value is null
            ? AttendanceResultCode.InternalError
            : (AttendanceResultCode)value.Value;
    }

    /// <summary>Shape of the row returned by <c>mobile.usp_Device_GetStatus</c>.</summary>
    private sealed class StatusRow
    {
        public Guid DevicePublicId { get; init; }
        public byte DeviceStatus { get; init; }
        public byte AttestationLevel { get; init; }
        public DateTime RegisteredUtc { get; init; }
        public DateTime? ApprovedUtc { get; init; }
        public DateTime? RevokedUtc { get; init; }
        public string? RevokedReason { get; init; }
        public Guid MobileUserPublicId { get; init; }
        public string UserId { get; init; } = string.Empty;
        public bool EmployeeActive { get; init; }
        public DateTime ServerTimeUtc { get; init; }
    }

    /// <summary>
    /// Shape of the row returned by
    /// <c>mobile.usp_Device_GetForSignatureVerification</c>.
    /// </summary>
    private sealed class VerificationRow
    {
        public int DeviceId { get; init; }
        public Guid DevicePublicId { get; init; }
        public int MobileUserId { get; init; }
        public Guid MobileUserPublicId { get; init; }
        public string UserId { get; init; } = string.Empty;
        public byte[] PublicKey { get; init; } = [];
        public byte DeviceStatus { get; init; }
        public byte AttestationLevel { get; init; }
        public byte Platform { get; init; }
        public string? AppVersion { get; init; }
    }
}
