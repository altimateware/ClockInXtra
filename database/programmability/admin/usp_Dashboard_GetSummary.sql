/*==============================================================================
  admin.usp_Dashboard_GetSummary
  Phase : 6
  Called by: the administration portal's landing page (any signed-in
             administrator; the page itself hides the sections whose pages the
             viewer may not open).

  Purpose
  -------
  One row of counts for the dashboard, so the landing page can show what is
  outstanding without the portal issuing a dozen queries to draw one screen.

  What it counts, and why each earns its place
  --------------------------------------------
  Two things stop this system working, and both are administrative: business
  settings nobody has decided yet, and device registrations nobody has
  approved. An employee standing at a door can resolve neither, so both belong
  on the first page an administrator sees. The rest answers the question an
  administrator actually arrives with — "is anything wrong this morning".

  * Settings awaiting confirmation and devices awaiting approval: work queues.
  * Employees who cannot clock in: the same queue seen from the other end. An
    employee missing a password, an authenticator or a device will be refused
    at a door, and nobody finds out until they are standing at one.
  * Open attendance from an earlier day: a clock-in with no clock-out. It is
    the shape of a day somebody forgot to close, and it will not resolve itself.
  * Refusals in the last day: location and device checks that said no. A
    handful is ordinary; a spike is a locked door, a moved office or an
    attacker, and all three want looking at.

  Which day "today" is
  --------------------
  The attendance day is defined by the organisation's configured business
  timezone (section 31), never by the server's own clock. The zone is read
  from Attendance.BusinessTimeZoneId and applied with AT TIME ZONE, which is
  the idiom admin.usp_AttendanceCorrection_Search already uses; falling back to
  UTC matches it too. Counting "today" against the server's local date would
  quietly disagree with every attendance record for part of each day.

  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Dashboard_GetSummary
    @ResultCode INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Zone SYSNAME =
        COALESCE((SELECT CAST(SettingValue AS SYSNAME) FROM core.ApplicationSetting
                  WHERE SettingKey = 'Attendance.BusinessTimeZoneId'), 'UTC');

    DECLARE @Today DATE =
        CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE @Zone AS DATE);

    DECLARE @Since DATETIME2(3) = DATEADD(HOUR, -24, SYSUTCDATETIME());

    SELECT
        /*---------------------------------------------------- work queues ----*/
        (SELECT COUNT(*) FROM core.Device WHERE Status = 0)          AS DevicesAwaitingApproval,
        (SELECT COUNT(*) FROM core.Device WHERE Status = 1)          AS ActiveDevices,

        (SELECT COUNT(*) FROM core.ApplicationSetting
          WHERE RequiresBusinessConfirmation = 1 AND ConfirmedUtc IS NULL)
                                                                     AS SettingsAwaitingConfirmation,

        (SELECT COUNT(*) FROM core.AttendanceCorrection WHERE Status = 1)
                                                                     AS CorrectionsAwaitingApproval,

        /*------------------------------------------------------- people ----*/
        (SELECT COUNT(*) FROM core.MobileUser WHERE Status = 1)      AS ActiveEmployees,

        /* Active employees who would be refused at a door right now. The three
           conditions are the same ones usp_MobileUser_Search calls readiness. */
        (SELECT COUNT(*)
           FROM core.MobileUser AS u
          WHERE u.Status = 1
            AND (NOT EXISTS (SELECT 1 FROM core.EmployeeCredential AS c
                              WHERE c.MobileUserId = u.MobileUserId)
                 OR NOT EXISTS (SELECT 1 FROM core.MfaCredential AS m
                                 WHERE m.MobileUserId = u.MobileUserId AND m.Status = 1)
                 OR NOT EXISTS (SELECT 1 FROM core.Device AS d
                                 WHERE d.MobileUserId = u.MobileUserId AND d.Status = 1)))
                                                                     AS EmployeesNotReady,

        /*-------------------------------------------------------- places ----*/
        (SELECT COUNT(*) FROM core.OfficeLocation WHERE Status = 1)  AS ActiveOfficeLocations,

        /*---------------------------------------------------- today's day ----*/
        (SELECT COUNT(*) FROM core.Attendance
          WHERE AttendanceDate = @Today AND Status = 1)              AS ClockedInNow,

        (SELECT COUNT(*) FROM core.Attendance
          WHERE AttendanceDate = @Today AND Status IN (2, 3))        AS CompletedToday,

        (SELECT COUNT(*) FROM core.Attendance
          WHERE AttendanceDate = @Today AND IsLateClockIn = 1)       AS LateToday,

        /* Still open from an earlier day: somebody never clocked out. */
        (SELECT COUNT(*) FROM core.Attendance
          WHERE AttendanceDate < @Today AND Status = 1)              AS MissingClockOuts,

        /*------------------------------------------- refusals, last 24h ----*/
        (SELECT COUNT(*) FROM audit.SecurityEvent
          WHERE OccurredUtc >= @Since AND ReasonCode LIKE 'LOCATION[_]%')
                                                                     AS LocationRefusals,

        (SELECT COUNT(*) FROM audit.SecurityEvent
          WHERE OccurredUtc >= @Since
            AND ReasonCode IN ('DEVICE_NOT_REGISTERED', 'DEVICE_REVOKED',
                               'DEVICE_NOT_APPROVED', 'SIGNATURE_INVALID',
                               'SIGNATURE_NONCE_REPLAYED'))          AS DeviceRefusals,

        (SELECT COUNT(*) FROM audit.SecurityEvent
          WHERE OccurredUtc >= @Since AND Severity = 3)              AS CriticalEvents;

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_Dashboard_GetSummary deployed.';
GO
