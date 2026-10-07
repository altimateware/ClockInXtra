/*==============================================================================
  admin.usp_MobileUser_Update
  Phase : 6
  Called by: the administration portal (permission MobileUser.Manage).

  Purpose
  -------
  Update an employee's profile details (§17).

  What this procedure deliberately cannot change
  ----------------------------------------------
  * UserId — it is the identifier the employee signs in with and the key the
    lockout counter is recorded against. Changing it would orphan security
    history and silently free the old identifier for reuse. If a user really
    must be renamed, that is a separate, audited operation.
  * Status — handled by usp_MobileUser_SetStatus, which also decides what
    happens to the employee's devices.
  * Credentials and authenticator — separate procedures, separate permissions.

  Keeping those out of a routine profile edit is what stops a low-risk screen
  from becoming a way to take over an account.

  Only fields that actually changed are audited, so the audit trail shows the
  substance of an edit rather than a wall of unchanged values.

  Transaction handling (decision DB-13)
  -------------------------------------
  Captures the outer transaction count and rolls back to a savepoint on a
  refusal, so a rejected edit never discards a caller's transaction.

  Result codes
      0    Success
      1001 InvalidRequest
      1070 NotFound
      1071 ConcurrencyConflict
      1072 DuplicateName (employee number already in use)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_MobileUser_Update
    @MobileUserId    INT,
    @EmployeeNumber  NVARCHAR(32)  = NULL,
    @FirstName       NVARCHAR(80),
    @LastName        NVARCHAR(80),
    @Email           NVARCHAR(256) = NULL,
    @PhoneNumber     NVARCHAR(32)  = NULL,
    @Department      NVARCHAR(120) = NULL,
    @JobTitle        NVARCHAR(120) = NULL,
    @RowVersion      BINARY(8),
    @AdministratorId INT,
    @CorrelationId   UNIQUEIDENTIFIER = NULL,
    @ResultCode      INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* DEC-11: every field is required, with the same shape checks as
       usp_MobileUser_Create. */
    IF LEN(LTRIM(RTRIM(@FirstName))) = 0 OR LEN(LTRIM(RTRIM(@LastName))) = 0
       OR ISNULL(LEN(LTRIM(RTRIM(@EmployeeNumber))), 0) = 0
       OR ISNULL(LEN(LTRIM(RTRIM(@Email))), 0) = 0
       OR ISNULL(LEN(LTRIM(RTRIM(@PhoneNumber))), 0) = 0
       OR ISNULL(LEN(LTRIM(RTRIM(@Department))), 0) = 0
       OR ISNULL(LEN(LTRIM(RTRIM(@JobTitle))), 0) = 0
       OR @Email NOT LIKE N'_%@_%._%' OR @Email LIKE N'%@%@%' OR @Email LIKE N'% %'
       OR @PhoneNumber LIKE N'%[^0-9 +()-]%'
       OR LEN(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@PhoneNumber, N' ', N''), N'+', N''), N'(', N''), N')', N''), N'-', N'')) < 7
    BEGIN
        SET @ResultCode = 1001;          -- InvalidRequest
        RETURN;
    END;

    DECLARE @NowUtc DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @OuterTranCount INT = @@TRANCOUNT;

    BEGIN TRY
        IF @OuterTranCount = 0
            BEGIN TRANSACTION;
        ELSE
            SAVE TRANSACTION UserUpdate;

        DECLARE @PublicId        UNIQUEIDENTIFIER,
                @CurrentVersion  BINARY(8),
                @OldEmployeeNo   NVARCHAR(32),
                @OldFirstName    NVARCHAR(80),
                @OldLastName     NVARCHAR(80),
                @OldEmail        NVARCHAR(256),
                @OldPhone        NVARCHAR(32),
                @OldDepartment   NVARCHAR(120),
                @OldJobTitle     NVARCHAR(120);

        SELECT @PublicId       = u.MobileUserPublicId,
               @CurrentVersion = u.[RowVersion],
               @OldEmployeeNo  = u.EmployeeNumber,
               @OldFirstName   = u.FirstName,
               @OldLastName    = u.LastName,
               @OldEmail       = u.Email,
               @OldPhone       = u.PhoneNumber,
               @OldDepartment  = u.Department,
               @OldJobTitle    = u.JobTitle
        FROM core.MobileUser AS u WITH (UPDLOCK)
        WHERE u.MobileUserId = @MobileUserId;

        IF @PublicId IS NULL
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION UserUpdate;
            SET @ResultCode = 1070;      -- NotFound
            RETURN;
        END;

        IF @CurrentVersion <> @RowVersion
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION UserUpdate;
            SET @ResultCode = 1071;      -- ConcurrencyConflict
            RETURN;
        END;

        /* A department or job title must be an active list entry — unless it
           is the one the employee already holds. Deactivating an entry stops
           it being chosen; it does not force everyone holding it to change
           the next time their record is edited. */
        IF (@Department <> ISNULL(@OldDepartment, N'')
                AND NOT EXISTS (SELECT 1 FROM core.Department WHERE Name = @Department AND IsActive = 1))
           OR (@JobTitle <> ISNULL(@OldJobTitle, N'')
                AND NOT EXISTS (SELECT 1 FROM core.JobTitle WHERE Name = @JobTitle AND IsActive = 1))
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION UserUpdate;
            SET @ResultCode = 1001;      -- InvalidRequest
            RETURN;
        END;

        IF @EmployeeNumber IS NOT NULL
           AND EXISTS (SELECT 1 FROM core.MobileUser
                       WHERE EmployeeNumber = @EmployeeNumber
                         AND MobileUserId <> @MobileUserId)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION UserUpdate;
            SET @ResultCode = 1072;      -- DuplicateName
            RETURN;
        END;

        UPDATE core.MobileUser
        SET EmployeeNumber           = @EmployeeNumber,
            FirstName                = @FirstName,
            LastName                 = @LastName,
            Email                    = @Email,
            PhoneNumber              = @PhoneNumber,
            Department               = @Department,
            JobTitle                 = @JobTitle,
            UpdatedUtc               = @NowUtc,
            UpdatedByAdministratorId = @AdministratorId
        WHERE MobileUserId = @MobileUserId;

        /* Record only what actually changed. ISNULL handling keeps a change
           from NULL to a value, and back, visible. */
        DECLARE @Changes NVARCHAR(2000) =
        (
            SELECT
                CASE WHEN ISNULL(@OldEmployeeNo, N'') <> ISNULL(@EmployeeNumber, N'') THEN @OldEmployeeNo END AS previousEmployeeNumber,
                CASE WHEN ISNULL(@OldEmployeeNo, N'') <> ISNULL(@EmployeeNumber, N'') THEN @EmployeeNumber END AS newEmployeeNumber,
                CASE WHEN @OldFirstName <> @FirstName THEN @OldFirstName END AS previousFirstName,
                CASE WHEN @OldFirstName <> @FirstName THEN @FirstName END    AS newFirstName,
                CASE WHEN @OldLastName <> @LastName THEN @OldLastName END    AS previousLastName,
                CASE WHEN @OldLastName <> @LastName THEN @LastName END       AS newLastName,
                /* Contact details are recorded as CHANGED, never by value
                   (Phase 20). The audit log is an append-only ledger: nothing
                   written to it can ever be erased, so an address or number
                   placed here would outlive the employee's record, any
                   retention period (OPEN-14) and any erasure request under
                   Nigerian data-protection law (§62). That a change happened,
                   by whom and when is the audit fact; the values themselves
                   are not needed to establish it. */
                CASE WHEN ISNULL(@OldEmail, N'') <> ISNULL(@Email, N'') THEN CAST(1 AS BIT) END       AS emailChanged,
                CASE WHEN ISNULL(@OldPhone, N'') <> ISNULL(@PhoneNumber, N'') THEN CAST(1 AS BIT) END AS phoneChanged,
                CASE WHEN ISNULL(@OldDepartment, N'') <> ISNULL(@Department, N'') THEN @OldDepartment END AS previousDepartment,
                CASE WHEN ISNULL(@OldDepartment, N'') <> ISNULL(@Department, N'') THEN @Department END    AS newDepartment,
                CASE WHEN ISNULL(@OldJobTitle, N'') <> ISNULL(@JobTitle, N'') THEN @OldJobTitle END AS previousJobTitle,
                CASE WHEN ISNULL(@OldJobTitle, N'') <> ISNULL(@JobTitle, N'') THEN @JobTitle END    AS newJobTitle
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
        );

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@PublicId AS NVARCHAR(64));

        EXEC core.usp_AuditLog_Create
            @EventType         = 'MobileUser.Updated',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @SubjectType       = 'MobileUser',
            @SubjectId         = @SubjectId,
            @Result            = 1,
            @SourceApplication = 'Attendance.Admin',
            @CorrelationId     = @CorrelationId,
            @Details           = @Changes,
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
            ROLLBACK TRANSACTION UserUpdate;
        END;

        IF ERROR_NUMBER() IN (2627, 2601)
        BEGIN
            SET @ResultCode = 1072;
            RETURN;
        END;

        THROW;
    END CATCH;
END;
GO

PRINT 'admin.usp_MobileUser_Update deployed.';
GO
