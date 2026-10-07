# 04 — Database Architecture

| Item | Value |
|---|---|
| Document | ClockInXtra — Database Architecture |
| Phase | 4 of 24 |
| Version | 0.1 (draft) |
| Date | 2026-09-12 |
| Target | SQL Server 2022 (16.x), compatibility level 160 |
| Authoritative artefacts | `database/**` scripts (Phase 5–6). This document records the design decisions behind them |

---

## 1. Design decisions

| ID | Decision | Rationale |
|---|---|---|
| DB-01 | **Schema-based privilege separation.** Tables in `core`; stored procedures in `mobile`, `admin`, `job`, and break-glass `recovery`; ledger audit tables in `audit` | The API login gets `EXECUTE` on `mobile` only, the portal on `admin` only, the maintenance account on `job` only. A compromised internet-facing API cannot reach administrative procedures (TD-15, TH-28) |
| DB-02 | **No table permissions for application logins.** All access flows through stored procedures owned by `dbo`, relying on ownership chaining | Satisfies §4 and §48. `SELECT` on a table is impossible even with full control of the application process |
| DB-03 | **Two-part identity: internal `INT`/`BIGINT IDENTITY` primary keys plus external `uniqueidentifier` public identifiers** for anything a client or URL can see (`DevicePublicId`, `AttendancePublicId`, `MobileUserPublicId`) | Keeps joins narrow while preventing enumeration of sequential IDs across the internet-facing API (TH-12) |
| DB-04 | **All persisted system timestamps are UTC `datetime2(3)`**, column names end in `Utc`. Business-local values are derived, never stored as the source of truth | §31. Millisecond precision is ample for attendance and keeps rows compact |
| DB-05 | **Attendance date is computed inside SQL** with `AT TIME ZONE` from the configured Windows time-zone ID, validated against `sys.time_zone_info` | One authoritative clock and calendar for all application servers (TD-09) |
| DB-06 | **Status columns are `tinyint` with `CHECK` constraints**, mirrored one-to-one by C# enums, documented in the table comments | Avoids a proliferation of lookup tables while keeping values constrained. Reference data that administrators can extend (e.g. offices) stays in real tables |
| DB-07 | **`rowversion` on mutable entities** (`MobileUser`, `Device`, `OfficeLocation`, `Administrator`, `ApplicationSetting`) | Optimistic concurrency for portal edits (§27) |
| DB-08 | **Audit and security events are append-only ledger tables** in the `audit` schema; everything else is a regular table | Tamper evidence against administrators and DBAs (TH-40). Ledger is available in every SQL Server 2022 edition |
| DB-09 | **Attendance and location evidence stay in regular tables** | Append-only ledger tables cannot delete old rows, which would make any retention policy impossible (CON-11). Integrity for these tables comes from `EXECUTE`-only access, constraints and the audit trail |
| DB-10 | **Every procedure returns a `@ResultCode INT OUTPUT`** from a fixed catalogue, plus a result set where applicable. Unexpected errors `THROW`; expected business outcomes do not | Maps cleanly to the API error codes (§34, §60) without parsing SQL error text |
| DB-11 | **No dynamic SQL anywhere.** Report filtering uses `OPTION (RECOMPILE)` with the standard nullable-parameter pattern | §4 and §49. `RECOMPILE` avoids parameter-sniffing problems that usually motivate dynamic SQL |
| DB-12 | **`READ COMMITTED` isolation with explicit `UPDLOCK, HOLDLOCK`** on the attendance key range inside the clock-in/out transactions | Prevents duplicate clock-ins across IIS nodes without imposing snapshot isolation on the whole database (§45) |
| DB-13 | **Savepoint-aware transactions.** A procedure that opens a transaction records `@@TRANCOUNT` first: it issues `BEGIN TRANSACTION` only when it is the outermost caller, and `SAVE TRANSACTION` otherwise. Rejection paths roll back to the savepoint, never with a bare `ROLLBACK TRANSACTION` | `ROLLBACK TRANSACTION` in T-SQL rolls back **every** nesting level and sets `@@TRANCOUNT` to 0. Without this, a business rejection — "already clocked in", "device revoked" — silently destroys an outer transaction the caller opened, and SQL Server then raises error 266 on return. Found by executing a procedure inside a transaction; see `06-implementation-status.md` |

**Transaction template** (every procedure that writes follows it):

```sql
DECLARE @OuterTranCount INT = @@TRANCOUNT;

BEGIN TRY
    IF @OuterTranCount = 0 BEGIN TRANSACTION; ELSE SAVE TRANSACTION <ProcedureName>;

    -- business rejection:
    IF <refused>
    BEGIN
        IF @OuterTranCount = 0 ROLLBACK TRANSACTION; ELSE ROLLBACK TRANSACTION <ProcedureName>;
        SET @ResultCode = <code>;
        RETURN;
    END;

    IF @OuterTranCount = 0 COMMIT TRANSACTION;   -- an inner caller commits nothing
END TRY
BEGIN CATCH
    -- XACT_STATE() = -1 means the transaction is doomed and a savepoint cannot
    -- be used; the error must propagate so the outermost caller aborts.
    IF XACT_STATE() = -1 AND @OuterTranCount > 0 THROW;
    IF XACT_STATE() <> 0 AND @OuterTranCount = 0 ROLLBACK TRANSACTION;
    ELSE IF XACT_STATE() = 1 ROLLBACK TRANSACTION <ProcedureName>;
    ...
END CATCH;
```

---

## 2. Naming conventions

| Object | Convention | Example |
|---|---|---|
| Table | Singular `PascalCase`, in its schema | `core.OfficeLocation` |
| Primary key | `PK_<Schema>_<Table>` | `PK_core_Device` |
| Foreign key | `FK_<Child>_<Parent>_<Column>` | `FK_Device_MobileUser_MobileUserId` |
| Unique constraint/index | `UQ_<Table>_<Columns>` / `UX_<Table>_<Columns>` | `UX_Attendance_MobileUserId_AttendanceDate` |
| Check constraint | `CK_<Table>_<Rule>` | `CK_OfficeLocation_Latitude` |
| Index | `IX_<Table>_<Columns>` | `IX_AttendanceEvent_OccurredUtc` |
| Default | `DF_<Table>_<Column>` | `DF_Device_CreatedUtc` |
| Procedure | `<schema>.usp_<Entity>_<Action>` | `mobile.usp_Attendance_ClockIn` |

---

## 3. Entity catalogue

Each entity is justified, as §27 requires. Entities listed in `Claude.md` that are **not** created are explained in §3.3.

### 3.1 Identity, credentials and devices

| Table | Purpose | Key columns and constraints |
|---|---|---|
| `core.MobileUser` | Employee who clocks in (§17) | `MobileUserId` PK; `MobileUserPublicId` unique; `UserId` (login identifier) unique, case-insensitive; `EmployeeNumber` unique where not null; names, email, phone, department, job title (all optional per §17's caution against inventing mandatory fields); `Status` tinyint (0 Inactive, 1 Active, 2 Suspended); `rowversion` |
| `core.EmployeeCredential` | Password hash for the local identity provider | One row per user (`UNIQUE(MobileUserId)`); `HashFormat` varchar(32) (`pbkdf2-sha512`), `Iterations` int, `Salt` varbinary(32), `Hash` varbinary(64), `MustChange` bit, `LastChangedUtc`. Separated from `MobileUser` so an AD provider can be adopted without touching profile data (DEC-01) |
| `core.MfaCredential` | TOTP enrolment (§13) | `MobileUserId` FK; `SecretProtected` varbinary(max) (Data Protection ciphertext, never plaintext); `Algorithm`, `Digits`, `PeriodSeconds`; `Status` tinyint (0 Pending, 1 Active, 2 Revoked); `LastAcceptedTimeStep` bigint for replay protection; `EnrolledUtc`, `RevokedUtc` |
| `core.Device` | Device binding (§18, DEC-04) | `DeviceId` PK; `DevicePublicId` unique (the `keyid` on the wire); `MobileUserId` FK; `PublicKey` varbinary(65) (uncompressed P-256 point) with `PublicKeyThumbprint` varbinary(32) unique; `Platform` tinyint; `AttestationLevel` tinyint (0 None, 1 Software, 2 Hardware); `Status` tinyint (0 PendingApproval, 1 Active, 2 Revoked); `AppVersion`, `OsVersion`, `DeviceModel` (length- and character-constrained, they are untrusted input — TH-36); `RegisteredUtc`, `ApprovedUtc`, `ApprovedByAdministratorId`, `LastSeenUtc`, `RevokedUtc`, `RevokedReason`; `rowversion`. **Filtered unique index** `UX_Device_ActiveUser ON (MobileUserId) WHERE Status = 1` enforces one active device per employee in the database, not in C# |
| `core.DeviceRegistrationChallenge` | Single-use attestation challenges | `ChallengeId` uniqueidentifier PK; `Challenge` varbinary(32); `IssuedUtc`, `ExpiresUtc`, `ConsumedUtc`, `IssuedToIpHash`. Consumed atomically with `UPDATE … OUTPUT … WHERE ConsumedUtc IS NULL` |

### 3.2 Attendance, location and request hygiene

| Table | Purpose | Key columns and constraints |
|---|---|---|
| `core.OfficeLocation` | Approved offices (§9, §19) | `OfficeLocationId` PK; `Name` unique; `Latitude` decimal(9,6) `CHECK BETWEEN -90 AND 90`; `Longitude` decimal(9,6) `CHECK BETWEEN -180 AND 180`; `AllowedRadiusMeters` decimal(6,2) `CHECK > 0`, default 5; `Status` tinyint; `CreatedUtc`, `UpdatedUtc`; `rowversion`. Decimal rather than float so stored coordinates are exact and comparable |
| `core.Attendance` | One record per employee per attendance day (ASM-02) | `AttendanceId` PK; `AttendancePublicId` unique; `MobileUserId` FK; `AttendanceDate` date; `ClockInUtc` datetime2(3) not null; `ClockOutUtc` null; `DurationMinutes` int null; `Status` tinyint (1 Open, 2 Closed, 3 Corrected); `ClockInOfficeLocationId`, `ClockOutOfficeLocationId`; `IsLateClockIn`, `IsEarlyClockOut` bit null (null until OPEN-6/7 resolved); `rowversion`. **`UX_Attendance_MobileUserId_AttendanceDate` unique** is the concurrency guarantee; `CHECK (ClockOutUtc IS NULL OR ClockOutUtc >= ClockInUtc)` |
| `core.AttendanceEvent` | Immutable per-event evidence | `AttendanceEventId` PK; `AttendanceId` FK; `EventType` tinyint (1 ClockIn, 2 ClockOut); `OccurredUtc`; `DeviceId`; `OfficeLocationId`; `DistanceMeters` decimal(8,2); `ReportedAccuracyMeters` decimal(8,2); `Platform`; `WasMockedLocation` bit; `CoordinatesProtected` varbinary(max) null (used only if OPEN-35 requires raw coordinates, always encrypted); `CorrelationId` |
| `core.RequestNonce` | Replay prevention (§37) | `DeviceId`, `Nonce` varbinary(16), `CreatedUtc`; `UX_RequestNonce_DeviceId_Nonce` unique is the replay check itself — the insert either succeeds or fails. Purged by `job.usp_Maintenance_PurgeRequestNonce` |
| `core.RequestIdempotency` | Safe retries (§53) | `DeviceId`, `IdempotencyKey` uniqueidentifier, `EndpointCode` tinyint, `RequestHash` varbinary(32), `ResultCode` int, `ResponsePayload` nvarchar(max), `CreatedUtc`; unique on `(DeviceId, IdempotencyKey)` |
| `core.AuthenticationAttempt` | Lockout counters that work across nodes (TD-10) | `SubjectType` tinyint (1 MobileUser, 2 Administrator), `SubjectKey` nvarchar(128), `FailedCount` int, `FirstFailedUtc`, `LastFailedUtc`, `LockedUntilUtc`; unique on `(SubjectType, SubjectKey)`. Counting in the database rather than in process memory is what makes lockout real in a multi-server deployment |

### 3.3 Administration and configuration

| Table | Purpose |
|---|---|
| `core.Administrator` | Portal accounts: `UserName` unique, password hash columns as in `EmployeeCredential`, `SecurityStamp` (session invalidation), `MfaCredential` link, `Status`, `LastLoginUtc`, `rowversion` |
| `core.Role`, `core.Permission`, `core.RolePermission`, `core.AdministratorRole` | Least-privilege authorization (§42). `Permission.Code` is the string used by the policy provider |
| `core.ApplicationSetting` | Configurable business and security rules (§15, §47): `SettingKey` unique, `SettingValue` nvarchar(400) **nullable**, `DataType`, `RequiresBusinessConfirmation` bit, `ConfirmedByAdministratorId`, `ConfirmedUtc`, `UpdatedUtc`, `rowversion`. A null value means "not yet decided", which is what makes `ATTENDANCE_NOT_CONFIGURED` possible instead of an invented default. Each row also defines valid input: `MinValue`/`MaxValue` (enforced by `usp_ApplicationSetting_Set` for numbers), `Unit`, and `BlankMeaning` (when set, a blank is a confirmable decision such as "Keep indefinitely"). The portal builds each input from these, so browser and database apply the same limits. Time zones are offered from `sys.time_zone_info` via `usp_TimeZone_GetAll`, the list the setting is validated against |
| `core.Department`, `core.JobTitle` | The lists an employee's department and job title are chosen from (DEC-11): unique `Name`, `IsActive`, `rowversion`. `core.MobileUser.Department` / `.JobTitle` keep the name and reference it with `ON UPDATE CASCADE`, so a rename reaches every employee and an entry in use cannot be deleted — it is withdrawn instead |
| `core.AttendanceCorrection` | Administrative corrections with maker–checker (§20, §32): original and corrected timestamps, reason, `RequestedByAdministratorId`, `ApprovedByAdministratorId`, `Status`. `CHECK (ApprovedByAdministratorId IS NULL OR ApprovedByAdministratorId <> RequestedByAdministratorId)` enforces segregation of duties in the database. Requested by Attendance Administrators, approved by Super Administrators (DEC-08, DEC-10) |

### 3.4 Audit (ledger)

| Table | Purpose |
|---|---|
| `audit.AuditLog` | `CREATE TABLE … WITH (LEDGER = ON (APPEND_ONLY = ON))`. Event type, actor type and id, subject, `OccurredUtc`, result, reason, source application, `CorrelationId`, `DevicePublicId`, and a JSON `Details` column that is explicitly documented as never carrying passwords, OTPs, secrets or keys (§32) |
| `audit.SecurityEvent` | Same ledger treatment for authentication failures, replay attempts, untrusted location sources, lockouts, and attestation rejections |

Ledger columns (`ledger_start_transaction_id` and friends) are added automatically and are excluded from `INSERT` lists.

### 3.5 Entities deliberately **not** created

| Suggested in §27 | Decision |
|---|---|
| `AttendanceEvent` as a generic event-sourcing log for all entities | Kept, but scoped strictly to attendance evidence. A general event store is not required by any stated requirement |
| Separate `Department` and `JobTitle` tables | Not created. §17 warns against inventing organisational structure; these stay as optional text on `MobileUser` until the business asks for managed lists |
| A holiday/work-calendar table | Not created. OPEN-29 is unanswered, and building a calendar model now would embed an invented business rule |
| A shift/roster model | Not created. OPEN-30 is unanswered (see ASM-02) |
| `MobileUserOfficeLocation` (restricting employees to specific offices) | Not created. OPEN-27's interim behaviour allows any active office. Adding it later is an additive migration with no data loss |

---

## 4. Stored procedure catalogue

Every procedure is complete in Phase 6. No placeholders.

### `mobile` (executed by the API login)

| Procedure | Purpose |
|---|---|
| `usp_Device_IssueRegistrationChallenge` | Create a single-use challenge |
| `usp_Device_Register` | Consume challenge, validate credentials state, insert `PendingApproval` device |
| `usp_Device_GetForSignatureVerification` | Return public key, status and user binding for a `DevicePublicId` |
| `usp_Device_TouchLastSeen` | Update `LastSeenUtc` |
| `usp_Device_GetStatus` | Registration status for the app |
| `usp_RequestNonce_TryInsert` | Replay check |
| `usp_Idempotency_TryBegin` / `usp_Idempotency_Complete` | Idempotent operation bookkeeping |
| `usp_OfficeLocation_GetActive` | Active offices for distance evaluation |
| `usp_MobileUser_GetByUserId` | User lookup for credential validation |
| `usp_EmployeeCredential_Get` | Hash parameters for verification |
| `usp_MfaCredential_GetForVerification` / `usp_MfaCredential_RecordTimeStep` | TOTP verification and replay protection |
| `usp_AuthenticationAttempt_RegisterFailure` / `usp_AuthenticationAttempt_Reset` / `usp_AuthenticationAttempt_Check` | Lockout |
| `usp_Attendance_GetCurrentStatus` | Server-authoritative clock-in state (§11) |
| `usp_Attendance_ClockIn` | Transactional clock-in (§12, §30) |
| `usp_Attendance_ClockOut` | Transactional clock-out (§14) |
| `usp_ApplicationSetting_GetMobileRuntime` | Settings the app is allowed to see (minimum version, policy flags) |
| `usp_SecurityEvent_Create` | Security event insert |

### `admin` (executed by the portal login)

User management, administrator/role/permission management, device approval and revocation, office-location CRUD and status, MFA enrolment and reset, settings read/write with confirmation, attendance search and correction workflow, all §20 reports, audit and security-event queries, and authentication procedures (`usp_Administrator_GetByUserName`, `usp_Administrator_RecordLogin`, `usp_Administrator_RotateSecurityStamp`).

### `core` (shared internals, executed by no application)

Both applications need to write audit entries, count failed logins and read attendance settings, but neither may be granted `EXECUTE` on the other's schema. These shared procedures therefore live in `core`, which **no application login can execute directly**. A `mobile.*` or `admin.*` procedure can still call them, because every object is owned by `dbo` and Microsoft documents that with an unbroken ownership chain "SQL Server only checks the EXECUTE permission for the caller, not the caller's permissions on other objects."

| Procedure | Purpose |
|---|---|
| `usp_ApplicationSetting_GetAttendanceRules` | One place that reads the attendance rules, so status, clock-in and clock-out cannot disagree |
| `usp_AuditLog_Create` | Insert into the append-only audit ledger |
| `usp_SecurityEvent_Create` | Insert into the append-only security-event ledger, where the *precise* failure reason is kept even when the API collapses it (CON-09) |
| `usp_AuthenticationAttempt_Check` / `_RegisterFailure` / `_Reset` | Account lockout that holds across all IIS nodes |
| `usp_MfaCredential_TryConsumeTimeStep` | One-time use of a TOTP time step, enforced in a single conditional `UPDATE` |

### `recovery` (executed by no application account)

`usp_Administrator_RecoverAccess` gives a named administrator a new password hash and a new authenticator secret, rotates the security stamp, clears the lockout and writes `Administrator.AccessRecovered` to the audit ledger with the operator's login and host. It exists because every other reset needs another administrator. The schema is explicitly **denied** to all three application logins: only a database administrator can run it, through the portal's `--reset-administrator` console command.

### `job` (executed by the maintenance account)

`usp_Maintenance_PurgeRequestNonce`, `usp_Maintenance_PurgeIdempotency`, `usp_Maintenance_PurgeChallenges`, `usp_Maintenance_PurgeAttendanceEvidence` (driven by retention settings; a no-op while OPEN-14 is unset).

`usp_Maintenance_GenerateLedgerDigest` returns a ledger digest to the caller rather than storing it — a digest kept only inside the database it describes proves nothing. `usp_Maintenance_VerifyLedger` verifies the ledger against a JSON array of previously exported digests and returns 1080 on a mismatch. Both run `WITH EXECUTE AS OWNER`, so the maintenance account needs nothing beyond `EXECUTE` on `job`. SQL Server's automatic digest storage targets Azure immutable storage, which §2.1 rules out; the scripts in `ops/` export to, and verify from, organisation-controlled write-once storage instead.

---

## 5. Database security model

| Principal | Type | Rights |
|---|---|---|
| `app_mobile` | Login → user | `EXECUTE` on schema `mobile` only |
| `app_admin` | Login → user | `EXECUTE` on schema `admin`, plus database-level `VIEW LEDGER CONTENT` (below) |
| `app_jobs` | Login → user | `EXECUTE` on schema `job` only |
| `db_reporting` *(optional)* | User | `SELECT` on specific reporting views only, if the organisation later wants BI access |

- No application principal is a member of `db_owner`, `db_datareader` or `db_datawriter` (§48).
- `app_admin` holds `VIEW LEDGER CONTENT` because the audit trail and the validation-failure report read `sys.database_ledger_transactions` for each row's ledger commit time and principal, and that view is checked against the caller — ownership chaining through the procedure does not cover it. Without the grant both pages fail in production; development hid this because the portal connects as the database owner there (found in Phase 20). The `DENY` on the `audit` schema still stands: verified by executing as a user holding exactly these grants, the reports succeed and `SELECT` on `audit.AuditLog` is refused.
- `DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::core` is stated explicitly for the application users, so an accidental future `GRANT` cannot silently widen access.
- Where Active Directory is available, the logins are gMSA-backed Windows logins so no password exists in configuration.
- Connections require encryption (`Encrypt=Mandatory` is the driver default in Microsoft.Data.SqlClient 7.x).
- TDE and backup encryption are available in Standard and Enterprise editions and are recommended; the certificates must be backed up separately from the database.

---

## 6. Retention and ledger interaction (CON-11)

| Data | Interim behaviour | Blocked on |
|---|---|---|
| `RequestNonce`, `RequestIdempotency` | Purged automatically (operational data, short windows) | — |
| `Attendance`, `AttendanceEvent` | Retained indefinitely (DEC-07): the retention settings are confirmed blank and the purge procedure deletes nothing. Values below 1 day are refused | DEC-07; legal check OPEN-41 |
| `audit.AuditLog`, `audit.SecurityEvent` | Append-only ledger, **never purged** | OPEN-14 and Legal (if deletion is mandated, the design moves to period-scoped audit tables) |

Database digests are generated on a schedule and exported to organisation-controlled WORM storage, then verified periodically. Verification failure is an incident, not a warning.

---

## 7. Deployment and upgrade approach

- Scripts are **idempotent and ordered**: `database/deploy/00_run_all.sql` executes schema, security, programmability and seed scripts in sequence.
- Every script is re-runnable (`IF NOT EXISTS` guards, `CREATE OR ALTER` for procedures).
- A `core.SchemaVersion` table records applied migration scripts with checksum and timestamp, so upgrades are traceable.
- Seed data inserts permissions, roles, the `SchemaVersion` row and the **settings rows with null values and `RequiresBusinessConfirmation = 1`**. No business value is seeded.
- Deployment is documented for `sqlcmd` and for SSMS, with the least-privilege grants applied last.
