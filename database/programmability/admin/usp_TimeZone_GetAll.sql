/*==============================================================================
  admin.usp_TimeZone_GetAll
  Phase : 24
  Called by: the administration portal (permission Setting.View), to offer the
             business time zone as a list rather than a text box.

  Purpose
  -------
  Return every time zone this SQL Server instance recognises, with its current
  offset from UTC.

  Why from SQL Server and not from the web server
  -----------------------------------------------
  admin.usp_ApplicationSetting_Set accepts a time zone only if it exists in
  sys.time_zone_info, and the attendance procedures convert with AT TIME ZONE
  against the same list. Offering the web server's list instead
  (TimeZoneInfo.GetSystemTimeZones) could show a zone the database refuses, or
  omit one it accepts, whenever the two machines are patched differently. The
  list comes from the one place that decides.

  The offset is today's. For zones that observe daylight saving it changes
  during the year; is_currently_dst says whether it is in effect now. Nigeria
  (W. Central Africa Standard Time) does not observe it, so its UTC+01:00 is
  fixed.

  Result codes
      0    Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_TimeZone_GetAll
    @ResultCode INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT tz.name                       AS TimeZoneId,
           tz.current_utc_offset         AS CurrentUtcOffset,     -- e.g. '+01:00'
           CAST(tz.is_currently_dst AS BIT) AS IsCurrentlyDaylightSaving
    FROM sys.time_zone_info AS tz
    ORDER BY
        /* West to east, as people scan a list of offsets; then by name. */
        CAST(LEFT(tz.current_utc_offset, 1) + '1' AS INT)
            * (CAST(SUBSTRING(tz.current_utc_offset, 2, 2) AS INT) * 60
               + CAST(SUBSTRING(tz.current_utc_offset, 5, 2) AS INT)),
        tz.name;

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_TimeZone_GetAll deployed.';
GO
