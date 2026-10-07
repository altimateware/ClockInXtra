/*==============================================================================
  core.usp_SecurityEvent_Create
  Phase : 6
  Called by: procedures in the mobile, admin and job schemas.

  Purpose
  -------
  Record a security-relevant event: a failed authentication, a replayed
  signature, a rejected attestation, a mocked location, a lockout.

  This is where the PRECISE reason is kept. The API deliberately collapses
  several distinct failures into one response code so that an internet-facing
  endpoint cannot confirm to an attacker that a password was correct
  (conflict CON-09, OPEN-38). Investigators need the real reason, so it is
  written here — to an append-only ledger table — rather than returned.

  The source address is recorded as a salted hash, never as a stored IP
  address: correlation of abuse does not require identifying a person (§63).

  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE core.usp_SecurityEvent_Create
    @EventType         VARCHAR(64),
    @Severity          TINYINT,                   -- 1 Information, 2 Warning, 3 Critical
    @SubjectType       TINYINT,                   -- 0 Unknown, 1 MobileUser, 2 Administrator, 3 Device
    @SubjectKey        NVARCHAR(128)    = NULL,
    @DevicePublicId    UNIQUEIDENTIFIER = NULL,
    @ReasonCode        VARCHAR(64),
    @SourceApplication VARCHAR(32),
    @SourceAddressHash VARBINARY(32)    = NULL,
    @CorrelationId     UNIQUEIDENTIFIER = NULL,
    @Details           NVARCHAR(2000)   = NULL,
    @OccurredUtc       DATETIME2(3)     = NULL,
    @ResultCode        INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO audit.SecurityEvent
        (EventType, OccurredUtc, Severity, SubjectType, SubjectKey, DevicePublicId,
         ReasonCode, SourceApplication, SourceAddressHash, CorrelationId, Details)
    VALUES
        (@EventType, COALESCE(@OccurredUtc, SYSUTCDATETIME()), @Severity, @SubjectType, @SubjectKey, @DevicePublicId,
         @ReasonCode, @SourceApplication, @SourceAddressHash, @CorrelationId, @Details);

    SET @ResultCode = 0;
END;
GO

PRINT 'core.usp_SecurityEvent_Create deployed.';
GO
