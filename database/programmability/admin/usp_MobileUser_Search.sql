/*==============================================================================
  admin.usp_MobileUser_Search
  Phase : 6
  Called by: the administration portal (permission MobileUser.View).

  Purpose
  -------
  List and search employees, with the operational state an administrator needs
  to act on: whether the person can actually clock in today (§17).

  Why the readiness columns are here
  ----------------------------------
  An employee account is only usable when three things are true: the account is
  active, an authenticator is enrolled and activated, and an approved device is
  bound to them. Any one of those missing means the person cannot clock in, and
  they will discover it at the office door.

  Rather than making an administrator open three screens to find that out, the
  list returns HasActiveDevice, HasActiveMfa and a derived CanClockIn. The
  portal can then show who is not ready before the employee finds out the hard
  way. The flags are computed from the same conditions the mobile procedures
  enforce, so the list cannot claim someone is ready when the API would refuse
  them.

  Filtering without dynamic SQL (DB-11)
  -------------------------------------
  Optional parameters with OPTION (RECOMPILE). @SearchTerm matches the user id,
  employee number, first or last name. It is a plain parameter compared with
  LIKE — never concatenated into a statement — so the usual injection risk of a
  search box does not exist here (§49).

  Result codes
      0    Success
      1001 InvalidRequest
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_MobileUser_Search
    @SearchTerm   NVARCHAR(128) = NULL,
    @Department   NVARCHAR(120) = NULL,
    @Status       TINYINT       = NULL,
    @OnlyNotReady BIT           = 0,
    @PageNumber   INT           = 1,
    @PageSize     INT           = 50,
    @TotalCount   INT OUTPUT,
    @ResultCode   INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SET @TotalCount = 0;

    IF @PageNumber IS NULL OR @PageNumber < 1
       OR @PageSize IS NULL OR @PageSize < 1 OR @PageSize > 500
       OR (@Status IS NOT NULL AND @Status NOT IN (0, 1, 2))
    BEGIN
        SET @ResultCode = 1001;          -- InvalidRequest
        RETURN;
    END;

    /* Escape LIKE wildcards so that a search for "100%" looks for that text
       rather than matching everything. */
    DECLARE @Pattern NVARCHAR(140) = NULL;

    IF @SearchTerm IS NOT NULL AND LEN(LTRIM(RTRIM(@SearchTerm))) > 0
    BEGIN
        SET @Pattern = N'%' +
            REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(@SearchTerm)), N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]') +
            N'%';
    END;

    ;WITH Matched AS
    (
        SELECT
            u.MobileUserId,
            u.MobileUserPublicId,
            u.UserId,
            u.EmployeeNumber,
            u.FirstName,
            u.LastName,
            u.Email,
            u.PhoneNumber,
            u.Department,
            u.JobTitle,
            u.Status,
            u.CreatedUtc,
            u.UpdatedUtc,
            u.[RowVersion],
            CAST(CASE WHEN EXISTS (SELECT 1 FROM core.Device AS d
                                   WHERE d.MobileUserId = u.MobileUserId AND d.Status = 1)
                      THEN 1 ELSE 0 END AS BIT) AS HasActiveDevice,
            CAST(CASE WHEN EXISTS (SELECT 1 FROM core.Device AS d
                                   WHERE d.MobileUserId = u.MobileUserId AND d.Status = 0)
                      THEN 1 ELSE 0 END AS BIT) AS HasDeviceAwaitingApproval,
            CAST(CASE WHEN EXISTS (SELECT 1 FROM core.MfaCredential AS m
                                   WHERE m.MobileUserId = u.MobileUserId AND m.Status = 1)
                      THEN 1 ELSE 0 END AS BIT) AS HasActiveMfa,
            CAST(CASE WHEN EXISTS (SELECT 1 FROM core.EmployeeCredential AS c
                                   WHERE c.MobileUserId = u.MobileUserId)
                      THEN 1 ELSE 0 END AS BIT) AS HasCredential
        FROM core.MobileUser AS u
        WHERE (@Status     IS NULL OR u.Status = @Status)
          AND (@Department IS NULL OR u.Department = @Department)
          AND (@Pattern    IS NULL
               OR u.UserId         LIKE @Pattern
               OR u.EmployeeNumber LIKE @Pattern
               OR u.FirstName      LIKE @Pattern
               OR u.LastName       LIKE @Pattern)
    ),
    Ready AS
    (
        SELECT m.*,
               CAST(CASE WHEN m.Status = 1 AND m.HasActiveDevice = 1
                          AND m.HasActiveMfa = 1 AND m.HasCredential = 1
                         THEN 1 ELSE 0 END AS BIT) AS CanClockIn
        FROM Matched AS m
    )
    SELECT @TotalCount = COUNT(*)
    FROM Ready
    WHERE (@OnlyNotReady = 0 OR CanClockIn = 0)
    OPTION (RECOMPILE);

    ;WITH Matched AS
    (
        SELECT
            u.MobileUserId,
            u.MobileUserPublicId,
            u.UserId,
            u.EmployeeNumber,
            u.FirstName,
            u.LastName,
            u.Email,
            u.PhoneNumber,
            u.Department,
            u.JobTitle,
            u.Status,
            u.CreatedUtc,
            u.UpdatedUtc,
            u.[RowVersion],
            CAST(CASE WHEN EXISTS (SELECT 1 FROM core.Device AS d
                                   WHERE d.MobileUserId = u.MobileUserId AND d.Status = 1)
                      THEN 1 ELSE 0 END AS BIT) AS HasActiveDevice,
            CAST(CASE WHEN EXISTS (SELECT 1 FROM core.Device AS d
                                   WHERE d.MobileUserId = u.MobileUserId AND d.Status = 0)
                      THEN 1 ELSE 0 END AS BIT) AS HasDeviceAwaitingApproval,
            CAST(CASE WHEN EXISTS (SELECT 1 FROM core.MfaCredential AS m
                                   WHERE m.MobileUserId = u.MobileUserId AND m.Status = 1)
                      THEN 1 ELSE 0 END AS BIT) AS HasActiveMfa,
            CAST(CASE WHEN EXISTS (SELECT 1 FROM core.EmployeeCredential AS c
                                   WHERE c.MobileUserId = u.MobileUserId)
                      THEN 1 ELSE 0 END AS BIT) AS HasCredential
        FROM core.MobileUser AS u
        WHERE (@Status     IS NULL OR u.Status = @Status)
          AND (@Department IS NULL OR u.Department = @Department)
          AND (@Pattern    IS NULL
               OR u.UserId         LIKE @Pattern
               OR u.EmployeeNumber LIKE @Pattern
               OR u.FirstName      LIKE @Pattern
               OR u.LastName       LIKE @Pattern)
    ),
    Ready AS
    (
        SELECT m.*,
               CAST(CASE WHEN m.Status = 1 AND m.HasActiveDevice = 1
                          AND m.HasActiveMfa = 1 AND m.HasCredential = 1
                         THEN 1 ELSE 0 END AS BIT) AS CanClockIn
        FROM Matched AS m
    )
    SELECT
        MobileUserId,
        MobileUserPublicId,
        UserId,
        EmployeeNumber,
        FirstName,
        LastName,
        Email,
        PhoneNumber,
        Department,
        JobTitle,
        Status,
        HasCredential,
        HasActiveMfa,
        HasActiveDevice,
        HasDeviceAwaitingApproval,
        CanClockIn,
        CreatedUtc,
        UpdatedUtc,
        [RowVersion]
    FROM Ready
    WHERE (@OnlyNotReady = 0 OR CanClockIn = 0)
    ORDER BY LastName, FirstName, MobileUserId
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY
    OPTION (RECOMPILE);

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_MobileUser_Search deployed.';
GO
