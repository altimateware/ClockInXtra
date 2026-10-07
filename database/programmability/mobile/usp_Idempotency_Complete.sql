/*==============================================================================
  mobile.usp_Idempotency_Complete
  Phase : 6
  Called by: clock-in and clock-out, after the attendance transaction has
             finished — whether it succeeded or failed with a business
             outcome.

  Purpose
  -------
  Store the outcome so that a retry carrying the same idempotency key receives
  the same answer instead of attempting the operation again (§53).

  Business failures are recorded too. If the first attempt was refused with
  ALREADY_CLOCKED_IN, the retry must be told the same thing rather than being
  processed afresh.

  What must NOT be stored in @ResponsePayload: anything secret. The payload is
  the attendance response the client already received — times, duration,
  status. No credentials ever pass through here.

  Result codes
      0 Success (a missing row is not an error: the purge job may have removed
        it, and the operation itself already completed)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_Idempotency_Complete
    @DeviceId          INT,
    @IdempotencyKey    UNIQUEIDENTIFIER,
    @OperationResult   INT,
    @ResponsePayload   NVARCHAR(MAX) = NULL,
    @ResultCode        INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE core.RequestIdempotency
    SET State           = 2,
        ResultCode      = @OperationResult,
        ResponsePayload = @ResponsePayload,
        CompletedUtc    = SYSUTCDATETIME()
    WHERE DeviceId       = @DeviceId
      AND IdempotencyKey = @IdempotencyKey
      AND State          = 1;

    SET @ResultCode = 0;
END;
GO

PRINT 'mobile.usp_Idempotency_Complete deployed.';
GO
