/*==============================================================================
  admin.usp_Administrator_CheckAuthorityOver
  Phase : 6 (added during Phase 12 completion)
  Called by: every administrator-management procedure, before it changes
             anything. Not called by the portal directly.

  Purpose
  -------
  Decide whether one administrator may act on another. One rule, in one place,
  so the procedures that change status, roles, passwords and authenticators
  cannot drift apart on the thing that matters most about them.

  THE RULE
  --------
  1. The acting administrator exists and is active. A disabled account must not
     act, even if a session somehow survived the security-stamp check.
  2. The target exists.
  3. Nobody acts on themselves. Deactivating your own account, stripping your
     own roles, resetting your own password without knowing the current one, or
     moving your own authenticator are all things a stolen session would want
     to do. They go through a colleague — separation of duties (§42).
     Changing your OWN password knowing the current one is a different path:
     usp_Administrator_ChangePassword.
  4. Nobody acts on an administrator who holds a permission they lack. Without
     this, someone holding only Administrator.Manage could reset a Super
     Administrator's password, sign in as them, and inherit every permission —
     the same escalation usp_Administrator_Create already refuses (TH-37). The
     comparison is over effective permissions, not role names, so renaming or
     re-scoping a role cannot slip past it.

  Read-only: it takes no locks beyond the caller's transaction and writes no
  audit row. The caller audits the outcome, because only the caller knows what
  was attempted.

  Result codes
      0    Authorised
      1070 NotFound (acting administrator missing or inactive, or target missing)
      1074 SeparationOfDutiesViolation (acting on yourself)
      1003 Forbidden (target holds a permission the actor does not)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_CheckAuthorityOver
    @ActingAdministratorId INT,
    @TargetAdministratorId INT,
    @ResultCode            INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM core.Administrator
                   WHERE AdministratorId = @ActingAdministratorId AND Status = 1)
    BEGIN
        SET @ResultCode = 1070;
        RETURN;
    END;

    IF NOT EXISTS (SELECT 1 FROM core.Administrator WHERE AdministratorId = @TargetAdministratorId)
    BEGIN
        SET @ResultCode = 1070;
        RETURN;
    END;

    IF @ActingAdministratorId = @TargetAdministratorId
    BEGIN
        SET @ResultCode = 1074;
        RETURN;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM core.AdministratorRole AS targetRole
        INNER JOIN core.RolePermission AS targetHeld ON targetHeld.RoleId = targetRole.RoleId
        WHERE targetRole.AdministratorId = @TargetAdministratorId
          AND NOT EXISTS
          (
              SELECT 1
              FROM core.AdministratorRole AS actorRole
              INNER JOIN core.RolePermission AS actorHeld ON actorHeld.RoleId = actorRole.RoleId
              WHERE actorRole.AdministratorId = @ActingAdministratorId
                AND actorHeld.PermissionId = targetHeld.PermissionId
          )
    )
    BEGIN
        SET @ResultCode = 1003;
        RETURN;
    END;

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_Administrator_CheckAuthorityOver deployed.';
GO
