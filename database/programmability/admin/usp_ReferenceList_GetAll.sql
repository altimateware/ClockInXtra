/*==============================================================================
  admin.usp_ReferenceList_GetAll
  Phase : 24
  Called by: the administration portal (permission MobileUser.View), for the
             list management page and the employee form's dropdowns.

  Purpose
  -------
  Every entry of one reference list (DEC-11), with how many employees hold it,
  so an administrator can see what a rename or a deactivation will touch.

  @List selects the list: 'Department' or 'JobTitle'. The two tables are
  queried by name in separate branches rather than by building a statement
  from the parameter (DB-11: no dynamic SQL).

  Result codes
      0    Success
      1001 InvalidRequest (unknown list)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_ReferenceList_GetAll
    @List       VARCHAR(16),
    @ResultCode INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @List = 'Department'
    BEGIN
        SELECT d.DepartmentId AS EntryId,
               d.Name,
               d.IsActive,
               (SELECT COUNT(*) FROM core.MobileUser AS m WHERE m.Department = d.Name) AS EmployeeCount,
               d.[RowVersion]
        FROM core.Department AS d
        ORDER BY d.Name;

        SET @ResultCode = 0;
        RETURN;
    END;

    IF @List = 'JobTitle'
    BEGIN
        SELECT j.JobTitleId AS EntryId,
               j.Name,
               j.IsActive,
               (SELECT COUNT(*) FROM core.MobileUser AS m WHERE m.JobTitle = j.Name) AS EmployeeCount,
               j.[RowVersion]
        FROM core.JobTitle AS j
        ORDER BY j.Name;

        SET @ResultCode = 0;
        RETURN;
    END;

    SET @ResultCode = 1001;
END;
GO

PRINT 'admin.usp_ReferenceList_GetAll deployed.';
GO
