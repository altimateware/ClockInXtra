/*==============================================================================
  ClockInXtra — Seed: permissions and roles
  File  : database/seed/20_seed_permissions_roles.sql
  Phase : 5

  Re-runnable. Permissions are matched by Code, roles by Name.

  Permission codes here are the exact strings the ASP.NET Core authorization
  policies use, so authorization stays centralised rather than scattered
  through controllers (Claude.md §42).

  SEPARATION OF DUTIES (DEC-08, DEC-10)
  -------------------------------------
  Corrections are permitted and require approval by a second administrator
  (DEC-08). Who does which (DEC-10): Attendance Administrators request,
  Super Administrators approve. So:

    * Attendance Administrator gains Attendance.Correct, and NOT
      Attendance.ApproveCorrection: they raise requests, they never approve.
    * Super Administrator gains Attendance.ApproveCorrection.

  Why Super Administrator also keeps Attendance.Correct
  -----------------------------------------------------
  An administrator may only grant a role whose every permission they already
  hold (admin.usp_Administrator_SetRole, result 1003). If Super Administrator
  lacked Attendance.Correct, nobody could ever assign the Attendance
  Administrator role, because Administrator.Manage belongs to Super
  Administrator alone. Holding the permission is what makes the role
  grantable; it is not an invitation to use it.

  The rule that actually separates the duties is enforced in the database and
  cannot be granted away: admin.usp_Attendance_ApproveCorrection and
  CK_AttendanceCorrection_SeparationOfDuties both refuse an approval by the
  administrator who requested it.
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;   -- required for DML against tables carrying filtered indexes
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

/*------------------------------------------------------------------ permissions */
DECLARE @Permission TABLE
(
    Code        VARCHAR(64)   NOT NULL PRIMARY KEY,
    Category    NVARCHAR(64)  NOT NULL,
    Description NVARCHAR(256) NOT NULL
);

INSERT INTO @Permission (Code, Category, Description)
VALUES
    ('MobileUser.View',              N'Users',      N'View employee records'),
    ('MobileUser.Manage',            N'Users',      N'Create, edit, activate and deactivate employees'),
    ('Administrator.View',           N'Users',      N'View administrator accounts'),
    ('Administrator.Manage',         N'Users',      N'Create, edit, activate and deactivate administrators'),
    ('Role.Manage',                  N'Users',      N'Create roles and assign permissions'),
    ('Device.View',                  N'Devices',    N'View registered devices'),
    ('Device.Approve',               N'Devices',    N'Approve a pending device registration'),
    ('Device.Revoke',                N'Devices',    N'Revoke a registered device'),
    ('OfficeLocation.View',          N'Locations',  N'View office locations'),
    ('OfficeLocation.Manage',        N'Locations',  N'Create, edit, enable and disable office locations'),
    ('Setting.View',                 N'Settings',   N'View application settings'),
    ('Setting.Manage',               N'Settings',   N'Change and confirm application settings'),
    ('Mfa.Enrol',                    N'Security',   N'Start authenticator enrolment for an employee'),
    ('Mfa.Reset',                    N'Security',   N'Revoke and re-issue an authenticator enrolment'),
    ('Attendance.View',              N'Attendance', N'View attendance records and reports'),
    ('Attendance.Correct',           N'Attendance', N'Request a correction to an attendance record'),
    ('Attendance.ApproveCorrection', N'Attendance', N'Approve or reject a requested attendance correction'),
    ('Report.View',                  N'Reporting',  N'Run attendance and exception reports'),
    ('Audit.View',                   N'Audit',      N'View the audit trail and security events');

INSERT INTO core.Permission (Code, Category, Description)
SELECT p.Code, p.Category, p.Description
FROM @Permission AS p
WHERE NOT EXISTS (SELECT 1 FROM core.Permission AS e WHERE e.Code = p.Code);

/* Keep descriptions in step when they are revised in a later release. */
UPDATE e
SET e.Category    = p.Category,
    e.Description = p.Description
FROM core.Permission AS e
INNER JOIN @Permission AS p ON p.Code = e.Code
WHERE e.Category <> p.Category
   OR e.Description <> p.Description;

/*----------------------------------------------------------------------- roles */
DECLARE @Role TABLE
(
    Name        NVARCHAR(64)  NOT NULL PRIMARY KEY,
    Description NVARCHAR(256) NOT NULL
);

INSERT INTO @Role (Name, Description)
VALUES
    (N'Super Administrator',      N'Full administrative control, including roles and settings'),
    (N'Attendance Administrator', N'Attendance records, reports and device approval'),
    (N'User Administrator',       N'Employee records, authenticator enrolment and device management'),
    (N'Location Administrator',   N'Office location configuration'),
    (N'Report Viewer',            N'Read-only access to attendance reports'),
    (N'Auditor',                  N'Read-only access to the audit trail and security events');

INSERT INTO core.Role (Name, Description, IsSystemRole)
SELECT r.Name, r.Description, 1
FROM @Role AS r
WHERE NOT EXISTS (SELECT 1 FROM core.Role AS e WHERE e.Name = r.Name);

/*------------------------------------------------------------ role → permission */
DECLARE @RolePermission TABLE
(
    RoleName       NVARCHAR(64) NOT NULL,
    PermissionCode VARCHAR(64)  NOT NULL,
    PRIMARY KEY (RoleName, PermissionCode)
);

/* Super Administrator receives every permission, which is also what keeps
   every other seeded role grantable (see the header). */
INSERT INTO @RolePermission (RoleName, PermissionCode)
SELECT N'Super Administrator', p.Code
FROM @Permission AS p;

INSERT INTO @RolePermission (RoleName, PermissionCode)
VALUES
    (N'Attendance Administrator', 'Attendance.View'),
    (N'Attendance Administrator', 'Attendance.Correct'),
    (N'Attendance Administrator', 'Report.View'),
    (N'Attendance Administrator', 'Device.View'),
    (N'Attendance Administrator', 'Device.Approve'),
    (N'Attendance Administrator', 'Device.Revoke'),
    (N'Attendance Administrator', 'MobileUser.View'),
    (N'Attendance Administrator', 'OfficeLocation.View'),

    (N'User Administrator',       'MobileUser.View'),
    (N'User Administrator',       'MobileUser.Manage'),
    (N'User Administrator',       'Device.View'),
    (N'User Administrator',       'Device.Approve'),
    (N'User Administrator',       'Device.Revoke'),
    (N'User Administrator',       'Mfa.Enrol'),
    (N'User Administrator',       'Mfa.Reset'),

    (N'Location Administrator',   'OfficeLocation.View'),
    (N'Location Administrator',   'OfficeLocation.Manage'),

    (N'Report Viewer',            'Report.View'),
    (N'Report Viewer',            'Attendance.View'),

    (N'Auditor',                  'Audit.View'),
    (N'Auditor',                  'Attendance.View'),
    (N'Auditor',                  'Report.View'),
    (N'Auditor',                  'Device.View'),
    (N'Auditor',                  'OfficeLocation.View'),
    (N'Auditor',                  'Setting.View');

INSERT INTO core.RolePermission (RoleId, PermissionId)
SELECT r.RoleId, p.PermissionId
FROM @RolePermission AS rp
INNER JOIN core.Role       AS r ON r.Name = rp.RoleName
INNER JOIN core.Permission AS p ON p.Code = rp.PermissionCode
WHERE NOT EXISTS (SELECT 1
                  FROM core.RolePermission AS e
                  WHERE e.RoleId = r.RoleId AND e.PermissionId = p.PermissionId);

COMMIT TRANSACTION;
GO

PRINT 'Permissions and roles seeded.';
GO
