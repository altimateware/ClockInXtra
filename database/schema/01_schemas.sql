/*==============================================================================
  ClockInXtra — Schemas
  File  : database/schema/01_schemas.sql
  Phase : 5

  Must be executed in the context of the application database.

  Schema separation is a security control, not organisation for its own sake
  (design decision DB-01). Application logins receive EXECUTE on exactly one
  procedure schema and never receive any permission on 'core'.

      core   tables (no application principal holds rights here)
      mobile stored procedures callable by the attendance API
      admin  stored procedures callable by the administration portal
      job    stored procedures callable by the maintenance account
      audit  append-only ledger tables for audit and security events
==============================================================================*/

/* Required by filtered indexes and by any later DML against tables that have
   them. sqlcmd defaults QUOTED_IDENTIFIER to OFF, so every script sets it
   explicitly rather than depending on how it was invoked. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

IF SCHEMA_ID(N'core') IS NULL
    EXEC (N'CREATE SCHEMA [core] AUTHORIZATION [dbo];');
GO

IF SCHEMA_ID(N'mobile') IS NULL
    EXEC (N'CREATE SCHEMA [mobile] AUTHORIZATION [dbo];');
GO

IF SCHEMA_ID(N'admin') IS NULL
    EXEC (N'CREATE SCHEMA [admin] AUTHORIZATION [dbo];');
GO

IF SCHEMA_ID(N'job') IS NULL
    EXEC (N'CREATE SCHEMA [job] AUTHORIZATION [dbo];');
GO

IF SCHEMA_ID(N'audit') IS NULL
    EXEC (N'CREATE SCHEMA [audit] AUTHORIZATION [dbo];');
GO

/* Break-glass account recovery. Deliberately granted to NO application
   account: the procedures here bypass the authority checks every portal action
   goes through, so only someone already trusted with the database itself may
   run them (see recovery.usp_Administrator_RecoverAccess). */
IF SCHEMA_ID(N'recovery') IS NULL
    EXEC (N'CREATE SCHEMA [recovery] AUTHORIZATION [dbo];');
GO

PRINT 'Schemas ready: core, mobile, admin, job, audit, recovery.';
GO
