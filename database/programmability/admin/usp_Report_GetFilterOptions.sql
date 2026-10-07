/*==============================================================================
  admin.usp_Report_GetFilterOptions
  Phase : 24
  Called by: the administration portal (permission Report.View or Audit.View),
             to offer report filters as pick-lists instead of blank text boxes.

  Purpose
  -------
  The values a report filter can usefully take: which employees exist, which
  departments are in use, and (for the audit trail) which event types have
  been recorded. The portal offers them as suggestions; typing is still
  allowed, and the report procedures validate what they receive.

  What it reveals
  ---------------
  Nothing a report viewer cannot already see: the attendance report itself
  lists employees' user ids, names and departments. No contact details, no
  status, no credentials.

  Result sets
      1  Employees:   UserId, DisplayName  (active and inactive: a report
                      covers past days, and a leaver still has history)
      2  Departments: Department            (distinct, non-blank)
      3  Event types: EventType             (only when @IncludeEventTypes = 1;
                      read from the audit ledger's EventType index, so it is
                      not paid for on every attendance report)

  Result codes
      0    Success
      1001 InvalidRequest (@MaxEmployees outside 1..20000)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Report_GetFilterOptions
    @MaxEmployees      INT = 5000,
    @IncludeEventTypes BIT = 0,
    @ResultCode        INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @MaxEmployees IS NULL OR @MaxEmployees < 1 OR @MaxEmployees > 20000
    BEGIN
        SET @ResultCode = 1001;
        RETURN;
    END;

    SELECT TOP (@MaxEmployees)
           m.UserId,
           LTRIM(RTRIM(CONCAT(m.FirstName, N' ', m.LastName))) AS DisplayName
    FROM core.MobileUser AS m
    ORDER BY m.LastName, m.FirstName, m.UserId;

    SELECT DISTINCT LTRIM(RTRIM(m.Department)) AS Department
    FROM core.MobileUser AS m
    WHERE m.Department IS NOT NULL AND LEN(LTRIM(RTRIM(m.Department))) > 0
    ORDER BY Department;

    IF @IncludeEventTypes = 1
    BEGIN
        SELECT DISTINCT a.EventType
        FROM audit.AuditLog AS a
        ORDER BY a.EventType;
    END;

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_Report_GetFilterOptions deployed.';
GO
