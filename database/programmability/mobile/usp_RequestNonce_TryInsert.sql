/*==============================================================================
  mobile.usp_RequestNonce_TryInsert
  Phase : 6
  Called by: the signature verification middleware, on every signed request.

  Purpose
  -------
  Replay protection for RFC 9421 signed requests (Claude.md §37). The nonce
  carried in Signature-Input must never have been seen before for this device.

  The check IS the insert. There is no "does it exist" read followed by a
  write, so two concurrent requests carrying the same nonce cannot both pass:
  one insert succeeds, the other violates the primary key and is reported as a
  replay. This is what makes the control correct across multiple IIS nodes.

  Result codes (docs/architecture/05-result-codes.md)
      0    Success
      1030 ReplayedRequest
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_RequestNonce_TryInsert
    @DeviceId            INT,
    @Nonce               VARBINARY(32),
    @SignatureCreatedUtc DATETIME2(3),
    @ResultCode          INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    /* XACT_ABORT stays OFF deliberately. A duplicate-key violation here is an
       expected outcome, not a failure, and must not doom a caller's
       transaction. The failed INSERT rolls back on its own. */
    SET XACT_ABORT OFF;

    BEGIN TRY
        INSERT INTO core.RequestNonce (DeviceId, Nonce, CreatedUtc, SignatureCreatedUtc)
        VALUES (@DeviceId, @Nonce, SYSUTCDATETIME(), @SignatureCreatedUtc);

        SET @ResultCode = 0;      -- Success
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() IN (2627, 2601)   -- PK violation / unique index violation
            SET @ResultCode = 1030;         -- ReplayedRequest
        ELSE
            THROW;                          -- unexpected: let the API log it and return INTERNAL_ERROR
    END CATCH;
END;
GO

PRINT 'mobile.usp_RequestNonce_TryInsert deployed.';
GO
