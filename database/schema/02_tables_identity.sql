/*==============================================================================
  ClockInXtra — Identity tables
  File  : database/schema/02_tables_identity.sql
  Phase : 5

  Contents
      core.MobileUser              employees who clock in
      core.EmployeeCredential      local password material (DEC-01)
      core.MfaCredential           TOTP enrolment for employees
      core.Administrator           administration portal accounts
      core.Role / Permission / RolePermission / AdministratorRole

  Status enumerations are tinyint columns constrained by CHECK and mirrored
  one-for-one by C# enums (design decision DB-06). The values are documented
  beside each column; changing them requires changing both sides.

  Secrets: password material is a salted hash only, never reversible
  encryption (Claude.md §16). TOTP secrets are stored as ASP.NET Core Data
  Protection ciphertext and are never written in plaintext or logged (§13).
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;   -- required: this script creates filtered indexes
SET NOCOUNT ON;
GO

/*------------------------------------------------------------------------------
  core.MobileUser

  Assumption ASM-09: an employee record must carry a display name so that
  administrators and reports can identify the person. Every other profile
  attribute is optional, because Claude.md §17 warns against inventing
  mandatory organisational fields.
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.MobileUser', N'U') IS NULL
BEGIN
    CREATE TABLE core.MobileUser
    (
        MobileUserId        INT              IDENTITY(1,1)  NOT NULL,
        MobileUserPublicId  UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_MobileUser_MobileUserPublicId DEFAULT (NEWID()),
        UserId              NVARCHAR(64)     NOT NULL,   -- the identifier the employee types into the app
        EmployeeNumber      NVARCHAR(32)     NULL,
        FirstName           NVARCHAR(80)     NOT NULL,
        LastName            NVARCHAR(80)     NOT NULL,
        Email               NVARCHAR(256)    NULL,
        PhoneNumber         NVARCHAR(32)     NULL,
        Department          NVARCHAR(120)    NULL,
        JobTitle            NVARCHAR(120)    NULL,
        Status              TINYINT          NOT NULL     -- 0 Inactive, 1 Active, 2 Suspended
            CONSTRAINT DF_MobileUser_Status DEFAULT (1),
        CreatedUtc          DATETIME2(3)     NOT NULL
            CONSTRAINT DF_MobileUser_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        CreatedByAdministratorId INT         NULL,
        UpdatedUtc          DATETIME2(3)     NULL,
        UpdatedByAdministratorId INT         NULL,
        [RowVersion]        ROWVERSION       NOT NULL,
        CONSTRAINT PK_core_MobileUser PRIMARY KEY CLUSTERED (MobileUserId),
        CONSTRAINT UQ_MobileUser_MobileUserPublicId UNIQUE (MobileUserPublicId),
        CONSTRAINT UQ_MobileUser_UserId UNIQUE (UserId),
        CONSTRAINT CK_MobileUser_Status CHECK (Status IN (0, 1, 2)),
        CONSTRAINT CK_MobileUser_UserId_NotBlank CHECK (LEN(LTRIM(RTRIM(UserId))) > 0)
    );

    /* Employee number is optional but must be unique when supplied. */
    CREATE UNIQUE NONCLUSTERED INDEX UX_MobileUser_EmployeeNumber
        ON core.MobileUser (EmployeeNumber)
        WHERE EmployeeNumber IS NOT NULL;

    CREATE NONCLUSTERED INDEX IX_MobileUser_Status_Department
        ON core.MobileUser (Status)
        INCLUDE (Department, LastName, FirstName);
END
GO

/*------------------------------------------------------------------------------
  core.EmployeeCredential

  Kept apart from the profile so that adopting Active Directory later
  (OPEN-22) means disabling this provider, not restructuring user data.

  HashFormat identifies the algorithm and parameter set, so credentials can be
  upgraded in place when the work factor is raised (TD-05).
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.EmployeeCredential', N'U') IS NULL
BEGIN
    CREATE TABLE core.EmployeeCredential
    (
        EmployeeCredentialId INT           IDENTITY(1,1) NOT NULL,
        MobileUserId         INT           NOT NULL,
        HashFormat           VARCHAR(32)   NOT NULL,   -- e.g. 'pbkdf2-sha512'
        Iterations           INT           NOT NULL,
        Salt                 VARBINARY(32) NOT NULL,
        PasswordHash         VARBINARY(64) NOT NULL,
        MustChange           BIT           NOT NULL
            CONSTRAINT DF_EmployeeCredential_MustChange DEFAULT (0),
        LastChangedUtc       DATETIME2(3)  NOT NULL
            CONSTRAINT DF_EmployeeCredential_LastChangedUtc DEFAULT (SYSUTCDATETIME()),
        [RowVersion]         ROWVERSION    NOT NULL,
        CONSTRAINT PK_core_EmployeeCredential PRIMARY KEY CLUSTERED (EmployeeCredentialId),
        CONSTRAINT UQ_EmployeeCredential_MobileUserId UNIQUE (MobileUserId),
        CONSTRAINT FK_EmployeeCredential_MobileUser_MobileUserId
            FOREIGN KEY (MobileUserId) REFERENCES core.MobileUser (MobileUserId),
        CONSTRAINT CK_EmployeeCredential_Iterations CHECK (Iterations >= 100000),
        CONSTRAINT CK_EmployeeCredential_HashFormat
            CHECK (HashFormat IN ('pbkdf2-sha512', 'pbkdf2-sha256'))
    );
END
GO

/*------------------------------------------------------------------------------
  core.MfaCredential

  LastAcceptedTimeStep is the replay defence required by Claude.md §13: a code
  from a time step already used is refused even while it is still valid.
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.MfaCredential', N'U') IS NULL
BEGIN
    CREATE TABLE core.MfaCredential
    (
        MfaCredentialId      INT             IDENTITY(1,1) NOT NULL,
        MobileUserId         INT             NOT NULL,
        SecretProtected      VARBINARY(MAX)  NOT NULL,  -- Data Protection ciphertext, never plaintext
        Algorithm            VARCHAR(8)      NOT NULL
            CONSTRAINT DF_MfaCredential_Algorithm DEFAULT ('SHA1'),
        Digits               TINYINT         NOT NULL
            CONSTRAINT DF_MfaCredential_Digits DEFAULT (6),
        PeriodSeconds        SMALLINT        NOT NULL
            CONSTRAINT DF_MfaCredential_PeriodSeconds DEFAULT (30),
        Status               TINYINT         NOT NULL     -- 0 PendingActivation, 1 Active, 2 Revoked
            CONSTRAINT DF_MfaCredential_Status DEFAULT (0),
        LastAcceptedTimeStep BIGINT          NULL,
        EnrolledUtc          DATETIME2(3)    NOT NULL
            CONSTRAINT DF_MfaCredential_EnrolledUtc DEFAULT (SYSUTCDATETIME()),
        EnrolledByAdministratorId INT        NULL,
        ActivatedUtc         DATETIME2(3)    NULL,
        RevokedUtc           DATETIME2(3)    NULL,
        RevokedByAdministratorId  INT        NULL,
        [RowVersion]         ROWVERSION      NOT NULL,
        CONSTRAINT PK_core_MfaCredential PRIMARY KEY CLUSTERED (MfaCredentialId),
        CONSTRAINT FK_MfaCredential_MobileUser_MobileUserId
            FOREIGN KEY (MobileUserId) REFERENCES core.MobileUser (MobileUserId),
        CONSTRAINT CK_MfaCredential_Status CHECK (Status IN (0, 1, 2)),
        CONSTRAINT CK_MfaCredential_Algorithm CHECK (Algorithm IN ('SHA1', 'SHA256', 'SHA512')),
        CONSTRAINT CK_MfaCredential_Digits CHECK (Digits IN (6, 8)),
        CONSTRAINT CK_MfaCredential_PeriodSeconds CHECK (PeriodSeconds BETWEEN 15 AND 120),
        CONSTRAINT CK_MfaCredential_RevokedUtc
            CHECK ((Status = 2 AND RevokedUtc IS NOT NULL) OR (Status <> 2 AND RevokedUtc IS NULL))
    );

    /* At most one credential per employee may be enrolled or active at a time. */
    CREATE UNIQUE NONCLUSTERED INDEX UX_MfaCredential_ActiveUser
        ON core.MfaCredential (MobileUserId)
        WHERE Status IN (0, 1);
END
GO

/*------------------------------------------------------------------------------
  core.Administrator

  SecurityStamp changes whenever the password, status or role assignment
  changes. The portal revalidates it on every request, which ends existing
  sessions immediately (threat TH-35).

  Administrator MFA is held here rather than in a separate table because the
  relationship is strictly one-to-one and has no independent lifecycle.
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.Administrator', N'U') IS NULL
BEGIN
    CREATE TABLE core.Administrator
    (
        AdministratorId        INT              IDENTITY(1,1) NOT NULL,
        AdministratorPublicId  UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_Administrator_AdministratorPublicId DEFAULT (NEWID()),
        UserName               NVARCHAR(64)     NOT NULL,
        DisplayName            NVARCHAR(160)    NOT NULL,
        Email                  NVARCHAR(256)    NULL,
        HashFormat             VARCHAR(32)      NOT NULL,
        Iterations             INT              NOT NULL,
        Salt                   VARBINARY(32)    NOT NULL,
        PasswordHash           VARBINARY(64)    NOT NULL,
        MustChangePassword     BIT              NOT NULL
            CONSTRAINT DF_Administrator_MustChangePassword DEFAULT (1),
        SecurityStamp          UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_Administrator_SecurityStamp DEFAULT (NEWID()),
        MfaSecretProtected     VARBINARY(MAX)   NULL,
        MfaStatus              TINYINT          NOT NULL     -- 0 NotEnrolled, 1 PendingActivation, 2 Active
            CONSTRAINT DF_Administrator_MfaStatus DEFAULT (0),
        MfaLastAcceptedTimeStep BIGINT          NULL,
        Status                 TINYINT          NOT NULL     -- 0 Inactive, 1 Active, 2 Locked
            CONSTRAINT DF_Administrator_Status DEFAULT (1),
        LastLoginUtc           DATETIME2(3)     NULL,
        CreatedUtc             DATETIME2(3)     NOT NULL
            CONSTRAINT DF_Administrator_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        CreatedByAdministratorId INT            NULL,
        UpdatedUtc             DATETIME2(3)     NULL,
        [RowVersion]           ROWVERSION       NOT NULL,
        CONSTRAINT PK_core_Administrator PRIMARY KEY CLUSTERED (AdministratorId),
        CONSTRAINT UQ_Administrator_AdministratorPublicId UNIQUE (AdministratorPublicId),
        CONSTRAINT UQ_Administrator_UserName UNIQUE (UserName),
        CONSTRAINT CK_Administrator_Status CHECK (Status IN (0, 1, 2)),
        CONSTRAINT CK_Administrator_MfaStatus CHECK (MfaStatus IN (0, 1, 2)),
        CONSTRAINT CK_Administrator_Iterations CHECK (Iterations >= 100000),
        CONSTRAINT CK_Administrator_HashFormat
            CHECK (HashFormat IN ('pbkdf2-sha512', 'pbkdf2-sha256')),
        CONSTRAINT CK_Administrator_MfaSecret
            CHECK ((MfaStatus = 0 AND MfaSecretProtected IS NULL)
                OR (MfaStatus IN (1, 2) AND MfaSecretProtected IS NOT NULL))
    );
END
GO

/* Deferred self-references, added once the table exists. */
IF OBJECT_ID(N'FK_Administrator_Administrator_CreatedBy', N'F') IS NULL
    ALTER TABLE core.Administrator WITH CHECK
        ADD CONSTRAINT FK_Administrator_Administrator_CreatedBy
            FOREIGN KEY (CreatedByAdministratorId) REFERENCES core.Administrator (AdministratorId);
GO

IF OBJECT_ID(N'FK_MobileUser_Administrator_CreatedBy', N'F') IS NULL
    ALTER TABLE core.MobileUser WITH CHECK
        ADD CONSTRAINT FK_MobileUser_Administrator_CreatedBy
            FOREIGN KEY (CreatedByAdministratorId) REFERENCES core.Administrator (AdministratorId);
GO

IF OBJECT_ID(N'FK_MobileUser_Administrator_UpdatedBy', N'F') IS NULL
    ALTER TABLE core.MobileUser WITH CHECK
        ADD CONSTRAINT FK_MobileUser_Administrator_UpdatedBy
            FOREIGN KEY (UpdatedByAdministratorId) REFERENCES core.Administrator (AdministratorId);
GO

IF OBJECT_ID(N'FK_MfaCredential_Administrator_EnrolledBy', N'F') IS NULL
    ALTER TABLE core.MfaCredential WITH CHECK
        ADD CONSTRAINT FK_MfaCredential_Administrator_EnrolledBy
            FOREIGN KEY (EnrolledByAdministratorId) REFERENCES core.Administrator (AdministratorId);
GO

IF OBJECT_ID(N'FK_MfaCredential_Administrator_RevokedBy', N'F') IS NULL
    ALTER TABLE core.MfaCredential WITH CHECK
        ADD CONSTRAINT FK_MfaCredential_Administrator_RevokedBy
            FOREIGN KEY (RevokedByAdministratorId) REFERENCES core.Administrator (AdministratorId);
GO

/*------------------------------------------------------------------------------
  Authorization model: roles hold permissions, administrators hold roles.
  Permission codes are the strings the ASP.NET Core policy provider uses, so
  authorization checks are centralised rather than scattered (Claude.md §42).
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.Permission', N'U') IS NULL
BEGIN
    CREATE TABLE core.Permission
    (
        PermissionId  INT           IDENTITY(1,1) NOT NULL,
        Code          VARCHAR(64)   NOT NULL,
        Category      NVARCHAR(64)  NOT NULL,
        Description   NVARCHAR(256) NOT NULL,
        CONSTRAINT PK_core_Permission PRIMARY KEY CLUSTERED (PermissionId),
        CONSTRAINT UQ_Permission_Code UNIQUE (Code)
    );
END
GO

IF OBJECT_ID(N'core.Role', N'U') IS NULL
BEGIN
    CREATE TABLE core.Role
    (
        RoleId        INT           IDENTITY(1,1) NOT NULL,
        Name          NVARCHAR(64)  NOT NULL,
        Description   NVARCHAR(256) NULL,
        IsSystemRole  BIT           NOT NULL
            CONSTRAINT DF_Role_IsSystemRole DEFAULT (0),
        CreatedUtc    DATETIME2(3)  NOT NULL
            CONSTRAINT DF_Role_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        [RowVersion]  ROWVERSION    NOT NULL,
        CONSTRAINT PK_core_Role PRIMARY KEY CLUSTERED (RoleId),
        CONSTRAINT UQ_Role_Name UNIQUE (Name)
    );
END
GO

IF OBJECT_ID(N'core.RolePermission', N'U') IS NULL
BEGIN
    CREATE TABLE core.RolePermission
    (
        RoleId        INT          NOT NULL,
        PermissionId  INT          NOT NULL,
        GrantedUtc    DATETIME2(3) NOT NULL
            CONSTRAINT DF_RolePermission_GrantedUtc DEFAULT (SYSUTCDATETIME()),
        GrantedByAdministratorId INT NULL,
        CONSTRAINT PK_core_RolePermission PRIMARY KEY CLUSTERED (RoleId, PermissionId),
        CONSTRAINT FK_RolePermission_Role_RoleId
            FOREIGN KEY (RoleId) REFERENCES core.Role (RoleId) ON DELETE CASCADE,
        CONSTRAINT FK_RolePermission_Permission_PermissionId
            FOREIGN KEY (PermissionId) REFERENCES core.Permission (PermissionId),
        CONSTRAINT FK_RolePermission_Administrator_GrantedBy
            FOREIGN KEY (GrantedByAdministratorId) REFERENCES core.Administrator (AdministratorId)
    );
END
GO

IF OBJECT_ID(N'core.AdministratorRole', N'U') IS NULL
BEGIN
    CREATE TABLE core.AdministratorRole
    (
        AdministratorId INT          NOT NULL,
        RoleId          INT          NOT NULL,
        AssignedUtc     DATETIME2(3) NOT NULL
            CONSTRAINT DF_AdministratorRole_AssignedUtc DEFAULT (SYSUTCDATETIME()),
        AssignedByAdministratorId INT NULL,
        CONSTRAINT PK_core_AdministratorRole PRIMARY KEY CLUSTERED (AdministratorId, RoleId),
        CONSTRAINT FK_AdministratorRole_Administrator_AdministratorId
            FOREIGN KEY (AdministratorId) REFERENCES core.Administrator (AdministratorId) ON DELETE CASCADE,
        CONSTRAINT FK_AdministratorRole_Role_RoleId
            FOREIGN KEY (RoleId) REFERENCES core.Role (RoleId),
        CONSTRAINT FK_AdministratorRole_Administrator_AssignedBy
            FOREIGN KEY (AssignedByAdministratorId) REFERENCES core.Administrator (AdministratorId)
    );

    CREATE NONCLUSTERED INDEX IX_AdministratorRole_RoleId
        ON core.AdministratorRole (RoleId);
END
GO

PRINT 'Identity tables ready.';
GO
