/*==============================================================================
  mobile.usp_ApplicationSetting_GetMobileRuntime
  Phase : 6
  Called by: GET /api/v1/mobile/app/config

  Purpose
  -------
  Return the handful of settings the mobile application is allowed to know.

  THE ALLOW-LIST IS THE POINT
  ---------------------------
  This procedure returns only rows whose IsMobileVisible flag is set. It is an
  allow-list, not a filter of known-bad keys, so a setting added in future is
  invisible to the app until somebody deliberately marks it visible.

  That matters because core.ApplicationSetting also holds security parameters —
  lockout thresholds, the signature skew window, attestation requirements. An
  attacker who learns the lockout threshold knows exactly how many guesses are
  free; one who learns the skew window knows how long a captured request stays
  replayable. None of it is secret in a cryptographic sense, and none of it
  needs to be on an untrusted device either.

  The configured status flag
  --------------------------
  AttendanceConfigured tells the app whether the business settings required for
  clock-in and clock-out exist. Without it the app could only discover the
  system is unconfigured by attempting attendance and receiving
  ATTENDANCE_NOT_CONFIGURED, which is a poor thing to learn at 08:00 with a
  queue behind you. The app shows an explanatory message instead of a clock-in
  button that cannot work.

  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_ApplicationSetting_GetMobileRuntime
    @ResultCode INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    ------------------------------------------------------------------
    -- 1. Settings explicitly marked as safe for the mobile client.
    ------------------------------------------------------------------
    SELECT
        s.SettingKey,
        s.SettingValue,
        s.DataType
    FROM core.ApplicationSetting AS s
    WHERE s.IsMobileVisible = 1
    ORDER BY s.SettingKey;

    ------------------------------------------------------------------
    -- 2. Whether attendance can operate at all.
    --    Mirrors exactly the conditions the attendance procedures check, so
    --    the app and the server cannot disagree about it.
    ------------------------------------------------------------------
    DECLARE @MissingForClockIn  INT,
            @MissingForClockOut INT;

    SELECT @MissingForClockIn = COUNT(*)
    FROM core.ApplicationSetting
    WHERE SettingKey IN ('Attendance.BusinessTimeZoneId',
                         'Attendance.ClockInCloseTime',
                         'Attendance.ClockInAfterCloseAction')
      AND SettingValue IS NULL;

    SELECT @MissingForClockOut = COUNT(*)
    FROM core.ApplicationSetting
    WHERE SettingKey IN ('Attendance.BusinessTimeZoneId',
                         'Attendance.ClockOutOpenTime',
                         'Attendance.ClockOutBeforeOpenAction')
      AND SettingValue IS NULL;

    SELECT
        CAST(CASE WHEN @MissingForClockIn  = 0 THEN 1 ELSE 0 END AS BIT) AS ClockInConfigured,
        CAST(CASE WHEN @MissingForClockOut = 0 THEN 1 ELSE 0 END AS BIT) AS ClockOutConfigured,
        SYSUTCDATETIME()                                                 AS ServerTimeUtc;

    SET @ResultCode = 0;
END;
GO

PRINT 'mobile.usp_ApplicationSetting_GetMobileRuntime deployed.';
GO
