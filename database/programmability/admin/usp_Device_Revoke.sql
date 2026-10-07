/*==============================================================================
  admin.usp_Device_Revoke
  Phase : 6
  Called by: the administration portal (permission Device.Revoke).

  Purpose
  -------
  Revoke a device: a lost or stolen phone, an employee leaving, or a device
  registration that should not have been made (§18).

  When the revocation takes effect
  --------------------------------
  Immediately, on the device's next request. Every signed request looks the
  device up through usp_Device_GetForSignatureVerification, which reports the
  status, and the attendance procedures re-check it inside their transaction.
  Nothing about a device's authorisation is cached, precisely so that this
  operation is not delayed by a cache lifetime (threat TH-21).

  Revocation is terminal. A replacement device requires a new registration and
  a new approval, because the private key of the old device cannot be
  recovered or re-bound — it never left the device's secure hardware.

  Attendance history is untouched. Records already made by this device remain,
  and the device reference in core.AttendanceEvent keeps them explainable.

  Transaction handling (decision DB-13)
  -------------------------------------
  Captures the outer transaction count and rolls back to a savepoint on a
  rejection, so a refusal never discards a caller's transaction.

  Result codes
      0    Success
      1070 NotFound
      1001 InvalidRequest (already revoked, or no reason given)
      1071 ConcurrencyConflict
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Device_Revoke
    @DeviceId        INT,
    @RowVersion      BINARY(8),
    @Reason          NVARCHAR(256),
    @AdministratorId INT,
    @CorrelationId   UNIQUEIDENTIFIER = NULL,
    @DevicePublicId  UNIQUEIDENTIFIER OUTPUT,
    @ResultCode      INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Reason IS NULL OR LEN(LTRIM(RTRIM(@Reason))) = 0
    BEGIN
        /* A revocation without a stated reason is not auditable, and §32
           requires the reason to be recorded. */
        SET @ResultCode = 1001;          -- InvalidRequest
        RETURN;
    END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();

    /* Cleared before use: the SELECT below matches no row for an unknown
       device, which in T-SQL leaves the variable as the caller passed it. */
    SET @DevicePublicId = NULL;

    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION DeviceRevoke;

        DECLARE @MobileUserId   INT,
                @Status         TINYINT,
                @CurrentVersion BINARY(8);

        SELECT @MobileUserId   = d.MobileUserId,
               @Status         = d.Status,
               @CurrentVersion = d.[RowVersion],
               @DevicePublicId = d.DevicePublicId
        FROM core.Device AS d WITH (UPDLOCK)
        WHERE d.DeviceId = @DeviceId;

        IF @MobileUserId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION DeviceRevoke;
            SET @ResultCode = 1070;      -- NotFound
            RETURN;
        END;

        IF @Status = 2
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION DeviceRevoke;
            SET @ResultCode = 1001;      -- InvalidRequest: already revoked
            RETURN;
        END;

        IF @CurrentVersion <> @RowVersion
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION DeviceRevoke;
            SET @ResultCode = 1071;      -- ConcurrencyConflict
            RETURN;
        END;

        UPDATE core.Device
        SET Status                   = 2,            -- Revoked
            RevokedUtc               = @NowUtc,
            RevokedByAdministratorId = @AdministratorId,
            RevokedReason            = @Reason
        WHERE DeviceId = @DeviceId;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@DevicePublicId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000);

        SET @Details =
        (
            SELECT @MobileUserId AS mobileUserId,
                   @Status       AS previousStatus,
                   @Reason       AS reason
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Device.Revoked',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @SubjectType       = 'Device',
            @SubjectId         = @SubjectId,
            @Result            = 1,
            @ReasonCode        = 'ADMIN_REVOCATION',
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
            ROLLBACK TRANSACTION DeviceRevoke;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Device_Revoke deployed.';
GO
