/*==============================================================================
  admin.usp_AuditLog_Search
  Phase : 6
  Called by: the administration portal (permission Audit.View).

  Purpose
  -------
  Search the audit trail and the security-event trail (§32, §57).

  Two result sets are returned: audit entries first, then security events,
  both filtered by the same criteria. They are separate tables because they
  answer different questions — "who changed what" versus "what was attacked" —
  but an investigator almost always wants both for the same window.

  Reading the ledger metadata
  ---------------------------
  Both tables are append-only ledger tables, so each row carries the
  transaction that produced it. Joining sys.database_ledger_transactions
  exposes the commit time and the database principal that performed the
  insert. That is what lets an auditor distinguish a row written by the
  application from one written by someone with direct database access.

  Why paging is by key and not OFFSET
  -----------------------------------
  Audit tables grow without bound and are read newest-first. Keyset paging on
  the identity column stays fast at any depth, whereas OFFSET degrades as the
  table grows.

  Result codes
      0    Success
      1001 InvalidRequest
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_AuditLog_Search
    @FromUtc       DATETIME2(3),
    @ToUtc         DATETIME2(3),
    @EventType     VARCHAR(64)      = NULL,
    @ActorType     TINYINT          = NULL,
    @ActorId       INT              = NULL,
    @SubjectType   VARCHAR(48)      = NULL,
    @SubjectId     NVARCHAR(64)     = NULL,
    @Result        TINYINT          = NULL,
    @CorrelationId UNIQUEIDENTIFIER = NULL,
    @MinSeverity   TINYINT          = NULL,   -- security events only
    @BeforeAuditLogId     BIGINT    = NULL,   -- keyset paging cursor
    @BeforeSecurityEventId BIGINT   = NULL,
    @PageSize      INT              = 100,
    @ResultCode    INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @FromUtc IS NULL OR @ToUtc IS NULL OR @ToUtc < @FromUtc
       OR @PageSize IS NULL OR @PageSize < 1 OR @PageSize > 1000
    BEGIN
        SET @ResultCode = 1001;          -- InvalidRequest
        RETURN;
    END;

    ------------------------------------------------------------------
    -- 1. Audit entries.
    ------------------------------------------------------------------
    SELECT TOP (@PageSize)
        a.AuditLogId,
        a.EventType,
        a.OccurredUtc,
        a.ActorType,
        a.ActorId,
        a.ActorDisplay,
        a.SubjectType,
        a.SubjectId,
        a.Result,
        a.ReasonCode,
        a.SourceApplication,
        a.CorrelationId,
        a.DevicePublicId,
        a.Details,
        t.commit_time    AS LedgerCommitTime,
        t.principal_name AS LedgerPrincipalName
    FROM audit.AuditLog AS a
    LEFT JOIN sys.database_ledger_transactions AS t
           ON t.transaction_id = a.ledger_start_transaction_id
    WHERE a.OccurredUtc >= @FromUtc
      AND a.OccurredUtc <= @ToUtc
      AND (@EventType     IS NULL OR a.EventType   = @EventType)
      AND (@ActorType     IS NULL OR a.ActorType   = @ActorType)
      AND (@ActorId       IS NULL OR a.ActorId     = @ActorId)
      AND (@SubjectType   IS NULL OR a.SubjectType = @SubjectType)
      AND (@SubjectId     IS NULL OR a.SubjectId   = @SubjectId)
      AND (@Result        IS NULL OR a.Result      = @Result)
      AND (@CorrelationId IS NULL OR a.CorrelationId = @CorrelationId)
      AND (@BeforeAuditLogId IS NULL OR a.AuditLogId < @BeforeAuditLogId)
    ORDER BY a.AuditLogId DESC
    OPTION (RECOMPILE);

    ------------------------------------------------------------------
    -- 2. Security events.
    --    This is where the precise failure reason lives, including the ones
    --    the mobile API deliberately collapses to INVALID_CREDENTIALS so that
    --    an internet-facing endpoint cannot confirm a correct password
    --    (conflict CON-09).
    ------------------------------------------------------------------
    SELECT TOP (@PageSize)
        s.SecurityEventId,
        s.EventType,
        s.OccurredUtc,
        s.Severity,
        s.SubjectType,
        s.SubjectKey,
        s.DevicePublicId,
        s.ReasonCode,
        s.SourceApplication,
        s.CorrelationId,
        s.Details,
        t.commit_time    AS LedgerCommitTime,
        t.principal_name AS LedgerPrincipalName
    FROM audit.SecurityEvent AS s
    LEFT JOIN sys.database_ledger_transactions AS t
           ON t.transaction_id = s.ledger_start_transaction_id
    WHERE s.OccurredUtc >= @FromUtc
      AND s.OccurredUtc <= @ToUtc
      AND (@EventType     IS NULL OR s.EventType = @EventType)
      AND (@SubjectId     IS NULL OR s.SubjectKey = @SubjectId)
      AND (@CorrelationId IS NULL OR s.CorrelationId = @CorrelationId)
      AND (@MinSeverity   IS NULL OR s.Severity >= @MinSeverity)
      AND (@BeforeSecurityEventId IS NULL OR s.SecurityEventId < @BeforeSecurityEventId)
    ORDER BY s.SecurityEventId DESC
    OPTION (RECOMPILE);

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_AuditLog_Search deployed.';
GO
