# ClockInXtra

An on-premises clock-in / clock-out system. Employees clock in with a Flutter app from within a configured radius of an office. Administrators manage employees, devices, offices, settings and reports in an ASP.NET Core portal. Everything runs on infrastructure the organisation controls: Windows Server, IIS and SQL Server, with no cloud services (Claude.md §2.1).

| Component | Technology | Folder |
|---|---|---|
| Mobile app | Flutter 3.47 (Dart 3.13), Riverpod | `mobile/` |
| Mobile API | ASP.NET Core 10 Web API | `src/Attendance.Api` |
| Administration portal | ASP.NET Core 10 MVC | `src/Attendance.Admin` |
| Domain, application, infrastructure | Clean Architecture; Dapper → stored procedures only | `src/Attendance.Domain`, `.Application`, `.Infrastructure` |
| Database | SQL Server 2022+ (append-only ledger tables for audit) | `database/` |
| Operations | Ledger digest export and verification | `ops/` |

**Status:** Android, API, portal and database are complete and tested. iOS is not yet deployable (it needs native code built on a Mac), and several business decisions remain open. See `docs/architecture/06-implementation-status.md`.

## How the pieces fit

```
Employee phone ──HTTPS, signed requests──▶ Reverse proxy (DMZ) ──▶ Attendance.Api ──┐
                                                                                     ├──▶ SQL Server
Administrator ──HTTPS (internal)──────────────────────────────▶ Attendance.Admin ──┘   (stored procedures only)

Attendance.Api, Attendance.Admin ──▶ Attendance.Infrastructure ──▶ Attendance.Application ──▶ Attendance.Domain
```

Each phone holds a hardware-backed signing key, attested at registration and approved by an administrator. Every attendance request is signed (RFC 9421). Clock-in also needs the employee's password and a TOTP code. The server decides the time, the location outcome and the attendance state; the phone only displays them.

## Documentation

| | |
|---|---|
| Requirements, assumptions, open decisions | `docs/architecture/01-requirements-register.md` |
| Threat model | `docs/architecture/02-threat-model.md` |
| Solution architecture, signing profile, endpoints | `docs/architecture/03-solution-architecture.md` |
| Database design and security model | `docs/architecture/04-database-architecture.md` |
| Result codes | `docs/architecture/05-result-codes.md` |
| What is built and verified | `docs/architecture/06-implementation-status.md` |
| Deploying SQL Server, IIS, Android, iOS | `docs/deployment/01`–`04` |
| Security configuration and review findings | `docs/deployment/05-security-configuration.md` |
| Running it day to day: checks, playbooks, escalation | `docs/operations/01-runbook.md` |
| Measured capacity and how to size a deployment | `docs/operations/02-capacity.md` |
| Mobile app | `mobile/README.md` |

## Prerequisites for development

- Windows with SQL Server 2022 or later (Developer edition is fine) and `sqlcmd`
- .NET SDK 10.0.300 or later 10.x (pinned in `global.json`)
- Flutter 3.47.x stable, Android SDK, and an emulator or test device
- For iOS: macOS with Xcode (see `docs/deployment/04-ios.md`)

## Build

```bash
dotnet build ClockInXtra.slnx
```

The build treats warnings as errors and audits NuGet packages for known vulnerabilities on every restore.

## Run locally

**1. Database** — from the `database` folder:

```bash
sqlcmd -S . -E -C -b -i deploy\00_create_database.sql
sqlcmd -S . -d ClockInXtra -E -C -b -I -i deploy\01_run_all.sql
```

The development settings connect with Windows authentication to `Server=.;Database=ClockInXtra`.

**2. First administrator** (once):

```bash
dotnet run --project src/Attendance.Admin -- --create-first-administrator
```

Enrol the printed `otpauth://` URI in an authenticator app.

**3. Portal and API:**

```bash
dotnet run --project src/Attendance.Admin --launch-profile https   # https://localhost:7196
dotnet run --project src/Attendance.Api --launch-profile http      # http://localhost:5047
```

In the portal, set the business time zone and the attendance times (Settings), add an office (Locations), and create an employee with an authenticator (Employees). Attendance is refused with `ATTENDANCE_NOT_CONFIGURED` until the required settings exist. That is deliberate: the system won't guess a business rule.

**4. Mobile app:** see `mobile/README.md`.

In Development, the key ring may live in the user profile. Outside Development, both hosts refuse to start without a shared, certificate-protected key ring (`docs/deployment/02-iis.md` §4).

## Test

```bash
# .NET: each project separately, or scripts/run-tests.ps1 for all of them.
# Always pass --project; the positional form silently runs zero tests.
dotnet test --project tests/Attendance.Domain.Tests
dotnet test --project tests/Attendance.Application.Tests
dotnet test --project tests/Attendance.Infrastructure.Tests   # needs the local database
dotnet test --project tests/Attendance.Api.Tests              # needs the local database
dotnet test --project tests/Attendance.Admin.Tests            # needs the local database
dotnet test --project tests/Attendance.Database.Tests         # concurrency + least privilege

# Database procedures, end to end (development/test databases only)
cd database
sqlcmd -S . -d ClockInXtra -E -C -b -I -i tests\smoke_attendance.sql

# Mobile
cd mobile
flutter test
flutter test integration_test -d <device-id>
```

Tests that use the database create their own fixtures and remove them afterwards. Rows written to the audit ledger can't be removed, by design; they are harmless test entries. `CLOCKINXTRA_TEST_CONNECTION` points the integration tests at another instance.

### Known development-machine issue

If Android builds fail with *Unable to establish loopback connection*, the JDK can't create its AF_UNIX socket in the user's `TEMP` folder (seen with some endpoint-security configurations). For the build, set `TEMP` and `TMP` to a plain directory such as `C:\buildtmp`.

## Principles the code keeps

- **No cloud, no EF, no inline SQL.** C# → repository → Dapper → stored procedure. SQL appears in C# only in test fixtures.
- **Unset means unset.** Business rules that haven't been decided refuse the operation instead of defaulting (§15, §68).
- **Fail closed.** A check that can't run (integrity, attestation, key ring) is treated as a failure.
- **The server is authoritative** for time, location outcome and attendance state; the phone is an untrusted client (§65).
