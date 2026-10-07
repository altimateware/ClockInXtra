/*==============================================================================
  ClockInXtra — Request hygiene tables
  File  : database/schema/05_tables_request_hygiene.sql
  Phase : 5

  Contents
      core.RequestNonce          replay prevention for signed requests (§37)
      core.RequestIdempotency    safe retries for clock-in/out (§53)
      core.AuthenticationAttempt lockout counters shared by all servers

  Why these live in the database
  ------------------------------
  The ASP.NET Core rate limiter keeps its state in the memory of one process.
  In a multi-node IIS deployment that cannot enforce a security control: an
  attacker simply spreads attempts across nodes, and a replayed request can be
  sent to a different node than the original. Anything that must hold across
  the whole deployment is therefore counted here (decision TD-10, §45).
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;   -- required: IX_AuthenticationAttempt_LockedUntilUtc is filtered
SET NOCOUNT ON;
GO

/*------------------------------------------------------------------------------
  core.RequestNonce

  The replay check IS the insert: a duplicate (DeviceId, Nonce) violates the
  unique index and the procedure reports REPLAYED_REQUEST. No read-then-write
  race is possible.

  Rows survive only as long as the signature skew window allows a replay, then
  a maintenance job removes them by CreatedUtc range.
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.RequestNonce', N'U') IS NULL
BEGIN
    CREATE TABLE core.RequestNonce
    (
        DeviceId   INT           NOT NULL,
        Nonce      VARBINARY(32) NOT NULL,
        CreatedUtc DATETIME2(3)  NOT NULL
            CONSTRAINT DF_RequestNonce_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        SignatureCreatedUtc DATETIME2(3) NOT NULL,
        CONSTRAINT PK_core_RequestNonce PRIMARY KEY NONCLUSTERED (DeviceId, Nonce),
        CONSTRAINT FK_RequestNonce_Device_DeviceId
            FOREIGN KEY (DeviceId) REFERENCES core.Device (DeviceId),
        CONSTRAINT CK_RequestNonce_Nonce CHECK (DATALENGTH(Nonce) BETWEEN 8 AND 32)
    );

    /* Clustered by arrival time so purging is a range scan rather than a
       scattered delete. */
    CREATE CLUSTERED INDEX IX_RequestNonce_CreatedUtc
        ON core.RequestNonce (CreatedUtc, DeviceId);
END
GO

/*------------------------------------------------------------------------------
  core.RequestIdempotency

  RequestHash lets the server distinguish an honest retry of the same
  operation from re-use of an idempotency key for a different request, which
  is rejected rather than silently answered from the stored result.

  ResponsePayload holds the response that was returned, so a retry receives an
  identical answer. It never contains credentials: the stored payloads are
  attendance results only.
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.RequestIdempotency', N'U') IS NULL
BEGIN
    CREATE TABLE core.RequestIdempotency
    (
        DeviceId        INT              NOT NULL,
        IdempotencyKey  UNIQUEIDENTIFIER NOT NULL,
        EndpointCode    TINYINT          NOT NULL,   -- 1 ClockIn, 2 ClockOut
        RequestHash     VARBINARY(32)    NOT NULL,
        State           TINYINT          NOT NULL,   -- 1 InProgress, 2 Completed
        ResultCode      INT              NULL,
        ResponsePayload NVARCHAR(MAX)    NULL,
        CreatedUtc      DATETIME2(3)     NOT NULL
            CONSTRAINT DF_RequestIdempotency_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        CompletedUtc    DATETIME2(3)     NULL,
        CONSTRAINT PK_core_RequestIdempotency PRIMARY KEY NONCLUSTERED (DeviceId, IdempotencyKey),
        CONSTRAINT FK_RequestIdempotency_Device_DeviceId
            FOREIGN KEY (DeviceId) REFERENCES core.Device (DeviceId),
        CONSTRAINT CK_RequestIdempotency_EndpointCode CHECK (EndpointCode IN (1, 2)),
        CONSTRAINT CK_RequestIdempotency_State CHECK (State IN (1, 2)),
        CONSTRAINT CK_RequestIdempotency_Completed
            CHECK ((State = 2 AND CompletedUtc IS NOT NULL AND ResultCode IS NOT NULL)
                OR (State = 1 AND CompletedUtc IS NULL))
    );

    CREATE CLUSTERED INDEX IX_RequestIdempotency_CreatedUtc
        ON core.RequestIdempotency (CreatedUtc, DeviceId);
END
GO

/*------------------------------------------------------------------------------
  core.AuthenticationAttempt

  SubjectKey is the account identifier being protected (a mobile UserId or an
  administrator UserName). Source addresses are throttled separately at the
  reverse proxy and in the application rate limiter; this table protects the
  account itself, which is what must hold across all nodes.
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.AuthenticationAttempt', N'U') IS NULL
BEGIN
    CREATE TABLE core.AuthenticationAttempt
    (
        SubjectType    TINYINT       NOT NULL,   -- 1 MobileUser, 2 Administrator
        SubjectKey     NVARCHAR(128) NOT NULL,
        FailedCount    INT           NOT NULL
            CONSTRAINT DF_AuthenticationAttempt_FailedCount DEFAULT (0),
        FirstFailedUtc DATETIME2(3)  NULL,
        LastFailedUtc  DATETIME2(3)  NULL,
        LockedUntilUtc DATETIME2(3)  NULL,
        UpdatedUtc     DATETIME2(3)  NOT NULL
            CONSTRAINT DF_AuthenticationAttempt_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_core_AuthenticationAttempt PRIMARY KEY CLUSTERED (SubjectType, SubjectKey),
        CONSTRAINT CK_AuthenticationAttempt_SubjectType CHECK (SubjectType IN (1, 2)),
        CONSTRAINT CK_AuthenticationAttempt_FailedCount CHECK (FailedCount >= 0)
    );

    CREATE NONCLUSTERED INDEX IX_AuthenticationAttempt_LockedUntilUtc
        ON core.AuthenticationAttempt (LockedUntilUtc)
        WHERE LockedUntilUtc IS NOT NULL;
END
GO

PRINT 'Request hygiene tables ready.';
GO
