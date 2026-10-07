/*==============================================================================
  mobile.usp_Device_Register
  Phase : 6
  Called by: POST /api/v1/mobile/device/register

  This is the only mobile endpoint that accepts a password from a device that
  is not yet registered, which makes it the system's main brute-force surface
  (threat TH-14). The caller has already:

      1. verified the password and the TOTP code, and consumed the time step
      2. verified the platform attestation (Android key attestation, or Apple
         App Attest if OPEN-33 is approved) and established the level reached
      3. verified that the request was signed by the private key matching the
         public key being registered — proof of possession
      4. checked and updated the lockout counters

  What this procedure owns
  ------------------------
  * Consuming the challenge exactly once, atomically. The UPDATE carries the
    condition, so two concurrent registrations cannot both consume it
    (threat TH-10).
  * Refusing a public key that is already bound to a device.
  * Applying DEC-04: one active device per employee.

  Why some refusals still keep their work
  ---------------------------------------
  Once a challenge has been consumed it must STAY consumed, even when the
  registration is then refused — otherwise a rejected attempt would hand the
  challenge back for another try, which is precisely the replay this design
  prevents. Those paths therefore commit (when outermost) rather than roll
  back. Only the "challenge was never valid" path unwinds.

  On DEC-04 and approval
  ----------------------
  When approval is required (the default, OPEN-32), the device is created as
  PendingApproval and the administrator's approval is what revokes the
  employee's previous device.

  When approval is NOT required, this procedure still refuses to displace an
  existing active device, returning 1024. Silently replacing it would mean
  that a phished password and one OTP were enough to move an employee's
  attendance to an attacker's phone with no human in the loop. Replacing a
  device is therefore always an administrative act.

  Transaction handling (decision DB-13)
  -------------------------------------
  The outer transaction count is captured first, so a rejection rolls back to
  a savepoint rather than discarding a caller's transaction.

  Result codes
      0    Success (device Active)
      1062 RegistrationPendingApproval (device created, awaiting approval)
      1060 ChallengeInvalid          1061 ChallengeExpired
      1063 PublicKeyAlreadyRegistered
      1024 ActiveDeviceAlreadyExists 1014 UserInactive
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_Device_Register
    @ChallengeId         UNIQUEIDENTIFIER,
    @Challenge           VARBINARY(32),
    @MobileUserId        INT,
    @PublicKey           VARBINARY(65),
    @PublicKeyThumbprint VARBINARY(32),
    @Platform            TINYINT,
    @AttestationLevel    TINYINT,
    @DeviceModel         NVARCHAR(64)     = NULL,
    @OsVersion           NVARCHAR(32)     = NULL,
    @AppVersion          NVARCHAR(32)     = NULL,
    @RequiresApproval    BIT,
    @CorrelationId       UNIQUEIDENTIFIER = NULL,
    @DevicePublicId      UNIQUEIDENTIFIER OUTPUT,
    @DeviceStatus        TINYINT          OUTPUT,
    @ResultCode          INT              OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();

    /* Clear the OUTPUT parameters so that a refused registration cannot hand
       back an identifier from an earlier call. */
    SET @DevicePublicId = NULL;
    SET @DeviceStatus   = NULL;

    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION DeviceRegister;

        ------------------------------------------------------------------
        -- 1. Consume the challenge. The WHERE clause is the guard, so the
        --    check and the consumption cannot be separated by a race.
        ------------------------------------------------------------------
        DECLARE @Consumed TABLE (Challenge VARBINARY(32), ExpiresUtc DATETIME2(3));

        UPDATE core.DeviceRegistrationChallenge
        SET ConsumedUtc = @NowUtc
        OUTPUT deleted.Challenge, deleted.ExpiresUtc INTO @Consumed (Challenge, ExpiresUtc)
        WHERE ChallengeId = @ChallengeId
          AND ConsumedUtc IS NULL;

        DECLARE @StoredChallenge VARBINARY(32),
                @ExpiresUtc      DATETIME2(3);

        SELECT @StoredChallenge = c.Challenge,
               @ExpiresUtc      = c.ExpiresUtc
        FROM @Consumed AS c;

        IF @StoredChallenge IS NULL
        BEGIN
            /* Nothing was consumed, so there is nothing to preserve. */
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION DeviceRegister;
            SET @ResultCode = 1060;    -- ChallengeInvalid: unknown or already consumed
            RETURN;
        END;

        IF @ExpiresUtc <= @NowUtc
        BEGIN
            /* The challenge is consumed either way: an expired challenge must
               not remain available for another attempt. */
            IF @OuterTranCount = 0 COMMIT TRANSACTION;
            SET @ResultCode = 1061;    -- ChallengeExpired
            RETURN;
        END;

        IF @StoredChallenge <> @Challenge
        BEGIN
            IF @OuterTranCount = 0 COMMIT TRANSACTION;
            SET @ResultCode = 1060;    -- ChallengeInvalid: value does not match
            RETURN;
        END;

        ------------------------------------------------------------------
        -- 2. The employee must be active.
        ------------------------------------------------------------------
        IF NOT EXISTS (SELECT 1 FROM core.MobileUser WHERE MobileUserId = @MobileUserId AND Status = 1)
        BEGIN
            IF @OuterTranCount = 0 COMMIT TRANSACTION;   -- keep the challenge consumed
            SET @ResultCode = 1014;    -- UserInactive
            RETURN;
        END;

        ------------------------------------------------------------------
        -- 3. The key must not already be registered anywhere.
        ------------------------------------------------------------------
        IF EXISTS (SELECT 1 FROM core.Device WHERE PublicKeyThumbprint = @PublicKeyThumbprint)
        BEGIN
            IF @OuterTranCount = 0 COMMIT TRANSACTION;
            SET @ResultCode = 1063;    -- PublicKeyAlreadyRegistered
            RETURN;
        END;

        ------------------------------------------------------------------
        -- 4. DEC-04: one active device per employee.
        ------------------------------------------------------------------
        SET @DeviceStatus = CASE WHEN @RequiresApproval = 1 THEN 0 ELSE 1 END;

        IF @DeviceStatus = 1
           AND EXISTS (SELECT 1 FROM core.Device WITH (UPDLOCK, HOLDLOCK)
                       WHERE MobileUserId = @MobileUserId AND Status = 1)
        BEGIN
            IF @OuterTranCount = 0 COMMIT TRANSACTION;
            SET @ResultCode = 1024;    -- ActiveDeviceAlreadyExists: an administrator must act
            RETURN;
        END;

        ------------------------------------------------------------------
        -- 5. Create the device record.
        ------------------------------------------------------------------
        SET @DevicePublicId = NEWID();

        INSERT INTO core.Device
            (DevicePublicId, MobileUserId, PublicKey, PublicKeyThumbprint, Platform,
             AttestationLevel, Status, DeviceModel, OsVersion, AppVersion,
             RegisteredUtc, ApprovedUtc)
        VALUES
            (@DevicePublicId, @MobileUserId, @PublicKey, @PublicKeyThumbprint, @Platform,
             @AttestationLevel, @DeviceStatus, @DeviceModel, @OsVersion, @AppVersion,
             @NowUtc, CASE WHEN @DeviceStatus = 1 THEN @NowUtc END);

        DECLARE @AuditResult INT;

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Device.Registered',
            @ActorType         = 1,                    -- MobileUser
            @ActorId           = @MobileUserId,
            @SubjectType       = 'Device',
            @SubjectId         = @DevicePublicId,
            @Result            = 1,
            @ReasonCode        = NULL,
            @SourceApplication = 'Attendance.Api',
            @CorrelationId     = @CorrelationId,
            @DevicePublicId    = @DevicePublicId,
            @Details           = NULL,
            @OccurredUtc       = @NowUtc,
            @ResultCode        = @AuditResult OUTPUT;

        IF @OuterTranCount = 0
            COMMIT TRANSACTION;

        SET @ResultCode = CASE WHEN @DeviceStatus = 0 THEN 1062 ELSE 0 END;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() = -1 AND @OuterTranCount > 0
            THROW;

        IF @OuterTranCount = 0
        BEGIN
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        END
        ELSE IF XACT_STATE() = 1
        BEGIN
            ROLLBACK TRANSACTION DeviceRegister;
        END;

        /* The filtered unique index UX_Device_ActiveUser is the real
           guarantee behind DEC-04; report the business outcome, not a SQL
           error. */
        IF ERROR_NUMBER() IN (2627, 2601)
        BEGIN
            SET @ResultCode = 1024;    -- ActiveDeviceAlreadyExists
            RETURN;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'mobile.usp_Device_Register deployed.';
GO
