/*==============================================================================
  admin.usp_Administrator_Search
  Phase : 6 (added during Phase 12 completion)
  Called by: the administration portal (permission Administrator.View).

  Purpose
  -------
  List administrator accounts with what an administrator needs to manage them:
  status, whether they can actually sign in, and which roles they hold.

  Returns two result sets
  -----------------------
  1. One row per administrator.
  2. One row per (administrator, role) assignment, for the administrators in
     set 1.

  Two sets rather than a delimited column, so a role name containing a comma
  cannot corrupt the list, and so the portal receives role ids it can act on.

  What is deliberately NOT returned
  ---------------------------------
  Password hash, salt, iteration count, MFA secret and security stamp. This
  procedure feeds a page, and nothing on that page needs credential material.
  usp_Administrator_GetForAuthentication is the only read that returns it, and
  only for a single named account.

  CanSignIn is computed here rather than in the portal, so the rule matches
  usp_Administrator_CheckManagerRemains: active, and — unless
  Security.RequireAdministratorMfa is 'false' — an enrolled authenticator.

  Result codes
      0    Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Administrator_Search
    @SearchTerm  NVARCHAR(64) = NULL,
    @ResultCode  INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @MfaRequired BIT =
        CASE WHEN EXISTS (SELECT 1 FROM core.ApplicationSetting
                          WHERE SettingKey = 'Security.RequireAdministratorMfa'
                            AND LOWER(LTRIM(RTRIM(SettingValue))) = N'false')
             THEN 0 ELSE 1 END;

    /* The term is matched as a literal: LIKE wildcards in what the user typed
       are escaped, so searching for "a_b" finds "a_b" and not "axb". */
    DECLARE @Pattern NVARCHAR(200) =
        CASE WHEN @SearchTerm IS NULL OR LEN(LTRIM(RTRIM(@SearchTerm))) = 0 THEN NULL
             ELSE N'%' + REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(@SearchTerm)),
                        N'\', N'\\'), N'%', N'\%'), N'_', N'\_') + N'%'
        END;

    DECLARE @Matched TABLE (AdministratorId INT NOT NULL PRIMARY KEY);

    INSERT INTO @Matched (AdministratorId)
    SELECT a.AdministratorId
    FROM core.Administrator AS a
    WHERE @Pattern IS NULL
       OR a.UserName    LIKE @Pattern ESCAPE N'\'
       OR a.DisplayName LIKE @Pattern ESCAPE N'\'
       OR a.Email       LIKE @Pattern ESCAPE N'\';

    SELECT a.AdministratorId,
           a.AdministratorPublicId,
           a.UserName,
           a.DisplayName,
           a.Email,
           a.Status,
           a.MfaStatus,
           a.MustChangePassword,
           a.LastLoginUtc,
           a.CreatedUtc,
           CAST(CASE WHEN a.Status = 1 AND (@MfaRequired = 0 OR a.MfaStatus IN (1, 2))
                     THEN 1 ELSE 0 END AS BIT) AS CanSignIn
    FROM core.Administrator AS a
    INNER JOIN @Matched AS m ON m.AdministratorId = a.AdministratorId
    ORDER BY a.Status DESC, a.DisplayName, a.UserName;

    SELECT ar.AdministratorId,
           r.RoleId,
           r.Name AS RoleName
    FROM core.AdministratorRole AS ar
    INNER JOIN @Matched AS m ON m.AdministratorId = ar.AdministratorId
    INNER JOIN core.Role AS r ON r.RoleId = ar.RoleId
    ORDER BY ar.AdministratorId, r.Name;

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_Administrator_Search deployed.';
GO
