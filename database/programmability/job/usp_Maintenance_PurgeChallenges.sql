/*==============================================================================
  job.usp_Maintenance_PurgeChallenges
  Phase : 6
  Called by: the scheduled maintenance job (account app_jobs).

  Purpose
  -------
  Remove device registration challenges that are spent or expired.

  Why deleting these is safe, unlike nonces
  -----------------------------------------
  A challenge carries its own expiry, and usp_Device_Register refuses any
  challenge whose ExpiresUtc has passed. Deleting an expired row therefore
  cannot widen what is accepted: an attacker replaying a deleted challenge is
  refused as unknown (1060) instead of expired (1061). Contrast that with
  replay nonces, where forgetting a nonce too early genuinely would reopen the
  replay window — which is why that purge has a hard safety floor and this one
  does not need one.

  A grace period is still applied so that recently consumed challenges remain
  visible for a while, which makes a failed registration easier to investigate
  while the employee is still on the phone to support.

  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE job.usp_Maintenance_PurgeChallenges
    @GraceHours  INT = 24,
    @BatchSize   INT = 5000,
    @MaxBatches  INT = 100,
    @RowsDeleted INT OUTPUT,
    @ResultCode  INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NowUtc    DATETIME2(3) = SYSUTCDATETIME(),
            @CutoffUtc DATETIME2(3);

    SET @CutoffUtc = DATEADD(HOUR, -COALESCE(@GraceHours, 24), @NowUtc);

    SET @RowsDeleted = 0;

    DECLARE @Batch INT = 0, @Affected INT = 1;

    WHILE @Affected > 0 AND @Batch < @MaxBatches
    BEGIN
        DELETE TOP (@BatchSize)
        FROM core.DeviceRegistrationChallenge
        WHERE IssuedUtc < @CutoffUtc
          AND (ConsumedUtc IS NOT NULL OR ExpiresUtc < @NowUtc);

        SET @Affected = @@ROWCOUNT;
        SET @RowsDeleted = @RowsDeleted + @Affected;
        SET @Batch = @Batch + 1;
    END;

    SET @ResultCode = 0;
END;
GO

PRINT 'job.usp_Maintenance_PurgeChallenges deployed.';
GO
