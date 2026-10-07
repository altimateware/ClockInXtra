/*==============================================================================
  ClockInXtra — Seed: application settings
  File  : database/seed/21_seed_settings.sql
  Phase : 5

  Re-runnable. Rows are matched by SettingKey and are only INSERTed; existing
  values are never overwritten, so a redeployment cannot undo an
  administrator's configuration.

  READ THIS BEFORE CHANGING ANYTHING HERE
  ---------------------------------------
  Settings that represent a BUSINESS decision are seeded with a NULL value.
  A NULL means "the business has not decided yet". The attendance procedures
  refuse to operate and return ATTENDANCE_NOT_CONFIGURED while any mandatory
  business setting is NULL, and the readiness health check reports Degraded.
  That is deliberate: Claude.md §15 and §68 forbid inventing business rules,
  so the system states the gap instead of guessing a value.

  Settings that represent a TECHNICAL default (skew windows, purge intervals)
  carry a value and are marked as not requiring business confirmation.

  Settings that represent a SECURITY default carry a safe value but still
  require business confirmation, so the decision is made consciously and
  recorded against a named administrator.
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

DECLARE @Seed TABLE
(
    SettingKey     VARCHAR(100)  NOT NULL PRIMARY KEY,
    SettingValue   NVARCHAR(400) NULL,
    DataType       VARCHAR(20)   NOT NULL,
    Category       NVARCHAR(64)  NOT NULL,
    Description    NVARCHAR(400) NOT NULL,
    AllowedValues  NVARCHAR(400) NULL,
    RequiresConfirmation BIT     NOT NULL,
    IsMobileVisible BIT          NOT NULL
);

/*========================= ATTENDANCE — business decisions =========================
  Every row below is NULL because it is an OPEN REQUIREMENT. See
  docs/architecture/01-requirements-register.md §6.1.                              */
INSERT INTO @Seed
    (SettingKey, SettingValue, DataType, Category, Description, AllowedValues, RequiresConfirmation, IsMobileVisible)
VALUES
    ('Attendance.BusinessTimeZoneId', NULL, 'timezone', N'Attendance',
     N'OPEN-5. Windows time zone identifier that defines the attendance day. Must exist in sys.time_zone_info. No default is assumed for any country.',
     NULL, 1, 0),

    ('Attendance.ClockInOpenTime', NULL, 'time', N'Attendance',
     N'Earliest local time a clock-in is accepted.', NULL, 1, 1),

    ('Attendance.ClockInCloseTime', NULL, 'time', N'Attendance',
     N'OPEN-6. Local time at which clock-in closes. This is one of the two explicitly required settings.',
     NULL, 1, 1),

    ('Attendance.ClockInAfterCloseAction', NULL, 'enum', N'Attendance',
     N'OPEN-6/OPEN-8. What happens to a clock-in attempted after the closing time.',
     N'Reject,AcceptAndFlagLate', 1, 0),

    ('Attendance.ClockOutOpenTime', NULL, 'time', N'Attendance',
     N'OPEN-7. Local time from which clock-out is accepted. This is the second explicitly required setting.',
     NULL, 1, 1),

    ('Attendance.ClockOutBeforeOpenAction', NULL, 'enum', N'Attendance',
     N'OPEN-7/OPEN-9. What happens to a clock-out attempted before the opening time.',
     N'Reject,AcceptAndFlagEarly', 1, 0),

    ('Attendance.GracePeriodMinutes', NULL, 'int', N'Attendance',
     N'Grace period applied to the clock-in closing time, if the business approves one.',
     NULL, 1, 0),

    ('Attendance.MinimumMinutesBeforeClockOut', NULL, 'int', N'Attendance',
     N'Minutes that must pass after an employee''s own clock-in before they may clock out (0-1440). Empty means no minimum.',
     NULL, 1, 0),

    ('Attendance.AllowCorrections', NULL, 'bool', N'Attendance',
     N'OPEN-10. Whether administrators may correct attendance records at all.', NULL, 1, 0),

    ('Attendance.CorrectionsRequireApproval', NULL, 'bool', N'Attendance',
     N'OPEN-12. Whether a correction needs a second administrator to approve it.', NULL, 1, 0),

/*========================= LOCATION — security policy =========================*/
    ('Location.AccuracyPolicy', N'DistanceAndAccuracyThreshold', 'enum', N'Location',
     N'OPEN-25. How reported accuracy affects the decision. The configured radius is never widened to compensate for poor accuracy.',
     N'DistanceOnly,DistanceAndAccuracyThreshold', 1, 0),

    ('Location.MaxAcceptedAccuracyMeters', NULL, 'decimal', N'Location',
     N'OPEN-25. Worst reported accuracy still accepted. When NULL, the matched office''s AllowedRadiusMeters is used, which is the strict interim rule.',
     NULL, 1, 0),

    ('Location.RejectMockedLocations', N'true', 'bool', N'Location',
     N'OPEN-26. Reject positions flagged as mock (Android) or software-simulated (iOS 15+). Security default is to reject.',
     NULL, 1, 0),

/*========================= SECURITY =========================*/
    ('Security.DeviceRegistrationRequiresApproval', N'true', 'bool', N'Security',
     N'OPEN-32. Whether a first device registration waits for administrator approval. Replacement devices always require approval (DEC-04).',
     NULL, 1, 0),

    ('Security.RequireHardwareAttestationAndroid', N'true', 'bool', N'Security',
     N'Require a valid Android key attestation chain showing a hardware-backed key, locked bootloader and verified boot.',
     NULL, 1, 0),

    ('Security.RequireHardwareAttestationIos', N'true', 'bool', N'Security',
     N'DEC-05 (2026-09-12): Apple App Attest is required for iOS registration. The device contacts Apple''s servers once per key; verification happens on our servers, so no attendance decision depends on Apple being reachable. Not available on simulators or app extensions.',
     NULL, 1, 0),

    ('Security.RequireAdministratorMfa', N'true', 'bool', N'Security',
     N'OPEN-34. Require an authenticator code for administration portal sign-in.', NULL, 1, 0),

    ('Security.CollapseCredentialErrorCodes', N'true', 'bool', N'Security',
     N'OPEN-38. Return INVALID_CREDENTIALS for wrong user, password or OTP so the API cannot confirm a correct password. The precise reason is recorded in security events.',
     NULL, 1, 0),

    ('Security.MobileLockoutThreshold', N'5', 'int', N'Security',
     N'Consecutive failures before a mobile account is locked.', NULL, 0, 0),

    ('Security.MobileLockoutMinutes', N'15', 'int', N'Security',
     N'How long a mobile account stays locked.', NULL, 0, 0),

    ('Security.AdministratorLockoutThreshold', N'5', 'int', N'Security',
     N'Consecutive failures before an administrator account is locked.', NULL, 0, 0),

    ('Security.AdministratorLockoutMinutes', N'15', 'int', N'Security',
     N'How long an administrator account stays locked.', NULL, 0, 0),

    ('Security.SignatureSkewSeconds', N'120', 'int', N'Security',
     N'Accepted difference between the signature created time and server time.', NULL, 0, 0),

    ('Security.TotpStepTolerance', N'1', 'int', N'Security',
     N'Authenticator time steps accepted either side of the current one. One step (30 seconds) covers ordinary phone clock drift. Raising it lengthens how long an observed code remains worth stealing, so it is a security setting, not a convenience one.', NULL, 0, 0),

    ('Security.ChallengeLifetimeSeconds', N'300', 'int', N'Security',
     N'Lifetime of a device registration challenge.', NULL, 0, 0),

/*========================= MOBILE =========================*/
    ('Mobile.MinimumAppVersion', NULL, 'string', N'Mobile',
     N'Lowest app version accepted by the API. Set at first release.', NULL, 0, 1),

    ('Mobile.MinimumAndroidSdk', N'28', 'int', N'Mobile',
     N'OPEN-31. Recommended floor: Android 9 (API 28), where hardware-enforced unlocked-device keys and cleartext-disabled networking begin.',
     NULL, 1, 1),

    ('Mobile.MinimumIosVersion', N'15', 'int', N'Mobile',
     N'OPEN-31. Recommended floor: iOS 15, the Flutter minimum, which also reports software-simulated locations.',
     NULL, 1, 1),

/*========================= RETENTION =========================*/
    ('Retention.AttendanceDays', NULL, 'int', N'Retention',
     N'OPEN-14. Retention period for attendance records. NULL means retain indefinitely and the purge job stays inert. Legal must decide (OPEN-41).',
     NULL, 1, 0),

    ('Retention.AttendanceEvidenceDays', NULL, 'int', N'Retention',
     N'OPEN-14. Retention period for per-event location evidence, in days (1-36500). NULL means retain indefinitely and the purge job stays inert.', NULL, 1, 0),

    ('Retention.RequestNonceHours', N'2', 'int', N'Retention',
     N'How long replay nonces are kept. Must exceed twice Security.SignatureSkewSeconds.', NULL, 0, 0),

    ('Retention.IdempotencyHours', N'48', 'int', N'Retention',
     N'How long idempotency results are kept for safe retries.', NULL, 0, 0);

INSERT INTO core.ApplicationSetting
    (SettingKey, SettingValue, DataType, Category, Description, AllowedValues,
     RequiresBusinessConfirmation, IsMobileVisible, ConfirmedUtc)
SELECT s.SettingKey, s.SettingValue, s.DataType, s.Category, s.Description, s.AllowedValues,
       s.RequiresConfirmation, s.IsMobileVisible,
       /* Technical defaults are considered settled on deployment; business and
          security settings stay unconfirmed until an administrator confirms. */
       CASE WHEN s.RequiresConfirmation = 0 THEN SYSUTCDATETIME() ELSE NULL END
FROM @Seed AS s
WHERE NOT EXISTS (SELECT 1 FROM core.ApplicationSetting AS e WHERE e.SettingKey = s.SettingKey);

/*------------------------------------------------------------------------------
  Input metadata: bounds, units and what a blank means.

  Unlike values, this IS written over existing rows on every deployment: it is
  the definition of the setting, not a decision about it, the same way
  permission descriptions are kept in step. It never changes a value.

  The bounds are technical guard rails, not business rules. Each refuses only
  what would break the system or defeat a control:
    - lockout thresholds below 3 lock out anyone who mistypes twice; above 20
      the lockout stops slowing a guessing attack;
    - a signature window above 10 minutes widens the replay window for no
      benefit, below 30 seconds ordinary phone clock drift fails requests;
    - minimum Android SDK 24 and iOS 15 are the Flutter floors (OPEN-31).
------------------------------------------------------------------------------*/
DECLARE @Meta TABLE
(
    SettingKey   VARCHAR(100)   NOT NULL PRIMARY KEY,
    MinValue     DECIMAL(18, 4) NULL,
    MaxValue     DECIMAL(18, 4) NULL,
    Unit         NVARCHAR(24)   NULL,
    BlankMeaning NVARCHAR(80)   NULL
);

INSERT INTO @Meta (SettingKey, MinValue, MaxValue, Unit, BlankMeaning)
VALUES
    ('Attendance.ClockInOpenTime',              NULL,  NULL, NULL,       N'No earliest time: open from midnight'),
    ('Attendance.GracePeriodMinutes',           0,     1440, N'minutes', N'No grace period'),
    ('Attendance.MinimumMinutesBeforeClockOut', 0,     1440, N'minutes', N'No minimum'),

    ('Location.MaxAcceptedAccuracyMeters',      1,     1000, N'metres',  NULL),

    ('Mobile.MinimumAndroidSdk',                24,    40,   N'API level', NULL),
    ('Mobile.MinimumIosVersion',                15,    30,   N'major version', NULL),

    ('Retention.AttendanceDays',                1,     36500, N'days',   N'Keep indefinitely'),
    ('Retention.AttendanceEvidenceDays',        1,     36500, N'days',   N'Keep indefinitely'),
    ('Retention.RequestNonceHours',             1,     168,   N'hours',  NULL),
    ('Retention.IdempotencyHours',              24,    720,   N'hours',  NULL),

    ('Security.AdministratorLockoutThreshold',  3,     20,    N'attempts', NULL),
    ('Security.AdministratorLockoutMinutes',    1,     1440,  N'minutes',  NULL),
    ('Security.MobileLockoutThreshold',         3,     20,    N'attempts', NULL),
    ('Security.MobileLockoutMinutes',           1,     1440,  N'minutes',  NULL),
    ('Security.ChallengeLifetimeSeconds',       60,    1800,  N'seconds',  NULL),
    ('Security.SignatureSkewSeconds',           30,    600,   N'seconds',  NULL),
    ('Security.TotpStepTolerance',               1,     10,    N'steps',    NULL),

    ('Mobile.MinimumAppVersion',                NULL,  NULL,  NULL,       N'No minimum version');

UPDATE s
SET s.MinValue     = m.MinValue,
    s.MaxValue     = m.MaxValue,
    s.Unit         = m.Unit,
    s.BlankMeaning = m.BlankMeaning
FROM core.ApplicationSetting AS s
INNER JOIN @Meta AS m ON m.SettingKey = s.SettingKey;

COMMIT TRANSACTION;
GO

/* Report what still blocks attendance processing, so the deployment output
   makes the outstanding business decisions visible rather than silent. */
SELECT SettingKey, Category, Description
FROM core.ApplicationSetting
WHERE SettingValue IS NULL
  AND ConfirmedUtc IS NULL      -- a blank confirmed as the decision is not outstanding
ORDER BY Category, SettingKey;
GO

PRINT 'Application settings seeded. Rows listed above are still UNSET and block attendance processing.';
GO
