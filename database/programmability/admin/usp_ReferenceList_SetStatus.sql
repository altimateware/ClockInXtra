/*==============================================================================
  admin.usp_ReferenceList_SetStatus
  Phase : 24
  Called by: the administration portal (permission MobileUser.Manage).

  Purpose
  -------
  Activate or deactivate a reference list entry (DEC-11). A deactivated entry
  is no longer offered for new employees or changes, but the employees who
  already hold it keep it: deactivating "Logistics" must not quietly rewrite
  anyone's record. Entries are never deleted while in use — the foreign key
  from core.MobileUser prevents it, and history reads better for it.

  Result codes
      0    Success
      1001 InvalidRequest (unknown list)
      1070 NotFound
      1071 ConcurrencyConflict
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_ReferenceList_SetStatus
    @List            VARCHAR(16),
    @EntryId         INT,
    @IsActive        BIT,
    @RowVersion      BINARY(8),
    @AdministratorId INT,
    @CorrelationId   UNIQUEIDENTIFIER = NULL,
    @ResultCode      INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @List NOT IN ('Department', 'JobTitle') OR @IsActive IS NULL OR @RowVersion IS NULL
    BEGIN
        SET @ResultCode = 1001;
        RETURN;
    END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;
    DECLARE @Name NVARCHAR(120), @CurrentVersion BINARY(8);

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION ReferenceListStatus;

        IF @List = 'Department'
            SELECT @Name = Name, @CurrentVersion = [RowVersion]
            FROM core.Department WITH (UPDLOCK) WHERE DepartmentId = @EntryId;
        ELSE
            SELECT @Name = Name, @CurrentVersion = [RowVersion]
            FROM core.JobTitle WITH (UPDLOCK) WHERE JobTitleId = @EntryId;

        IF @Name IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ReferenceListStatus;
            SET @ResultCode = 1070;
            RETURN;
        END;

        IF @CurrentVersion <> @RowVersion
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ReferenceListStatus;
            SET @ResultCode = 1071;
            RETURN;
        END;

        IF @List = 'Department'
            UPDATE core.Department SET IsActive = @IsActive, UpdatedUtc = @NowUtc WHERE DepartmentId = @EntryId;
        ELSE
            UPDATE core.JobTitle SET IsActive = @IsActive, UpdatedUtc = @NowUtc WHERE JobTitleId = @EntryId;

        DECLARE @AuditResult INT,
                @EventType   VARCHAR(64) = CONCAT(@List, CASE WHEN @IsActive = 1 THEN '.Activated' ELSE '.Deactivated' END),
                @SubjectId   NVARCHAR(64) = CAST(@EntryId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000) = (SELECT @Name AS name FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

        EXEC core.usp_AuditLog_Create
            @EventType         = @EventType,
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @SubjectType       = @List,
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
            ROLLBACK TRANSACTION ReferenceListStatus;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_ReferenceList_SetStatus deployed.';
GO
