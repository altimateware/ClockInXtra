/*==============================================================================
  mobile.usp_Idempotency_TryBegin
  Phase : 6
  Called by: clock-in and clock-out, BEFORE the attendance transaction.

  Purpose
  -------
  Make retries safe (Claude.md §53). If the client times out after the server
  has already recorded a clock-in, the retry must not create a second record.

  How it works
  ------------
  The INSERT itself claims the key. Three outcomes follow:

    * insert succeeds      -> this is the first attempt; proceed
    * key exists, same
      request, completed   -> return the original outcome verbatim
    * key exists, same
      request, in flight   -> the first attempt is still running; do not start
                              a second attendance transaction
    * key exists, but the
      request differs      -> the key is being reused for something else,
                              which is refused rather than silently answered
                              with an unrelated stored result

  The request hash is what makes the distinction possible. It is computed by
  the API over the meaningful request fields, so an honest retry of the same
  operation hashes identically.

  Result codes
      0    Success — proceed with the operation
      1032 IdempotencyKeyReuse — same key, different request
      1033 IdempotentReplay — completed; @StoredResultCode/@StoredPayload hold
           the original answer
      1034 IdempotentInProgress — the original attempt has not finished
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_Idempotency_TryBegin
    @DeviceId         INT,
    @IdempotencyKey   UNIQUEIDENTIFIER,
    @EndpointCode     TINYINT,               -- 1 ClockIn, 2 ClockOut
    @RequestHash      VARBINARY(32),
    @StoredResultCode INT            OUTPUT,
    @StoredPayload    NVARCHAR(MAX)  OUTPUT,
    @ResultCode       INT            OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    /* A duplicate key is an expected outcome here, so it must not doom a
       caller's transaction. */
    SET XACT_ABORT OFF;

    SET @StoredResultCode = NULL;
    SET @StoredPayload    = NULL;

    BEGIN TRY
        INSERT INTO core.RequestIdempotency
            (DeviceId, IdempotencyKey, EndpointCode, RequestHash, State, CreatedUtc)
        VALUES
            (@DeviceId, @IdempotencyKey, @EndpointCode, @RequestHash, 1, SYSUTCDATETIME());

        SET @ResultCode = 0;        -- first attempt, proceed
        RETURN;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() NOT IN (2627, 2601)
            THROW;                  -- unexpected failure
    END CATCH;

    /* The key already exists: decide which of the three cases applies. */
    DECLARE @ExistingHash     VARBINARY(32),
            @ExistingState    TINYINT,
            @ExistingEndpoint TINYINT;

    SELECT @ExistingHash     = i.RequestHash,
           @ExistingState    = i.State,
           @ExistingEndpoint = i.EndpointCode,
           @StoredResultCode = i.ResultCode,
           @StoredPayload    = i.ResponsePayload
    FROM core.RequestIdempotency AS i
    WHERE i.DeviceId       = @DeviceId
      AND i.IdempotencyKey = @IdempotencyKey;

    IF @ExistingHash IS NULL
    BEGIN
        /* The row vanished between the failed insert and this read, which can
           only happen if the retention purge removed it. Treat it as a fresh
           attempt rather than failing the employee's clock-in. */
        SET @ResultCode = 0;
        RETURN;
    END;

    IF @ExistingEndpoint <> @EndpointCode OR @ExistingHash <> @RequestHash
    BEGIN
        SET @StoredResultCode = NULL;
        SET @StoredPayload    = NULL;
        SET @ResultCode = 1032;     -- IdempotencyKeyReuse
        RETURN;
    END;

    IF @ExistingState = 2
        SET @ResultCode = 1033;     -- IdempotentReplay: return the original answer
    ELSE
        SET @ResultCode = 1034;     -- IdempotentInProgress
END;
GO

PRINT 'mobile.usp_Idempotency_TryBegin deployed.';
GO
