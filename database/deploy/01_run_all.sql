/*==============================================================================
  ClockInXtra — Deployment runner
  File  : database/deploy/01_run_all.sql
  Phase : 5–6

  Runs every database object script in dependency order and records each one
  in core.SchemaVersion.

  Prerequisites
  -------------
  1. database/deploy/00_create_database.sql has been run on the instance.
  2. The deployment account holds ENABLE LEDGER (required to create the
     append-only audit tables) plus the usual DDL rights.
  3. The application logins exist, if the security script is to be run
     (see database/security/10_security_users_grants.sql).

  How to run — SQLCMD MODE IS REQUIRED because this script uses :r includes.
  Run it from the 'database' directory, because :r resolves paths relative to
  the current working directory, not to this file:

      cd database
      sqlcmd -S localhost -d ClockInXtra -E -C -b -I -i "deploy/01_run_all.sql"

  The include paths below use forward slashes deliberately. Windows accepts
  them and Linux requires them: with backslashes, sqlcmd on Linux refuses
  every include with 'Invalid filename.' and the deployment stops at the
  first table script. Automatic deployment is unaffected either way, because
  SqlScriptSet normalises separators before resolving a resource.

      In SSMS: Query menu -> SQLCMD Mode, set the working directory
      accordingly, then execute.

  The -I switch (QUOTED_IDENTIFIER ON) is belt and braces: every script sets
  the option itself, because filtered indexes cannot be created without it and
  sqlcmd defaults it to OFF.

  Re-running is safe: table scripts guard their own objects, procedures use
  CREATE OR ALTER, and seed scripts insert only what is missing.
==============================================================================*/

:on error exit
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

PRINT '================================================================';
PRINT 'ClockInXtra database deployment';
PRINT 'Database : ' + DB_NAME();
PRINT 'Server   : ' + @@SERVERNAME;
PRINT 'Started  : ' + CONVERT(VARCHAR(30), SYSUTCDATETIME(), 126) + 'Z (UTC)';
PRINT '================================================================';
GO

/* Refuse to deploy into master or any other system database. */
IF DB_ID() <= 4
BEGIN
    RAISERROR(N'Refusing to deploy into a system database. Connect to the ClockInXtra database first.', 20, 1) WITH LOG;
END
GO

/*--------------------------------------------------------------- 1. schema ----*/
PRINT '--- Schemas and tables ---';
GO
:r schema/01_schemas.sql
:r schema/02_tables_identity.sql
:r schema/03_tables_device.sql
:r schema/04_tables_attendance.sql
:r schema/05_tables_request_hygiene.sql
:r schema/06_tables_settings.sql
:r schema/07_tables_audit_ledger.sql
:r schema/08_tables_reference_lists.sql

/*------------------------------------------------------ 2. programmability ----*/
/* 'core' procedures are shared internals called only by the mobile, admin and
   job procedures, so they are deployed first. */
PRINT '--- Stored procedures (core: shared internals) ---';
GO
:r programmability/core/usp_ApplicationSetting_GetAttendanceRules.sql
:r programmability/core/usp_AuditLog_Create.sql
:r programmability/core/usp_SecurityEvent_Create.sql
:r programmability/core/usp_AuthenticationAttempt_Check.sql
:r programmability/core/usp_AuthenticationAttempt_RegisterFailure.sql
:r programmability/core/usp_AuthenticationAttempt_Reset.sql
:r programmability/core/usp_MfaCredential_TryConsumeTimeStep.sql

PRINT '--- Stored procedures (mobile: attendance API) ---';
GO
:r programmability/mobile/usp_Device_IssueRegistrationChallenge.sql
:r programmability/mobile/usp_Device_Register.sql
:r programmability/mobile/usp_Device_GetForSignatureVerification.sql
:r programmability/mobile/usp_Device_TouchLastSeen.sql
:r programmability/mobile/usp_Device_GetStatus.sql
:r programmability/mobile/usp_ApplicationSetting_GetMobileRuntime.sql
:r programmability/mobile/usp_ApplicationSetting_GetSecurityPolicy.sql
:r programmability/mobile/usp_RequestNonce_TryInsert.sql
:r programmability/mobile/usp_Idempotency_TryBegin.sql
:r programmability/mobile/usp_Idempotency_Complete.sql
:r programmability/mobile/usp_MobileUser_GetForAuthentication.sql
:r programmability/mobile/usp_OfficeLocation_GetActive.sql
:r programmability/mobile/usp_Attendance_GetCurrentStatus.sql
:r programmability/mobile/usp_Attendance_ClockIn.sql
:r programmability/mobile/usp_Attendance_ClockOut.sql

PRINT '--- Stored procedures (admin: administration portal) ---';
GO
:r programmability/admin/usp_OfficeLocation_Create.sql
:r programmability/admin/usp_OfficeLocation_Update.sql
:r programmability/admin/usp_OfficeLocation_SetStatus.sql
:r programmability/admin/usp_OfficeLocation_GetAll.sql
:r programmability/admin/usp_MobileUser_Update.sql
:r programmability/admin/usp_MobileUser_GetById.sql
:r programmability/admin/usp_ReferenceList_GetAll.sql
:r programmability/admin/usp_ReferenceList_Save.sql
:r programmability/admin/usp_ReferenceList_SetStatus.sql
:r programmability/admin/usp_Device_Approve.sql
:r programmability/admin/usp_Device_Revoke.sql
:r programmability/admin/usp_ApplicationSetting_GetAll.sql
:r programmability/admin/usp_TimeZone_GetAll.sql
:r programmability/admin/usp_ApplicationSetting_Set.sql
:r programmability/admin/usp_MobileUser_Create.sql
:r programmability/admin/usp_MobileUser_Search.sql
:r programmability/admin/usp_Administrator_GetForAuthentication.sql
:r programmability/admin/usp_Administrator_Create.sql
:r programmability/admin/usp_Attendance_GetDailyReport.sql
:r programmability/admin/usp_Attendance_RequestCorrection.sql
:r programmability/admin/usp_Attendance_ApproveCorrection.sql
:r programmability/admin/usp_Attendance_GetForCorrection.sql
:r programmability/admin/usp_AttendanceCorrection_Search.sql
:r programmability/admin/usp_AuditLog_Search.sql
:r programmability/admin/ufn_Report_ValidationFailureCategory.sql
:r programmability/admin/usp_Report_GetValidationFailures.sql
:r programmability/admin/usp_Report_GetFilterOptions.sql
:r programmability/admin/usp_MfaCredential_Enrol.sql
:r programmability/admin/usp_MfaCredential_Activate.sql
:r programmability/admin/usp_MfaCredential_Revoke.sql
:r programmability/admin/usp_Device_GetPendingApprovals.sql
:r programmability/admin/usp_MobileUser_SetStatus.sql
:r programmability/admin/usp_Administrator_RecordLogin.sql
:r programmability/admin/usp_Administrator_RecordLogout.sql
:r programmability/admin/usp_Administrator_TryConsumeTimeStep.sql
:r programmability/admin/usp_Administrator_Bootstrap.sql
:r programmability/admin/usp_MfaCredential_GetForActivation.sql
:r programmability/admin/usp_Administrator_CheckAuthorityOver.sql
:r programmability/admin/usp_Administrator_CheckManagerRemains.sql
:r programmability/admin/usp_Administrator_EnrolMfa.sql
:r programmability/admin/usp_Administrator_ChangePassword.sql
:r programmability/admin/usp_Administrator_ResetPassword.sql
:r programmability/admin/usp_Administrator_SetStatus.sql
:r programmability/admin/usp_Administrator_SetRole.sql
:r programmability/admin/usp_Administrator_Search.sql
:r programmability/admin/usp_Role_GetAll.sql

PRINT '--- Stored procedures (job: scheduled maintenance) ---';
GO
:r programmability/job/usp_Maintenance_PurgeRequestNonce.sql
:r programmability/job/usp_Maintenance_PurgeIdempotency.sql
:r programmability/job/usp_Maintenance_PurgeChallenges.sql
:r programmability/job/usp_Maintenance_PurgeAttendanceEvidence.sql
:r programmability/job/usp_Maintenance_GenerateLedgerDigest.sql
:r programmability/job/usp_Maintenance_VerifyLedger.sql

PRINT '--- Stored procedures (recovery: break-glass, granted to no application account) ---';
GO
:r programmability/recovery/usp_Administrator_RecoverAccess.sql

/*------------------------------------------------------------ 3. seed data ----*/
PRINT '--- Reference and seed data ---';
GO
:r seed/20_seed_permissions_roles.sql
:r seed/21_seed_settings.sql

/*------------------------------------------------- 4. record the deployment ---*/
DECLARE @scripts TABLE (ScriptName NVARCHAR(260) NOT NULL PRIMARY KEY);
INSERT INTO @scripts (ScriptName)
VALUES
    (N'schema/01_schemas.sql'),
    (N'schema/02_tables_identity.sql'),
    (N'schema/03_tables_device.sql'),
    (N'schema/04_tables_attendance.sql'),
    (N'schema/05_tables_request_hygiene.sql'),
    (N'schema/06_tables_settings.sql'),
    (N'schema/07_tables_audit_ledger.sql'),
    (N'schema/08_tables_reference_lists.sql'),
    (N'seed/20_seed_permissions_roles.sql'),
    (N'seed/21_seed_settings.sql');

INSERT INTO core.SchemaVersion (ScriptName, Notes)
SELECT s.ScriptName, N'Applied by 01_run_all.sql'
FROM @scripts AS s
WHERE NOT EXISTS (SELECT 1 FROM core.SchemaVersion AS v WHERE v.ScriptName = s.ScriptName);

/* Procedures are deployed with CREATE OR ALTER on every run, so they are
   recorded as one entry that is refreshed rather than one row per version. */
UPDATE core.SchemaVersion
SET AppliedUtc = SYSUTCDATETIME(),
    AppliedBy  = SUSER_SNAME(),
    Notes      = CONCAT(N'Procedures redeployed: ',
                        (SELECT COUNT(*) FROM sys.procedures), N' objects')
WHERE ScriptName = N'programmability/*';

IF @@ROWCOUNT = 0
    INSERT INTO core.SchemaVersion (ScriptName, Notes)
    VALUES (N'programmability/*',
            CONCAT(N'Procedures deployed: ', (SELECT COUNT(*) FROM sys.procedures), N' objects'));
GO

/*--------------------------------------------------------- 5. sanity checks ---*/
PRINT '--- Post-deployment verification ---';
GO

/* Every table that should exist, does. */
DECLARE @expected TABLE (ObjectName NVARCHAR(200) NOT NULL PRIMARY KEY);
INSERT INTO @expected (ObjectName)
VALUES
    (N'core.MobileUser'), (N'core.EmployeeCredential'), (N'core.MfaCredential'),
    (N'core.Administrator'), (N'core.Role'), (N'core.Permission'),
    (N'core.RolePermission'), (N'core.AdministratorRole'),
    (N'core.Device'), (N'core.DeviceRegistrationChallenge'),
    (N'core.OfficeLocation'), (N'core.Attendance'), (N'core.AttendanceEvent'),
    (N'core.AttendanceCorrection'), (N'core.RequestNonce'),
    (N'core.RequestIdempotency'), (N'core.AuthenticationAttempt'),
    (N'core.ApplicationSetting'), (N'core.SchemaVersion'),
    (N'core.Department'), (N'core.JobTitle'),
    (N'audit.AuditLog'), (N'audit.SecurityEvent');

DECLARE @missingObjects NVARCHAR(MAX) =
(
    SELECT STRING_AGG(e.ObjectName, N', ')
    FROM @expected AS e
    WHERE OBJECT_ID(e.ObjectName, N'U') IS NULL
);

IF @missingObjects IS NOT NULL
BEGIN
    DECLARE @msg NVARCHAR(MAX) = N'Deployment incomplete. Missing tables: ' + @missingObjects;
    RAISERROR(@msg, 16, 1);
END
GO

/* The audit tables must actually be append-only ledger tables: if this check
   fails the tamper-evidence control described in the threat model is absent. */
IF EXISTS
(
    SELECT 1
    FROM sys.tables
    WHERE object_id IN (OBJECT_ID(N'audit.AuditLog'), OBJECT_ID(N'audit.SecurityEvent'))
      AND (ledger_type_desc <> N'APPEND_ONLY_LEDGER_TABLE')
)
BEGIN
    RAISERROR(N'audit.AuditLog / audit.SecurityEvent are not append-only ledger tables.', 16, 1);
END
GO

/* The filtered index behind DEC-04 (one active device per employee) must
   exist: without it the guarantee is only in application code. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Device_ActiveUser' AND has_filter = 1)
BEGIN
    RAISERROR(N'UX_Device_ActiveUser is missing: one-active-device-per-employee is not enforced.', 16, 1);
END
GO

PRINT '--- Object counts ---';
GO
SELECT 'tables' AS Item, COUNT(*) AS Cnt FROM sys.tables WHERE schema_id IN (SCHEMA_ID('core'), SCHEMA_ID('audit'))
UNION ALL SELECT 'procedures', COUNT(*) FROM sys.procedures
UNION ALL SELECT 'foreign keys', COUNT(*) FROM sys.foreign_keys
UNION ALL SELECT 'check constraints', COUNT(*) FROM sys.check_constraints
UNION ALL SELECT 'filtered indexes', COUNT(*) FROM sys.indexes WHERE has_filter = 1;
GO

/* Show the outstanding business decisions. These block attendance processing
   by design: the system refuses rather than inventing a value (§15, §68). */
PRINT '--- Settings still awaiting a business decision ---';
GO
SELECT SettingKey, Category
FROM core.ApplicationSetting
WHERE SettingValue IS NULL
  AND ConfirmedUtc IS NULL      -- a confirmed blank is a decision, not a gap
ORDER BY Category, SettingKey;
GO

PRINT '================================================================';
PRINT 'Deployment complete.';
PRINT 'NEXT STEPS:';
PRINT '  1. Least privilege : database/security/10_security_users_grants.sql';
PRINT '  2. Verify behaviour: database/tests/smoke_attendance.sql  (dev/test only)';
PRINT '  3. Configure the settings listed above in the admin portal.';
PRINT '================================================================';
GO
