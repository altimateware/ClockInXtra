/*==============================================================================
  mobile.usp_Attendance_ClockIn
  Phase : 6
  Called by: POST /api/v1/mobile/attendance/clock-in

  This is the integrity heart of the system (Claude.md §12, §28, §30, §45).

  What the caller has ALREADY done before reaching here
  -----------------------------------------------------
      1. verified the RFC 9421 request signature and the nonce (not replayed)
      2. confirmed the device is Active and bound to this employee
      3. verified the password and the TOTP code, and consumed the time step
      4. validated the location and resolved the matched office
      5. claimed the idempotency key
  Those steps are expensive or belong in the application; they are not
  repeated here. What this procedure owns is everything that must be decided
  atomically against the database.

  What this procedure guarantees
  ------------------------------
  * The timestamp is the server's, taken inside the transaction (§31). No
    client clock is trusted.
  * The attendance day is derived in SQL from the configured business
    timezone, so all application servers agree (decision DB-05).
  * Exactly one record can exist per employee per attendance day, enforced by
    UX_Attendance_MobileUserId_AttendanceDate together with a held key-range
    lock. Two simultaneous requests on different IIS nodes cannot both
    succeed (§45).
  * Either an attendance record AND its event AND its audit entry are all
    written, or nothing is (§30). There is no partial record.

  Transaction handling (decision DB-13)
  -------------------------------------
  A bare ROLLBACK TRANSACTION in T-SQL rolls back EVERY nesting level and sets
  @@TRANCOUNT to 0. If this procedure were called inside a caller's
  transaction, a routine business rejection such as ALREADY_CLOCKED_IN would
  silently destroy that caller's work. So the outer transaction count is
  captured first: this procedure begins a transaction only when it is
  outermost, and otherwise takes a savepoint and rolls back to it.

  Business rules NOT invented here
  --------------------------------
  If the closing time or its after-close action is unset, the procedure
  REFUSES with 1053 rather than assuming that late clock-ins are fine (or not
  fine). That is required by §15 and §68.

  Result codes
      0    Success
      1022 DeviceRevoked            1023 DeviceNotBoundToUser
      1014 UserInactive             1043 OfficeLocationInactive
      1050 AlreadyClockedIn         1052 AttendanceWindowClosed
      1053 AttendanceNotConfigured  1054 AttendanceOperationFailed
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_Attendance_ClockIn
    @MobileUserId           INT,
    @DeviceId               INT,
    @OfficeLocationId       INT,
    @DistanceMeters         DECIMAL(8, 2),
    @ReportedAccuracyMeters DECIMAL(8, 2),
    @Platform               TINYINT,
    @WasMockedLocation      BIT              = 0,
    @CoordinatesProtected   VARBINARY(MAX)   = NULL,
    @CorrelationId          UNIQUEIDENTIFIER = NULL,
    @AttendancePublicId     UNIQUEIDENTIFIER OUTPUT,
    @AttendanceDate         DATE             OUTPUT,
    @ClockInUtc             DATETIME2(3)     OUTPUT,
    @IsLateClockIn          BIT              OUTPUT,
    @ResultCode             INT              OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* Clear the OUTPUT parameters: on any early return the caller must not be
       handed values left over from a previous call. */
    SET @AttendancePublicId = NULL;
    SET @AttendanceDate     = NULL;
    SET @ClockInUtc         = NULL;
    SET @IsLateClockIn      = NULL;

    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    ------------------------------------------------------------------
    -- 1. Configuration. Refuse rather than guess.
    ------------------------------------------------------------------
    DECLARE @BusinessTimeZoneId       SYSNAME,
            @ClockInOpenTime          TIME(0),
            @ClockInCloseTime         TIME(0),
            @ClockInAfterCloseAction  VARCHAR(32),
            @ClockOutOpenTime         TIME(0),
            @ClockOutBeforeOpenAction VARCHAR(32),
            @GracePeriodMinutes       INT,
            @SettingsResult           INT;

    EXEC core.usp_ApplicationSetting_GetAttendanceRules
        @BusinessTimeZoneId       = @BusinessTimeZoneId       OUTPUT,
        @ClockInOpenTime          = @ClockInOpenTime          OUTPUT,
        @ClockInCloseTime         = @ClockInCloseTime         OUTPUT,
        @ClockInAfterCloseAction  = @ClockInAfterCloseAction  OUTPUT,
        @ClockOutOpenTime         = @ClockOutOpenTime         OUTPUT,
        @ClockOutBeforeOpenAction = @ClockOutBeforeOpenAction OUTPUT,
        @GracePeriodMinutes       = @GracePeriodMinutes       OUTPUT,
        @ResultCode               = @SettingsResult           OUTPUT;

    IF @BusinessTimeZoneId IS NULL          -- OPEN-5
       OR @ClockInCloseTime IS NULL         -- OPEN-6, explicitly required by the business
       OR @ClockInAfterCloseAction IS NULL  -- OPEN-6/OPEN-8
    BEGIN
        SET @ResultCode = 1053;             -- AttendanceNotConfigured
        RETURN;
    END;

    IF @ClockInAfterCloseAction NOT IN ('Reject', 'AcceptAndFlagLate')
    BEGIN
        /* A value outside the allowed set is a misconfiguration, not a
           licence to pick a behaviour. */
        SET @ResultCode = 1053;
        RETURN;
    END;

    ------------------------------------------------------------------
    -- 2. Server time and the business-local attendance day.
    ------------------------------------------------------------------
    DECLARE @NowUtc   DATETIME2(3) = SYSUTCDATETIME(),
            @LocalNow DATETIME2(3);

    BEGIN TRY
        SET @LocalNow = CAST(@NowUtc AT TIME ZONE 'UTC' AT TIME ZONE @BusinessTimeZoneId AS DATETIME2(3));
    END TRY
    BEGIN CATCH
        SET @ResultCode = 1053;   -- invalid time zone identifier (error 9820)
        RETURN;
    END CATCH;

    SET @AttendanceDate = CAST(@LocalNow AS DATE);

    ------------------------------------------------------------------
    -- 3. Attendance window.
    --    Times are combined with the attendance date so that arithmetic
    --    cannot wrap around midnight unnoticed.
    ------------------------------------------------------------------
    DECLARE @DayStartLocal DATETIME2(3) = CAST(@AttendanceDate AS DATETIME2(3)),
            @OpenLocal     DATETIME2(3),
            @CloseLocal    DATETIME2(3);

    IF @ClockInOpenTime IS NOT NULL
        SET @OpenLocal = DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @ClockInOpenTime), @DayStartLocal);

    SET @CloseLocal = DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @ClockInCloseTime), @DayStartLocal);
    SET @CloseLocal = DATEADD(MINUTE, COALESCE(@GracePeriodMinutes, 0), @CloseLocal);

    /* An opening time is optional: the stated requirement asks only for when
       clock-in ENDS. NULL therefore means no earliest-time restriction, and
       that reading is documented rather than silently assumed. */
    IF @OpenLocal IS NOT NULL AND @LocalNow < @OpenLocal
    BEGIN
        SET @ResultCode = 1052;   -- AttendanceWindowClosed (too early)
        RETURN;
    END;

    SET @IsLateClockIn = 0;

    IF @LocalNow > @CloseLocal
    BEGIN
        IF @ClockInAfterCloseAction = 'Reject'
        BEGIN
            SET @ResultCode = 1052;   -- AttendanceWindowClosed (too late)
            RETURN;
        END;

        SET @IsLateClockIn = 1;       -- AcceptAndFlagLate
    END;

    ------------------------------------------------------------------
    -- 4. The transaction.
    ------------------------------------------------------------------
    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION ClockIn;

        /* 4a. The device must still be active and still bound to this
               employee. UPDLOCK serialises against a concurrent revocation. */
        DECLARE @DeviceStatus TINYINT, @DeviceOwner INT;

        SELECT @DeviceStatus = d.Status,
               @DeviceOwner  = d.MobileUserId
        FROM core.Device AS d WITH (UPDLOCK)
        WHERE d.DeviceId = @DeviceId;

        IF @DeviceStatus IS NULL OR @DeviceStatus = 2
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ClockIn;
            SET @ResultCode = 1022;   -- DeviceRevoked / unknown
            RETURN;
        END;

        IF @DeviceOwner <> @MobileUserId
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ClockIn;
            SET @ResultCode = 1023;   -- DeviceNotBoundToUser
            RETURN;
        END;

        /* 4b. The employee must still be active. */
        IF NOT EXISTS (SELECT 1 FROM core.MobileUser WHERE MobileUserId = @MobileUserId AND Status = 1)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ClockIn;
            SET @ResultCode = 1014;   -- UserInactive
            RETURN;
        END;

        /* 4c. The office must still be active: it may have been disabled
               between the location check and this transaction. */
        IF NOT EXISTS (SELECT 1 FROM core.OfficeLocation WHERE OfficeLocationId = @OfficeLocationId AND Status = 1)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ClockIn;
            SET @ResultCode = 1043;   -- OfficeLocationInactive
            RETURN;
        END;

        /* 4d. Hold the key range for (employee, attendance date). HOLDLOCK
               keeps the range locked for the rest of the transaction, so a
               concurrent request on another node blocks here instead of
               inserting a second record. */
        IF EXISTS
        (
            SELECT 1
            FROM core.Attendance WITH (UPDLOCK, HOLDLOCK)
            WHERE MobileUserId   = @MobileUserId
              AND AttendanceDate = @AttendanceDate
        )
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ClockIn;
            SET @ResultCode = 1050;   -- AlreadyClockedIn
            RETURN;
        END;

        /* 4e. Create the record. */
        SET @AttendancePublicId = NEWID();
        SET @ClockInUtc         = @NowUtc;

        DECLARE @AttendanceId BIGINT;

        INSERT INTO core.Attendance
            (AttendancePublicId, MobileUserId, AttendanceDate, ClockInUtc, Status,
             ClockInOfficeLocationId, IsLateClockIn, CreatedUtc)
        VALUES
            (@AttendancePublicId, @MobileUserId, @AttendanceDate, @ClockInUtc, 1,
             @OfficeLocationId, @IsLateClockIn, @NowUtc);

        SET @AttendanceId = SCOPE_IDENTITY();

        /* 4f. Evidence for this transaction. */
        INSERT INTO core.AttendanceEvent
            (AttendanceId, EventType, OccurredUtc, MobileUserId, DeviceId, OfficeLocationId,
             DistanceMeters, ReportedAccuracyMeters, Platform, WasMockedLocation,
             CoordinatesProtected, CorrelationId, CreatedUtc)
        VALUES
            (@AttendanceId, 1, @ClockInUtc, @MobileUserId, @DeviceId, @OfficeLocationId,
             @DistanceMeters, @ReportedAccuracyMeters, @Platform, @WasMockedLocation,
             @CoordinatesProtected, @CorrelationId, @NowUtc);

        /* 4g. Audit entry, inside the same transaction (§30). */
        DECLARE @AuditResult INT;

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Attendance.ClockIn',
            @ActorType         = 1,                       -- MobileUser
            @ActorId           = @MobileUserId,
            @SubjectType       = 'Attendance',
            @SubjectId         = @AttendancePublicId,
            @Result            = 1,                       -- Success
            @ReasonCode        = NULL,
            @SourceApplication = 'Attendance.Api',
            @CorrelationId     = @CorrelationId,
            @Details           = NULL,
            @OccurredUtc       = @NowUtc,
            @ResultCode        = @AuditResult OUTPUT;

        /* Only the outermost caller commits: an inner call leaves the
           decision to whoever opened the transaction. */
        IF @OuterTranCount = 0
            COMMIT TRANSACTION;

        SET @ResultCode = 0;
    END TRY
    BEGIN CATCH
        /* A doomed transaction (XACT_STATE = -1) cannot be released with a
           savepoint, so when someone else owns the transaction the error must
           propagate and let them abort. */
        IF XACT_STATE() = -1 AND @OuterTranCount > 0
            THROW;

        IF @OuterTranCount = 0
        BEGIN
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        END
        ELSE IF XACT_STATE() = 1
        BEGIN
            ROLLBACK TRANSACTION ClockIn;
        END;

        /* The unique index is the real guarantee; if a concurrent request won
           the race, report the business outcome rather than a SQL error
           (§34: never expose SQL errors). */
        IF ERROR_NUMBER() IN (2627, 2601)
        BEGIN
            SET @ResultCode = 1050;   -- AlreadyClockedIn
            RETURN;
        END;

        THROW;   -- genuinely unexpected: the API logs it and returns INTERNAL_ERROR
    END CATCH;
END;
GO

PRINT 'mobile.usp_Attendance_ClockIn deployed.';
GO
