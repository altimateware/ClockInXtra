# 02 — Threat Model

| Item | Value |
|---|---|
| Document | ClockInXtra — Threat Model |
| Phase | 2 of 24 |
| Version | 0.1 (draft) |
| Date | 2026-09-11 |
| Inputs | `01-requirements-register.md` (DEC-01…04, TD-01…15, ASM, CON, OPEN) |
| Review triggers | Any change to DEC-02/DEC-03, a resolved OPEN-25/26/32/33/38, new endpoint, new data element, new third-party component, security incident |

---

## 1. Scope and method

**In scope:** Flutter mobile app (Android, iOS), device secure hardware usage, DMZ reverse proxy, Attendance API, Admin portal, SQL Server database, Data Protection key ring, audit and log stores, build/dependency supply chain.

**Out of scope (owned elsewhere, but assumed present):** perimeter firewall/IDS, Windows Server and AD hardening baselines, physical security, MDM platform, organisation PKI operations.

**Method:**
1. Security objectives and assets.
2. Threat actors with realistic capability.
3. Trust boundaries and data flows.
4. STRIDE analysis per data flow and boundary.
5. Controls mapped to each threat, with qualitative inherent and residual rating (**L**ow / **M**edium / **H**igh, combining likelihood and impact).
6. Residual risks that need explicit business acceptance.

---

## 2. Security objectives (in priority order)

1. **Attendance integrity.** Each record should show that the bound employee's registered device made the request, reported a position near an approved office, and that the server set the time. The residual risks in §9 describe where this cannot be *proven*.
2. **Credential and secret protection.** Passwords, TOTP secrets, device private keys and the Data Protection key ring.
3. **Accountability.** Every security-relevant and administrative action is attributable and tamper-evident.
4. **Privacy.** Location and attendance data are minimised, access-restricted and retained only per policy.
5. **Availability at peak.** Morning clock-in bursts must not be starved by abuse traffic.

---

## 3. Assets

| ID | Asset | Location | Sensitivity |
|---|---|---|---|
| A-01 | Attendance records (clock-in/out, derived durations) | SQL Server | High (integrity) |
| A-02 | Employee credential hashes | SQL Server | High |
| A-03 | TOTP shared secrets (encrypted) | SQL Server + key ring | Critical |
| A-04 | Device private keys (non-exportable) | Android Keystore TEE/StrongBox, iOS Secure Enclave | Critical |
| A-05 | Device public keys and bindings | SQL Server | High (integrity) |
| A-06 | Data Protection key ring + protecting certificate | ACL'd file share + certificate store | Critical |
| A-07 | Office location configuration (coordinates, radius) | SQL Server | High (integrity) |
| A-08 | Location evidence (distance, accuracy, mock flag; raw coordinates only if OPEN-35) | SQL Server | High (privacy) |
| A-09 | Administrator accounts, roles, permissions | SQL Server | Critical |
| A-10 | Audit log and security events | SQL Server (append-only ledger tables) + exported digests | High (integrity) |
| A-11 | Application logs | Log store (on-prem) | Medium |
| A-12 | Application settings (attendance windows, timezone, policies) | SQL Server | High (integrity) |
| A-13 | Employee user ID stored on device | Platform secure storage | Medium |
| A-14 | Signing identities for mobile releases (keystore, Apple certificates) | Build infrastructure | Critical |

---

## 4. Threat actors

| ID | Actor | Motivation | Capability | Likelihood of attempt |
|---|---|---|---|---|
| TA-1 | **Dishonest employee** | Record attendance without being present | Owns device and credentials; can install mock-location apps; may root/jailbreak; low–medium skill; high motivation and unlimited attempts | **High** |
| TA-2 | **Colluding colleague** ("buddy punching") | Clock in for an absent colleague | Physical access to colleague's phone and shared credentials | High |
| TA-3 | **External internet attacker** | Account takeover, data theft, disruption | Credential stuffing lists, automated scanning, botnets | High (internet-facing, DEC-03) |
| TA-4 | **Device thief/finder** | Opportunistic misuse | Physical possession of a device, possibly unlocked | Medium |
| TA-5 | **Network attacker** | Intercept or alter traffic | Hostile Wi-Fi, TLS interception if a user installs a rogue CA | Low–Medium |
| TA-6 | **Malicious or compromised administrator** | Falsify attendance, bind rogue devices, snoop on location data | Legitimate portal access within assigned permissions | Medium |
| TA-7 | **Privileged infrastructure insider** (DBA, server admin) | Tamper with records, extract secrets | Direct database, backup and host access | Low–Medium |
| TA-8 | **Supply-chain attacker** | Code execution in app or server | Malicious or compromised NuGet/pub package or build tool | Low |

---

## 5. Architecture and trust boundaries

```text
 UNTRUSTED                             │ DMZ                  │ INTERNAL APP TIER           │ DATA TIER
                                       │                      │                             │
 ┌──────────────────────────────┐      │                      │                             │
 │ Employee device              │      │                      │                             │
 │ ┌──────────────┐ ┌─────────┐ │ TB-2 │ ┌──────────────────┐ │ TB-3 ┌──────────────────┐   │ TB-4 ┌──────────────┐
 │ │ Flutter app  │ │TEE / SE │ │─TLS─▶│ │ Reverse proxy    │─┼─TLS─▶│ Attendance API   │───┼─TLS─▶│ SQL Server   │
 │ │ (sandbox)    │◀┤ keys    │ │      │ │ (TLS terminate + │ │      │ (IIS, ≥2 nodes)  │   │      │ mobile.* SPs │
 │ └──────────────┘ └─────────┘ │      │ │  re-encrypt)     │ │      └──────────────────┘   │      │ admin.*  SPs │
 │   TB-6 (app ↔ secure hw)     │      │ └──────────────────┘ │      ┌──────────────────┐   │      │ ledger tables│
 └──────────────────────────────┘      │                      │ TB-5 │ Admin portal     │───┼─TLS─▶└──────────────┘
          │ TB-1 (device ↔ internet)   │                      │◀─────│ (internal only)  │   │      ┌──────────────┐
          │                            │                      │ admin└──────────────────┘   │ SMB  │ DP key ring  │
          ▼ TB-7 (platform vendors, OPEN-33)                  │ workstations  API + Admin ──┼─────▶│ share (ACL)  │
   Apple App Attest service (device-initiated)                │                             │      └──────────────┘
   Google attestation revocation list (server-initiated fetch)│                             │
```

| Boundary | Crossing | Primary concerns |
|---|---|---|
| TB-1 | Device ↔ internet | Hostile networks, interception |
| TB-2 | Internet ↔ DMZ proxy | Abuse volume, scanning, TLS configuration |
| TB-3 | DMZ ↔ application tier | Proxy compromise, header spoofing (`X-Forwarded-For`), TLS bridging |
| TB-4 | Application ↔ data tier | SQL injection, over-privileged logins, secret exposure |
| TB-5 | Internal admin workstations ↔ Admin portal | CSRF, session theft, privilege abuse |
| TB-6 | App code ↔ device secure hardware | Key extraction (not possible by design), key misuse on compromised OS |
| TB-7 | Our systems ↔ platform vendor services | Availability and trust dependency on Apple/Google (CON-02) |

---

## 6. Data flows

| ID | Flow | Authentication presented | Notes |
|---|---|---|---|
| DF-1 | **Device registration**: app obtains a server challenge → generates a hardware-backed P-256 key bound to that challenge → submits user ID, password, TOTP, public key, attestation, platform metadata | Password + TOTP + attestation (no device key yet) | The **only** mobile endpoint that accepts a password from an unregistered device. Highest brute-force exposure. Result: `PendingApproval` (OPEN-32) |
| DF-2 | **Startup location validation** (§8) | Device signature | UX gate only (ASM-05) |
| DF-3 | **User status** (§11) | Device signature | Server is source of truth |
| DF-4 | **Clock-in** (§12) | Device signature + password + TOTP + location | Transactional, idempotent |
| DF-5 | **Clock-out** (§14) | Device signature + location | No password/TOTP by requirement (ASM-04) |
| DF-6 | **Administration** | Cookie session after password + admin TOTP (OPEN-34) | Internal network only (ASM-01) |
| DF-7 | **Audit/security events** | Written only by stored procedures | Append-only ledger tables |
| DF-8 | **Attestation trust material**: Google root certificates (static configuration), Android revocation list fetch, Apple App Attest root | Server-side configuration | OPEN-33 |

---

## 7. Threat register

Legend: **STRIDE** = **S**poofing, **T**ampering, **R**epudiation, **I**nformation disclosure, **D**enial of service, **E**levation of privilege. **Inh** = inherent rating, **Res** = residual rating after listed controls.

### 7.1 Location and physical presence

| ID | STRIDE | Actor | Threat scenario | Controls | Inh | Res |
|---|---|---|---|---|---|---|
| TH-01 | S, T | TA-1 | Android **mock-location provider** reports office coordinates from home | Reject locations flagged `isMocked` (OPEN-26). Record flag as security event. Key attestation at registration exposes `deviceLocked` / `verifiedBootState` (an unlocked bootloader is a common prerequisite for rooting). Client root heuristics. Server-side location check on every attendance transaction | H | M |
| TH-02 | S, T | TA-1 | iOS **simulated location** (developer tooling, jailbreak tweaks) | Reject `CLLocationSourceInformation.isSimulatedBySoftware` (iOS 15+). Jailbreak heuristics. Same server-side checks | H | M |
| TH-03 | S | TA-1 | **Hooked location APIs on a rooted/jailbroken device** that also hides root | Defence in depth only: attestation boot state (Android), heuristics, anomaly reporting (e.g., identical coordinates across days, implausible accuracy values) | H | **M–H** (accepted residual, §9) |
| TH-04 | S | TA-1 | **External radio-level GNSS spoofing** | No reliable software control. Audit anomalies | M | M |
| TH-05 | S | TA-2 | **Buddy punching**: colleague uses victim's unlocked phone, shared password and TOTP | Three factors are required for clock-in. One active device per employee (DEC-04). Keys usable only while the device is unlocked. Optional local biometric gate (§3.1, future). Policy and HR sanctions | H | **M** (cannot prevent willing credential sharing) |
| TH-06 | S | TA-1 | **Genuine accuracy too poor** at 5 m, so honest employees are rejected (availability, not attack) | Strict interim rule never widens radius (CON-01). Per-office radius configuration. Pilot measurement. Clear user message (`LOCATION_ACCURACY_INSUFFICIENT`) | H | M (operational risk) |

### 7.2 Device identity and registration

| ID | STRIDE | Actor | Threat scenario | Controls | Inh | Res |
|---|---|---|---|---|---|---|
| TH-07 | S | TA-1, TA-3 | **Emulator or cloned app instance** impersonates a registered device by copying app data | Private key is non-exportable (TEE/StrongBox/Secure Enclave). Copied app data contains no usable key. Android attestation must show `attestationSecurityLevel` of `TrustedEnvironment` or `StrongBox`. Emulator heuristics | H | L (Android) / **M (iOS without App Attest, OPEN-33)** |
| TH-08 | S | TA-3 | **Attacker registers own device** to a victim account using phished password + TOTP | Registration requires password + fresh TOTP. SQL-backed per-account and per-source lockout. `PendingApproval` requires admin approval (OPEN-32). Approving a new device revokes the old one, so the victim's app stops working immediately (a visible signal). Audited | H | L–M |
| TH-09 | E | TA-6 | **Administrator approves a rogue device** or binds their own device to another employee | Separate permission `Device.Approve`. All approvals in append-only audit. Report of approvals per administrator. Optional dual control if required | M | M |
| TH-10 | S | TA-1 | **Replaying a registration** (re-using a challenge or attestation) | Challenge is single-use, short-lived, stored server-side, consumed atomically. Attestation must embed that exact challenge | M | L |
| TH-11 | T | TA-1 | **Repackaged/modified app** (instrumented to bypass client checks) | Android attestation `attestationApplicationId` (package name + signing-certificate digest) verified at registration. iOS App Attest App ID if OPEN-33 approved. Minimum app version enforced by server | H | M (L on Android) |
| TH-12 | I | TA-3 | **Device ID enumeration** | Server-issued random identifiers. Requests with an unknown device ID or bad signature get a uniform `UNAUTHORIZED`. Only validly signed requests from a known-but-revoked key get `DEVICE_REVOKED` | M | L |

### 7.3 Authentication and credentials

| ID | STRIDE | Actor | Threat scenario | Controls | Inh | Res |
|---|---|---|---|---|---|---|
| TH-13 | S | TA-3 | **Credential stuffing/password spraying** on clock-in | Clock-in is only processed after a valid signature from an **active device bound to that user**, so attackers without an approved device never reach password verification. Signature verification runs before PBKDF2 (cheap before expensive) | H | L |
| TH-14 | S | TA-3 | **Credential stuffing on registration** (DF-1, the only exposed password surface) | SQL-backed failure counters and lockout per account (TD-10). Per-source throttling at proxy and application. TOTP required in the same request. Challenge issuance throttled. Uniform error responses | H | L–M |
| TH-15 | S | TA-3 | **TOTP brute force** | Per-credential failure counter and lockout in SQL. With ±1 step tolerance 3 of 10⁶ codes are valid, so ≈ 3×10⁻⁶ success per guess before lockout | M | L |
| TH-16 | S | TA-5, TA-2 | **TOTP code replay** within its validity window | Last accepted time step persisted per credential. A step ≤ the last accepted is rejected. Update is atomic with the attendance transaction | M | L |
| TH-17 | I | TA-3 | **Password confirmation oracle** via a distinct `INVALID_OTP` response (CON-09) | Mobile API returns `INVALID_CREDENTIALS` for user/password/OTP failures. Exact reason only in security events (OPEN-38) | M | L (if adopted) |
| TH-18 | I | TA-7 | **TOTP secrets extracted** from database or backups | Data Protection encryption (TD-06). Key ring on a separate ACL'd share protected by certificate. DBA and backup operators lack key ring and certificate private key | H | L |
| TH-19 | I | TA-7 | **Offline cracking of password hashes** from a stolen backup | PBKDF2-HMAC-SHA512 220k iterations with unique salts (TD-05). Encrypted backups. Password policy (OPEN-40) | M | L–M |
| TH-20 | S | TA-3, TA-6 | **Administrator account takeover** | Admin portal internal-only (ASM-01). Admin TOTP (OPEN-34). SQL-backed lockout. Secure cookies. Short idle timeout. Security-stamp revalidation on password/role change. Audit | H | L |
| TH-21 | S | TA-4 | **Lost or stolen device** | Clock-in still needs password + TOTP. Device keys require an unlocked device (Android `setUnlockedDeviceRequired`, API 28+; iOS access control `WhenUnlockedThisDeviceOnly`). Immediate admin revocation. Residual: an unlocked stolen phone can clock the employee **out** (ASM-04) and, if the authenticator is on the same phone, factors collapse (CON-10) | M | M |

### 7.4 Request integrity, replay and concurrency

| ID | STRIDE | Actor | Threat scenario | Controls | Inh | Res |
|---|---|---|---|---|---|---|
| TH-22 | T | TA-5 | **Request modification in transit** or after TLS termination | TLS on every hop. Request signature covers method, target, authority and a content digest of the body (profile in `03`). Android apps targeting API 24+ do not trust user-installed CAs by default. On iOS a user-installed CA must be explicitly granted full trust by the user | M | L |
| TH-23 | T, R | TA-1, TA-5 | **Replay of a captured signed request** (for example yesterday's clock-in, or a status call) | Signature `created` must fall within a small skew window of server time. Nonce stored with a unique constraint for the window's duration. Server-assigned timestamps. One attendance record per day (ASM-02) | H | L |
| TH-24 | T | — | **Client retries** after a timeout create duplicate records | Idempotency key per logical operation, unique per device. A replay returns the original outcome without re-executing. Unique index on (employee, attendance date) | M | L |
| TH-25 | T | TA-1 | **Concurrent clock-ins** racing across IIS nodes | Enforced in SQL: transaction with key-range locking hints (`UPDLOCK, HOLDLOCK`) on the attendance key plus a unique index. Application-level locks are not relied upon (§45) | M | L |
| TH-26 | T | TA-1 | **Device clock manipulation** | Server time is authoritative for attendance. Device time only affects whether a signature falls inside the skew window, and outside it the request is rejected with a clear code | M | L |

### 7.5 API and application

| ID | STRIDE | Actor | Threat scenario | Controls | Inh | Res |
|---|---|---|---|---|---|---|
| TH-27 | T, I | TA-3 | **SQL injection** | Stored procedures only. Typed Dapper parameters. No dynamic SQL. Application logins have `EXECUTE` only, no table rights (TD-15) | H | L |
| TH-28 | E | TA-3 | **Reaching administrative capability** through the internet-facing API | No admin endpoints in the API. The API's SQL login can execute only `mobile.*` procedures. Separate application pools and identities | H | L |
| TH-29 | D | TA-3 | **Application-layer DoS**: floods, oversized bodies, slow requests, forcing expensive password hashing | Proxy connection and request-rate limits. Kestrel/IIS body size limits and timeouts. Per-endpoint limiter policies with **bounded partition keys** (Microsoft warns that partitioning on unbounded user input can exhaust memory). Signature verification before password hashing. Concurrency cap on hashing | H | **M** (volumetric attacks need perimeter/ISP controls; no cloud scrubbing by design) |
| TH-30 | I | TA-3 | **Error details disclosed** (stack traces, SQL errors) | Centralised exception handling. Fixed error-code catalogue. Correlation ID returned instead of details | M | L |
| TH-31 | I | TA-7, TA-3 | **Secrets or location in logs** | No request/response body logging. Redaction policy for known-sensitive properties. Coordinates never logged. Automated tests assert redaction | M | L |
| TH-32 | S | TA-3 | **Client IP spoofing** via `X-Forwarded-For` to evade throttling | Forwarded headers accepted only from configured proxy addresses (`KnownProxies`/`KnownNetworks`) | M | L |
| TH-33 | T, I | TA-8 | **Malicious or vulnerable dependency** | Central package management with pinned versions. Lock files (`packages.lock.json`, `pubspec.lock`). Minimal dependency set with telemetry-free packages only (TD-11). `dotnet list package --vulnerable` and dependency review in CI. Internal package mirror recommended | M | M |

### 7.6 Administrative portal

| ID | STRIDE | Actor | Threat scenario | Controls | Inh | Res |
|---|---|---|---|---|---|---|
| TH-34 | T | TA-3 via admin browser | **CSRF** | Antiforgery tokens validated globally on unsafe methods. `SameSite=Strict` authentication cookie | M | L |
| TH-35 | S | TA-3 | **Session hijacking** | `HttpOnly`, `Secure`, `SameSite=Strict`. Idle and absolute timeouts. Session invalidated on logout, password reset and permission change | M | L |
| TH-36 | T, I | TA-1 | **Stored XSS via device-supplied metadata** (device model/OS strings originate from untrusted clients and are displayed to admins) | Strict length/character validation at the API boundary. Razor output encoding (no raw HTML rendering). Content-Security-Policy without inline scripts | M | L |
| TH-37 | E | TA-6 | **Privilege escalation between admin roles** | Centralised permission-based policies. Server-side checks on every action. Role/permission management restricted to its own permission. All grants audited | M | L |
| TH-38 | I | TA-6 | **Unauthorised viewing of location or attendance data** | Report permissions scoped by report type. Distances instead of coordinates (ASM-06). Report access audited | M | L |
| TH-39 | T | TA-6 | **Falsified attendance corrections** | Corrections are permitted (DEC-08) and the two permissions sit in different roles (DEC-10). Maker–checker with approver ≠ requester enforced in the stored procedure and by a table constraint. Corrected times are confined to the record's own attendance day and may not be in the future; one pending correction per record. Original record preserved and the correction linked. Append-only audit | H | L–M |

### 7.7 Data tier, audit and operations

| ID | STRIDE | Actor | Threat scenario | Controls | Inh | Res |
|---|---|---|---|---|---|---|
| TH-40 | R, T | TA-6, TA-7 | **Tampering with audit or attendance history** | Application logins cannot UPDATE/DELETE audit data. The audit and security-event tables are **SQL Server 2022 append-only ledger tables**; attendance and location evidence are deliberately not (DB-09 — a ledger table can never be purged, which would make any retention policy impossible), and are protected by EXECUTE-only access, constraints and the audit trail of every change. Database digests are exported to organisation-controlled WORM storage by `ops/Export-LedgerDigest.ps1` and verified by `ops/Test-LedgerIntegrity.ps1` (`job.usp_Maintenance_GenerateLedgerDigest` / `_VerifyLedger`). SQL Server Audit for privileged DBA activity | H | L–M (a host administrator can alter files, but ledger verification detects it) |
| TH-47 | D, R | TA-1, TA-2 | **Flooding the security ledger with unverifiable requests.** Signature verification precedes the rate limiter by design, and each refusal writes to an append-only ledger that cannot be purged | Per-address budget of failed signatures (`ISignatureFailureBudget`): once spent, refusals are answered 429 without a device lookup or a ledger write, and a single event records that recording stopped. Bounded at limit + 1 rows per address per minute. A caller commanding many addresses still costs that per address, so an internet-facing deployment needs a network control in front (OPEN-20) | M | L |
| TH-48 | E, R | TA-6, TA-7 | **Abuse of administrator recovery** to take over an account | Not reachable from the portal or either API: the `recovery` schema is denied to every application login, so a compromised web process cannot call it. It requires database-administrator rights, which could already alter the tables. It adds a proper hash, rotated sessions, both factors replaced, and an append-only audit entry recording the operator's login and host. `Administrator.AccessRecovered` is listed as an immediate escalation in the runbook | H | L |
| TH-41 | I | TA-7 | **Backup theft** | SQL Server backup encryption and Transparent Data Encryption (both available in SQL Server 2022 **Standard and Enterprise**, not Express/Web). Certificates backed up separately from database backups. Restricted backup share | M | L |
| TH-42 | D | — | **Loss of the Data Protection key ring or certificate** makes TOTP secrets undecryptable | Key ring and certificate private key included in backup and escrow. Keys never deleted, only retired. Restore tested in DR drills (OPEN-18/19) | M | M |
| TH-43 | T | TA-6 | **Timezone or attendance-window misconfiguration** shifts attendance dates | Timezone validated against `sys.time_zone_info`. Settings changes audited with old/new values. Readiness check reports unconfigured settings. NTP on all servers | M | L |
| TH-44 | D | — | **Platform vendor dependency outage** (App Attest unreachable, revocation list unavailable) | Registration only, never clock-in/out. Cached revocation list with defined staleness policy. Clear `REGISTRATION_TEMPORARILY_UNAVAILABLE` response | M | L |
| TH-45 | T, E | TA-8, insider | **Compromise of mobile release signing keys** allows a malicious app build that passes attestation app-identity checks | Signing keys held on restricted build infrastructure with access audit. Android Play App Signing or equivalent handled by IT Ops decision (OPEN-43) | M | M |

### 7.8 Privacy

| ID | STRIDE | Actor | Threat scenario | Controls | Inh | Res |
|---|---|---|---|---|---|---|
| TH-46 | I | TA-6, TA-7 | **Excess collection or retention** of location, device and attendance data | Data minimisation (ASM-06). Only justified device metadata. Retention settings and purge job (OPEN-14). Legal review (OPEN-41) | M | L–M (pending OPEN-14/41) |

---

## 8. Defence-in-depth summary

| Layer | Key controls |
|---|---|
| Device | Hardware-backed non-exportable signing key; secure storage for user ID; root/jailbreak and emulator heuristics; mock/simulated location rejection; no secrets cached; no offline queueing |
| Transport | TLS on every hop; Android default distrust of user CAs; request signatures with body digest; replay window + nonce store |
| Perimeter | DMZ reverse proxy; connection/rate limits; trusted-proxy forwarded headers; admin portal not published |
| Application | Signature → device status → rate limit → validation → credential check ordering; idempotency; centralised errors; structured redacted logging; permission-based authorization |
| Data | Stored-procedure-only access; per-application least-privilege logins; constraints and locking for attendance integrity; encrypted TOTP secrets; append-only ledger tables for audit |
| Operations | Key ring backup/escrow; ledger digest export and verification; NTP; dependency pinning and review |

---

## 9. Residual risks requiring explicit business acceptance

| ID | Residual risk | Why it cannot be eliminated | Owner |
|---|---|---|---|
| RR-01 | **Location can be falsified on a sufficiently compromised device or with radio spoofing** (TH-03, TH-04) | GPS is reported by the device. Client-side integrity checks are bypassable (§65) | Business owner |
| RR-02 | **Buddy punching with deliberately shared phone and credentials** (TH-05) | Willing collusion defeats possession- and knowledge-based factors | Business owner / HR |
| RR-03 | **iOS hardware key binding cannot be server-verified without App Attest** (TH-07, OPEN-33) | Only App Attest provides server-verifiable evidence that the key is in the Secure Enclave and belongs to the genuine app | Security |
| RR-04 | **Honest employees rejected by 5 m radius and accuracy rule** (TH-06) | Consumer location accuracy is often worse than 5 m indoors | Business owner |
| RR-05 | **Volumetric DDoS** (TH-29) | No cloud scrubbing by design (§2.1). Depends on perimeter/ISP capabilities | IT Ops |
| RR-06 | **Stolen unlocked phone can clock the employee out** (TH-21) | Clock-out requires no password/TOTP by requirement (§14) | Business owner |
| RR-07 | **Administrator collusion** (TH-09, TH-39) | Authorised users acting within permissions. Mitigated by audit and segregation, not prevented | Business owner |

---

## 10. Security reality statements (`Claude.md` §65)

- **GPS is not a security boundary.** The location check is a proximity control that raises the cost of cheating. It does not prove physical presence.
- **Device identifiers are not permanent hardware IDs.** The device credential is a server-registered hardware-backed key pair. Reinstall or reset requires re-registration.
- **Root/jailbreak detection is bypassable** and is treated as defence in depth. The server never relies on the client's own verdict alone.
- **The mobile app is an untrusted client.** Every authorization decision is made server-side.
- **Encryption does not make secrets safe by itself.** TOTP-secret protection depends on the key ring and certificate being access-controlled and backed up.
- **HTTPS is mandatory.** No additional payload encryption is implemented (DEC-02). Integrity and replay protection come from request signatures.
- **Client timestamps are not authoritative.** Attendance time is set inside SQL Server.
- **A 5-metre radius is technically demanding** and will produce false rejections in some environments.

---

## 11. Security test traceability (`Claude.md` §50)

| Required security test | Threats covered | Test level |
|---|---|---|
| Brute-force protection | TH-13, TH-14, TH-15, TH-20 | API integration + stored procedure tests |
| Replay attacks | TH-10, TH-16, TH-23 | API integration (signed request replay, nonce reuse, stale `created`) |
| Invalid device / revoked device | TH-07, TH-12, TH-21 | API integration + stored procedure |
| Invalid location / location boundary (0, 1, 3, 4.9, 5.0, 5.1, 10 m) | TH-01, TH-02, TH-06 | Domain unit tests + API integration |
| Invalid OTP / OTP replay | TH-15, TH-16, TH-17 | Application + API integration |
| Duplicate clock-in / clock-out, concurrency | TH-24, TH-25 | Stored procedure concurrency tests against SQL Server + API parallel requests |
| Unauthorized administrative access / privilege escalation | TH-28, TH-37, TH-38 | Admin integration tests per permission |
| CSRF | TH-34 | Admin integration tests (missing/invalid token rejected) |
| Session expiration | TH-35 | Admin integration tests |
| Input validation / stored XSS | TH-36, TH-27 | Validator unit tests + Admin rendering tests |
| Error disclosure | TH-30 | API tests assert no exception or SQL detail in responses |
| Log redaction | TH-31 | Logging tests with captured sink |
| Mock/simulated location, root/jailbreak behaviour | TH-01, TH-02, TH-03 | Flutter unit/widget tests with faked platform services. On-device manual verification where platform test facilities do not allow automation |
