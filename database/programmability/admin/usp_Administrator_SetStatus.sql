/*==============================================================================
  admin.usp_Administrator_SetStatus
  Phase : 6 (added during Phase 12 completion)
  Called by: the administration portal (permission Administrator.Manage).

  Purpose
  -------
  Activate or deactivate another administrator's account (§17).

  Rules, in order
  ---------------
  1. Authority — usp_Administrator_CheckAuthorityOver: not yourself, and not an
     administrator who holds a permission you lack.
  2. Deactivation needs a reason. Taking away someone's access is exactly the
     kind of act an auditor will later ask about.
  3. Deactivation must leave somebody able to manage administrators —
     usp_Administrator_CheckManagerRemains. Bootstrap will not run again once an
     administrator exists, so a system with no remaining manager could only be
     recovered by editing the database by hand.
  4. The security stamp is rotated. The portal revalidates it on every request,
     so a deactivated administrator's open sessions end on their next click
     rather than when their cookie happens to expire (TH-35).

  Only 0 (Inactive) and 1 (Active) are set here. Status 2 (Locked) belongs to
  the authentication lockout mechanism, not to an administrator's decision.

  Setting the status an account already has is a successful no-op: no stamp
  rotation, no audit row. A double-submitted form should not end the target's
  sessions twice or clutter the trail.

  Result codes
      0    Success
      1001 InvalidRequest (status out of range, or deactivation without reason)
      1003 Forbidden (target holds a permission the actor lacks)
      1070 NotFound
      1074 SeparationOfDutiesViolation (acting on yourself)
      1077 LastAdministratorManager
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_SetStatus
    @AdministratorId        INT,
    @Status                 TINYINT,
    @Reason                 NVARCHAR(256) = NULL,
    @ActingAdministratorId  INT,
    @CorrelationId          UNIQUEIDENTIFIER = NULL,
    @ResultCode             INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Status NOT IN (0, 1)
       OR (@Status = 0 AND LEN(LTRIM(RTRIM(ISNULL(@Reason, N'')))) = 0)
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
            SAVE TRANSACTION AdminSetStatus;

        EXEC admin.usp_Administrator_CheckAuthorityOver
            @ActingAdministratorId = @ActingAdministratorId,
            @TargetAdministratorId = @AdministratorId,
            @ResultCode            = @Check OUTPUT;

        IF @Check <> 0
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminSetStatus;
            SET @ResultCode = @Check;
            RETURN;
        END;

        DECLARE @Current  TINYINT,
                @PublicId UNIQUEIDENTIFIER;

        SELECT @Current  = a.Status,
               @PublicId = a.AdministratorPublicId
        FROM core.Administrator AS a WITH (UPDLOCK, HOLDLOCK)
        WHERE a.AdministratorId = @AdministratorId;

        IF @Current = @Status
        BEGIN
            IF @OuterTranCount = 0 COMMIT TRANSACTION;
            SET @ResultCode = 0;
            RETURN;
        END;

        IF @Status = 0
        BEGIN
            EXEC admin.usp_Administrator_CheckManagerRemains
                @DeactivatedAdministratorId = @AdministratorId,
                @ResultCode                 = @Check OUTPUT;

            IF @Check <> 0
            BEGIN
                IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminSetStatus;
                SET @ResultCode = @Check;
                RETURN;
            END;
        END;

        UPDATE core.Administrator
        SET Status        = @Status,
            SecurityStamp = NEWID(),
            UpdatedUtc    = @NowUtc
        WHERE AdministratorId = @AdministratorId;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@PublicId AS NVARCHAR(64)),
                @EventType   VARCHAR(64) =
                    CASE WHEN @Status = 1 THEN 'Administrator.Activated'
                         ELSE 'Administrator.Deactivated' END,
                @Details     NVARCHAR(2000) =
                (
                    SELECT @Current AS previousStatus,
                           @Status  AS newStatus,
                           NULLIF(LTRIM(RTRIM(@Reason)), N'') AS reason
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
                );

        EXEC core.usp_AuditLog_Create
            @EventType         = @EventType,
            @ActorType         = 2,
            @ActorId           = @ActingAdministratorId,
            @SubjectType       = 'Administrator',
            @SubjectId         = @SubjectId,
            @Result            = 1,
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
            ROLLBACK TRANSACTION AdminSetStatus;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Administrator_SetStatus deployed.';
GO
