/*==============================================================================
  admin.usp_Report_GetValidationFailures
  Phase : 6 (classification rewritten in Phase 19)
  Called by: the administration portal (permission Report.View).

  Purpose
  -------
  The location-validation and device-validation failure reports required by
  Claude.md §20, with identity and request-integrity failures alongside.

  Where the data comes from, and why
  ----------------------------------
  These reports read audit.SecurityEvent, not the attendance tables. A refused
  clock-in leaves no attendance record — that is the point of refusing it — so
  the only durable evidence is the security event written at the moment of
  refusal. Because that table is an append-only ledger, the report cannot be
  quietly pruned by anyone, including a database administrator.

  This is also where the reasons the API deliberately hides are visible. The
  mobile client receives INVALID_CREDENTIALS for a wrong password, a wrong OTP
  or a replayed OTP, so that an internet-facing endpoint cannot confirm a
  correct password (CON-09). An investigator with Report.View sees the precise
  ReasonCode here.

  HOW EVENTS ARE CLASSIFIED — AND WHY IT CHANGED
  -----------------------------------------------
  The first version matched a fixed list of REASON codes — the client-facing
  codes such as DEVICE_REVOKED and ATTESTATION_REJECTED. The server does not
  record those. It records its internal reasons, in the spelling each writer
  uses: the signature middleware writes the result code's name (DeviceRevoked,
  DeviceNotApproved), the authenticators write their own codes
  (INVALID_PASSWORD, OtpReplayed, UnknownUser), the attestation verifier writes
  the precise check that failed. Measured against the development ledger in
  Phase 19, the old list recognised 21 of roughly 600 events; every category
  filter returned next to nothing while failures were happening. And with no
  category chosen it returned successful sign-ins as "failures".

  Classification is now by EVENT TYPE, which each writer sets deliberately and
  which is a small, stable vocabulary. Reason is consulted only where one event
  type spans two categories: a request signature refused because the DEVICE
  is revoked is a device failure, not a request-integrity one. An event whose
  type is not in the map is not a failure — a successful sign-in, a password
  change — and is excluded entirely, so a new kind of success can never leak
  into a failure report.

  The map is the contract with the writers. It lives in
  admin.ufn_Report_ValidationFailureCategory so that it can be tested on its
  own (this procedure's two result sets cannot be captured by a T-SQL test).
  A new refusal event type must be added there, with a smoke test.

      Category  Event types
      1 Location   Location.Rejected, Location.ValidationFailed
      2 Device     Device.RegistrationRefused, Device.AttestationRejected,
                   Signature.Rejected when the reason is the device's state
      3 Identity   Auth.Failed, Auth.Locked, Auth.PasswordChangeFailed,
                   Auth.PasswordChangeLocked, Mfa.Missing, Mfa.SecretUnreadable,
                   Signature.Rejected when the employee is inactive
      4 Integrity  Signature.Rejected otherwise (replay, skew, digest, forgery)

  Finding an employee's events
  ----------------------------
  @SubjectKey matches the event subject — an employee's user id for sign-in
  failures — AND any event about a device bound to that employee. A refused
  signature names the device, not the person; without the second match, filtering
  by employee would silently omit every device-level refusal of their phone.

  What it deliberately does not show
  ----------------------------------
  Coordinates. A failure report showing where employees were when they were
  refused would be a location history of people who did nothing wrong. The
  distance and accuracy figures on successful events are available in the
  attendance report; refusals carry only the reason.

  Returns
      1. Up to @PageSize failures, newest first; page with @BeforeSecurityEventId.
      2. A summary by category and reason over the whole filtered range, so a
         pattern is visible without paging — the view that answers CON-01: if
         LOCATION_ACCURACY_INSUFFICIENT dominates, the 5 metre radius is
         refusing honest employees and OPEN-25 needs revisiting with evidence.

  Result codes
      0    Success
      1001 InvalidRequest
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE admin.usp_Report_GetValidationFailures
    @FromUtc     DATETIME2(3),
    @ToUtc       DATETIME2(3),
    @Category    TINYINT      = NULL,
    @SubjectKey  NVARCHAR(128) = NULL,
    @MinSeverity TINYINT      = NULL,
    @PageSize    INT          = 200,
    @BeforeSecurityEventId BIGINT = NULL,
    @ResultCode  INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    IF @FromUtc IS NULL OR @ToUtc IS NULL OR @ToUtc < @FromUtc
       OR @PageSize IS NULL OR @PageSize < 1 OR @PageSize > 1000
       OR (@Category IS NOT NULL AND @Category NOT IN (1, 2, 3, 4))
    BEGIN
        SET @ResultCode = 1001;          -- InvalidRequest
        RETURN;
    END;

    /* Devices bound to the employee named by @SubjectKey, so their device-level
       refusals are found too. */
    DECLARE @SubjectDevices TABLE (DevicePublicId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY);

    IF @SubjectKey IS NOT NULL
    BEGIN
        INSERT INTO @SubjectDevices (DevicePublicId)
        SELECT d.DevicePublicId
        FROM core.Device AS d
        INNER JOIN core.MobileUser AS u ON u.MobileUserId = d.MobileUserId
        WHERE u.UserId = @SubjectKey;
    END;

    /* Classified once, so the page and the summary cannot disagree. */
    CREATE TABLE #Failures
    (
        SecurityEventId BIGINT        NOT NULL PRIMARY KEY,
        Category        TINYINT       NOT NULL
    );

    INSERT INTO #Failures (SecurityEventId, Category)
    SELECT s.SecurityEventId, c.Category
    FROM audit.SecurityEvent AS s
    CROSS APPLY admin.ufn_Report_ValidationFailureCategory(s.EventType, s.ReasonCode) AS c
    WHERE s.OccurredUtc >= @FromUtc
      AND s.OccurredUtc <= @ToUtc
      AND (@MinSeverity IS NULL OR s.Severity >= @MinSeverity)
      AND (@SubjectKey IS NULL
           OR s.SubjectKey = @SubjectKey
           OR s.DevicePublicId IN (SELECT DevicePublicId FROM @SubjectDevices))
      AND (@Category IS NULL OR c.Category = @Category)
    OPTION (RECOMPILE);

    ------------------------------------------------------------------
    -- 1. The failures themselves, newest first.
    ------------------------------------------------------------------
    SELECT TOP (@PageSize)
        s.SecurityEventId,
        s.OccurredUtc,
        f.Category,
        s.EventType,
        s.Severity,
        s.ReasonCode,
        s.SubjectType,
        s.SubjectKey,
        s.DevicePublicId,
        /* The employee behind the event: named directly by a sign-in failure,
           or through the device a refused signature names. */
        COALESCE(u.UserId,     du.UserId)     AS EmployeeUserId,
        COALESCE(u.FirstName,  du.FirstName)  AS FirstName,
        COALESCE(u.LastName,   du.LastName)   AS LastName,
        COALESCE(u.Department, du.Department) AS Department,
        s.SourceApplication,
        s.CorrelationId,
        t.commit_time    AS LedgerCommitTime,
        t.principal_name AS LedgerPrincipalName
    FROM #Failures AS f
    INNER JOIN audit.SecurityEvent AS s ON s.SecurityEventId = f.SecurityEventId
    LEFT JOIN core.MobileUser AS u
           ON u.UserId = s.SubjectKey
          AND s.SubjectType = 1
    LEFT JOIN core.Device AS d
           ON d.DevicePublicId = s.DevicePublicId
    LEFT JOIN core.MobileUser AS du
           ON du.MobileUserId = d.MobileUserId
    LEFT JOIN sys.database_ledger_transactions AS t
           ON t.transaction_id = s.ledger_start_transaction_id
    WHERE (@BeforeSecurityEventId IS NULL OR s.SecurityEventId < @BeforeSecurityEventId)
    ORDER BY s.SecurityEventId DESC;

    ------------------------------------------------------------------
    -- 2. A summary by category and reason over the whole filtered range.
    ------------------------------------------------------------------
    SELECT
        f.Category,
        s.ReasonCode,
        COUNT(*)                        AS EventCount,
        COUNT(DISTINCT s.SubjectKey)    AS DistinctSubjects,
        MIN(s.OccurredUtc)              AS FirstOccurredUtc,
        MAX(s.OccurredUtc)              AS LastOccurredUtc
    FROM #Failures AS f
    INNER JOIN audit.SecurityEvent AS s ON s.SecurityEventId = f.SecurityEventId
    GROUP BY f.Category, s.ReasonCode
    ORDER BY EventCount DESC;

    SET @ResultCode = 0;
END;
GO

PRINT 'admin.usp_Report_GetValidationFailures deployed.';
GO
