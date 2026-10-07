/*==============================================================================
  mobile.usp_Device_GetStatus
  Phase : 6
  Called by: POST /api/v1/mobile/device/status

  Purpose
  -------
  Tell the application where its own registration stands, so it can show
  "waiting for approval" rather than failing every attendance attempt with an
  error the employee cannot act on.

  This endpoint is reachable by a device that is NOT yet active
  --------------------------------------------------------------
  Almost every mobile endpoint requires an Active device. This one deliberately
  does not: a PendingApproval device must be able to ask whether it has been
  approved. The signature is still verified against the registered public key,
  so the caller still proves possession of the private key — what is relaxed is
  the status requirement, not the authentication.

  A revoked device is told plainly that it is revoked. There is nothing to gain
  by being vague: the holder already knows which device they have, and an
  employee whose handset stopped working deserves to see why rather than a
  generic failure. The signature check means only the real device can ask.

  Result codes
      0    Success (the row describes the current state)
      1020 DeviceNotRegistered
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_Device_GetStatus
    @DevicePublicId UNIQUEIDENTIFIER,
    @ResultCode     INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @DeviceId INT;

    SELECT @DeviceId = d.DeviceId
    FROM core.Device AS d
    WHERE d.DevicePublicId = @DevicePublicId;

    IF @DeviceId IS NULL
    BEGIN
        SET @ResultCode = 1020;          -- DeviceNotRegistered
        RETURN;
    END;

    SELECT
        d.DevicePublicId,
        d.Status                AS DeviceStatus,      -- 0 Pending, 1 Active, 2 Revoked
        d.AttestationLevel,
        d.RegisteredUtc,
        d.ApprovedUtc,
        d.RevokedUtc,
        /* The reason is shown to the holder of the device: they are the person
           who needs to know why it stopped working. It never includes anything
           about other employees or other devices. */
        d.RevokedReason,
        u.MobileUserPublicId,
        u.UserId,
        CAST(CASE WHEN u.Status = 1 THEN 1 ELSE 0 END AS BIT) AS EmployeeActive,
        SYSUTCDATETIME()        AS ServerTimeUtc
    FROM core.Device AS d
    INNER JOIN core.MobileUser AS u
            ON u.MobileUserId = d.MobileUserId
    WHERE d.DeviceId = @DeviceId;

    SET @ResultCode = 0;
END;
GO

PRINT 'mobile.usp_Device_GetStatus deployed.';
GO
