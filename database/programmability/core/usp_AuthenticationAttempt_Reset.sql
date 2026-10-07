/*==============================================================================
  core.usp_AuthenticationAttempt_Reset
  Phase : 6
  Called by: authentication procedures after a FULLY successful
             authentication, and by the admin portal when an administrator
             deliberately unlocks an account.

  Purpose
  -------
  Clear the consecutive-failure counter and any lock.

  Important: call this only when every factor has succeeded. Resetting after a
  correct password but before the OTP check would let an attacker keep the
  counter at zero by alternating a known password with OTP guesses, which
  would defeat the brute-force protection on the second factor.

  Result codes
      0 Success (no row to clear is not an error)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE core.usp_AuthenticationAttempt_Reset
    @SubjectType TINYINT,
    @SubjectKey  NVARCHAR(128),
    @ResultCode  INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM core.AuthenticationAttempt
    WHERE SubjectType = @SubjectType
      AND SubjectKey  = @SubjectKey;

    SET @ResultCode = 0;
END;
GO

PRINT 'core.usp_AuthenticationAttempt_Reset deployed.';
GO
