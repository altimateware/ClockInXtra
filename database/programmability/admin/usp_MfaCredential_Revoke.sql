/*==============================================================================
  admin.usp_MfaCredential_Revoke
  Phase : 6
  Called by: the administration portal (permission Mfa.Reset).

  Purpose
  -------
  Revoke an employee's authenticator enrolment (§13, §17): a lost phone, a
  suspected compromise, or an employee leaving.

  What revoking the authenticator does and does not stop
  ------------------------------------------------------
  Clock-in requires a valid authenticator code, so revoking the enrolment stops
  the employee clocking in from the next attempt onward — no cache, no delay,
  because usp_MobileUser_GetForAuthentication only returns an ACTIVE credential
  and clock-in refuses with MFA_NOT_ENROLLED without one.

  It does NOT stop clock-out. By the stated requirement, clock-out needs no
  password and no authenticator code (§14, ASM-04), so a device that is still
  registered can still close an open attendance record. That is the documented
  consequence of the requirement, recorded as residual risk RR-06. If an
  employee is leaving or their phone is compromised, the device must be revoked
  as well — usp_Device_Revoke — and the portal prompts for both rather than
  quietly assuming one implies the other.

  Revocation is terminal: a new authenticator requires a fresh enrolment and a
  fresh proof, because the old secret can no longer be trusted.

  Transaction handling (decision DB-13)
  -------------------------------------
  Captures the outer transaction count and rolls back to a savepoint on a
  refusal. Revoking an authenticator and revoking a device are frequently done
  together, so this procedure is expected to be called inside a caller's
  transaction.

  Result codes
      0    Success
      1015 MfaNotEnrolled (nothing active or pending to revoke)
      1001 InvalidRequest (no reason supplied)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_MfaCredential_Revoke
    @MobileUserId    INT,
    @Reason          NVARCHAR(256),
    @AdministratorId INT,
    @CorrelationId   UNIQUEIDENTIFIER = NULL,
    @ResultCode      INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Reason IS NULL OR LEN(LTRIM(RTRIM(@Reason))) = 0
    BEGIN
        /* §32 requires the reason for a security-relevant action to be
           recorded; a revocation with no stated reason is not auditable. */
        SET @ResultCode = 1001;          -- InvalidRequest
        RETURN;
    END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION MfaRevoke;

        DECLARE @MfaCredentialId INT,
                @PreviousStatus  TINYINT,
                @UserPublicId    UNIQUEIDENTIFIER;

        SELECT @UserPublicId = u.MobileUserPublicId
        FROM core.MobileUser AS u
        WHERE u.MobileUserId = @MobileUserId;

        SELECT @MfaCredentialId = m.MfaCredentialId,
               @PreviousStatus  = m.Status
        FROM core.MfaCredential AS m WITH (UPDLOCK, HOLDLOCK)
        WHERE m.MobileUserId = @MobileUserId
          AND m.Status IN (0, 1);        -- pending or active

        IF @MfaCredentialId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION MfaRevoke;
            SET @ResultCode = 1015;      -- MfaNotEnrolled
            RETURN;
        END;

        UPDATE core.MfaCredential
        SET Status                   = 2,            -- Revoked
            RevokedUtc               = @NowUtc,
            RevokedByAdministratorId = @AdministratorId
        WHERE MfaCredentialId = @MfaCredentialId;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@UserPublicId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000);

        /* The reason and the previous state are recorded. The secret never
           appears in an audit entry, encrypted or otherwise. */
        SET @Details =
        (
            SELECT @PreviousStatus AS previousStatus,
                   @Reason         AS reason
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Mfa.Revoked',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @SubjectType       = 'MobileUser',
            @SubjectId         = @SubjectId,
            @Result            = 1,
            @ReasonCode        = 'ADMIN_REVOCATION',
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
            ROLLBACK TRANSACTION MfaRevoke;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_MfaCredential_Revoke deployed.';
GO
