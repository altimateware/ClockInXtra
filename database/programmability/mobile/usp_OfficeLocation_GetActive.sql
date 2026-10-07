/*==============================================================================
  mobile.usp_OfficeLocation_GetActive
  Phase : 6
  Called by: the location validation service, for every location check.

  Purpose
  -------
  Return the active office locations against which a reported position is
  measured.

  Why the distance is NOT computed here
  -------------------------------------
  The geodesic calculation lives in Attendance.Domain (GeoDistance, Haversine
  on mean Earth radius 6,371,008.8 m) so that it is unit-testable against the
  boundary cases required by Claude.md §51 (0, 1, 3, 4.9, 5.0, 5.1, 10 m) and
  has exactly one implementation. The database is the source of the office
  configuration, not a second implementation of the geometry.

  The result set is small — one row per office — and is cached briefly by the
  application, with the cache invalidated when an administrator changes a
  location.

  What the mobile client receives is NOT this data: the API returns only a
  valid/invalid decision plus, on success, the matched office identifier
  (§9: do not expose unnecessary internal location data).

  Result codes
      0 Success (an empty result set is a valid answer: no offices configured)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_OfficeLocation_GetActive
    @ResultCode INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        o.OfficeLocationId,
        o.OfficeLocationPublicId,
        o.Name,
        o.Latitude,
        o.Longitude,
        o.AllowedRadiusMeters
    FROM core.OfficeLocation AS o
    WHERE o.Status = 1              -- Active
    ORDER BY o.OfficeLocationId;

    SET @ResultCode = 0;
END;
GO

PRINT 'mobile.usp_OfficeLocation_GetActive deployed.';
GO
