/*==============================================================================
  admin.usp_ApplicationSetting_Set
  Phase : 6
  Called by: the administration portal (permission Setting.Manage).

  Purpose
  -------
  Record a business or security decision against a setting, and optionally
  mark it as confirmed by a named administrator.

  Validation happens here as well as in the portal
  ------------------------------------------------
  These values change how attendance is calculated for everyone, so the
  database validates them itself (§44):

    timezone  must exist in sys.time_zone_info. An unrecognised identifier
              would make every clock-in fail at AT TIME ZONE, so it is
              refused at the point of entry instead.
    enum      must be one of AllowedValues.
    time      must parse as a time.
    int/decimal/bool  must parse as that type.
    int/decimal       must lie within the row's MinValue..MaxValue, when set.
    Mobile.MinimumAppVersion  must look like a version: 1, 1.4 or 1.4.0.

  The bounds, and whether a blank is a confirmable decision (BlankMeaning),
  are read from the setting's own row rather than listed here, so the portal
  can offer exactly the same limits on its inputs (Phase 24).

  Confirmation
  ------------
  Setting a value and confirming it are separate acts. @Confirm = 1 records
  that a named administrator has accepted the value as the business decision,
  which is what clears it from the outstanding-decisions list. The table's
  CHECK constraint keeps the two columns consistent.

  Audit
  -----
  The previous and new values are both recorded. Changing the clock-in closing
  time changes who counts as late, so an auditor must be able to see what the
  rule was on any given day.

  Transaction handling (decision DB-13)
  -------------------------------------
  Captures the outer transaction count and rolls back to a savepoint on a
  rejection. This matters here because configuring a system usually means
  setting several values together, and a caller that wraps them in one
  transaction must not lose all of them because the last one failed
  validation.

  Result codes
      0    Success
      1070 NotFound
      1071 ConcurrencyConflict
      1076 InvalidTimeZone
      1001 InvalidRequest (value does not match the declared type or allowed set)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_ApplicationSetting_Set
    @SettingKey      VARCHAR(100),
    @SettingValue    NVARCHAR(400),        -- NULL is permitted: it returns the setting to "undecided"
    @Confirm         BIT,
    @RowVersion      BINARY(8),
    @AdministratorId INT,
    @CorrelationId   UNIQUEIDENTIFIER = NULL,
    @ResultCode      INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION SettingSet;

        DECLARE @DataType       VARCHAR(20),
                @AllowedValues  NVARCHAR(400),
                @OldValue       NVARCHAR(400),
                @CurrentVersion BINARY(8),
                @SettingId      INT,
                @MinValue       DECIMAL(18, 4),
                @MaxValue       DECIMAL(18, 4),
                @BlankMeaning   NVARCHAR(80);

        SELECT @SettingId      = s.ApplicationSettingId,
               @DataType       = s.DataType,
               @AllowedValues  = s.AllowedValues,
               @OldValue       = s.SettingValue,
               @CurrentVersion = s.[RowVersion],
               @MinValue       = s.MinValue,
               @MaxValue       = s.MaxValue,
               @BlankMeaning   = s.BlankMeaning
        FROM core.ApplicationSetting AS s WITH (UPDLOCK)
        WHERE s.SettingKey = @SettingKey;

        IF @SettingId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION SettingSet;
            SET @ResultCode = 1070;      -- NotFound
            RETURN;
        END;

        IF @CurrentVersion <> @RowVersion
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION SettingSet;
            SET @ResultCode = 1071;      -- ConcurrencyConflict
            RETURN;
        END;

        ------------------------------------------------------------------
        -- Validate the value against its declared type.
        ------------------------------------------------------------------
        IF @SettingValue IS NOT NULL
        BEGIN
            IF @DataType = 'timezone'
               AND NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = @SettingValue)
            BEGIN
                IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION SettingSet;
                SET @ResultCode = 1076;  -- InvalidTimeZone
                RETURN;
            END;

            IF @DataType = 'enum'
               AND NOT EXISTS (SELECT 1
                               FROM STRING_SPLIT(COALESCE(@AllowedValues, N''), ',')
                               WHERE LTRIM(RTRIM(value)) = @SettingValue)
            BEGIN
                IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION SettingSet;
                SET @ResultCode = 1001;  -- InvalidRequest
                RETURN;
            END;

            IF (@DataType = 'time'    AND TRY_CAST(@SettingValue AS TIME(0)) IS NULL)
               OR (@DataType = 'int'     AND TRY_CAST(@SettingValue AS INT) IS NULL)
               OR (@DataType = 'decimal' AND TRY_CAST(@SettingValue AS DECIMAL(18, 4)) IS NULL)
               OR (@DataType = 'date'    AND TRY_CAST(@SettingValue AS DATE) IS NULL)
               OR (@DataType = 'bool'    AND @SettingValue NOT IN (N'true', N'false'))
            BEGIN
                IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION SettingSet;
                SET @ResultCode = 1001;  -- InvalidRequest
                RETURN;
            END;

            /* Bounds, from the setting's own row. They exist because type
               checking alone accepted values that break the system: a
               negative grace period silently moved the clock-in deadline
               earlier, and a retention of 0 days would put the purge cut-off
               at today and delete EVERY record. Keeping data indefinitely is
               a blank, not 0. */
            IF @DataType IN ('int', 'decimal')
               AND (   (@MinValue IS NOT NULL AND TRY_CAST(@SettingValue AS DECIMAL(18, 4)) < @MinValue)
                    OR (@MaxValue IS NOT NULL AND TRY_CAST(@SettingValue AS DECIMAL(18, 4)) > @MaxValue))
            BEGIN
                IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION SettingSet;
                SET @ResultCode = 1001;  -- InvalidRequest
                RETURN;
            END;

            /* A version the mobile app compares numerically: digits separated
               by single dots, e.g. 1.4.0. Anything else would make the
               comparison meaningless and could lock every phone out. */
            IF @SettingKey = 'Mobile.MinimumAppVersion'
               AND (   @SettingValue LIKE N'%[^0-9.]%'
                    OR @SettingValue LIKE N'.%'
                    OR @SettingValue LIKE N'%.'
                    OR @SettingValue LIKE N'%..%'
                    OR LEN(@SettingValue) > 32)
            BEGIN
                IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION SettingSet;
                SET @ResultCode = 1001;  -- InvalidRequest
                RETURN;
            END;
        END;

        /* A blank value normally means "undecided" and cannot be confirmed. For
           settings whose row carries a BlankMeaning, blank is itself a
           meaningful choice — no grace period, no earliest clock-in time, keep
           data indefinitely — so a blank can be confirmed as the decision,
           after which it no longer appears as outstanding. The settings that
           attendance cannot run without carry no BlankMeaning. */
        DECLARE @BlankIsADecision BIT =
            CASE WHEN @BlankMeaning IS NOT NULL THEN 1 ELSE 0 END;

        DECLARE @EffectiveConfirm BIT =
            CASE WHEN @SettingValue IS NULL AND @BlankIsADecision = 0 THEN 0 ELSE @Confirm END;

        UPDATE core.ApplicationSetting
        SET SettingValue                 = @SettingValue,
            RequiresBusinessConfirmation = CASE WHEN @EffectiveConfirm = 1 THEN 0 ELSE 1 END,
            ConfirmedUtc                 = CASE WHEN @EffectiveConfirm = 1 THEN @NowUtc END,
            ConfirmedByAdministratorId   = CASE WHEN @EffectiveConfirm = 1 THEN @AdministratorId END,
            UpdatedUtc                   = @NowUtc,
            UpdatedByAdministratorId     = @AdministratorId
        WHERE ApplicationSettingId = @SettingId;

        DECLARE @AuditResult INT,
                @Details     NVARCHAR(2000);

        SET @Details =
        (
            SELECT @SettingKey       AS settingKey,
                   @OldValue         AS previousValue,
                   @SettingValue     AS newValue,
                   @EffectiveConfirm AS confirmed
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'Setting.Changed',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @SubjectType       = 'ApplicationSetting',
            @SubjectId         = @SettingKey,
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
            ROLLBACK TRANSACTION SettingSet;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_ApplicationSetting_Set deployed.';
GO
