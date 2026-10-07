/*==============================================================================
  admin.usp_Administrator_RecordLogout
  Phase : 20
  Called by: the administration portal, when an administrator signs out.

  Purpose
  -------
  Record the sign-out (§32 lists logout among the audited events) and end
  every session the administrator holds.

  Why signing out rotates the security stamp
  ------------------------------------------
  Deleting the cookie in the browser ends that browser's session and nothing
  else. A copy of the cookie — taken from a shared machine, a proxy log or
  malware — would otherwise stay valid until it expired: the classic "session
  not invalidated on logout" weakness. The portal revalidates the security
  stamp on every request, so changing it here makes every outstanding copy
  worthless at once. The cost is that signing out in one browser also signs
  the administrator out of any other; for accounts that can revoke devices
  and rewrite attendance rules, that is the right trade.

  Session EXPIRY is not recorded: nothing happens at that moment for the
  server to observe, and inventing an event for it would be a guess.

  Result codes
      0    Success
      1070 NotFound
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_RecordLogout
    @AdministratorId  INT,
    @CorrelationId    UNIQUEIDENTIFIER = NULL,
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
            SAVE TRANSACTION AdminLogout;

        DECLARE @UserName NVARCHAR(64),
                @PublicId UNIQUEIDENTIFIER;

        SELECT @UserName = a.UserName,
               @PublicId = a.AdministratorPublicId
        FROM core.Administrator AS a WITH (UPDLOCK)
        WHERE a.AdministratorId = @AdministratorId;

        IF @UserName IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminLogout;
            SET @ResultCode = 1070;      -- NotFound
            RETURN;
        END;

        UPDATE core.Administrator
        SET SecurityStamp = NEWID(),
            UpdatedUtc    = @NowUtc
        WHERE AdministratorId = @AdministratorId;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@PublicId AS NVARCHAR(64));

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Administrator.LoggedOut',
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
            ROLLBACK TRANSACTION AdminLogout;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Administrator_RecordLogout deployed.';
GO
