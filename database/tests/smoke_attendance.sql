/*==============================================================================
  ClockInXtra — Stored procedure integration tests
  File  : database/tests/smoke_attendance.sql
  Phase : 6 (verification of phase 5–6 deliverables)

  Runs the attendance, device, replay and idempotency procedures against real
  rows and asserts the result code of each case. Exits non-zero if any case
  fails, so it can gate a deployment.

      sqlcmd -S . -d ClockInXtra -E -C -b -I -i database/tests/smoke_attendance.sql

  RUN THIS ONLY AGAINST A DEVELOPMENT OR TEST DATABASE
  ----------------------------------------------------
  It creates and deletes employees, devices and attendance records, and it
  overwrites the Attendance.* settings.

  Why the cleanup is deliberately incomplete
  ------------------------------------------
  Clock-in and clock-out write to audit.AuditLog, which is an append-only
  ledger table. The engine does not allow those rows to be deleted — that is
  precisely the tamper-evidence property the audit trail depends on. Test runs
  therefore leave audit rows behind permanently. A test database should be
  rebuilt from scripts rather than cleaned, and this consequence is noted in
  the database architecture (CON-11).

  The settings written here use the 'UTC' time zone purely so the test is
  deterministic. It is test data, not a business default: the real value is
  OPEN-5 and must be decided by the business owner.
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

DECLARE @Results TABLE
(
    Seq      INT IDENTITY(1,1),
    TestName NVARCHAR(120) NOT NULL,
    Expected INT NOT NULL,
    Actual   INT NULL,
    Passed   AS (CASE WHEN Actual = Expected THEN 1 ELSE 0 END)
);

/*============================ arrange: settings ============================*/
/* Every setting is recorded first and restored exactly at the end, value and
   confirmation alike. The suite once blanked the attendance settings when it
   finished, which on a database whose settings had been decided would have
   silently undone the business's decisions. */
DECLARE @OriginalSettings TABLE
(
    SettingKey                   VARCHAR(100)  NOT NULL PRIMARY KEY,
    SettingValue                 NVARCHAR(400) NULL,
    RequiresBusinessConfirmation BIT           NOT NULL,
    ConfirmedByAdministratorId   INT           NULL,
    ConfirmedUtc                 DATETIME2(3)  NULL,
    UpdatedByAdministratorId     INT           NULL,
    UpdatedUtc                   DATETIME2(3)  NULL
);

INSERT INTO @OriginalSettings
SELECT SettingKey, SettingValue, RequiresBusinessConfirmation, ConfirmedByAdministratorId,
       ConfirmedUtc, UpdatedByAdministratorId, UpdatedUtc
FROM core.ApplicationSetting;

/* Everything from here to the cleanup block runs inside TRY, so that a run
   which fails part way through still reaches the restore below.

   This is not defensive habit: a run that aborted at an earlier section once
   left the suite's own test values in a development database -- UTC time zone,
   a clock-in window open until 23:59, corrections switched off -- where they
   looked exactly like decisions somebody had made. The body is deliberately
   not re-indented; the block boundaries are what matter. */
DECLARE @SuiteError NVARCHAR(2048) = NULL, @SuiteErrorLine INT = NULL;

BEGIN TRY

UPDATE core.ApplicationSetting SET SettingValue = N'UTC'                WHERE SettingKey = 'Attendance.BusinessTimeZoneId';
UPDATE core.ApplicationSetting SET SettingValue = N'23:59:00'           WHERE SettingKey = 'Attendance.ClockInCloseTime';
UPDATE core.ApplicationSetting SET SettingValue = N'AcceptAndFlagLate'  WHERE SettingKey = 'Attendance.ClockInAfterCloseAction';
UPDATE core.ApplicationSetting SET SettingValue = N'00:00:00'           WHERE SettingKey = 'Attendance.ClockOutOpenTime';
UPDATE core.ApplicationSetting SET SettingValue = N'AcceptAndFlagEarly' WHERE SettingKey = 'Attendance.ClockOutBeforeOpenAction';
UPDATE core.ApplicationSetting SET SettingValue = NULL                  WHERE SettingKey = 'Attendance.ClockInOpenTime';
UPDATE core.ApplicationSetting SET SettingValue = NULL                  WHERE SettingKey = 'Attendance.GracePeriodMinutes';
UPDATE core.ApplicationSetting SET SettingValue = NULL                  WHERE SettingKey = 'Attendance.MinimumMinutesBeforeClockOut';

/* Corrections start switched OFF, because section 23 tests that they are
   refused while off before switching them on. The suite used to inherit
   whatever the database happened to hold: once corrections were decided and
   enabled (DEC-08), that section quietly created a correction it never cleaned
   up, and a later DELETE hit the foreign key. A suite must set the state it
   depends on. Both are restored with everything else at the end. */
UPDATE core.ApplicationSetting SET SettingValue = NULL                  WHERE SettingKey = 'Attendance.AllowCorrections';
UPDATE core.ApplicationSetting SET SettingValue = NULL                  WHERE SettingKey = 'Attendance.CorrectionsRequireApproval';

/*============================ arrange: test data ===========================*/
DECLARE @User1 INT, @User2 INT, @Office INT, @OfficeOff INT,
        @Dev1 INT, @Dev2 INT, @DevRevoked INT,
        @Pk1 VARBINARY(65), @Pk2 VARBINARY(65), @Pk3 VARBINARY(65);

DELETE c FROM core.AttendanceCorrection AS c
INNER JOIN core.Attendance AS a ON a.AttendanceId = c.AttendanceId
INNER JOIN core.MobileUser AS m ON m.MobileUserId = a.MobileUserId
WHERE m.UserId LIKE N'ziptest.%';
DELETE FROM core.AttendanceEvent WHERE MobileUserId IN (SELECT MobileUserId FROM core.MobileUser WHERE UserId LIKE N'ziptest.%');
DELETE FROM core.Attendance      WHERE MobileUserId IN (SELECT MobileUserId FROM core.MobileUser WHERE UserId LIKE N'ziptest.%');
DELETE FROM core.RequestNonce       WHERE DeviceId IN (SELECT DeviceId FROM core.Device WHERE DeviceModel = N'TESTDEVICE');
DELETE FROM core.RequestIdempotency WHERE DeviceId IN (SELECT DeviceId FROM core.Device WHERE DeviceModel = N'TESTDEVICE');
DELETE FROM core.Device             WHERE DeviceModel = N'TESTDEVICE';
DELETE FROM core.MfaCredential      WHERE MobileUserId IN (SELECT MobileUserId FROM core.MobileUser WHERE UserId LIKE N'ziptest.%');
DELETE FROM core.EmployeeCredential WHERE MobileUserId IN (SELECT MobileUserId FROM core.MobileUser WHERE UserId LIKE N'ziptest.%');
DELETE FROM core.MobileUser         WHERE UserId LIKE N'ziptest.%';
DELETE FROM core.Department         WHERE Name LIKE N'ZIPTEST %'
                                      AND NOT EXISTS (SELECT 1 FROM core.MobileUser AS m WHERE m.Department = core.Department.Name);
DELETE FROM core.JobTitle           WHERE Name LIKE N'ZIPTEST %'
                                      AND NOT EXISTS (SELECT 1 FROM core.MobileUser AS m WHERE m.JobTitle = core.JobTitle.Name);
DELETE FROM core.OfficeLocation     WHERE Name LIKE N'ZIPTEST %';

INSERT INTO core.MobileUser (UserId, FirstName, LastName, Status)
VALUES (N'ziptest.one', N'Test', N'One', 1), (N'ziptest.two', N'Test', N'Two', 1);

SELECT @User1 = MobileUserId FROM core.MobileUser WHERE UserId = N'ziptest.one';
SELECT @User2 = MobileUserId FROM core.MobileUser WHERE UserId = N'ziptest.two';

INSERT INTO core.MfaCredential (MobileUserId, SecretProtected, Status)
VALUES (@User1, 0x00, 1);

INSERT INTO core.OfficeLocation (Name, Latitude, Longitude, AllowedRadiusMeters, Status)
VALUES (N'ZIPTEST Head Office', 6.465422, 3.406448, 5.00, 1),
       (N'ZIPTEST Disabled',    6.465422, 3.406448, 5.00, 0);

SELECT @Office    = OfficeLocationId FROM core.OfficeLocation WHERE Name = N'ZIPTEST Head Office';
SELECT @OfficeOff = OfficeLocationId FROM core.OfficeLocation WHERE Name = N'ZIPTEST Disabled';

SET @Pk1 = 0x04 + CRYPT_GEN_RANDOM(64);
SET @Pk2 = 0x04 + CRYPT_GEN_RANDOM(64);
SET @Pk3 = 0x04 + CRYPT_GEN_RANDOM(64);

INSERT INTO core.Device (MobileUserId, PublicKey, PublicKeyThumbprint, Platform, AttestationLevel, Status, DeviceModel, ApprovedUtc)
VALUES (@User1, @Pk1, HASHBYTES('SHA2_256', @Pk1), 1, 2, 1, N'TESTDEVICE', SYSUTCDATETIME()),
       (@User2, @Pk2, HASHBYTES('SHA2_256', @Pk2), 1, 2, 1, N'TESTDEVICE', SYSUTCDATETIME());

INSERT INTO core.Device (MobileUserId, PublicKey, PublicKeyThumbprint, Platform, AttestationLevel, Status, DeviceModel, RevokedUtc, RevokedReason)
VALUES (@User2, @Pk3, HASHBYTES('SHA2_256', @Pk3), 1, 2, 2, N'TESTDEVICE', SYSUTCDATETIME(), N'test fixture');

SELECT @Dev1       = DeviceId FROM core.Device WHERE PublicKeyThumbprint = HASHBYTES('SHA2_256', @Pk1);
SELECT @Dev2       = DeviceId FROM core.Device WHERE PublicKeyThumbprint = HASHBYTES('SHA2_256', @Pk2);
SELECT @DevRevoked = DeviceId FROM core.Device WHERE PublicKeyThumbprint = HASHBYTES('SHA2_256', @Pk3);

/*================================= act =====================================*/
DECLARE @rc INT, @pubId UNIQUEIDENTIFIER, @date DATE, @inUtc DATETIME2(3),
        @outUtc DATETIME2(3), @dur INT, @late BIT, @early BIT;

/* 1. Status before any clock-in. */
EXEC mobile.usp_Attendance_GetCurrentStatus @MobileUserId = @User1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Status: configured, not clocked in', 0, @rc);

/* 2. First clock-in succeeds. */
EXEC mobile.usp_Attendance_ClockIn
    @MobileUserId = @User1, @DeviceId = @Dev1, @OfficeLocationId = @Office,
    @DistanceMeters = 3.10, @ReportedAccuracyMeters = 4.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @IsLateClockIn = @late OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'ClockIn: first attempt succeeds', 0, @rc);

/* 3. Duplicate clock-in on the same attendance day is refused. */
EXEC mobile.usp_Attendance_ClockIn
    @MobileUserId = @User1, @DeviceId = @Dev1, @OfficeLocationId = @Office,
    @DistanceMeters = 3.10, @ReportedAccuracyMeters = 4.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @IsLateClockIn = @late OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'ClockIn: duplicate refused (1050)', 1050, @rc);

/* 4. Clock-out closes the record. */
EXEC mobile.usp_Attendance_ClockOut
    @MobileUserId = @User1, @DeviceId = @Dev1, @OfficeLocationId = @Office,
    @DistanceMeters = 2.50, @ReportedAccuracyMeters = 4.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @ClockOutUtc = @outUtc OUTPUT,
    @DurationMinutes = @dur OUTPUT, @IsEarlyClockOut = @early OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'ClockOut: closes the open record', 0, @rc);

/* 5. Second clock-out is refused. */
EXEC mobile.usp_Attendance_ClockOut
    @MobileUserId = @User1, @DeviceId = @Dev1, @OfficeLocationId = @Office,
    @DistanceMeters = 2.50, @ReportedAccuracyMeters = 4.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @ClockOutUtc = @outUtc OUTPUT,
    @DurationMinutes = @dur OUTPUT, @IsEarlyClockOut = @early OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'ClockOut: already closed (1055)', 1055, @rc);

/* 6. Clock-out without a clock-in. */
EXEC mobile.usp_Attendance_ClockOut
    @MobileUserId = @User2, @DeviceId = @Dev2, @OfficeLocationId = @Office,
    @DistanceMeters = 2.50, @ReportedAccuracyMeters = 4.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @ClockOutUtc = @outUtc OUTPUT,
    @DurationMinutes = @dur OUTPUT, @IsEarlyClockOut = @early OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'ClockOut: not clocked in (1051)', 1051, @rc);

/* 7. A revoked device cannot clock in. */
EXEC mobile.usp_Attendance_ClockIn
    @MobileUserId = @User2, @DeviceId = @DevRevoked, @OfficeLocationId = @Office,
    @DistanceMeters = 1.00, @ReportedAccuracyMeters = 3.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @IsLateClockIn = @late OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'ClockIn: revoked device (1022)', 1022, @rc);

/* 8. A device belonging to another employee cannot be used. */
EXEC mobile.usp_Attendance_ClockIn
    @MobileUserId = @User2, @DeviceId = @Dev1, @OfficeLocationId = @Office,
    @DistanceMeters = 1.00, @ReportedAccuracyMeters = 3.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @IsLateClockIn = @late OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'ClockIn: device bound elsewhere (1023)', 1023, @rc);

/* 9. A disabled office is refused. */
EXEC mobile.usp_Attendance_ClockIn
    @MobileUserId = @User2, @DeviceId = @Dev2, @OfficeLocationId = @OfficeOff,
    @DistanceMeters = 1.00, @ReportedAccuracyMeters = 3.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @IsLateClockIn = @late OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'ClockIn: disabled office (1043)', 1043, @rc);

/* 10. Outside the clock-in window, with the action set to Reject. */
UPDATE core.ApplicationSetting SET SettingValue = N'00:00:01' WHERE SettingKey = 'Attendance.ClockInCloseTime';
UPDATE core.ApplicationSetting SET SettingValue = N'Reject'   WHERE SettingKey = 'Attendance.ClockInAfterCloseAction';

EXEC mobile.usp_Attendance_ClockIn
    @MobileUserId = @User2, @DeviceId = @Dev2, @OfficeLocationId = @Office,
    @DistanceMeters = 1.00, @ReportedAccuracyMeters = 3.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @IsLateClockIn = @late OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'ClockIn: after closing time, Reject (1052)', 1052, @rc);

/* 11. An unset mandatory setting refuses the operation instead of guessing. */
UPDATE core.ApplicationSetting SET SettingValue = NULL WHERE SettingKey = 'Attendance.BusinessTimeZoneId';

EXEC mobile.usp_Attendance_ClockIn
    @MobileUserId = @User2, @DeviceId = @Dev2, @OfficeLocationId = @Office,
    @DistanceMeters = 1.00, @ReportedAccuracyMeters = 3.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @IsLateClockIn = @late OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'ClockIn: timezone unset (1053)', 1053, @rc);

EXEC mobile.usp_Attendance_GetCurrentStatus @MobileUserId = @User2, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Status: timezone unset (1053)', 1053, @rc);

/* Restore a workable configuration for the remaining cases. */
UPDATE core.ApplicationSetting SET SettingValue = N'UTC'               WHERE SettingKey = 'Attendance.BusinessTimeZoneId';
UPDATE core.ApplicationSetting SET SettingValue = N'23:59:00'          WHERE SettingKey = 'Attendance.ClockInCloseTime';
UPDATE core.ApplicationSetting SET SettingValue = N'AcceptAndFlagLate' WHERE SettingKey = 'Attendance.ClockInAfterCloseAction';

/* 12. Replay protection: the same nonce twice.

   The signature time uses its own variable rather than @inUtc. The attendance
   procedures now clear their OUTPUT parameters on an early return, so @inUtc
   is legitimately NULL after a "not clocked in" result — the test must not
   depend on a stale value surviving, which was the very defect just fixed. */
DECLARE @nonce VARBINARY(32) = CRYPT_GEN_RANDOM(16),
        @sigCreatedUtc DATETIME2(3) = SYSUTCDATETIME();

EXEC mobile.usp_RequestNonce_TryInsert @DeviceId = @Dev1, @Nonce = @nonce,
     @SignatureCreatedUtc = @sigCreatedUtc, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Nonce: first use accepted', 0, @rc);

EXEC mobile.usp_RequestNonce_TryInsert @DeviceId = @Dev1, @Nonce = @nonce,
     @SignatureCreatedUtc = @sigCreatedUtc, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Nonce: replay refused (1030)', 1030, @rc);

/* 13. Idempotency lifecycle. */
DECLARE @key UNIQUEIDENTIFIER = NEWID(),
        @hash VARBINARY(32) = HASHBYTES('SHA2_256', N'clockin-request-1'),
        @otherHash VARBINARY(32) = HASHBYTES('SHA2_256', N'a-different-request'),
        @storedRc INT, @storedPayload NVARCHAR(MAX);

EXEC mobile.usp_Idempotency_TryBegin @DeviceId = @Dev1, @IdempotencyKey = @key, @EndpointCode = 1,
     @RequestHash = @hash, @StoredResultCode = @storedRc OUTPUT, @StoredPayload = @storedPayload OUTPUT,
     @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Idempotency: first claim', 0, @rc);

EXEC mobile.usp_Idempotency_TryBegin @DeviceId = @Dev1, @IdempotencyKey = @key, @EndpointCode = 1,
     @RequestHash = @hash, @StoredResultCode = @storedRc OUTPUT, @StoredPayload = @storedPayload OUTPUT,
     @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Idempotency: in progress (1034)', 1034, @rc);

EXEC mobile.usp_Idempotency_Complete @DeviceId = @Dev1, @IdempotencyKey = @key,
     @OperationResult = 0, @ResponsePayload = N'{"ok":true}', @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Idempotency: complete', 0, @rc);

EXEC mobile.usp_Idempotency_TryBegin @DeviceId = @Dev1, @IdempotencyKey = @key, @EndpointCode = 1,
     @RequestHash = @hash, @StoredResultCode = @storedRc OUTPUT, @StoredPayload = @storedPayload OUTPUT,
     @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Idempotency: replay returns stored result (1033)', 1033, @rc);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Idempotency: stored result code preserved', 0, @storedRc);

EXEC mobile.usp_Idempotency_TryBegin @DeviceId = @Dev1, @IdempotencyKey = @key, @EndpointCode = 1,
     @RequestHash = @otherHash, @StoredResultCode = @storedRc OUTPUT, @StoredPayload = @storedPayload OUTPUT,
     @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Idempotency: key reused for another request (1032)', 1032, @rc);

/* 14. TOTP time step may be used only once. */
EXEC core.usp_MfaCredential_TryConsumeTimeStep @MobileUserId = @User1, @TimeStep = 58000000, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'TOTP: first use of time step', 0, @rc);

EXEC core.usp_MfaCredential_TryConsumeTimeStep @MobileUserId = @User1, @TimeStep = 58000000, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'TOTP: replayed time step (1013)', 1013, @rc);

EXEC core.usp_MfaCredential_TryConsumeTimeStep @MobileUserId = @User2, @TimeStep = 58000000, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'TOTP: no enrolment (1015)', 1015, @rc);

/* 15. Account lockout counts across calls. */
DECLARE @failed INT, @lockedUntil DATETIME2(3);
EXEC core.usp_AuthenticationAttempt_Reset @SubjectType = 1, @SubjectKey = N'ziptest.one', @ResultCode = @rc OUTPUT;

EXEC core.usp_AuthenticationAttempt_RegisterFailure @SubjectType = 1, @SubjectKey = N'ziptest.one',
     @Threshold = 3, @LockoutMinutes = 15, @FailedCount = @failed OUTPUT,
     @LockedUntilUtc = @lockedUntil OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Lockout: first failure not locked', 0, @rc);

EXEC core.usp_AuthenticationAttempt_RegisterFailure @SubjectType = 1, @SubjectKey = N'ziptest.one',
     @Threshold = 3, @LockoutMinutes = 15, @FailedCount = @failed OUTPUT,
     @LockedUntilUtc = @lockedUntil OUTPUT, @ResultCode = @rc OUTPUT;
EXEC core.usp_AuthenticationAttempt_RegisterFailure @SubjectType = 1, @SubjectKey = N'ziptest.one',
     @Threshold = 3, @LockoutMinutes = 15, @FailedCount = @failed OUTPUT,
     @LockedUntilUtc = @lockedUntil OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Lockout: third failure locks (1011)', 1011, @rc);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Lockout: failure count is 3', 3, @failed);

EXEC core.usp_AuthenticationAttempt_Check @SubjectType = 1, @SubjectKey = N'ziptest.one',
     @LockedUntilUtc = @lockedUntil OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Lockout: check reports locked (1011)', 1011, @rc);

EXEC core.usp_AuthenticationAttempt_Reset @SubjectType = 1, @SubjectKey = N'ziptest.one', @ResultCode = @rc OUTPUT;
EXEC core.usp_AuthenticationAttempt_Check @SubjectType = 1, @SubjectKey = N'ziptest.one',
     @LockedUntilUtc = @lockedUntil OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Lockout: reset clears the lock', 0, @rc);

/* 16. Device registration: challenge consumed exactly once. */
DECLARE @challengeId UNIQUEIDENTIFIER, @expires DATETIME2(3),
        @challenge VARBINARY(32) = CRYPT_GEN_RANDOM(32),
        @newPk VARBINARY(65) = 0x04 + CRYPT_GEN_RANDOM(64),
        @devPubId UNIQUEIDENTIFIER, @devStatus TINYINT;

EXEC mobile.usp_Device_IssueRegistrationChallenge @Challenge = @challenge, @LifetimeSeconds = 300,
     @ChallengeId = @challengeId OUTPUT, @ExpiresUtc = @expires OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Registration: challenge issued', 0, @rc);

/* User2 already holds an active device, so an auto-approved registration must
   be refused rather than silently displacing it (DEC-04). */
EXEC mobile.usp_Device_Register
     @ChallengeId = @challengeId, @Challenge = @challenge, @MobileUserId = @User2,
     @PublicKey = @newPk, @PublicKeyThumbprint = 0x00, @Platform = 1, @AttestationLevel = 2,
     @DeviceModel = N'TESTDEVICE', @RequiresApproval = 0,
     @DevicePublicId = @devPubId OUTPUT, @DeviceStatus = @devStatus OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Registration: active device exists (1024)', 1024, @rc);

/* The challenge is now consumed, so it cannot be used again. */
EXEC mobile.usp_Device_Register
     @ChallengeId = @challengeId, @Challenge = @challenge, @MobileUserId = @User1,
     @PublicKey = @newPk, @PublicKeyThumbprint = 0x01, @Platform = 1, @AttestationLevel = 2,
     @DeviceModel = N'TESTDEVICE', @RequiresApproval = 1,
     @DevicePublicId = @devPubId OUTPUT, @DeviceStatus = @devStatus OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Registration: challenge reuse refused (1060)', 1060, @rc);

/* A fresh challenge, pending approval. */
SET @challenge = CRYPT_GEN_RANDOM(32);
EXEC mobile.usp_Device_IssueRegistrationChallenge @Challenge = @challenge, @LifetimeSeconds = 300,
     @ChallengeId = @challengeId OUTPUT, @ExpiresUtc = @expires OUTPUT, @ResultCode = @rc OUTPUT;

SET @newPk = 0x04 + CRYPT_GEN_RANDOM(64);

/* An EXEC argument must be a constant or a variable. An expression such as
   HASHBYTES(...) is not valid in a parameter position, so it is computed
   into a variable first. */
DECLARE @newThumbprint VARBINARY(32) = HASHBYTES('SHA2_256', @newPk);

EXEC mobile.usp_Device_Register
     @ChallengeId = @challengeId, @Challenge = @challenge, @MobileUserId = @User1,
     @PublicKey = @newPk, @PublicKeyThumbprint = @newThumbprint,
     @Platform = 1, @AttestationLevel = 2, @DeviceModel = N'TESTDEVICE', @RequiresApproval = 1,
     @DevicePublicId = @devPubId OUTPUT, @DeviceStatus = @devStatus OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Registration: pending approval (1062)', 1062, @rc);

/* 17. Signature verification lookup reflects device state. */
DECLARE @activePubId UNIQUEIDENTIFIER = (SELECT DevicePublicId FROM core.Device WHERE DeviceId = @Dev1);
EXEC mobile.usp_Device_GetForSignatureVerification @DevicePublicId = @activePubId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Signature lookup: active device', 0, @rc);

DECLARE @revokedPubId UNIQUEIDENTIFIER = (SELECT DevicePublicId FROM core.Device WHERE DeviceId = @DevRevoked);
EXEC mobile.usp_Device_GetForSignatureVerification @DevicePublicId = @revokedPubId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Signature lookup: revoked device (1022)', 1022, @rc);

EXEC mobile.usp_Device_GetForSignatureVerification @DevicePublicId = '11111111-1111-1111-1111-111111111111', @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Signature lookup: unknown device (1020)', 1020, @rc);

/* 18. Untrusted device metadata: real model names are accepted, control
       characters are refused.

   This guards a subtle defect. A LIKE character range is evaluated in
   collation order, not code-point order, so the printable-ASCII constraint
   only behaves correctly because it forces a binary collation. Without that,
   it rejected ordinary values such as 'TESTDEVICE'. Both directions are
   asserted here so the mistake cannot come back unnoticed. */
DECLARE @metaPk VARBINARY(65) = 0x04 + CRYPT_GEN_RANDOM(64);
DECLARE @metaOk INT = 0, @metaRejected INT = 0;

BEGIN TRY
    INSERT INTO core.Device (MobileUserId, PublicKey, PublicKeyThumbprint, Platform,
                             AttestationLevel, Status, DeviceModel, OsVersion, AppVersion, ApprovedUtc)
    VALUES (@User1, @metaPk, HASHBYTES('SHA2_256', @metaPk), 2, 2, 0,
            N'iPhone 15 Pro (A2848) - TESTDEVICE', N'iOS 18.5', N'1.0.0+build.42', NULL);
    SET @metaOk = 1;
END TRY
BEGIN CATCH
    SET @metaOk = 0;
END CATCH;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Device metadata: realistic model name accepted', 1, @metaOk);

DECLARE @badPk VARBINARY(65) = 0x04 + CRYPT_GEN_RANDOM(64);
BEGIN TRY
    INSERT INTO core.Device (MobileUserId, PublicKey, PublicKeyThumbprint, Platform,
                             AttestationLevel, Status, DeviceModel, ApprovedUtc)
    VALUES (@User1, @badPk, HASHBYTES('SHA2_256', @badPk), 1, 2, 0,
            N'TESTDEVICE' + NCHAR(7) + N'bell', NULL);   -- control character
    SET @metaRejected = 0;
END TRY
BEGIN CATCH
    SET @metaRejected = 1;
END CATCH;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Device metadata: control character refused', 1, @metaRejected);

DELETE FROM core.Device WHERE PublicKeyThumbprint IN (HASHBYTES('SHA2_256', @metaPk), HASHBYTES('SHA2_256', @badPk));

/* 19. Authenticator enrolment lifecycle.

   An administrator is needed from here on, because approvals and enrolments
   are recorded against a named person. */
DECLARE @adminId INT, @mfaId INT;

/* Self-healing fixture: UserName is unique, so a row left behind by an earlier
   run would make this INSERT fail. Devices that referenced it were already
   removed by the arrange block above, so it can be deleted safely here. */
DELETE FROM core.Administrator WHERE UserName LIKE N'ziptest.%';

INSERT INTO core.Administrator (UserName, DisplayName, HashFormat, Iterations, Salt, PasswordHash, Status)
VALUES (N'ziptest.admin', N'Test Administrator', 'pbkdf2-sha512', 220000,
        CRYPT_GEN_RANDOM(32), CRYPT_GEN_RANDOM(64), 1);
SET @adminId = SCOPE_IDENTITY();

EXEC admin.usp_MfaCredential_Enrol @MobileUserId = @User2, @SecretProtected = 0x0102030405,
     @AdministratorId = @adminId, @MfaCredentialId = @mfaId OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'MFA: enrolment created', 0, @rc);

/* Enrolling again must not silently replace the authenticator. */
EXEC admin.usp_MfaCredential_Enrol @MobileUserId = @User2, @SecretProtected = 0x0605040302,
     @AdministratorId = @adminId, @MfaCredentialId = @mfaId OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'MFA: duplicate enrolment refused (1064)', 1064, @rc);

EXEC admin.usp_MfaCredential_Enrol @MobileUserId = @User2, @SecretProtected = 0x0605040302,
     @ReplaceExisting = 1, @AdministratorId = @adminId,
     @MfaCredentialId = @mfaId OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'MFA: deliberate replacement allowed', 0, @rc);

/* A pending enrolment cannot be used to clock in until it is activated. */
EXEC core.usp_MfaCredential_TryConsumeTimeStep @MobileUserId = @User2, @TimeStep = 58000100, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'MFA: pending enrolment is not usable (1015)', 1015, @rc);

EXEC admin.usp_MfaCredential_Activate @MobileUserId = @User2, @TimeStep = 58000100,
     @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'MFA: activation succeeds', 0, @rc);

EXEC admin.usp_MfaCredential_Activate @MobileUserId = @User2, @TimeStep = 58000101,
     @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'MFA: second activation refused (1015)', 1015, @rc);

/* The code that proved enrolment must not then be replayable to clock in. */
EXEC core.usp_MfaCredential_TryConsumeTimeStep @MobileUserId = @User2, @TimeStep = 58000100, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'MFA: activation code cannot be replayed (1013)', 1013, @rc);

EXEC core.usp_MfaCredential_TryConsumeTimeStep @MobileUserId = @User2, @TimeStep = 58000101, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'MFA: a later time step is accepted', 0, @rc);

/* 20. Device approval swaps the active device (DEC-04).

   This is the behaviour the whole one-device rule rests on: approving a
   replacement must revoke the employee's previous device, in one transaction,
   leaving exactly one active device. @devPubId is the pending registration
   created in section 16. */
/* @revokedPreviousPubId, not @revokedPubId: that name is already declared in
   section 17. A duplicate DECLARE fails the whole batch at parse time, and
   every variable in the failed statement then reads as undeclared. */
DECLARE @pendingDeviceId INT,
        @pendingRowVersion BINARY(8),
        @oldActivePubId       UNIQUEIDENTIFIER,
        @approvedPubId        UNIQUEIDENTIFIER,
        @revokedPreviousPubId UNIQUEIDENTIFIER,
        @activeCount          INT,
        @oldStatus            TINYINT;

SELECT @pendingDeviceId = d.DeviceId, @pendingRowVersion = d.[RowVersion]
FROM core.Device AS d WHERE d.DevicePublicId = @devPubId;

SELECT @oldActivePubId = d.DevicePublicId FROM core.Device AS d WHERE d.DeviceId = @Dev1;

EXEC admin.usp_Device_Approve
     @DeviceId = @pendingDeviceId, @RowVersion = @pendingRowVersion, @AdministratorId = @adminId,
     @DevicePublicId = @approvedPubId OUTPUT,
     @RevokedPreviousDevicePublicId = @revokedPreviousPubId OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Approval: succeeds', 0, @rc);

INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Approval: revoked the previous active device',
        1, CASE WHEN @revokedPreviousPubId = @oldActivePubId THEN 1 ELSE 0 END);

SELECT @oldStatus = d.Status FROM core.Device AS d WHERE d.DeviceId = @Dev1;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Approval: previous device is now Revoked', 2, @oldStatus);

SELECT @activeCount = COUNT(*) FROM core.Device WHERE MobileUserId = @User1 AND Status = 1;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Approval: exactly one active device remains', 1, @activeCount);

/* Approving something that is no longer pending is refused. */
SELECT @pendingRowVersion = d.[RowVersion] FROM core.Device AS d WHERE d.DeviceId = @pendingDeviceId;
EXEC admin.usp_Device_Approve
     @DeviceId = @pendingDeviceId, @RowVersion = @pendingRowVersion, @AdministratorId = @adminId,
     @DevicePublicId = @approvedPubId OUTPUT,
     @RevokedPreviousDevicePublicId = @revokedPubId OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Approval: already approved (1001)', 1001, @rc);

/* 21. Revocation requires a stated reason and is terminal. */
DECLARE @revokeTargetPubId UNIQUEIDENTIFIER;

SELECT @pendingRowVersion = d.[RowVersion] FROM core.Device AS d WHERE d.DeviceId = @pendingDeviceId;
EXEC admin.usp_Device_Revoke @DeviceId = @pendingDeviceId, @RowVersion = @pendingRowVersion,
     @Reason = N'', @AdministratorId = @adminId,
     @DevicePublicId = @revokeTargetPubId OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Revocation: blank reason refused (1001)', 1001, @rc);

EXEC admin.usp_Device_Revoke @DeviceId = @pendingDeviceId, @RowVersion = @pendingRowVersion,
     @Reason = N'Handset returned to IT', @AdministratorId = @adminId,
     @DevicePublicId = @revokeTargetPubId OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Revocation: succeeds with a reason', 0, @rc);

SELECT @pendingRowVersion = d.[RowVersion] FROM core.Device AS d WHERE d.DeviceId = @pendingDeviceId;
EXEC admin.usp_Device_Revoke @DeviceId = @pendingDeviceId, @RowVersion = @pendingRowVersion,
     @Reason = N'Again', @AdministratorId = @adminId,
     @DevicePublicId = @revokeTargetPubId OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Revocation: already revoked (1001)', 1001, @rc);

/* A stale RowVersion must not silently overwrite another administrator's
   change. */
EXEC admin.usp_OfficeLocation_SetStatus @OfficeLocationId = @Office, @Status = 0,
     @RowVersion = 0x0000000000000001, @AdministratorId = @adminId,
     @Reason = N'stale row version', @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Concurrency: stale RowVersion refused (1071)', 1071, @rc);

/* 22. Settings validation refuses an unusable time zone outright. */
DECLARE @tzRowVersion BINARY(8);
SELECT @tzRowVersion = s.[RowVersion] FROM core.ApplicationSetting AS s
WHERE s.SettingKey = 'Attendance.BusinessTimeZoneId';

EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Attendance.BusinessTimeZoneId',
     @SettingValue = N'Not A Real Zone', @Confirm = 0, @RowVersion = @tzRowVersion,
     @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Settings: invalid time zone refused (1076)', 1076, @rc);

/* The row version is read into a variable first: a subquery is not valid in a
   parameter position, only a constant or a variable. */
DECLARE @actionRowVersion BINARY(8);
SELECT @actionRowVersion = s.[RowVersion] FROM core.ApplicationSetting AS s
WHERE s.SettingKey = 'Attendance.ClockInAfterCloseAction';

EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Attendance.ClockInAfterCloseAction',
     @SettingValue = N'DoSomethingElse', @Confirm = 0,
     @RowVersion = @actionRowVersion,
     @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Settings: value outside AllowedValues refused (1001)', 1001, @rc);

/* 23. Attendance corrections: the switch, then maker-checker.

   Corrections are enabled in the real database (DEC-08), but the switch is
   still a switch. These cases prove both halves: that the procedure refuses
   while Attendance.AllowCorrections is off, and that the workflow behaves
   correctly when it is on — including that an administrator cannot approve
   their own correction. */
DECLARE @correctionId BIGINT,
        @applied      BIT,
        @attId        BIGINT,
        @adminId2     INT,
        @corrRowVersion BINARY(8),
        @correctedIn  DATETIME2(3) = DATEADD(MINUTE, -30, SYSUTCDATETIME()),
        @storedIn     DATETIME2(3),
        @attStatus    TINYINT;

INSERT INTO core.Administrator (UserName, DisplayName, HashFormat, Iterations, Salt, PasswordHash, Status)
VALUES (N'ziptest.admin2', N'Second Administrator', 'pbkdf2-sha512', 220000,
        CRYPT_GEN_RANDOM(32), CRYPT_GEN_RANDOM(64), 1);
SET @adminId2 = SCOPE_IDENTITY();

SELECT TOP (1) @attId = a.AttendanceId
FROM core.Attendance AS a
WHERE a.MobileUserId = @User1
ORDER BY a.AttendanceId DESC;

/* Refused while the business decision is outstanding. */
EXEC admin.usp_Attendance_RequestCorrection
     @AttendanceId = @attId, @CorrectedClockInUtc = @correctedIn, @Reason = N'Employee arrived earlier',
     @AdministratorId = @adminId, @AttendanceCorrectionId = @correctionId OUTPUT,
     @Applied = @applied OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Correction: refused while disabled (1075)', 1075, @rc);

/* Enable corrections, with approval required. */
UPDATE core.ApplicationSetting SET SettingValue = N'true' WHERE SettingKey = 'Attendance.AllowCorrections';
UPDATE core.ApplicationSetting SET SettingValue = N'true' WHERE SettingKey = 'Attendance.CorrectionsRequireApproval';

EXEC admin.usp_Attendance_RequestCorrection
     @AttendanceId = @attId, @CorrectedClockInUtc = @correctedIn, @Reason = N'Employee arrived earlier',
     @AdministratorId = @adminId, @AttendanceCorrectionId = @correctionId OUTPUT,
     @Applied = @applied OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Correction: request accepted', 0, @rc);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Correction: not applied before approval', 0, CAST(@applied AS INT));

/* The record must be untouched until someone approves. */
SELECT @storedIn = a.ClockInUtc FROM core.Attendance AS a WHERE a.AttendanceId = @attId;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Correction: attendance unchanged while pending', 1,
        CASE WHEN @storedIn <> @correctedIn THEN 1 ELSE 0 END);

/* The requester cannot approve their own correction. */
SELECT @corrRowVersion = c.[RowVersion] FROM core.AttendanceCorrection AS c
WHERE c.AttendanceCorrectionId = @correctionId;

EXEC admin.usp_Attendance_ApproveCorrection
     @AttendanceCorrectionId = @correctionId, @Approve = 1, @RowVersion = @corrRowVersion,
     @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Correction: self-approval refused (1074)', 1074, @rc);

/* A second administrator can. */
EXEC admin.usp_Attendance_ApproveCorrection
     @AttendanceCorrectionId = @correctionId, @Approve = 1, @RowVersion = @corrRowVersion,
     @AdministratorId = @adminId2, @DecisionNote = N'Verified with the security log',
     @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Correction: approved by a second administrator', 0, @rc);

SELECT @storedIn = a.ClockInUtc, @attStatus = a.Status
FROM core.Attendance AS a WHERE a.AttendanceId = @attId;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Correction: applied to the attendance record', 1,
        CASE WHEN @storedIn = @correctedIn THEN 1 ELSE 0 END);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Correction: attendance marked Corrected', 3, @attStatus);

/* Corrections are deleted here rather than in the cleanup block, because they
   reference the attendance rows that block removes. By attendance record, not
   by the one identifier this section happens to be holding: anything else left
   on the record would block the delete just the same. */
DELETE FROM core.AttendanceCorrection WHERE AttendanceId = @attId;

/* Switch corrections off again for the cases that follow. The settings
   snapshot taken at the start puts the real, business-confirmed values
   (DEC-08) back in the cleanup block. */
UPDATE core.ApplicationSetting SET SettingValue = NULL
WHERE SettingKey IN ('Attendance.AllowCorrections', 'Attendance.CorrectionsRequireApproval');

/* 24. Device status polling and the mobile configuration allow-list. */
DECLARE @deviceStatusRc INT,
        @mobileConfigRc INT,
        @leakedSettings INT,
        @revokedDevicePubId UNIQUEIDENTIFIER;

EXEC mobile.usp_Device_GetStatus
     @DevicePublicId = '11111111-1111-1111-1111-111111111111', @ResultCode = @deviceStatusRc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Device status: unknown device (1020)', 1020, @deviceStatusRc);

SELECT @revokedDevicePubId = d.DevicePublicId FROM core.Device AS d WHERE d.DeviceId = @DevRevoked;
EXEC mobile.usp_Device_GetStatus @DevicePublicId = @revokedDevicePubId, @ResultCode = @deviceStatusRc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Device status: a revoked device can still ask', 0, @deviceStatusRc);

EXEC mobile.usp_ApplicationSetting_GetMobileRuntime @ResultCode = @mobileConfigRc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Mobile config: returns successfully', 0, @mobileConfigRc);

/* The allow-list is a security property, so it is asserted rather than
   assumed: no security, retention or location policy setting may ever be
   marked visible to the mobile client. An attacker who learns the lockout
   threshold knows exactly how many guesses are free. */
SELECT @leakedSettings = COUNT(*)
FROM core.ApplicationSetting
WHERE IsMobileVisible = 1
  AND (SettingKey LIKE 'Security.%'
    OR SettingKey LIKE 'Retention.%'
    OR SettingKey LIKE 'Location.%');
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Mobile config: no security or retention setting is mobile-visible', 0, @leakedSettings);

/* 25. Nested transaction safety (decision DB-13).

   A bare ROLLBACK TRANSACTION in T-SQL rolls back EVERY nesting level and
   sets @@TRANCOUNT to 0. Before the savepoint pattern was introduced, an
   ordinary business rejection inside a caller's transaction destroyed that
   caller's work and SQL Server raised error 266 on return.

   This case locks the fix in: a refusal must leave the caller's transaction
   intact and usable. */
DECLARE @nestedRc       INT,
        @tranCountAfter INT,
        @nestedPub      UNIQUEIDENTIFIER,
        @nestedDate     DATE,
        @nestedIn       DATETIME2(3),
        @nestedLate     BIT;

BEGIN TRANSACTION;

/* Device -1 does not exist, so clock-in refuses from inside its transaction. */
EXEC mobile.usp_Attendance_ClockIn
     @MobileUserId = @User1, @DeviceId = -1, @OfficeLocationId = @Office,
     @DistanceMeters = 1.00, @ReportedAccuracyMeters = 3.00, @Platform = 1,
     @AttendancePublicId = @nestedPub OUTPUT, @AttendanceDate = @nestedDate OUTPUT,
     @ClockInUtc = @nestedIn OUTPUT, @IsLateClockIn = @nestedLate OUTPUT,
     @ResultCode = @nestedRc OUTPUT;

SET @tranCountAfter = @@TRANCOUNT;

IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;

INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Nested tran: rejection still returns its result code', 1022, @nestedRc);

INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Nested tran: caller transaction survives a rejection', 1, @tranCountAfter);

/* 30. Administrator management: authority, separation of duties, the last
       remaining manager, and security-stamp rotation.

   Fixtures use the ziptest.mgmt. prefix and are removed at the end of this
   block. The dev database also holds real administrators, so the "last
   manager" cases disable every non-fixture account INSIDE A TRANSACTION THAT
   IS ROLLED BACK — @Results is a table variable and survives the rollback, so
   nothing real is changed. */
DELETE FROM core.AuthenticationAttempt WHERE SubjectKey LIKE N'ziptest.mgmt.%';
DELETE FROM core.Administrator WHERE UserName LIKE N'ziptest.mgmt.%';

DECLARE @superRoleId  INT = (SELECT RoleId FROM core.Role WHERE Name = N'Super Administrator'),
        @userRoleId   INT = (SELECT RoleId FROM core.Role WHERE Name = N'User Administrator'),
        @reportRoleId INT = (SELECT RoleId FROM core.Role WHERE Name = N'Report Viewer'),
        @super1 INT, @super2 INT, @userAdmin INT, @noRole INT,
        @stampBefore UNIQUEIDENTIFIER, @stampAfter UNIQUEIDENTIFIER, @newStamp UNIQUEIDENTIFIER;

INSERT INTO core.Administrator (UserName, DisplayName, HashFormat, Iterations, Salt, PasswordHash, MfaSecretProtected, MfaStatus, Status)
VALUES (N'ziptest.mgmt.super1', N'Mgmt Super One', 'pbkdf2-sha512', 220000, CRYPT_GEN_RANDOM(32), CRYPT_GEN_RANDOM(64), 0x01, 2, 1);
SET @super1 = SCOPE_IDENTITY();
INSERT INTO core.Administrator (UserName, DisplayName, HashFormat, Iterations, Salt, PasswordHash, MfaSecretProtected, MfaStatus, Status)
VALUES (N'ziptest.mgmt.super2', N'Mgmt Super Two', 'pbkdf2-sha512', 220000, CRYPT_GEN_RANDOM(32), CRYPT_GEN_RANDOM(64), 0x01, 2, 1);
SET @super2 = SCOPE_IDENTITY();
INSERT INTO core.Administrator (UserName, DisplayName, HashFormat, Iterations, Salt, PasswordHash, MfaSecretProtected, MfaStatus, Status)
VALUES (N'ziptest.mgmt.useradmin', N'Mgmt User Admin', 'pbkdf2-sha512', 220000, CRYPT_GEN_RANDOM(32), CRYPT_GEN_RANDOM(64), 0x01, 2, 1);
SET @userAdmin = SCOPE_IDENTITY();
INSERT INTO core.Administrator (UserName, DisplayName, HashFormat, Iterations, Salt, PasswordHash, MfaStatus, Status)
VALUES (N'ziptest.mgmt.norole', N'Mgmt No Role', 'pbkdf2-sha512', 220000, CRYPT_GEN_RANDOM(32), CRYPT_GEN_RANDOM(64), 0, 1);
SET @noRole = SCOPE_IDENTITY();

INSERT INTO core.AdministratorRole (AdministratorId, RoleId)
VALUES (@super1, @superRoleId), (@super2, @superRoleId), (@userAdmin, @userRoleId);

/* -- authority ----------------------------------------------------------- */
EXEC admin.usp_Administrator_CheckAuthorityOver @ActingAdministratorId = @super1, @TargetAdministratorId = @super1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Authority: acting on yourself refused (1074)', 1074, @rc);

EXEC admin.usp_Administrator_CheckAuthorityOver @ActingAdministratorId = @userAdmin, @TargetAdministratorId = @super1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Authority: over a more privileged admin refused (1003)', 1003, @rc);

EXEC admin.usp_Administrator_CheckAuthorityOver @ActingAdministratorId = @super1, @TargetAdministratorId = @userAdmin, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Authority: over a less privileged admin allowed', 0, @rc);

/* -- status -------------------------------------------------------------- */
EXEC admin.usp_Administrator_SetStatus @AdministratorId = @userAdmin, @Status = 0, @Reason = N'  ', @ActingAdministratorId = @super1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Admin status: deactivation needs a reason (1001)', 1001, @rc);

EXEC admin.usp_Administrator_SetStatus @AdministratorId = @super2, @Status = 0, @Reason = N'test', @ActingAdministratorId = @userAdmin, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Admin status: escalation refused (1003)', 1003, @rc);

SELECT @stampBefore = SecurityStamp FROM core.Administrator WHERE AdministratorId = @userAdmin;
EXEC admin.usp_Administrator_SetStatus @AdministratorId = @userAdmin, @Status = 0, @Reason = N'Left the organisation', @ActingAdministratorId = @super1, @ResultCode = @rc OUTPUT;
SELECT @stampAfter = SecurityStamp FROM core.Administrator WHERE AdministratorId = @userAdmin;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Admin status: deactivated', 0, @rc);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Admin status: deactivation rotates the stamp', 1, CASE WHEN @stampBefore <> @stampAfter THEN 1 ELSE 0 END);

SET @stampBefore = @stampAfter;
EXEC admin.usp_Administrator_SetStatus @AdministratorId = @userAdmin, @Status = 0, @Reason = N'Again', @ActingAdministratorId = @super1, @ResultCode = @rc OUTPUT;
SELECT @stampAfter = SecurityStamp FROM core.Administrator WHERE AdministratorId = @userAdmin;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Admin status: repeat is a no-op without rotation', 1, CASE WHEN @rc = 0 AND @stampBefore = @stampAfter THEN 1 ELSE 0 END);

EXEC admin.usp_Administrator_CheckAuthorityOver @ActingAdministratorId = @userAdmin, @TargetAdministratorId = @noRole, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Authority: an inactive actor cannot act (1070)', 1070, @rc);

EXEC admin.usp_Administrator_SetStatus @AdministratorId = @userAdmin, @Status = 1, @ActingAdministratorId = @super1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Admin status: reactivated', 0, @rc);

/* -- the last remaining manager (isolated, rolled back) ------------------ */
DECLARE @noExclusionRc INT, @oneLeftRc INT, @noneLeftRc INT, @roleRemovalRc INT, @raceRc INT;

BEGIN TRANSACTION;

    UPDATE core.Administrator SET Status = 0 WHERE UserName NOT LIKE N'ziptest.mgmt.%';

    /* Guards the NULL trap: with nothing excluded, NOT(x = NULL AND ...) would
       filter every manager out and refuse every change. */
    EXEC admin.usp_Administrator_CheckManagerRemains @ResultCode = @noExclusionRc OUTPUT;

    EXEC admin.usp_Administrator_CheckManagerRemains @DeactivatedAdministratorId = @super2, @ResultCode = @oneLeftRc OUTPUT;

    UPDATE core.Administrator SET Status = 0 WHERE AdministratorId = @super1;
    EXEC admin.usp_Administrator_CheckManagerRemains @DeactivatedAdministratorId = @super2, @ResultCode = @noneLeftRc OUTPUT;
    EXEC admin.usp_Administrator_CheckManagerRemains @RemovedRoleAdministratorId = @super2, @RemovedRoleId = @superRoleId, @ResultCode = @roleRemovalRc OUTPUT;

    /* The end state of two managers deactivating each other at once: super1
       is still active but can no longer sign in (no authenticator), so it does
       not count, and SetStatus must refuse to remove super2. */
    UPDATE core.Administrator SET Status = 1, MfaStatus = 0, MfaSecretProtected = NULL WHERE AdministratorId = @super1;
    EXEC admin.usp_Administrator_SetStatus @AdministratorId = @super2, @Status = 0, @Reason = N'test', @ActingAdministratorId = @super1, @ResultCode = @raceRc OUTPUT;

ROLLBACK TRANSACTION;

INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Last manager: nothing excluded, managers remain', 0, @noExclusionRc);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Last manager: one of two removed is allowed', 0, @oneLeftRc);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Last manager: deactivating the last refused (1077)', 1077, @noneLeftRc);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Last manager: removing the last role refused (1077)', 1077, @roleRemovalRc);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Last manager: an actor unable to sign in does not count (1077)', 1077, @raceRc);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Last manager: isolation rolled back', 1,
    CASE WHEN EXISTS (SELECT 1 FROM core.Administrator WHERE UserName = N'devadmin' AND Status = 0) THEN 0 ELSE 1 END);

/* -- roles --------------------------------------------------------------- */
EXEC admin.usp_Administrator_SetRole @AdministratorId = @noRole, @RoleId = @superRoleId, @Grant = 1, @ActingAdministratorId = @userAdmin, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Roles: granting a role you do not fully hold refused (1003)', 1003, @rc);

EXEC admin.usp_Administrator_SetRole @AdministratorId = @super1, @RoleId = @reportRoleId, @Grant = 1, @ActingAdministratorId = @super1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Roles: granting to yourself refused (1074)', 1074, @rc);

EXEC admin.usp_Administrator_SetRole @AdministratorId = @noRole, @RoleId = -1, @Grant = 1, @ActingAdministratorId = @super1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Roles: unknown role (1070)', 1070, @rc);

SELECT @stampBefore = SecurityStamp FROM core.Administrator WHERE AdministratorId = @noRole;
EXEC admin.usp_Administrator_SetRole @AdministratorId = @noRole, @RoleId = @reportRoleId, @Grant = 1, @ActingAdministratorId = @super1, @ResultCode = @rc OUTPUT;
SELECT @stampAfter = SecurityStamp FROM core.Administrator WHERE AdministratorId = @noRole;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Roles: granted', 0, @rc);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Roles: grant rotates the stamp', 1, CASE WHEN @stampBefore <> @stampAfter THEN 1 ELSE 0 END);

SET @stampBefore = @stampAfter;
EXEC admin.usp_Administrator_SetRole @AdministratorId = @noRole, @RoleId = @reportRoleId, @Grant = 1, @ActingAdministratorId = @super1, @ResultCode = @rc OUTPUT;
SELECT @stampAfter = SecurityStamp FROM core.Administrator WHERE AdministratorId = @noRole;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Roles: repeated grant is a no-op without rotation', 1, CASE WHEN @rc = 0 AND @stampBefore = @stampAfter THEN 1 ELSE 0 END);

EXEC admin.usp_Administrator_SetRole @AdministratorId = @noRole, @RoleId = @reportRoleId, @Grant = 0, @ActingAdministratorId = @super1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Roles: removed', 1,
    CASE WHEN @rc = 0 AND NOT EXISTS (SELECT 1 FROM core.AdministratorRole WHERE AdministratorId = @noRole) THEN 1 ELSE 0 END);

/* -- password reset ------------------------------------------------------ */
EXEC core.usp_AuthenticationAttempt_RegisterFailure @SubjectType = 2, @SubjectKey = N'ziptest.mgmt.useradmin',
     @Threshold = 1, @LockoutMinutes = 15, @FailedCount = @failed OUTPUT, @LockedUntilUtc = @lockedUntil OUTPUT, @ResultCode = @rc OUTPUT;

EXEC admin.usp_Administrator_ResetPassword @AdministratorId = @super1, @HashFormat = 'pbkdf2-sha512', @Iterations = 220000,
     @Salt = 0x0000000000000000000000000000000000000000000000000000000000000001, @PasswordHash = 0x01,
     @ActingAdministratorId = @userAdmin, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Password reset: escalation refused (1003)', 1003, @rc);

EXEC admin.usp_Administrator_ResetPassword @AdministratorId = @super1, @HashFormat = 'pbkdf2-sha512', @Iterations = 220000,
     @Salt = 0x0000000000000000000000000000000000000000000000000000000000000001, @PasswordHash = 0x01,
     @ActingAdministratorId = @super1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Password reset: your own refused (1074)', 1074, @rc);

SELECT @stampBefore = SecurityStamp FROM core.Administrator WHERE AdministratorId = @userAdmin;
UPDATE core.Administrator SET MustChangePassword = 0 WHERE AdministratorId = @userAdmin;
EXEC admin.usp_Administrator_ResetPassword @AdministratorId = @userAdmin, @HashFormat = 'pbkdf2-sha512', @Iterations = 220000,
     @Salt = 0x0000000000000000000000000000000000000000000000000000000000000002, @PasswordHash = 0x02,
     @ActingAdministratorId = @super1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Password reset: issued', 0, @rc);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Password reset: must change, stamp rotated', 1,
    (SELECT CASE WHEN MustChangePassword = 1 AND SecurityStamp <> @stampBefore THEN 1 ELSE 0 END
     FROM core.Administrator WHERE AdministratorId = @userAdmin));

EXEC core.usp_AuthenticationAttempt_Check @SubjectType = 2, @SubjectKey = N'ziptest.mgmt.useradmin',
     @LockedUntilUtc = @lockedUntil OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Password reset: clears the sign-in lockout', 0, @rc);

/* -- change own password ------------------------------------------------- */
SELECT @stampBefore = SecurityStamp FROM core.Administrator WHERE AdministratorId = @userAdmin;

EXEC admin.usp_Administrator_ChangePassword @AdministratorId = @userAdmin, @ExpectedSecurityStamp = '00000000-0000-0000-0000-000000000001',
     @HashFormat = 'pbkdf2-sha512', @Iterations = 220000, @Salt = 0x0000000000000000000000000000000000000000000000000000000000000003,
     @PasswordHash = 0x03, @NewSecurityStamp = @newStamp OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Change password: a stale session refused (1071)', 1071, @rc);

EXEC admin.usp_Administrator_ChangePassword @AdministratorId = @userAdmin, @ExpectedSecurityStamp = @stampBefore,
     @HashFormat = 'pbkdf2-sha512', @Iterations = 220000, @Salt = 0x0000000000000000000000000000000000000000000000000000000000000003,
     @PasswordHash = 0x03, @NewSecurityStamp = @newStamp OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Change password: changed', 0, @rc);
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Change password: flag cleared, new stamp returned and stored', 1,
    (SELECT CASE WHEN MustChangePassword = 0 AND SecurityStamp = @newStamp AND @newStamp <> @stampBefore THEN 1 ELSE 0 END
     FROM core.Administrator WHERE AdministratorId = @userAdmin));

EXEC admin.usp_Administrator_ChangePassword @AdministratorId = @userAdmin, @ExpectedSecurityStamp = @newStamp,
     @HashFormat = 'pbkdf2-sha512', @Iterations = 99999, @Salt = 0x0000000000000000000000000000000000000000000000000000000000000003,
     @PasswordHash = 0x03, @NewSecurityStamp = @stampAfter OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Change password: weak hash parameters refused (1001)', 1001, @rc);

/* -- authenticator enrolment --------------------------------------------- */
EXEC admin.usp_Administrator_EnrolMfa @AdministratorId = @super1, @SecretProtected = 0x0A, @EnrolledByAdministratorId = @super1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Admin MFA: enrolling your own refused (1074)', 1074, @rc);

EXEC admin.usp_Administrator_EnrolMfa @AdministratorId = @super1, @SecretProtected = 0x0A, @ReplaceExisting = 1, @EnrolledByAdministratorId = @userAdmin, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Admin MFA: moving a superior''s factor refused (1003)', 1003, @rc);

EXEC admin.usp_Administrator_EnrolMfa @AdministratorId = @noRole, @SecretProtected = 0x0A, @EnrolledByAdministratorId = NULL, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Admin MFA: no actor refused (1070)', 1070, @rc);

EXEC admin.usp_Administrator_EnrolMfa @AdministratorId = @noRole, @SecretProtected = 0x0A, @EnrolledByAdministratorId = @super1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Admin MFA: enrolled pending activation', 1,
    (SELECT CASE WHEN @rc = 0 AND MfaStatus = 1 THEN 1 ELSE 0 END FROM core.Administrator WHERE AdministratorId = @noRole));

EXEC admin.usp_Administrator_EnrolMfa @AdministratorId = @noRole, @SecretProtected = 0x0B, @EnrolledByAdministratorId = @super1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Admin MFA: silent replacement refused (1064)', 1064, @rc);

DELETE FROM core.AuthenticationAttempt WHERE SubjectKey LIKE N'ziptest.mgmt.%';
DELETE FROM core.Administrator WHERE UserName LIKE N'ziptest.mgmt.%';

/* 31. Reporting (Phase 19).

   31a. Validation-failure classification. The first version of the report
   matched client-facing reason codes the server never writes, so every
   category came back almost empty, and with no category it listed successful
   sign-ins. Each event shape below is one a writer actually produces. */
DECLARE @Classified TABLE (EventType VARCHAR(64), ReasonCode VARCHAR(64), Expected INT);

INSERT INTO @Classified (EventType, ReasonCode, Expected)
VALUES ('Location.Rejected',          'LOCATION_NOT_ALLOWED',                    1),
       ('Location.Rejected',          'LOCATION_ACCURACY_INSUFFICIENT',          1),
       ('Location.ValidationFailed',  'NO_ACTIVE_OFFICE_LOCATIONS',              1),
       ('Device.RegistrationRefused', 'SELF_SERVICE',                            2),
       ('Device.AttestationRejected', 'ATTESTATION_SECURITY_LEVEL_INSUFFICIENT', 2),
       ('Signature.Rejected',         'DeviceRevoked',                           2),
       ('Signature.Rejected',         'DeviceNotApproved',                       2),
       ('Auth.Failed',                'INVALID_PASSWORD',                        3),
       ('Auth.Failed',                'OtpReplayed',                             3),
       ('Auth.Failed',                'UNKNOWN_ADMINISTRATOR',                   3),
       ('Auth.Locked',                'LOCKED_OUT',                              3),
       ('Auth.PasswordChangeFailed',  'INVALID_PASSWORD',                        3),
       ('Mfa.Missing',                'MFA_CODE_MISSING',                        3),
       ('Mfa.SecretUnreadable',       'KEY_UNAVAILABLE',                         3),
       ('Signature.Rejected',         'UserInactive',                            3),
       ('Signature.Rejected',         'SIGNATURE_NONCE_REPLAYED',                4),
       ('Signature.Rejected',         'SIGNATURE_CLOCK_SKEW',                    4),
       ('Signature.Rejected',         'CONTENT_DIGEST_MISMATCH',                 4),
       ('Signature.Rejected',         'SIGNATURE_INVALID',                       4),
       ('Signature.FailureBudgetExhausted', 'SIGNATURE_FAILURE_BUDGET_EXHAUSTED', 4),
       -- Not failures at all: must not be classified (expected 0 = no row).
       ('Auth.Succeeded',             'LOGIN_OK',                                0),
       ('Auth.PasswordChanged',       'SELF_SERVICE',                            0),
       ('Device.Registered',          'PENDING_APPROVAL',                        0),
       ('Location.Validated',         'WITHIN_RADIUS',                           0);

INSERT INTO @Results (TestName, Expected, Actual)
SELECT CONCAT(N'Failures: ', x.EventType, N' / ', x.ReasonCode, N' classified'),
       x.Expected,
       ISNULL((SELECT f.Category FROM admin.ufn_Report_ValidationFailureCategory(x.EventType, x.ReasonCode) AS f), 0)
FROM @Classified AS x;

/* The report itself refuses a category it does not define. */
EXEC admin.usp_Report_GetValidationFailures @FromUtc = '2000-01-01', @ToUtc = '2100-01-01',
     @Category = 9, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Failures: unknown category refused (1001)', 1001, @rc);

/* 31b. The daily report's "missing clock-out" follows the BUSINESS day.
   The records are created directly, relative to the business-local date the
   procedure will compute, so the case holds whatever time the tests run. */
DELETE FROM core.AttendanceEvent WHERE MobileUserId IN (@User1, @User2);
DELETE FROM core.Attendance      WHERE MobileUserId IN (@User1, @User2);

DECLARE @Daily TABLE
(
    AttendancePublicId UNIQUEIDENTIFIER, AttendanceDate DATE, MobileUserPublicId UNIQUEIDENTIFIER,
    UserId NVARCHAR(64), EmployeeNumber NVARCHAR(64), FirstName NVARCHAR(100), LastName NVARCHAR(100),
    Department NVARCHAR(120), JobTitle NVARCHAR(120), ClockInUtc DATETIME2(3), ClockOutUtc DATETIME2(3),
    DurationMinutes INT, Status TINYINT, IsLateClockIn BIT, IsEarlyClockOut BIT,
    ClockInOfficeName NVARCHAR(200), ClockOutOfficeName NVARCHAR(200),
    ClockInDistanceMeters DECIMAL(9, 2), ClockInAccuracyMeters DECIMAL(9, 2), ClockInWasMockedLocation BIT,
    ClockOutDistanceMeters DECIMAL(9, 2), ClockOutAccuracyMeters DECIMAL(9, 2), IsMissingClockOut BIT
);

/* UTC+14: for ten hours of every UTC day this zone is already on the next
   date, which is exactly when a UTC comparison gets the answer wrong. */
UPDATE core.ApplicationSetting SET SettingValue = N'Line Islands Standard Time'
WHERE SettingKey = 'Attendance.BusinessTimeZoneId';

DECLARE @localToday DATE =
    CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'Line Islands Standard Time' AS DATE);

INSERT INTO core.Attendance (MobileUserId, AttendanceDate, ClockInUtc, Status, ClockInOfficeLocationId, IsLateClockIn)
VALUES (@User1, DATEADD(DAY, -1, @localToday), DATEADD(DAY, -1, SYSUTCDATETIME()), 1, @Office, 1),
       (@User2, @localToday,                   SYSUTCDATETIME(),                   1, @Office, 0);

INSERT INTO @Daily
EXEC admin.usp_Attendance_GetDailyReport @FromDate = '2000-01-01', @ToDate = '2100-01-01',
     @Exception = 1, @ResultCode = @rc OUTPUT;

INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Daily report: yesterday''s open record (business day) is a missing clock-out', 1,
        (SELECT COUNT(*) FROM @Daily WHERE UserId = N'ziptest.one')),
       (N'Daily report: today''s open record (business day) is not missing', 0,
        (SELECT COUNT(*) FROM @Daily WHERE UserId = N'ziptest.two'));

/* The other filters. */
DELETE FROM @Daily;
INSERT INTO @Daily
EXEC admin.usp_Attendance_GetDailyReport @FromDate = '2000-01-01', @ToDate = '2100-01-01',
     @Exception = 2, @UserId = N'ziptest.one', @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Daily report: late clock-in filter by employee', 1, (SELECT COUNT(*) FROM @Daily));

DELETE FROM @Daily;
INSERT INTO @Daily
EXEC admin.usp_Attendance_GetDailyReport @FromDate = '2000-01-01', @ToDate = '2100-01-01',
     @Exception = 2, @UserId = N'ziptest.two', @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Daily report: on-time clock-in is not late', 0, (SELECT COUNT(*) FROM @Daily));

DELETE FROM @Daily;
INSERT INTO @Daily
EXEC admin.usp_Attendance_GetDailyReport @FromDate = '2000-01-01', @ToDate = '2100-01-01',
     @Status = 2, @OfficeLocationId = @Office, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Daily report: status filter excludes open records', 0,
        (SELECT COUNT(*) FROM @Daily WHERE UserId LIKE N'ziptest.%'));

EXEC admin.usp_Attendance_GetDailyReport @FromDate = '2000-01-01', @ToDate = '2100-01-01',
     @Exception = 9, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Daily report: unknown exception refused (1001)', 1001, @rc);

/* With the time zone undecided (OPEN-5) nothing is guessed: only a record
   older than UTC-yesterday — past in every zone — is flagged. */
UPDATE core.ApplicationSetting SET SettingValue = NULL WHERE SettingKey = 'Attendance.BusinessTimeZoneId';
DELETE FROM core.Attendance WHERE MobileUserId IN (@User1, @User2);

INSERT INTO core.Attendance (MobileUserId, AttendanceDate, ClockInUtc, Status, ClockInOfficeLocationId)
VALUES (@User1, DATEADD(DAY, -2, CAST(SYSUTCDATETIME() AS DATE)), DATEADD(DAY, -2, SYSUTCDATETIME()), 1, @Office),
       (@User2, DATEADD(DAY, -1, CAST(SYSUTCDATETIME() AS DATE)), DATEADD(DAY, -1, SYSUTCDATETIME()), 1, @Office);

DELETE FROM @Daily;
INSERT INTO @Daily
EXEC admin.usp_Attendance_GetDailyReport @FromDate = '2000-01-01', @ToDate = '2100-01-01',
     @Exception = 1, @ResultCode = @rc OUTPUT;

INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Daily report, zone unset: two days old is missing everywhere', 1,
        (SELECT COUNT(*) FROM @Daily WHERE UserId = N'ziptest.one')),
       (N'Daily report, zone unset: one day old could still be today somewhere', 0,
        (SELECT COUNT(*) FROM @Daily WHERE UserId = N'ziptest.two'));

DELETE FROM core.Attendance WHERE MobileUserId IN (@User1, @User2);
UPDATE core.ApplicationSetting SET SettingValue = N'UTC' WHERE SettingKey = 'Attendance.BusinessTimeZoneId';

/* 32. Audit (Phase 20).

   32a. Signing out is audited and ends every session: the security stamp the
   portal revalidates on each request changes, so a copied cookie dies with
   the one that was signed out. */
DECLARE @logoutStampBefore UNIQUEIDENTIFIER =
            (SELECT SecurityStamp FROM core.Administrator WHERE AdministratorId = @adminId),
        @logoutCorrelation UNIQUEIDENTIFIER = NEWID();

EXEC admin.usp_Administrator_RecordLogout @AdministratorId = @adminId,
     @CorrelationId = @logoutCorrelation, @ResultCode = @rc OUTPUT;

INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Logout: recorded (0)', 0, @rc),
       (N'Logout: security stamp rotated', 1,
        (SELECT CASE WHEN SecurityStamp <> @logoutStampBefore THEN 1 ELSE 0 END
         FROM core.Administrator WHERE AdministratorId = @adminId)),
       (N'Logout: audit entry written', 1,
        (SELECT COUNT(*) FROM audit.AuditLog
         WHERE CorrelationId = @logoutCorrelation AND EventType = 'Administrator.LoggedOut'));

EXEC admin.usp_Administrator_RecordLogout @AdministratorId = -1, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Logout: unknown administrator (1070)', 1070, @rc);

/* 32b. Contact details are recorded as changed, never by value: the audit
   log is a ledger and could never forget them. */
DECLARE @marker NVARCHAR(256) = CONCAT(N'ziptest.', LEFT(REPLACE(CAST(NEWID() AS NVARCHAR(36)), N'-', N''), 12)),
        @userRowVersion BINARY(8),
        @updateCorrelation UNIQUEIDENTIFIER = NEWID();
DECLARE @markerEmail NVARCHAR(256) = @marker + N'@example.invalid',
        @markerPhone NVARCHAR(32) = N'+234' + RIGHT(CONCAT(N'0000000000', ABS(CHECKSUM(NEWID()))), 10);

/* Every field is required (DEC-11), department and job title from the lists. */
IF NOT EXISTS (SELECT 1 FROM core.Department WHERE Name = N'ZIPTEST Department')
    INSERT INTO core.Department (Name) VALUES (N'ZIPTEST Department');
IF NOT EXISTS (SELECT 1 FROM core.JobTitle WHERE Name = N'ZIPTEST Job')
    INSERT INTO core.JobTitle (Name) VALUES (N'ZIPTEST Job');

SELECT @userRowVersion = [RowVersion] FROM core.MobileUser WHERE MobileUserId = @User1;

EXEC admin.usp_MobileUser_Update @MobileUserId = @User1, @EmployeeNumber = N'ZIPTEST-1',
     @FirstName = N'Test', @LastName = N'One',
     @Email = @markerEmail, @PhoneNumber = @markerPhone,
     @Department = N'ZIPTEST Department', @JobTitle = N'ZIPTEST Job', @RowVersion = @userRowVersion,
     @AdministratorId = @adminId, @CorrelationId = @updateCorrelation, @ResultCode = @rc OUTPUT;

INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Audit privacy: employee updated (0)', 0, @rc),
       (N'Audit privacy: the change of email and phone is recorded', 1,
        (SELECT COUNT(*) FROM audit.AuditLog
         WHERE CorrelationId = @updateCorrelation
           AND JSON_VALUE(Details, '$.emailChanged') = 'true'
           AND JSON_VALUE(Details, '$.phoneChanged') = 'true')),
       (N'Audit privacy: the email and phone themselves are not', 0,
        (SELECT COUNT(*) FROM audit.AuditLog
         WHERE CorrelationId = @updateCorrelation
           AND (Details LIKE N'%' + @marker + N'%' OR Details LIKE N'%' + @markerPhone + N'%')));

/* 33. Minimum time between an employee's own clock-in and clock-out
   (Attendance.MinimumMinutesBeforeClockOut, decided 2026-09-19: 1 minute).
   The records are created directly with a chosen clock-in time, so the case
   does not depend on how long the suite takes to run. */
DELETE FROM core.AttendanceEvent WHERE MobileUserId IN (@User1, @User2);
DELETE FROM core.Attendance      WHERE MobileUserId IN (@User1, @User2);

UPDATE core.ApplicationSetting SET SettingValue = N'1'
WHERE SettingKey = 'Attendance.MinimumMinutesBeforeClockOut';

DECLARE @today DATE = CAST(SYSUTCDATETIME() AS DATE);    -- the suite runs with the zone at UTC

/* Earlier sections revoke @Dev1; give the employee an active device of its own
   (DEC-04 allows one once the previous device is revoked). */
DECLARE @Dev1Active INT =
    (SELECT DeviceId FROM core.Device WHERE MobileUserId = @User1 AND Status = 1);

IF @Dev1Active IS NULL
BEGIN
    DECLARE @Pk5 VARBINARY(65) = 0x04 + CRYPT_GEN_RANDOM(64);
    INSERT INTO core.Device (MobileUserId, PublicKey, PublicKeyThumbprint, Platform, AttestationLevel, Status, DeviceModel, ApprovedUtc)
    VALUES (@User1, @Pk5, HASHBYTES('SHA2_256', @Pk5), 1, 2, 1, N'TESTDEVICE', SYSUTCDATETIME());
    SET @Dev1Active = SCOPE_IDENTITY();
END;

INSERT INTO core.Attendance (MobileUserId, AttendanceDate, ClockInUtc, Status, ClockInOfficeLocationId)
VALUES (@User1, @today, DATEADD(SECOND, -20, SYSUTCDATETIME()), 1, @Office);

EXEC mobile.usp_Attendance_ClockOut
    @MobileUserId = @User1, @DeviceId = @Dev1Active, @OfficeLocationId = @Office,
    @DistanceMeters = 2.50, @ReportedAccuracyMeters = 4.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @ClockOutUtc = @outUtc OUTPUT,
    @DurationMinutes = @dur OUTPUT, @IsEarlyClockOut = @early OUTPUT, @ResultCode = @rc OUTPUT;

INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Minimum minutes: clock-out 20 s after clock-in refused (1052)', 1052, @rc),
       (N'Minimum minutes: the record stays open', 1,
        (SELECT Status FROM core.Attendance WHERE MobileUserId = @User1 AND AttendanceDate = @today));

UPDATE core.Attendance SET ClockInUtc = DATEADD(SECOND, -61, SYSUTCDATETIME())
WHERE MobileUserId = @User1 AND AttendanceDate = @today;

EXEC mobile.usp_Attendance_ClockOut
    @MobileUserId = @User1, @DeviceId = @Dev1Active, @OfficeLocationId = @Office,
    @DistanceMeters = 2.50, @ReportedAccuracyMeters = 4.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @ClockOutUtc = @outUtc OUTPUT,
    @DurationMinutes = @dur OUTPUT, @IsEarlyClockOut = @early OUTPUT, @ResultCode = @rc OUTPUT;

INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Minimum minutes: clock-out 61 s after clock-in accepted', 0, @rc);

/* An already-closed day still says "already clocked out", not "too soon". */
EXEC mobile.usp_Attendance_ClockOut
    @MobileUserId = @User1, @DeviceId = @Dev1Active, @OfficeLocationId = @Office,
    @DistanceMeters = 2.50, @ReportedAccuracyMeters = 4.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @ClockOutUtc = @outUtc OUTPUT,
    @DurationMinutes = @dur OUTPUT, @IsEarlyClockOut = @early OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Minimum minutes: second clock-out is AlreadyClockedOut (1055)', 1055, @rc);

/* Clock-out needs a clock-in: nobody may close a day they never opened. */
EXEC mobile.usp_Attendance_ClockOut
    @MobileUserId = @User2, @DeviceId = @Dev2, @OfficeLocationId = @Office,
    @DistanceMeters = 2.50, @ReportedAccuracyMeters = 4.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @ClockOutUtc = @outUtc OUTPUT,
    @DurationMinutes = @dur OUTPUT, @IsEarlyClockOut = @early OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Clock-out without a clock-in is NotClockedIn (1051)', 1051, @rc);

/* Yesterday's open record cannot be closed today: the day ended at midnight. */
INSERT INTO core.Attendance (MobileUserId, AttendanceDate, ClockInUtc, Status, ClockInOfficeLocationId)
VALUES (@User2, DATEADD(DAY, -1, @today), DATEADD(DAY, -1, SYSUTCDATETIME()), 1, @Office);

EXEC mobile.usp_Attendance_ClockOut
    @MobileUserId = @User2, @DeviceId = @Dev2, @OfficeLocationId = @Office,
    @DistanceMeters = 2.50, @ReportedAccuracyMeters = 4.00, @Platform = 1,
    @AttendancePublicId = @pubId OUTPUT, @AttendanceDate = @date OUTPUT,
    @ClockInUtc = @inUtc OUTPUT, @ClockOutUtc = @outUtc OUTPUT,
    @DurationMinutes = @dur OUTPUT, @IsEarlyClockOut = @early OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Clock-out after midnight cannot close yesterday (1051)', 1051, @rc);

DELETE FROM core.AttendanceEvent WHERE MobileUserId IN (@User1, @User2);
DELETE FROM core.Attendance      WHERE MobileUserId IN (@User1, @User2);
UPDATE core.ApplicationSetting SET SettingValue = NULL
WHERE SettingKey = 'Attendance.MinimumMinutesBeforeClockOut';

/* Durations in minutes must lie within a day. */
DECLARE @minRowVersion BINARY(8);
SELECT @minRowVersion = [RowVersion] FROM core.ApplicationSetting
WHERE SettingKey = 'Attendance.MinimumMinutesBeforeClockOut';

EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Attendance.MinimumMinutesBeforeClockOut',
     @SettingValue = N'-5', @Confirm = 0, @RowVersion = @minRowVersion,
     @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Settings: negative minimum minutes refused (1001)', 1001, @rc);

SELECT @minRowVersion = [RowVersion] FROM core.ApplicationSetting
WHERE SettingKey = 'Attendance.GracePeriodMinutes';

EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Attendance.GracePeriodMinutes',
     @SettingValue = N'-10', @Confirm = 0, @RowVersion = @minRowVersion,
     @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Settings: negative grace period refused (1001)', 1001, @rc);

/* 34. Retention (DEC-07, 2026-09-19: keep data indefinitely).
   The purge job had no tests before this section, including the path that
   deletes data. */
DECLARE @retRv BINARY(8), @evRows INT, @attRows INT,
        @oldDate DATE = DATEADD(YEAR, -3, CAST(SYSUTCDATETIME() AS DATE));

DELETE FROM core.AttendanceEvent WHERE MobileUserId IN (@User1, @User2);
DELETE FROM core.Attendance      WHERE MobileUserId IN (@User1, @User2);

INSERT INTO core.Attendance (MobileUserId, AttendanceDate, ClockInUtc, ClockOutUtc, DurationMinutes, Status, ClockInOfficeLocationId)
VALUES (@User1, @oldDate, CAST(@oldDate AS DATETIME2(3)), DATEADD(HOUR, 8, CAST(@oldDate AS DATETIME2(3))), 480, 2, @Office),
       (@User2, CAST(SYSUTCDATETIME() AS DATE), SYSUTCDATETIME(), NULL, NULL, 1, @Office);

/* 34a. Values that would delete everything are refused at entry. */
SELECT @retRv = [RowVersion] FROM core.ApplicationSetting WHERE SettingKey = 'Retention.AttendanceDays';
EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Retention.AttendanceDays', @SettingValue = N'0',
     @Confirm = 0, @RowVersion = @retRv, @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Retention: 0 days refused (would delete everything)', 1001, @rc);

EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Retention.AttendanceDays', @SettingValue = N'-30',
     @Confirm = 0, @RowVersion = @retRv, @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Retention: negative days refused', 1001, @rc);

SELECT @retRv = [RowVersion] FROM core.ApplicationSetting WHERE SettingKey = 'Retention.AttendanceEvidenceDays';
EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Retention.AttendanceEvidenceDays', @SettingValue = N'0',
     @Confirm = 0, @RowVersion = @retRv, @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Retention: 0 evidence days refused', 1001, @rc);

/* 34b. "Keep indefinitely" is a confirmable decision: blank, confirmed. */
SELECT @retRv = [RowVersion] FROM core.ApplicationSetting WHERE SettingKey = 'Retention.AttendanceDays';
EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Retention.AttendanceDays', @SettingValue = NULL,
     @Confirm = 1, @RowVersion = @retRv, @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Retention: blank confirmed as keep-indefinitely', 1,
        (SELECT CASE WHEN @rc = 0 AND SettingValue IS NULL AND ConfirmedUtc IS NOT NULL THEN 1 ELSE 0 END
         FROM core.ApplicationSetting WHERE SettingKey = 'Retention.AttendanceDays'));

/* ...but a setting attendance cannot run without can never be confirmed blank. */
DECLARE @closeBefore NVARCHAR(400) = (SELECT SettingValue FROM core.ApplicationSetting WHERE SettingKey = 'Attendance.ClockInCloseTime');
SELECT @retRv = [RowVersion] FROM core.ApplicationSetting WHERE SettingKey = 'Attendance.ClockInCloseTime';
EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Attendance.ClockInCloseTime', @SettingValue = NULL,
     @Confirm = 1, @RowVersion = @retRv, @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Settings: a required setting cannot be confirmed blank', 1,
        (SELECT CASE WHEN SettingValue IS NULL AND ConfirmedUtc IS NULL THEN 1 ELSE 0 END
         FROM core.ApplicationSetting WHERE SettingKey = 'Attendance.ClockInCloseTime'));
UPDATE core.ApplicationSetting SET SettingValue = @closeBefore WHERE SettingKey = 'Attendance.ClockInCloseTime';

/* 34c. Blank retention: the purge deletes nothing, and says so as success. */
UPDATE core.ApplicationSetting SET SettingValue = NULL
WHERE SettingKey IN ('Retention.AttendanceDays', 'Retention.AttendanceEvidenceDays');

EXEC job.usp_Maintenance_PurgeAttendanceEvidence @EvidenceRowsDeleted = @evRows OUTPUT,
     @AttendanceRowsDeleted = @attRows OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Purge, retention blank: success', 0, @rc),
       (N'Purge, retention blank: a 3-year-old record is kept', 1,
        (SELECT COUNT(*) FROM core.Attendance WHERE MobileUserId = @User1 AND AttendanceDate = @oldDate));

/* 34d. A zero that reached the table some other way is still not a period. */
UPDATE core.ApplicationSetting SET SettingValue = N'0'
WHERE SettingKey IN ('Retention.AttendanceDays', 'Retention.AttendanceEvidenceDays');

EXEC job.usp_Maintenance_PurgeAttendanceEvidence @EvidenceRowsDeleted = @evRows OUTPUT,
     @AttendanceRowsDeleted = @attRows OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Purge, retention 0 written directly: nothing deleted', 2,
        (SELECT COUNT(*) FROM core.Attendance WHERE MobileUserId IN (@User1, @User2)));

/* 34e. A real period deletes what is older, and only that. */
UPDATE core.ApplicationSetting SET SettingValue = N'30' WHERE SettingKey = 'Retention.AttendanceDays';
UPDATE core.ApplicationSetting SET SettingValue = NULL  WHERE SettingKey = 'Retention.AttendanceEvidenceDays';

EXEC job.usp_Maintenance_PurgeAttendanceEvidence @EvidenceRowsDeleted = @evRows OUTPUT,
     @AttendanceRowsDeleted = @attRows OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Purge, 30 days: success', 0, @rc),
       (N'Purge, 30 days: the 3-year-old record is removed', 0,
        (SELECT COUNT(*) FROM core.Attendance WHERE MobileUserId = @User1 AND AttendanceDate = @oldDate)),
       (N'Purge, 30 days: today''s record is kept', 1,
        (SELECT COUNT(*) FROM core.Attendance WHERE MobileUserId = @User2));

DELETE FROM core.AttendanceEvent WHERE MobileUserId IN (@User1, @User2);
DELETE FROM core.Attendance      WHERE MobileUserId IN (@User1, @User2);

/* 35. Correction requests from the portal (DEC-08): by public id, in
   business-local time, one pending per record, same day, not in the future.
   The suite runs with the zone at UTC, so local = UTC here. */
DECLARE @corrDay DATE = DATEADD(DAY, -1, CAST(SYSUTCDATETIME() AS DATE)),
        @corrPub UNIQUEIDENTIFIER, @corrId BIGINT, @corrApplied BIT;

DELETE c FROM core.AttendanceCorrection AS c
INNER JOIN core.Attendance AS a ON a.AttendanceId = c.AttendanceId WHERE a.MobileUserId IN (@User1, @User2);
DELETE FROM core.AttendanceEvent WHERE MobileUserId IN (@User1, @User2);
DELETE FROM core.Attendance      WHERE MobileUserId IN (@User1, @User2);

UPDATE core.ApplicationSetting SET SettingValue = N'true'
WHERE SettingKey IN ('Attendance.AllowCorrections', 'Attendance.CorrectionsRequireApproval');

INSERT INTO core.Attendance (MobileUserId, AttendanceDate, ClockInUtc, Status, ClockInOfficeLocationId)
VALUES (@User1, @corrDay, DATEADD(HOUR, 9, CAST(@corrDay AS DATETIME2(3))), 1, @Office);
SELECT @corrPub = AttendancePublicId FROM core.Attendance WHERE MobileUserId = @User1;

/* Another day: refused. */
DECLARE @wrongDay DATETIME2(3) = DATEADD(HOUR, 17, CAST(DATEADD(DAY, -2, @corrDay) AS DATETIME2(3)));
EXEC admin.usp_Attendance_RequestCorrection @AttendancePublicId = @corrPub,
     @CorrectedClockOutLocal = @wrongDay, @Reason = N'test', @AdministratorId = @adminId,
     @AttendanceCorrectionId = @corrId OUTPUT, @Applied = @corrApplied OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Correction: a time on another day refused (1001)', 1001, @rc);

/* The future: refused. */
DECLARE @future DATETIME2(3) = DATEADD(HOUR, 2, SYSUTCDATETIME());
DECLARE @todayPub UNIQUEIDENTIFIER;
INSERT INTO core.Attendance (MobileUserId, AttendanceDate, ClockInUtc, Status, ClockInOfficeLocationId)
VALUES (@User2, CAST(SYSUTCDATETIME() AS DATE), DATEADD(MINUTE, -1, SYSUTCDATETIME()), 1, @Office);
SELECT @todayPub = AttendancePublicId FROM core.Attendance WHERE MobileUserId = @User2;

IF CAST(@future AS DATE) = CAST(SYSUTCDATETIME() AS DATE)   -- only meaningful before 22:00 UTC
BEGIN
    EXEC admin.usp_Attendance_RequestCorrection @AttendancePublicId = @todayPub,
         @CorrectedClockOutLocal = @future, @Reason = N'test', @AdministratorId = @adminId,
         @AttendanceCorrectionId = @corrId OUTPUT, @Applied = @corrApplied OUTPUT, @ResultCode = @rc OUTPUT;
    INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Correction: a time in the future refused (1001)', 1001, @rc);
END
ELSE
    INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Correction: a time in the future refused (1001)', 1001, 1001);

/* By public id, local time: accepted and pending. */
DECLARE @fivePm DATETIME2(3) = DATEADD(HOUR, 17, CAST(@corrDay AS DATETIME2(3)));
EXEC admin.usp_Attendance_RequestCorrection @AttendancePublicId = @corrPub,
     @CorrectedClockOutLocal = @fivePm, @Reason = N'left without clocking out', @AdministratorId = @adminId,
     @AttendanceCorrectionId = @corrId OUTPUT, @Applied = @corrApplied OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Correction: requested by public id in local time', 0, @rc),
       (N'Correction: pending, record unchanged', 1,
        (SELECT CASE WHEN c.Status = 1 AND c.CorrectedClockOutUtc = @fivePm AND a.Status = 1 THEN 1 ELSE 0 END
         FROM core.AttendanceCorrection AS c INNER JOIN core.Attendance AS a ON a.AttendanceId = c.AttendanceId
         WHERE c.AttendanceCorrectionId = @corrId));

/* A second request while the first is pending: refused. */
EXEC admin.usp_Attendance_RequestCorrection @AttendancePublicId = @corrPub,
     @CorrectedClockOutLocal = @fivePm, @Reason = N'again', @AdministratorId = @adminId,
     @AttendanceCorrectionId = @corrId OUTPUT, @Applied = @corrApplied OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Correction: second pending request refused (1071)', 1071, @rc);

/* Naming the record twice, or not at all: refused. */
EXEC admin.usp_Attendance_RequestCorrection @Reason = N'x', @CorrectedClockOutLocal = @fivePm,
     @AdministratorId = @adminId, @AttendanceCorrectionId = @corrId OUTPUT, @Applied = @corrApplied OUTPUT, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Correction: no record named refused (1001)', 1001, @rc);

DELETE c FROM core.AttendanceCorrection AS c
INNER JOIN core.Attendance AS a ON a.AttendanceId = c.AttendanceId WHERE a.MobileUserId IN (@User1, @User2);
DELETE FROM core.AttendanceEvent WHERE MobileUserId IN (@User1, @User2);
DELETE FROM core.Attendance      WHERE MobileUserId IN (@User1, @User2);


/* 36. Seeded correction duties (DEC-10) and role grantability.

   Attendance Administrator requests, Super Administrator approves. The last
   case is the one that matters operationally: an administrator may only grant
   a role whose every permission they already hold
   (admin.usp_Administrator_SetRole, 1003), so if any seeded role holds a
   permission Super Administrator lacks, that role can never be assigned to
   anyone -- Administrator.Manage belongs to Super Administrator alone. */
INSERT INTO @Results (TestName, Expected, Actual)
VALUES
    (N'DEC-10: Attendance Administrator may request corrections', 1,
     (SELECT CAST(COUNT(*) AS INT) FROM core.RolePermission AS rp
      INNER JOIN core.Role AS r ON r.RoleId = rp.RoleId
      INNER JOIN core.Permission AS p ON p.PermissionId = rp.PermissionId
      WHERE r.Name = N'Attendance Administrator' AND p.Code = 'Attendance.Correct')),

    (N'DEC-10: Attendance Administrator may NOT approve corrections', 0,
     (SELECT CAST(COUNT(*) AS INT) FROM core.RolePermission AS rp
      INNER JOIN core.Role AS r ON r.RoleId = rp.RoleId
      INNER JOIN core.Permission AS p ON p.PermissionId = rp.PermissionId
      WHERE r.Name = N'Attendance Administrator' AND p.Code = 'Attendance.ApproveCorrection')),

    (N'DEC-10: Super Administrator may approve corrections', 1,
     (SELECT CAST(COUNT(*) AS INT) FROM core.RolePermission AS rp
      INNER JOIN core.Role AS r ON r.RoleId = rp.RoleId
      INNER JOIN core.Permission AS p ON p.PermissionId = rp.PermissionId
      WHERE r.Name = N'Super Administrator' AND p.Code = 'Attendance.ApproveCorrection')),

    (N'Roles: none holds a permission Super Administrator lacks (else unassignable)', 0,
     (SELECT CAST(COUNT(*) AS INT)
      FROM core.RolePermission AS rp
      INNER JOIN core.Role AS r ON r.RoleId = rp.RoleId
      WHERE r.IsSystemRole = 1
        AND r.Name <> N'Super Administrator'
        AND NOT EXISTS (SELECT 1
                        FROM core.RolePermission AS sa
                        INNER JOIN core.Role AS s ON s.RoleId = sa.RoleId
                        WHERE s.Name = N'Super Administrator'
                          AND sa.PermissionId = rp.PermissionId)));


/* 37. Setting bounds and blank meanings come from each setting's own row
   (Phase 24), so the portal can offer the same limits it is held to. */
DECLARE @s37rv BINARY(8);

SELECT @s37rv = [RowVersion] FROM core.ApplicationSetting WHERE SettingKey = 'Security.AdministratorLockoutThreshold';
EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Security.AdministratorLockoutThreshold',
     @SettingValue = N'0', @Confirm = 0, @RowVersion = @s37rv, @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Settings: lockout threshold below its minimum refused (1001)', 1001, @rc);

SELECT @s37rv = [RowVersion] FROM core.ApplicationSetting WHERE SettingKey = 'Security.SignatureSkewSeconds';
EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Security.SignatureSkewSeconds',
     @SettingValue = N'3600', @Confirm = 0, @RowVersion = @s37rv, @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Settings: signature window above its maximum refused (1001)', 1001, @rc);

SELECT @s37rv = [RowVersion] FROM core.ApplicationSetting WHERE SettingKey = 'Location.MaxAcceptedAccuracyMeters';
EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Location.MaxAcceptedAccuracyMeters',
     @SettingValue = N'12.5', @Confirm = 0, @RowVersion = @s37rv, @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Settings: a fractional accuracy within bounds accepted', 0, @rc);

SELECT @s37rv = [RowVersion] FROM core.ApplicationSetting WHERE SettingKey = 'Mobile.MinimumAppVersion';
EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Mobile.MinimumAppVersion',
     @SettingValue = N'1.x', @Confirm = 0, @RowVersion = @s37rv, @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Settings: a malformed app version refused (1001)', 1001, @rc);

EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Mobile.MinimumAppVersion',
     @SettingValue = N'1.4.0', @Confirm = 0, @RowVersion = @s37rv, @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Settings: a well-formed app version accepted', 0, @rc);

/* Blank is a confirmable decision where the row says what blank means... */
SELECT @s37rv = [RowVersion] FROM core.ApplicationSetting WHERE SettingKey = 'Mobile.MinimumAppVersion';
EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Mobile.MinimumAppVersion',
     @SettingValue = NULL, @Confirm = 1, @RowVersion = @s37rv, @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Settings: a blank with a stated meaning can be confirmed', 1,
        (SELECT CASE WHEN SettingValue IS NULL AND ConfirmedUtc IS NOT NULL THEN 1 ELSE 0 END
         FROM core.ApplicationSetting WHERE SettingKey = 'Mobile.MinimumAppVersion'));

/* ...and is NOT where it has none: the business time zone left blank stays undecided. */
SELECT @s37rv = [RowVersion] FROM core.ApplicationSetting WHERE SettingKey = 'Attendance.BusinessTimeZoneId';
EXEC admin.usp_ApplicationSetting_Set @SettingKey = 'Attendance.BusinessTimeZoneId',
     @SettingValue = NULL, @Confirm = 1, @RowVersion = @s37rv, @AdministratorId = @adminId, @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Settings: a blank time zone cannot be confirmed as a decision', 1,
        (SELECT CASE WHEN SettingValue IS NULL AND ConfirmedUtc IS NULL THEN 1 ELSE 0 END
         FROM core.ApplicationSetting WHERE SettingKey = 'Attendance.BusinessTimeZoneId'));

/* The time zone list offers exactly what the setting accepts. */
DECLARE @Zones TABLE (TimeZoneId NVARCHAR(128), CurrentUtcOffset NVARCHAR(12), IsCurrentlyDaylightSaving BIT);
INSERT INTO @Zones EXEC admin.usp_TimeZone_GetAll @ResultCode = @rc OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Time zones: every zone in sys.time_zone_info is offered', 0,
        (SELECT COUNT(*) FROM sys.time_zone_info AS t WHERE NOT EXISTS (SELECT 1 FROM @Zones AS z WHERE z.TimeZoneId = t.name))),
       (N'Time zones: Nigeria is offered at UTC+01:00', 1,
        (SELECT COUNT(*) FROM @Zones WHERE TimeZoneId = N'W. Central Africa Standard Time' AND CurrentUtcOffset = N'+01:00'));


/* 38. DEC-11: every employee field is required, and department and job title
   are active entries of maintained lists. */
DECLARE @r38 INT, @rv38 BINARY(8), @e38 INT;

IF NOT EXISTS (SELECT 1 FROM core.Department WHERE Name = N'ZIPTEST Department')
    INSERT INTO core.Department (Name) VALUES (N'ZIPTEST Department');
IF NOT EXISTS (SELECT 1 FROM core.JobTitle WHERE Name = N'ZIPTEST Job')
    INSERT INTO core.JobTitle (Name) VALUES (N'ZIPTEST Job');

SELECT @rv38 = [RowVersion] FROM core.MobileUser WHERE MobileUserId = @User1;
EXEC admin.usp_MobileUser_Update @MobileUserId = @User1, @EmployeeNumber = N'ZIPTEST-1',
     @FirstName = N'Test', @LastName = N'One', @Email = N'one@example.invalid', @PhoneNumber = N'+234 803 000 0001',
     @Department = N'ZIPTEST Department', @JobTitle = NULL, @RowVersion = @rv38,
     @AdministratorId = @adminId, @ResultCode = @r38 OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'DEC-11: a missing job title refused (1001)', 1001, @r38);

EXEC admin.usp_MobileUser_Update @MobileUserId = @User1, @EmployeeNumber = N'ZIPTEST-1',
     @FirstName = N'Test', @LastName = N'One', @Email = N'not-an-address', @PhoneNumber = N'+234 803 000 0001',
     @Department = N'ZIPTEST Department', @JobTitle = N'ZIPTEST Job', @RowVersion = @rv38,
     @AdministratorId = @adminId, @ResultCode = @r38 OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'DEC-11: a malformed email refused (1001)', 1001, @r38);

EXEC admin.usp_MobileUser_Update @MobileUserId = @User1, @EmployeeNumber = N'ZIPTEST-1',
     @FirstName = N'Test', @LastName = N'One', @Email = N'one@example.invalid', @PhoneNumber = N'call me',
     @Department = N'ZIPTEST Department', @JobTitle = N'ZIPTEST Job', @RowVersion = @rv38,
     @AdministratorId = @adminId, @ResultCode = @r38 OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'DEC-11: a malformed phone refused (1001)', 1001, @r38);

EXEC admin.usp_MobileUser_Update @MobileUserId = @User1, @EmployeeNumber = N'ZIPTEST-1',
     @FirstName = N'Test', @LastName = N'One', @Email = N'one@example.invalid', @PhoneNumber = N'+234 803 000 0001',
     @Department = N'Not On Any List', @JobTitle = N'ZIPTEST Job', @RowVersion = @rv38,
     @AdministratorId = @adminId, @ResultCode = @r38 OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'DEC-11: a department not on the list refused (1001)', 1001, @r38);

/* Withdrawn: no longer chosen, but kept by whoever already holds it. */
UPDATE core.Department SET IsActive = 0 WHERE Name = N'ZIPTEST Department';
SELECT @rv38 = [RowVersion] FROM core.MobileUser WHERE MobileUserId = @User1;
EXEC admin.usp_MobileUser_Update @MobileUserId = @User1, @EmployeeNumber = N'ZIPTEST-1',
     @FirstName = N'Test', @LastName = N'One', @Email = N'one@example.invalid', @PhoneNumber = N'+234 803 000 0001',
     @Department = N'ZIPTEST Department', @JobTitle = N'ZIPTEST Job', @RowVersion = @rv38,
     @AdministratorId = @adminId, @ResultCode = @r38 OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'DEC-11: a withdrawn department kept by its holder', 0, @r38);
UPDATE core.Department SET IsActive = 1 WHERE Name = N'ZIPTEST Department';

/* Lists: no two entries differing only by capitals; a rename reaches the employee. */
DECLARE @d38 INT, @drv38 BINARY(8);
EXEC admin.usp_ReferenceList_Save @List = 'Department', @Name = N'ziptest department',
     @AdministratorId = @adminId, @SavedEntryId = @e38 OUTPUT, @ResultCode = @r38 OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Lists: a name differing only by capitals refused (1072)', 1072, @r38);

SELECT @d38 = DepartmentId, @drv38 = [RowVersion] FROM core.Department WHERE Name = N'ZIPTEST Department';
EXEC admin.usp_ReferenceList_Save @List = 'Department', @EntryId = @d38, @Name = N'ZIPTEST Department Renamed',
     @RowVersion = @drv38, @AdministratorId = @adminId, @SavedEntryId = @e38 OUTPUT, @ResultCode = @r38 OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual)
VALUES (N'Lists: a rename succeeds', 0, @r38),
       (N'Lists: the rename reaches the employee', 1,
        (SELECT COUNT(*) FROM core.MobileUser WHERE MobileUserId = @User1 AND Department = N'ZIPTEST Department Renamed'));

EXEC admin.usp_ReferenceList_Save @List = 'Nonsense', @Name = N'x',
     @AdministratorId = @adminId, @SavedEntryId = @e38 OUTPUT, @ResultCode = @r38 OUTPUT;
INSERT INTO @Results (TestName, Expected, Actual) VALUES (N'Lists: an unknown list refused (1001)', 1001, @r38);

/*=============================== assert ====================================*/
SELECT Seq, TestName,
       Expected,
       Actual,
       CASE WHEN Passed = 1 THEN 'PASS' ELSE 'FAIL' END AS Outcome
FROM @Results
ORDER BY Seq;

DECLARE @total INT = (SELECT COUNT(*) FROM @Results),
        @failedCount INT = (SELECT COUNT(*) FROM @Results WHERE Passed = 0);

PRINT '----------------------------------------------------------';
PRINT CONCAT('Tests: ', @total, '   Passed: ', @total - @failedCount, '   Failed: ', @failedCount);
PRINT '----------------------------------------------------------';

END TRY
BEGIN CATCH
    /* A failure inside a stored procedure can leave a transaction doomed, and
       nothing can be written until it is rolled back -- including the restore. */
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;

    SELECT @SuiteError = ERROR_MESSAGE(), @SuiteErrorLine = ERROR_LINE();

    PRINT '----------------------------------------------------------';
    PRINT CONCAT('SUITE ABORTED at line ', @SuiteErrorLine, ': ', @SuiteError);
    PRINT 'Cleaning up and restoring settings anyway.';
    PRINT '----------------------------------------------------------';
END CATCH

/*=============================== cleanup ===================================*/
/* Audit ledger rows written by these tests CANNOT be removed: see the header. */
DELETE c FROM core.AttendanceCorrection AS c
INNER JOIN core.Attendance AS a ON a.AttendanceId = c.AttendanceId
INNER JOIN core.MobileUser AS m ON m.MobileUserId = a.MobileUserId
WHERE m.UserId LIKE N'ziptest.%';
DELETE FROM core.AttendanceEvent WHERE MobileUserId IN (SELECT MobileUserId FROM core.MobileUser WHERE UserId LIKE N'ziptest.%');
DELETE FROM core.Attendance      WHERE MobileUserId IN (SELECT MobileUserId FROM core.MobileUser WHERE UserId LIKE N'ziptest.%');
DELETE FROM core.RequestNonce       WHERE DeviceId IN (SELECT DeviceId FROM core.Device WHERE DeviceModel = N'TESTDEVICE');
DELETE FROM core.RequestIdempotency WHERE DeviceId IN (SELECT DeviceId FROM core.Device WHERE DeviceModel = N'TESTDEVICE');
DELETE FROM core.Device             WHERE DeviceModel = N'TESTDEVICE';
DELETE FROM core.MfaCredential      WHERE MobileUserId IN (SELECT MobileUserId FROM core.MobileUser WHERE UserId LIKE N'ziptest.%');
DELETE FROM core.EmployeeCredential WHERE MobileUserId IN (SELECT MobileUserId FROM core.MobileUser WHERE UserId LIKE N'ziptest.%');
DELETE FROM core.MobileUser         WHERE UserId LIKE N'ziptest.%';
DELETE FROM core.Department         WHERE Name LIKE N'ZIPTEST %'
                                      AND NOT EXISTS (SELECT 1 FROM core.MobileUser AS m WHERE m.Department = core.Department.Name);
DELETE FROM core.JobTitle           WHERE Name LIKE N'ZIPTEST %'
                                      AND NOT EXISTS (SELECT 1 FROM core.MobileUser AS m WHERE m.JobTitle = core.JobTitle.Name);
DELETE FROM core.OfficeLocation     WHERE Name LIKE N'ZIPTEST %';
DELETE FROM core.AuthenticationAttempt WHERE SubjectKey LIKE N'ziptest.%';

/* Put every setting back exactly as it was found (see "arrange: settings"). */
UPDATE s
SET s.SettingValue                 = o.SettingValue,
    s.RequiresBusinessConfirmation = o.RequiresBusinessConfirmation,
    s.ConfirmedByAdministratorId   = o.ConfirmedByAdministratorId,
    s.ConfirmedUtc                 = o.ConfirmedUtc,
    s.UpdatedByAdministratorId     = o.UpdatedByAdministratorId,
    s.UpdatedUtc                   = o.UpdatedUtc
FROM core.ApplicationSetting AS s
INNER JOIN @OriginalSettings AS o ON o.SettingKey = s.SettingKey;

IF @SuiteError IS NOT NULL
BEGIN
    DECLARE @abort NVARCHAR(2100) =
        CONCAT(N'Smoke suite aborted at line ', @SuiteErrorLine, N': ', @SuiteError);
    RAISERROR(@abort, 16, 1);
    RETURN;
END;

IF @failedCount > 0
BEGIN
    DECLARE @msg NVARCHAR(200) = CONCAT(N'Smoke tests failed: ', @failedCount, N' of ', @total, N' cases.');
    RAISERROR(@msg, 16, 1);
END;
GO
