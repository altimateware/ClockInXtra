/*==============================================================================
  core.usp_MfaCredential_TryConsumeTimeStep
  Phase : 6
  Called by: clock-in and device registration, AFTER the application has
             verified the TOTP code and knows which time step matched.

  Purpose
  -------
  Enforce one-time use of a TOTP code (Claude.md §13: "implement replay
  protection where appropriate").

  A TOTP code stays valid for its whole 30-second step, and with a ±1 step
  tolerance a single code is acceptable for about 90 seconds. Without this
  check, anyone who observed a code — over someone's shoulder, or on a
  compromised device — could reuse it within that window. Recording the last
  accepted step and refusing anything at or below it closes that window.

  Why the whole check is one UPDATE
  ---------------------------------
  The WHERE clause contains the condition, so two concurrent requests carrying
  the same code cannot both succeed: the first updates the row, the second
  matches no row. A read followed by a write would leave a race open.

  The TOTP secret itself is never read here and never leaves the encrypted
  column except through the application's Data Protection services.

  Result codes
      0    Success — this time step is now consumed
      1013 OtpReplayed — the step was already used (internal code; the API
           reports INVALID_CREDENTIALS so it cannot confirm a valid password)
      1015 MfaNotEnrolled — no active authenticator for this employee
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE core.usp_MfaCredential_TryConsumeTimeStep
    @MobileUserId INT,
    @TimeStep     BIGINT,
    @ResultCode   INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE core.MfaCredential
    SET LastAcceptedTimeStep = @TimeStep
    WHERE MobileUserId = @MobileUserId
      AND Status       = 1                       -- Active enrolment only
      AND (LastAcceptedTimeStep IS NULL OR LastAcceptedTimeStep < @TimeStep);

    IF @@ROWCOUNT = 1
    BEGIN
        SET @ResultCode = 0;
        RETURN;
    END;

    /* No row updated: distinguish "no active enrolment" from "already used",
       because the two need different operational responses even though the
       client sees the same error. */
    IF EXISTS (SELECT 1 FROM core.MfaCredential
               WHERE MobileUserId = @MobileUserId AND Status = 1)
        SET @ResultCode = 1013;   -- OtpReplayed
    ELSE
        SET @ResultCode = 1015;   -- MfaNotEnrolled
END;
GO

PRINT 'core.usp_MfaCredential_TryConsumeTimeStep deployed.';
GO
