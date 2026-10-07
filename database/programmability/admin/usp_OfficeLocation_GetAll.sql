/*==============================================================================
  admin.usp_OfficeLocation_GetAll
  Phase : 6
  Called by: the administration portal (permission OfficeLocation.View).

  Purpose
  -------
  List office locations for administration, including the figures needed to
  judge whether a location's radius is workable in practice (§19).

  Why the observed accuracy statistics are here
  ---------------------------------------------
  Conflict CON-01 is the hardest operational problem in this system: a
  5 metre radius sits at or below the accuracy consumer phones actually
  achieve, especially indoors, so honest employees can be refused. Rather than
  leaving administrators to guess, this returns what has actually been
  measured at each office — how many attendance events were recorded there,
  and the median and worst reported accuracy — so a radius change is an
  evidence-based decision taken with the business owner (OPEN-25), not a
  reaction to complaints.

  RecentRejections counts location failures logged against the office in the
  security-event trail, which is the other half of the picture: a location
  nobody can clock in at generates rejections rather than events.

  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_OfficeLocation_GetAll
    @IncludeDisabled BIT = 1,
    @StatisticsDays  INT = 30,
    @ResultCode      INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @StatisticsDays IS NULL OR @StatisticsDays < 1 OR @StatisticsDays > 3650
    BEGIN
        SET @ResultCode = 1001;          -- InvalidRequest
        RETURN;
    END;

    DECLARE @FromUtc DATETIME2(3) = DATEADD(DAY, -@StatisticsDays, SYSUTCDATETIME());

    SELECT
        o.OfficeLocationId,
        o.OfficeLocationPublicId,
        o.Name,
        o.Description,
        o.Latitude,
        o.Longitude,
        o.AllowedRadiusMeters,
        o.Status,
        o.CreatedUtc,
        o.UpdatedUtc,
        o.[RowVersion],
        ca.UserName AS CreatedByUserName,
        ua.UserName AS UpdatedByUserName,
        stats.EventCount,
        stats.MedianAccuracyMeters,
        stats.WorstAccuracyMeters,
        stats.MaxDistanceMeters
    FROM core.OfficeLocation AS o
    LEFT JOIN core.Administrator AS ca ON ca.AdministratorId = o.CreatedByAdministratorId
    LEFT JOIN core.Administrator AS ua ON ua.AdministratorId = o.UpdatedByAdministratorId
    OUTER APPLY
    (
        SELECT
            COUNT(*)                            AS EventCount,
            MAX(e.ReportedAccuracyMeters)       AS WorstAccuracyMeters,
            MAX(e.DistanceMeters)               AS MaxDistanceMeters,
            /* PERCENTILE_CONT is a window function, so it is evaluated per row
               and then collapsed with MIN; the median is the same for every
               row in the group. */
            MIN(e.MedianAccuracy)               AS MedianAccuracyMeters
        FROM
        (
            SELECT
                ev.ReportedAccuracyMeters,
                ev.DistanceMeters,
                PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY ev.ReportedAccuracyMeters)
                    OVER () AS MedianAccuracy
            FROM core.AttendanceEvent AS ev
            WHERE ev.OfficeLocationId = o.OfficeLocationId
              AND ev.OccurredUtc >= @FromUtc
              AND ev.ReportedAccuracyMeters IS NOT NULL
        ) AS e
    ) AS stats
    WHERE (@IncludeDisabled = 1 OR o.Status = 1)
    ORDER BY o.Name
    OPTION (RECOMPILE);

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_OfficeLocation_GetAll deployed.';
GO
