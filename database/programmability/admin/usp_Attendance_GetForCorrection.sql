/*==============================================================================
  admin.usp_Attendance_GetForCorrection
  Phase : 19 (corrections enabled 2026-09-19, DEC-08)
  Called by: the administration portal — the "request a correction" form.

  Purpose
  -------
  One attendance record, by its public id, with its times in the business
  time zone and whether a correction to it is already awaiting approval (the
  request procedure allows one at a time).

  Result codes
      0    Success
      1070 NotFound
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Attendance_GetForCorrection
    @AttendancePublicId UNIQUEIDENTIFIER,
    @ResultCode         INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Zone SYSNAME =
        COALESCE((SELECT CAST(SettingValue AS SYSNAME) FROM core.ApplicationSetting
                  WHERE SettingKey = 'Attendance.BusinessTimeZoneId'), 'UTC');

    SELECT
        a.AttendancePublicId,
        a.AttendanceDate,
        u.UserId,
        u.FirstName,
        u.LastName,
        a.Status,
        a.ClockInUtc,
        a.ClockOutUtc,
        CAST(a.ClockInUtc  AT TIME ZONE 'UTC' AT TIME ZONE @Zone AS DATETIME2(0)) AS ClockInLocal,
        CAST(a.ClockOutUtc AT TIME ZONE 'UTC' AT TIME ZONE @Zone AS DATETIME2(0)) AS ClockOutLocal,
        CAST(CASE WHEN EXISTS (SELECT 1 FROM core.AttendanceCorrection AS c
                               WHERE c.AttendanceId = a.AttendanceId AND c.Status = 1)
                  THEN 1 ELSE 0 END AS BIT) AS HasPendingCorrection
    FROM core.Attendance AS a
    INNER JOIN core.MobileUser AS u ON u.MobileUserId = a.MobileUserId
    WHERE a.AttendancePublicId = @AttendancePublicId;

    SET @ResultCode = CASE WHEN @@ROWCOUNT = 0 THEN 1070 ELSE 0 END;
END;
GO

PRINT 'admin.usp_Attendance_GetForCorrection deployed.';
GO
