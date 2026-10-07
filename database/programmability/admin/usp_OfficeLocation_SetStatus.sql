/*==============================================================================
  admin.usp_OfficeLocation_SetStatus
  Phase : 6
  Called by: the administration portal (permission OfficeLocation.Manage).

  Purpose
  -------
  Enable or disable an office location (§19).

  Why disabling is a first-class, audited operation
  -------------------------------------------------
  Disabling an office immediately stops every employee from clocking in there:
  usp_Attendance_ClockIn re-checks the office inside its transaction, so even
  a request that passed location validation a moment earlier is refused with
  1043. That makes this a control with real operational consequences, which is
  why it is recorded with its previous value.

  Locations are never deleted. Attendance records reference the office they
  were recorded at, and that history has to stay intact and explainable.

  Transaction handling (decision DB-13)
  -------------------------------------
  Captures the outer transaction count and rolls back to a savepoint on a
  rejection, so a refusal never discards a caller's transaction.

  Result codes
      0    Success
      1001 InvalidRequest
      1070 NotFound
      1071 ConcurrencyConflict
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_OfficeLocation_SetStatus
    @OfficeLocationId INT,
    @Status           TINYINT,          -- 0 Disabled, 1 Active
    @RowVersion       BINARY(8),
    @AdministratorId  INT,
    @Reason           NVARCHAR(256)    = NULL,
    @CorrelationId    UNIQUEIDENTIFIER = NULL,
    @ResultCode       INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Status NOT IN (0, 1)
    BEGIN
        SET @ResultCode = 1001;      -- InvalidRequest
        RETURN;
    END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION OfficeSetStatus;

        DECLARE @OldStatus      TINYINT,
                @PublicId       UNIQUEIDENTIFIER,
                @Name           NVARCHAR(120),
                @CurrentVersion BINARY(8);

        SELECT @OldStatus      = o.Status,
               @PublicId       = o.OfficeLocationPublicId,
               @Name           = o.Name,
               @CurrentVersion = o.[RowVersion]
        FROM core.OfficeLocation AS o WITH (UPDLOCK)
        WHERE o.OfficeLocationId = @OfficeLocationId;

        IF @PublicId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION OfficeSetStatus;
            SET @ResultCode = 1070;  -- NotFound
            RETURN;
        END;

        IF @CurrentVersion <> @RowVersion
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION OfficeSetStatus;
            SET @ResultCode = 1071;  -- ConcurrencyConflict
            RETURN;
        END;

        UPDATE core.OfficeLocation
        SET Status                   = @Status,
            UpdatedUtc               = @NowUtc,
            UpdatedByAdministratorId = @AdministratorId
        WHERE OfficeLocationId = @OfficeLocationId;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@PublicId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000);

        SET @Details =
        (
            SELECT @Name      AS name,
                   @OldStatus AS previousStatus,
                   @Status    AS newStatus,
                   @Reason    AS reason
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'OfficeLocation.StatusChanged',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @SubjectType       = 'OfficeLocation',
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
            ROLLBACK TRANSACTION OfficeSetStatus;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_OfficeLocation_SetStatus deployed.';
GO
