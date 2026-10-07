/*==============================================================================
  mobile.usp_Device_GetForSignatureVerification
  Phase : 6
  Called by: the signature verification middleware, on every signed request.

  Purpose
  -------
  Return the material needed to verify a request signature and to decide
  whether the device may act at all: the public key, the device status and the
  employee it is bound to.

  Why this is read on every request rather than cached
  ----------------------------------------------------
  Device revocation must take effect immediately (§18: a revoked device must
  not be able to perform attendance transactions). A cache would leave a
  revoked device working until the entry expired. This lookup is a single
  index seek on a unique key.

  The row is returned even when the device is not usable, because the API
  needs the identifiers to write a meaningful security event. The API must
  act on @ResultCode, never on the mere presence of the row.

  Result codes
      0    Success
      1020 DeviceNotRegistered
      1021 DeviceNotApproved
      1022 DeviceRevoked
      1014 UserInactive
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_Device_GetForSignatureVerification
    @DevicePublicId UNIQUEIDENTIFIER,
    @ResultCode     INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @DeviceId     INT,
            @DeviceStatus TINYINT,
            @UserStatus   TINYINT;

    /* Assignment and data retrieval must be separate statements in T-SQL, so
       the status is resolved first and the row returned afterwards. */
    SELECT @DeviceId     = d.DeviceId,
           @DeviceStatus = d.Status,
           @UserStatus   = u.Status
    FROM core.Device AS d
    INNER JOIN core.MobileUser AS u
            ON u.MobileUserId = d.MobileUserId
    WHERE d.DevicePublicId = @DevicePublicId;

    IF @DeviceId IS NULL
    BEGIN
        SET @ResultCode = 1020;             -- DeviceNotRegistered
        RETURN;
    END;

    SET @ResultCode =
        CASE
            WHEN @DeviceStatus = 0 THEN 1021    -- DeviceNotApproved
            WHEN @DeviceStatus = 2 THEN 1022    -- DeviceRevoked
            WHEN @UserStatus  <> 1 THEN 1014    -- UserInactive (inactive or suspended employee)
            ELSE 0
        END;

    SELECT
        d.DeviceId,
        d.DevicePublicId,
        d.MobileUserId,
        u.MobileUserPublicId,
        u.UserId,
        d.PublicKey,
        d.Status            AS DeviceStatus,
        d.AttestationLevel,
        d.Platform,
        d.AppVersion,
        u.Status            AS MobileUserStatus
    FROM core.Device AS d
    INNER JOIN core.MobileUser AS u
            ON u.MobileUserId = d.MobileUserId
    WHERE d.DeviceId = @DeviceId;
END;
GO

PRINT 'mobile.usp_Device_GetForSignatureVerification deployed.';
GO
