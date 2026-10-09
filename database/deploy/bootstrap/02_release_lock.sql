/*==============================================================================
  ClockInXtra — Bootstrap: release the deployment lock
  File  : database/deploy/bootstrap/02_release_lock.sql

  Run against master, on the same connection that took the lock. Closing that
  connection would release it anyway; this makes the release explicit so the
  next host in the queue is not waiting on connection teardown.

  Parameters
      @Resource  nvarchar(255)
==============================================================================*/

DECLARE @result INT;

EXEC @result = sys.sp_releaseapplock
    @Resource  = @Resource,
    @LockOwner = N'Session';

SELECT @result;
