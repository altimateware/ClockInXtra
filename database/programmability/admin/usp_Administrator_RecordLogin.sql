/*==============================================================================
  admin.usp_Administrator_RecordLogin
  Phase : 6
  Called by: the administration portal, after a FULLY successful sign-in —
             password verified and, where required, the authenticator code
             verified too.

  Purpose
  -------
  Record the successful sign-in, clear the lockout counter, and write the
  audit entry (§32).

  Call this only after EVERY factor has passed
  --------------------------------------------
  Clearing the failure counter after a correct password but before the
  authenticator check would let an attacker who already knows the password
  keep the counter at zero while guessing codes indefinitely. The second
  factor's brute-force protection depends on this being the last step, not an
  intermediate one.

  Failed sign-ins are NOT recorded here. They go through
  core.usp_AuthenticationAttempt_RegisterFailure and a security event, so that
  the precise reason is retained without being returned to the browser.

  Transaction handling (decision DB-13)
  -------------------------------------
  The login timestamp, the lockout reset and both trail entries belong
  together: a sign-in that cleared the lockout but left no audit record would
  be exactly the gap an investigator needs. The outer transaction count is
  captured so a refusal rolls back to a savepoint rather than discarding a
  caller's transaction.

  Result codes
      0    Success
      1070 NotFound
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_RecordLogin
    @AdministratorId  INT,
    @CorrelationId    UNIQUEIDENTIFIER = NULL,
    @SourceAddressHash VARBINARY(32)   = NULL,
    @ResultCode       INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION AdminLogin;

        DECLARE @UserName NVARCHAR(64),
                @PublicId UNIQUEIDENTIFIER;

        SELECT @UserName = a.UserName,
               @PublicId = a.AdministratorPublicId
        FROM core.Administrator AS a WITH (UPDLOCK)
        WHERE a.AdministratorId = @AdministratorId;

        IF @UserName IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminLogin;
            SET @ResultCode = 1070;      -- NotFound
            RETURN;
        END;

        UPDATE core.Administrator
        SET LastLoginUtc = @NowUtc
        WHERE AdministratorId = @AdministratorId;

        DECLARE @ResetResult INT;

        EXEC core.usp_AuthenticationAttempt_Reset
            @SubjectType = 2,            -- Administrator
            @SubjectKey  = @UserName,
            @ResultCode  = @ResetResult OUTPUT;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@PublicId AS NVARCHAR(64));

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Administrator.LoginSucceeded',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @ActorDisplay      = @UserName,
            @SubjectType       = 'Administrator',
            @SubjectId         = @SubjectId,
            @Result            = 1,
            @SourceApplication = 'Attendance.Admin',
            @CorrelationId     = @CorrelationId,
            @Details           = NULL,
            @OccurredUtc       = @NowUtc,
            @ResultCode        = @AuditResult OUTPUT;

        /* A successful sign-in is also a security event: investigators need
           successes and failures in one timeline, not just the failures. */
        DECLARE @SecurityResult INT;

        EXEC core.usp_SecurityEvent_Create
            @EventType         = 'Auth.Succeeded',
            @Severity          = 1,      -- Information
            @SubjectType       = 2,      -- Administrator
            @SubjectKey        = @UserName,
            @ReasonCode        = 'LOGIN_OK',
            @SourceApplication = 'Attendance.Admin',
            @SourceAddressHash = @SourceAddressHash,
            @CorrelationId     = @CorrelationId,
            @Details           = NULL,
            @OccurredUtc       = @NowUtc,
            @ResultCode        = @SecurityResult OUTPUT;

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
            ROLLBACK TRANSACTION AdminLogin;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Administrator_RecordLogin deployed.';
GO
