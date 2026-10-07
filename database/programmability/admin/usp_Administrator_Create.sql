/*==============================================================================
  admin.usp_Administrator_Create
  Phase : 6
  Called by: the administration portal (permission Administrator.Manage).

  Purpose
  -------
  Create an administration portal account and, optionally, assign its first
  role (§17, §42).

  PRIVILEGE ESCALATION IS BLOCKED HERE
  ------------------------------------
  An administrator may not create an account holding a permission they do not
  hold themselves. Without that rule, anyone with Administrator.Manage could
  mint a Super Administrator and then sign in as it — which would make every
  other permission boundary decorative (threat TH-37).

  The check is done in the database rather than only in the portal, because it
  is the kind of rule that quietly stops being enforced when a second code path
  appears. The comparison is over effective permissions, not role names, so
  renaming or re-scoping a role cannot slip past it.

  Passwords
  ---------
  The hash, salt and iteration count are computed in the application and passed
  in; plaintext never reaches the database (§24). MustChangePassword defaults to
  1 so an administrator-chosen initial password cannot remain in use.

  MFA is not enrolled here. A new account has MfaStatus = 0 and, when
  Security.RequireAdministratorMfa is true, must complete enrolment before it
  can sign in — so an account created and forgotten is not a standing way in.

  Transaction handling (decision DB-13)
  -------------------------------------
  The outer transaction count is captured first, so a refusal — including the
  escalation refusal — rolls back to a savepoint rather than discarding a
  caller's transaction. This procedure is where that defect was first
  observed: a bare ROLLBACK inside a caller's transaction zeroed @@TRANCOUNT
  and produced SQL Server error 266 on return.

  Result codes
      0    Success
      1001 InvalidRequest
      1003 Forbidden (the creator lacks a permission the role grants)
      1070 NotFound (the creating administrator or the role does not exist)
      1072 DuplicateName (user name already in use)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_Create
    @UserName              NVARCHAR(64),
    @DisplayName           NVARCHAR(160),
    @Email                 NVARCHAR(256) = NULL,
    @HashFormat            VARCHAR(32),
    @Iterations            INT,
    @Salt                  VARBINARY(32),
    @PasswordHash          VARBINARY(64),
    @RoleId                INT = NULL,
    @CreatedByAdministratorId INT,
    @CorrelationId         UNIQUEIDENTIFIER = NULL,
    @AdministratorId       INT              OUTPUT,
    @AdministratorPublicId UNIQUEIDENTIFIER OUTPUT,
    @ResultCode            INT              OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @AdministratorId = NULL;
    SET @AdministratorPublicId = NULL;

    IF LEN(LTRIM(RTRIM(@UserName))) = 0
       OR LEN(LTRIM(RTRIM(@DisplayName))) = 0
       OR @Iterations < 100000
       OR DATALENGTH(@Salt) <> 32
       OR DATALENGTH(@PasswordHash) = 0
       OR @HashFormat NOT IN ('pbkdf2-sha512', 'pbkdf2-sha256')
    BEGIN
        SET @ResultCode = 1001;          -- InvalidRequest
        RETURN;
    END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION AdministratorCreate;

        /* The creator must exist and be active: a disabled account must not be
           able to act, even if a session somehow survived. */
        IF NOT EXISTS (SELECT 1 FROM core.Administrator
                       WHERE AdministratorId = @CreatedByAdministratorId AND Status = 1)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdministratorCreate;
            SET @ResultCode = 1070;      -- NotFound
            RETURN;
        END;

        IF @RoleId IS NOT NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM core.Role WHERE RoleId = @RoleId)
            BEGIN
                IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdministratorCreate;
                SET @ResultCode = 1070;  -- NotFound
                RETURN;
            END;

            /* Any permission the new role grants that the creator does not
               already hold is an escalation. */
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
                      WHERE ar.AdministratorId = @CreatedByAdministratorId
                        AND held.PermissionId = granted.PermissionId
                  )
            )
            BEGIN
                IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdministratorCreate;
                SET @ResultCode = 1003;  -- Forbidden: privilege escalation
                RETURN;
            END;
        END;

        IF EXISTS (SELECT 1 FROM core.Administrator WHERE UserName = @UserName)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdministratorCreate;
            SET @ResultCode = 1072;      -- DuplicateName
            RETURN;
        END;

        SET @AdministratorPublicId = NEWID();

        INSERT INTO core.Administrator
            (AdministratorPublicId, UserName, DisplayName, Email,
             HashFormat, Iterations, Salt, PasswordHash,
             MustChangePassword, MfaStatus, Status, CreatedUtc, CreatedByAdministratorId)
        VALUES
            (@AdministratorPublicId, @UserName, @DisplayName, @Email,
             @HashFormat, @Iterations, @Salt, @PasswordHash,
             1, 0, 1, @NowUtc, @CreatedByAdministratorId);

        SET @AdministratorId = SCOPE_IDENTITY();

        IF @RoleId IS NOT NULL
        BEGIN
            INSERT INTO core.AdministratorRole (AdministratorId, RoleId, AssignedUtc, AssignedByAdministratorId)
            VALUES (@AdministratorId, @RoleId, @NowUtc, @CreatedByAdministratorId);
        END;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@AdministratorPublicId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000);

        /* Credential material is never audited — only who was created, by whom,
           and with what authority. */
        SET @Details =
        (
            SELECT @UserName    AS userName,
                   @DisplayName AS displayName,
                   @RoleId      AS assignedRoleId
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Administrator.Created',
            @ActorType         = 2,
            @ActorId           = @CreatedByAdministratorId,
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
            ROLLBACK TRANSACTION AdministratorCreate;
        END;

        IF ERROR_NUMBER() IN (2627, 2601)
        BEGIN
            SET @ResultCode = 1072;      -- DuplicateName
            RETURN;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Administrator_Create deployed.';
GO
