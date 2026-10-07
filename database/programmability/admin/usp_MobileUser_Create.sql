/*==============================================================================
  admin.usp_MobileUser_Create
  Phase : 6
  Called by: the administration portal (permission MobileUser.Manage).

  Purpose
  -------
  Create an employee record together with its initial local credential
  (§17, DEC-01).

  Password handling
  -----------------
  This procedure receives an ALREADY HASHED password: the salt, the iteration
  count and the derived key. A plaintext password is never passed to the
  database, never stored, and never logged (§24). Hashing happens in the
  application, where the PBKDF2 parameters live, so the work factor can be
  raised without a schema change and so the CPU cost is never paid inside a
  transaction.

  MustChange defaults to 1: the administrator sets an initial password and the
  employee is expected to change it. Whether a change flow exists on mobile is
  OPEN-40, so this flag is recorded but not yet acted upon by the app.

  Authenticator enrolment is a separate, deliberate step (OPEN-2). A user
  created here cannot clock in until an authenticator has been enrolled, and
  the portal shows that as an outstanding action rather than creating a
  half-usable account silently.

  Transaction handling (decision DB-13)
  -------------------------------------
  The employee row and its credential must commit together — an employee with
  no credential could never sign in, and a credential with no employee is
  orphaned. The outer transaction count is captured so a refusal rolls back to
  a savepoint rather than discarding a caller's transaction, which matters when
  employees are imported in batches.

  Every field is required (DEC-11)
  --------------------------------
  The business decided that no employee field is optional: employee number,
  email, phone, department and job title are all mandatory, as well as the
  names and user id. Department and job title must be ACTIVE entries of the
  maintained lists (core.Department, core.JobTitle); the foreign keys on
  core.MobileUser guarantee they exist, and this procedure adds that they are
  still in use. Email and phone get a shape check here — enough to refuse
  something that is plainly not an address or a number, not a claim that the
  address is deliverable.

  Result codes
      0    Success
      1001 InvalidRequest (a field missing or malformed, or a department or job
           title that is not an active list entry)
      1072 DuplicateName (UserId or EmployeeNumber already in use)
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_MobileUser_Create
    @UserId             NVARCHAR(64),
    @EmployeeNumber     NVARCHAR(32)  = NULL,
    @FirstName          NVARCHAR(80),
    @LastName           NVARCHAR(80),
    @Email              NVARCHAR(256) = NULL,
    @PhoneNumber        NVARCHAR(32)  = NULL,
    @Department         NVARCHAR(120) = NULL,
    @JobTitle           NVARCHAR(120) = NULL,
    @Status             TINYINT,
    @HashFormat         VARCHAR(32),
    @Iterations         INT,
    @Salt               VARBINARY(32),
    @PasswordHash       VARBINARY(64),
    @MustChange         BIT           = 1,
    @AdministratorId    INT,
    @CorrelationId      UNIQUEIDENTIFIER = NULL,
    @MobileUserId       INT              OUTPUT,
    @MobileUserPublicId UNIQUEIDENTIFIER OUTPUT,
    @ResultCode         INT              OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @MobileUserId       = NULL;
    SET @MobileUserPublicId = NULL;

    IF LEN(LTRIM(RTRIM(@UserId))) = 0
       OR LEN(LTRIM(RTRIM(@FirstName))) = 0
       OR LEN(LTRIM(RTRIM(@LastName))) = 0
       -- DEC-11: every field is required.
       OR ISNULL(LEN(LTRIM(RTRIM(@EmployeeNumber))), 0) = 0
       OR ISNULL(LEN(LTRIM(RTRIM(@Email))), 0) = 0
       OR ISNULL(LEN(LTRIM(RTRIM(@PhoneNumber))), 0) = 0
       OR ISNULL(LEN(LTRIM(RTRIM(@Department))), 0) = 0
       OR ISNULL(LEN(LTRIM(RTRIM(@JobTitle))), 0) = 0
       -- Plainly not an email address: no single @ with text either side and a dot after it.
       OR @Email NOT LIKE N'_%@_%._%' OR @Email LIKE N'%@%@%' OR @Email LIKE N'% %'
       -- Plainly not a phone number: only digits, spaces, + ( ) - allowed, at least 7 digits.
       OR @PhoneNumber LIKE N'%[^0-9 +()-]%'
       OR LEN(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@PhoneNumber, N' ', N''), N'+', N''), N'(', N''), N')', N''), N'-', N'')) < 7
       OR @Status NOT IN (0, 1, 2)
       OR @Iterations < 100000
       OR DATALENGTH(@Salt) <> 32
       OR DATALENGTH(@PasswordHash) = 0
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
            SAVE TRANSACTION UserCreate;

        IF EXISTS (SELECT 1 FROM core.MobileUser WHERE UserId = @UserId)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION UserCreate;
            SET @ResultCode = 1072;      -- DuplicateName
            RETURN;
        END;

        IF @EmployeeNumber IS NOT NULL
           AND EXISTS (SELECT 1 FROM core.MobileUser WHERE EmployeeNumber = @EmployeeNumber)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION UserCreate;
            SET @ResultCode = 1072;      -- DuplicateName
            RETURN;
        END;

        /* Department and job title are choices from the maintained lists, and
           a deactivated entry is no longer offered for new employees. */
        IF NOT EXISTS (SELECT 1 FROM core.Department WHERE Name = @Department AND IsActive = 1)
           OR NOT EXISTS (SELECT 1 FROM core.JobTitle WHERE Name = @JobTitle AND IsActive = 1)
        BEGIN
            IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION UserCreate;
            SET @ResultCode = 1001;      -- InvalidRequest
            RETURN;
        END;

        SET @MobileUserPublicId = NEWID();

        INSERT INTO core.MobileUser
            (MobileUserPublicId, UserId, EmployeeNumber, FirstName, LastName, Email,
             PhoneNumber, Department, JobTitle, Status, CreatedUtc, CreatedByAdministratorId)
        VALUES
            (@MobileUserPublicId, @UserId, @EmployeeNumber, @FirstName, @LastName, @Email,
             @PhoneNumber, @Department, @JobTitle, @Status, @NowUtc, @AdministratorId);

        SET @MobileUserId = SCOPE_IDENTITY();

        INSERT INTO core.EmployeeCredential
            (MobileUserId, HashFormat, Iterations, Salt, PasswordHash, MustChange, LastChangedUtc)
        VALUES
            (@MobileUserId, @HashFormat, @Iterations, @Salt, @PasswordHash, @MustChange, @NowUtc);

        DECLARE @AuditResult INT,
                @SubjectId   NVARCHAR(64) = CAST(@MobileUserPublicId AS NVARCHAR(64)),
                @Details     NVARCHAR(2000);

        /* The audit records WHO was created and by whom. It never records the
           salt, the hash or any credential material (§32). */
        SET @Details =
        (
            SELECT @UserId         AS userId,
                   @EmployeeNumber AS employeeNumber,
                   @Department     AS department,
                   @Status         AS status
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        EXEC core.usp_AuditLog_Create
            @EventType         = 'MobileUser.Created',
            @ActorType         = 2,
            @ActorId           = @AdministratorId,
            @SubjectType       = 'MobileUser',
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
            ROLLBACK TRANSACTION UserCreate;
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

PRINT 'admin.usp_MobileUser_Create deployed.';
GO
