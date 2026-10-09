/*==============================================================================
  ClockInXtra — Database security: users and grants
  File  : database/security/10_security_users_grants.sql
  Phase : 5

  Run this AFTER the schema scripts and BEFORE (or after) the programmability
  scripts; it grants at schema level, so procedures created later are covered
  automatically.

  Principle (Claude.md §48, design decision DB-01/DB-02)
  -----------------------------------------------------
  Each application gets EXECUTE on exactly one procedure schema and nothing
  else. No application principal is a member of db_owner, db_datareader or
  db_datawriter, and none can read a table directly.

  Why the DENY statements are safe
  --------------------------------
  All tables and procedures are owned by dbo, so the ownership chain between a
  procedure and the tables it reads is unbroken. When the owners match, SQL
  Server does not check permissions on the referenced tables at all, which is
  why a user can hold EXECUTE on the procedure while being denied every
  permission on the underlying tables. The DENY statements below therefore
  block direct ad-hoc access (for example someone connecting with SSMS using
  the application account) without affecting the procedures.

  Logins are NOT created here
  ---------------------------
  Creating a SQL login requires a password, and passwords must never be
  committed to source control (§47). The operator creates the login first, by
  one of these means:

      -- Preferred, where Active Directory is available (no password anywhere):
      CREATE LOGIN [ALTIMATEWARE\svc_ClockInXtra_Api$] FROM WINDOWS;

      -- Fallback, SQL authentication; the password comes from the
      -- organisation's secret store and is typed by the operator:
      CREATE LOGIN [app_mobile] WITH PASSWORD = N'<from secret store>',
          CHECK_POLICY = ON;

  Then run this script, naming the three logins. All three are REQUIRED:

      sqlcmd -S <server> -d ClockInXtra -E -C -b \
             -v MobileUser="app_mobile" AdminUser="app_admin" JobUser="app_jobs" \
             -i database/security/10_security_users_grants.sql

  There are deliberately no defaults. This script used to carry
  :setvar MobileUser "app_mobile" and so on, which looked like defaults and was
  not: a :setvar runs AFTER sqlcmd has applied -v and reassigns the variable, so
  every -v on the command line was silently discarded and the script went
  looking for logins named app_* that nobody had created. Omitting a variable
  now fails with "scripting variable not defined", which is the correct
  outcome: granting rights to a principal nobody named is not something to
  guess at.
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/*------------------------------------------------------------------------------
  Create a database user for each application login.
  If the login does not exist, fail loudly rather than silently skipping: a
  half-applied security model is worse than a clear error.
------------------------------------------------------------------------------*/
DECLARE @principals TABLE (UserName SYSNAME NOT NULL PRIMARY KEY);
INSERT INTO @principals (UserName)
VALUES (N'$(MobileUser)'), (N'$(AdminUser)'), (N'$(JobUser)');

DECLARE @missing NVARCHAR(MAX) =
(
    SELECT STRING_AGG(p.UserName, N', ')
    FROM @principals AS p
    /* COLLATE: server catalog names use the instance collation, and the
       database is created with its own (00_create_database.sql). Where the two
       differ — SQL_Latin1_General_CP1_CI_AS is the installer default for US
       English — comparing them without this fails with error 468 and the
       deployment stops here (found in Phase 21). */
    WHERE NOT EXISTS (SELECT 1 FROM sys.server_principals AS sp
                      WHERE sp.name COLLATE DATABASE_DEFAULT = p.UserName)
      AND NOT EXISTS (SELECT 1 FROM sys.database_principals AS dp WHERE dp.name = p.UserName)
);

IF @missing IS NOT NULL
BEGIN
    DECLARE @message NVARCHAR(400) =
        N'These logins do not exist on the instance: ' + @missing +
        N'. Create them first (see the header of this script), then re-run.';
    RAISERROR(@message, 16, 1);
END
GO

IF DATABASE_PRINCIPAL_ID(N'$(MobileUser)') IS NULL
    CREATE USER [$(MobileUser)] FOR LOGIN [$(MobileUser)];
GO

IF DATABASE_PRINCIPAL_ID(N'$(AdminUser)') IS NULL
    CREATE USER [$(AdminUser)] FOR LOGIN [$(AdminUser)];
GO

IF DATABASE_PRINCIPAL_ID(N'$(JobUser)') IS NULL
    CREATE USER [$(JobUser)] FOR LOGIN [$(JobUser)];
GO

/*------------------------------------------------------------------------------
  The only grants any application receives.
------------------------------------------------------------------------------*/
GRANT EXECUTE ON SCHEMA::[mobile] TO [$(MobileUser)];
GO
GRANT EXECUTE ON SCHEMA::[admin]  TO [$(AdminUser)];
GO
GRANT EXECUTE ON SCHEMA::[job]    TO [$(JobUser)];
GO

/*------------------------------------------------------------------------------
  Explicitly deny everything else, including cross-application procedure
  schemas. A compromised internet-facing API process must not be able to call
  an administrative procedure (threat TH-28).
------------------------------------------------------------------------------*/
DENY EXECUTE ON SCHEMA::[admin] TO [$(MobileUser)], [$(JobUser)];
GO
DENY EXECUTE ON SCHEMA::[mobile] TO [$(AdminUser)], [$(JobUser)];
GO
DENY EXECUTE ON SCHEMA::[job] TO [$(MobileUser)], [$(AdminUser)];
GO

/* Break-glass recovery bypasses every authority check the portal applies, so
   no application may reach it -- above all not the portal, whose web process is
   the thing most likely to be compromised. Denied explicitly rather than merely
   not granted, so a later schema-wide grant cannot open it by accident. */
DENY EXECUTE ON SCHEMA::[recovery] TO [$(MobileUser)], [$(AdminUser)], [$(JobUser)];
GO

DENY SELECT, INSERT, UPDATE, DELETE, ALTER, REFERENCES ON SCHEMA::[core]
    TO [$(MobileUser)], [$(AdminUser)], [$(JobUser)];
GO

DENY SELECT, INSERT, UPDATE, DELETE, ALTER, REFERENCES ON SCHEMA::[audit]
    TO [$(MobileUser)], [$(AdminUser)], [$(JobUser)];
GO

/*------------------------------------------------------------------------------
  The portal's one database-level permission: VIEW LEDGER CONTENT.

  The audit trail and the validation-failure report show when SQL Server's
  ledger committed each row, and under which principal, from
  sys.database_ledger_transactions. That view is checked against the CALLER'S
  permissions — ownership chaining through the stored procedure does not cover
  it — so without this grant both pages fail for app_admin with "VIEW LEDGER
  CONTENT permission denied". (Found in Phase 20: in development the portal
  connects as the database owner, which hid the failure.)

  What it does not widen: the DENY on the audit schema above still applies, so
  the portal cannot read the audit tables themselves except through the
  admin-schema procedures. Verified by executing as a login-less user holding
  exactly these grants: both reports succeed; SELECT on audit.AuditLog is
  refused (229).
------------------------------------------------------------------------------*/
GRANT VIEW LEDGER CONTENT TO [$(AdminUser)];
GO

/*------------------------------------------------------------------------------
  Verification: print the effective permissions so the deployment log shows
  exactly what each application account can do.
------------------------------------------------------------------------------*/
SELECT
    dp.name                       AS Principal,
    dbp.permission_name           AS Permission,
    dbp.state_desc                AS State,
    SCHEMA_NAME(dbp.major_id)     AS SchemaName
FROM sys.database_permissions AS dbp
INNER JOIN sys.database_principals AS dp
        ON dp.principal_id = dbp.grantee_principal_id
WHERE dp.name IN (N'$(MobileUser)', N'$(AdminUser)', N'$(JobUser)')
  AND dbp.class IN (0, 3)   /* database, schema */
ORDER BY dp.name, SchemaName, dbp.permission_name;
GO

/* Guard against a future change quietly widening access. */
IF EXISTS
(
    SELECT 1
    FROM sys.database_role_members AS drm
    INNER JOIN sys.database_principals AS r ON r.principal_id = drm.role_principal_id
    INNER JOIN sys.database_principals AS m ON m.principal_id = drm.member_principal_id
    WHERE m.name IN (N'$(MobileUser)', N'$(AdminUser)', N'$(JobUser)')
      AND r.name IN (N'db_owner', N'db_datareader', N'db_datawriter', N'db_ddladmin')
)
BEGIN
    RAISERROR(N'An application account is a member of a privileged database role. This violates the least-privilege design (Claude.md §48).', 16, 1);
END
GO

PRINT 'Database users and least-privilege grants applied.';
GO
