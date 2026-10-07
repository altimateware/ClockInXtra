/*==============================================================================
  ClockInXtra — Schema: reference lists (departments and job titles)
  File  : database/schema/08_tables_reference_lists.sql
  Phase : 24

  DEC-11: every employee field is required, and an employee's department and
  job title are chosen from lists the organisation maintains, not typed.

  Why lists, not free text
  ------------------------
  Typed text drifts: "Finance", "finance", "Finance Dept" become three
  departments in every report and filter. A list makes the value a choice.

  Why the employee row keeps the NAME, with a foreign key to it
  ------------------------------------------------------------
  core.MobileUser.Department and .JobTitle stay as they are — every report,
  filter and search already reads them — and gain a foreign key to the list's
  unique Name, with ON UPDATE CASCADE. So:
    * an employee can only hold a name that is on the list;
    * renaming an entry renames it on every employee in the same statement,
      with nothing to re-point by hand;
    * an entry in use cannot be deleted. Entries are deactivated instead,
      which removes them from the choice for new employees while the people
      who already hold them keep them.

  Upgrade path
  ------------
  On an existing database the distinct values already held by employees are
  copied into the lists before the foreign keys are added, so the keys never
  find an orphan. Re-runnable.
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'core.Department', N'U') IS NULL
BEGIN
    CREATE TABLE core.Department
    (
        DepartmentId             INT           IDENTITY(1,1) NOT NULL,
        Name                     NVARCHAR(120) NOT NULL,
        IsActive                 BIT           NOT NULL
            CONSTRAINT DF_Department_IsActive DEFAULT (1),
        CreatedUtc               DATETIME2(3)  NOT NULL
            CONSTRAINT DF_Department_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        CreatedByAdministratorId INT           NULL,
        UpdatedUtc               DATETIME2(3)  NULL,
        [RowVersion]             ROWVERSION    NOT NULL,
        CONSTRAINT PK_core_Department PRIMARY KEY CLUSTERED (DepartmentId),
        CONSTRAINT UQ_Department_Name UNIQUE (Name),
        CONSTRAINT CK_Department_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),
        CONSTRAINT FK_Department_Administrator_CreatedBy
            FOREIGN KEY (CreatedByAdministratorId) REFERENCES core.Administrator (AdministratorId)
    );
END
GO

IF OBJECT_ID(N'core.JobTitle', N'U') IS NULL
BEGIN
    CREATE TABLE core.JobTitle
    (
        JobTitleId               INT           IDENTITY(1,1) NOT NULL,
        Name                     NVARCHAR(120) NOT NULL,
        IsActive                 BIT           NOT NULL
            CONSTRAINT DF_JobTitle_IsActive DEFAULT (1),
        CreatedUtc               DATETIME2(3)  NOT NULL
            CONSTRAINT DF_JobTitle_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        CreatedByAdministratorId INT           NULL,
        UpdatedUtc               DATETIME2(3)  NULL,
        [RowVersion]             ROWVERSION    NOT NULL,
        CONSTRAINT PK_core_JobTitle PRIMARY KEY CLUSTERED (JobTitleId),
        CONSTRAINT UQ_JobTitle_Name UNIQUE (Name),
        CONSTRAINT CK_JobTitle_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),
        CONSTRAINT FK_JobTitle_Administrator_CreatedBy
            FOREIGN KEY (CreatedByAdministratorId) REFERENCES core.Administrator (AdministratorId)
    );
END
GO

/* Values already held by employees join the lists, so nothing is orphaned. */
INSERT INTO core.Department (Name)
SELECT DISTINCT m.Department
FROM core.MobileUser AS m
WHERE m.Department IS NOT NULL
  AND LEN(LTRIM(RTRIM(m.Department))) > 0
  AND NOT EXISTS (SELECT 1 FROM core.Department AS d WHERE d.Name = m.Department);
GO

INSERT INTO core.JobTitle (Name)
SELECT DISTINCT m.JobTitle
FROM core.MobileUser AS m
WHERE m.JobTitle IS NOT NULL
  AND LEN(LTRIM(RTRIM(m.JobTitle))) > 0
  AND NOT EXISTS (SELECT 1 FROM core.JobTitle AS j WHERE j.Name = m.JobTitle);
GO

/* A blank string held by an employee is not a department: it becomes NULL
   ("not recorded") rather than a list entry nobody could read. */
UPDATE core.MobileUser SET Department = NULL WHERE Department IS NOT NULL AND LEN(LTRIM(RTRIM(Department))) = 0;
UPDATE core.MobileUser SET JobTitle   = NULL WHERE JobTitle   IS NOT NULL AND LEN(LTRIM(RTRIM(JobTitle)))   = 0;
GO

IF OBJECT_ID(N'core.FK_MobileUser_Department_Name', N'F') IS NULL
    ALTER TABLE core.MobileUser ADD CONSTRAINT FK_MobileUser_Department_Name
        FOREIGN KEY (Department) REFERENCES core.Department (Name) ON UPDATE CASCADE;
GO

IF OBJECT_ID(N'core.FK_MobileUser_JobTitle_Name', N'F') IS NULL
    ALTER TABLE core.MobileUser ADD CONSTRAINT FK_MobileUser_JobTitle_Name
        FOREIGN KEY (JobTitle) REFERENCES core.JobTitle (Name) ON UPDATE CASCADE;
GO

PRINT 'Reference lists ready: core.Department, core.JobTitle.';
GO
