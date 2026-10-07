/*==============================================================================
  job.usp_Maintenance_GenerateLedgerDigest
  Phase : 20
  Called by: ops/Export-LedgerDigest.ps1, run on a schedule by the maintenance
             account (app_jobs).

  Purpose
  -------
  Produce a database ledger digest: a small JSON document holding the hash of
  the latest block of ledger transactions. It covers audit.AuditLog and
  audit.SecurityEvent (decision DB-08).

  Why the digest has to leave the database
  ----------------------------------------
  Ledger tables are tamper-EVIDENT, not tamper-proof. Someone with control of
  the server can alter the data files and recompute the hashes stored inside
  the database; what they cannot do is alter a digest that was already copied
  somewhere they do not control. Verification (job.usp_Maintenance_VerifyLedger)
  compares the database against those copies. A digest kept only in this
  database proves nothing — which is why this procedure returns the digest to
  the caller instead of storing it, and the export script writes it to
  organisation-controlled write-once storage (threat TH-40). SQL Server's own
  automatic digest storage targets Azure immutable blob storage, which §2.1
  rules out.

  Why EXECUTE AS OWNER
  --------------------
  sys.sp_generate_database_ledger_digest is documented as requiring the
  GENERATE LEDGER DIGEST permission. (On the development instance, SQL Server
  2022 16.0.1200, a login-less test user without that grant could run it —
  generating a digest reads nothing but a hash, so this is not a leak.)
  Running as the owner makes the procedure work whichever behaviour a given
  instance has, and keeps the maintenance account's only right on the
  database EXECUTE on the job schema, so everything it can do is listed in
  this folder.

  Returns
      @Digest    the digest JSON (NULL if the database has no ledger blocks yet)
  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE job.usp_Maintenance_GenerateLedgerDigest
    @Digest     NVARCHAR(MAX) OUTPUT,
    @ResultCode INT           OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Latest TABLE (LatestDigest NVARCHAR(MAX) NULL);

    INSERT INTO @Latest (LatestDigest)
    EXEC sys.sp_generate_database_ledger_digest;

    SELECT TOP (1) @Digest = LatestDigest FROM @Latest;

    SET @ResultCode = 0;
END;
GO

PRINT 'job.usp_Maintenance_GenerateLedgerDigest deployed.';
GO
