# 03 — Solution Architecture

| Item | Value |
|---|---|
| Document | ClockInXtra — Solution Architecture |
| Phase | 3 of 24 |
| Version | 0.1 (draft) |
| Date | 2026-09-11 |
| Inputs | `01-requirements-register.md`, `02-threat-model.md` |
| Normative for | Phases 4–24 |

---

## 1. Architecture principles

1. **The mobile app is an untrusted client.** Every authorization and business decision is made server-side.
2. **The database is the integrity boundary for attendance.** Uniqueness, locking and time come from SQL Server, not from application code (§28, §45).
3. **Cheap checks before expensive ones.** Signature verification (microseconds) precedes password hashing (hundreds of milliseconds), which protects the system under abuse.
4. **Least privilege everywhere**, including two separate SQL logins with `EXECUTE`-only rights on their own schema.
5. **No invented business rules.** Unset business settings cause a clear refusal, never a guessed default (§15, §68).
6. **Nothing leaves the organisation's infrastructure**, apart from the platform-vendor attestation touchpoints listed in CON-02, which are subject to OPEN-33.
7. **Standard building blocks only.** No bespoke cryptographic constructions.

---

## 2. Component overview

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ Employee device (Android / iOS)                                              │
│  Flutter app  ──uses──▶ Device Trust plugin (Kotlin/Swift, in-repo)          │
│     │                     • P-256 key in Keystore(TEE/StrongBox)/Secure Encl.│
│     │                     • key attestation, signing                         │
│     │                     • root/jailbreak + emulator heuristics             │
│     │                     • simulated-location flag (iOS 15+)                │
│     └──uses──▶ geolocator, flutter_secure_storage, dio                       │
└───────────────────────────────┬──────────────────────────────────────────────┘
                                │ HTTPS + RFC 9421 signed requests
┌───────────────────────────────▼──────────────────────────────────────────────┐
│ DMZ: reverse proxy (IIS ARR / organisation WAF)                              │
│  TLS termination + re-encryption, connection & rate limits, request caps     │
└───────────────────────────────┬──────────────────────────────────────────────┘
┌───────────────────────────────▼──────────────────────────────────────────────┐
│ Application tier (internal)                                                   │
│  ┌────────────────────────────┐        ┌──────────────────────────────────┐  │
│  │ Attendance.Api (IIS, ≥2)   │        │ Attendance.Admin (IIS, internal) │  │
│  │  signature middleware      │        │  cookie auth + admin TOTP        │  │
│  │  rate limiting, validation │        │  permission policies, antiforgery│  │
│  └───────────┬────────────────┘        └───────────────┬──────────────────┘  │
│              │        Attendance.Application / .Domain │                     │
│              │        Attendance.Infrastructure (Dapper)                     │
└──────────────┼────────────────────────────────────────┼─────────────────────┘
               │ SQL login: app_mobile (EXECUTE mobile.*)│ app_admin (EXECUTE admin.*)
┌──────────────▼────────────────────────────────────────▼─────────────────────┐
│ Data tier: SQL Server 2022 — tables, stored procedures, ledger audit tables  │
│ Data Protection key ring on ACL'd SMB share, protected by PKI certificate    │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Solution layout

```text
ClockInXtra.sln
├── src/
│   ├── Attendance.Domain/              # entities, value objects, enums, domain services, exceptions
│   ├── Attendance.Application/         # use cases, DTOs, abstractions, validators, policies
│   ├── Attendance.Infrastructure/      # Dapper repositories, crypto, TOTP, attestation, time
│   ├── Attendance.Api/                 # mobile REST API + middleware/filters
│   └── Attendance.Admin/               # ASP.NET Core MVC portal
├── database/
│   ├── schema/                         # schemas, tables, constraints, indexes
│   ├── programmability/                # stored procedures (one file each)
│   ├── security/                       # logins, users, roles, grants
│   ├── seed/                           # reference data, provisional settings rows
│   └── deploy/                         # ordered deployment + upgrade scripts
├── mobile/clockinxtra_app/             # Flutter application
│   └── packages/device_trust/          # in-repo platform plugin (Kotlin + Swift)
├── tests/
│   ├── Attendance.Domain.Tests/
│   ├── Attendance.Application.Tests/
│   ├── Attendance.Infrastructure.Tests/   # integration, real SQL Server
│   ├── Attendance.Api.Tests/              # WebApplicationFactory + security tests
│   ├── Attendance.Admin.Tests/
│   └── Attendance.Database.Tests/         # stored procedure + concurrency tests
└── docs/architecture/
```

**Project references (dependencies point inward only):**

| Project | References |
|---|---|
| `Attendance.Domain` | *(none)* |
| `Attendance.Application` | Domain |
| `Attendance.Infrastructure` | Application, Domain |
| `Attendance.Api` | Application, Domain, Infrastructure *(composition root only)* |
| `Attendance.Admin` | Application, Domain, Infrastructure *(composition root only)* |

An architecture test asserts that `Attendance.Domain` references no other project and that `Application` does not reference `Infrastructure`.

---

## 4. Technology stack (verified 2026-09-11)

| Concern | Choice | Version |
|---|---|---|
| Runtime | .NET (LTS) | 10.0.x |
| API | ASP.NET Core Web API (controllers) | 10 |
| Admin | ASP.NET Core MVC + Razor views | 10 |
| Data access | Dapper → stored procedures only | 2.1.79 |
| SQL driver | Microsoft.Data.SqlClient (`Encrypt=Mandatory`) | 7.0.3 |
| Database | SQL Server 2022 | 16.x |
| Validation | FluentValidation (core package only) | 12.1.1 |
| Versioning | Asp.Versioning.Mvc (+ ApiExplorer) | 10.2.1 |
| Logging | Serilog.AspNetCore | 10.0.0 |
| OpenAPI | Microsoft.AspNetCore.OpenApi + Swashbuckle SwaggerUI (non-Production only) | 10.x / 10.2.3 |
| TOTP | Otp.NET | 1.4.1 |
| CBOR (Apple attestation) | System.Formats.Cbor | 10.0.12 |
| ASN.1 (Android attestation) | `System.Formats.Asn1` (built in) | — |
| Tests | xunit.v3 | 4.0.0 |
| Mobile | Flutter / Dart | 3.47.4 / 3.13.3 |
| Mobile state + DI | flutter_riverpod | 3.4.3 |
| Mobile HTTP | dio | 5.11.1 |
| Mobile secure storage | flutter_secure_storage | 11.x |
| Mobile location | geolocator | 14.0.3 |
| Device trust | in-repo `device_trust` plugin | — |

---

## 5. Identity and trust model

§40 requires these five concerns to be distinguished. They are separate mechanisms here:

| Concern | Mechanism | Where verified |
|---|---|---|
| 1. Employee identity | User ID + password via `IEmployeeCredentialValidator` (local store now, AD later — DEC-01) | Clock-in and device registration only |
| 2. Device identity | Non-exportable P-256 key registered to one employee; `DeviceId` is the `keyid` | Every mobile request |
| 3. Request integrity | RFC 9421 signature over method, authority, path and body digest | Every mobile request |
| 4. API authorization | Device status (`Active`) + binding to the claimed employee + endpoint policy | Every mobile request |
| 5. MFA | TOTP (RFC 6238) verified server-side, with replay protection | Registration and clock-in |

**Why no bearer tokens** (§40 asks for justification): a token is a bearer secret that must be stored on an untrusted device, refreshed, rotated and revoked, and it grants access to anyone who steals it. A hardware-bound key cannot be exported, is revoked centrally with immediate effect on the next request, requires no refresh flow, and gives per-request integrity at the same time. The trade-off accepted is a database lookup of the device record per request.

### 5.1 Device lifecycle

```text
        ┌────────────┐  register (password+TOTP+attestation, self-signed)
        │ (none)     │──────────────────────────────▶┌─────────────────┐
        └────────────┘                                │ PendingApproval │
                                                      └────────┬────────┘
                              admin approves (revokes previous active device) │
                                                      ┌────────▼────────┐
                    ┌────── admin revokes ────────────│ Active          │
                    │                                 └────────┬────────┘
              ┌─────▼──────┐                  employee exits / lost device │
              │ Revoked    │◀──────────────────────────────────────────────┘
              └────────────┘   (terminal; a new device requires new registration)
```

- One `Active` device per employee (DEC-04), enforced by a filtered unique index on `(MobileUserId) WHERE Status = Active`.
- A device row is bound to exactly one employee; the public key is unique across the table.
- Revocation takes effect on the **next request**, since device status is read during signature verification.

---

## 6. Mobile request signing profile (normative)

This is a constrained profile of **RFC 9421 (HTTP Message Signatures, Standards Track, February 2024)** with **RFC 9530 `Content-Digest`**. No custom cryptography is introduced.

### 6.1 Client requirements

| Element | Rule |
|---|---|
| Algorithm | `ecdsa-p256-sha256` |
| Key | P-256, generated in Android Keystore (`KeyGenParameterSpec`, `PURPOSE_SIGN`, `DIGEST_SHA256`, `setUnlockedDeviceRequired(true)`, StrongBox where available) or iOS `SecureEnclave.P256.Signing.PrivateKey` with access control `WhenUnlockedThisDeviceOnly`. Never exportable, never backed up |
| Covered components | `("@method" "@authority" "@path" "content-digest")` — `content-digest` omitted only for requests without a body |
| Parameters | `created` (UNIX seconds, required), `nonce` (128-bit random, base64url, required), `keyid` (the `DeviceId`, required), `alg="ecdsa-p256-sha256"` (required), `tag="clockinxtra-mobile-v1"` (required) |
| `Content-Digest` | `sha-256=:<base64>:` computed over the exact request body bytes as transmitted |
| Signature encoding | **Raw `r‖s`: two 32-byte big-endian integers concatenated into 64 bytes** (RFC 9421 §3.3.4), base64 in the `Signature` field |
| Platform note | Android's `SHA256withECDSA` produces **DER**, which the plugin converts to raw `r‖s`. iOS CryptoKit `ECDSASignature.rawRepresentation` is already raw |

Example (line breaks added for readability only):

```http
POST /api/v1/mobile/attendance/clock-out HTTP/1.1
Host: attendance.example.com
Content-Type: application/json
Content-Digest: sha-256=:X48E9qOokqqrvdts8nOJRJN3OWDUoyWxBf7kbu9DBPE=:
Idempotency-Key: 6f1a0e0a-6a0e-4a1e-9a6c-0b6d1f2a3c4d
Signature-Input: sig1=("@method" "@authority" "@path" "content-digest")
  ;created=1789459200;keyid="8f2c...";alg="ecdsa-p256-sha256"
  ;nonce="Y2xvY2tpblh0cmE";tag="clockinxtra-mobile-v1"
Signature: sig1=:MEUCIQ...64-byte raw r||s, base64...:
```

### 6.2 Server verification order

1. Parse `Signature-Input` / `Signature`. Malformed → `401 UNAUTHORIZED`.
2. Reject unless `alg` is `ecdsa-p256-sha256` and `tag` matches. Never take the algorithm choice from the client beyond this allow-list.
3. Reject unless the covered-component set exactly matches the profile (RFC 9421 §7.2.1 warns about insufficient coverage).
4. `created` must be within `AllowedClockSkewSeconds` (default 120) of server time, otherwise `401 CLOCK_SKEW`.
5. Insert `(DeviceId, Nonce)` into `mobile.RequestNonce` with a unique constraint. A duplicate key means replay → `401 REPLAYED_REQUEST`. Rows older than twice the skew window are purged by a scheduled job.
6. Recompute `Content-Digest` over the received body. Mismatch → `400 INVALID_REQUEST`.
7. Load the device by `keyid`. Unknown → `401 UNAUTHORIZED`; `PendingApproval` → `403 DEVICE_NOT_APPROVED`; `Revoked` → `403 DEVICE_REVOKED`.
8. Verify the signature with `ECDsa.VerifyData(signatureBase, signature, HashAlgorithmName.SHA256)`. .NET's default signature format is IEEE P1363 (`r‖s`), which matches the RFC, so no conversion is needed server-side.
9. Attach the authenticated `DeviceId` and `MobileUserId` to the request context. **A user ID in the body is only accepted if it matches the device binding**, otherwise `403 FORBIDDEN`.

`@authority` and `@path` must reflect the values the client saw. The API therefore enables `ForwardedHeaders` restricted to the configured reverse-proxy addresses (`KnownProxies`/`KnownNetworks`), never wildcard.

### 6.3 Registration requests

The registration request is signed with the **new** private key, whose public key is in the body (`keyid="unregistered"`). This proves possession of the private key. Binding to the device and app comes from the attestation, which embeds the server-issued challenge:

| Platform | Attestation | Server-side verification |
|---|---|---|
| Android | Key attestation certificate chain from `setAttestationChallenge` (API 24+) | Chain validates to a configured Google root; challenge matches the issued one; `attestationSecurityLevel` is `TrustedEnvironment` or `StrongBox`; `attestationApplicationId` matches the configured package name and signing-certificate digest; `RootOfTrust.deviceLocked = true` and `verifiedBootState = Verified`; revocation list consulted per OPEN-33 |
| iOS | App Attest attestation object (CBOR) — **only if OPEN-33 is approved** | Verified against Apple's App Attest root, App ID matches, challenge matches, key ID binds the public key. Without approval, the server records `AttestationLevel = None` and the residual risk RR-03 applies |

`AttestationLevel` (`None` / `Software` / `Hardware`) is stored on the device record so reports and policy can distinguish trust levels.

### 6.4 Idempotency

- `Idempotency-Key` (UUID) is required on clock-in and clock-out.
- `mobile.RequestIdempotency` holds `(DeviceId, IdempotencyKey)` unique, plus endpoint, request hash, outcome code and response payload.
- Same key + same request hash → the stored outcome is returned without re-executing.
- Same key + different request hash → `409 IDEMPOTENCY_KEY_REUSED`.
- The app does **not** retain the password for retries. After a timeout it calls `user/status` to reconcile (§52: no silent offline queueing).

---

## 7. Endpoint catalogue (v1)

| Endpoint | Auth | Extra credentials | Rate-limit partition | Notes |
|---|---|---|---|---|
| `POST /api/v1/mobile/device/registration/challenge` | none | — | client IP (bounded) | Returns a single-use, short-lived challenge. Response is identical whether or not the user exists |
| `POST /api/v1/mobile/device/register` | self-signed + attestation | password + TOTP | user ID + client IP | Result `PendingApproval` (OPEN-32). SQL-backed lockout |
| `POST /api/v1/mobile/device/status` | signature | — | device | Lets the app show "awaiting approval" |
| `POST /api/v1/mobile/location/validate` | signature | — | device | Startup gate (ASM-05) |
| `POST /api/v1/mobile/user/status` | signature | — | device | Server is the source of truth (§11) |
| `POST /api/v1/mobile/attendance/clock-in` | signature | password + TOTP | device + user | Idempotent, transactional |
| `POST /api/v1/mobile/attendance/clock-out` | signature | — | device + user | Idempotent, transactional |
| `GET /api/v1/mobile/app/config` | signature | — | device | Minimum app version, policy flags. No secrets |
| `GET /health/live`, `GET /health/ready` | none | — | — | Bound to internal addresses; **not** published through the proxy |

All responses carry `X-Correlation-Id`. Error bodies follow §34:

```json
{ "success": false, "code": "LOCATION_NOT_ALLOWED", "message": "…", "correlationId": "…" }
```

**Error codes:** `INVALID_REQUEST`, `UNAUTHORIZED`, `FORBIDDEN`, `INVALID_CREDENTIALS`, `DEVICE_NOT_REGISTERED`, `DEVICE_NOT_APPROVED`, `DEVICE_REVOKED`, `CLOCK_SKEW`, `REPLAYED_REQUEST`, `IDEMPOTENCY_KEY_REUSED`, `LOCATION_NOT_ALLOWED`, `LOCATION_ACCURACY_INSUFFICIENT`, `LOCATION_SOURCE_UNTRUSTED`, `ALREADY_CLOCKED_IN`, `NOT_CLOCKED_IN`, `ATTENDANCE_WINDOW_CLOSED`, `ATTENDANCE_NOT_CONFIGURED`, `ATTENDANCE_OPERATION_FAILED`, `APP_VERSION_UNSUPPORTED`, `RATE_LIMITED`, `INTERNAL_ERROR`.

Per CON-09/OPEN-38, `INVALID_OTP` is **not** returned to the mobile client; OTP failures return `INVALID_CREDENTIALS` and the precise reason is written to security events only.

---

## 8. Location validation design

1. **Boundary validation:** latitude ∈ [−90, 90], longitude ∈ [−180, 180], accuracy > 0 and finite, coordinates not null.
2. **Source trust:** reject when Android reports `isMocked` or iOS reports `isSimulatedBySoftware` → `LOCATION_SOURCE_UNTRUSTED` plus a security event (OPEN-26).
3. **Candidate offices:** active office locations, cached in memory for 60 seconds; the cache is invalidated immediately when an administrator changes a location.
4. **Distance:** Haversine with mean Earth radius 6,371,008.8 m, computed in `Attendance.Domain` (`GeoDistance`). Never Euclidean degrees.
5. **Decision (interim, OPEN-25):** valid when `distance ≤ AllowedRadiusMeters` **and** `accuracy ≤ AllowedRadiusMeters`. The radius is never widened for poor accuracy.
6. **Response minimisation (§9):** on success the office identifier and name; on failure only the reason code. Distances and candidate lists are never returned.
7. **Evidence stored:** office ID, distance (metres, 2 dp), reported accuracy, platform, mock/simulated flag. Raw coordinates only if OPEN-35 requires them, and then encrypted.

Boundary tests (§51) cover 0, 1, 3, 4.9, 5.0, 5.1 and 10 m, using a reference implementation and `geography::STDistance` for cross-checking, with a documented tolerance. These test the arithmetic. They do not imply that GPS can resolve 5 m in the field (CON-01).

---

## 9. Attendance transaction design

**Clock-in** (`mobile.usp_Attendance_ClockIn`), a single transaction, `READ COMMITTED` plus explicit key-range locking:

```text
BEGIN TRANSACTION
  @NowUtc = SYSUTCDATETIME()                          -- server is authoritative (§31)
  read timezone + window settings; if any NULL → ATTENDANCE_NOT_CONFIGURED
  @LocalNow = @NowUtc AT TIME ZONE 'UTC' AT TIME ZONE @BusinessTimeZone
  @AttendanceDate = CAST(@LocalNow AS date)
  validate user active, device active and bound to user (WITH UPDLOCK)
  validate office location still active
  evaluate attendance window rules (close time, action)
  SELECT existing attendance for (@MobileUserId, @AttendanceDate) WITH (UPDLOCK, HOLDLOCK)
     → found: raise ALREADY_CLOCKED_IN
  INSERT attendance (unique index on (MobileUserId, AttendanceDate))
  INSERT attendance event + audit event
COMMIT
```

- Concurrency is handled by the unique index plus `UPDLOCK, HOLDLOCK` on the key range, so two simultaneous requests on different IIS nodes cannot both insert (§45).
- A unique-key violation is caught and mapped to `ALREADY_CLOCKED_IN`, never surfaced as a SQL error (§34).
- Credential and TOTP verification happen in the application layer **before** the transaction opens, so no expensive work is done while holding locks. The attendance-state check itself is inside the transaction.
- **Clock-out** mirrors this: it locks the open record for the attendance day, verifies it is open, sets the server timestamp, computes duration, and writes the event. There is no partial record at any point (§30).

### 9.1 How repositories compose

Repositories obtain a **connection lease** rather than a bare connection. The lease carries two things the caller should not have to reason about:

- **Ownership.** A repository called on its own gets a connection it disposes. The same repository called inside a unit of work gets someone else's connection and must leave it open — disposing it would close the caller's transaction underneath them.
- **The ambient transaction.** ADO.NET refuses to execute a command against a connection with a pending local transaction unless that transaction is passed on the command, so a repository that ignored it would fail the moment it was composed with anything else.

This is the application-side half of decision DB-13. The procedures are savepoint-aware so that a business rejection inside a composition rolls back only its own work; the lease is what makes such a composition expressible in the first place. The integration tests exercise exactly this path — each test wraps its work in a transaction it always rolls back — so a procedure that regressed to a bare `ROLLBACK` would fail the test suite immediately rather than in production.

---

## 10. Admin application architecture

- **Authentication:** ASP.NET Core cookie authentication (no Identity framework, since its default stores are EF Core — prohibited by §4). Password verification uses the same hashing service (TD-05). Admin TOTP per OPEN-34.
- **Cookie:** `HttpOnly`, `Secure`, `SameSite=Strict`, sliding idle timeout plus absolute lifetime, cookie protected by the shared Data Protection key ring.
- **Session invalidation:** a security stamp on the administrator row is re-validated on each request, so password resets and permission changes end existing sessions.
- **Authorization:** permission-based policies registered centrally. Controllers declare `[Authorize(Policy = Permissions.OfficeLocation_Manage)]`. No ad-hoc role string checks scattered in controllers (§42).
- **Lockout:** SQL-backed failed-attempt counters with a lockout window.
- **Antiforgery:** global auto-validation on unsafe methods.
- **Headers:** HSTS, CSP without `unsafe-inline`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, restrictive `Permissions-Policy`.

**Permissions** (seed set; roles map to these, §42):

`MobileUser.View/Manage`, `Administrator.View/Manage`, `Role.Manage`, `Device.View/Approve/Revoke`, `OfficeLocation.View/Manage`, `Setting.View/Manage`, `Mfa.Enroll/Reset`, `Attendance.View`, `Attendance.Correct`, `Attendance.ApproveCorrection`, `Report.View`, `Audit.View`.

Seeded roles: Super Administrator, Attendance Administrator, User Administrator, Location Administrator, Report Viewer, Auditor. Correction duties are split by role (DEC-10): **Attendance Administrator** holds `Attendance.Correct` but not `Attendance.ApproveCorrection`, so it can request and never approve. **Super Administrator** holds every permission — including `Attendance.Correct`, because a role may only be granted by someone holding all of its permissions, and without it the Attendance Administrator role would be unassignable. Requester ≠ approver is enforced in the stored procedure and by a table constraint, so no grant of permissions can defeat it.

---

## 11. Data model (summary; full design in Phase 4)

Every table has a justification, per §27.

| Table | Why it exists |
|---|---|
| `MobileUser` | Employee profile and status (§17) |
| `EmployeeCredential` | Password hash + parameters, separated from the profile so an AD provider can replace it without touching user data (DEC-01) |
| `MfaCredential` | Encrypted TOTP secret, status, last accepted time step (replay protection) |
| `Device` | Device binding, public key, platform, attestation level, status, revocation (§18) |
| `DeviceRegistrationChallenge` | Single-use attestation challenges with expiry |
| `RequestNonce` | Replay prevention for signed requests (§37) |
| `RequestIdempotency` | Safe retries for clock-in/out (§53) |
| `AuthenticationAttempt` | Lockout counters that work across IIS nodes (TD-10) |
| `OfficeLocation` | Coordinates, per-location radius, status (§9, §19) |
| `Attendance` | One record per employee per attendance day (ASM-02) |
| `AttendanceEvent` | Immutable record of each clock-in/out with location evidence |
| `AttendanceCorrection` | Administrative corrections with maker–checker (DEC-08, DEC-10) |
| `ApplicationSetting` | Configurable business rules, with a flag marking values still awaiting business confirmation |
| `Administrator`, `Role`, `Permission`, `RolePermission`, `AdministratorRole` | Portal authentication and least-privilege authorization |
| `AuditLog` *(append-only ledger)* | Administrative and security-relevant actions (§32) |
| `SecurityEvent` *(append-only ledger)* | Failed authentications, replay attempts, untrusted location sources |

Conventions: `datetime2(3)` UTC for system timestamps, `rowversion` where optimistic concurrency is needed, check constraints for coordinate ranges and enum values, foreign keys throughout, and no table access granted to application logins (TD-15).

---

## 12. Cryptography and key inventory

| Key / secret | Algorithm | Where it lives | Rotation | Backup |
|---|---|---|---|---|
| Device signing key | ECDSA P-256 | Device TEE/StrongBox or Secure Enclave, non-exportable | New key on re-registration | **Never** backed up (by design) |
| TOTP secret | 160-bit random, RFC 6238 | SQL Server, encrypted with Data Protection | On re-enrolment | With the database + key ring |
| Employee/admin password | PBKDF2-HMAC-SHA512, 220k iterations, 32-byte salt | SQL Server | Re-hash on login when parameters change | With the database |
| Data Protection key ring | AES + HMAC (framework default) | ACL'd SMB share, protected by PKI certificate | Framework default 90-day key lifetime; old keys retained for decryption | **Critical**: key ring + certificate private key (TH-42) |
| TLS certificates | Per organisation PKI / public CA | Certificate stores and proxy | Per organisation policy | Per organisation policy |
| Ledger digests | SHA-256 (SQL Server) | Exported to WORM storage | Per digest schedule | Yes |

**Key-ring operating rules** (from the ASP.NET Core key-management documentation, and mandatory because TOTP secrets are long-lived):

- **Keys are never deleted.** Microsoft states that once a key is deleted, "all data protected by the key is permanently undecipherable, and there's no emergency override like there's with revoked keys." Deleting a key would therefore destroy every enrolled TOTP secret.
- **Revocation is not deletion.** "Revoked keys by default may not be used to unprotect payloads, but the application developer can override this behavior if necessary" — `IPersistedDataProtector.DangerousUnprotect` allows recovery, reporting `wasRevoked`, after which the payload's authenticity must be independently corroborated. This is the documented break-glass path for a compromised key, and it is a controlled operational procedure, not an application code path.
- The default key lifetime is 90 days with automatic rolling, and a new key is activated after a 2-day propagation delay. Both API and Admin must share the key ring and application name, or the Admin portal could enrol a secret the API cannot read.
- Key ring **and** certificate private key are covered by backup and restore drills (TH-42).

No key material is in source control or configuration files. Connection strings use Windows authentication via gMSA where AD is available, which removes the SQL password from configuration entirely; SQL authentication with a secret store is the fallback.

---

## 13. Configuration model

| Layer | Contents | Store |
|---|---|---|
| Infrastructure configuration | Connection string, key ring path, certificate thumbprint, proxy addresses, rate limits, skew window, log settings | `appsettings.json` + environment overrides + secret store. Bound with the Options pattern and validated at startup (`ValidateOnStart`) |
| Business settings | Timezone, clock-in close time and action, clock-out open time and action, attendance-day rule, correction switches, retention periods | `ApplicationSetting` table, edited in the portal, all changes audited |
| Security policy settings | Accuracy rule, mock-location rejection, attestation requirements, minimum app version, lockout thresholds | `ApplicationSetting`, change-audited |

Business settings that are unset make `/health/ready` report `Degraded` (still HTTP 200, so the load balancer keeps every node) and attendance endpoints return `ATTENDANCE_NOT_CONFIGURED`.

---

## 14. Observability

- **Serilog** structured logging with a correlation ID enriched onto every event, written to rolling files and the Windows Event Log (on-premises only; §61).
- Request logging records method, path, status, duration, correlation ID, device ID and user ID. It never records bodies, passwords, OTPs, signatures or coordinates (§33).
- **Health checks:** `/health/live` (process) and `/health/ready`, which returns a JSON report per check (never exception details). API: `database` and `key-ring` (Unhealthy when failing: the node cannot serve), `attendance-configuration` (Degraded when clock-in or clock-out settings are unset or no office is active). Portal: `database` and `key-ring`. In addition, a non-development host refuses to start without a shared, certificate-protected key ring (`KeyRingConfiguration`).
- **Security events** go to the database, not only to logs, so the portal can report on them.

---

## 15. Deployment topology

| Tier | Nodes | Notes |
|---|---|---|
| DMZ | ≥2 reverse-proxy nodes | TLS termination with re-encryption to the app tier; connection, rate and body-size limits; only `/api/v1/mobile/*` published |
| Application | ≥2 IIS nodes (API); ≥1 IIS node (Admin, internal) | Separate application pools and service accounts. Shared Data Protection key ring with the same application name |
| Data | SQL Server 2022 | HA depends on OPEN-17: **Basic availability groups** are available in Standard edition (two replicas, one database, no readable secondary); **Always On availability groups** require Enterprise |
| Build | Windows agent + **macOS agent** | iOS builds require macOS with Xcode (CON-06, OPEN-42) |

Single-server deployment is explicitly not proposed for production (§46). Ports: 443 externally, 443 between proxy and app tier, 1433 (or a fixed instance port) between app and data tiers, SMB to the key ring share.

---

## 16. Failure modes

| Failure | Behaviour |
|---|---|
| Database unavailable | Attendance endpoints return `INTERNAL_ERROR`; readiness fails so the load balancer drains the node; app shows a clear failure and never changes local attendance state (§52) |
| Key ring unreadable | Startup fails fast rather than running with unprotected secrets |
| Mandatory settings unset | `ATTENDANCE_NOT_CONFIGURED`, readiness `Degraded` |
| Attestation service unreachable | Registration only is affected, with a distinct retryable code (TH-44) |
| Device clock wrong | `CLOCK_SKEW` with a message telling the employee to enable automatic time |
| Poor GPS accuracy | `LOCATION_ACCURACY_INSUFFICIENT`, with guidance to move outdoors or near a window |

---

## 17. Traceability

| `Claude.md` | Addressed by |
|---|---|
| §4 database rules, §29 procedures | §3 layout, §9, §11 here; Phase 5–6 deliverables |
| §5 Clean Architecture | §3 project references + architecture test |
| §9 location validation | §8 |
| §12–14 clock-in/out | §7, §9 |
| §13 MFA | §5, §12 |
| §22–23 API security, encryption | §6, DEC-02 |
| §28, §30, §45 integrity, transactions, concurrency | §9 |
| §31 time handling | §9 (SQL-side `SYSUTCDATETIME` + `AT TIME ZONE`) |
| §34 error model, §60 exception handling | §7 |
| §36 rate limiting | §7 (per-endpoint partitions) + TD-10 |
| §37 replay protection | §6.2, §6.4 |
| §40 authentication design | §5 |
| §41–43 admin auth, authorization, headers | §10 |
| §46 deployment | §15 |
| §47 configuration | §13 |
| §61 observability | §14 |

---

## 18. Open points carried into later phases

- OPEN-25 (accuracy rule), OPEN-26 (spoofing policy), OPEN-32 (first-registration approval), OPEN-33 (platform attestation), OPEN-38 (error granularity) all have interim behaviour implemented behind settings, and all are flagged in the portal as awaiting business confirmation.
- CON-11 (ledger vs. retention) is resolved in Phase 4 once OPEN-14 is answered.
- Phase 15 finalises the iOS jailbreak-heuristics choice (in-house implementation versus a licensed library, CON-08).

## 19. Sources

RFC 9421 §3.3.4 and §7.2 (local copy of the RFC text); RFC 9530; Microsoft Learn for ASP.NET Core OpenAPI, rate limiting, Data Protection, SQL Server ledger, editions and basic availability groups; Android Keystore and key attestation documentation; Apple CryptoKit, DeviceCheck and Core Location documentation. Full list in `01-requirements-register.md` §8.
