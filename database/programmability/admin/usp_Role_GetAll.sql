/*==============================================================================
  admin.usp_Role_GetAll
  Phase : 6 (added during Phase 12 completion)
  Called by: the administration portal (permission Administrator.View), to list
             roles with their permissions and to offer roles for assignment.

  Purpose
  -------
  Return every role and exactly what each one grants.

  Returns two result sets
  -----------------------
  1. One row per role.
  2. One row per (role, permission).

  Why an administrator must be able to see permissions, not just role names
  -------------------------------------------------------------------------
  A role name is a label; the permissions are the authority. "Attendance
  Administrator" sounds narrow and can approve and revoke devices. Showing
  the permission list beside every role is what lets someone assigning it know
  what they are actually handing over.

  AssignableByActor
  -----------------
  When @ActingAdministratorId is given, each role says whether that
  administrator could grant it — whether every permission it carries is one they
  already hold. The portal uses this to avoid offering a role that
  usp_Administrator_SetRole would refuse. It is a courtesy, not the control: the
  refusal in usp_Administrator_SetRole is what actually prevents escalation.

  Result codes
      0    Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Role_GetAll
    @ActingAdministratorId INT = NULL,
    @ResultCode            INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT r.RoleId,
           r.Name,
           r.Description,
           r.IsSystemRole,
           CAST(CASE
                    WHEN @ActingAdministratorId IS NULL THEN 0
                    WHEN EXISTS
                    (
                        SELECT 1
                        FROM core.RolePermission AS granted
                        WHERE granted.RoleId = r.RoleId
                          AND NOT EXISTS
                          (
                              SELECT 1
                              FROM core.AdministratorRole AS ar
                              INNER JOIN core.RolePermission AS held ON held.RoleId = ar.RoleId
                              WHERE ar.AdministratorId = @ActingAdministratorId
                                AND held.PermissionId = granted.PermissionId
                          )
                    ) THEN 0
                    ELSE 1
                END AS BIT) AS AssignableByActor
    FROM core.Role AS r
    ORDER BY r.Name;

    SELECT rp.RoleId,
           p.Code,
           p.Category,
           p.Description
    FROM core.RolePermission AS rp
    INNER JOIN core.Permission AS p ON p.PermissionId = rp.PermissionId
    ORDER BY rp.RoleId, p.Category, p.Code;

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_Role_GetAll deployed.';
GO
