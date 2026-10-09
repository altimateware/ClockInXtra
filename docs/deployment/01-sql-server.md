# 01 — Deploying the database (SQL Server)

Everything the applications do in the database goes through stored procedures (Claude.md §4). This guide creates the database, deploys every object, applies least-privilege security, and sets up audit-ledger verification.

**Verified against:** SQL Server 2022 Developer 16.0.1200 on the development machine, where every script below has been run. Production-scale concerns (availability groups, TDE, backup encryption) follow Microsoft's documentation and are marked as organisation decisions where they depend on one.

---

## 1. Prerequisites

| Item | Requirement |
|---|---|
| SQL Server | 2022 or later (append-only **ledger tables** are required, DB-08). Edition depends on OPEN-17: Standard supports *basic* availability groups (two replicas, one database); Enterprise supports full Always On AGs |
| sqlcmd | A version supporting `-C` and `:r` (SQLCMD mode) |
| Collation | The database is created as `Latin1_General_100_CI_AS` regardless of the instance collation. The scripts handle a different instance collation (the common `SQL_Latin1_General_CP1_CI_AS` included) |
| Operator | A login that can create databases and users (e.g. `sysadmin` during installation only) |
| Transport | Force encryption on the instance with a certificate from the organisation's PKI (OPEN-21). The applications connect with `Encrypt=True` and validate the certificate |

---

## 2. Create the database

From the `database` directory:

```bash
sqlcmd -S <server> -E -C -b -i deploy\00_create_database.sql
```

This creates `ClockInXtra` (override with `-v DatabaseName=...`) and sets full recovery, read-committed snapshot and snapshot isolation. Re-running it against an existing database only re-applies those settings.

## 3. Deploy schema, procedures and seed data

```bash
sqlcmd -S <server> -d ClockInXtra -E -C -b -I -i deploy\01_run_all.sql
```

- **Run from the `database` directory**: the script includes files with `:r`, relative to where sqlcmd runs.
- **`-I` is required.** It turns on `QUOTED_IDENTIFIER`, which filtered indexes need; sqlcmd defaults it to off.
- **`-b` stops on the first error.** Always use it, and read the output: the script finishes with post-deployment checks (every table present, the audit tables really are ledger tables, the DEC-04 index exists) and lists **settings still awaiting a business decision**.
- The script is re-runnable. Tables are created only if missing; procedures are `CREATE OR ALTER`; seed data is merged.

## 4. Application logins and least privilege

Logins are **not** created by the scripts, because a SQL login needs a password and passwords never go into source control (§47).

**Preferred — Windows authentication with group Managed Service Accounts** (no password exists anywhere):

```sql
CREATE LOGIN [ALTIMATEWARE\gmsa-cix-api$]   FROM WINDOWS;
CREATE LOGIN [ALTIMATEWARE\gmsa-cix-admin$] FROM WINDOWS;
CREATE LOGIN [ALTIMATEWARE\gmsa-cix-jobs$]  FROM WINDOWS;
```

**Fallback — SQL authentication.** The operator types the password from the organisation's secret store; it goes into the application configuration through the same secret store, never into a file in source control.

Then apply the grants, naming the three logins:

```bash
sqlcmd -S <server> -d ClockInXtra -E -C -b -I ^
  -v MobileUser="ALTIMATEWARE\gmsa-cix-api$" AdminUser="ALTIMATEWARE\gmsa-cix-admin$" JobUser="ALTIMATEWARE\gmsa-cix-jobs$" ^
  -i security\10_security_users_grants.sql
```

What each account can then do:

| Account | Rights |
|---|---|
| API (`MobileUser`) | `EXECUTE` on schema `mobile`. Nothing else |
| Portal (`AdminUser`) | `EXECUTE` on schema `admin`, plus `VIEW LEDGER CONTENT` (the audit pages show ledger commit times; see `04-database-architecture.md` §5) |
| Maintenance (`JobUser`) | `EXECUTE` on schema `job` |

Every account is explicitly **denied** direct access to the `core` and `audit` tables and to the other applications' schemas. The script prints the resulting permissions and fails if an account is a member of `db_owner`, `db_datareader` or `db_datawriter`.

These grants are tested: `tests/Attendance.Database.Tests/LeastPrivilegeTests.cs` applies this very script to throwaway users and checks each account can do its job and nothing more.

## 5. Scheduled maintenance

Run as the maintenance account, for example from SQL Server Agent or Task Scheduler:

| Procedure | Suggested schedule | Purpose |
|---|---|---|
| `job.usp_Maintenance_PurgeRequestNonce` | Hourly | Removes replay-protection nonces past their window (keeps a safety floor) |
| `job.usp_Maintenance_PurgeIdempotency` | Hourly | Removes completed idempotency records |
| `job.usp_Maintenance_PurgeChallenges` | Daily | Removes expired device-registration challenges |
| `job.usp_Maintenance_PurgeAttendanceEvidence` | Daily | Applies retention to attendance records and evidence. Retention is **indefinite** (DEC-07), so it currently deletes nothing and reports success; it starts purging only if a number of days is set |

## 6. Audit ledger digests — required for tamper evidence

`audit.AuditLog` and `audit.SecurityEvent` are append-only ledger tables. That makes tampering **detectable**, but only against digests kept somewhere the database administrators cannot change (threat TH-40). SQL Server's automatic digest storage uses Azure, which §2.1 rules out, so digests are exported by script to storage the organisation controls:

```powershell
# Hourly, as a Windows identity mapped to the maintenance account:
.\ops\Export-LedgerDigest.ps1 -Server sql01.corp.local -DigestDirectory \\worm01\clockinxtra-ledger

# Daily, and before audit evidence is handed to anyone:
.\ops\Test-LedgerIntegrity.ps1 -Server sql01.corp.local -DigestDirectory \\worm01\clockinxtra-ledger
```

- `-DigestDirectory` should be **write-once storage**, or at least a share on another server where the SQL Server administrators and service account can write but not modify or delete.
- `Test-LedgerIntegrity.ps1` exits **0** when verified, **2 when verification FAILS** (a security incident: preserve the database and the digest store and do not attempt a repair), and **1** when it could not verify at all. Alert on anything but 0.
- Both scripts run on Windows PowerShell 5.1 with nothing installed. Add `-Credential` for a SQL login.

## 7. Backup and encryption

These depend on organisation decisions (OPEN-18 disaster recovery, OPEN-19 backups) and are listed so that they are decided, not assumed:

- **Backups:** full recovery model is set, so schedule full + log backups to meet the recovery point objective. Use `WITH ENCRYPTION` (supported for native backups) with a certificate whose private key is itself backed up off-server.
- **Encryption at rest:** Transparent Data Encryption is available in Standard edition from SQL Server 2019. TOTP secrets are already encrypted by the application (Data Protection); TDE additionally covers personal data and backups.
- **Restore drills** must include the Data Protection key ring and its certificate (see `02-iis.md` §4): a database restored without the key ring leaves every authenticator secret undecryptable, and every employee would need re-enrolling.

## 8. Upgrades

Run `deploy\01_run_all.sql` again with the new scripts. It records what it applied in `core.SchemaVersion`. Take a backup first; ledger tables cannot be rolled back by deleting rows, so restores are the only rollback for the audit trail.

## 9. Verifying a development or test deployment

`database/tests/smoke_attendance.sql` exercises the procedures end to end (194 cases). **Never run it against production:** it creates and deletes test data, and its audit rows remain in the ledger permanently.

```bash
sqlcmd -S <server> -d ClockInXtra -E -C -b -I -i tests\smoke_attendance.sql
```
