/*==============================================================================
  mobile.usp_Device_TouchLastSeen
  Phase : 6
  Called by: the API, after a request has been fully authenticated.

  Purpose
  -------
  Record that an active device was seen, which supports the administrative
  view of dormant devices and helps investigate a disputed attendance record.

  Only Active devices are touched: a revoked device's LastSeenUtc must keep
  showing when it was last legitimately used.

  This is deliberately a separate call rather than part of the attendance
  transaction. It is not part of the attendance integrity guarantee, and it
  must never lengthen the transaction that holds attendance locks (§30).

  Result codes
      0 Success (including "no row updated", which is not an error here)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_Device_TouchLastSeen
    @DeviceId   INT,
    @AppVersion NVARCHAR(32) = NULL,
    @OsVersion  NVARCHAR(32) = NULL,
    @ResultCode INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE core.Device
    SET LastSeenUtc = SYSUTCDATETIME(),
        /* Client-supplied metadata is refreshed only when supplied, and is
           never used for any authorization decision. */
        AppVersion  = COALESCE(@AppVersion, AppVersion),
        OsVersion   = COALESCE(@OsVersion, OsVersion)
    WHERE DeviceId = @DeviceId
      AND Status   = 1;   -- Active only

    SET @ResultCode = 0;
END;
GO

PRINT 'mobile.usp_Device_TouchLastSeen deployed.';
GO
