/*==============================================================================
  ClockInXtra — Settings and deployment metadata
  File  : database/schema/06_tables_settings.sql
  Phase : 5

  Contents
      core.ApplicationSetting  configurable business and security rules
      core.SchemaVersion       record of applied deployment scripts

  The central rule for this table
  -------------------------------
  SettingValue is NULLABLE on purpose. A NULL means "the business has not
  decided yet", and the attendance procedures refuse to operate rather than
  invent a default (Claude.md §15 and §68). RequiresBusinessConfirmation stays
  1 until a named administrator confirms the value, which is what the portal
  shows as an outstanding decision.
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'core.ApplicationSetting', N'U') IS NULL
BEGIN
    CREATE TABLE core.ApplicationSetting
    (
        ApplicationSettingId INT           IDENTITY(1,1) NOT NULL,
        SettingKey           VARCHAR(100)  NOT NULL,
        SettingValue         NVARCHAR(400) NULL,          -- NULL = not yet decided
        DataType             VARCHAR(20)   NOT NULL,      -- string|int|decimal|bool|time|date|timezone|enum
        Category             NVARCHAR(64)  NOT NULL,      -- Attendance|Security|Location|Retention|Mobile
        Description          NVARCHAR(400) NOT NULL,
        AllowedValues        NVARCHAR(400) NULL,          -- for DataType 'enum', a comma separated list
        RequiresBusinessConfirmation BIT   NOT NULL
            CONSTRAINT DF_ApplicationSetting_RequiresBusinessConfirmation DEFAULT (1),
        IsMobileVisible      BIT           NOT NULL       -- may this value be sent to the mobile app?
            CONSTRAINT DF_ApplicationSetting_IsMobileVisible DEFAULT (0),
        ConfirmedByAdministratorId INT     NULL,
        ConfirmedUtc         DATETIME2(3)  NULL,
        UpdatedByAdministratorId   INT     NULL,
        UpdatedUtc           DATETIME2(3)  NULL,
        CreatedUtc           DATETIME2(3)  NOT NULL
            CONSTRAINT DF_ApplicationSetting_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        [RowVersion]         ROWVERSION    NOT NULL,
        CONSTRAINT PK_core_ApplicationSetting PRIMARY KEY CLUSTERED (ApplicationSettingId),
        CONSTRAINT UQ_ApplicationSetting_SettingKey UNIQUE (SettingKey),
        CONSTRAINT FK_ApplicationSetting_Administrator_ConfirmedBy
            FOREIGN KEY (ConfirmedByAdministratorId) REFERENCES core.Administrator (AdministratorId),
        CONSTRAINT FK_ApplicationSetting_Administrator_UpdatedBy
            FOREIGN KEY (UpdatedByAdministratorId) REFERENCES core.Administrator (AdministratorId),
        CONSTRAINT CK_ApplicationSetting_DataType
            CHECK (DataType IN ('string', 'int', 'decimal', 'bool', 'time', 'date', 'timezone', 'enum')),
        CONSTRAINT CK_ApplicationSetting_Confirmation
            CHECK ((RequiresBusinessConfirmation = 1 AND ConfirmedUtc IS NULL)
                OR (RequiresBusinessConfirmation = 0 AND ConfirmedUtc IS NOT NULL))
    );

    CREATE NONCLUSTERED INDEX IX_ApplicationSetting_Category
        ON core.ApplicationSetting (Category)
        INCLUDE (SettingKey, SettingValue, RequiresBusinessConfirmation);
END
GO

/*------------------------------------------------------------------------------
  Input metadata (Phase 24).

  What a valid value looks like, held beside the value itself so there is one
  definition: admin.usp_ApplicationSetting_Set enforces it, and the portal
  builds its input from it (a number box with these bounds, a time picker, a
  list). Before this, only two settings had range checks, buried in the
  procedure, and the portal offered a free text box for everything.

    MinValue / MaxValue  Inclusive bounds for 'int' and 'decimal' settings.
                         Technical sanity limits, not business decisions: they
                         refuse values that would break the system (a lockout
                         threshold of 0, a signature window of an hour), not
                         values a business might reasonably choose.
    Unit                 Shown beside the input: minutes, days, metres...
    BlankMeaning         When set, leaving the value blank is itself a valid,
                         confirmable decision, and this says what it means
                         ("Keep indefinitely"). When NULL, blank means
                         undecided and cannot be confirmed.

  Added with ALTER so an existing database is upgraded in place.
------------------------------------------------------------------------------*/
IF COL_LENGTH(N'core.ApplicationSetting', N'MinValue') IS NULL
    ALTER TABLE core.ApplicationSetting ADD MinValue DECIMAL(18, 4) NULL;
GO
IF COL_LENGTH(N'core.ApplicationSetting', N'MaxValue') IS NULL
    ALTER TABLE core.ApplicationSetting ADD MaxValue DECIMAL(18, 4) NULL;
GO
IF COL_LENGTH(N'core.ApplicationSetting', N'Unit') IS NULL
    ALTER TABLE core.ApplicationSetting ADD Unit NVARCHAR(24) NULL;
GO
IF COL_LENGTH(N'core.ApplicationSetting', N'BlankMeaning') IS NULL
    ALTER TABLE core.ApplicationSetting ADD BlankMeaning NVARCHAR(80) NULL;
GO

IF OBJECT_ID(N'core.CK_ApplicationSetting_Range', N'C') IS NULL
    ALTER TABLE core.ApplicationSetting ADD CONSTRAINT CK_ApplicationSetting_Range
        CHECK (MinValue IS NULL OR MaxValue IS NULL OR MinValue <= MaxValue);
GO

/*------------------------------------------------------------------------------
  core.SchemaVersion

  Every deployment script records itself here with a checksum, so an upgrade
  can tell what has been applied and detect a script that was edited after
  being deployed.
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.SchemaVersion', N'U') IS NULL
BEGIN
    CREATE TABLE core.SchemaVersion
    (
        SchemaVersionId INT           IDENTITY(1,1) NOT NULL,
        ScriptName      NVARCHAR(260) NOT NULL,
        ScriptChecksum  VARBINARY(32) NULL,
        AppliedUtc      DATETIME2(3)  NOT NULL
            CONSTRAINT DF_SchemaVersion_AppliedUtc DEFAULT (SYSUTCDATETIME()),
        AppliedBy       SYSNAME       NOT NULL
            CONSTRAINT DF_SchemaVersion_AppliedBy DEFAULT (SUSER_SNAME()),
        Notes           NVARCHAR(400) NULL,
        CONSTRAINT PK_core_SchemaVersion PRIMARY KEY CLUSTERED (SchemaVersionId),
        CONSTRAINT UQ_SchemaVersion_ScriptName UNIQUE (ScriptName)
    );
END
GO

PRINT 'Settings tables ready.';
GO
