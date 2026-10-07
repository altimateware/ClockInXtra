/*==============================================================================
  admin.usp_Administrator_EnrolMfa
  Phase : 6 (added during Phase 13, when the gap became visible)
  Called by: the administration portal (permission Administrator.Manage), when
             one administrator enrols or resets another's authenticator —
             including straight after creating their account.

  The first administrator does not come through here: usp_Administrator_Bootstrap
  stores its secret in the same statement that creates the account.

  Purpose
  -------
  Set an administrator's TOTP secret. Nothing else could.

  WHY THIS EXISTS
  ---------------
  core.Administrator has carried MfaSecretProtected, MfaStatus and
  MfaLastAcceptedTimeStep since the schema was written, and
  AdministratorAuthenticator refuses a sign-in when MfaStatus is None while
  Security.RequireAdministratorMfa is true. But no procedure ever WROTE those
  columns: usp_MfaCredential_Enrol serves employees, through a different table.

  The effect was that a freshly deployed system could not be signed into at all.
  Bootstrap created an administrator with MfaStatus = 0, the sign-in page asked
  for a code, and there was no path — console, portal or otherwise — that could
  ever give the account one. A complete chain with no way to start it.

  Enrolment does not activate
  ---------------------------
  The secret is stored with MfaStatus = 1 (PendingActivation). It becomes Active
  only when the administrator first proves a working code at sign-in, where
  usp_Administrator_TryConsumeTimeStep consumes the step and activates the
  secret in one statement. Storing it Active would mean that a secret which was
  never successfully scanned locks the account out until a colleague re-enrols
  it.

  Sign-in therefore accepts a PendingActivation administrator whose code
  verifies, and activates it on that first use. The security property is the
  same in both states — a valid code from the enrolled secret must be presented
  — so this is not a relaxation.

  The secret never appears here in the clear
  ------------------------------------------
  @SecretProtected is ciphertext from the application's Data Protection
  services, protected under SecretPurposes.AdministratorTotpSecret. The
  plaintext exists only for as long as it takes to show the operator the
  enrolment URI once, and it is never written to an audit entry, a log or an
  error message (§13, §32, §33). The audit row below records that an enrolment
  happened and who did it, never what was enrolled.

  Replacing an existing authenticator is deliberate
  -------------------------------------------------
  An administrator who already has a pending or active authenticator is refused
  with 1064 unless @ReplaceExisting is set. Silently replacing one is how
  somebody loses access to their own account, and how an attacker with portal
  access quietly moves the second factor to a device they control.

  WHO MAY DO THIS
  ---------------
  @EnrolledByAdministratorId is required and goes through
  usp_Administrator_CheckAuthorityOver: not yourself, and not someone holding a
  permission you lack.

  An earlier version accepted NULL here (for bootstrap) and checked nothing
  else. That was a hole: whoever could call it could replace a Super
  Administrator's authenticator with one on their own phone, and a reset
  password plus a moved second factor is a complete account takeover. Moving
  your OWN authenticator is refused for the same reason — it is precisely what a
  stolen session would do.

  Why the security stamp is NOT rotated here
  ------------------------------------------
  Rotating it would evict the subject's live sessions, and a new authenticator
  does not change what those sessions may do: the subject already passed both
  factors to open them, and the enrolment grants no permission. Where eviction
  is the point — a suspected compromise — the portal's password reset and
  deactivation both rotate the stamp, and those are the tools for it.

  Transaction handling (decision DB-13)
  -------------------------------------
  The update and its audit row must not be half-applied. The outer transaction
  count is captured so a refusal rolls back to a savepoint rather than
  discarding a caller's transaction.

  Result codes
      0    Success (secret stored, pending activation)
      1001 InvalidRequest (empty secret)
      1003 Forbidden (target holds a permission the actor lacks)
      1070 NotFound (no such administrator, or the actor is missing or inactive)
      1074 SeparationOfDutiesViolation (enrolling your own)
      1014 UserInactive (the administrator is disabled)
      1064 MfaAlreadyEnrolled (and @ReplaceExisting was not set)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_EnrolMfa
    @AdministratorId            INT,
    @SecretProtected            VARBINARY(MAX),
    @ReplaceExisting            BIT = 0,
    @EnrolledByAdministratorId  INT,
    @CorrelationId              UNIQUEIDENTIFIER = NULL,
    @ResultCode                 INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @SecretProtected IS NULL OR DATALENGTH(@SecretProtected) = 0
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
            SAVE TRANSACTION AdminEnrolMfa;

        DECLARE @Check INT;

        EXEC admin.usp_Administrator_CheckAuthorityOver
            @ActingAdministratorId = @EnrolledByAdministratorId,
            @TargetAdministratorId = @AdministratorId,
            @ResultCode            = @Check OUTPUT;

        IF @Check <> 0
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminEnrolMfa;
            SET @ResultCode = @Check;
            RETURN;
        END;

        DECLARE @Status         TINYINT,
                @ExistingMfa    TINYINT,
                @PublicId       UNIQUEIDENTIFIER,
                @UserName       NVARCHAR(64);

        /* UPDLOCK so two concurrent enrolments cannot both read None and both
           write a different secret, leaving the operator holding a URI for a
           secret that is no longer stored. */
        SELECT @Status      = a.Status,
               @ExistingMfa = a.MfaStatus,
               @PublicId    = a.AdministratorPublicId,
               @UserName    = a.UserName
        FROM core.Administrator AS a WITH (UPDLOCK, HOLDLOCK)
        WHERE a.AdministratorId = @AdministratorId;

        IF @PublicId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminEnrolMfa;
            SET @ResultCode = 1070;      -- NotFound
            RETURN;
        END;

        IF @Status <> 1
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminEnrolMfa;
            SET @ResultCode = 1014;      -- UserInactive
            RETURN;
        END;

        IF @ExistingMfa <> 0 AND @ReplaceExisting = 0
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION AdminEnrolMfa;
            SET @ResultCode = 1064;      -- MfaAlreadyEnrolled
            RETURN;
        END;

        UPDATE core.Administrator
        SET MfaSecretProtected       = @SecretProtected,
            MfaStatus                = 1,       -- PendingActivation
            /* Cleared deliberately. The step counter belongs to the old secret;
               carrying it over could refuse the first valid code of the new one
               for up to a step, which during a reset looks exactly like the
               enrolment having failed. */
            MfaLastAcceptedTimeStep  = NULL,
            UpdatedUtc               = @NowUtc
        WHERE AdministratorId = @AdministratorId;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@PublicId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000) =
                (
                    SELECT @ExistingMfa AS previousMfaStatus,
                           CASE WHEN @ReplaceExisting = 1 THEN N'Replacement'
                                ELSE N'First enrolment' END AS kind
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
                );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Administrator.MfaEnrolled',
            @ActorType         = 2,
            @ActorId           = @EnrolledByAdministratorId,
            @SubjectType       = 'Administrator',
            @SubjectId         = @SubjectId,
            @Result            = 1,
            @ReasonCode        = 'PENDING_ACTIVATION',
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
            ROLLBACK TRANSACTION AdminEnrolMfa;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Administrator_EnrolMfa deployed.';
GO
