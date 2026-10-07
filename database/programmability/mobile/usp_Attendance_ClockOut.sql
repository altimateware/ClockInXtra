/*==============================================================================
  mobile.usp_Attendance_ClockOut
  Phase : 6
  Called by: POST /api/v1/mobile/attendance/clock-out

  Claude.md §14. As with clock-in, the signature, device binding and location
  have already been verified by the caller. By the stated requirement,
  clock-out needs no password and no TOTP code (assumption ASM-04); the
  consequence — that possession of the unlocked phone is sufficient — is
  recorded as residual risk RR-06 in the threat model.

  Guarantees
  ----------
  * The clock-out timestamp is the server's (§14: "the server must determine
    the official timestamp").
  * The open record is locked before it is examined, so two concurrent
    clock-outs cannot both close it or both compute a duration.
  * Record, event and audit entry commit together or not at all.

  Duration is computed in whole minutes from the stored clock-in time, never
  from anything the client sends.

  Transaction handling (decision DB-13)
  -------------------------------------
  The outer transaction count is captured first. This procedure opens a
  transaction only when it is the outermost caller and otherwise takes a
  savepoint, because a bare ROLLBACK would discard a caller's transaction
  along with its own work — turning an ordinary "not clocked in" answer into
  data loss for whoever called it.

  The day ends at midnight (business time)
  ----------------------------------------
  Only TODAY's record can be closed. A record left open past midnight stays
  open — it is a missing clock-out in the daily report, fixed by a correction —
  so clock-out is effectively available until 23:59:59 of the day of clock-in.

  Result codes
      0    Success
      1022 DeviceRevoked            1023 DeviceNotBoundToUser
      1043 OfficeLocationInactive   1051 NotClockedIn
      1055 AlreadyClockedOut        1052 AttendanceWindowClosed
      1053 AttendanceNotConfigured
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_Attendance_ClockOut
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
    @ClockOutUtc            DATETIME2(3)     OUTPUT,
    @DurationMinutes        INT              OUTPUT,
    @IsEarlyClockOut        BIT              OUTPUT,
    @ResultCode             INT              OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* Clear the OUTPUT parameters. This matters most for @ClockInUtc and
       @AttendancePublicId, which are populated by a SELECT that may match no
       row: T-SQL would otherwise leave the caller's previous values in place
       and a "not clocked in" result could carry someone else's timestamps. */
    SET @AttendancePublicId = NULL;
    SET @AttendanceDate     = NULL;
    SET @ClockInUtc         = NULL;
    SET @ClockOutUtc        = NULL;
    SET @DurationMinutes    = NULL;
    SET @IsEarlyClockOut    = NULL;

    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    ------------------------------------------------------------------
    -- 1. Configuration.
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

    IF @BusinessTimeZoneId IS NULL              -- OPEN-5
       OR @ClockOutOpenTime IS NULL             -- OPEN-7, explicitly required by the business
       OR @ClockOutBeforeOpenAction IS NULL     -- OPEN-7/OPEN-9
    BEGIN
        SET @ResultCode = 1053;                 -- AttendanceNotConfigured
        RETURN;
    END;

    IF @ClockOutBeforeOpenAction NOT IN ('Reject', 'AcceptAndFlagEarly')
    BEGIN
        SET @ResultCode = 1053;
        RETURN;
    END;

    ------------------------------------------------------------------
    -- 2. Server time and attendance day.
    ------------------------------------------------------------------
    DECLARE @NowUtc   DATETIME2(3) = SYSUTCDATETIME(),
            @LocalNow DATETIME2(3);

    BEGIN TRY
        SET @LocalNow = CAST(@NowUtc AT TIME ZONE 'UTC' AT TIME ZONE @BusinessTimeZoneId AS DATETIME2(3));
    END TRY
    BEGIN CATCH
        SET @ResultCode = 1053;
        RETURN;
    END CATCH;

    SET @AttendanceDate = CAST(@LocalNow AS DATE);

    ------------------------------------------------------------------
    -- 3. Clock-out window.
    ------------------------------------------------------------------
    DECLARE @DayStartLocal DATETIME2(3) = CAST(@AttendanceDate AS DATETIME2(3)),
            @OpenLocal     DATETIME2(3);

    SET @OpenLocal = DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @ClockOutOpenTime), @DayStartLocal);

    SET @IsEarlyClockOut = 0;

    IF @LocalNow < @OpenLocal
    BEGIN
        IF @ClockOutBeforeOpenAction = 'Reject'
        BEGIN
            SET @ResultCode = 1052;   -- AttendanceWindowClosed (too early to clock out)
            RETURN;
        END;

        SET @IsEarlyClockOut = 1;     -- AcceptAndFlagEarly
    END;

    ------------------------------------------------------------------
    -- 4. The transaction.
    ------------------------------------------------------------------
    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION ClockOut;

        DECLARE @DeviceStatus TINYINT, @DeviceOwner INT;

        SELECT @DeviceStatus = d.Status,
               @DeviceOwner  = d.MobileUserId
        FROM core.Device AS d WITH (UPDLOCK)
        WHERE d.DeviceId = @DeviceId;

        IF @DeviceStatus IS NULL OR @DeviceStatus = 2
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ClockOut;
            SET @ResultCode = 1022;   -- DeviceRevoked / unknown
            RETURN;
        END;

        IF @DeviceOwner <> @MobileUserId
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ClockOut;
            SET @ResultCode = 1023;   -- DeviceNotBoundToUser
            RETURN;
        END;

        IF NOT EXISTS (SELECT 1 FROM core.OfficeLocation WHERE OfficeLocationId = @OfficeLocationId AND Status = 1)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ClockOut;
            SET @ResultCode = 1043;   -- OfficeLocationInactive
            RETURN;
        END;

        /* 4a. Lock the record for today before inspecting it. */
        DECLARE @AttendanceId BIGINT,
                @Status       TINYINT;

        SELECT @AttendanceId       = a.AttendanceId,
               @Status             = a.Status,
               @ClockInUtc         = a.ClockInUtc,
               @AttendancePublicId = a.AttendancePublicId
        FROM core.Attendance AS a WITH (UPDLOCK, HOLDLOCK)
        WHERE a.MobileUserId   = @MobileUserId
          AND a.AttendanceDate = @AttendanceDate;

        IF @AttendanceId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ClockOut;
            SET @ResultCode = 1051;   -- NotClockedIn
            RETURN;
        END;

        IF @Status <> 1
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ClockOut;
            SET @ResultCode = 1055;   -- AlreadyClockedOut
            RETURN;
        END;

        /* 4a-bis. A minimum time after the employee's OWN clock-in
               (Attendance.MinimumMinutesBeforeClockOut). Unlike the opening
               time above, this is relative to each person, so it is checked
               against the record just read. Empty means no minimum. Reported
               as AttendanceWindowClosed: the employee is outside the period
               in which clock-out is permitted, and the app already explains
               that code. */
        DECLARE @MinimumMinutes INT =
            (SELECT TRY_CAST(SettingValue AS INT)
             FROM core.ApplicationSetting
             WHERE SettingKey = 'Attendance.MinimumMinutesBeforeClockOut');

        IF @MinimumMinutes > 0 AND @NowUtc < DATEADD(MINUTE, @MinimumMinutes, @ClockInUtc)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION ClockOut;
            SET @ResultCode = 1052;   -- AttendanceWindowClosed (too soon after clock-in)
            RETURN;
        END;

        /* 4b. Close the record using server time only. */
        SET @ClockOutUtc     = @NowUtc;
        SET @DurationMinutes = DATEDIFF(MINUTE, @ClockInUtc, @ClockOutUtc);

        UPDATE core.Attendance
        SET ClockOutUtc              = @ClockOutUtc,
            DurationMinutes          = @DurationMinutes,
            Status                   = 2,               -- Closed
            ClockOutOfficeLocationId = @OfficeLocationId,
            IsEarlyClockOut          = @IsEarlyClockOut,
            UpdatedUtc               = @NowUtc
        WHERE AttendanceId = @AttendanceId;

        INSERT INTO core.AttendanceEvent
            (AttendanceId, EventType, OccurredUtc, MobileUserId, DeviceId, OfficeLocationId,
             DistanceMeters, ReportedAccuracyMeters, Platform, WasMockedLocation,
             CoordinatesProtected, CorrelationId, CreatedUtc)
        VALUES
            (@AttendanceId, 2, @ClockOutUtc, @MobileUserId, @DeviceId, @OfficeLocationId,
             @DistanceMeters, @ReportedAccuracyMeters, @Platform, @WasMockedLocation,
             @CoordinatesProtected, @CorrelationId, @NowUtc);

        DECLARE @AuditResult INT;

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Attendance.ClockOut',
            @ActorType         = 1,
            @ActorId           = @MobileUserId,
            @SubjectType       = 'Attendance',
            @SubjectId         = @AttendancePublicId,
            @Result            = 1,
            @ReasonCode        = NULL,
            @SourceApplication = 'Attendance.Api',
            @CorrelationId     = @CorrelationId,
            @Details           = NULL,
            @OccurredUtc       = @NowUtc,
            @ResultCode        = @AuditResult OUTPUT;

        IF @OuterTranCount = 0
            COMMIT TRANSACTION;

        SET @ResultCode = 0;
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
            ROLLBACK TRANSACTION ClockOut;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'mobile.usp_Attendance_ClockOut deployed.';
GO
