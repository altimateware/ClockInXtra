/*==============================================================================
  admin.usp_Administrator_Bootstrap
  Phase : 6 (added during Phase 12, when the gap became visible)
  Called by: the one-time setup command in the administration portal.

  Purpose
  -------
  Create the FIRST administrator. Nothing else can: usp_Administrator_Create
  requires @CreatedByAdministratorId, and on a fresh deployment there is no
  administrator to name — so without this the portal cannot be signed into at
  all, and the system has no way to approve a device or decide a setting.

  THE SAFETY PROPERTY
  -------------------
  This procedure refuses when core.Administrator holds ANY row. It is not
  "create an administrator"; it is "create the first one, if there is none".
  That single condition is what stops it being a permanent back door: once
  setup has run, every later account must come through
  usp_Administrator_Create, which records who created it.

  It cannot be used to regain access to a system whose administrators have all
  been locked out, and that is deliberate. Recovering from that situation is a
  database-administrator task with its own authorisation, not something an
  application procedure should quietly permit.

  The password is hashed by the APPLICATION before it arrives here, with the
  parameters in use at the time (PBKDF2-HMAC-SHA512). SQL Server never sees the
  password, and no plaintext credential is passed, logged or stored (§16, §24).

  The authenticator is enrolled in the SAME statement, and that is deliberate
  ---------------------------------------------------------------------------
  Security.RequireAdministratorMfa is true, so an administrator with no
  authenticator cannot sign in — and the first one has no colleague who could
  enrol one for them. Creating the account and enrolling its secret must
  therefore be one atomic act. If they were two calls and the second failed, the
  operator would be left holding an account that can never be used, and this
  procedure would refuse to run again because an administrator now exists.

  @MfaSecretProtected is ciphertext from the application's Data Protection
  services. It is stored PendingActivation and becomes Active on the first code
  that verifies at sign-in, so a secret that was never successfully scanned
  costs nothing more than re-running setup on an empty system — rather than
  locking out the only account that exists.

  Result codes
      0    Success
      1001 InvalidRequest — an administrator already exists, or no secret given
      1070 NotFound — the Super Administrator role is missing (seed not applied)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_Bootstrap
    @UserName              NVARCHAR(64),
    @DisplayName           NVARCHAR(160),
    @Email                 NVARCHAR(256) = NULL,
    @HashFormat            VARCHAR(32),
    @Iterations            INT,
    @Salt                  VARBINARY(32),
    @PasswordHash          VARBINARY(64),
    @MfaSecretProtected    VARBINARY(MAX),
    @CorrelationId         UNIQUEIDENTIFIER = NULL,
    @AdministratorId       INT              OUTPUT,
    @AdministratorPublicId UNIQUEIDENTIFIER OUTPUT,
    @ResultCode            INT              OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @AdministratorId       = NULL;
    SET @AdministratorPublicId = NULL;

    /* Refused before anything is created. An account without an authenticator
       could never sign in, and this procedure would not run a second time. */
    IF @MfaSecretProtected IS NULL OR DATALENGTH(@MfaSecretProtected) = 0
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
            SAVE TRANSACTION AdminBootstrap;

        /* HOLDLOCK so two concurrent setup attempts cannot both see an empty
           table and both insert. */
        IF EXISTS (SELECT 1 FROM core.Administrator WITH (UPDLOCK, HOLDLOCK))
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminBootstrap;
            SET @ResultCode = 1001;      -- setup has already been done
            RETURN;
        END;

        DECLARE @RoleId INT;

        SELECT @RoleId = r.RoleId
        FROM core.Role AS r
        WHERE r.Name = N'Super Administrator';

        IF @RoleId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminBootstrap;
            SET @ResultCode = 1070;      -- the role seed has not been applied
            RETURN;
        END;

        SET @AdministratorPublicId = NEWID();

        INSERT INTO core.Administrator
            (AdministratorPublicId, UserName, DisplayName, Email,
             HashFormat, Iterations, Salt, PasswordHash,
             MustChangePassword, SecurityStamp,
             MfaSecretProtected, MfaStatus, Status, CreatedUtc)
        VALUES
            (@AdministratorPublicId, @UserName, @DisplayName, @Email,
             @HashFormat, @Iterations, @Salt, @PasswordHash,
             /* The operator typed this password at a console during setup, so it
                is changed at first sign-in like any other issued credential. */
             1, NEWID(),
             /* PendingActivation: the first working code at sign-in activates it. */
             @MfaSecretProtected, 1, 1, @NowUtc);

        SET @AdministratorId = SCOPE_IDENTITY();

        INSERT INTO core.AdministratorRole (AdministratorId, RoleId, AssignedUtc)
        VALUES (@AdministratorId, @RoleId, @NowUtc);

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@AdministratorPublicId AS NVARCHAR(64));

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Administrator.Bootstrapped',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @ActorDisplay      = @UserName,
            @SubjectType       = 'Administrator',
            @SubjectId         = @SubjectId,
            @Result            = 1,
            @ReasonCode        = 'FIRST_ADMINISTRATOR',
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
            ROLLBACK TRANSACTION AdminBootstrap;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Administrator_Bootstrap deployed.';
GO
