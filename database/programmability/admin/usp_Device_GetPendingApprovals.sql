/*==============================================================================
  admin.usp_Device_GetPendingApprovals
  Phase : 6
  Called by: the administration portal (permission Device.View / Device.Approve).

  Purpose
  -------
  List device registrations awaiting approval, with the context an
  administrator needs to make a judgement rather than click through.

  What the list shows and why
  ---------------------------
  * AttestationLevel — whether the server could actually verify that the key
    lives in device secure hardware. A registration with level None is not
    necessarily fraudulent (on iOS it simply means App Attest is not enabled,
    OPEN-33), but it is weaker evidence and the administrator should see that
    before approving.
  * CurrentActiveDevice — approving this registration will REVOKE that device
    (DEC-04). The consequence is shown up front, because an employee whose
    working phone is about to stop clocking in deserves that to be a conscious
    decision.
  * RecentFailedAttempts — a registration preceded by failed authentication
    attempts against the same account is the shape a phished-credential
    takeover takes (threat TH-08). Surfacing it is what makes the human in the
    loop worth having.

  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Device_GetPendingApprovals
    @ResultCode INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        d.DeviceId,
        d.DevicePublicId,
        d.RegisteredUtc,
        d.Platform,
        d.AttestationLevel,
        d.DeviceModel,
        d.OsVersion,
        d.AppVersion,
        d.[RowVersion],
        u.MobileUserId,
        u.MobileUserPublicId,
        u.UserId,
        u.FirstName,
        u.LastName,
        u.Department,
        u.Status AS MobileUserStatus,
        active.DevicePublicId AS CurrentActiveDevicePublicId,
        active.DeviceModel    AS CurrentActiveDeviceModel,
        active.LastSeenUtc    AS CurrentActiveDeviceLastSeenUtc,
        COALESCE(att.FailedCount, 0) AS RecentFailedAttempts,
        att.LastFailedUtc
    FROM core.Device AS d
    INNER JOIN core.MobileUser AS u
            ON u.MobileUserId = d.MobileUserId
    /* The device this approval would revoke, if any. */
    LEFT JOIN core.Device AS active
           ON active.MobileUserId = d.MobileUserId
          AND active.Status = 1
    LEFT JOIN core.AuthenticationAttempt AS att
           ON att.SubjectType = 1
          AND att.SubjectKey  = u.UserId
    WHERE d.Status = 0                    -- PendingApproval
    ORDER BY d.RegisteredUtc;

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_Device_GetPendingApprovals deployed.';
GO
