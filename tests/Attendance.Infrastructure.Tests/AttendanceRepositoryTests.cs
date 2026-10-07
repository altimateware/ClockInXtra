using System.Data;
using Attendance.Application.Abstractions;
using Attendance.Tests;
using Attendance.Domain.Enums;
using Attendance.Infrastructure.Persistence.Connection;
using Attendance.Infrastructure.Persistence.Repositories;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Xunit;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// Integration tests for <see cref="AttendanceRepository"/> against a real
/// SQL Server instance.
/// </summary>
/// <remarks>
/// <para>
/// These deliberately do not mock the database. The stored procedures ARE the
/// contract — the attendance day, the duplicate check, the locking and the
/// authoritative timestamps all live there — so a test with a faked connection
/// would verify nothing that matters (Claude.md §50).
/// </para>
/// <para>
/// The connection string comes from <see cref="TestEnvironment"/>:
/// <c>CLOCKINXTRA_TEST_CONNECTION</c> if a build agent sets it, otherwise the
/// local developer instance, and in both cases with timeouts a loaded machine
/// needs.
/// </para>
/// <para>
/// Every test runs inside its own transaction which is always rolled back, so
/// the database is left exactly as it was found — with one documented
/// exception: rows written to the append-only audit ledger by a committed
/// transaction cannot be removed. Nothing here commits, so nothing is left.
/// </para>
/// </remarks>
public sealed class AttendanceRepositoryTests : IAsyncLifetime
{
    private readonly string _connectionString = TestEnvironment.ConnectionString;

    private SqlConnection _connection = null!;
    private SqlTransaction _transaction = null!;
    private AttendanceRepository _repository = null!;

    private int _mobileUserId;
    private int _deviceId;
    private int _officeLocationId;

    /// <summary>
    /// Creates the fixture rows inside a transaction that is never committed.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        _connection = new SqlConnection(_connectionString);
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        _transaction = (SqlTransaction)await _connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        _repository = new AttendanceRepository(new AmbientTransactionConnectionFactory(_connection, _transaction));

        await ConfigureAttendanceSettingsAsync();
        await CreateFixtureRowsAsync();
    }

    /// <summary>Rolls everything back.</summary>
    public async ValueTask DisposeAsync()
    {
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ClockIn_CreatesARecordAndReturnsTheServerTimestamp()
    {
        ClockInOutcome outcome = await _repository.ClockInAsync(
            NewClockInRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(AttendanceResultCode.Success, outcome.ResultCode);
        Assert.NotNull(outcome.AttendancePublicId);
        Assert.NotNull(outcome.AttendanceDate);
        Assert.NotNull(outcome.ClockInUtc);

        // The timestamp must come back as UTC, not as an unspecified DateTime
        // reinterpreted as local time — the conversion trap documented in
        // SqlConversions.ToUtcOffset.
        Assert.Equal(TimeSpan.Zero, outcome.ClockInUtc!.Value.Offset);
        Assert.True(
            (DateTimeOffset.UtcNow - outcome.ClockInUtc.Value).Duration() < TimeSpan.FromMinutes(5),
            "the server timestamp should be close to now");
    }

    [Fact]
    public async Task ClockIn_RefusesASecondRecordForTheSameDay()
    {
        ClockInOutcome first = await _repository.ClockInAsync(
            NewClockInRequest(), TestContext.Current.CancellationToken);
        Assert.Equal(AttendanceResultCode.Success, first.ResultCode);

        ClockInOutcome second = await _repository.ClockInAsync(
            NewClockInRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(AttendanceResultCode.AlreadyClockedIn, second.ResultCode);
        Assert.Null(second.AttendancePublicId);
    }

    [Fact]
    public async Task GetCurrentStatus_ReportsClockedInAfterAClockIn()
    {
        AttendanceStatus before = await _repository.GetCurrentStatusAsync(
            _mobileUserId, TestContext.Current.CancellationToken);
        Assert.Equal(AttendanceResultCode.Success, before.ResultCode);
        Assert.Equal(AttendanceState.NotClockedIn, before.State);

        await _repository.ClockInAsync(NewClockInRequest(), TestContext.Current.CancellationToken);

        AttendanceStatus after = await _repository.GetCurrentStatusAsync(
            _mobileUserId, TestContext.Current.CancellationToken);

        Assert.Equal(AttendanceState.ClockedIn, after.State);
        Assert.NotNull(after.ClockInUtc);
        Assert.Null(after.ClockOutUtc);
    }

    [Fact]
    public async Task ClockOut_ClosesTheRecordAndComputesDuration()
    {
        await _repository.ClockInAsync(NewClockInRequest(), TestContext.Current.CancellationToken);

        ClockOutOutcome outcome = await _repository.ClockOutAsync(
            NewClockOutRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(AttendanceResultCode.Success, outcome.ResultCode);
        Assert.NotNull(outcome.ClockOutUtc);
        Assert.NotNull(outcome.DurationMinutes);

        // Duration is computed by the database from its own timestamps, never
        // from anything the client supplied.
        Assert.True(outcome.DurationMinutes >= 0);
    }

    [Fact]
    public async Task ClockOut_RefusesWhenThereIsNoOpenRecord()
    {
        ClockOutOutcome outcome = await _repository.ClockOutAsync(
            NewClockOutRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(AttendanceResultCode.NotClockedIn, outcome.ResultCode);
        Assert.Null(outcome.ClockOutUtc);
    }

    [Fact]
    public async Task ClockIn_RefusesWhenAMandatoryBusinessSettingIsUnset()
    {
        // The central safeguard: with the business timezone undecided the system
        // refuses rather than assuming one (§15, §68).
        await ExecuteAsync(
            "UPDATE core.ApplicationSetting SET SettingValue = NULL WHERE SettingKey = 'Attendance.BusinessTimeZoneId';");

        ClockInOutcome outcome = await _repository.ClockInAsync(
            NewClockInRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(AttendanceResultCode.AttendanceNotConfigured, outcome.ResultCode);
        Assert.Null(outcome.AttendancePublicId);
    }

    private ClockInRequest NewClockInRequest() =>
        new(_mobileUserId, _deviceId, NewEvidence(), Guid.NewGuid());

    private ClockOutRequest NewClockOutRequest() =>
        new(_mobileUserId, _deviceId, NewEvidence(), Guid.NewGuid());

    private LocationEvidence NewEvidence() =>
        new(_officeLocationId,
            DistanceMeters: 2.50m,
            ReportedAccuracyMeters: 4.00m,
            Platform: 1,
            WasMockedLocation: false,
            CoordinatesProtected: null);

    private async Task ConfigureAttendanceSettingsAsync()
    {
        // 'UTC' keeps the test deterministic. It is test data, not a business
        // default: the real value is OPEN-5.
        await ExecuteAsync("""
            UPDATE core.ApplicationSetting SET SettingValue = N'UTC'               WHERE SettingKey = 'Attendance.BusinessTimeZoneId';
            UPDATE core.ApplicationSetting SET SettingValue = N'23:59:00'          WHERE SettingKey = 'Attendance.ClockInCloseTime';
            UPDATE core.ApplicationSetting SET SettingValue = N'AcceptAndFlagLate' WHERE SettingKey = 'Attendance.ClockInAfterCloseAction';
            UPDATE core.ApplicationSetting SET SettingValue = N'00:00:00'          WHERE SettingKey = 'Attendance.ClockOutOpenTime';
            UPDATE core.ApplicationSetting SET SettingValue = N'AcceptAndFlagEarly' WHERE SettingKey = 'Attendance.ClockOutBeforeOpenAction';
            UPDATE core.ApplicationSetting SET SettingValue = NULL                 WHERE SettingKey = 'Attendance.ClockInOpenTime';
            UPDATE core.ApplicationSetting SET SettingValue = NULL                 WHERE SettingKey = 'Attendance.GracePeriodMinutes';
            UPDATE core.ApplicationSetting SET SettingValue = NULL                 WHERE SettingKey = 'Attendance.MinimumMinutesBeforeClockOut';
            """);
    }

    private async Task CreateFixtureRowsAsync()
    {
        const string sql = """
            DECLARE @pk VARBINARY(65) = 0x04 + CRYPT_GEN_RANDOM(64);

            INSERT INTO core.MobileUser (UserId, FirstName, LastName, Status)
            VALUES (CONCAT(N'itest.', LOWER(CONVERT(NVARCHAR(36), NEWID()))), N'Integration', N'Test', 1);
            DECLARE @userId INT = SCOPE_IDENTITY();

            INSERT INTO core.OfficeLocation (Name, Latitude, Longitude, AllowedRadiusMeters, Status)
            VALUES (CONCAT(N'ITEST ', CONVERT(NVARCHAR(36), NEWID())), 6.465422, 3.406448, 5.00, 1);
            DECLARE @officeId INT = SCOPE_IDENTITY();

            INSERT INTO core.Device (MobileUserId, PublicKey, PublicKeyThumbprint, Platform,
                                     AttestationLevel, Status, DeviceModel, ApprovedUtc)
            VALUES (@userId, @pk, HASHBYTES('SHA2_256', @pk), 1, 2, 1, N'INTEGRATION TEST', SYSUTCDATETIME());
            DECLARE @deviceId INT = SCOPE_IDENTITY();

            SELECT @userId AS MobileUserId, @deviceId AS DeviceId, @officeId AS OfficeLocationId;
            """;

        FixtureRow row = await _connection.QuerySingleAsync<FixtureRow>(
            new CommandDefinition(sql, transaction: _transaction,
                cancellationToken: TestContext.Current.CancellationToken));

        _mobileUserId = row.MobileUserId;
        _deviceId = row.DeviceId;
        _officeLocationId = row.OfficeLocationId;
    }

    private async Task ExecuteAsync(string sql) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(sql, transaction: _transaction,
                cancellationToken: TestContext.Current.CancellationToken));

    private sealed class FixtureRow
    {
        public int MobileUserId { get; init; }
        public int DeviceId { get; init; }
        public int OfficeLocationId { get; init; }
    }

    /// <summary>
    /// Hands the repository the test's own connection and transaction.
    /// </summary>
    /// <remarks>
    /// This is what lets every test roll back cleanly, and it doubles as a live
    /// check of decision DB-13: the repository's procedures run inside a
    /// transaction this factory owns, so any procedure still using a bare
    /// ROLLBACK would destroy the test's transaction and the failure would be
    /// immediate and obvious.
    /// </remarks>
    private sealed class AmbientTransactionConnectionFactory : ISqlConnectionFactory
    {
        private readonly SqlConnection _connection;
        private readonly SqlTransaction _transaction;

        public AmbientTransactionConnectionFactory(SqlConnection connection, SqlTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public int CommandTimeoutSeconds => 30;

        /// <summary>
        /// Lends the test's connection and transaction, and keeps ownership.
        /// </summary>
        /// <remarks>
        /// Borrowed, never owned: if the repository disposed this connection the
        /// next test would run against a closed one. ADO.NET also refuses to
        /// execute a command on a connection with a pending local transaction
        /// unless that transaction is supplied, which is why the lease carries
        /// it rather than the repository guessing.
        /// </remarks>
        public Task<SqlConnectionLease> LeaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SqlConnectionLease.Borrowed(_connection, _transaction));
    }
}
