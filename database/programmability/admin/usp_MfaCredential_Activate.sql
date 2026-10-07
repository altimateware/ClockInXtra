/*==============================================================================
  admin.usp_MfaCredential_Activate
  Phase : 6
  Called by: the administration portal, after the employee has demonstrated a
             working authenticator code.

  Purpose
  -------
  Turn a pending enrolment into an active one, proving the authenticator was
  set up correctly before it is required for clock-in.

  The caller has already verified the code
  ----------------------------------------
  TOTP verification happens in the application, which knows the algorithm and
  holds the decrypted secret only for that moment. It passes in the time step
  that matched, which is recorded immediately as LastAcceptedTimeStep. That
  closes the replay window from the very first code: the code used to prove
  enrolment cannot then be replayed to clock in (§13).

  The UPDATE carries its own condition
  ------------------------------------
  Only a credential in PendingActivation can be activated, and the WHERE
  clause enforces it, so two concurrent activations cannot both succeed.

  Transaction handling (decision DB-13)
  -------------------------------------
  Captures the outer transaction count and rolls back to a savepoint on a
  refusal, so calling this inside a caller's transaction cannot discard their
  work.

  Result codes
      0    Success
      1015 MfaNotEnrolled (nothing pending to activate)
      1001 InvalidRequest
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_MfaCredential_Activate
    @MobileUserId    INT,
    @TimeStep        BIGINT,
    @AdministratorId INT,
    @CorrelationId   UNIQUEIDENTIFIER = NULL,
    @ResultCode      INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @TimeStep IS NULL OR @TimeStep <= 0
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
            SAVE TRANSACTION MfaActivate;

        UPDATE core.MfaCredential
        SET Status               = 1,            -- Active
            ActivatedUtc         = @NowUtc,
            LastAcceptedTimeStep = @TimeStep
        WHERE MobileUserId = @MobileUserId
          AND Status       = 0;                  -- PendingActivation only

        IF @@ROWCOUNT = 0
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION MfaActivate;
            SET @ResultCode = 1015;      -- MfaNotEnrolled
            RETURN;
        END;

        DECLARE @UserPublicId UNIQUEIDENTIFIER =
                (SELECT MobileUserPublicId FROM core.MobileUser WHERE MobileUserId = @MobileUserId);

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@UserPublicId AS NVARCHAR(64));

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Mfa.Activated',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @SubjectType       = 'MobileUser',
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
            ROLLBACK TRANSACTION MfaActivate;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_MfaCredential_Activate deployed.';
GO
