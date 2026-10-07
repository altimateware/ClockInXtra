using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Dapper;

namespace Attendance.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IMobileUserRepository"/>.
/// </summary>
/// <remarks>
/// Stored procedure only, typed parameters, no SQL text (Claude.md §4).
/// </remarks>
public sealed class MobileUserRepository : IMobileUserRepository
{
    private const string GetForAuthenticationProcedure = "mobile.usp_MobileUser_GetForAuthentication";
    private const string ResultCodeParameter = "@ResultCode";

    private readonly ISqlConnectionFactory _connectionFactory;

    /// <summary>Creates the repository.</summary>
    public MobileUserRepository(ISqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<MobileUserAuthenticationRecord?> GetForAuthenticationAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);

        DynamicParameters parameters = new();

        // Size stated explicitly to match core.MobileUser.UserId (nvarchar(64)).
        // An inferred nvarchar(4000) would compare against a different type and
        // can cost the index seek on a lookup that runs on every clock-in.
        parameters.Add("@UserId", userId, DbType.String, size: 64);
        parameters.Add(ResultCodeParameter, dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using SqlConnectionLease lease =
            await _connectionFactory.LeaseAsync(cancellationToken).ConfigureAwait(false);

        AuthenticationRow? row = await lease.Connection
            .QuerySingleOrDefaultAsync<AuthenticationRow>(
                lease.StoredProcedure(
                    GetForAuthenticationProcedure,
                    parameters,
                    _connectionFactory.CommandTimeoutSeconds,
                    cancellationToken))
            .ConfigureAwait(false);

        int? resultCode = parameters.Get<int?>(ResultCodeParameter);

        // 1010 means no such employee. The procedure returns no row, and the
        // caller must still perform a dummy hash so an unknown identifier does
        // not answer faster than a known one (threat TH-14).
        if (row is null || resultCode == (int)AttendanceResultCode.InvalidCredentials)
        {
            return null;
        }

        PasswordHash? credential = row.HashFormat is null || row.Salt is null || row.PasswordHash is null
            ? null
            : new PasswordHash(row.HashFormat, row.Iterations ?? 0, row.Salt, row.PasswordHash);

        MfaEnrolment? mfa = row.MfaCredentialId is null || row.MfaSecretProtected is null
            ? null
            : new MfaEnrolment(
                row.MfaCredentialId.Value,
                row.MfaSecretProtected,
                new TotpParameters(
                    ParseAlgorithm(row.MfaAlgorithm),
                    row.MfaDigits ?? 6,
                    row.MfaPeriodSeconds ?? 30),
                row.MfaLastAcceptedTimeStep);

        return new MobileUserAuthenticationRecord(
            row.MobileUserId,
            row.MobileUserPublicId,
            row.UserId,
            // Status 1 is Active. Anything else — inactive, suspended — means the
            // employee may not clock in, and the API reports it as invalid
            // credentials so the response cannot be used to probe account state.
            row.MobileUserStatus == 1,
            credential,
            mfa);
    }

    private static TotpAlgorithm ParseAlgorithm(string? algorithm) => algorithm switch
    {
        "SHA256" => TotpAlgorithm.Sha256,
        "SHA512" => TotpAlgorithm.Sha512,
        _ => TotpAlgorithm.Sha1,
    };

    /// <summary>
    /// Shape of the row returned by
    /// <c>mobile.usp_MobileUser_GetForAuthentication</c>.
    /// </summary>
    private sealed class AuthenticationRow
    {
        public int MobileUserId { get; init; }
        public Guid MobileUserPublicId { get; init; }
        public string UserId { get; init; } = string.Empty;
        public byte MobileUserStatus { get; init; }

        public string? HashFormat { get; init; }
        public int? Iterations { get; init; }
        public byte[]? Salt { get; init; }
        public byte[]? PasswordHash { get; init; }
        public bool? MustChange { get; init; }

        public int? MfaCredentialId { get; init; }
        public byte[]? MfaSecretProtected { get; init; }
        public string? MfaAlgorithm { get; init; }
        public byte? MfaDigits { get; init; }
        public short? MfaPeriodSeconds { get; init; }
        public byte? MfaStatus { get; init; }
        public long? MfaLastAcceptedTimeStep { get; init; }
    }
}
