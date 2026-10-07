/*==============================================================================
  core.usp_AuditLog_Create
  Phase : 6
  Called by: procedures in the mobile, admin and job schemas — never directly
             by an application.

  Why it lives in the 'core' schema
  ---------------------------------
  Both applications must write audit records, but neither may be granted
  EXECUTE on the other's procedure schema. Shared internal procedures
  therefore live in 'core', which no application login can execute directly.
  A mobile or admin procedure can still call this one, because all objects are
  owned by dbo and SQL Server does not check permissions on referenced objects
  when the ownership chain is unbroken.

  Target table
  ------------
  audit.AuditLog is an append-only ledger table: INSERT is the only operation
  the engine permits, so an audit row cannot later be altered or deleted, even
  by a database administrator (threat TH-40).

  WHAT MUST NEVER BE PASSED TO @Details
  -------------------------------------
  Passwords, OTP values, TOTP secrets, signatures, private keys, encryption
  keys or raw coordinates (Claude.md §32 and §33). @Details carries a small
  JSON object describing what changed, for example which fields an
  administrator edited. Review this at every call site.

  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE core.usp_AuditLog_Create
    @EventType         VARCHAR(64),
    @ActorType         TINYINT,                   -- 0 System, 1 MobileUser, 2 Administrator
    @ActorId           INT              = NULL,
    @ActorDisplay      NVARCHAR(128)    = NULL,
    @SubjectType       VARCHAR(48)      = NULL,
    @SubjectId         NVARCHAR(64)     = NULL,
    @Result            TINYINT,                   -- 1 Success, 2 Failure
    @ReasonCode        VARCHAR(64)      = NULL,
    @SourceApplication VARCHAR(32),
    @CorrelationId     UNIQUEIDENTIFIER = NULL,
    @DevicePublicId    UNIQUEIDENTIFIER = NULL,
    @Details           NVARCHAR(2000)   = NULL,
    @OccurredUtc       DATETIME2(3)     = NULL,   -- caller may pass the transaction time for consistency
    @ResultCode        INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    /* The server decides the time. A caller-supplied value is accepted only so
       that every row written inside one transaction shares an instant, and it
       still originates from SYSUTCDATETIME() on this server (§31). */
    INSERT INTO audit.AuditLog
        (EventType, OccurredUtc, ActorType, ActorId, ActorDisplay,
         SubjectType, SubjectId, Result, ReasonCode, SourceApplication,
         CorrelationId, DevicePublicId, Details)
    VALUES
        (@EventType, COALESCE(@OccurredUtc, SYSUTCDATETIME()), @ActorType, @ActorId, @ActorDisplay,
         @SubjectType, @SubjectId, @Result, @ReasonCode, @SourceApplication,
         @CorrelationId, @DevicePublicId, @Details);

    SET @ResultCode = 0;
END;
GO

PRINT 'core.usp_AuditLog_Create deployed.';
GO
