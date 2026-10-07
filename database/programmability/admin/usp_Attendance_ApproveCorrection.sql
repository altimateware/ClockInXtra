/*==============================================================================
  admin.usp_Attendance_ApproveCorrection
  Phase : 6
  Called by: the administration portal (permission Attendance.ApproveCorrection).

  Purpose
  -------
  Approve or reject a requested attendance correction, and apply it to the
  record when approved (§15, §20, §32).

  The permission it serves, Attendance.ApproveCorrection, is granted to Super
  Administrator only; the requesting permission belongs to Attendance
  Administrator (DEC-10), so a request and its approval come from two roles as
  well as two people.

  Separation of duties is enforced twice, on purpose
  --------------------------------------------------
  This procedure refuses when the approver is the requester, and
  CK_AttendanceCorrection_SeparationOfDuties refuses it again at the table.
  The check here produces a clean 1074 for the portal to display; the
  constraint is what makes the rule true regardless of which code path, script
  or future procedure writes the row. Application checks are convenience;
  constraints are the guarantee.

  Approving an attendance correction changes a record of when someone worked,
  which may affect pay. Both the decision and the resulting values are written
  to the append-only audit ledger, and the original timestamps stay on the
  correction row forever.

  Transaction handling (decision DB-13)
  -------------------------------------
  Approval updates the correction, the attendance record and its event
  together. The outer transaction count is captured so that a refusal — most
  importantly the separation-of-duties refusal — rolls back to a savepoint
  instead of discarding a caller's transaction.

  Result codes
      0    Success (approved and applied, or rejected)
      1070 NotFound
      1001 InvalidRequest (already decided, or nothing to apply)
      1071 ConcurrencyConflict
      1074 SeparationOfDutiesViolation
      1075 CorrectionsDisabled
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Attendance_ApproveCorrection
    @AttendanceCorrectionId BIGINT,
    @Approve                BIT,
    @DecisionNote           NVARCHAR(512) = NULL,
    @RowVersion             BINARY(8),
    @AdministratorId        INT,
    @CorrelationId          UNIQUEIDENTIFIER = NULL,
    @ResultCode             INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @AllowCorrections NVARCHAR(400);

    SELECT @AllowCorrections = SettingValue
    FROM core.ApplicationSetting WHERE SettingKey = 'Attendance.AllowCorrections';

    IF @AllowCorrections IS NULL OR @AllowCorrections <> N'true'
    BEGIN
        SET @ResultCode = 1075;          -- CorrectionsDisabled
        RETURN;
    END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION CorrectionApprove;

        DECLARE @AttendanceId    BIGINT,
                @Status          TINYINT,
                @RequestedBy     INT,
                @CorrectedIn     DATETIME2(3),
                @CorrectedOut    DATETIME2(3),
                @OriginalIn      DATETIME2(3),
                @OriginalOut     DATETIME2(3),
                @CurrentVersion  BINARY(8);

        SELECT @AttendanceId   = c.AttendanceId,
               @Status         = c.Status,
               @RequestedBy    = c.RequestedByAdministratorId,
               @CorrectedIn    = c.CorrectedClockInUtc,
               @CorrectedOut   = c.CorrectedClockOutUtc,
               @OriginalIn     = c.OriginalClockInUtc,
               @OriginalOut    = c.OriginalClockOutUtc,
               @CurrentVersion = c.[RowVersion]
        FROM core.AttendanceCorrection AS c WITH (UPDLOCK, HOLDLOCK)
        WHERE c.AttendanceCorrectionId = @AttendanceCorrectionId;

        IF @AttendanceId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION CorrectionApprove;
            SET @ResultCode = 1070;      -- NotFound
            RETURN;
        END;

        IF @CurrentVersion <> @RowVersion
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION CorrectionApprove;
            SET @ResultCode = 1071;      -- ConcurrencyConflict
            RETURN;
        END;

        IF @Status <> 1                  -- only a Requested correction can be decided
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION CorrectionApprove;
            SET @ResultCode = 1001;      -- InvalidRequest
            RETURN;
        END;

        IF @RequestedBy = @AdministratorId
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION CorrectionApprove;
            SET @ResultCode = 1074;      -- SeparationOfDutiesViolation
            RETURN;
        END;

        DECLARE @MobileUserId    INT,
                @AttendancePubId UNIQUEIDENTIFIER;

        SELECT @MobileUserId    = a.MobileUserId,
               @AttendancePubId = a.AttendancePublicId
        FROM core.Attendance AS a WITH (UPDLOCK)
        WHERE a.AttendanceId = @AttendanceId;

        IF @Approve = 1
        BEGIN
            DECLARE @NewIn  DATETIME2(3) = COALESCE(@CorrectedIn, @OriginalIn),
                    @NewOut DATETIME2(3) = COALESCE(@CorrectedOut, @OriginalOut);

            UPDATE core.Attendance
            SET ClockInUtc      = @NewIn,
                ClockOutUtc     = @NewOut,
                DurationMinutes = CASE WHEN @NewOut IS NULL THEN NULL
                                       ELSE DATEDIFF(MINUTE, @NewIn, @NewOut) END,
                Status          = 3,                 -- Corrected
                UpdatedUtc      = @NowUtc
            WHERE AttendanceId = @AttendanceId;

            INSERT INTO core.AttendanceEvent
                (AttendanceId, EventType, OccurredUtc, MobileUserId, DeviceId,
                 OfficeLocationId, CorrelationId, CreatedUtc)
            VALUES
                (@AttendanceId, 3, @NowUtc, @MobileUserId, NULL, NULL, @CorrelationId, @NowUtc);

            UPDATE core.AttendanceCorrection
            SET Status                    = 4,       -- Applied
                ApprovedByAdministratorId = @AdministratorId,
                DecidedUtc                = @NowUtc,
                DecisionNote              = @DecisionNote,
                AppliedUtc                = @NowUtc
            WHERE AttendanceCorrectionId = @AttendanceCorrectionId;
        END
        ELSE
        BEGIN
            UPDATE core.AttendanceCorrection
            SET Status                    = 3,       -- Rejected
                ApprovedByAdministratorId = @AdministratorId,
                DecidedUtc                = @NowUtc,
                DecisionNote              = @DecisionNote
            WHERE AttendanceCorrectionId = @AttendanceCorrectionId;
        END;

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@AttendancePubId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000),
                @EventType   VARCHAR(64) =
                    CASE WHEN @Approve = 1 THEN 'Attendance.CorrectionApplied'
                         ELSE 'Attendance.CorrectionRejected' END;

        SET @Details =
        (
            SELECT @AttendanceCorrectionId AS correctionId,
                   @RequestedBy            AS requestedByAdministratorId,
                   @AdministratorId        AS decidedByAdministratorId,
                   @OriginalIn             AS originalClockInUtc,
                   @OriginalOut            AS originalClockOutUtc,
                   @CorrectedIn            AS correctedClockInUtc,
                   @CorrectedOut           AS correctedClockOutUtc,
                   @DecisionNote           AS decisionNote
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = @EventType,
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
            ROLLBACK TRANSACTION CorrectionApprove;
        END;

        /* The table constraint is the real separation-of-duties guarantee. */
        IF ERROR_NUMBER() = 547
        BEGIN
            SET @ResultCode = 1074;      -- SeparationOfDutiesViolation
            RETURN;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_Attendance_ApproveCorrection deployed.';
GO
