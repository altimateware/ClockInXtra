/*==============================================================================
  admin.usp_Attendance_RequestCorrection
  Phase : 6
  Called by: the administration portal (permission Attendance.Correct).

  Purpose
  -------
  Record a request to correct an attendance record (§15, §20, §32).

  WHO MAY DO THIS (DEC-08, DEC-10)
  --------------------------------
  Corrections are permitted and need a second administrator's approval
  (DEC-08). Attendance.Correct, the permission this procedure serves, is
  granted to Attendance Administrator; Attendance.ApproveCorrection is granted
  to Super Administrator, and no seeded role holds both (DEC-10).

  The switch remains a switch: Attendance.AllowCorrections is read on every
  call, and while it is anything other than 'true' this procedure refuses with
  1075. Turning corrections off is a settings change, not a deployment.

  The original record is never overwritten in place
  -------------------------------------------------
  A correction stores the original and the corrected timestamps side by side,
  and applying one writes a new AttendanceEvent of type CorrectionApplied.
  An auditor can always reconstruct what the record said before anyone
  touched it, and the append-only audit ledger records who touched it.

  Separation of duties is enforced by the database
  ------------------------------------------------
  CK_AttendanceCorrection_SeparationOfDuties prevents the approver from being
  the requester. When approval is NOT required, the correction is applied
  immediately with no approver recorded, which is materially different from
  self-approval and is visible as such in the audit trail.

  Transaction handling (decision DB-13)
  -------------------------------------
  Applying a correction is several writes — the correction row, the attendance
  record and its event — that must not be half-applied. The outer transaction
  count is captured so a refusal rolls back to a savepoint rather than
  discarding a caller's transaction.

  Result codes
      0    Success (correction recorded; applied immediately when approval is
           not required)
      1070 NotFound
      1071 ConcurrencyConflict (a correction to this record already awaits approval)
      1075 CorrectionsDisabled (OPEN-10 unanswered, or corrections switched off)
      1001 InvalidRequest (no reason, nothing to change, clock-out before clock-in,
           a time outside the record's attendance day, or in the future)
      1053 AttendanceNotConfigured (approval policy undecided, OPEN-12)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Attendance_RequestCorrection
    @AttendanceId          BIGINT       = NULL,   -- internal key, or ...
    @AttendancePublicId    UNIQUEIDENTIFIER = NULL, -- ... the public id the portal shows
    @CorrectedClockInUtc   DATETIME2(3) = NULL,
    @CorrectedClockOutUtc  DATETIME2(3) = NULL,
    @CorrectedClockInLocal  DATETIME2(3) = NULL,  -- business-local alternatives, converted
    @CorrectedClockOutLocal DATETIME2(3) = NULL,  -- with Attendance.BusinessTimeZoneId
    @Reason                NVARCHAR(512),
    @AdministratorId       INT,
    @CorrelationId         UNIQUEIDENTIFIER = NULL,
    @AttendanceCorrectionId BIGINT OUTPUT,
    @Applied               BIT    OUTPUT,
    @ResultCode            INT    OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @AttendanceCorrectionId = NULL;
    SET @Applied = 0;

    /* Exactly one way of naming the record, and at most one form of each time. */
    IF (@AttendanceId IS NULL AND @AttendancePublicId IS NULL)
       OR (@AttendanceId IS NOT NULL AND @AttendancePublicId IS NOT NULL)
       OR (@CorrectedClockInUtc IS NOT NULL AND @CorrectedClockInLocal IS NOT NULL)
       OR (@CorrectedClockOutUtc IS NOT NULL AND @CorrectedClockOutLocal IS NOT NULL)
    BEGIN
        SET @ResultCode = 1001;
        RETURN;
    END;

    /* The business time zone: administrators enter the times they know, which
       are local, and the attendance day is defined in this zone. Without it a
       correction cannot be checked against the day it belongs to. */
    DECLARE @BusinessTimeZoneId SYSNAME =
        (SELECT CAST(SettingValue AS SYSNAME) FROM core.ApplicationSetting
         WHERE SettingKey = 'Attendance.BusinessTimeZoneId');

    IF @BusinessTimeZoneId IS NULL
    BEGIN
        SET @ResultCode = 1053;          -- AttendanceNotConfigured
        RETURN;
    END;

    BEGIN TRY
        IF @CorrectedClockInLocal IS NOT NULL
            SET @CorrectedClockInUtc = CAST(@CorrectedClockInLocal AT TIME ZONE @BusinessTimeZoneId AT TIME ZONE 'UTC' AS DATETIME2(3));
        IF @CorrectedClockOutLocal IS NOT NULL
            SET @CorrectedClockOutUtc = CAST(@CorrectedClockOutLocal AT TIME ZONE @BusinessTimeZoneId AT TIME ZONE 'UTC' AS DATETIME2(3));
    END TRY
    BEGIN CATCH
        SET @ResultCode = 1053;          -- the configured zone is not valid on this instance
        RETURN;
    END CATCH;

    IF @AttendanceId IS NULL
        SELECT @AttendanceId = AttendanceId FROM core.Attendance WHERE AttendancePublicId = @AttendancePublicId;

    IF @AttendanceId IS NULL
    BEGIN
        SET @ResultCode = 1070;          -- NotFound
        RETURN;
    END;

    IF @Reason IS NULL OR LEN(LTRIM(RTRIM(@Reason))) = 0
       OR (@CorrectedClockInUtc IS NULL AND @CorrectedClockOutUtc IS NULL)
    BEGIN
        SET @ResultCode = 1001;          -- InvalidRequest: nothing to correct, or no reason given
        RETURN;
    END;

    ------------------------------------------------------------------
    -- Is this feature switched on at all?
    ------------------------------------------------------------------
    DECLARE @AllowCorrections NVARCHAR(400),
            @RequireApproval  NVARCHAR(400);

    SELECT @AllowCorrections = SettingValue
    FROM core.ApplicationSetting WHERE SettingKey = 'Attendance.AllowCorrections';

    SELECT @RequireApproval = SettingValue
    FROM core.ApplicationSetting WHERE SettingKey = 'Attendance.CorrectionsRequireApproval';

    IF @AllowCorrections IS NULL OR @AllowCorrections <> N'true'
    BEGIN
        SET @ResultCode = 1075;          -- CorrectionsDisabled
        RETURN;
    END;

    /* Corrections are permitted, but whether they need approval has not been
       decided. Refuse rather than choosing on the business's behalf. */
    IF @RequireApproval IS NULL
    BEGIN
        SET @ResultCode = 1053;          -- AttendanceNotConfigured
        RETURN;
    END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION CorrectionRequest;

        DECLARE @MobileUserId     INT,
                @AttendancePubId  UNIQUEIDENTIFIER,
                @OriginalIn       DATETIME2(3),
                @OriginalOut      DATETIME2(3),
                @AttendanceStatus TINYINT,
                @AttendanceDate   DATE;

        SELECT @AttendanceDate   = a.AttendanceDate,
               @MobileUserId     = a.MobileUserId,
               @AttendancePubId  = a.AttendancePublicId,
               @OriginalIn       = a.ClockInUtc,
               @OriginalOut      = a.ClockOutUtc,
               @AttendanceStatus = a.Status
        FROM core.Attendance AS a WITH (UPDLOCK, HOLDLOCK)
        WHERE a.AttendanceId = @AttendanceId;

        IF @AttendancePubId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION CorrectionRequest;
            SET @ResultCode = 1070;      -- NotFound
            RETURN;
        END;

        DECLARE @NewIn  DATETIME2(3) = COALESCE(@CorrectedClockInUtc, @OriginalIn),
                @NewOut DATETIME2(3) = COALESCE(@CorrectedClockOutUtc, @OriginalOut);

        /* One correction at a time per record: two pending requests would be
           approved independently and the second would silently overwrite the
           first. The earlier request must be decided first. */
        IF EXISTS (SELECT 1 FROM core.AttendanceCorrection WITH (UPDLOCK, HOLDLOCK)
                   WHERE AttendanceId = @AttendanceId AND Status = 1)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION CorrectionRequest;
            SET @ResultCode = 1071;      -- ConcurrencyConflict: a correction is already awaiting approval
            RETURN;
        END;

        /* A correction fixes the times within the record's own attendance day
           (ASM-03: the day is the business-local date of the clock-in, and
           DEC-06: clock-out ends at 23:59 that day). Moving a record to
           another day would be a different record, not a correction. Nor can a
           correction record a time that has not happened yet. */
        IF CAST(@NewIn AT TIME ZONE 'UTC' AT TIME ZONE @BusinessTimeZoneId AS DATE) <> @AttendanceDate
           OR (@NewOut IS NOT NULL
               AND CAST(@NewOut AT TIME ZONE 'UTC' AT TIME ZONE @BusinessTimeZoneId AS DATE) <> @AttendanceDate)
           OR @NewIn > @NowUtc
           OR @NewOut > @NowUtc
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION CorrectionRequest;
            SET @ResultCode = 1001;      -- InvalidRequest: outside the attendance day, or in the future
            RETURN;
        END;

        IF @NewOut IS NOT NULL AND @NewOut < @NewIn
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION CorrectionRequest;
            SET @ResultCode = 1001;      -- InvalidRequest: clock-out before clock-in
            RETURN;
        END;

        DECLARE @FieldChanged TINYINT =
            CASE
                WHEN @CorrectedClockInUtc IS NOT NULL AND @CorrectedClockOutUtc IS NOT NULL THEN 3
                WHEN @CorrectedClockInUtc IS NOT NULL THEN 1
                ELSE 2
            END;

        DECLARE @Status TINYINT = CASE WHEN @RequireApproval = N'true' THEN 1 ELSE 4 END;  -- 1 Requested, 4 Applied

        INSERT INTO core.AttendanceCorrection
            (AttendanceId, FieldChanged, OriginalClockInUtc, OriginalClockOutUtc,
             CorrectedClockInUtc, CorrectedClockOutUtc, Reason, Status,
             RequestedByAdministratorId, RequestedUtc, AppliedUtc)
        VALUES
            (@AttendanceId, @FieldChanged, @OriginalIn, @OriginalOut,
             @CorrectedClockInUtc, @CorrectedClockOutUtc, @Reason, @Status,
             @AdministratorId, @NowUtc,
             CASE WHEN @Status = 4 THEN @NowUtc END);

        SET @AttendanceCorrectionId = SCOPE_IDENTITY();

        ------------------------------------------------------------------
        -- Apply immediately only when the business has said approval is not
        -- required. Otherwise the record is untouched until someone else
        -- approves.
        ------------------------------------------------------------------
        IF @Status = 4
        BEGIN
            UPDATE core.Attendance
            SET ClockInUtc      = @NewIn,
                ClockOutUtc     = @NewOut,
                DurationMinutes = CASE WHEN @NewOut IS NULL THEN NULL
                                       ELSE DATEDIFF(MINUTE, @NewIn, @NewOut) END,
                Status          = 3,                 -- Corrected
                UpdatedUtc      = @NowUtc
            WHERE AttendanceId = @AttendanceId;

            /* The correction is itself an event, so the history shows the
               change rather than only its outcome. */
            INSERT INTO core.AttendanceEvent
                (AttendanceId, EventType, OccurredUtc, MobileUserId, DeviceId,
                 OfficeLocationId, CorrelationId, CreatedUtc)
            VALUES
                (@AttendanceId, 3, @NowUtc, @MobileUserId, NULL,
                 NULL, @CorrelationId, @NowUtc);

            SET @Applied = 1;
        END;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@AttendancePubId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000);

        SET @Details =
        (
            SELECT @OriginalIn           AS originalClockInUtc,
                   @OriginalOut          AS originalClockOutUtc,
                   @CorrectedClockInUtc  AS correctedClockInUtc,
                   @CorrectedClockOutUtc AS correctedClockOutUtc,
                   @Reason               AS reason,
                   @Applied              AS appliedImmediately
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Attendance.CorrectionRequested',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @SubjectType       = 'Attendance',
            @SubjectId         = @SubjectId,
            @Result            = 1,
            @SourceApplication = 'Attendance.Admin',
            @CorrelationId     = @CorrelationId,
            @Details           = @Details,
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
            ROLLBACK TRANSACTION CorrectionRequest;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Attendance_RequestCorrection deployed.';
GO
