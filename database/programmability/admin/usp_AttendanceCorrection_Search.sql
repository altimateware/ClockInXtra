/*==============================================================================
  admin.usp_AttendanceCorrection_Search
  Phase : 19 (corrections enabled 2026-09-19, DEC-08)
  Called by: the administration portal — the corrections list, for requesters
             (Attendance.Correct) and approvers (Attendance.ApproveCorrection).

  Purpose
  -------
  Lists attendance corrections, awaiting-approval first, with the original
  and corrected times side by side — the "attendance corrections" report of
  §20 as well as the approver's work queue.

  Times are returned in UTC and in the business time zone, so the portal shows
  administrators the times they actually reason in without converting them in
  a second place.

  Returns
      Up to @PageSize corrections: awaiting approval first, then newest first.
      RequestedByAdministratorId is included so the portal can withhold the
      approve button from the requester; the database refuses such an approval
      regardless (1074, CK_AttendanceCorrection_SeparationOfDuties).

  Result codes
      0    Success
      1001 InvalidRequest
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_AttendanceCorrection_Search
    @Status     TINYINT = NULL,   -- 1 Requested, 2 Approved, 3 Rejected, 4 Applied
    @PageSize   INT     = 200,
    @ResultCode INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF (@Status IS NOT NULL AND @Status NOT IN (1, 2, 3, 4))
       OR @PageSize IS NULL OR @PageSize < 1 OR @PageSize > 1000
    BEGIN
        SET @ResultCode = 1001;
        RETURN;
    END;

    DECLARE @Zone SYSNAME =
        COALESCE((SELECT CAST(SettingValue AS SYSNAME) FROM core.ApplicationSetting
                  WHERE SettingKey = 'Attendance.BusinessTimeZoneId'), 'UTC');

    SELECT TOP (@PageSize)
        c.AttendanceCorrectionId,
        a.AttendancePublicId,
        a.AttendanceDate,
        u.UserId,
        u.FirstName,
        u.LastName,
        c.FieldChanged,
        c.OriginalClockInUtc,
        c.OriginalClockOutUtc,
        c.CorrectedClockInUtc,
        c.CorrectedClockOutUtc,
        CAST(c.OriginalClockInUtc   AT TIME ZONE 'UTC' AT TIME ZONE @Zone AS DATETIME2(0)) AS OriginalClockInLocal,
        CAST(c.OriginalClockOutUtc  AT TIME ZONE 'UTC' AT TIME ZONE @Zone AS DATETIME2(0)) AS OriginalClockOutLocal,
        CAST(c.CorrectedClockInUtc  AT TIME ZONE 'UTC' AT TIME ZONE @Zone AS DATETIME2(0)) AS CorrectedClockInLocal,
        CAST(c.CorrectedClockOutUtc AT TIME ZONE 'UTC' AT TIME ZONE @Zone AS DATETIME2(0)) AS CorrectedClockOutLocal,
        c.Reason,
        c.Status,
        c.RequestedByAdministratorId,
        rq.DisplayName AS RequestedByName,
        c.RequestedUtc,
        ap.DisplayName AS DecidedByName,
        c.DecidedUtc,
        c.DecisionNote,
        c.AppliedUtc,
        c.[RowVersion]
    FROM core.AttendanceCorrection AS c
    INNER JOIN core.Attendance AS a ON a.AttendanceId = c.AttendanceId
    INNER JOIN core.MobileUser AS u ON u.MobileUserId = a.MobileUserId
    INNER JOIN core.Administrator AS rq ON rq.AdministratorId = c.RequestedByAdministratorId
    LEFT JOIN core.Administrator AS ap ON ap.AdministratorId = c.ApprovedByAdministratorId
    WHERE (@Status IS NULL OR c.Status = @Status)
    ORDER BY CASE WHEN c.Status = 1 THEN 0 ELSE 1 END, c.AttendanceCorrectionId DESC
    OPTION (RECOMPILE);

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_AttendanceCorrection_Search deployed.';
GO
