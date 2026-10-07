using Attendance.Tests;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Attendance.Database.Tests;

/// <summary>An employee with one approved device, created for a single test.</summary>
/// <param name="MobileUserId">Internal key.</param>
/// <param name="DeviceId">Internal key of the approved device.</param>
public sealed record Employee(int MobileUserId, int DeviceId);

/// <summary>
/// Committed fixture data for <see cref="ConcurrencyTests"/>: an office, the
/// attendance settings clock-in requires, and employees created per test.
/// </summary>
/// <remarks>
/// <para>
/// The attendance settings are shared with everything else using this
/// database. They are recorded on the way in and put back exactly on the way
/// out, because the real values are the business owner's decisions (DEC-06: the
/// time zone, the clock-in window, the clock-out rule) and a test that left its
/// own behind would silently change how the system behaves for everyone using
/// that database. The values set here are test data, not business defaults.
/// </para>
/// <para>
/// The connection string comes from <see cref="TestEnvironment"/>: the local
/// developer instance unless <c>CLOCKINXTRA_TEST_CONNECTION</c> points a build
/// agent at its own, with the timeouts a loaded machine needs either way.
/// </para>
/// </remarks>
public sealed class ConcurrencyFixture : IAsyncLifetime
{
    private static readonly string[] SettingKeys =
    [
        "Attendance.BusinessTimeZoneId",
        "Attendance.ClockInOpenTime",
        "Attendance.ClockInCloseTime",
        "Attendance.ClockInAfterCloseAction",
        "Attendance.ClockOutOpenTime",
        "Attendance.ClockOutBeforeOpenAction",
        "Attendance.GracePeriodMinutes",
        "Attendance.MinimumMinutesBeforeClockOut",
    ];

    private readonly string _connectionString = TestEnvironment.ConnectionString;

    private readonly string _prefix = $"dbtest.{Guid.NewGuid():N}"[..20];

    private List<StoredSetting> _originalSettings = [];

    /// <summary>The office every clock-in in these tests is attributed to.</summary>
    public int OfficeLocationId { get; private set; }

    /// <summary>Opens a new, independent connection.</summary>
    public async Task<SqlConnection> OpenAsync()
    {
        SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await using SqlConnection connection = await OpenAsync();

        _originalSettings =
        [
            .. await connection.QueryAsync<StoredSetting>(
                "SELECT SettingKey, SettingValue FROM core.ApplicationSetting WHERE SettingKey IN @Keys",
                new { Keys = SettingKeys }),
        ];

        // UTC keeps "today" unambiguous; the window is wide open so the tests
        // exercise concurrency, not the attendance rules.
        await connection.ExecuteAsync("""
            UPDATE core.ApplicationSetting SET SettingValue = N'UTC'                WHERE SettingKey = 'Attendance.BusinessTimeZoneId';
            UPDATE core.ApplicationSetting SET SettingValue = NULL                  WHERE SettingKey = 'Attendance.ClockInOpenTime';
            UPDATE core.ApplicationSetting SET SettingValue = N'23:59:00'           WHERE SettingKey = 'Attendance.ClockInCloseTime';
            UPDATE core.ApplicationSetting SET SettingValue = N'AcceptAndFlagLate'  WHERE SettingKey = 'Attendance.ClockInAfterCloseAction';
            UPDATE core.ApplicationSetting SET SettingValue = N'00:00:00'           WHERE SettingKey = 'Attendance.ClockOutOpenTime';
            UPDATE core.ApplicationSetting SET SettingValue = N'AcceptAndFlagEarly' WHERE SettingKey = 'Attendance.ClockOutBeforeOpenAction';
            UPDATE core.ApplicationSetting SET SettingValue = NULL                  WHERE SettingKey = 'Attendance.GracePeriodMinutes';
            UPDATE core.ApplicationSetting SET SettingValue = NULL                  WHERE SettingKey = 'Attendance.MinimumMinutesBeforeClockOut';
            """);

        OfficeLocationId = await connection.QuerySingleAsync<int>("""
            INSERT INTO core.OfficeLocation (Name, Latitude, Longitude, AllowedRadiusMeters, Status)
            VALUES (@Name, 6.465422, 3.406448, 5.00, 1);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """,
            new { Name = $"DBTEST {_prefix}" });
    }

    /// <summary>Creates an employee with one approved device, and optionally an active authenticator.</summary>
    public async Task<Employee> CreateEmployeeAsync(bool withAuthenticator = false)
    {
        await using SqlConnection connection = await OpenAsync();

        return await connection.QuerySingleAsync<Employee>("""
            DECLARE @pk VARBINARY(65) = 0x04 + CRYPT_GEN_RANDOM(64);

            INSERT INTO core.MobileUser (UserId, FirstName, LastName, Status)
            VALUES (CONCAT(@Prefix, N'.', LOWER(CONVERT(NVARCHAR(36), NEWID()))), N'Concurrency', N'Test', 1);
            DECLARE @userId INT = SCOPE_IDENTITY();

            INSERT INTO core.Device (MobileUserId, PublicKey, PublicKeyThumbprint, Platform,
                                     AttestationLevel, Status, DeviceModel, ApprovedUtc)
            VALUES (@userId, @pk, HASHBYTES('SHA2_256', @pk), 1, 2, 1, N'CONCURRENCY TEST', SYSUTCDATETIME());
            DECLARE @deviceId INT = SCOPE_IDENTITY();

            IF @WithAuthenticator = 1
                INSERT INTO core.MfaCredential (MobileUserId, SecretProtected, Status, ActivatedUtc)
                VALUES (@userId, 0x00, 1, SYSUTCDATETIME());

            SELECT @userId AS MobileUserId, @deviceId AS DeviceId;
            """,
            new { Prefix = _prefix, WithAuthenticator = withAuthenticator });
    }

    /// <summary>Attendance records for an employee.</summary>
    public async Task<int> CountAttendanceAsync(int mobileUserId)
    {
        await using SqlConnection connection = await OpenAsync();

        return await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM core.Attendance WHERE MobileUserId = @Id", new { Id = mobileUserId });
    }

    /// <summary>Attendance records for an employee, and how many are closed.</summary>
    public async Task<(int Records, int Closed)> CountClosedAsync(int mobileUserId)
    {
        await using SqlConnection connection = await OpenAsync();

        return await connection.QuerySingleAsync<(int, int)>(
            "SELECT COUNT(*), SUM(CASE WHEN Status = 2 AND ClockOutUtc IS NOT NULL THEN 1 ELSE 0 END) FROM core.Attendance WHERE MobileUserId = @Id",
            new { Id = mobileUserId });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await using SqlConnection connection = await OpenAsync();

        await connection.ExecuteAsync("""
            DECLARE @users TABLE (MobileUserId INT PRIMARY KEY);
            INSERT INTO @users SELECT MobileUserId FROM core.MobileUser WHERE UserId LIKE @Pattern;

            DELETE FROM core.AttendanceEvent    WHERE MobileUserId IN (SELECT MobileUserId FROM @users);
            DELETE FROM core.Attendance         WHERE MobileUserId IN (SELECT MobileUserId FROM @users);
            DELETE FROM core.RequestNonce       WHERE DeviceId IN (SELECT DeviceId FROM core.Device WHERE MobileUserId IN (SELECT MobileUserId FROM @users));
            DELETE FROM core.RequestIdempotency WHERE DeviceId IN (SELECT DeviceId FROM core.Device WHERE MobileUserId IN (SELECT MobileUserId FROM @users));
            DELETE FROM core.Device             WHERE MobileUserId IN (SELECT MobileUserId FROM @users);
            DELETE FROM core.MfaCredential      WHERE MobileUserId IN (SELECT MobileUserId FROM @users);
            DELETE FROM core.MobileUser         WHERE MobileUserId IN (SELECT MobileUserId FROM @users);
            DELETE FROM core.OfficeLocation     WHERE OfficeLocationId = @Office;
            """,
            new { Pattern = _prefix + ".%", Office = OfficeLocationId });

        foreach (StoredSetting setting in _originalSettings)
        {
            await connection.ExecuteAsync(
                "UPDATE core.ApplicationSetting SET SettingValue = @SettingValue WHERE SettingKey = @SettingKey",
                setting);
        }
    }

    private sealed class StoredSetting
    {
        public string SettingKey { get; init; } = string.Empty;
        public string? SettingValue { get; init; }
    }
}
