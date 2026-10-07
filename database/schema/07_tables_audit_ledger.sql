/*==============================================================================
  ClockInXtra — Audit and security event tables (append-only ledger)
  File  : database/schema/07_tables_audit_ledger.sql
  Phase : 5

  Contents
      audit.AuditLog       administrative and security-relevant actions (§32)
      audit.SecurityEvent  authentication failures, replays, untrusted input

  Why ledger tables
  -----------------
  Claude.md §32 requires an audit trail, and the threat model requires it to
  resist tampering by administrators and DBAs (threat TH-40). SQL Server 2022
  append-only ledger tables block UPDATE and DELETE at the engine level and
  hash every row into the database ledger, so tampering is detectable through
  digest verification even by someone who can edit the database files.

  PERMISSION REQUIREMENT
  ----------------------
  Creating an append-only ledger table requires the ENABLE LEDGER permission.
  The deployment account must hold it, or this script fails. This is stated in
  the deployment guide. Application logins never need it.

  CONSEQUENCE THAT MUST BE UNDERSTOOD BEFORE GO-LIVE (conflict CON-11)
  --------------------------------------------------------------------
  Microsoft documents that deleting older data in append-only ledger tables is
  not supported, and TRUNCATE TABLE is not supported. These two tables can
  therefore never be purged row by row. If OPEN-14 (data retention) requires
  deletion of audit data, this design must change to period-scoped audit
  tables before production. Attendance data lives in ordinary tables precisely
  so that retention remains possible there.

  Design notes
  ------------
  * No foreign keys: audit rows must survive even if a referenced row is later
    removed, and they must never fail to insert because of a reference.
    Actor and subject are recorded by both surrogate id and a display value.
  * No DEFAULT constraints: the procedures always supply every value
    explicitly, which keeps the audit content unambiguous.
  * Two GENERATED ALWAYS ledger columns are added automatically by the engine
    (ledger_start_transaction_id, ledger_start_sequence_number). INSERT
    statements must not reference them.
  * NEVER write passwords, OTP values, TOTP secrets, tokens, signatures or
    encryption keys into any column here (§32, §33). The Details column is
    reviewed in code review for exactly this.
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'audit.AuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE audit.AuditLog
    (
        AuditLogId        BIGINT           IDENTITY(1,1) NOT NULL,
        EventType         VARCHAR(64)      NOT NULL,   -- e.g. 'Device.Approved', 'OfficeLocation.Updated'
        OccurredUtc       DATETIME2(3)     NOT NULL,

        ActorType         TINYINT          NOT NULL,   -- 0 System, 1 MobileUser, 2 Administrator
        ActorId           INT              NULL,       -- MobileUserId or AdministratorId
        ActorDisplay      NVARCHAR(128)    NULL,       -- user name captured at the time of the event

        SubjectType       VARCHAR(48)      NULL,       -- e.g. 'Device', 'MobileUser', 'Attendance'
        SubjectId         NVARCHAR(64)     NULL,       -- public identifier of the affected entity

        Result            TINYINT          NOT NULL,   -- 1 Success, 2 Failure
        ReasonCode        VARCHAR(64)      NULL,       -- matches the API error catalogue where applicable
        SourceApplication VARCHAR(32)      NOT NULL,   -- 'Attendance.Api' | 'Attendance.Admin' | 'Attendance.Job'
        CorrelationId     UNIQUEIDENTIFIER NULL,
        DevicePublicId    UNIQUEIDENTIFIER NULL,
        Details           NVARCHAR(2000)   NULL,       -- JSON. Never secrets. Never coordinates.

        CONSTRAINT PK_audit_AuditLog PRIMARY KEY CLUSTERED (AuditLogId),
        CONSTRAINT CK_AuditLog_ActorType CHECK (ActorType IN (0, 1, 2)),
        CONSTRAINT CK_AuditLog_Result CHECK (Result IN (1, 2)),
        CONSTRAINT CK_AuditLog_SourceApplication
            CHECK (SourceApplication IN ('Attendance.Api', 'Attendance.Admin', 'Attendance.Job'))
    )
    WITH (LEDGER = ON (APPEND_ONLY = ON));

    CREATE NONCLUSTERED INDEX IX_AuditLog_OccurredUtc
        ON audit.AuditLog (OccurredUtc)
        INCLUDE (EventType, ActorType, ActorId, Result);

    CREATE NONCLUSTERED INDEX IX_AuditLog_EventType_OccurredUtc
        ON audit.AuditLog (EventType, OccurredUtc);

    CREATE NONCLUSTERED INDEX IX_AuditLog_Subject
        ON audit.AuditLog (SubjectType, SubjectId)
        INCLUDE (OccurredUtc, EventType, Result);
END
GO

IF OBJECT_ID(N'audit.SecurityEvent', N'U') IS NULL
BEGIN
    CREATE TABLE audit.SecurityEvent
    (
        SecurityEventId   BIGINT           IDENTITY(1,1) NOT NULL,
        EventType         VARCHAR(64)      NOT NULL,   -- 'Auth.Failed', 'Signature.Replay', 'Location.Mocked', ...
        OccurredUtc       DATETIME2(3)     NOT NULL,
        Severity          TINYINT          NOT NULL,   -- 1 Information, 2 Warning, 3 Critical

        SubjectType       TINYINT          NOT NULL,   -- 0 Unknown, 1 MobileUser, 2 Administrator, 3 Device
        SubjectKey        NVARCHAR(128)    NULL,       -- account identifier or device public id
        DevicePublicId    UNIQUEIDENTIFIER NULL,

        ReasonCode        VARCHAR(64)      NOT NULL,   -- the precise internal reason, e.g. 'INVALID_OTP'
        SourceApplication VARCHAR(32)      NOT NULL,
        SourceAddressHash VARBINARY(32)    NULL,       -- salted hash, not a stored IP address (§63)
        CorrelationId     UNIQUEIDENTIFIER NULL,
        Details           NVARCHAR(2000)   NULL,       -- JSON. Never secrets.

        CONSTRAINT PK_audit_SecurityEvent PRIMARY KEY CLUSTERED (SecurityEventId),
        CONSTRAINT CK_SecurityEvent_Severity CHECK (Severity IN (1, 2, 3)),
        CONSTRAINT CK_SecurityEvent_SubjectType CHECK (SubjectType IN (0, 1, 2, 3)),
        CONSTRAINT CK_SecurityEvent_SourceApplication
            CHECK (SourceApplication IN ('Attendance.Api', 'Attendance.Admin', 'Attendance.Job'))
    )
    WITH (LEDGER = ON (APPEND_ONLY = ON));

    CREATE NONCLUSTERED INDEX IX_SecurityEvent_OccurredUtc
        ON audit.SecurityEvent (OccurredUtc)
        INCLUDE (EventType, Severity, ReasonCode);

    CREATE NONCLUSTERED INDEX IX_SecurityEvent_Subject
        ON audit.SecurityEvent (SubjectType, SubjectKey, OccurredUtc);
END
GO

/* The precise internal failure reason (for example INVALID_OTP) belongs here
   and NOT in the API response, because distinguishing a wrong password from a
   wrong OTP on an internet-facing endpoint confirms a correct password to an
   attacker (conflict CON-09 / OPEN-38). */

PRINT 'Audit ledger tables ready.';
GO
