/*==============================================================================
  ClockInXtra — Database creation
  File     : database/deploy/00_create_database.sql
  Phase    : 5 (SQL schema)
  Target   : SQL Server 2022 (16.x)

  Run this script FIRST, connected to the target instance (not to the
  application database). It is re-runnable: an existing database is left
  in place and only its settings are re-applied.

  Execute with sqlcmd, for example:
      sqlcmd -S localhost -E -C -i database/deploy/00_create_database.sql

  Notes on the chosen options
  ---------------------------
  * READ_COMMITTED_SNAPSHOT is ON so that reporting queries in the admin
    portal do not block attendance writes. The clock-in/clock-out procedures
    deliberately use UPDLOCK/HOLDLOCK hints, which take real locks even under
    snapshot semantics, so the duplicate-clock-in guarantee is unaffected.
  * RECOVERY FULL is required for point-in-time restore and for availability
    groups. The backup strategy itself is an OPEN REQUIREMENT (OPEN-19).
  * The collation is stated explicitly so that behaviour does not depend on
    the instance default. UserId and UserName comparisons are case-insensitive.
==============================================================================*/

SET NOCOUNT ON;
GO

:setvar DatabaseName "ClockInXtra"
GO

IF DB_ID(N'$(DatabaseName)') IS NULL
BEGIN
    PRINT 'Creating database $(DatabaseName)...';
    EXEC (N'CREATE DATABASE [$(DatabaseName)] COLLATE Latin1_General_100_CI_AS;');
END
ELSE
    PRINT 'Database $(DatabaseName) already exists — applying settings only.';
GO

ALTER DATABASE [$(DatabaseName)] SET COMPATIBILITY_LEVEL = 160;
GO

ALTER DATABASE [$(DatabaseName)] SET RECOVERY FULL;
GO

ALTER DATABASE [$(DatabaseName)] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO

ALTER DATABASE [$(DatabaseName)] SET ALLOW_SNAPSHOT_ISOLATION ON;
GO

/* Auto-close/auto-shrink must stay off for a transactional workload. */
ALTER DATABASE [$(DatabaseName)] SET AUTO_CLOSE OFF;
GO
ALTER DATABASE [$(DatabaseName)] SET AUTO_SHRINK OFF;
GO
ALTER DATABASE [$(DatabaseName)] SET AUTO_CREATE_STATISTICS ON;
GO
ALTER DATABASE [$(DatabaseName)] SET AUTO_UPDATE_STATISTICS ON;
GO

/* Verify the instance can host the ledger audit tables used in phase 5.
   Ledger is available in every SQL Server 2022 edition; this check exists to
   fail early and clearly on an older instance. */
IF CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) < 16
BEGIN
    RAISERROR(N'ClockInXtra requires SQL Server 2022 (16.x) or later: the audit trail uses ledger tables.', 16, 1);
END
GO

PRINT 'Database $(DatabaseName) is ready.';
GO
