/*==============================================================================
  ClockInXtra — Bootstrap: record the deployed script-set fingerprint
  File  : database/deploy/bootstrap/04_fingerprint_write.sql

  Run against the application database, under the deployment lock, after every
  object script has succeeded. Writing it last is what makes the record mean
  "this script set deployed cleanly": a deployment that throws half way leaves
  the old fingerprint, and the next start tries again.

  The row sits alongside the per-script rows that database/deploy/01_run_all.sql
  writes when it is run by hand from sqlcmd. The two do not conflict: this one
  owns a single reserved ScriptName and nothing else touches it.

  Parameters
      @ScriptName   nvarchar(260)
      @Fingerprint  varbinary(32)
      @Notes        nvarchar(400)
==============================================================================*/

UPDATE core.SchemaVersion
SET ScriptChecksum = @Fingerprint,
    AppliedUtc     = SYSUTCDATETIME(),
    AppliedBy      = SUSER_SNAME(),
    Notes          = @Notes
WHERE ScriptName = @ScriptName;

IF @@ROWCOUNT = 0
    INSERT INTO core.SchemaVersion (ScriptName, ScriptChecksum, Notes)
    VALUES (@ScriptName, @Fingerprint, @Notes);
