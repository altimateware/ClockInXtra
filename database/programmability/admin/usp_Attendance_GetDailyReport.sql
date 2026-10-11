/*==============================================================================
  admin.usp_Attendance_GetDailyReport
  Phase : 6 (filters and business-date correction in Phase 19)
  Called by: the administration portal (permission Report.View or
             Attendance.View).

  Purpose
  -------
  The daily attendance report required by §20, filterable by date range,
  employee, department, office and status.

  Filtering without dynamic SQL (decision DB-11)
  ----------------------------------------------
  Optional filters use the standard nullable-parameter pattern together with
  OPTION (RECOMPILE). Recompiling per execution gives each filter combination
  a plan suited to it, which is the usual reason people reach for dynamic SQL.
  Dynamic SQL is prohibited here (§4, §49), and it is not needed.

  What this report deliberately does NOT expose (§26, §63)
  --------------------------------------------------------
  No raw coordinates. Administrators see the matched office, the distance in
  metres and the accuracy the device reported — enough to judge whether an
  attendance record is sound, without turning the report into a location
  history of an employee's movements.

  Late and early flags are NULL where the corresponding business rule has not
  been configured. NULL means "not evaluated", not "no". The portal renders it
  as such rather than showing a reassuring blank.

  Exceptions (§20: clock-in exceptions, missing clock-outs, late arrivals,
  early clock-outs)
  --------------------------------------------------------------------------
  @Exception narrows the report to one kind of exception row:
      1 Missing clock-out   an open record from a business day already past
      2 Late clock-in       IsLateClockIn = 1
      3 Early clock-out     IsEarlyClockOut = 1
      4 Mocked location     the platform flagged the clock-in position
  Late and early rows appear only where those rules are configured; with the
  rule unconfigured the flag is NULL and the row is not an exception.

  "A day already past" is the BUSINESS day (Phase 19 correction)
  --------------------------------------------------------------
  The first version compared the attendance date with the UTC date. For a
  business east of UTC that flags nothing until UTC midnight — 01:00 in Lagos —
  and for one west of UTC it flags today's open records as missing while the
  employees are still at work. The comparison now uses the configured
  Attendance.BusinessTimeZoneId, the same zone that assigned the attendance
  date in the first place.

  If that setting is absent or invalid (OPEN-5), no zone is guessed. A record
  is then flagged only when its date is before the UTC date minus one day —
  which is past in every time zone on Earth (offsets run from -12 to +14
  hours), so the flag is never wrong, merely a day late.

  @UserId is the employee's sign-in identifier, which is what an administrator
  knows; the internal key is never asked of a person.

  Result codes
      0    Success
      1001 InvalidRequest
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Attendance_GetDailyReport
    @FromDate         DATE,
    @ToDate           DATE,
    @UserId           NVARCHAR(64)  = NULL,
    @Department       NVARCHAR(120) = NULL,
    @OfficeLocationId INT           = NULL,
    @Status           TINYINT       = NULL,   -- 1 Open, 2 Closed, 3 Corrected
    @Exception        TINYINT       = NULL,   -- 1 Missing clock-out, 2 Late, 3 Early out, 4 Mocked
    @Page             INT           = 1,
    @PageSize         INT           = 1000,
    @ResultCode       INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @FromDate IS NULL OR @ToDate IS NULL OR @ToDate < @FromDate
       OR (@Status IS NOT NULL AND @Status NOT IN (1, 2, 3))
       OR (@Exception IS NOT NULL AND @Exception NOT IN (1, 2, 3, 4))
       OR @PageSize IS NULL OR @PageSize < 1 OR @PageSize > 5000
       OR @Page IS NULL OR @Page < 1
    BEGIN
        SET @ResultCode = 1001;          -- InvalidRequest
        RETURN;
    END;

    /* The business-local date today, or the conservative stand-in when the
       zone is unknown (see the header). */
    DECLARE @BusinessTimeZoneId SYSNAME,
            @Today              DATE,
            @NowUtc             DATETIME2(3) = SYSUTCDATETIME();

    SELECT @BusinessTimeZoneId = CAST(SettingValue AS SYSNAME)
    FROM core.ApplicationSetting
    WHERE SettingKey = 'Attendance.BusinessTimeZoneId';

    IF @BusinessTimeZoneId IS NOT NULL
    BEGIN
        BEGIN TRY
            SET @Today = CAST(@NowUtc AT TIME ZONE 'UTC' AT TIME ZONE @BusinessTimeZoneId AS DATE);
        END TRY
        BEGIN CATCH
            SET @Today = NULL;       -- not a valid zone on this instance (9820)
        END CATCH;
    END;

    IF @Today IS NULL
        SET @Today = DATEADD(DAY, -1, CAST(@NowUtc AS DATE));

    /* The total rides on every row rather than returning through an OUTPUT
       parameter. An OUTPUT is only populated once the result set has been
       consumed, which is a trap worth avoiding: read it too early and Dapper
       hands back a DBNull rather than a number. It also keeps this procedure's
       shape the same as admin.usp_Device_GetRegistered. */
    SELECT
        COUNT(*) OVER () AS TotalCount,
        a.AttendancePublicId,
        a.AttendanceDate,
        u.MobileUserPublicId,
        u.UserId,
        u.EmployeeNumber,
        u.FirstName,
        u.LastName,
        u.Department,
        u.JobTitle,
        a.ClockInUtc,
        a.ClockOutUtc,
        a.DurationMinutes,
        a.Status,
        a.IsLateClockIn,
        a.IsEarlyClockOut,
        oi.Name AS ClockInOfficeName,
        oo.Name AS ClockOutOfficeName,
        ei.DistanceMeters         AS ClockInDistanceMeters,
        ei.ReportedAccuracyMeters AS ClockInAccuracyMeters,
        ei.WasMockedLocation      AS ClockInWasMockedLocation,
        eo.DistanceMeters         AS ClockOutDistanceMeters,
        eo.ReportedAccuracyMeters AS ClockOutAccuracyMeters,
        CAST(CASE WHEN a.Status = 1 AND a.AttendanceDate < @Today
                  THEN 1 ELSE 0 END AS BIT) AS IsMissingClockOut
    FROM core.Attendance AS a
    INNER JOIN core.MobileUser AS u
            ON u.MobileUserId = a.MobileUserId
    LEFT JOIN core.OfficeLocation AS oi
           ON oi.OfficeLocationId = a.ClockInOfficeLocationId
    LEFT JOIN core.OfficeLocation AS oo
           ON oo.OfficeLocationId = a.ClockOutOfficeLocationId
    /* One evidence row per attendance and event type, so these joins cannot
       multiply rows. */
    LEFT JOIN core.AttendanceEvent AS ei
           ON ei.AttendanceId = a.AttendanceId AND ei.EventType = 1
    LEFT JOIN core.AttendanceEvent AS eo
           ON eo.AttendanceId = a.AttendanceId AND eo.EventType = 2
    WHERE a.AttendanceDate >= @FromDate
      AND a.AttendanceDate <= @ToDate
      AND (@UserId           IS NULL OR u.UserId = @UserId)
      AND (@Department       IS NULL OR u.Department = @Department)
      AND (@OfficeLocationId IS NULL OR a.ClockInOfficeLocationId = @OfficeLocationId
                                     OR a.ClockOutOfficeLocationId = @OfficeLocationId)
      AND (@Status           IS NULL OR a.Status = @Status)
      AND (@Exception IS NULL
           OR (@Exception = 1 AND a.Status = 1 AND a.AttendanceDate < @Today)
           OR (@Exception = 2 AND a.IsLateClockIn = 1)
           OR (@Exception = 3 AND a.IsEarlyClockOut = 1)
           OR (@Exception = 4 AND ei.WasMockedLocation = 1))
    ORDER BY a.AttendanceDate DESC, u.LastName, u.FirstName,
             /* A unique tie-break. Without one the order of equal rows is
                undefined, and OFFSET then slices an order that may differ
                between two queries: a record can appear on two pages, or on
                none. On an attendance report that is a record that looks
                missing. */
             a.AttendanceId
    OFFSET (@Page - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY
    OPTION (RECOMPILE);

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_Attendance_GetDailyReport deployed.';
GO
