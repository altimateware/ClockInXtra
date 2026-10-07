/*==============================================================================
  admin.usp_Administrator_ChangePassword
  Phase : 6 (added during Phase 12 completion)
  Called by: the administration portal, when a signed-in administrator changes
             their OWN password. Resetting someone else's is
             usp_Administrator_ResetPassword.

  Purpose
  -------
  Replace an administrator's password hash, clear MustChangePassword, and end
  every other session.

  WHY THIS EXISTS
  ---------------
  Every administrator account is created with MustChangePassword = 1 — by
  setup, by usp_Administrator_Create, and by a reset — and the dashboard told
  the administrator their password "must be changed before you continue". No
  procedure could change it. The flag was a promise nothing kept.

  What is verified where
  ----------------------
  The CURRENT password is verified by the application, before this is called:
  PBKDF2 runs there, not in SQL Server, and plaintext never reaches the
  database (§16, §24). Password policy is also applied there. This procedure
  owns what only the database can guarantee: the change is atomic, audited,
  and bound to the session that asked for it.

  @ExpectedSecurityStamp binds the change to a live session
  ---------------------------------------------------------
  The portal passes the stamp carried in the caller's cookie. If it no longer
  matches — the account was disabled, a role changed, or another session
  already changed the password — the request came from a session that should
  have ended, and it is refused (1071). Without this, a session evicted a
  moment ago could still set a password the legitimate owner does not know.

  The stamp is rotated, so every other session ends immediately (TH-35). The
  new stamp is returned so the portal can re-issue the caller's own cookie
  rather than signing them out of the page they are on.

  Result codes
      0    Success
      1001 InvalidRequest (malformed hash material)
      1014 UserInactive
      1070 NotFound
      1071 ConcurrencyConflict (the session's stamp is no longer current)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_ChangePassword
    @AdministratorId        INT,
    @ExpectedSecurityStamp  UNIQUEIDENTIFIER,
    @HashFormat             VARCHAR(32),
    @Iterations             INT,
    @Salt                   VARBINARY(32),
    @PasswordHash           VARBINARY(64),
    @CorrelationId          UNIQUEIDENTIFIER = NULL,
    @NewSecurityStamp       UNIQUEIDENTIFIER OUTPUT,
    @ResultCode             INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @NewSecurityStamp = NULL;

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

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION AdminChangePassword;

        DECLARE @Status   TINYINT,
                @Stamp    UNIQUEIDENTIFIER,
                @PublicId UNIQUEIDENTIFIER,
                @UserName NVARCHAR(64);

        SELECT @Status   = a.Status,
               @Stamp    = a.SecurityStamp,
               @PublicId = a.AdministratorPublicId,
               @UserName = a.UserName
        FROM core.Administrator AS a WITH (UPDLOCK, HOLDLOCK)
        WHERE a.AdministratorId = @AdministratorId;

        IF @PublicId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminChangePassword;
            SET @ResultCode = 1070;
            RETURN;
        END;

        IF @Status <> 1
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminChangePassword;
            SET @ResultCode = 1014;
            RETURN;
        END;

        IF @Stamp <> @ExpectedSecurityStamp
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminChangePassword;
            SET @ResultCode = 1071;
            RETURN;
        END;

        SET @NewSecurityStamp = NEWID();

        UPDATE core.Administrator
        SET HashFormat         = @HashFormat,
            Iterations         = @Iterations,
            Salt               = @Salt,
            PasswordHash       = @PasswordHash,
            MustChangePassword = 0,
            SecurityStamp      = @NewSecurityStamp,
            UpdatedUtc         = @NowUtc
        WHERE AdministratorId = @AdministratorId;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@PublicId AS NVARCHAR(64));

        /* That it changed, who changed it, and that sessions were ended. Never
           the hash, the salt or anything derived from the password. */
        EXEC core.usp_AuditLog_Create
            @EventType         = 'Administrator.PasswordChanged',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @ActorDisplay      = @UserName,
            @SubjectType       = 'Administrator',
            @SubjectId         = @SubjectId,
            @Result            = 1,
            @ReasonCode        = 'SELF_SERVICE',
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
            ROLLBACK TRANSACTION AdminChangePassword;
        END;

        SET @NewSecurityStamp = NULL;
        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Administrator_ChangePassword deployed.';
GO
