/*==============================================================================
  admin.usp_MfaCredential_Enrol
  Phase : 6
  Called by: the administration portal (permission Mfa.Enrol, and Mfa.Reset
             when replacing an existing enrolment).

  Purpose
  -------
  Begin authenticator enrolment for an employee (§13, §17).

  The secret never appears here in the clear
  ------------------------------------------
  @SecretProtected is ciphertext produced by the application's Data Protection
  services. The database stores and returns only that. The plaintext secret
  exists in the portal for exactly as long as it takes to show the QR code
  once, and it is never written to a log, an audit entry or an error message
  (§13, §32, §33).

  Enrolment does not activate
  ---------------------------
  The credential is created as PendingActivation. It becomes usable only after
  the employee proves a working code through usp_MfaCredential_Activate. That
  ordering matters: activating on creation would leave an employee unable to
  clock in if the secret was never successfully scanned, and the failure would
  only surface the next morning.

  Replacing an existing enrolment is deliberate
  ---------------------------------------------
  If the employee already has a pending or active authenticator, this refuses
  with 1064 unless @ReplaceExisting is set. Silently replacing an authenticator
  is how someone loses access to their account, or how an attacker with portal
  access quietly moves the second factor to a device they control. Replacement
  is therefore an explicit act, and the revocation of the old credential is
  audited alongside the new enrolment.

  OPEN-2 governs WHO performs enrolment and through what process. This
  procedure supports administrator-led enrolment; it does not assume that is
  the final answer.

  Transaction handling (decision DB-13)
  -------------------------------------
  Replacement is two writes — revoke the old credential, insert the new one —
  that must never be half-applied, or an employee would be left with no
  authenticator at all. The outer transaction count is captured so a refusal
  rolls back to a savepoint rather than discarding a caller's transaction.

  Result codes
      0    Success (credential created, pending activation)
      1070 NotFound (no such employee)
      1014 UserInactive
      1064 MfaAlreadyEnrolled
      1001 InvalidRequest
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_MfaCredential_Enrol
    @MobileUserId     INT,
    @SecretProtected  VARBINARY(MAX),
    @Algorithm        VARCHAR(8)  = 'SHA1',
    @Digits           TINYINT     = 6,
    @PeriodSeconds    SMALLINT    = 30,
    @ReplaceExisting  BIT         = 0,
    @AdministratorId  INT,
    @CorrelationId    UNIQUEIDENTIFIER = NULL,
    @MfaCredentialId  INT OUTPUT,
    @ResultCode       INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @MfaCredentialId = NULL;

    IF @SecretProtected IS NULL OR DATALENGTH(@SecretProtected) = 0
       OR @Algorithm NOT IN ('SHA1', 'SHA256', 'SHA512')
       OR @Digits NOT IN (6, 8)
       OR @PeriodSeconds < 15 OR @PeriodSeconds > 120
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
            SAVE TRANSACTION MfaEnrol;

        DECLARE @UserStatus     TINYINT,
                @UserPublicId   UNIQUEIDENTIFIER,
                @ExistingId     INT,
                @ExistingStatus TINYINT;

        SELECT @UserStatus   = u.Status,
               @UserPublicId = u.MobileUserPublicId
        FROM core.MobileUser AS u
        WHERE u.MobileUserId = @MobileUserId;

        IF @UserPublicId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION MfaEnrol;
            SET @ResultCode = 1070;      -- NotFound
            RETURN;
        END;

        IF @UserStatus <> 1
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION MfaEnrol;
            SET @ResultCode = 1014;      -- UserInactive
            RETURN;
        END;

        /* UX_MfaCredential_ActiveUser allows only one row per employee in
           status Pending or Active, so an existing one must be dealt with
           before inserting. */
        SELECT @ExistingId     = m.MfaCredentialId,
               @ExistingStatus = m.Status
        FROM core.MfaCredential AS m WITH (UPDLOCK, HOLDLOCK)
        WHERE m.MobileUserId = @MobileUserId
          AND m.Status IN (0, 1);

        IF @ExistingId IS NOT NULL AND @ReplaceExisting = 0
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION MfaEnrol;
            SET @ResultCode = 1064;      -- MfaAlreadyEnrolled
            RETURN;
        END;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@UserPublicId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000);

        IF @ExistingId IS NOT NULL
        BEGIN
            UPDATE core.MfaCredential
            SET Status                  = 2,          -- Revoked
                RevokedUtc              = @NowUtc,
                RevokedByAdministratorId = @AdministratorId
            WHERE MfaCredentialId = @ExistingId;

            SET @Details =
            (
                SELECT @ExistingStatus AS previousStatus,
                       N'Replaced by a new enrolment' AS reason
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
            );

            EXEC core.usp_AuditLog_Create
                @EventType         = 'Mfa.Revoked',
                @ActorType         = 2,
                @ActorId           = @AdministratorId,
                @SubjectType       = 'MobileUser',
                @SubjectId         = @SubjectId,
                @Result            = 1,
                @ReasonCode        = 'REPLACED',
                @SourceApplication = 'Attendance.Admin',
                @CorrelationId     = @CorrelationId,
                @Details           = @Details,
                @OccurredUtc       = @NowUtc,
                @ResultCode        = @AuditResult OUTPUT;
        END;

        INSERT INTO core.MfaCredential
            (MobileUserId, SecretProtected, Algorithm, Digits, PeriodSeconds,
             Status, EnrolledUtc, EnrolledByAdministratorId)
        VALUES
            (@MobileUserId, @SecretProtected, @Algorithm, @Digits, @PeriodSeconds,
             0, @NowUtc, @AdministratorId);

        SET @MfaCredentialId = SCOPE_IDENTITY();

        /* Algorithm parameters are audited; the secret is not. */
        SET @Details =
        (
            SELECT @Algorithm     AS algorithm,
                   @Digits        AS digits,
                   @PeriodSeconds AS periodSeconds
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Mfa.Enrolled',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @SubjectType       = 'MobileUser',
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
            ROLLBACK TRANSACTION MfaEnrol;
        END;

        IF ERROR_NUMBER() IN (2627, 2601)
        BEGIN
            SET @ResultCode = 1064;      -- MfaAlreadyEnrolled
            RETURN;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_MfaCredential_Enrol deployed.';
GO
