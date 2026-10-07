/*==============================================================================
  recovery.usp_Administrator_RecoverAccess
  Phase : 24
  Called by: Attendance.Admin --reset-administrator <user name>, run on a server
             by someone who is a database administrator. Never by the portal.

  Purpose
  -------
  Break-glass recovery: give a named administrator a new password and a new
  authenticator when nobody can sign in to do it the normal way.

  Why it exists
  -------------
  Every other way to reset an administrator needs another administrator
  (usp_Administrator_ResetPassword, usp_Administrator_EnrolMfa), and the
  first-administrator setup refuses once any administrator exists. So losing
  the last Super Administrator's password or phone locked everyone out for good,
  with no route back short of editing tables by hand — which is worse than this,
  because it leaves no audit trail.

  Who may run it, and why that is the control
  -------------------------------------------
  It lives in the [recovery] schema, which is granted to NO application account.
  The portal's own login cannot execute it; nor can the mobile API's or the
  maintenance job's. A compromised web server therefore cannot use it to take
  over an administrator.

  Running it requires EXECUTE on [recovery], which in practice means db_owner or
  sysadmin: someone who could already change these tables directly. This grants
  no power they do not have. What it adds is that they use it through a path
  that hashes the password properly, rotates the security stamp and writes the
  act to the append-only audit ledger, recording the Windows or SQL login that
  did it. The ledger records the executing principal as well, independently.

  What it does
  ------------
  - Stores the new password hash (computed by the caller; no plaintext here) and
    sets MustChangePassword: whoever ran recovery has seen this password.
  - Replaces the authenticator secret, PendingActivation: the first working code
    at sign-in activates it. The old authenticator stops working at once.
  - Rotates the security stamp, ending every open session of the account.
  - Clears the sign-in lockout counter.
  - Reactivates a deactivated or locked account ONLY when @Reactivate = 1: the
    operator must decide that explicitly, because a deactivation may have been
    deliberate.

  It does NOT change roles. Recovery restores access to an account; what that
  account may do is still decided in the portal, by the rules there.

  Result codes
      0    Success
      1001 InvalidRequest (malformed hash material or secret)
      1014 UserInactive (account not active and @Reactivate = 0)
      1070 NotFound
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE recovery.usp_Administrator_RecoverAccess
    @UserName           NVARCHAR(64),
    @HashFormat         VARCHAR(32),
    @Iterations         INT,
    @Salt               VARBINARY(32),
    @PasswordHash       VARBINARY(64),
    @MfaSecretProtected VARBINARY(MAX),
    @Reactivate         BIT              = 0,
    @CorrelationId      UNIQUEIDENTIFIER = NULL,
    @ResultCode         INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @UserName IS NULL OR LEN(LTRIM(RTRIM(@UserName))) = 0
       OR @Iterations < 100000
       OR DATALENGTH(@Salt) <> 32
       OR DATALENGTH(@PasswordHash) = 0
       OR @HashFormat NOT IN ('pbkdf2-sha512', 'pbkdf2-sha256')
       OR @MfaSecretProtected IS NULL OR DATALENGTH(@MfaSecretProtected) = 0
    BEGIN
        SET @ResultCode = 1001;
        RETURN;
    END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION RecoverAccess;

        DECLARE @AdministratorId INT,
                @PublicId        UNIQUEIDENTIFIER,
                @Status          TINYINT;

        SELECT @AdministratorId = a.AdministratorId,
               @PublicId        = a.AdministratorPublicId,
               @Status          = a.Status
        FROM core.Administrator AS a WITH (UPDLOCK, HOLDLOCK)
        WHERE a.UserName = @UserName;

        IF @AdministratorId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION RecoverAccess;
            SET @ResultCode = 1070;
            RETURN;
        END;

        IF @Status <> 1 AND @Reactivate = 0
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION RecoverAccess;
            SET @ResultCode = 1014;
            RETURN;
        END;

        UPDATE core.Administrator
        SET HashFormat              = @HashFormat,
            Iterations              = @Iterations,
            Salt                    = @Salt,
            PasswordHash            = @PasswordHash,
            MustChangePassword      = 1,
            MfaSecretProtected      = @MfaSecretProtected,
            MfaStatus               = 1,          -- PendingActivation
            MfaLastAcceptedTimeStep = NULL,
            Status                  = 1,
            SecurityStamp           = NEWID(),
            UpdatedUtc              = @NowUtc
        WHERE AdministratorId = @AdministratorId;

        DECLARE @LockoutResult INT;

        EXEC core.usp_AuthenticationAttempt_Reset
            @SubjectType = 2,                     -- Administrator
            @SubjectKey  = @UserName,
            @ResultCode  = @LockoutResult OUTPUT;

        /* Who did it. ORIGINAL_LOGIN is the login that opened the connection,
           unaffected by any impersonation; HOST_NAME is where the command ran.
           Neither is a secret, and both are what an investigator needs first. */
        DECLARE @Details NVARCHAR(2000) =
        (
            SELECT ORIGINAL_LOGIN()                         AS performedByLogin,
                   HOST_NAME()                              AS performedOnHost,
                   CAST(@Status AS INT)                     AS previousStatus,
                   CAST(CASE WHEN @Status <> 1 THEN 1 ELSE 0 END AS BIT) AS reactivated
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@PublicId AS NVARCHAR(64)),
                @Actor       NVARCHAR(128) = LEFT(CONCAT(N'recovery: ', ORIGINAL_LOGIN()), 128);

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Administrator.AccessRecovered',
            @ActorType         = 0,                -- System: no administrator acted
            @ActorId           = NULL,
            @ActorDisplay      = @Actor,
            @SubjectType       = 'Administrator',
            @SubjectId         = @SubjectId,
            @Result            = 1,
            @ReasonCode        = 'BREAK_GLASS_RECOVERY',
            @SourceApplication = 'Attendance.Admin',
            @CorrelationId     = @CorrelationId,
            @Details           = @Details,
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
            ROLLBACK TRANSACTION RecoverAccess;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'recovery.usp_Administrator_RecoverAccess deployed.';
GO
