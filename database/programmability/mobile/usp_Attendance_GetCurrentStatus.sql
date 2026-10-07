/*==============================================================================
  mobile.usp_Attendance_GetCurrentStatus
  Phase : 6
  Called by: POST /api/v1/mobile/user/status

  Purpose
  -------
  Tell the app whether to show Clock-In or Clock-Out (Claude.md §11).
  The server is the source of truth; the app never decides this from local
  state.

  How the attendance day is determined (§31, decision DB-05)
  ----------------------------------------------------------
  The current instant comes from SYSUTCDATETIME() on this server, and the
  business-local date is derived with AT TIME ZONE using the configured
  Windows time-zone identifier. The device clock is never consulted. All
  application servers therefore agree on today's date even if their own
  regional settings differ.

  Behaviour with an unfinished record from a previous day (ASM-03)
  ----------------------------------------------------------------
  Status is scoped to the CURRENT attendance day, exactly as §11 words it. An
  open record left over from an earlier day does not make the employee appear
  clocked in today, and does not block today's clock-in. It is surfaced
  instead through the Missing Clock-Outs report. Whether attendance may span
  midnight is OPEN-28; if the business says yes, this rule changes.

  Result codes
      0    Success
      1053 AttendanceNotConfigured — the business timezone is not set
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_Attendance_GetCurrentStatus
    @MobileUserId INT,
    @ResultCode   INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @BusinessTimeZoneId       SYSNAME,
            @ClockInOpenTime          TIME(0),
            @ClockInCloseTime         TIME(0),
            @ClockInAfterCloseAction  VARCHAR(32),
            @ClockOutOpenTime         TIME(0),
            @ClockOutBeforeOpenAction VARCHAR(32),
            @GracePeriodMinutes       INT,
            @SettingsResult           INT;

    EXEC core.usp_ApplicationSetting_GetAttendanceRules
        @BusinessTimeZoneId       = @BusinessTimeZoneId       OUTPUT,
        @ClockInOpenTime          = @ClockInOpenTime          OUTPUT,
        @ClockInCloseTime         = @ClockInCloseTime         OUTPUT,
        @ClockInAfterCloseAction  = @ClockInAfterCloseAction  OUTPUT,
        @ClockOutOpenTime         = @ClockOutOpenTime         OUTPUT,
        @ClockOutBeforeOpenAction = @ClockOutBeforeOpenAction OUTPUT,
        @GracePeriodMinutes       = @GracePeriodMinutes       OUTPUT,
        @ResultCode               = @SettingsResult           OUTPUT;

    IF @BusinessTimeZoneId IS NULL
    BEGIN
        /* OPEN-5 is unanswered. Refusing here is deliberate: guessing a
           timezone would silently assign attendance to the wrong day. */
        SET @ResultCode = 1053;      -- AttendanceNotConfigured
        RETURN;
    END;

    DECLARE @NowUtc         DATETIME2(3) = SYSUTCDATETIME(),
            @LocalNow       DATETIME2(3),
            @AttendanceDate DATE;

    BEGIN TRY
        SET @LocalNow = CAST(@NowUtc AT TIME ZONE 'UTC' AT TIME ZONE @BusinessTimeZoneId AS DATETIME2(3));
    END TRY
    BEGIN CATCH
        /* The configured identifier is not a valid Windows time zone on this
           instance (error 9820). Treat it as unconfigured rather than
           guessing a zone. */
        SET @ResultCode = 1053;      -- AttendanceNotConfigured
        RETURN;
    END CATCH;

    SET @AttendanceDate = CAST(@LocalNow AS DATE);

    SELECT
        @AttendanceDate                                   AS AttendanceDate,
        @NowUtc                                           AS ServerTimeUtc,
        CAST(@LocalNow AS DATETIME2(3))                   AS BusinessLocalTime,
        @BusinessTimeZoneId                               AS BusinessTimeZoneId,
        CASE WHEN a.AttendanceId IS NULL THEN 0
             WHEN a.Status = 1          THEN 1            -- ClockedIn (open record today)
             ELSE 2                                        -- Completed for today
        END                                               AS AttendanceState,
        a.AttendancePublicId,
        a.ClockInUtc,
        a.ClockOutUtc,
        a.DurationMinutes,
        @ClockInCloseTime                                 AS ClockInCloseTime,
        @ClockOutOpenTime                                 AS ClockOutOpenTime
    FROM (SELECT @AttendanceDate AS AttendanceDate) AS d
    LEFT JOIN core.Attendance AS a
           ON a.MobileUserId   = @MobileUserId
          AND a.AttendanceDate = d.AttendanceDate;

    SET @ResultCode = 0;
END;
GO

PRINT 'mobile.usp_Attendance_GetCurrentStatus deployed.';
GO
