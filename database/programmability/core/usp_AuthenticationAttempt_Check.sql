/*==============================================================================
  core.usp_AuthenticationAttempt_Check
  Phase : 6
  Called by: mobile and admin authentication procedures, BEFORE any password
             or OTP verification is attempted.

  Purpose
  -------
  Report whether an account is currently locked out.

  Why the database holds this counter
  -----------------------------------
  The ASP.NET Core rate limiter keeps its counters in the memory of a single
  process. With two or more IIS nodes behind a load balancer, an attacker
  simply spreads attempts across nodes and the limit never triggers. Account
  lockout is a security control, so it is counted where all nodes share it
  (decision TD-10, Claude.md §45).

  This procedure does not clear an expired lock; it simply reports that the
  account is no longer locked. RegisterFailure and Reset maintain the row.

  Result codes
      0    Not locked, the caller may proceed to verify credentials
      1011 AccountLocked
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE core.usp_AuthenticationAttempt_Check
    @SubjectType     TINYINT,          -- 1 MobileUser, 2 Administrator
    @SubjectKey      NVARCHAR(128),
    @LockedUntilUtc  DATETIME2(3) OUTPUT,
    @ResultCode      INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    /* Initialise the OUTPUT parameter before use. In T-SQL, "SELECT @var = col"
       that matches NO ROWS leaves the variable holding whatever the caller
       passed in. Without this line, checking an account whose lockout row has
       just been deleted would report the caller's previous lock time and keep
       a cleared account locked. */
    SET @LockedUntilUtc = NULL;

    SELECT @LockedUntilUtc = a.LockedUntilUtc
    FROM core.AuthenticationAttempt AS a
    WHERE a.SubjectType = @SubjectType
      AND a.SubjectKey  = @SubjectKey;

    IF @LockedUntilUtc IS NOT NULL AND @LockedUntilUtc > SYSUTCDATETIME()
        SET @ResultCode = 1011;       -- AccountLocked
    ELSE
    BEGIN
        SET @LockedUntilUtc = NULL;   -- an expired lock is not a lock
        SET @ResultCode = 0;
    END;
END;
GO

PRINT 'core.usp_AuthenticationAttempt_Check deployed.';
GO
