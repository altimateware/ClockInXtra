using System.Data;
using Attendance.Domain.Enums;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Attendance.Database.Tests;

/// <summary>
/// Races against the real stored procedures: many connections, released at the
/// same instant, all trying to do the one thing that may happen only once
/// (Claude.md §28, §45).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are not in a rolled-back transaction</b> like the other
/// integration tests. A race needs separate connections that each commit; one
/// shared transaction would serialise them and prove nothing. Fixture rows are
/// committed under a <c>dbtest.</c> prefix and removed afterwards. Rows the
/// procedures write to the audit ledger cannot be removed — that is what a
/// ledger is — and are harmless test entries.
/// </para>
/// <para>
/// <b>What a pass means.</b> Exactly one attempt succeeds, every other attempt
/// is refused with the specific code for "already done", and nothing else
/// happens: no deadlock victim, no constraint violation surfacing as a 500, no
/// second row. Any of those would be what an employee double-tapping on a slow
/// network, or two IIS nodes behind a load balancer, actually runs into — and
/// §45 is explicit that application-level locking cannot prevent it.
/// </para>
/// </remarks>
public sealed class ConcurrencyTests : IClassFixture<ConcurrencyFixture>
{
    /// <summary>Contenders per race. Enough to overlap on a developer machine.</summary>
    private const int Contenders = 16;

    private readonly ConcurrencyFixture _fixture;

    /// <summary>Creates the test class.</summary>
    public ConcurrencyTests(ConcurrencyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SimultaneousClockInsCreateExactlyOneRecord()
    {
        Employee employee = await _fixture.CreateEmployeeAsync();

        IReadOnlyList<int> results = await RaceAsync(connection =>
            ClockInAsync(connection, employee));

        Assert.Equal(1, results.Count(code => code == 0));
        Assert.All(
            results.Where(code => code != 0),
            code => Assert.Equal((int)AttendanceResultCode.AlreadyClockedIn, code));

        Assert.Equal(1, await _fixture.CountAttendanceAsync(employee.MobileUserId));
    }

    [Fact]
    public async Task SimultaneousClockOutsCloseTheRecordExactlyOnce()
    {
        Employee employee = await _fixture.CreateEmployeeAsync();

        await using (SqlConnection connection = await _fixture.OpenAsync())
        {
            Assert.Equal(0, await ClockInAsync(connection, employee));
        }

        IReadOnlyList<int> results = await RaceAsync(connection =>
            ClockOutAsync(connection, employee));

        Assert.Equal(1, results.Count(code => code == 0));
        Assert.All(
            results.Where(code => code != 0),
            code => Assert.Contains(
                code,
                new[] { (int)AttendanceResultCode.AlreadyClockedOut, (int)AttendanceResultCode.NotClockedIn }));

        (int records, int closed) = await _fixture.CountClosedAsync(employee.MobileUserId);
        Assert.Equal(1, records);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task ASignatureNonceIsAcceptedExactlyOnce()
    {
        // TH-20: a captured request replayed at the same moment as the original,
        // to a different application server.
        Employee employee = await _fixture.CreateEmployeeAsync();
        byte[] nonce = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        DateTime created = DateTime.UtcNow;

        IReadOnlyList<int> results = await RaceAsync(async connection =>
        {
            DynamicParameters parameters = new();
            parameters.Add("@DeviceId", employee.DeviceId, DbType.Int32);
            parameters.Add("@Nonce", nonce, DbType.Binary, size: 32);
            parameters.Add("@SignatureCreatedUtc", created, DbType.DateTime2);
            parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

            await connection.ExecuteAsync(new CommandDefinition(
                "mobile.usp_RequestNonce_TryInsert", parameters, commandType: CommandType.StoredProcedure));

            return parameters.Get<int>("@ResultCode");
        });

        Assert.Equal(1, results.Count(code => code == 0));
        Assert.All(
            results.Where(code => code != 0),
            code => Assert.Equal((int)AttendanceResultCode.ReplayedRequest, code));
    }

    [Fact]
    public async Task AnIdempotencyKeyStartsExactlyOneOperation()
    {
        // The same request arriving twice — a client retry racing its original.
        // One proceeds; the rest are told it is in progress, never allowed to run
        // the operation a second time.
        Employee employee = await _fixture.CreateEmployeeAsync();
        Guid key = Guid.NewGuid();
        byte[] requestHash = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();

        IReadOnlyList<int> results = await RaceAsync(async connection =>
        {
            DynamicParameters parameters = new();
            parameters.Add("@DeviceId", employee.DeviceId, DbType.Int32);
            parameters.Add("@IdempotencyKey", key, DbType.Guid);
            parameters.Add("@EndpointCode", (byte)1, DbType.Byte);
            parameters.Add("@RequestHash", requestHash, DbType.Binary, size: 32);
            parameters.Add("@StoredResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);
            parameters.Add("@StoredPayload", dbType: DbType.String, size: -1, direction: ParameterDirection.Output);
            parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

            await connection.ExecuteAsync(new CommandDefinition(
                "mobile.usp_Idempotency_TryBegin", parameters, commandType: CommandType.StoredProcedure));

            return parameters.Get<int>("@ResultCode");
        });

        Assert.Equal(1, results.Count(code => code == 0));
        Assert.All(
            results.Where(code => code != 0),
            code => Assert.Equal((int)AttendanceResultCode.IdempotentInProgress, code));
    }

    [Fact]
    public async Task AnAuthenticatorTimeStepIsConsumedExactlyOnce()
    {
        // One six-digit code, submitted to several servers at once by someone who
        // watched it being typed. Only one submission may count.
        Employee employee = await _fixture.CreateEmployeeAsync(withAuthenticator: true);
        long timeStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

        IReadOnlyList<int> results = await RaceAsync(async connection =>
        {
            DynamicParameters parameters = new();
            parameters.Add("@MobileUserId", employee.MobileUserId, DbType.Int32);
            parameters.Add("@TimeStep", timeStep, DbType.Int64);
            parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

            await connection.ExecuteAsync(new CommandDefinition(
                "core.usp_MfaCredential_TryConsumeTimeStep", parameters, commandType: CommandType.StoredProcedure));

            return parameters.Get<int>("@ResultCode");
        });

        Assert.Equal(1, results.Count(code => code == 0));
        Assert.All(
            results.Where(code => code != 0),
            code => Assert.Equal((int)AttendanceResultCode.OtpReplayed, code));
    }

    /// <summary>
    /// Opens every connection first, then releases all the attempts at once, so
    /// connection set-up time does not stagger them into a queue.
    /// </summary>
    private async Task<IReadOnlyList<int>> RaceAsync(Func<SqlConnection, Task<int>> attempt)
    {
        List<SqlConnection> connections = [];

        try
        {
            for (int i = 0; i < Contenders; i++)
            {
                connections.Add(await _fixture.OpenAsync());
            }

            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<int>[] attempts =
            [
                .. connections.Select(connection => Task.Run(async () =>
                {
                    await start.Task;
                    return await attempt(connection);
                })),
            ];

            start.SetResult();

            // A deadlock victim or any other SqlException surfaces here and fails
            // the test: under concurrency the procedure must refuse cleanly, not
            // collapse into an error the API would report as INTERNAL_ERROR.
            return await Task.WhenAll(attempts);
        }
        finally
        {
            foreach (SqlConnection connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private async Task<int> ClockInAsync(SqlConnection connection, Employee employee)
    {
        DynamicParameters parameters = new();
        parameters.Add("@MobileUserId", employee.MobileUserId, DbType.Int32);
        parameters.Add("@DeviceId", employee.DeviceId, DbType.Int32);
        parameters.Add("@OfficeLocationId", _fixture.OfficeLocationId, DbType.Int32);
        parameters.Add("@DistanceMeters", 1.5m, DbType.Decimal);
        parameters.Add("@ReportedAccuracyMeters", 3.0m, DbType.Decimal);
        parameters.Add("@Platform", (byte)1, DbType.Byte);
        parameters.Add("@CorrelationId", Guid.NewGuid(), DbType.Guid);
        parameters.Add("@AttendancePublicId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add("@AttendanceDate", dbType: DbType.Date, direction: ParameterDirection.Output);
        parameters.Add("@ClockInUtc", dbType: DbType.DateTime2, direction: ParameterDirection.Output);
        parameters.Add("@IsLateClockIn", dbType: DbType.Boolean, direction: ParameterDirection.Output);
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await connection.ExecuteAsync(new CommandDefinition(
            "mobile.usp_Attendance_ClockIn", parameters, commandType: CommandType.StoredProcedure));

        return parameters.Get<int>("@ResultCode");
    }

    private async Task<int> ClockOutAsync(SqlConnection connection, Employee employee)
    {
        DynamicParameters parameters = new();
        parameters.Add("@MobileUserId", employee.MobileUserId, DbType.Int32);
        parameters.Add("@DeviceId", employee.DeviceId, DbType.Int32);
        parameters.Add("@OfficeLocationId", _fixture.OfficeLocationId, DbType.Int32);
        parameters.Add("@DistanceMeters", 1.5m, DbType.Decimal);
        parameters.Add("@ReportedAccuracyMeters", 3.0m, DbType.Decimal);
        parameters.Add("@Platform", (byte)1, DbType.Byte);
        parameters.Add("@CorrelationId", Guid.NewGuid(), DbType.Guid);
        parameters.Add("@AttendancePublicId", dbType: DbType.Guid, direction: ParameterDirection.Output);
        parameters.Add("@AttendanceDate", dbType: DbType.Date, direction: ParameterDirection.Output);
        parameters.Add("@ClockInUtc", dbType: DbType.DateTime2, direction: ParameterDirection.Output);
        parameters.Add("@ClockOutUtc", dbType: DbType.DateTime2, direction: ParameterDirection.Output);
        parameters.Add("@DurationMinutes", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@IsEarlyClockOut", dbType: DbType.Boolean, direction: ParameterDirection.Output);
        parameters.Add("@ResultCode", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await connection.ExecuteAsync(new CommandDefinition(
            "mobile.usp_Attendance_ClockOut", parameters, commandType: CommandType.StoredProcedure));

        return parameters.Get<int>("@ResultCode");
    }
}
