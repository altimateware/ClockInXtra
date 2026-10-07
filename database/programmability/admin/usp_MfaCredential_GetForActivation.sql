/*==============================================================================
  admin.usp_MfaCredential_GetForActivation
  Phase : 6 (added during Phase 12, when the gap became visible)
  Called by: the administration portal, to verify the code an employee produces
             while proving their authenticator works.

  Purpose
  -------
  Return the protected secret and algorithm parameters for an enrolment that is
  still PENDING ACTIVATION, so the portal can verify one code and then call
  usp_MfaCredential_Activate with the matched time step.

  WHY THIS IS RESTRICTED TO PENDING CREDENTIALS
  ---------------------------------------------
  The WHERE clause requires Status = 0. An ACTIVE authenticator's secret is
  never returned by this procedure, so a portal account cannot use it to read
  the second factor of an employee who is already enrolled and clocking in.
  Enrolment is the only moment the portal has a legitimate reason to hold that
  secret, and this limits it to exactly that moment.

  Replacing an active authenticator remains possible — but only through
  usp_MfaCredential_Enrol with @ReplaceExisting, which revokes the old one and
  audits both facts. That path is visible; quietly reading an existing secret
  would not be.

  What is returned is CIPHERTEXT. Only the application's Data Protection key
  ring can read it, and the plaintext exists in the portal for the length of one
  verification (§13, §33).

  Result codes
      0    Success
      1070 NotFound — no enrolment awaiting activation for this employee
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_MfaCredential_GetForActivation
    @MobileUserId INT,
    @ResultCode   INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @MfaCredentialId INT;

    SELECT @MfaCredentialId = m.MfaCredentialId
    FROM core.MfaCredential AS m
    WHERE m.MobileUserId = @MobileUserId
      AND m.Status = 0;                 -- PendingActivation only

    IF @MfaCredentialId IS NULL
    BEGIN
        SET @ResultCode = 1070;         -- NotFound
        RETURN;
    END;

    SELECT
        m.MfaCredentialId,
        m.SecretProtected,
        m.Algorithm,
        m.Digits,
        m.PeriodSeconds
    FROM core.MfaCredential AS m
    WHERE m.MfaCredentialId = @MfaCredentialId;

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_MfaCredential_GetForActivation deployed.';
GO
