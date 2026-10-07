/*==============================================================================
  admin.usp_OfficeLocation_Create
  Phase : 6
  Called by: the administration portal (permission OfficeLocation.Manage).

  Purpose
  -------
  Create an approved office location (Claude.md §19).

  Validation belongs in more than one place (§44). The portal validates for a
  helpful user experience; these constraints are the authoritative check,
  because the database must not be able to hold an impossible coordinate no
  matter which code path writes it:

      CK_OfficeLocation_Latitude   -90 .. +90
      CK_OfficeLocation_Longitude  -180 .. +180
      CK_OfficeLocation_Radius     greater than 0

  The radius defaults to 5 metres per the stated requirement but is
  per-location and configurable. Before relying on a 5 metre radius in the
  field, read conflict CON-01: consumer GPS accuracy is frequently worse than
  that, so honest employees can be refused. A per-office pilot measurement is
  recommended before go-live.

  Transaction handling (decision DB-13)
  -------------------------------------
  Captures the outer transaction count and rolls back to a savepoint on a
  refusal. Offices are often created in batches when a deployment is set up,
  and one duplicate name must not discard the whole batch.

  Result codes
      0    Success
      1001 InvalidRequest (coordinate or radius out of range)
      1072 DuplicateName
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_OfficeLocation_Create
    @Name                   NVARCHAR(120),
    @Description            NVARCHAR(400) = NULL,
    @Latitude               DECIMAL(9, 6),
    @Longitude              DECIMAL(9, 6),
    @AllowedRadiusMeters    DECIMAL(6, 2),
    @Status                 TINYINT,
    @AdministratorId        INT,
    @CorrelationId          UNIQUEIDENTIFIER = NULL,
    @OfficeLocationId       INT              OUTPUT,
    @OfficeLocationPublicId UNIQUEIDENTIFIER OUTPUT,
    @ResultCode             INT              OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @OfficeLocationId = NULL;
    SET @OfficeLocationPublicId = NULL;

    IF @Latitude  IS NULL OR @Latitude  < -90  OR @Latitude  > 90
       OR @Longitude IS NULL OR @Longitude < -180 OR @Longitude > 180
       OR @AllowedRadiusMeters IS NULL OR @AllowedRadiusMeters <= 0 OR @AllowedRadiusMeters > 10000
       OR @Status NOT IN (0, 1)
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
            SAVE TRANSACTION OfficeCreate;

        IF EXISTS (SELECT 1 FROM core.OfficeLocation WHERE Name = @Name)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION OfficeCreate;
            SET @ResultCode = 1072;  -- DuplicateName
            RETURN;
        END;

        SET @OfficeLocationPublicId = NEWID();

        INSERT INTO core.OfficeLocation
            (OfficeLocationPublicId, Name, Description, Latitude, Longitude,
             AllowedRadiusMeters, Status, CreatedUtc, CreatedByAdministratorId)
        VALUES
            (@OfficeLocationPublicId, @Name, @Description, @Latitude, @Longitude,
             @AllowedRadiusMeters, @Status, @NowUtc, @AdministratorId);

        SET @OfficeLocationId = SCOPE_IDENTITY();

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@OfficeLocationPublicId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000);

        /* Coordinates ARE recorded in this audit entry, deliberately: an
           office location is configuration, not an employee's whereabouts, and
           §19 requires changes to it to be auditable. Employee positions are
           never written to the audit trail.

           The JSON is built into a variable because an EXEC argument must be
           a constant or a variable, never an expression or subquery. */
        SET @Details =
        (
            SELECT @Name                AS name,
                   @Latitude            AS latitude,
                   @Longitude           AS longitude,
                   @AllowedRadiusMeters AS radiusMeters,
                   @Status              AS status
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'OfficeLocation.Created',
            @ActorType         = 2,                        -- Administrator
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
            ROLLBACK TRANSACTION OfficeCreate;
        END;

        IF ERROR_NUMBER() IN (2627, 2601)
        BEGIN
            SET @ResultCode = 1072;  -- DuplicateName
            RETURN;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_OfficeLocation_Create deployed.';
GO
