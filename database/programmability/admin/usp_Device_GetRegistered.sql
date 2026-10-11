/*==============================================================================
  admin.usp_Device_GetRegistered
  Phase : 6
  Called by: the administration portal (permission Device.View).

  Purpose
  -------
  List every registered device, whatever its status, so an administrator can
  see and act on the ones already in service.

  Why this exists separately from usp_Device_GetPendingApprovals
  --------------------------------------------------------------
  That procedure answers "what is waiting for me to decide", and the portal
  had nothing else. The consequence was that an ACTIVE device could not be
  revoked at all: the Revoke action, its permission and admin.usp_Device_Revoke
  all existed, but no screen could reach a device that was not pending. The
  only route to revocation was approving a replacement, which meant an
  employee who lost a phone could not have it cut off until they had enrolled
  another one. That is the wrong way round for a lost or stolen handset.

  Ordering puts what needs attention first: pending, then active, then
  revoked, and most recently registered first within each.

  Revoked rows are included rather than hidden. An administrator asking "why
  did this employee's phone stop working" needs to see the revocation and its
  reason, and the reason is already shown to the employee in the app.

  The public key itself is never selected. Nothing in the portal needs it, and
  a key on a page is a key in a browser cache, a screenshot and a print queue.

  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Device_GetRegistered
    @Status     TINYINT = NULL,       -- NULL = every status
    @Page       INT     = 1,
    @PageSize   INT     = 25,
    @ResultCode INT     OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @Status IS NOT NULL AND @Status NOT IN (0, 1, 2)
    BEGIN
        SET @ResultCode = 1001;       -- InvalidRequest: unknown status filter
        RETURN;
    END;

    /* A page size arriving from a query string is untrusted. The ceiling is
       here as well as in the caller because this procedure is the thing that
       would actually read the rows. */
    IF @Page IS NULL OR @Page < 1 OR @PageSize IS NULL OR @PageSize < 1 OR @PageSize > 200
    BEGIN
        SET @ResultCode = 1001;       -- InvalidRequest
        RETURN;
    END;

    SELECT
        COUNT(*) OVER () AS TotalCount,
        d.DeviceId,
        d.DevicePublicId,
        d.[RowVersion],
        d.Status,
        d.Platform,
        d.AttestationLevel,
        d.DeviceModel,
        d.OsVersion,
        d.AppVersion,
        d.RegisteredUtc,
        d.ApprovedUtc,
        d.LastSeenUtc,
        d.RevokedUtc,
        d.RevokedReason,
        u.UserId,
        u.FirstName,
        u.LastName,
        u.Department
    FROM core.Device AS d
    INNER JOIN core.MobileUser AS u
            ON u.MobileUserId = d.MobileUserId
    WHERE (@Status IS NULL OR d.Status = @Status)
    ORDER BY
        /* Pending first: those are decisions somebody is waiting on. */
        CASE d.Status WHEN 0 THEN 0 WHEN 1 THEN 1 ELSE 2 END,
        d.RegisteredUtc DESC,
        /* A tie-break that is unique, so a row cannot appear on two pages or on
           none: ORDER BY without one leaves the order of equal rows undefined,
           and OFFSET then slices an order that may differ between queries. */
        d.DeviceId DESC
    OFFSET (@Page - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_Device_GetRegistered deployed.';
GO
