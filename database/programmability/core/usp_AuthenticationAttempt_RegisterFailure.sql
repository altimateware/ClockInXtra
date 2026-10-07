/*==============================================================================
  core.usp_AuthenticationAttempt_RegisterFailure
  Phase : 6
  Called by: authentication procedures after ANY credential failure — wrong
             password, wrong OTP, replayed OTP, or unknown account.

  Purpose
  -------
  Count consecutive failures per account and lock the account once the
  configured threshold is reached (Claude.md §36, threats TH-14, TH-15).

  Two details that matter
  -----------------------
  1. Failures are also counted for accounts that do not exist. If unknown
     accounts were not counted, the difference in behaviour would let an
     attacker enumerate valid user identifiers.
  2. The counter window resets when the previous failure is older than the
     lockout period, so an occasional typo months apart never accumulates
     into a lock.

  Thresholds are passed in by the caller, which reads them from
  core.ApplicationSetting (Security.MobileLockoutThreshold /
  Security.AdministratorLockoutThreshold). They are not hard-coded here.

  Result codes
      0    Failure recorded, account still usable
      1011 AccountLocked (this failure triggered or extended the lock)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE core.usp_AuthenticationAttempt_RegisterFailure
    @SubjectType     TINYINT,
    @SubjectKey      NVARCHAR(128),
    @Threshold       INT,
    @LockoutMinutes  INT,
    @FailedCount     INT          OUTPUT,
    @LockedUntilUtc  DATETIME2(3) OUTPUT,
    @ResultCode      INT          OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();

    /* MERGE with HOLDLOCK makes the upsert atomic: concurrent failures from
       several nodes cannot both insert the same key, and the count cannot be
       lost to a race. */
    MERGE core.AuthenticationAttempt WITH (HOLDLOCK) AS target
    USING (SELECT @SubjectType AS SubjectType, @SubjectKey AS SubjectKey) AS source
        ON target.SubjectType = source.SubjectType
       AND target.SubjectKey  = source.SubjectKey
    WHEN MATCHED THEN
        UPDATE SET
            /* Restart the count when the previous failure is outside the window. */
            FailedCount    = CASE
                                WHEN target.LastFailedUtc IS NULL
                                  OR DATEDIFF(MINUTE, target.LastFailedUtc, @NowUtc) > @LockoutMinutes
                                THEN 1
                                ELSE target.FailedCount + 1
                             END,
            FirstFailedUtc = CASE
                                WHEN target.LastFailedUtc IS NULL
                                  OR DATEDIFF(MINUTE, target.LastFailedUtc, @NowUtc) > @LockoutMinutes
                                THEN @NowUtc
                                ELSE target.FirstFailedUtc
                             END,
            LastFailedUtc  = @NowUtc,
            LockedUntilUtc = CASE
                                WHEN (CASE
                                        WHEN target.LastFailedUtc IS NULL
                                          OR DATEDIFF(MINUTE, target.LastFailedUtc, @NowUtc) > @LockoutMinutes
                                        THEN 1
                                        ELSE target.FailedCount + 1
                                      END) >= @Threshold
                                THEN DATEADD(MINUTE, @LockoutMinutes, @NowUtc)
                                ELSE target.LockedUntilUtc
                             END,
            UpdatedUtc     = @NowUtc
    WHEN NOT MATCHED THEN
        INSERT (SubjectType, SubjectKey, FailedCount, FirstFailedUtc, LastFailedUtc, LockedUntilUtc, UpdatedUtc)
        VALUES (@SubjectType, @SubjectKey, 1, @NowUtc, @NowUtc,
                CASE WHEN 1 >= @Threshold THEN DATEADD(MINUTE, @LockoutMinutes, @NowUtc) END,
                @NowUtc);

    SELECT @FailedCount    = a.FailedCount,
           @LockedUntilUtc = a.LockedUntilUtc
    FROM core.AuthenticationAttempt AS a
    WHERE a.SubjectType = @SubjectType
      AND a.SubjectKey  = @SubjectKey;

    IF @LockedUntilUtc IS NOT NULL AND @LockedUntilUtc > @NowUtc
        SET @ResultCode = 1011;       -- AccountLocked
    ELSE
        SET @ResultCode = 0;
END;
GO

PRINT 'core.usp_AuthenticationAttempt_RegisterFailure deployed.';
GO
