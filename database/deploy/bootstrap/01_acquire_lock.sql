/*==============================================================================
  ClockInXtra — Bootstrap: take the deployment lock
  File  : database/deploy/bootstrap/01_acquire_lock.sql

  Run against master, because the application database may not exist yet and an
  application lock is scoped to the database it is taken in. Every host that
  might deploy therefore queues on the same resource in the same place.

  @LockOwner = 'Session' deliberately, not 'Transaction': the lock has to span
  CREATE DATABASE and the object scripts, which run on a different connection
  and cannot share one transaction. Closing the connection releases it, so a
  host that is killed mid-deployment does not leave the lock held.

  Returns the sp_getapplock result:
       0  granted immediately
       1  granted after waiting
      -1  timed out
      -2  cancelled
      -3  chosen as a deadlock victim
    -999  parameter or other error

  Parameters
      @Resource       nvarchar(255)
      @LockTimeoutMs  int
==============================================================================*/

DECLARE @result INT;

EXEC @result = sys.sp_getapplock
    @Resource    = @Resource,
    @LockMode    = N'Exclusive',
    @LockOwner   = N'Session',
    @LockTimeout = @LockTimeoutMs;

SELECT @result;
