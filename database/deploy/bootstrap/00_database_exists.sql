/*==============================================================================
  ClockInXtra — Bootstrap: does the application database exist?
  File  : database/deploy/bootstrap/00_database_exists.sql

  Run against master. Returns 1 if the named database exists, else 0.

  Why this is a script and not a string in C#: Claude.md §4 keeps SQL out of
  application code. Automatic deployment is the one component that cannot route
  through a stored procedure — it is what creates the procedures — so its
  handful of control statements live here instead, parameterised, where they can
  be reviewed alongside the rest of the database.

  Parameters
      @DatabaseName  sysname
==============================================================================*/

SELECT CASE WHEN DB_ID(@DatabaseName) IS NULL THEN 0 ELSE 1 END;
