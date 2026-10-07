/*==============================================================================
  admin.usp_Device_Approve
  Phase : 6
  Called by: the administration portal (permission Device.Approve).

  Purpose
  -------
  Approve a pending device registration and make it the employee's active
  device (§18, decision DEC-04).

  THIS IS THE PROCEDURE THAT ENFORCES DEC-04
  ------------------------------------------
  An employee has at most one active device. Approving a replacement therefore
  REVOKES the previous one, in the same transaction. Both facts are audited,
  and the caller is told which device was revoked so the portal can show it.

  The order matters: the previous device must be revoked before the new one is
  activated, because UX_Device_ActiveUser (a filtered unique index) permits
  only one Active row per employee. Doing it the other way round would fail on
  the index rather than complete the swap.

  Why approval is a human decision
  --------------------------------
  Registration needs the employee's password and a valid authenticator code,
  but those can be phished. If approval were automatic, a phished credential
  plus one code would silently move an employee's attendance to an attacker's
  phone. Requiring an administrator puts a person in the loop, and revoking
  the old device makes the change immediately visible to the employee, whose
  app stops working (threat TH-08).

  Transaction handling (decision DB-13)
  -------------------------------------
  The device swap is two updates that must never be half-applied — an employee
  left with no active device, or with two. The outer transaction count is
  captured so that a rejection rolls back to a savepoint instead of discarding
  a caller's transaction.

  Result codes
      0    Success
      1070 NotFound
      1001 InvalidRequest (the device is not awaiting approval)
      1071 ConcurrencyConflict
      1024 ActiveDeviceAlreadyExists
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Device_Approve
    @DeviceId                     INT,
    @RowVersion                   BINARY(8),
    @AdministratorId              INT,
    @CorrelationId                UNIQUEIDENTIFIER = NULL,
    @DevicePublicId               UNIQUEIDENTIFIER OUTPUT,
    @RevokedPreviousDevicePublicId UNIQUEIDENTIFIER OUTPUT,
    @ResultCode                   INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();

    /* Both OUTPUT parameters are cleared first: @DevicePublicId is populated
       by a SELECT that matches no row when the device does not exist, and
       T-SQL would otherwise leave the caller's previous value in place. */
    SET @DevicePublicId                = NULL;
    SET @RevokedPreviousDevicePublicId = NULL;

    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION DeviceApprove;

        DECLARE @MobileUserId   INT,
                @Status         TINYINT,
                @CurrentVersion BINARY(8);

        SELECT @MobileUserId   = d.MobileUserId,
               @Status         = d.Status,
               @CurrentVersion = d.[RowVersion],
               @DevicePublicId = d.DevicePublicId
        FROM core.Device AS d WITH (UPDLOCK, HOLDLOCK)
        WHERE d.DeviceId = @DeviceId;

        IF @MobileUserId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION DeviceApprove;
            SET @ResultCode = 1070;      -- NotFound
            RETURN;
        END;

        IF @Status <> 0                  -- must be PendingApproval
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION DeviceApprove;
            SET @ResultCode = 1001;      -- InvalidRequest
            RETURN;
        END;

        IF @CurrentVersion <> @RowVersion
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION DeviceApprove;
            SET @ResultCode = 1071;      -- ConcurrencyConflict
            RETURN;
        END;

        ------------------------------------------------------------------
        -- 1. Revoke the employee's current active device, if any.
        ------------------------------------------------------------------
        DECLARE @Revoked TABLE (DevicePublicId UNIQUEIDENTIFIER, DeviceId INT);

        UPDATE core.Device
        SET Status                  = 2,             -- Revoked
            RevokedUtc              = @NowUtc,
            RevokedByAdministratorId = @AdministratorId,
            RevokedReason           = N'Replaced by a newly approved device'
        OUTPUT deleted.DevicePublicId, deleted.DeviceId INTO @Revoked (DevicePublicId, DeviceId)
        WHERE MobileUserId = @MobileUserId
          AND Status       = 1
          AND DeviceId    <> @DeviceId;

        SELECT @RevokedPreviousDevicePublicId = r.DevicePublicId FROM @Revoked AS r;

        ------------------------------------------------------------------
        -- 2. Activate the approved device.
        ------------------------------------------------------------------
        UPDATE core.Device
        SET Status                    = 1,           -- Active
            ApprovedUtc               = @NowUtc,
            ApprovedByAdministratorId = @AdministratorId
        WHERE DeviceId = @DeviceId;

        ------------------------------------------------------------------
        -- 3. Audit both facts.
        ------------------------------------------------------------------
        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@DevicePublicId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000);

        IF @RevokedPreviousDevicePublicId IS NOT NULL
        BEGIN
            DECLARE @RevokedSubjectId NVARCHAR(64) = CAST(@RevokedPreviousDevicePublicId AS NVARCHAR(64)),
                    @RevokedDetails   NVARCHAR(2000);

            SET @RevokedDetails =
            (
                SELECT N'Replaced by a newly approved device' AS reason,
                       @SubjectId                             AS replacedByDevice
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
            );

            EXEC core.usp_AuditLog_Create
                @EventType         = 'Device.Revoked',
                @ActorType         = 2,
                @ActorId           = @AdministratorId,
                @SubjectType       = 'Device',
                @SubjectId         = @RevokedSubjectId,
                @Result            = 1,
                @ReasonCode        = 'REPLACED',
                @SourceApplication = 'Attendance.Admin',
                @CorrelationId     = @CorrelationId,
                @DevicePublicId    = @RevokedPreviousDevicePublicId,
                @Details           = @RevokedDetails,
                @OccurredUtc       = @NowUtc,
                @ResultCode        = @AuditResult OUTPUT;
        END;

        SET @Details =
        (
            SELECT @MobileUserId                  AS mobileUserId,
                   @RevokedPreviousDevicePublicId AS revokedPreviousDevice
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Device.Approved',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @SubjectType       = 'Device',
            @SubjectId         = @SubjectId,
            @Result            = 1,
            @SourceApplication = 'Attendance.Admin',
            @CorrelationId     = @CorrelationId,
            @DevicePublicId    = @DevicePublicId,
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
            ROLLBACK TRANSACTION DeviceApprove;
        END;

        IF ERROR_NUMBER() IN (2627, 2601)
        BEGIN
            /* UX_Device_ActiveUser refused a second active device. */
            SET @ResultCode = 1024;      -- ActiveDeviceAlreadyExists
            RETURN;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Device_Approve deployed.';
GO
