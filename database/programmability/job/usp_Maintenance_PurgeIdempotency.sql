/*==============================================================================
  job.usp_Maintenance_PurgeIdempotency
  Phase : 6
  Called by: the scheduled maintenance job (account app_jobs).

  Purpose
  -------
  Remove idempotency records once a retry carrying the same key is no longer
  plausible.

  The trade-off being made
  ------------------------
  These rows are what make a retry safe: while the row exists, a repeated
  clock-in returns the original answer instead of attempting the operation
  again. Deleting one early does not corrupt anything, because the unique
  index on (employee, attendance date) still prevents a duplicate record — the
  client would simply receive ALREADY_CLOCKED_IN rather than the original
  success. Keeping them for the configured window (default 48 hours) means
  ordinary retries get the cleaner answer.

  Rows still marked InProgress are treated the same way once they are old:
  an operation that never completed after the retention window is not going to
  complete now, and leaving the row would block the key forever.

  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE job.usp_Maintenance_PurgeIdempotency
    @BatchSize   INT = 5000,
    @MaxBatches  INT = 200,
    @RowsDeleted INT OUTPUT,
    @ResultCode  INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @RetentionHours INT,
            @CutoffUtc      DATETIME2(3),
            @NowUtc         DATETIME2(3) = SYSUTCDATETIME();

    SELECT @RetentionHours = TRY_CAST(SettingValue AS INT)
    FROM core.ApplicationSetting WHERE SettingKey = 'Retention.IdempotencyHours';

    SET @RetentionHours = COALESCE(@RetentionHours, 48);
    SET @CutoffUtc = DATEADD(HOUR, -@RetentionHours, @NowUtc);

    SET @RowsDeleted = 0;

    DECLARE @Batch INT = 0, @Affected INT = 1;

    WHILE @Affected > 0 AND @Batch < @MaxBatches
    BEGIN
        DELETE TOP (@BatchSize)
        FROM core.RequestIdempotency
        WHERE CreatedUtc < @CutoffUtc;

        SET @Affected = @@ROWCOUNT;
        SET @RowsDeleted = @RowsDeleted + @Affected;
        SET @Batch = @Batch + 1;
    END;

    SET @ResultCode = 0;
END;
GO

PRINT 'job.usp_Maintenance_PurgeIdempotency deployed.';
GO
