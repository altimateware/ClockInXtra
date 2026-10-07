/*==============================================================================
  admin.usp_Administrator_CheckManagerRemains
  Phase : 6 (added during Phase 12 completion)
  Called by: usp_Administrator_SetStatus and usp_Administrator_SetRole, inside
             their transactions, before they deactivate an administrator or
             remove a role. Not called by the portal directly.

  Purpose
  -------
  Refuse any change that would leave nobody able to manage administrators.

  WHY THIS IS A DATABASE RULE
  ---------------------------
  usp_Administrator_Bootstrap deliberately refuses to run once any
  administrator exists, so it cannot be used as a back door. The price of that
  is that the system must never reach a state where no remaining administrator
  can create or restore another: recovery would then need direct database
  surgery. One careless deactivation, or two administrators disabling each
  other at the same moment, would be enough.

  WHO COUNTS AS A REMAINING MANAGER
  ---------------------------------
  An administrator who, after the proposed change, is:
    - active (Status = 1),
    - holds Administrator.Manage through any role, and
    - can actually sign in: when Security.RequireAdministratorMfa is not
      'false', that means an enrolled authenticator (MfaStatus 1 or 2). An
      account with the permission but no way past the sign-in page is not a
      manager in any sense that helps.

  The proposed change is modelled by excluding either the whole target account
  (@DeactivatedAdministratorId) or one role on the target
  (@RemovedRoleAdministratorId + @RemovedRoleId).

  CONCURRENCY
  -----------
  The candidate rows are read WITH (UPDLOCK, HOLDLOCK). Two administrators each
  deactivating the other in overlapping transactions would otherwise both see
  one manager remaining, and both succeed.

  Result codes
      0    At least one manager remains
      1077 LastAdministratorManager — the change would leave none
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_CheckManagerRemains
    @DeactivatedAdministratorId  INT = NULL,
    @RemovedRoleAdministratorId  INT = NULL,
    @RemovedRoleId               INT = NULL,
    @ResultCode                  INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    /* Unconfirmed or missing reads as required: the safe default, and the one
       AdministratorAuthenticator applies. */
    DECLARE @MfaRequired BIT =
        CASE WHEN EXISTS (SELECT 1 FROM core.ApplicationSetting
                          WHERE SettingKey = 'Security.RequireAdministratorMfa'
                            AND LOWER(LTRIM(RTRIM(SettingValue))) = N'false')
             THEN 0 ELSE 1 END;

    IF EXISTS
    (
        SELECT 1
        FROM core.Administrator AS a WITH (UPDLOCK, HOLDLOCK)
        WHERE a.Status = 1
          AND (@DeactivatedAdministratorId IS NULL OR a.AdministratorId <> @DeactivatedAdministratorId)
          AND (@MfaRequired = 0 OR a.MfaStatus IN (1, 2))
          AND EXISTS
          (
              SELECT 1
              FROM core.AdministratorRole AS ar WITH (UPDLOCK, HOLDLOCK)
              INNER JOIN core.RolePermission AS rp ON rp.RoleId = ar.RoleId
              INNER JOIN core.Permission     AS p  ON p.PermissionId = rp.PermissionId
              WHERE ar.AdministratorId = a.AdministratorId
                AND p.Code = 'Administrator.Manage'
                /* Written as an explicit OR, never NOT (x = @p AND y = @q): with
                   no role being removed both parameters are NULL, the equality
                   is UNKNOWN, NOT UNKNOWN is still UNKNOWN, and every manager
                   would silently be filtered out — refusing every change. */
                AND (@RemovedRoleId IS NULL
                     OR a.AdministratorId <> @RemovedRoleAdministratorId
                     OR ar.RoleId <> @RemovedRoleId)
          )
    )
        SET @ResultCode = 0;
    ELSE
        SET @ResultCode = 1077;
END;
GO

PRINT 'admin.usp_Administrator_CheckManagerRemains deployed.';
GO
