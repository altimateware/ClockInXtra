/*==============================================================================
  job.usp_Maintenance_VerifyLedger
  Phase : 20
  Called by: ops/Test-LedgerIntegrity.ps1, on a schedule and on demand
             (for example before audit evidence is handed to an investigator).

  Purpose
  -------
  Verify the audit ledger against digests previously exported by
  job.usp_Maintenance_GenerateLedgerDigest and kept outside the database.

  What a pass and a failure mean
  ------------------------------
  A pass means every ledger row, and every block up to the newest digest
  supplied, hashes to what was recorded when that digest was taken: no audit
  or security-event row has been altered, removed or inserted out of order
  since. A failure means someone changed the ledger outside the application
  — the incident TH-40 describes. It is reported, never repaired: the evidence
  of tampering is the thing to preserve.

  @Digests is a JSON ARRAY of digest documents, exactly as exported. Supplying
  several verifies the chain through each of them.

  Why EXECUTE AS OWNER
  --------------------
  sys.sp_verify_database_ledger requires VIEW LEDGER CONTENT, which is not
  granted to any application account. Wrapping it keeps the maintenance
  account's rights to EXECUTE on the job schema.

  Returns
      @FailureMessage  SQL Server's own description when verification fails
  Result codes
      0    Verified
      1001 InvalidRequest       (no digests, or not a JSON array)
      1080 LedgerVerificationFailed
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE job.usp_Maintenance_VerifyLedger
    @Digests        NVARCHAR(MAX),
    @FailureMessage NVARCHAR(4000) OUTPUT,
    @ResultCode     INT            OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;

    SET @FailureMessage = NULL;

    IF @Digests IS NULL OR ISJSON(@Digests) = 0 OR LEFT(LTRIM(@Digests), 1) <> N'['
    BEGIN
        SET @ResultCode = 1001;          -- InvalidRequest
        RETURN;
    END;

    BEGIN TRY
        /* Called directly, not through INSERT … EXEC: that form runs inside an
           implicit transaction, and verification refuses to run in one. Its
           result set (the last block verified) therefore reaches the caller,
           which may log it; the outcome is @ResultCode. */
        EXEC sys.sp_verify_database_ledger @Digests;

        SET @ResultCode = 0;
    END TRY
    BEGIN CATCH
        SET @FailureMessage = ERROR_MESSAGE();
        SET @ResultCode = 1080;          -- LedgerVerificationFailed
    END CATCH;
END;
GO

PRINT 'job.usp_Maintenance_VerifyLedger deployed.';
GO
