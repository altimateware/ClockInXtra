/*==============================================================================
  admin.usp_OfficeLocation_Update
  Phase : 6
  Called by: the administration portal (permission OfficeLocation.Manage).

  Purpose
  -------
  Edit an office location's name, description, coordinates or radius (§19).

  Concurrency
  -----------
  The caller passes the @RowVersion it loaded. If another administrator has
  saved in the meantime, the update matches no row and 1071 is returned rather
  than silently overwriting their change.

  Audit
  -----
  Both the previous and the new values are recorded. Moving an office or
  widening its radius changes who can clock in, so an auditor needs to see
  what it was before, not only what it became.

  Transaction handling (decision DB-13)
  -------------------------------------
  Captures the outer transaction count and rolls back to a savepoint on a
  refusal, so a rejected edit never discards a caller's transaction.

  Result codes
      0    Success
      1001 InvalidRequest
      1070 NotFound
      1071 ConcurrencyConflict
      1072 DuplicateName
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_OfficeLocation_Update
    @OfficeLocationId    INT,
    @Name                NVARCHAR(120),
    @Description         NVARCHAR(400) = NULL,
    @Latitude            DECIMAL(9, 6),
    @Longitude           DECIMAL(9, 6),
    @AllowedRadiusMeters DECIMAL(6, 2),
    @RowVersion          BINARY(8),
    @AdministratorId     INT,
    @CorrelationId       UNIQUEIDENTIFIER = NULL,
    @ResultCode          INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Latitude  IS NULL OR @Latitude  < -90  OR @Latitude  > 90
       OR @Longitude IS NULL OR @Longitude < -180 OR @Longitude > 180
       OR @AllowedRadiusMeters IS NULL OR @AllowedRadiusMeters <= 0 OR @AllowedRadiusMeters > 10000
       OR LEN(LTRIM(RTRIM(@Name))) = 0
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
            SAVE TRANSACTION OfficeUpdate;

        DECLARE @OldName        NVARCHAR(120),
                @OldLatitude    DECIMAL(9, 6),
                @OldLongitude   DECIMAL(9, 6),
                @OldRadius      DECIMAL(6, 2),
                @PublicId       UNIQUEIDENTIFIER,
                @CurrentVersion BINARY(8);

        SELECT @OldName        = o.Name,
               @OldLatitude    = o.Latitude,
               @OldLongitude   = o.Longitude,
               @OldRadius      = o.AllowedRadiusMeters,
               @PublicId       = o.OfficeLocationPublicId,
               @CurrentVersion = o.[RowVersion]
        FROM core.OfficeLocation AS o WITH (UPDLOCK)
        WHERE o.OfficeLocationId = @OfficeLocationId;

        IF @PublicId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION OfficeUpdate;
            SET @ResultCode = 1070;  -- NotFound
            RETURN;
        END;

        IF @CurrentVersion <> @RowVersion
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION OfficeUpdate;
            SET @ResultCode = 1071;  -- ConcurrencyConflict
            RETURN;
        END;

        IF EXISTS (SELECT 1 FROM core.OfficeLocation
                   WHERE Name = @Name AND OfficeLocationId <> @OfficeLocationId)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION OfficeUpdate;
            SET @ResultCode = 1072;  -- DuplicateName
            RETURN;
        END;

        UPDATE core.OfficeLocation
        SET Name                     = @Name,
            Description              = @Description,
            Latitude                 = @Latitude,
            Longitude                = @Longitude,
            AllowedRadiusMeters      = @AllowedRadiusMeters,
            UpdatedUtc               = @NowUtc,
            UpdatedByAdministratorId = @AdministratorId
        WHERE OfficeLocationId = @OfficeLocationId;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@PublicId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000);

        /* Built into a variable: an EXEC argument must be a constant or a
           variable, never an expression or subquery. */
        SET @Details =
        (
            SELECT @OldName             AS previousName,
                   @OldLatitude         AS previousLatitude,
                   @OldLongitude        AS previousLongitude,
                   @OldRadius           AS previousRadiusMeters,
                   @Name                AS newName,
                   @Latitude            AS newLatitude,
                   @Longitude           AS newLongitude,
                   @AllowedRadiusMeters AS newRadiusMeters
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'OfficeLocation.Updated',
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
            ROLLBACK TRANSACTION OfficeUpdate;
        END;

        IF ERROR_NUMBER() IN (2627, 2601)
        BEGIN
            SET @ResultCode = 1072;
            RETURN;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_OfficeLocation_Update deployed.';
GO
