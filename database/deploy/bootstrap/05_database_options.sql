/*==============================================================================
  ClockInXtra — Bootstrap: are an existing database's options the documented ones?
  File  : database/deploy/bootstrap/05_database_options.sql

  Run against master, for a database that already existed when deployment
  started.

  Why this exists
  ---------------
  database/deploy/00_create_database.sql sets the collation, the compatibility
  level, RECOVERY FULL, READ_COMMITTED_SNAPSHOT and the snapshot isolation
  state. Automatic deployment runs it ONLY when it creates the database,
  because it contains

      ALTER DATABASE ... SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE

  which disconnects every open session — unacceptable at the startup of one
  host among several serving traffic.

  The consequence is that a database created by hand, or by a different script,
  keeps whatever options it was made with and silently never receives these.
  That is not a theoretical risk: a server prepared ahead of a first deployment
  had collation SQL_Latin1_General_CP1_CI_AS rather than the documented
  Latin1_General_100_CI_AS, and no READ_COMMITTED_SNAPSHOT. Objects deploy
  perfectly into such a database and the difference shows up later as
  comparison and blocking behaviour that no test reproduces.

  This reports the options so the deployer can say so plainly. It never changes
  anything: correcting a collation means rebuilding the database, which is a
  decision for whoever owns the data, not for a process that is starting up.

  Parameters
      @DatabaseName  sysname
==============================================================================*/

SELECT
    d.collation_name                        AS CollationName,
    d.compatibility_level                   AS CompatibilityLevel,
    d.is_read_committed_snapshot_on         AS ReadCommittedSnapshotOn,
    d.snapshot_isolation_state              AS SnapshotIsolationState,
    d.recovery_model_desc                   AS RecoveryModel
FROM sys.databases AS d
WHERE d.name = @DatabaseName;
