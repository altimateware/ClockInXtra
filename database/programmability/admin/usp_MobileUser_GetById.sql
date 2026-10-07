/*==============================================================================
  admin.usp_MobileUser_GetById
  Phase : 24
  Called by: the administration portal (permission MobileUser.View), to fill
             the employee edit form.

  Purpose
  -------
  One employee's editable details and the concurrency token to save them with.
  Nothing about credentials, authenticators or devices: those have their own
  deliberate flows, and an edit form is no place to show them.

  Result codes
      0    Success
      1070 NotFound
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_MobileUser_GetById
    @MobileUserId INT,
    @ResultCode   INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT u.MobileUserId,
           u.UserId,
           u.EmployeeNumber,
           u.FirstName,
           u.LastName,
           u.Email,
           u.PhoneNumber,
           u.Department,
           u.JobTitle,
           CAST(CASE WHEN u.Status = 1 THEN 1 ELSE 0 END AS BIT) AS IsActive,
           u.[RowVersion]
    FROM core.MobileUser AS u
    WHERE u.MobileUserId = @MobileUserId;

    SET @ResultCode = CASE WHEN @@ROWCOUNT = 0 THEN 1070 ELSE 0 END;
END;
GO

PRINT 'admin.usp_MobileUser_GetById deployed.';
GO
