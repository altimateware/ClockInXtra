/*==============================================================================
  ClockInXtra — Device tables
  File  : database/schema/03_tables_device.sql
  Phase : 5

  Contents
      core.Device                       registered devices bound to employees
      core.DeviceRegistrationChallenge  single-use attestation challenges

  The device credential is the hardware-backed public key, not any operating
  system identifier (conflict CON-05). Android and iOS both restrict permanent
  hardware identifiers, so DeviceModel/OsVersion are metadata only and are
  never used for authorization.

  DECISION DEC-04 is enforced here by the database, not by C#:
  UX_Device_ActiveUser permits at most one Active device per employee.
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;   -- required: UX_Device_ActiveUser is a filtered index
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'core.Device', N'U') IS NULL
BEGIN
    CREATE TABLE core.Device
    (
        DeviceId            INT              IDENTITY(1,1) NOT NULL,
        DevicePublicId      UNIQUEIDENTIFIER NOT NULL       -- the 'keyid' carried in the signature
            CONSTRAINT DF_Device_DevicePublicId DEFAULT (NEWID()),
        MobileUserId        INT              NOT NULL,

        /* Uncompressed P-256 public key point: 0x04 || X(32) || Y(32) = 65 bytes.
           The thumbprint is SHA-256 of that point and carries the uniqueness
           constraint, because VARBINARY(65) is indexable but the thumbprint
           keeps the index narrow and makes duplicate detection explicit. */
        PublicKey           VARBINARY(65)    NOT NULL,
        PublicKeyThumbprint VARBINARY(32)    NOT NULL,

        Platform            TINYINT          NOT NULL,      -- 1 Android, 2 iOS
        AttestationLevel    TINYINT          NOT NULL       -- 0 None, 1 Software, 2 Hardware
            CONSTRAINT DF_Device_AttestationLevel DEFAULT (0),
        Status              TINYINT          NOT NULL       -- 0 PendingApproval, 1 Active, 2 Revoked
            CONSTRAINT DF_Device_Status DEFAULT (0),

        /* Untrusted, client-supplied metadata. Length and character range are
           constrained because these strings are displayed in the admin portal
           (threat TH-36). Printable ASCII only. */
        DeviceModel         NVARCHAR(64)     NULL,
        OsVersion           NVARCHAR(32)     NULL,
        AppVersion          NVARCHAR(32)     NULL,

        RegisteredUtc       DATETIME2(3)     NOT NULL
            CONSTRAINT DF_Device_RegisteredUtc DEFAULT (SYSUTCDATETIME()),
        ApprovedUtc         DATETIME2(3)     NULL,
        ApprovedByAdministratorId INT        NULL,
        LastSeenUtc         DATETIME2(3)     NULL,
        RevokedUtc          DATETIME2(3)     NULL,
        RevokedByAdministratorId  INT        NULL,
        RevokedReason       NVARCHAR(256)    NULL,
        [RowVersion]        ROWVERSION       NOT NULL,

        CONSTRAINT PK_core_Device PRIMARY KEY CLUSTERED (DeviceId),
        CONSTRAINT UQ_Device_DevicePublicId UNIQUE (DevicePublicId),
        CONSTRAINT UQ_Device_PublicKeyThumbprint UNIQUE (PublicKeyThumbprint),
        CONSTRAINT FK_Device_MobileUser_MobileUserId
            FOREIGN KEY (MobileUserId) REFERENCES core.MobileUser (MobileUserId),
        CONSTRAINT FK_Device_Administrator_ApprovedBy
            FOREIGN KEY (ApprovedByAdministratorId) REFERENCES core.Administrator (AdministratorId),
        CONSTRAINT FK_Device_Administrator_RevokedBy
            FOREIGN KEY (RevokedByAdministratorId) REFERENCES core.Administrator (AdministratorId),
        CONSTRAINT CK_Device_Platform CHECK (Platform IN (1, 2)),
        CONSTRAINT CK_Device_Status CHECK (Status IN (0, 1, 2)),
        CONSTRAINT CK_Device_AttestationLevel CHECK (AttestationLevel IN (0, 1, 2)),
        CONSTRAINT CK_Device_PublicKeyFormat
            CHECK (DATALENGTH(PublicKey) = 65 AND SUBSTRING(PublicKey, 1, 1) = 0x04),
        CONSTRAINT CK_Device_Approved
            CHECK ((Status = 1 AND ApprovedUtc IS NOT NULL) OR Status <> 1),
        CONSTRAINT CK_Device_Revoked
            CHECK ((Status = 2 AND RevokedUtc IS NOT NULL) OR (Status <> 2 AND RevokedUtc IS NULL)),
        /* Printable-ASCII check on untrusted client metadata.

           The binary collation is essential, not decoration. A LIKE character
           range is evaluated in COLLATION order, not code-point order. Under
           the database's Latin1_General_100_CI_AS collation, '~' sorts before
           the letters, so the range ' ' to '~' excludes ordinary capitals and
           '%[^ -~]%' matches a plain value like 'TESTDEVICE' — the constraint
           would reject every real device model. Forcing BIN2 makes the range
           mean code points 0x20..0x7E, which is what is intended: printable
           ASCII in, control characters out. */
        CONSTRAINT CK_Device_DeviceModel_Printable
            CHECK (DeviceModel IS NULL
                   OR DeviceModel COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^ -~]%'),
        CONSTRAINT CK_Device_OsVersion_Printable
            CHECK (OsVersion IS NULL
                   OR OsVersion COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^ -~]%'),
        CONSTRAINT CK_Device_AppVersion_Printable
            CHECK (AppVersion IS NULL
                   OR AppVersion COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^ -~]%')
    );

    /* DEC-04: at most one active device per employee. */
    CREATE UNIQUE NONCLUSTERED INDEX UX_Device_ActiveUser
        ON core.Device (MobileUserId)
        WHERE Status = 1;

    /* Supports the administration queue of devices awaiting approval. */
    CREATE NONCLUSTERED INDEX IX_Device_Status_RegisteredUtc
        ON core.Device (Status, RegisteredUtc)
        INCLUDE (MobileUserId, Platform, AttestationLevel);
END
GO

/*------------------------------------------------------------------------------
  core.DeviceRegistrationChallenge

  A challenge is issued before key generation and is embedded in the platform
  attestation, which is what binds the attestation to this server and prevents
  replay of a previously captured attestation (threat TH-10).

  Consumption must be atomic; the registration procedure uses
  UPDATE ... OUTPUT ... WHERE ConsumedUtc IS NULL so that two concurrent
  registrations cannot both consume one challenge.

  The source address is stored as a salted hash rather than in the clear:
  it is needed only to correlate abuse, not to identify a person (§63).
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.DeviceRegistrationChallenge', N'U') IS NULL
BEGIN
    CREATE TABLE core.DeviceRegistrationChallenge
    (
        ChallengeId    UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_DeviceRegistrationChallenge_ChallengeId DEFAULT (NEWID()),
        Challenge      VARBINARY(32)    NOT NULL,
        IssuedUtc      DATETIME2(3)     NOT NULL
            CONSTRAINT DF_DeviceRegistrationChallenge_IssuedUtc DEFAULT (SYSUTCDATETIME()),
        ExpiresUtc     DATETIME2(3)     NOT NULL,
        ConsumedUtc    DATETIME2(3)     NULL,
        IssuedToIpHash VARBINARY(32)    NULL,
        CONSTRAINT PK_core_DeviceRegistrationChallenge
            PRIMARY KEY NONCLUSTERED (ChallengeId),
        CONSTRAINT CK_DeviceRegistrationChallenge_Expiry CHECK (ExpiresUtc > IssuedUtc)
    );

    /* Clustered on issue time so that expiry purges are range scans. */
    CREATE CLUSTERED INDEX IX_DeviceRegistrationChallenge_IssuedUtc
        ON core.DeviceRegistrationChallenge (IssuedUtc, ChallengeId);
END
GO

PRINT 'Device tables ready.';
GO
