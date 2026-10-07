/*==============================================================================
  job.usp_Maintenance_PurgeAttendanceEvidence
  Phase : 6
  Called by: the scheduled maintenance job (account app_jobs).

  Purpose
  -------
  Apply the data-retention policy to attendance records and their location
  evidence (§62).

  BLANK MEANS KEEP INDEFINITELY
  -----------------------------
  Retention.AttendanceDays and Retention.AttendanceEvidenceDays are in days.
  Blank means the data is kept indefinitely and this procedure deletes
  nothing — which is the business decision recorded on 2026-09-19 (DEC-07:
  "leave the data for as long as possible"). Whether indefinite retention of
  employee location evidence is acceptable under the Nigeria Data Protection
  Act remains for Legal to confirm (OPEN-41); setting a period later is all it
  takes to start purging.

  A value below 1 is never treated as a period: 0 would put the cut-off at
  today and delete every earlier record, irreversibly. The settings procedure
  refuses it, and this procedure ignores it as well.

  Two periods, because the sensitivity differs
  --------------------------------------------
  Location evidence (distance, accuracy, device, mock flag) is the more
  privacy-sensitive half and can be purged on a shorter clock than the
  attendance record itself, which is an employment record. Purging evidence
  leaves the attendance row intact and auditable; purging attendance removes
  its evidence first, because of the foreign key.

  Batching and transactions (decision DB-13)
  ------------------------------------------
  Deletions run in batches so that locks are held briefly and a long purge
  cannot block attendance. Each batch is its own transaction ONLY when this
  procedure is the outermost caller. If a caller already opened a transaction,
  the batches join it and that caller decides the outcome — committing a batch
  inside someone else's transaction would be committing their work too, which
  is not this procedure's decision to make.

  What this procedure CANNOT purge
  --------------------------------
  Rows in audit.AuditLog and audit.SecurityEvent. They are append-only ledger
  tables and SQL Server does not permit deletion from them — that is the
  tamper-evidence property the audit trail depends on. If Legal requires audit
  data to be deleted, the design must change to period-scoped audit tables
  before production (conflict CON-11).

  Result codes
      0    Success, including "nothing to do" when retention is indefinite
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE job.usp_Maintenance_PurgeAttendanceEvidence
    @BatchSize           INT = 2000,
    @MaxBatches          INT = 500,
    @EvidenceRowsDeleted INT OUTPUT,
    @AttendanceRowsDeleted INT OUTPUT,
    @ResultCode          INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @EvidenceRowsDeleted   = 0;
    SET @AttendanceRowsDeleted = 0;

    DECLARE @AttendanceDays INT,
            @EvidenceDays   INT,
            @NowUtc         DATETIME2(3) = SYSUTCDATETIME();

    SELECT @AttendanceDays = TRY_CAST(SettingValue AS INT)
    FROM core.ApplicationSetting WHERE SettingKey = 'Retention.AttendanceDays';

    SELECT @EvidenceDays = TRY_CAST(SettingValue AS INT)
    FROM core.ApplicationSetting WHERE SettingKey = 'Retention.AttendanceEvidenceDays';

    /* A period below one day is never a retention period: 0 would put the
       cut-off at today and delete every earlier record. The settings procedure
       refuses such values; this repeats the guard here, where the deletion
       happens, in case a value ever arrives by another route. */
    IF @AttendanceDays < 1 SET @AttendanceDays = NULL;
    IF @EvidenceDays   < 1 SET @EvidenceDays   = NULL;

    /* Blank means keep indefinitely (DEC-07, 2026-09-19). Nothing is deleted,
       and that is success, not an error a scheduler should alert on. */
    IF @AttendanceDays IS NULL AND @EvidenceDays IS NULL
    BEGIN
        SET @ResultCode = 0;
        RETURN;
    END;

    DECLARE @OuterTranCount INT = @@TRANCOUNT;
    DECLARE @Batch INT, @Affected INT;

    ------------------------------------------------------------------
    -- 1. Location evidence for attendance older than the evidence period.
    --    The attendance record itself survives; only the per-event
    --    location detail is removed.
    ------------------------------------------------------------------
    IF @EvidenceDays IS NOT NULL
    BEGIN
        DECLARE @EvidenceCutoff DATE = CAST(DATEADD(DAY, -@EvidenceDays, @NowUtc) AS DATE);

        SET @Batch = 0;
        SET @Affected = 1;

        WHILE @Affected > 0 AND @Batch < @MaxBatches
        BEGIN
            DELETE TOP (@BatchSize) e
            FROM core.AttendanceEvent AS e
            INNER JOIN core.Attendance AS a ON a.AttendanceId = e.AttendanceId
            WHERE a.AttendanceDate < @EvidenceCutoff;

            SET @Affected = @@ROWCOUNT;
            SET @EvidenceRowsDeleted = @EvidenceRowsDeleted + @Affected;
            SET @Batch = @Batch + 1;
        END;
    END;

    ------------------------------------------------------------------
    -- 2. Attendance records beyond the retention period, with their
    --    remaining evidence rows removed first to satisfy the foreign key.
    ------------------------------------------------------------------
    IF @AttendanceDays IS NOT NULL
    BEGIN
        DECLARE @AttendanceCutoff DATE = CAST(DATEADD(DAY, -@AttendanceDays, @NowUtc) AS DATE);
        DECLARE @Doomed TABLE (AttendanceId BIGINT PRIMARY KEY);

        SET @Batch = 0;
        SET @Affected = 1;

        WHILE @Affected > 0 AND @Batch < @MaxBatches
        BEGIN
            /* Each batch is atomic. It opens its own transaction only when
               nobody else owns one; otherwise it simply joins the caller's. */
            IF @OuterTranCount = 0
                BEGIN TRANSACTION;

            DELETE FROM @Doomed;

            INSERT INTO @Doomed (AttendanceId)
            SELECT TOP (@BatchSize) a.AttendanceId
            FROM core.Attendance AS a
            WHERE a.AttendanceDate < @AttendanceCutoff
            ORDER BY a.AttendanceId;

            DELETE e
            FROM core.AttendanceEvent AS e
            INNER JOIN @Doomed AS d ON d.AttendanceId = e.AttendanceId;

            SET @EvidenceRowsDeleted = @EvidenceRowsDeleted + @@ROWCOUNT;

            /* Corrections reference attendance as well, so they go with it. */
            DELETE c
            FROM core.AttendanceCorrection AS c
            INNER JOIN @Doomed AS d ON d.AttendanceId = c.AttendanceId;

            DELETE a
            FROM core.Attendance AS a
            INNER JOIN @Doomed AS d ON d.AttendanceId = a.AttendanceId;

            SET @Affected = @@ROWCOUNT;
            SET @AttendanceRowsDeleted = @AttendanceRowsDeleted + @Affected;

            IF @OuterTranCount = 0
                COMMIT TRANSACTION;

            SET @Batch = @Batch + 1;
        END;
    END;

    ------------------------------------------------------------------
    -- 3. Record that the purge ran, and what it removed. The audit trail
    --    must show deletions, not only creations.
    ------------------------------------------------------------------
    DECLARE @AuditResult INT,
            @Details     NVARCHAR(2000);

    SET @Details =
    (
        SELECT @AttendanceDays        AS attendanceRetentionDays,
               @EvidenceDays          AS evidenceRetentionDays,
               @AttendanceRowsDeleted AS attendanceRowsDeleted,
               @EvidenceRowsDeleted   AS evidenceRowsDeleted
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    );

    EXEC core.usp_AuditLog_Create
        @EventType         = 'Retention.Purged',
        @ActorType         = 0,                  -- System
        @ActorId           = NULL,
        @SubjectType       = 'Attendance',
        @SubjectId         = NULL,
        @Result            = 1,
        @SourceApplication = 'Attendance.Job',
        @Details           = @Details,
        @OccurredUtc       = @NowUtc,
        @ResultCode        = @AuditResult OUTPUT;

    SET @ResultCode = 0;
END;
GO

PRINT 'job.usp_Maintenance_PurgeAttendanceEvidence deployed.';
GO
