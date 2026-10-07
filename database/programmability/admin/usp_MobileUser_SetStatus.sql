/*==============================================================================
  admin.usp_MobileUser_SetStatus
  Phase : 6
  Called by: the administration portal (permission MobileUser.Manage).

  Purpose
  -------
  Activate, deactivate or suspend an employee (§17).

  What deactivation actually stops, and what it does not
  ------------------------------------------------------
  Deactivating an employee stops attendance immediately: every signed request
  resolves the employee through usp_Device_GetForSignatureVerification, which
  returns 1014 for a non-active user, and clock-in re-checks the status inside
  its transaction. Nothing is cached, so there is no window.

  It does NOT revoke the device. That is a separate, deliberate act, because
  the two answer different questions: "may this person clock in?" and "is this
  handset still trusted?". A leaver normally needs both, and the portal
  prompts for the device revocation rather than performing it silently — a
  silent revocation would destroy the audit distinction between an employee
  being deactivated and their device being withdrawn.

  @RevokeDevices lets the caller ask for both in one transaction when that is
  what they mean, and each revocation is audited separately.

  An open attendance record is left untouched: rewriting or closing it here
  would fabricate a clock-out that never happened. It surfaces in the Missing
  Clock-Outs report for a human to resolve.

  Transaction handling (decision DB-13)
  -------------------------------------
  The status change and any device revocations must commit together. The outer
  transaction count is captured so a refusal rolls back to a savepoint rather
  than discarding a caller's transaction.

  Result codes
      0    Success
      1001 InvalidRequest
      1070 NotFound
      1071 ConcurrencyConflict
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_MobileUser_SetStatus
    @MobileUserId    INT,
    @Status          TINYINT,          -- 0 Inactive, 1 Active, 2 Suspended
    @RowVersion      BINARY(8),
    @RevokeDevices   BIT = 0,
    @Reason          NVARCHAR(256) = NULL,
    @AdministratorId INT,
    @CorrelationId   UNIQUEIDENTIFIER = NULL,
    @DevicesRevoked  INT OUTPUT,
    @ResultCode      INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @DevicesRevoked = 0;

    IF @Status NOT IN (0, 1, 2)
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
            SAVE TRANSACTION UserSetStatus;

        DECLARE @OldStatus      TINYINT,
                @PublicId       UNIQUEIDENTIFIER,
                @UserId         NVARCHAR(64),
                @CurrentVersion BINARY(8);

        SELECT @OldStatus      = u.Status,
               @PublicId       = u.MobileUserPublicId,
               @UserId         = u.UserId,
               @CurrentVersion = u.[RowVersion]
        FROM core.MobileUser AS u WITH (UPDLOCK)
        WHERE u.MobileUserId = @MobileUserId;

        IF @PublicId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION UserSetStatus;
            SET @ResultCode = 1070;      -- NotFound
            RETURN;
        END;

        IF @CurrentVersion <> @RowVersion
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION UserSetStatus;
            SET @ResultCode = 1071;      -- ConcurrencyConflict
            RETURN;
        END;

        UPDATE core.MobileUser
        SET Status                   = @Status,
            UpdatedUtc               = @NowUtc,
            UpdatedByAdministratorId = @AdministratorId
        WHERE MobileUserId = @MobileUserId;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@PublicId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000);

        IF @RevokeDevices = 1
        BEGIN
            DECLARE @Revoked TABLE (DevicePublicId UNIQUEIDENTIFIER);

            UPDATE core.Device
            SET Status                   = 2,
                RevokedUtc               = @NowUtc,
                RevokedByAdministratorId = @AdministratorId,
                RevokedReason            = COALESCE(@Reason, N'Employee status changed')
            OUTPUT deleted.DevicePublicId INTO @Revoked (DevicePublicId)
            WHERE MobileUserId = @MobileUserId
              AND Status IN (0, 1);

            SET @DevicesRevoked = @@ROWCOUNT;

            DECLARE @RevokedPublicId UNIQUEIDENTIFIER,
                    @RevokedSubject  NVARCHAR(64);

            DECLARE revoked_cursor CURSOR LOCAL FAST_FORWARD FOR
                SELECT DevicePublicId FROM @Revoked;

            OPEN revoked_cursor;
            FETCH NEXT FROM revoked_cursor INTO @RevokedPublicId;

            WHILE @@FETCH_STATUS = 0
            BEGIN
                SET @RevokedSubject = CAST(@RevokedPublicId AS NVARCHAR(64));

                EXEC core.usp_AuditLog_Create
                    @EventType         = 'Device.Revoked',
                    @ActorType         = 2,
                    @ActorId           = @AdministratorId,
                    @SubjectType       = 'Device',
                    @SubjectId         = @RevokedSubject,
                    @Result            = 1,
                    @ReasonCode        = 'USER_STATUS_CHANGED',
                    @SourceApplication = 'Attendance.Admin',
                    @CorrelationId     = @CorrelationId,
                    @DevicePublicId    = @RevokedPublicId,
                    @Details           = NULL,
                    @OccurredUtc       = @NowUtc,
                    @ResultCode        = @AuditResult OUTPUT;

                FETCH NEXT FROM revoked_cursor INTO @RevokedPublicId;
            END;

            CLOSE revoked_cursor;
            DEALLOCATE revoked_cursor;
        END;

        SET @Details =
        (
            SELECT @UserId         AS userId,
                   @OldStatus      AS previousStatus,
                   @Status         AS newStatus,
                   @Reason         AS reason,
                   @DevicesRevoked AS devicesRevoked
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'MobileUser.StatusChanged',
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
        /* The cursor is local to this procedure, so it must be closed here
           whatever happens — a rollback does not deallocate it. */
        IF CURSOR_STATUS('local', 'revoked_cursor') >= 0
        BEGIN
            CLOSE revoked_cursor;
            DEALLOCATE revoked_cursor;
        END;

        IF XACT_STATE() = -1 AND @OuterTranCount > 0
            THROW;

        IF @OuterTranCount = 0
        BEGIN
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        END
        ELSE IF XACT_STATE() = 1
        BEGIN
            ROLLBACK TRANSACTION UserSetStatus;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_MobileUser_SetStatus deployed.';
GO
