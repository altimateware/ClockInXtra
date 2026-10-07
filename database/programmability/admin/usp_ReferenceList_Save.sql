/*==============================================================================
  admin.usp_ReferenceList_Save
  Phase : 24
  Called by: the administration portal (permission MobileUser.Manage).

  Purpose
  -------
  Add an entry to a reference list (@EntryId NULL), or rename one (@EntryId and
  @RowVersion given). DEC-11.

  A rename reaches every employee
  -------------------------------
  core.MobileUser references the list by name with ON UPDATE CASCADE, so the
  UPDATE below renames the entry on every employee who holds it, inside this
  transaction. The audit entry records how many that was: a rename changes what
  reports show for those people, and an auditor should be able to see it.

  Result codes
      0    Success
      1001 InvalidRequest (unknown list, blank name, or longer than 120)
      1070 NotFound
      1071 ConcurrencyConflict
      1072 DuplicateName
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_ReferenceList_Save
    @List            VARCHAR(16),
    @EntryId         INT              = NULL,
    @Name            NVARCHAR(200),
    @RowVersion      BINARY(8)        = NULL,
    @AdministratorId INT,
    @CorrelationId   UNIQUEIDENTIFIER = NULL,
    @SavedEntryId    INT              OUTPUT,
    @ResultCode      INT              OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @SavedEntryId = NULL;
    SET @Name = LTRIM(RTRIM(@Name));

    IF @List NOT IN ('Department', 'JobTitle')
       OR @Name IS NULL OR LEN(@Name) = 0 OR LEN(@Name) > 120
       OR (@EntryId IS NOT NULL AND @RowVersion IS NULL)
    BEGIN
        SET @ResultCode = 1001;
        RETURN;
    END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;
    DECLARE @OldName NVARCHAR(120), @CurrentVersion BINARY(8), @Affected INT = 0;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION ReferenceListSave;

        /* A name already used by ANOTHER entry of the same list is refused,
           whatever its case: "Finance" and "finance" are one department. */
        IF (@List = 'Department' AND EXISTS (SELECT 1 FROM core.Department WITH (UPDLOCK, HOLDLOCK)
                                             WHERE UPPER(Name) = UPPER(@Name) AND (@EntryId IS NULL OR DepartmentId <> @EntryId)))
           OR (@List = 'JobTitle' AND EXISTS (SELECT 1 FROM core.JobTitle WITH (UPDLOCK, HOLDLOCK)
                                              WHERE UPPER(Name) = UPPER(@Name) AND (@EntryId IS NULL OR JobTitleId <> @EntryId)))
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ReferenceListSave;
            SET @ResultCode = 1072;
            RETURN;
        END;

        IF @EntryId IS NULL
        BEGIN
            IF @List = 'Department'
            BEGIN
                INSERT INTO core.Department (Name, CreatedByAdministratorId) VALUES (@Name, @AdministratorId);
                SET @SavedEntryId = SCOPE_IDENTITY();
            END
            ELSE
            BEGIN
                INSERT INTO core.JobTitle (Name, CreatedByAdministratorId) VALUES (@Name, @AdministratorId);
                SET @SavedEntryId = SCOPE_IDENTITY();
            END;
        END
        ELSE
        BEGIN
            IF @List = 'Department'
                SELECT @OldName = Name, @CurrentVersion = [RowVersion]
                FROM core.Department WITH (UPDLOCK) WHERE DepartmentId = @EntryId;
            ELSE
                SELECT @OldName = Name, @CurrentVersion = [RowVersion]
                FROM core.JobTitle WITH (UPDLOCK) WHERE JobTitleId = @EntryId;

            IF @OldName IS NULL
            BEGIN
                IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ReferenceListSave;
                SET @ResultCode = 1070;
                RETURN;
            END;

            IF @CurrentVersion <> @RowVersion
            BEGIN
                IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ReferenceListSave;
                SET @ResultCode = 1071;
                RETURN;
            END;

            IF @List = 'Department'
            BEGIN
                SELECT @Affected = COUNT(*) FROM core.MobileUser WHERE Department = @OldName;
                UPDATE core.Department SET Name = @Name, UpdatedUtc = @NowUtc WHERE DepartmentId = @EntryId;
            END
            ELSE
            BEGIN
                SELECT @Affected = COUNT(*) FROM core.MobileUser WHERE JobTitle = @OldName;
                UPDATE core.JobTitle SET Name = @Name, UpdatedUtc = @NowUtc WHERE JobTitleId = @EntryId;
            END;

            SET @SavedEntryId = @EntryId;
        END;

        DECLARE @AuditResult INT,
                @EventType   VARCHAR(64) = CONCAT(@List, CASE WHEN @EntryId IS NULL THEN '.Created' ELSE '.Renamed' END),
                @SubjectId   NVARCHAR(64) = CAST(@SavedEntryId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000) =
                (
                    SELECT @OldName  AS previousName,
                           @Name     AS name,
                           @Affected AS employeesRenamed
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
                );

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
            ROLLBACK TRANSACTION ReferenceListSave;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_ReferenceList_Save deployed.';
GO
