/*==============================================================================
  mobile.usp_MobileUser_GetForAuthentication
  Phase : 6
  Called by: clock-in and device registration, to obtain the material needed
             to verify a password and a TOTP code.

  Purpose
  -------
  Return the employee's credential parameters and encrypted authenticator
  secret in a single round trip.

  TIMING BEHAVIOUR THE CALLER MUST IMPLEMENT
  ------------------------------------------
  When no employee matches, this procedure returns 1010 and no row. The
  calling service must still perform a dummy password hash with the same cost
  parameters before responding. Otherwise an unknown user would answer
  measurably faster than a known one, which hands an attacker a way to
  enumerate valid user identifiers (threat TH-14). The uniform response code
  alone is not enough; the timing has to be uniform too.

  What is deliberately NOT done here
  ----------------------------------
  No password comparison. Verification happens in the application, where the
  PBKDF2 parameters and constant-time comparison live, so that hashing is
  never performed inside a database transaction holding locks.

  Result codes
      0    Success
      1010 InvalidCredentials (no such employee)
      1014 UserInactive
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_MobileUser_GetForAuthentication
    @UserId     NVARCHAR(64),
    @ResultCode INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @MobileUserId INT,
            @Status       TINYINT;

    SELECT @MobileUserId = u.MobileUserId,
           @Status       = u.Status
    FROM core.MobileUser AS u
    WHERE u.UserId = @UserId;

    IF @MobileUserId IS NULL
    BEGIN
        SET @ResultCode = 1010;      -- InvalidCredentials: no such user
        RETURN;
    END;

    IF @Status <> 1
        SET @ResultCode = 1014;      -- UserInactive (reported to the client as INVALID_CREDENTIALS)
    ELSE
        SET @ResultCode = 0;

    SELECT
        u.MobileUserId,
        u.MobileUserPublicId,
        u.UserId,
        u.Status                AS MobileUserStatus,
        c.HashFormat,
        c.Iterations,
        c.Salt,
        c.PasswordHash,
        c.MustChange,
        m.MfaCredentialId,
        m.SecretProtected       AS MfaSecretProtected,
        m.Algorithm             AS MfaAlgorithm,
        m.Digits                AS MfaDigits,
        m.PeriodSeconds         AS MfaPeriodSeconds,
        m.Status                AS MfaStatus,
        m.LastAcceptedTimeStep  AS MfaLastAcceptedTimeStep
    FROM core.MobileUser AS u
    LEFT JOIN core.EmployeeCredential AS c
           ON c.MobileUserId = u.MobileUserId
    LEFT JOIN core.MfaCredential AS m
           ON m.MobileUserId = u.MobileUserId
          AND m.Status = 1               -- active enrolment only
    WHERE u.MobileUserId = @MobileUserId;
END;
GO

PRINT 'mobile.usp_MobileUser_GetForAuthentication deployed.';
GO
