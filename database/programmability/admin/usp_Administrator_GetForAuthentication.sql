/*==============================================================================
  admin.usp_Administrator_GetForAuthentication
  Phase : 6
  Called by: the administration portal sign-in, and by the cookie validation
             handler on each request.

  Purpose
  -------
  Return everything needed to authenticate an administrator and to build their
  authorization claims, in one round trip: credential parameters, MFA state,
  the security stamp, and the effective permission set.

  Two result sets are returned:
      1. the administrator row
      2. the distinct permission codes granted through their roles

  Why the security stamp is here
  ------------------------------
  The portal revalidates the stamp on every request. Changing a password,
  disabling an account or altering role membership rotates the stamp, which
  invalidates every existing session immediately rather than leaving a signed
  cookie usable until it expires (threat TH-35). That only works if the stamp
  is fetched on each validation, so this procedure is called often and is kept
  to two index seeks.

  TIMING BEHAVIOUR THE CALLER MUST IMPLEMENT
  ------------------------------------------
  When no administrator matches, 1010 is returned with no rows. The caller
  must still perform a dummy password hash so that an unknown user name does
  not answer faster than a known one.

  Result codes
      0    Success
      1010 InvalidCredentials (no such administrator)
      1014 UserInactive (inactive or locked; reported to the user as invalid
           credentials so the portal does not confirm which accounts exist)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_GetForAuthentication
    @UserName   NVARCHAR(64),
    @ResultCode INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @AdministratorId INT,
            @Status          TINYINT;

    SELECT @AdministratorId = a.AdministratorId,
           @Status          = a.Status
    FROM core.Administrator AS a
    WHERE a.UserName = @UserName;

    IF @AdministratorId IS NULL
    BEGIN
        SET @ResultCode = 1010;          -- InvalidCredentials
        RETURN;
    END;

    SET @ResultCode = CASE WHEN @Status = 1 THEN 0 ELSE 1014 END;

    SELECT
        a.AdministratorId,
        a.AdministratorPublicId,
        a.UserName,
        a.DisplayName,
        a.Email,
        a.HashFormat,
        a.Iterations,
        a.Salt,
        a.PasswordHash,
        a.MustChangePassword,
        a.SecurityStamp,
        a.MfaSecretProtected,
        a.MfaStatus,
        a.MfaLastAcceptedTimeStep,
        a.Status,
        a.LastLoginUtc,
        a.[RowVersion]
    FROM core.Administrator AS a
    WHERE a.AdministratorId = @AdministratorId;

    /* Effective permissions, flattened across every assigned role. The portal
       turns these into authorization policy claims, so authorization checks
       stay centralised instead of testing role names in controllers (§42). */
    SELECT DISTINCT p.Code AS PermissionCode
    FROM core.AdministratorRole AS ar
    INNER JOIN core.RolePermission AS rp ON rp.RoleId       = ar.RoleId
    INNER JOIN core.Permission     AS p  ON p.PermissionId  = rp.PermissionId
    WHERE ar.AdministratorId = @AdministratorId
    ORDER BY p.Code;
END;
GO

PRINT 'admin.usp_Administrator_GetForAuthentication deployed.';
GO
