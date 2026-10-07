/*==============================================================================
  job.usp_Maintenance_PurgeRequestNonce
  Phase : 6
  Called by: the scheduled maintenance job (account app_jobs).

  Purpose
  -------
  Remove replay nonces that are older than the window in which a replay is
  still possible.

  Why the retention floor exists
  ------------------------------
  A nonce only needs to be remembered for as long as a captured request could
  still be accepted, which is bounded by Security.SignatureSkewSeconds. If the
  purge ran more aggressively than that, a captured request could be replayed
  after its nonce had been forgotten — the purge itself would open the hole it
  is meant to keep closed. The procedure therefore refuses to delete anything
  newer than twice the skew window, no matter what the retention setting says.

  Batched deletes keep locks short so that authentication traffic is not
  blocked while housekeeping runs.

  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE job.usp_Maintenance_PurgeRequestNonce
    @BatchSize   INT = 5000,
    @MaxBatches  INT = 200,
    @RowsDeleted INT OUTPUT,
    @ResultCode  INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @RetentionHours  INT,
            @SkewSeconds     INT,
            @CutoffUtc       DATETIME2(3),
            @SafetyCutoffUtc DATETIME2(3),
            @NowUtc          DATETIME2(3) = SYSUTCDATETIME();

    SELECT @RetentionHours = TRY_CAST(SettingValue AS INT)
    FROM core.ApplicationSetting WHERE SettingKey = 'Retention.RequestNonceHours';

    SELECT @SkewSeconds = TRY_CAST(SettingValue AS INT)
    FROM core.ApplicationSetting WHERE SettingKey = 'Security.SignatureSkewSeconds';

    SET @RetentionHours = COALESCE(@RetentionHours, 2);
    SET @SkewSeconds    = COALESCE(@SkewSeconds, 120);

    SET @CutoffUtc       = DATEADD(HOUR, -@RetentionHours, @NowUtc);
    SET @SafetyCutoffUtc = DATEADD(SECOND, -(2 * @SkewSeconds), @NowUtc);

    /* Never delete inside the replay window, whatever the setting says. */
    IF @CutoffUtc > @SafetyCutoffUtc
        SET @CutoffUtc = @SafetyCutoffUtc;

    SET @RowsDeleted = 0;

    DECLARE @Batch INT = 0, @Affected INT = 1;

    WHILE @Affected > 0 AND @Batch < @MaxBatches
    BEGIN
        DELETE TOP (@BatchSize)
        FROM core.RequestNonce
        WHERE CreatedUtc < @CutoffUtc;

        SET @Affected = @@ROWCOUNT;
        SET @RowsDeleted = @RowsDeleted + @Affected;
        SET @Batch = @Batch + 1;
    END;

    SET @ResultCode = 0;
END;
GO

PRINT 'job.usp_Maintenance_PurgeRequestNonce deployed.';
GO
