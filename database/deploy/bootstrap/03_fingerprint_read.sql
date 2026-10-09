/*==============================================================================
  ClockInXtra — Bootstrap: read the deployed script-set fingerprint
  File  : database/deploy/bootstrap/03_fingerprint_read.sql

  Run against the application database. Returns the SHA-256 of the script set
  that was last deployed automatically, or no row if the database has never
  been deployed.

  core.SchemaVersion may not exist yet — on a database that was created but not
  populated, or one created by something other than this deployment — so the
  table is tested for rather than assumed. That is what makes the fast path on
  every subsequent start safe: one query, no error, no deployment.

  Parameters
      @ScriptName  nvarchar(260)
==============================================================================*/

IF OBJECT_ID(N'core.SchemaVersion', N'U') IS NULL
    SELECT CONVERT(VARBINARY(32), NULL) AS ScriptChecksum WHERE 1 = 0;
ELSE
    SELECT ScriptChecksum
    FROM core.SchemaVersion
    WHERE ScriptName = @ScriptName;
