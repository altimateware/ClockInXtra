/*==============================================================================
  admin.usp_ApplicationSetting_GetAll
  Phase : 6
  Called by: the administration portal (permission Setting.View), and by the
             readiness health check.

  Purpose
  -------
  List the configurable business and security rules, showing which ones are
  still undecided.

  Why the portal needs IsUnset and RequiresBusinessConfirmation
  -------------------------------------------------------------
  A NULL value is not a blank field to be tidied away: it means the business
  has not made that decision, and the attendance procedures refuse to operate
  while a mandatory one is missing (1053 AttendanceNotConfigured). The portal
  surfaces these as outstanding decisions, and the readiness endpoint reports
  Degraded, so an unconfigured system announces itself rather than quietly
  inventing behaviour (Claude.md §15, §68).

  IsMandatoryForAttendance marks the settings that actually block clock-in or
  clock-out, as opposed to those that merely remain optional.

  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_ApplicationSetting_GetAll
    @Category   NVARCHAR(64) = NULL,   -- NULL returns every category
    @ResultCode INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        s.ApplicationSettingId,
        s.SettingKey,
        s.SettingValue,
        s.DataType,
        s.Category,
        s.Description,
        s.AllowedValues,
        s.MinValue,
        s.MaxValue,
        s.Unit,
        s.BlankMeaning,
        s.RequiresBusinessConfirmation,
        s.IsMobileVisible,
        s.ConfirmedUtc,
        s.ConfirmedByAdministratorId,
        ca.UserName AS ConfirmedByUserName,
        s.UpdatedUtc,
        ua.UserName AS UpdatedByUserName,
        s.[RowVersion],
        /* Undecided = blank and never confirmed. A blank confirmed as the
           decision (e.g. retention: keep indefinitely) is decided. */
        CAST(CASE WHEN s.SettingValue IS NULL AND s.ConfirmedUtc IS NULL THEN 1 ELSE 0 END AS BIT) AS IsUnset,
        CAST(CASE WHEN s.SettingKey IN ('Attendance.BusinessTimeZoneId',
                                        'Attendance.ClockInCloseTime',
                                        'Attendance.ClockInAfterCloseAction',
                                        'Attendance.ClockOutOpenTime',
                                        'Attendance.ClockOutBeforeOpenAction')
                  THEN 1 ELSE 0 END AS BIT) AS IsMandatoryForAttendance
    FROM core.ApplicationSetting AS s
    LEFT JOIN core.Administrator AS ca ON ca.AdministratorId = s.ConfirmedByAdministratorId
    LEFT JOIN core.Administrator AS ua ON ua.AdministratorId = s.UpdatedByAdministratorId
    WHERE (@Category IS NULL OR s.Category = @Category)
    ORDER BY s.Category, s.SettingKey
    OPTION (RECOMPILE);   -- optional filter; recompile instead of dynamic SQL (DB-11)

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_ApplicationSetting_GetAll deployed.';
GO
