/*==============================================================================
  core.usp_ApplicationSetting_GetAttendanceRules
  Phase : 6
  Called by: mobile.usp_Attendance_GetCurrentStatus, usp_Attendance_ClockIn,
             usp_Attendance_ClockOut — never directly by an application.

  Purpose
  -------
  Read the attendance business rules from core.ApplicationSetting in one place
  so that every attendance operation sees exactly the same configuration, read
  inside the same transaction.

  THE CENTRAL RULE OF THIS PROCEDURE
  ----------------------------------
  A NULL setting means the business has not decided. It does NOT mean "off",
  "unrestricted" or any invented default. Callers must refuse the operation
  with 1053 AttendanceNotConfigured when a rule they need is missing
  (Claude.md §15 and §68 forbid inventing business rules).

  The one documented exception is Attendance.ClockInOpenTime. The stated
  requirement asks only for "when clock-in time ends" and "when clock-out
  should start", so an opening time for clock-in is genuinely optional. NULL
  there means no earliest-time restriction, and that reading is recorded here
  rather than hidden in code.

  Result codes
      0 Success (values returned; some may be NULL — that is the caller's
        decision to act on)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE core.usp_ApplicationSetting_GetAttendanceRules
    @BusinessTimeZoneId        SYSNAME      OUTPUT,
    @ClockInOpenTime           TIME(0)      OUTPUT,
    @ClockInCloseTime          TIME(0)      OUTPUT,
    @ClockInAfterCloseAction   VARCHAR(32)  OUTPUT,
    @ClockOutOpenTime          TIME(0)      OUTPUT,
    @ClockOutBeforeOpenAction  VARCHAR(32)  OUTPUT,
    @GracePeriodMinutes        INT          OUTPUT,
    @ResultCode                INT          OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    /* Initialise every OUTPUT parameter. This is a safety requirement, not
       tidiness: in T-SQL a "SELECT @var = col" that matches no rows leaves the
       variable unchanged, so a caller reusing variables could see a stale
       value where a setting is actually absent. Since NULL is precisely how
       this system represents "the business has not decided", a stale value
       would let attendance proceed under a rule nobody configured. */
    SET @BusinessTimeZoneId       = NULL;
    SET @ClockInOpenTime          = NULL;
    SET @ClockInCloseTime         = NULL;
    SET @ClockInAfterCloseAction  = NULL;
    SET @ClockOutOpenTime         = NULL;
    SET @ClockOutBeforeOpenAction = NULL;
    SET @GracePeriodMinutes       = NULL;

    DECLARE @s TABLE (SettingKey VARCHAR(100) NOT NULL PRIMARY KEY, SettingValue NVARCHAR(400) NULL);

    INSERT INTO @s (SettingKey, SettingValue)
    SELECT a.SettingKey, a.SettingValue
    FROM core.ApplicationSetting AS a
    WHERE a.SettingKey IN
    (
        'Attendance.BusinessTimeZoneId',
        'Attendance.ClockInOpenTime',
        'Attendance.ClockInCloseTime',
        'Attendance.ClockInAfterCloseAction',
        'Attendance.ClockOutOpenTime',
        'Attendance.ClockOutBeforeOpenAction',
        'Attendance.GracePeriodMinutes'
    );

    SELECT @BusinessTimeZoneId = CAST(SettingValue AS SYSNAME)
    FROM @s WHERE SettingKey = 'Attendance.BusinessTimeZoneId';

    SELECT @ClockInOpenTime = TRY_CAST(SettingValue AS TIME(0))
    FROM @s WHERE SettingKey = 'Attendance.ClockInOpenTime';

    SELECT @ClockInCloseTime = TRY_CAST(SettingValue AS TIME(0))
    FROM @s WHERE SettingKey = 'Attendance.ClockInCloseTime';

    SELECT @ClockInAfterCloseAction = CAST(SettingValue AS VARCHAR(32))
    FROM @s WHERE SettingKey = 'Attendance.ClockInAfterCloseAction';

    SELECT @ClockOutOpenTime = TRY_CAST(SettingValue AS TIME(0))
    FROM @s WHERE SettingKey = 'Attendance.ClockOutOpenTime';

    SELECT @ClockOutBeforeOpenAction = CAST(SettingValue AS VARCHAR(32))
    FROM @s WHERE SettingKey = 'Attendance.ClockOutBeforeOpenAction';

    SELECT @GracePeriodMinutes = TRY_CAST(SettingValue AS INT)
    FROM @s WHERE SettingKey = 'Attendance.GracePeriodMinutes';

    SET @ResultCode = 0;
END;
GO

PRINT 'core.usp_ApplicationSetting_GetAttendanceRules deployed.';
GO
