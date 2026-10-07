/*==============================================================================
  ClockInXtra — Office location and attendance tables
  File  : database/schema/04_tables_attendance.sql
  Phase : 5

  Contents
      core.OfficeLocation        approved offices, per-location radius
      core.Attendance            one record per employee per attendance day
      core.AttendanceEvent       immutable evidence for each clock-in/out
      core.AttendanceCorrection  maker-checker corrections (DEC-08, DEC-10)

  Integrity notes
  ---------------
  * UX_Attendance_MobileUserId_AttendanceDate is the real protection against
    duplicate clock-ins. Two concurrent requests on different IIS nodes cannot
    both succeed, regardless of application-level checks (Claude.md §45).
  * Coordinates are stored as DECIMAL, not FLOAT, so stored values are exact.
  * Raw coordinates of a clock-in are NOT stored by default (assumption
    ASM-06). CoordinatesProtected exists for the case where OPEN-35 decides
    they must be retained, and it only ever holds encrypted data.
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;   -- required: IX_Attendance_Open is a filtered index
SET NOCOUNT ON;
GO

/*------------------------------------------------------------------------------
  core.OfficeLocation

  AllowedRadiusMeters defaults to 5 per the stated requirement, but is
  per-location and configurable (Claude.md §9). See conflict CON-01 about what
  a 5 metre radius means in practice for consumer GPS.
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.OfficeLocation', N'U') IS NULL
BEGIN
    CREATE TABLE core.OfficeLocation
    (
        OfficeLocationId    INT            IDENTITY(1,1) NOT NULL,
        OfficeLocationPublicId UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_OfficeLocation_PublicId DEFAULT (NEWID()),
        Name                NVARCHAR(120)  NOT NULL,
        Description         NVARCHAR(400)  NULL,
        Latitude            DECIMAL(9, 6)  NOT NULL,
        Longitude           DECIMAL(9, 6)  NOT NULL,
        AllowedRadiusMeters DECIMAL(6, 2)  NOT NULL
            CONSTRAINT DF_OfficeLocation_AllowedRadiusMeters DEFAULT (5.00),
        Status              TINYINT        NOT NULL        -- 0 Disabled, 1 Active
            CONSTRAINT DF_OfficeLocation_Status DEFAULT (1),
        CreatedUtc          DATETIME2(3)   NOT NULL
            CONSTRAINT DF_OfficeLocation_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        CreatedByAdministratorId INT       NULL,
        UpdatedUtc          DATETIME2(3)   NULL,
        UpdatedByAdministratorId INT       NULL,
        [RowVersion]        ROWVERSION     NOT NULL,
        CONSTRAINT PK_core_OfficeLocation PRIMARY KEY CLUSTERED (OfficeLocationId),
        CONSTRAINT UQ_OfficeLocation_PublicId UNIQUE (OfficeLocationPublicId),
        CONSTRAINT UQ_OfficeLocation_Name UNIQUE (Name),
        CONSTRAINT FK_OfficeLocation_Administrator_CreatedBy
            FOREIGN KEY (CreatedByAdministratorId) REFERENCES core.Administrator (AdministratorId),
        CONSTRAINT FK_OfficeLocation_Administrator_UpdatedBy
            FOREIGN KEY (UpdatedByAdministratorId) REFERENCES core.Administrator (AdministratorId),
        CONSTRAINT CK_OfficeLocation_Latitude  CHECK (Latitude  >= -90  AND Latitude  <= 90),
        CONSTRAINT CK_OfficeLocation_Longitude CHECK (Longitude >= -180 AND Longitude <= 180),
        CONSTRAINT CK_OfficeLocation_Radius    CHECK (AllowedRadiusMeters > 0 AND AllowedRadiusMeters <= 10000),
        CONSTRAINT CK_OfficeLocation_Status    CHECK (Status IN (0, 1))
    );

    CREATE NONCLUSTERED INDEX IX_OfficeLocation_Status
        ON core.OfficeLocation (Status)
        INCLUDE (Latitude, Longitude, AllowedRadiusMeters, Name);
END
GO

/*------------------------------------------------------------------------------
  core.Attendance

  ASM-02: one clock-in and one clock-out per employee per attendance day.
  ASM-03: AttendanceDate is the business-local date computed by SQL Server at
  clock-in time, never a client-supplied date.

  IsLateClockIn / IsEarlyClockOut stay NULL until the business answers
  OPEN-6 / OPEN-7. A NULL here means "not evaluated", not "false".
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.Attendance', N'U') IS NULL
BEGIN
    CREATE TABLE core.Attendance
    (
        AttendanceId          BIGINT           IDENTITY(1,1) NOT NULL,
        AttendancePublicId    UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_Attendance_AttendancePublicId DEFAULT (NEWID()),
        MobileUserId          INT              NOT NULL,
        AttendanceDate        DATE             NOT NULL,
        ClockInUtc            DATETIME2(3)     NOT NULL,
        ClockOutUtc           DATETIME2(3)     NULL,
        DurationMinutes       INT              NULL,
        Status                TINYINT          NOT NULL     -- 1 Open, 2 Closed, 3 Corrected
            CONSTRAINT DF_Attendance_Status DEFAULT (1),
        ClockInOfficeLocationId  INT           NOT NULL,
        ClockOutOfficeLocationId INT           NULL,
        IsLateClockIn         BIT              NULL,
        IsEarlyClockOut       BIT              NULL,
        CreatedUtc            DATETIME2(3)     NOT NULL
            CONSTRAINT DF_Attendance_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedUtc            DATETIME2(3)     NULL,
        [RowVersion]          ROWVERSION       NOT NULL,
        CONSTRAINT PK_core_Attendance PRIMARY KEY CLUSTERED (AttendanceId),
        CONSTRAINT UQ_Attendance_AttendancePublicId UNIQUE (AttendancePublicId),
        CONSTRAINT FK_Attendance_MobileUser_MobileUserId
            FOREIGN KEY (MobileUserId) REFERENCES core.MobileUser (MobileUserId),
        CONSTRAINT FK_Attendance_OfficeLocation_ClockIn
            FOREIGN KEY (ClockInOfficeLocationId) REFERENCES core.OfficeLocation (OfficeLocationId),
        CONSTRAINT FK_Attendance_OfficeLocation_ClockOut
            FOREIGN KEY (ClockOutOfficeLocationId) REFERENCES core.OfficeLocation (OfficeLocationId),
        CONSTRAINT CK_Attendance_Status CHECK (Status IN (1, 2, 3)),
        CONSTRAINT CK_Attendance_ClockOutOrder
            CHECK (ClockOutUtc IS NULL OR ClockOutUtc >= ClockInUtc),
        CONSTRAINT CK_Attendance_ClosedHasClockOut
            CHECK ((Status = 1 AND ClockOutUtc IS NULL) OR (Status <> 1)),
        CONSTRAINT CK_Attendance_DurationSign
            CHECK (DurationMinutes IS NULL OR DurationMinutes >= 0)
    );

    /* The duplicate-clock-in guarantee. */
    CREATE UNIQUE NONCLUSTERED INDEX UX_Attendance_MobileUserId_AttendanceDate
        ON core.Attendance (MobileUserId, AttendanceDate);

    /* Daily attendance and missing-clock-out reporting. */
    CREATE NONCLUSTERED INDEX IX_Attendance_AttendanceDate_Status
        ON core.Attendance (AttendanceDate, Status)
        INCLUDE (MobileUserId, ClockInUtc, ClockOutUtc, DurationMinutes);

    /* Open records, used by status lookups and the missing-clock-out report. */
    CREATE NONCLUSTERED INDEX IX_Attendance_Open
        ON core.Attendance (MobileUserId, AttendanceDate)
        WHERE Status = 1;
END
GO

/*------------------------------------------------------------------------------
  core.AttendanceEvent

  Evidence for each individual transaction. Rows are inserted once and never
  updated; corrections create new rows and leave the original intact
  (Claude.md §28: no partial or rewritten attendance records).
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.AttendanceEvent', N'U') IS NULL
BEGIN
    CREATE TABLE core.AttendanceEvent
    (
        AttendanceEventId      BIGINT           IDENTITY(1,1) NOT NULL,
        AttendanceId           BIGINT           NOT NULL,
        EventType              TINYINT          NOT NULL,   -- 1 ClockIn, 2 ClockOut, 3 CorrectionApplied
        OccurredUtc            DATETIME2(3)     NOT NULL,
        MobileUserId           INT              NOT NULL,
        DeviceId               INT              NULL,       -- NULL for administrative corrections
        OfficeLocationId       INT              NULL,
        DistanceMeters         DECIMAL(8, 2)    NULL,
        ReportedAccuracyMeters DECIMAL(8, 2)    NULL,
        Platform               TINYINT          NULL,
        WasMockedLocation      BIT              NULL,
        CoordinatesProtected   VARBINARY(MAX)   NULL,       -- encrypted; only used if OPEN-35 requires it
        CorrelationId          UNIQUEIDENTIFIER NULL,
        CreatedUtc             DATETIME2(3)     NOT NULL
            CONSTRAINT DF_AttendanceEvent_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_core_AttendanceEvent PRIMARY KEY CLUSTERED (AttendanceEventId),
        CONSTRAINT FK_AttendanceEvent_Attendance_AttendanceId
            FOREIGN KEY (AttendanceId) REFERENCES core.Attendance (AttendanceId),
        CONSTRAINT FK_AttendanceEvent_MobileUser_MobileUserId
            FOREIGN KEY (MobileUserId) REFERENCES core.MobileUser (MobileUserId),
        CONSTRAINT FK_AttendanceEvent_Device_DeviceId
            FOREIGN KEY (DeviceId) REFERENCES core.Device (DeviceId),
        CONSTRAINT FK_AttendanceEvent_OfficeLocation_OfficeLocationId
            FOREIGN KEY (OfficeLocationId) REFERENCES core.OfficeLocation (OfficeLocationId),
        CONSTRAINT CK_AttendanceEvent_EventType CHECK (EventType IN (1, 2, 3)),
        CONSTRAINT CK_AttendanceEvent_Platform CHECK (Platform IS NULL OR Platform IN (1, 2)),
        CONSTRAINT CK_AttendanceEvent_Distance CHECK (DistanceMeters IS NULL OR DistanceMeters >= 0),
        CONSTRAINT CK_AttendanceEvent_Accuracy
            CHECK (ReportedAccuracyMeters IS NULL OR ReportedAccuracyMeters > 0)
    );

    CREATE NONCLUSTERED INDEX IX_AttendanceEvent_AttendanceId
        ON core.AttendanceEvent (AttendanceId, EventType);

    CREATE NONCLUSTERED INDEX IX_AttendanceEvent_OccurredUtc
        ON core.AttendanceEvent (OccurredUtc)
        INCLUDE (MobileUserId, EventType, OfficeLocationId, DistanceMeters);
END
GO

/*------------------------------------------------------------------------------
  core.AttendanceCorrection

  Corrections are permitted, require a second administrator's approval
  (DEC-08), and are requested by Attendance Administrators and approved by
  Super Administrators (DEC-10). The table also supports the reporting
  requirement "attendance corrections" (Claude.md §20).

  Segregation of duties is a database constraint, not a UI rule: the approver
  can never be the requester.
------------------------------------------------------------------------------*/
IF OBJECT_ID(N'core.AttendanceCorrection', N'U') IS NULL
BEGIN
    CREATE TABLE core.AttendanceCorrection
    (
        AttendanceCorrectionId BIGINT       IDENTITY(1,1) NOT NULL,
        AttendanceId           BIGINT       NOT NULL,
        FieldChanged           TINYINT      NOT NULL,   -- 1 ClockIn, 2 ClockOut, 3 Both
        OriginalClockInUtc     DATETIME2(3) NULL,
        OriginalClockOutUtc    DATETIME2(3) NULL,
        CorrectedClockInUtc    DATETIME2(3) NULL,
        CorrectedClockOutUtc   DATETIME2(3) NULL,
        Reason                 NVARCHAR(512) NOT NULL,
        Status                 TINYINT      NOT NULL,   -- 1 Requested, 2 Approved, 3 Rejected, 4 Applied
        RequestedByAdministratorId INT      NOT NULL,
        RequestedUtc           DATETIME2(3) NOT NULL
            CONSTRAINT DF_AttendanceCorrection_RequestedUtc DEFAULT (SYSUTCDATETIME()),
        ApprovedByAdministratorId  INT      NULL,
        DecidedUtc             DATETIME2(3) NULL,
        DecisionNote           NVARCHAR(512) NULL,
        AppliedUtc             DATETIME2(3) NULL,
        [RowVersion]           ROWVERSION   NOT NULL,
        CONSTRAINT PK_core_AttendanceCorrection PRIMARY KEY CLUSTERED (AttendanceCorrectionId),
        CONSTRAINT FK_AttendanceCorrection_Attendance_AttendanceId
            FOREIGN KEY (AttendanceId) REFERENCES core.Attendance (AttendanceId),
        CONSTRAINT FK_AttendanceCorrection_Administrator_RequestedBy
            FOREIGN KEY (RequestedByAdministratorId) REFERENCES core.Administrator (AdministratorId),
        CONSTRAINT FK_AttendanceCorrection_Administrator_ApprovedBy
            FOREIGN KEY (ApprovedByAdministratorId) REFERENCES core.Administrator (AdministratorId),
        CONSTRAINT CK_AttendanceCorrection_Status CHECK (Status IN (1, 2, 3, 4)),
        CONSTRAINT CK_AttendanceCorrection_FieldChanged CHECK (FieldChanged IN (1, 2, 3)),
        CONSTRAINT CK_AttendanceCorrection_SeparationOfDuties
            CHECK (ApprovedByAdministratorId IS NULL
                   OR ApprovedByAdministratorId <> RequestedByAdministratorId),
        CONSTRAINT CK_AttendanceCorrection_Order
            CHECK (CorrectedClockOutUtc IS NULL
                   OR CorrectedClockInUtc IS NULL
                   OR CorrectedClockOutUtc >= CorrectedClockInUtc)
    );

    CREATE NONCLUSTERED INDEX IX_AttendanceCorrection_Status_RequestedUtc
        ON core.AttendanceCorrection (Status, RequestedUtc)
        INCLUDE (AttendanceId, RequestedByAdministratorId);
END
GO

PRINT 'Attendance tables ready.';
GO
