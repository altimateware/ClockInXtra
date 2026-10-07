using Attendance.Application.Abstractions;
using Attendance.Tests;
using Attendance.Domain.Enums;
using Attendance.Domain.Services;
using Attendance.Domain.ValueObjects;
using Attendance.Infrastructure.Persistence.Connection;
using Attendance.Infrastructure.Persistence.Repositories;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// Integration tests for the Phase 9 repositories, against a real SQL Server
/// instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these exist to catch.</b> Every one of these classes compiles whether
/// or not its parameter names match the stored procedure's. A repository that
/// passes <c>@Subject</c> where the procedure declares <c>@SubjectType</c> builds
/// cleanly and throws the first time an employee tries to clock in. Only
/// executing them against the real procedures proves the chain, which is why
/// these tests are not written against a faked connection (§50).
/// </para>
/// <para>
/// Everything runs inside one transaction that is always rolled back, so the
/// database is left as it was found — and, as with the attendance tests, that
/// doubles as a live check of decision DB-13.
/// </para>
/// </remarks>
public sealed class MobileRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly string _connectionString = TestEnvironment.ConnectionString;

    private SqlConnection _connection = null!;
    private SqlTransaction _transaction = null!;
    private ISqlConnectionFactory _factory = null!;

    private int _mobileUserId;
    private int _deviceId;
    private Guid _devicePublicId;
    private int _officeLocationId;
    private int _userWithoutDeviceId;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _connection = new SqlConnection(_connectionString);
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        _transaction = (SqlTransaction)await _connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        _factory = new AmbientTransactionConnectionFactory(_connection, _transaction);

        await CreateFixtureRowsAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- Policy provider ---------------------------------------------------

    [Fact]
    public async Task PolicyProvider_ReadsAndParsesTheSeededSecurityPolicy()
    {
        using AttendancePolicyProvider provider = new(_factory);

        AttendancePolicy policy = await provider.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LocationAccuracyPolicy.DistanceAndAccuracyThreshold, policy.AccuracyPolicy);
        Assert.Equal(5, policy.MobileLockoutThreshold);
        Assert.Equal(15, policy.MobileLockoutMinutes);
        Assert.Equal(120, policy.SignatureSkewSeconds);
        Assert.Equal(300, policy.ChallengeLifetimeSeconds);
        Assert.True(policy.RejectUntrustedLocationSource);
        Assert.True(policy.CollapseCredentialErrorCodes);
        Assert.True(policy.RequireHardwareAttestationAndroid);
        Assert.True(policy.RequireHardwareAttestationIos);
        Assert.True(policy.DeviceRegistrationRequiresApproval);

        // A decided accuracy threshold arrives as its value (DEC-09 set 20 m on
        // the development database), an undecided one as null rather than an
        // invented default (§15, §68). Both are set here, inside the rolled-back
        // transaction, so the test does not depend on what a database has decided.
        await ExecuteAsync(
            "UPDATE core.ApplicationSetting SET SettingValue = N'20' WHERE SettingKey = 'Location.MaxAcceptedAccuracyMeters';");
        using (AttendancePolicyProvider decided = new(_factory))
        {
            Assert.Equal(20d, (await decided.GetAsync(TestContext.Current.CancellationToken)).MaxAcceptedAccuracyMeters);
        }

        await ExecuteAsync(
            "UPDATE core.ApplicationSetting SET SettingValue = NULL WHERE SettingKey IN ('Location.MaxAcceptedAccuracyMeters', 'Mobile.MinimumAppVersion');");
        using (AttendancePolicyProvider undecided = new(_factory))
        {
            AttendancePolicy unset = await undecided.GetAsync(TestContext.Current.CancellationToken);
            Assert.Null(unset.MaxAcceptedAccuracyMeters);
            Assert.Null(unset.MinimumAppVersion);
        }
    }

    [Fact]
    public async Task PolicyProvider_FallsBackToTheRestrictiveValueWhenASettingIsUnparseable()
    {
        await ExecuteAsync(
            "UPDATE core.ApplicationSetting SET SettingValue = N'not-a-number' WHERE SettingKey = 'Security.MobileLockoutThreshold';");

        using AttendancePolicyProvider provider = new(_factory);
        AttendancePolicy policy = await provider.GetAsync(TestContext.Current.CancellationToken);

        // A configuration problem must make the system stricter, never more
        // permissive: a silently huge threshold would disable lockout.
        Assert.Equal(5, policy.MobileLockoutThreshold);
    }

    // ---- Office locations --------------------------------------------------

    [Fact]
    public async Task OfficeLocations_ReturnTheActiveOfficeWithItsRadius()
    {
        using OfficeLocationRepository repository = new(_factory);

        IReadOnlyList<OfficeLocationCandidate> offices =
            await repository.GetActiveAsync(TestContext.Current.CancellationToken);

        OfficeLocationCandidate fixture = Assert.Single(
            offices, o => o.OfficeLocationId == _officeLocationId);

        Assert.Equal(5d, fixture.AllowedRadiusMeters);
        Assert.Equal(6.465422d, fixture.Coordinates.Latitude, precision: 6);
    }

    [Fact]
    public async Task OfficeLocations_ExcludeADisabledOffice()
    {
        using OfficeLocationRepository repository = new(_factory);

        await ExecuteAsync($"UPDATE core.OfficeLocation SET Status = 0 WHERE OfficeLocationId = {_officeLocationId};");
        repository.InvalidateCache();

        IReadOnlyList<OfficeLocationCandidate> offices =
            await repository.GetActiveAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(offices, o => o.OfficeLocationId == _officeLocationId);
    }

    // ---- Security events ---------------------------------------------------

    [Fact]
    public async Task SecurityEvents_AreWrittenToTheLedger()
    {
        SecurityEventRecorder recorder = new(_factory);
        Guid correlationId = Guid.NewGuid();

        await recorder.RecordAsync(
            new SecurityEvent(
                "Auth.Failed",
                SecurityEventSeverity.Warning,
                SecurityEventSubject.MobileUser,
                "itest.subject",
                "INVALID_OTP",
                "Attendance.Api",
                DevicePublicId: _devicePublicId,
                SourceAddressHash: new byte[32],
                CorrelationId: correlationId),
            TestContext.Current.CancellationToken);

        int rows = await _connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit.SecurityEvent WHERE CorrelationId = @correlationId;",
            new { correlationId }, _transaction, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, rows);
    }

    // ---- Lockout -----------------------------------------------------------

    [Fact]
    public async Task Lockout_CountsFailuresLocksAtTheThresholdAndResetClears()
    {
        AuthenticationAttemptRepository repository = new(_factory);
        string subject = $"itest.{Guid.NewGuid():N}";

        LockoutState initial = await repository.CheckAsync(
            AuthenticationSubject.MobileUser, subject, TestContext.Current.CancellationToken);
        Assert.False(initial.IsLockedOut);

        LockoutState state = default;

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            state = await repository.RegisterFailureAsync(
                AuthenticationSubject.MobileUser, subject, threshold: 5, lockoutMinutes: 15,
                TestContext.Current.CancellationToken);
        }

        Assert.True(state.IsLockedOut);
        Assert.Equal(5, state.FailedCount);
        Assert.NotNull(state.LockedUntilUtc);

        LockoutState locked = await repository.CheckAsync(
            AuthenticationSubject.MobileUser, subject, TestContext.Current.CancellationToken);
        Assert.True(locked.IsLockedOut);

        await repository.ResetAsync(
            AuthenticationSubject.MobileUser, subject, TestContext.Current.CancellationToken);

        LockoutState cleared = await repository.CheckAsync(
            AuthenticationSubject.MobileUser, subject, TestContext.Current.CancellationToken);
        Assert.False(cleared.IsLockedOut);
        Assert.Null(cleared.LockedUntilUtc);
    }

    // ---- Authenticator time steps ------------------------------------------

    [Fact]
    public async Task TimeStep_IsConsumedOnceAndRefusedTheSecondTime()
    {
        MfaCredentialRepository repository = new(_factory);
        const long TimeStep = 58_000_000L;

        AttendanceResultCode first = await repository.TryConsumeTimeStepAsync(
            _mobileUserId, TimeStep, TestContext.Current.CancellationToken);
        Assert.Equal(AttendanceResultCode.Success, first);

        AttendanceResultCode second = await repository.TryConsumeTimeStepAsync(
            _mobileUserId, TimeStep, TestContext.Current.CancellationToken);
        Assert.Equal(AttendanceResultCode.OtpReplayed, second);
    }

    [Fact]
    public async Task TimeStep_ReportsMfaNotEnrolledForAnEmployeeWithoutAnAuthenticator()
    {
        MfaCredentialRepository repository = new(_factory);

        AttendanceResultCode result = await repository.TryConsumeTimeStepAsync(
            _userWithoutDeviceId, 58_000_001L, TestContext.Current.CancellationToken);

        Assert.Equal(AttendanceResultCode.MfaNotEnrolled, result);
    }

    // ---- Idempotency -------------------------------------------------------

    [Fact]
    public async Task Idempotency_DistinguishesFirstAttemptInFlightReplayAndKeyReuse()
    {
        IdempotencyStore store = new(_factory);
        Guid key = Guid.NewGuid();
        byte[] hash = new byte[32];
        hash[0] = 0x11;

        IdempotencyClaim first = await store.TryBeginAsync(
            _deviceId, key, IdempotentEndpoint.ClockIn, hash, TestContext.Current.CancellationToken);
        Assert.Equal(IdempotencyDisposition.Proceed, first.Disposition);

        IdempotencyClaim inFlight = await store.TryBeginAsync(
            _deviceId, key, IdempotentEndpoint.ClockIn, hash, TestContext.Current.CancellationToken);
        Assert.Equal(IdempotencyDisposition.InProgress, inFlight.Disposition);

        await store.CompleteAsync(
            _deviceId, key, (int)AttendanceResultCode.Success, "{\"ok\":true}", TestContext.Current.CancellationToken);

        IdempotencyClaim replay = await store.TryBeginAsync(
            _deviceId, key, IdempotentEndpoint.ClockIn, hash, TestContext.Current.CancellationToken);
        Assert.Equal(IdempotencyDisposition.ReplayStoredResult, replay.Disposition);
        Assert.Equal((int)AttendanceResultCode.Success, replay.StoredResultCode);
        Assert.Equal("{\"ok\":true}", replay.StoredResponsePayload);

        byte[] differentHash = new byte[32];
        differentHash[0] = 0x22;

        IdempotencyClaim reused = await store.TryBeginAsync(
            _deviceId, key, IdempotentEndpoint.ClockIn, differentHash, TestContext.Current.CancellationToken);
        Assert.Equal(IdempotencyDisposition.KeyReused, reused.Disposition);
        Assert.Null(reused.StoredResultCode);
    }

    // ---- Devices -----------------------------------------------------------

    [Fact]
    public async Task Device_IssuesAChallengeThatExpiresInTheFuture()
    {
        DeviceRepository repository = new(_factory);

        RegistrationChallenge challenge = await repository.IssueRegistrationChallengeAsync(
            new byte[32], lifetimeSeconds: 300, issuedToIpHash: null, TestContext.Current.CancellationToken);

        Assert.Equal(AttendanceResultCode.Success, challenge.ResultCode);
        Assert.NotEqual(Guid.Empty, challenge.ChallengeId);
        Assert.Equal(TimeSpan.Zero, challenge.ExpiresUtc.Offset);
        Assert.True(challenge.ExpiresUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Device_ReportsItsOwnStatus()
    {
        DeviceRepository repository = new(_factory);

        DeviceStatusRecord? record = await repository.GetStatusAsync(
            _devicePublicId, TestContext.Current.CancellationToken);

        Assert.NotNull(record);
        Assert.Equal(AttendanceResultCode.Success, record!.Value.ResultCode);
        Assert.Equal(DeviceStatus.Active, record.Value.Status);
        Assert.Equal(AttestationLevel.Hardware, record.Value.AttestationLevel);
        Assert.True(record.Value.EmployeeActive);
    }

    [Fact]
    public async Task Device_ReturnsNullForAnUnknownDevice()
    {
        DeviceRepository repository = new(_factory);

        DeviceStatusRecord? record = await repository.GetStatusAsync(
            Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Null(record);
    }

    [Fact]
    public async Task Device_ReturnsTheKeyMaterialForSignatureVerification()
    {
        DeviceRepository repository = new(_factory);

        DeviceVerificationRecord? record = await repository.GetForSignatureVerificationAsync(
            _devicePublicId, TestContext.Current.CancellationToken);

        Assert.NotNull(record);
        Assert.Equal(AttendanceResultCode.Success, record!.Value.ResultCode);
        Assert.Equal(_deviceId, record.Value.DeviceId);
        Assert.Equal(_mobileUserId, record.Value.MobileUserId);
        Assert.Equal(DevicePlatform.Android, record.Value.Platform);

        // 65-byte uncompressed P-256 point, exactly as stored.
        Assert.Equal(65, record.Value.PublicKey.Length);
        Assert.Equal(0x04, record.Value.PublicKey[0]);
    }

    [Fact]
    public async Task Device_ReportsRevocationThroughTheResultCodeNotTheRow()
    {
        DeviceRepository repository = new(_factory);

        await ExecuteAsync($"""
            UPDATE core.Device
            SET Status = 2, RevokedUtc = SYSUTCDATETIME(), RevokedReason = N'Integration test'
            WHERE DeviceId = {_deviceId};
            """);

        DeviceVerificationRecord? record = await repository.GetForSignatureVerificationAsync(
            _devicePublicId, TestContext.Current.CancellationToken);

        // The row still comes back, so the API can write a meaningful event —
        // the result code is what says the device may not act.
        Assert.NotNull(record);
        Assert.Equal(AttendanceResultCode.DeviceRevoked, record!.Value.ResultCode);
    }

    [Fact]
    public async Task Device_RegistrationIsRefusedWhenTheEmployeeAlreadyHasAnActiveDevice()
    {
        // DEC-04, travelling the whole chain: C# to Dapper to procedure.
        DeviceRepository repository = new(_factory);

        byte[] challengeBytes = new byte[32];
        challengeBytes[0] = 0x5A;

        RegistrationChallenge challenge = await repository.IssueRegistrationChallengeAsync(
            challengeBytes, 300, null, TestContext.Current.CancellationToken);

        byte[] publicKey = NewPublicKey();

        DeviceRegistrationOutcome outcome = await repository.RegisterAsync(
            new DeviceRegistrationRequest(
                challenge.ChallengeId,
                challengeBytes,
                _mobileUserId,
                publicKey,
                System.Security.Cryptography.SHA256.HashData(publicKey),
                DevicePlatform.Android,
                AttestationLevel.Hardware,
                "ITEST MODEL",
                "Android 15",
                "1.0.0",
                RequiresApproval: false,
                Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        Assert.Equal(AttendanceResultCode.ActiveDeviceAlreadyExists, outcome.ResultCode);
        Assert.Null(outcome.DevicePublicId);
    }

    [Fact]
    public async Task Device_RegistrationCreatesAPendingDeviceForAnEmployeeWithoutOne()
    {
        DeviceRepository repository = new(_factory);

        byte[] challengeBytes = new byte[32];
        challengeBytes[0] = 0x7C;

        RegistrationChallenge challenge = await repository.IssueRegistrationChallengeAsync(
            challengeBytes, 300, null, TestContext.Current.CancellationToken);

        byte[] publicKey = NewPublicKey();

        DeviceRegistrationOutcome outcome = await repository.RegisterAsync(
            new DeviceRegistrationRequest(
                challenge.ChallengeId,
                challengeBytes,
                _userWithoutDeviceId,
                publicKey,
                System.Security.Cryptography.SHA256.HashData(publicKey),
                DevicePlatform.Ios,
                AttestationLevel.Hardware,
                "ITEST IPHONE",
                "iOS 18",
                "1.0.0",
                RequiresApproval: true,
                Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        Assert.Equal(AttendanceResultCode.RegistrationPendingApproval, outcome.ResultCode);
        Assert.Equal(DeviceStatus.PendingApproval, outcome.Status);
        Assert.NotNull(outcome.DevicePublicId);
    }

    [Fact]
    public async Task Device_RegistrationRefusesAChallengeThatWasAlreadyConsumed()
    {
        DeviceRepository repository = new(_factory);

        byte[] challengeBytes = new byte[32];
        challengeBytes[0] = 0x3B;

        RegistrationChallenge challenge = await repository.IssueRegistrationChallengeAsync(
            challengeBytes, 300, null, TestContext.Current.CancellationToken);

        byte[] firstKey = NewPublicKey();

        await repository.RegisterAsync(
            new DeviceRegistrationRequest(
                challenge.ChallengeId, challengeBytes, _userWithoutDeviceId, firstKey,
                System.Security.Cryptography.SHA256.HashData(firstKey), DevicePlatform.Android,
                AttestationLevel.Hardware, null, null, null, true, Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        byte[] secondKey = NewPublicKey();

        DeviceRegistrationOutcome replay = await repository.RegisterAsync(
            new DeviceRegistrationRequest(
                challenge.ChallengeId, challengeBytes, _userWithoutDeviceId, secondKey,
                System.Security.Cryptography.SHA256.HashData(secondKey), DevicePlatform.Android,
                AttestationLevel.Hardware, null, null, null, true, Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        // A consumed challenge stays consumed. This is the replay defence.
        Assert.Equal(AttendanceResultCode.ChallengeInvalid, replay.ResultCode);
    }

    [Fact]
    public async Task Device_TouchLastSeenUpdatesAnActiveDevice()
    {
        DeviceRepository repository = new(_factory);

        await repository.TouchLastSeenAsync(
            _deviceId, "9.9.9", "Android 15", TestContext.Current.CancellationToken);

        string? appVersion = await _connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT AppVersion FROM core.Device WHERE DeviceId = {_deviceId};",
            transaction: _transaction, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("9.9.9", appVersion);
    }

    // ---- Mobile configuration ----------------------------------------------

    [Fact]
    public async Task MobileConfiguration_ReturnsOnlyMobileVisibleSettings()
    {
        MobileConfigurationRepository repository = new(_factory);

        MobileRuntimeConfiguration configuration =
            await repository.GetRuntimeConfigurationAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(configuration.Settings);

        // The allow-list is the point: security parameters must never appear.
        Assert.DoesNotContain(configuration.Settings, s => s.Key == "Security.MobileLockoutThreshold");
        Assert.DoesNotContain(configuration.Settings, s => s.Key == "Security.SignatureSkewSeconds");

        Assert.NotEqual(default, configuration.ServerTimeUtc);
    }

    [Fact]
    public async Task MobileConfiguration_ReportsAttendanceAsUnconfiguredWhenATimeZoneIsMissing()
    {
        await ExecuteAsync(
            "UPDATE core.ApplicationSetting SET SettingValue = NULL WHERE SettingKey = 'Attendance.BusinessTimeZoneId';");

        MobileConfigurationRepository repository = new(_factory);

        MobileRuntimeConfiguration configuration =
            await repository.GetRuntimeConfigurationAsync(TestContext.Current.CancellationToken);

        Assert.False(configuration.ClockInConfigured);
        Assert.False(configuration.ClockOutConfigured);
    }

    // ---- Fixture -----------------------------------------------------------

    private static byte[] NewPublicKey()
    {
        byte[] key = new byte[65];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key.AsSpan(1));
        key[0] = 0x04;
        return key;
    }

    private async Task CreateFixtureRowsAsync()
    {
        const string sql = """
            DECLARE @pk VARBINARY(65) = 0x04 + CRYPT_GEN_RANDOM(64);

            INSERT INTO core.MobileUser (UserId, FirstName, LastName, Status)
            VALUES (CONCAT(N'itest.', LOWER(CONVERT(NVARCHAR(36), NEWID()))), N'Integration', N'Test', 1);
            DECLARE @userId INT = SCOPE_IDENTITY();

            INSERT INTO core.MobileUser (UserId, FirstName, LastName, Status)
            VALUES (CONCAT(N'itest2.', LOWER(CONVERT(NVARCHAR(36), NEWID()))), N'No', N'Device', 1);
            DECLARE @userNoDeviceId INT = SCOPE_IDENTITY();

            INSERT INTO core.MfaCredential (MobileUserId, SecretProtected, Algorithm, Digits,
                                           PeriodSeconds, Status, ActivatedUtc)
            VALUES (@userId, CRYPT_GEN_RANDOM(64), 'SHA1', 6, 30, 1, SYSUTCDATETIME());

            INSERT INTO core.OfficeLocation (Name, Latitude, Longitude, AllowedRadiusMeters, Status)
            VALUES (CONCAT(N'ITEST ', CONVERT(NVARCHAR(36), NEWID())), 6.465422, 3.406448, 5.00, 1);
            DECLARE @officeId INT = SCOPE_IDENTITY();

            INSERT INTO core.Device (MobileUserId, PublicKey, PublicKeyThumbprint, Platform,
                                     AttestationLevel, Status, DeviceModel, ApprovedUtc)
            VALUES (@userId, @pk, HASHBYTES('SHA2_256', @pk), 1, 2, 1, N'INTEGRATION TEST', SYSUTCDATETIME());
            DECLARE @deviceId INT = SCOPE_IDENTITY();

            SELECT @userId           AS MobileUserId,
                   @userNoDeviceId   AS UserWithoutDeviceId,
                   @deviceId         AS DeviceId,
                   @officeId         AS OfficeLocationId,
                   (SELECT DevicePublicId FROM core.Device WHERE DeviceId = @deviceId) AS DevicePublicId;
            """;

        FixtureRow row = await _connection.QuerySingleAsync<FixtureRow>(
            new CommandDefinition(sql, transaction: _transaction,
                cancellationToken: TestContext.Current.CancellationToken));

        _mobileUserId = row.MobileUserId;
        _userWithoutDeviceId = row.UserWithoutDeviceId;
        _deviceId = row.DeviceId;
        _officeLocationId = row.OfficeLocationId;
        _devicePublicId = row.DevicePublicId;
    }

    private async Task ExecuteAsync(string sql) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(sql, transaction: _transaction,
                cancellationToken: TestContext.Current.CancellationToken));

    private sealed class FixtureRow
    {
        public int MobileUserId { get; init; }
        public int UserWithoutDeviceId { get; init; }
        public int DeviceId { get; init; }
        public int OfficeLocationId { get; init; }
        public Guid DevicePublicId { get; init; }
    }

    /// <summary>Hands each repository the test's own connection and transaction.</summary>
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

        public Task<SqlConnectionLease> LeaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SqlConnectionLease.Borrowed(_connection, _transaction));
    }
}
