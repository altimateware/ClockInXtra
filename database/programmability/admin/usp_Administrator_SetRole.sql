/*==============================================================================
  admin.usp_Administrator_SetRole
  Phase : 6 (added during Phase 12 completion)
  Called by: the administration portal (permission Administrator.Manage).

  Purpose
  -------
  Grant a role to, or remove a role from, another administrator (§17, §42).

  Rules, in order
  ---------------
  1. Authority over the target — usp_Administrator_CheckAuthorityOver: not
     yourself, and not someone holding a permission you lack.
  2. Granting: every permission the role carries must already be held by the
     actor. This is the same escalation rule usp_Administrator_Create applies
     (TH-37). Rule 1 alone would not be enough: it looks at what the target has
     NOW, not at what they are about to receive.
  3. Removing: must leave somebody able to manage administrators —
     usp_Administrator_CheckManagerRemains.
  4. The target's security stamp is rotated. Their live sessions are
     revalidated on the next request and pick up the new permission set; a
     withdrawn permission stops working immediately (TH-35).

  Granting a role already held, or removing one not held, is a successful no-op
  with no stamp rotation and no audit row.

  Result codes
      0    Success
      1003 Forbidden (escalation, or authority over the target denied)
      1070 NotFound (actor, target or role)
      1074 SeparationOfDutiesViolation (acting on yourself)
      1077 LastAdministratorManager
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_SetRole
    @AdministratorId        INT,
    @RoleId                 INT,
    @Grant                  BIT,
    @ActingAdministratorId  INT,
    @CorrelationId          UNIQUEIDENTIFIER = NULL,
    @ResultCode             INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;
    DECLARE @Check INT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION AdminSetRole;

        DECLARE @RoleName NVARCHAR(64);

        SELECT @RoleName = r.Name FROM core.Role AS r WHERE r.RoleId = @RoleId;

        IF @RoleName IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminSetRole;
            SET @ResultCode = 1070;
            RETURN;
        END;

        EXEC admin.usp_Administrator_CheckAuthorityOver
            @ActingAdministratorId = @ActingAdministratorId,
            @TargetAdministratorId = @AdministratorId,
            @ResultCode            = @Check OUTPUT;

        IF @Check <> 0
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminSetRole;
            SET @ResultCode = @Check;
            RETURN;
        END;

        DECLARE @Held BIT =
            CASE WHEN EXISTS (SELECT 1 FROM core.AdministratorRole WITH (UPDLOCK, HOLDLOCK)
                              WHERE AdministratorId = @AdministratorId AND RoleId = @RoleId)
                 THEN 1 ELSE 0 END;

        IF @Held = @Grant
        BEGIN
            IF @OuterTranCount = 0 COMMIT TRANSACTION;
            SET @ResultCode = 0;
            RETURN;
        END;

        IF @Grant = 1
        BEGIN
            IF EXISTS
            (
                SELECT 1
                FROM core.RolePermission AS granted
                WHERE granted.RoleId = @RoleId
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM core.AdministratorRole AS ar
                      INNER JOIN core.RolePermission AS held ON held.RoleId = ar.RoleId
                      WHERE ar.AdministratorId = @ActingAdministratorId
                        AND held.PermissionId = granted.PermissionId
                  )
            )
            BEGIN
                IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminSetRole;
                SET @ResultCode = 1003;
                RETURN;
            END;

            INSERT INTO core.AdministratorRole (AdministratorId, RoleId, AssignedUtc, AssignedByAdministratorId)
            VALUES (@AdministratorId, @RoleId, @NowUtc, @ActingAdministratorId);
        END
        ELSE
        BEGIN
            EXEC admin.usp_Administrator_CheckManagerRemains
                @RemovedRoleAdministratorId = @AdministratorId,
                @RemovedRoleId              = @RoleId,
                @ResultCode                 = @Check OUTPUT;

            IF @Check <> 0
            BEGIN
                IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminSetRole;
                SET @ResultCode = @Check;
                RETURN;
            END;

            DELETE FROM core.AdministratorRole
            WHERE AdministratorId = @AdministratorId AND RoleId = @RoleId;
        END;

        UPDATE core.Administrator
        SET SecurityStamp = NEWID(),
            UpdatedUtc    = @NowUtc
        WHERE AdministratorId = @AdministratorId;

        DECLARE @AuditResult INT,
                @PublicId    UNIQUEIDENTIFIER =
                    (SELECT AdministratorPublicId FROM core.Administrator WHERE AdministratorId = @AdministratorId),
                @EventType   VARCHAR(64) =
                    CASE WHEN @Grant = 1 THEN 'Administrator.RoleGranted'
                         ELSE 'Administrator.RoleRemoved' END;

        DECLARE @SubjectId NVARCHAR(64) = CAST(@PublicId AS NVARCHAR(64)),
                @Details   NVARCHAR(2000) =
                (
                    SELECT @RoleId AS roleId, @RoleName AS roleName
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
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
            ROLLBACK TRANSACTION AdminSetRole;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Administrator_SetRole deployed.';
GO
