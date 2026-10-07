/*==============================================================================
  admin.usp_Administrator_TryConsumeTimeStep
  Phase : 6 (added during Phase 12, when the gap became visible)
  Called by: the administration portal sign-in, AFTER the code has been
             verified and the matched time step is known.

  Purpose
  -------
  Enforce one-time use of an administrator's authenticator code.

  Why this exists separately from core.usp_MfaCredential_TryConsumeTimeStep
  ------------------------------------------------------------------------
  That procedure serves employees, whose enrolment lives in core.MfaCredential.
  An administrator's authenticator lives on core.Administrator itself, because
  an administrator has exactly one and it is part of the account rather than a
  separate credential record. Two tables, so two procedures.

  The gap this closes
  -------------------
  Without it the portal could verify a code and then not consume it, leaving a
  single code usable for its whole window — about 90 seconds with the ±1 step
  tolerance. That is long enough for someone who read it over a shoulder, and
  it would apply to the accounts that can revoke devices, approve replacements
  and rewrite attendance rules.

  Why the whole check is one UPDATE
  ---------------------------------
  The WHERE clause carries the condition, so two concurrent sign-ins presenting
  the same code cannot both succeed: the first updates the row, the second
  matches nothing. A read followed by a write would leave a race open across
  portal instances.

  It also activates a pending enrolment
  -------------------------------------
  A newly enrolled secret is stored PendingActivation (see
  usp_Administrator_EnrolMfa) and becomes Active on the first code that is
  actually proven. Reaching this procedure IS that proof: the caller verifies
  the code before consuming its step, so a consumed step and a proven secret are
  the same event.

  Folding it into this UPDATE rather than adding a separate activation
  procedure is deliberate. The two facts — "this step is used" and "this secret
  works" — are established together, and splitting them across two statements
  would allow a state where the step was consumed but the credential stayed
  pending, which is precisely the kind of half-applied outcome DB-13 exists to
  prevent. Setting MfaStatus = 2 when it is already 2 is a no-op.

  Result codes
      0    Success — this time step is now consumed, and the secret is Active
      1013 OtpReplayed — the step was already used
      1015 MfaNotEnrolled — no enrolled authenticator on this account
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_TryConsumeTimeStep
    @AdministratorId INT,
    @TimeStep        BIGINT,
    @ResultCode      INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE core.Administrator
    SET MfaLastAcceptedTimeStep = @TimeStep,
        MfaStatus               = 2    -- Active: this code proved the secret
    WHERE AdministratorId = @AdministratorId
      AND MfaStatus IN (1, 2)          -- PendingActivation or Active
      AND (MfaLastAcceptedTimeStep IS NULL OR MfaLastAcceptedTimeStep < @TimeStep);

    IF @@ROWCOUNT = 1
    BEGIN
        SET @ResultCode = 0;
        RETURN;
    END;

    /* No row updated: distinguish "no authenticator" from "already used". The
       portal reports both as invalid credentials, but an investigator needs to
       tell them apart. */
    IF EXISTS (SELECT 1 FROM core.Administrator
               WHERE AdministratorId = @AdministratorId AND MfaStatus IN (1, 2))
        SET @ResultCode = 1013;        -- OtpReplayed
    ELSE
        SET @ResultCode = 1015;        -- MfaNotEnrolled
END;
GO

PRINT 'admin.usp_Administrator_TryConsumeTimeStep deployed.';
GO
