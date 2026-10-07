/*==============================================================================
  admin.ufn_Report_ValidationFailureCategory
  Phase : 19
  Called by: admin.usp_Report_GetValidationFailures; the smoke tests.

  Purpose
  -------
  Classifies one security event as a validation failure, or not at all. This
  is the contract between the code that WRITES refusal events and the report
  that reads them, so it lives in one place that can be tested on its own —
  the report procedure returns two result sets, which a T-SQL test cannot
  capture, but this function can be asserted directly for every event shape.

  Classification is by event type, which each writer sets deliberately.
  Reason is consulted only where one event type spans two categories: a
  request signature refused because the device is revoked is a DEVICE failure,
  not a request-integrity one. An event type that is not listed is not a
  failure — a successful sign-in, a password change — and returns no row, so a
  new kind of success can never appear in a failure report.

      Category  Event types
      1 Location   Location.Rejected, Location.ValidationFailed
      2 Device     Device.RegistrationRefused, Device.AttestationRejected,
                   Signature.Rejected when the reason is the device's state
      3 Identity   Auth.Failed, Auth.Locked, Auth.PasswordChangeFailed,
                   Auth.PasswordChangeLocked, Mfa.Missing, Mfa.SecretUnreadable,
                   Signature.Rejected when the employee is inactive
      4 Integrity  Signature.Rejected otherwise (replay, skew, digest, forgery),
                   Signature.FailureBudgetExhausted

  A new refusal event type must be added here, with a smoke test.

  Returns
      Zero rows (not a failure) or one row: Category TINYINT.
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER FUNCTION admin.ufn_Report_ValidationFailureCategory
(
    @EventType  VARCHAR(64),
    @ReasonCode VARCHAR(64)
)
RETURNS TABLE
AS
RETURN
    SELECT TOP (1) m.Category
    FROM
    (
        VALUES
            ('Location.Rejected',          CAST(NULL AS VARCHAR(64)), CAST(1 AS TINYINT)),
            ('Location.ValidationFailed',  NULL, 1),

            ('Device.RegistrationRefused', NULL, 2),
            ('Device.AttestationRejected', NULL, 2),
            ('Signature.Rejected', 'DeviceRevoked',         2),
            ('Signature.Rejected', 'DeviceNotApproved',     2),
            ('Signature.Rejected', 'DeviceNotRegistered',   2),
            ('Signature.Rejected', 'DEVICE_NOT_REGISTERED', 2),

            ('Auth.Failed',                NULL, 3),
            ('Auth.Locked',                NULL, 3),
            ('Auth.PasswordChangeFailed',  NULL, 3),
            ('Auth.PasswordChangeLocked',  NULL, 3),
            ('Mfa.Missing',                NULL, 3),
            ('Mfa.SecretUnreadable',       NULL, 3),
            ('Signature.Rejected', 'UserInactive',          3),

            ('Signature.Rejected',         NULL, 4),

            -- One address produced so many unverifiable requests that individual
            -- refusals stopped being recorded. It belongs in the report: it is
            -- the marker saying the count below it is no longer complete.
            ('Signature.FailureBudgetExhausted', NULL, 4)
    ) AS m (EventType, ReasonCode, Category)
    WHERE m.EventType = @EventType
      AND (m.ReasonCode = @ReasonCode OR m.ReasonCode IS NULL)
    -- A reason-specific row outranks the event type's default.
    ORDER BY CASE WHEN m.ReasonCode IS NULL THEN 1 ELSE 0 END;
GO

PRINT 'admin.ufn_Report_ValidationFailureCategory deployed.';
GO
