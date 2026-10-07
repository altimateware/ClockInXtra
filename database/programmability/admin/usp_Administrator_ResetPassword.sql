/*==============================================================================
  admin.usp_Administrator_ResetPassword
  Phase : 6 (added during Phase 12 completion)
  Called by: the administration portal (permission Administrator.Manage), when
             one administrator issues another a new password.

  Purpose
  -------
  Replace another administrator's password with a newly issued one that must be
  changed at the next sign-in.

  Distinct from usp_Administrator_ChangePassword on purpose
  ---------------------------------------------------------
  Changing your own password proves you know the current one. A reset proves
  nothing about the target's password; it rests entirely on the actor's
  authority. So it goes through usp_Administrator_CheckAuthorityOver — not
  yourself, and not someone holding a permission you lack. Otherwise anyone with
  Administrator.Manage could reset a Super Administrator's password and sign in
  as them (TH-37). The second factor still stands in their way, but a control
  that relies on the next control is not a control.

  What a reset does
  -----------------
  - Stores the new hash (computed in the application; no plaintext here, §24).
  - Sets MustChangePassword, because the actor has now seen this password.
  - Rotates the security stamp, ending every session the target has open. A
    reset is often a response to suspected compromise, and leaving the old
    sessions alive would defeat it.
  - Clears the target's sign-in lockout counter. A reset is commonly the answer
    to a colleague who has locked themselves out, and a fresh password that
    still cannot be used for fifteen minutes helps nobody. The lockout exists to
    slow guessing of a password; this one was issued, not guessed.

  The authenticator is NOT touched. Resetting a password and moving the second
  factor are separate decisions (usp_Administrator_EnrolMfa), so a single
  compromised administrator session cannot do both to someone in one act.

  Result codes
      0    Success
      1001 InvalidRequest (malformed hash material)
      1003 Forbidden (target holds a permission the actor lacks)
      1070 NotFound
      1074 SeparationOfDutiesViolation (resetting your own)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_ResetPassword
    @AdministratorId        INT,
    @HashFormat             VARCHAR(32),
    @Iterations             INT,
    @Salt                   VARBINARY(32),
    @PasswordHash           VARBINARY(64),
    @ActingAdministratorId  INT,
    @CorrelationId          UNIQUEIDENTIFIER = NULL,
    @ResultCode             INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Iterations < 100000
       OR DATALENGTH(@Salt) <> 32
       OR DATALENGTH(@PasswordHash) = 0
       OR @HashFormat NOT IN ('pbkdf2-sha512', 'pbkdf2-sha256')
    BEGIN
        SET @ResultCode = 1001;
        RETURN;
    END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;
    DECLARE @Check INT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION AdminResetPassword;

        EXEC admin.usp_Administrator_CheckAuthorityOver
            @ActingAdministratorId = @ActingAdministratorId,
            @TargetAdministratorId = @AdministratorId,
            @ResultCode            = @Check OUTPUT;

        IF @Check <> 0
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminResetPassword;
            SET @ResultCode = @Check;
            RETURN;
        END;

        DECLARE @PublicId UNIQUEIDENTIFIER,
                @UserName NVARCHAR(64);

        SELECT @PublicId = a.AdministratorPublicId,
               @UserName = a.UserName
        FROM core.Administrator AS a WITH (UPDLOCK, HOLDLOCK)
        WHERE a.AdministratorId = @AdministratorId;

        UPDATE core.Administrator
        SET HashFormat         = @HashFormat,
            Iterations         = @Iterations,
            Salt               = @Salt,
            PasswordHash       = @PasswordHash,
            MustChangePassword = 1,
            SecurityStamp      = NEWID(),
            UpdatedUtc         = @NowUtc
        WHERE AdministratorId = @AdministratorId;

        DECLARE @LockoutResult INT;

        EXEC core.usp_AuthenticationAttempt_Reset
            @SubjectType = 2,                   -- Administrator
            @SubjectKey  = @UserName,
            @ResultCode  = @LockoutResult OUTPUT;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@PublicId AS NVARCHAR(64));

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Administrator.PasswordReset',
            @ActorType         = 2,
            @ActorId           = @ActingAdministratorId,
            @SubjectType       = 'Administrator',
            @SubjectId         = @SubjectId,
            @Result            = 1,
            @ReasonCode        = 'ISSUED_BY_ADMINISTRATOR',
            @SourceApplication = 'Attendance.Admin',
            @CorrelationId     = @CorrelationId,
            @Details           = NULL,
            @OccurredUtc       = @NowUtc,
            @ResultCode        = @AuditResult OUTPUT;

        IF @OuterTranCount = 0
            COMMIT TRANSACTION;

        SET @ResultCode = 0;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() = -1 AND @OuterTranCount > 0
            THROW;

        IF @OuterTranCount = 0
        BEGIN
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        END
        ELSE IF XACT_STATE() = 1
        BEGIN
            ROLLBACK TRANSACTION AdminResetPassword;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Administrator_ResetPassword deployed.';
GO
