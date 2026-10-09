# 01 — Requirements Clarification & Assumptions Register

| Item | Value |
|---|---|
| Document | ClockInXtra — Requirements Clarification & Assumptions Register |
| Phase | 1 of 24 (see `Claude.md` §66) |
| Version | 0.1 (draft for business sign-off) |
| Date | 2026-09-11 |
| Status | **Draft — contains OPEN REQUIREMENTS that block production release** |

## 1. How to read this register

| Prefix | Meaning | Who may change it |
|---|---|---|
| `DEC-nn` | Stakeholder decision confirmed in writing | Business owner |
| `TD-nn` | Technical decision made by the architecture team. Not a business rule. Reversible with documented impact | Architecture |
| `ASM-nn` | Assumption the build relies on. Must be confirmed or corrected before production | Business owner |
| `CON-nn` | Conflict between a requirement and a platform/security reality | Architecture + business owner |
| `OPEN-nn` | **OPEN REQUIREMENT**: an unresolved business, legal or operational decision | Named owner |

**Rule for unresolved business settings.** Where a business rule is OPEN (for example clock-in closing time or business timezone), the system ships with that setting **unset**. It **refuses the affected operation** with `ATTENDANCE_NOT_CONFIGURED` until an authorised administrator sets it. The readiness health check reports `Degraded` while mandatory settings are missing. No business value is invented in code or seed data.

---

## 2. Confirmed stakeholder decisions

Captured 2026-09-11.

### DEC-01 — Employee identity source (resolves §68.1 and §68.22)
- The Clock-In password is validated through an `IEmployeeCredentialValidator` abstraction.
- **First implementation:** a local credential store in SQL Server with password hashing (see TD-05).
- **Active Directory is not required now.** An LDAPS-based provider can be added later without changing the mobile API contract.

### DEC-02 — Request/response protection (resolves §68.23)
- **TLS is mandatory** on every hop that carries API traffic, including reverse proxy → application servers (TLS bridging, no plaintext in the DMZ or internal segments).
- **No application-layer payload encryption.** Instead, every mobile request is **signed** with a hardware-backed, non-exportable device key (see TD-07).
- **Why signing and not a second encryption layer** (required by §23):
  - *Confidentiality:* TLS already provides it end to end to organisation-controlled endpoints. A second encryption layer protects nothing extra unless TLS is terminated by an untrusted party, and this deployment has none.
  - *What TLS does not provide:* (a) proof that the request came from a specific registered device, (b) integrity after TLS termination at the proxy, and (c) protection against replay of a request captured on a compromised client. Request signing with nonce and timestamp addresses all three.
  - *Cost of the rejected alternative:* per-device key agreement, key provisioning, rotation and revocation for payload keys. That adds operational risk without addressing any threat left open by TLS plus signing.
- **Stated plainly, so the original requirement is not misrepresented:** "all endpoints request and response should be encrypted" is met by mandatory TLS. Payloads are not additionally encrypted at the application layer.

### DEC-03 — API exposure (resolves §68.20)
- The mobile API is **internet-facing**, published through a reverse proxy in a DMZ.
- Consequences accepted with this decision:
  - GPS-based proximity is the **only** location signal. Network location (office Wi-Fi/VPN) cannot be used as a second factor.
  - The API is exposed to internet-scale credential stuffing, scanning and denial-of-service. Controls are listed in `02-threat-model.md`.
  - The API TLS certificate must be trusted by unmanaged mobile OS trust stores (see OPEN-21).

### DEC-05 — iOS App Attest accepted (resolves OPEN-33 for iOS)
*Decided 2026-09-12.*

- The server **requires Apple App Attest** for iOS device registration.
- **What this buys.** Without it the server cannot verify that an iOS device's private key really lives in the Secure Enclave, or that the app is the genuine build — iOS registrations would be materially weaker than Android's, which residual risk RR-03 recorded. With it, both platforms produce attestation the server verifies itself.
- **What it costs.** The device contacts Apple's App Attest service once per key: `DCAppAttestService.attestKey` "asks Apple to attest to the validity of a generated cryptographic key" and reports `serverUnavailable` when it cannot reach them. The **verification** of the resulting attestation happens on our servers against Apple's root, so no attendance decision depends on Apple being reachable — only registration does.
- **Relationship to the no-cloud rule (§2.1).** This is a platform-vendor service the *device* calls, not a SaaS dependency of our infrastructure. No attendance data goes to Apple. The conflict was surfaced as CON-02 and accepted deliberately rather than absorbed silently.
- Consequences: `Security.RequireHardwareAttestationIos` is set to `true`; registration fails with a distinct retryable code when Apple is unreachable (TH-44); App Attest is unavailable on simulators and in app extensions, so those cannot register.
- Android is unaffected: key attestation is verified entirely on-premises, with the revocation list the only outbound fetch.

### DEC-04 — Employee ↔ device binding (resolves §68.3 and §68.4)
- An employee has **at most one active device**.
- A device is bound to **exactly one employee**. Shared devices are not supported.
- Registering a replacement device requires **administrator approval**. Approval atomically **revokes** the employee's previously active device.
- Enforced in the database by filtered unique indexes, not only in C#.

### DEC-08 — Attendance corrections (resolves §68.10 and §68.12; decided 2026-09-19)
- Administrators **may correct** attendance records, and every correction **requires approval by a second administrator**. The requester can never approve their own request (enforced by the procedure and by `CK_AttendanceCorrection_SeparationOfDuties`).
- A correction changes clock-in and/or clock-out **within the record's own attendance day**, never to a time in the future, one pending correction per record at a time. The original times are kept with the correction permanently, and both request and decision are in the audit ledger.
- Portal: **Corrections** (list, approve, reject — rejecting needs a reason) and **Correct…** on each row of the attendance report. Times are entered in business-local time.
- Who requests and who approves is **DEC-10**.

### DEC-11 — Employee details: every field required, department and job title from lists (decided 2026-09-21)
- The business owner decided that **no employee field is optional**: user id, first and last name, employee number, email, phone, department and job title are all required. This replaces the earlier default, which followed Claude.md §17 in not inventing organisational fields as mandatory — that rule forbids *assuming* them mandatory, and the business has now chosen to.
- **Department and job title are chosen from lists**, not typed: `core.Department` and `core.JobTitle`, maintained in the portal (People → Departments & job titles) by anyone holding `MobileUser.Manage`.
- An entry is **renamed** (every employee holding it is renamed with it, by `ON UPDATE CASCADE`) or **withdrawn** (no longer offered; employees who hold it keep it). Entries are never deleted while in use. Names differing only by capitals are refused as duplicates.
- Enforced by `usp_MobileUser_Create` / `_Update` as well as the form: a missing field, a malformed email or phone, or a department or job title that is not an active list entry is refused.
- **Existing records** created before this decision may lack some details. They are not rejected; the employee list names what each is missing, and the new **Edit details** form is where they are completed. On the development database this applies to `e.adeyemi` (job title, email and phone missing).
- Values already held by employees were copied into the lists when they were introduced, so no record was orphaned.

### DEC-10 — Who may correct attendance (resolves §68.11; decided 2026-09-19)
- **Attendance Administrators request** corrections: the seeded role holds `Attendance.Correct` and **not** `Attendance.ApproveCorrection`, so they can never approve one.
- **Super Administrators approve or reject** them: the seeded role holds `Attendance.ApproveCorrection`.
- Super Administrator also keeps `Attendance.Correct`, for a mechanical reason rather than a business one: an administrator may only grant a role whose every permission they already hold (`usp_Administrator_SetRole`, result 1003), and `Administrator.Manage` belongs to Super Administrator alone. Without it, **nobody could ever assign the Attendance Administrator role**. Holding the permission keeps the role grantable; the working practice is still that Attendance Administrators raise requests.
- What guarantees the separation is not the permission split but the database: `usp_Attendance_ApproveCorrection` and `CK_AttendanceCorrection_SeparationOfDuties` both refuse an approval by the administrator who requested it. No role grant can override that.
- Granting `Attendance.Correct` to a further role later is a role change in the portal, not a code change.

### DEC-09 — Location accuracy threshold (resolves §68.25; decided 2026-09-19)
- The business asked for "the threshold phones can manage". `Location.MaxAcceptedAccuracyMeters` = **20 m**, with `Location.AccuracyPolicy` = `DistanceAndAccuracyThreshold` confirmed.
- Why 20 m: phones commonly report a few metres under open sky but routinely 10–20 m indoors and beside buildings, where clock-ins happen. Android's figure is a 68%-confidence radius (CON-01). 20 m accepts ordinary indoor readings while still refusing fixes too vague to place a person at a building.
- **What it does not change:** the reported position must still be within the office's radius (5 m). With readings uncertain by up to 20 m, many honest clock-ins will still fall outside 5 m. The radius is per office and is the business's to set; the validation-failure report's summary (LOCATION_NOT_ALLOWED vs. LOCATION_ACCURACY_INSUFFICIENT) and the office accuracy statistics are the evidence to decide it with.

### DEC-07 — Data retention (resolves §68.14; decided 2026-09-19)
- There is **no retention period**: attendance records and their location evidence are kept **for as long as possible**, i.e. indefinitely. `Retention.AttendanceDays` and `Retention.AttendanceEvidenceDays` are confirmed **blank**, which is how "keep indefinitely" is expressed; the purge job runs and deletes nothing.
- Setting a number of days later is all it takes to start purging. Values below 1 day are refused (and ignored by the purge job), because 0 would delete every earlier record at once.
- The audit and security-event ledgers are never purged regardless (DB-08).
- **Caveat, not a blocker:** the Nigeria Data Protection Act 2023 expects personal data to be kept no longer than necessary for its purpose. Indefinite retention of employee attendance and location evidence is the business's decision, and whether it is compliant is for Legal / the DPO to confirm — **OPEN-41 stays open**.
- Operational retention of replay nonces (2 hours) and idempotency results (48 hours) is unchanged: those are short-lived security records, not attendance data.

### DEC-06 — Attendance time zone and times (resolves §68.5–§68.9; decided 2026-09-19)
- **Time zone:** Nigerian time, UTC+1 — Windows ID `W. Central Africa Standard Time` (`Attendance.BusinessTimeZoneId`). Nigeria observes no daylight saving, so the offset is fixed. The attendance day runs midnight to midnight in this zone.
- **Clock-in** is accepted from **00:00 to 08:30** business time. A clock-in after 08:30 is **rejected**, not flagged: late clock-in is not permitted. No grace period.
- **Clock-out** is allowed only for an employee who has clocked in that day, from **1 minute after their own clock-in** until **23:59** (end of the attendance day). There is no fixed clock-out opening time, and so no "early clock-out".
- A record not closed by midnight stays open and is reported as a **missing clock-out**; closing it needs an attendance correction (DEC-08, DEC-10).
- Settings: `Attendance.ClockInOpenTime` = 00:00, `Attendance.ClockInCloseTime` = 08:30, `Attendance.ClockInAfterCloseAction` = Reject, `Attendance.ClockOutOpenTime` = 00:00, `Attendance.ClockOutBeforeOpenAction` = Reject (never triggers), `Attendance.MinimumMinutesBeforeClockOut` = 1 (added for this decision).
- Boundary as implemented: 08:30:00 exactly is accepted, 08:30:01 is refused. If "until 08:30" is meant to include the whole minute, set the closing time to 08:30:59.
- Verified 2026-09-19 against the development database with these exact settings: a clock-in at 07:50 Lagos time was accepted, an immediate clock-out refused (1052), and a clock-out 61 seconds later accepted without an early flag.

---

## 3. Technical decisions (architecture-owned)

| ID | Decision | Rationale / verification |
|---|---|---|
| TD-01 | **.NET 10 (LTS)**, ASP.NET Core 10 | Newest LTS. 10.0.12 current as of 2026-09-08, supported until **2028-11-14** (dotnet.microsoft.com support policy). .NET 8 and .NET 9 both end support 2026-11-10. |
| TD-02 | **Flutter stable 3.47.4 / Dart 3.13.3** | Current stable as of 2026-09-11 (Flutter release manifest). **The workstation has Flutter 3.38.8 / Dart 3.10.7 and must be upgraded:** `flutter_riverpod` 3.4.x requires Dart ≥ 3.12. |
| TD-03 | **SQL Server 2022** as development baseline | Local instance is SQL Server 2022 Developer 16.0.1200.5. Production edition depends on availability requirements (OPEN-17). |
| TD-04 | **TOTP per RFC 6238**: HMAC-SHA-1, 6 digits, 30-second step, ±1 step tolerance, per-credential last-accepted-step replay protection | SHA-1/6/30 are the Key URI Format defaults and the most interoperable parameters across authenticator apps. HMAC-SHA-1 in HOTP/TOTP does not depend on SHA-1 collision resistance. Library: **Otp.NET 1.4.1** (`VerifyTotp` reports the matched time step, which is needed for replay protection). |
| TD-05 | **Password hashing: PBKDF2-HMAC-SHA512, 220,000 iterations**, 32-byte random salt, 64-byte derived key, versioned hash format with rehash-on-login when parameters change. Uses the built-in `Rfc2898DeriveBytes.Pbkdf2` (Windows CNG). | OWASP Password Storage Cheat Sheet lists PBKDF2-HMAC-SHA512 at 220,000 iterations. OWASP prefers Argon2id, but .NET has no built-in Argon2 (dotnet/runtime discussion #117822). The main managed package (Konscious 1.3.1, June 2024) does not target .NET 10, and Isopoh 2.0.0 (August 2023) targets up to .NET 7. A first-party OS-backed primitive is chosen to reduce supply-chain risk in the credential path. The versioned format allows migrating to Argon2id later. |
| TD-06 | **Encryption of secrets at rest** (TOTP secrets, and coordinates if OPEN-35 requires storage): **ASP.NET Core Data Protection**. Key ring persisted with `PersistKeysToFileSystem` to an ACL-restricted share, protected by `ProtectKeysWithCertificate` using an organisation-PKI certificate. Same `SetApplicationName` in API and Admin. | Established Microsoft authenticated-encryption API; no custom cryptography. Supports web farms and key rotation (default key lifetime 90 days). A database administrator alone cannot decrypt TOTP secrets without both the key ring and the certificate private key. **The key ring and certificate must be backed up**: losing them forces re-enrolment of every authenticator. |
| TD-07 | **No bearer tokens for the mobile API.** Each request is authenticated by an ECDSA P-256 signature from a key generated inside Android Keystore (TEE/StrongBox) or the iOS Secure Enclave. The server stores only the public key. Signature profile, nonce and timestamp rules are specified in `03-solution-architecture.md`. | Removes token issuance, refresh, storage and revocation from the untrusted client. Device revocation takes effect on the next request. Android `KeyGenParameterSpec` supports attestation challenges. `SecureEnclave.P256.Signing.PrivateKey` is available from iOS 13 (Apple reference). |
| TD-08 | **State management: Riverpod 3** (`flutter_riverpod` 3.4.3, verified publisher, MIT) | Actively maintained (last release 7 days ago). Provides dependency injection and state in one mechanism with test overrides. `flutter_bloc` 9.1.1 was considered; its last release was 16 months ago. |
| TD-09 | **Authoritative time is set inside SQL Server.** `SYSUTCDATETIME()` is taken within the clock-in/clock-out stored procedures. The attendance date is derived in the same transaction using `AT TIME ZONE` with the configured Windows time-zone ID (validated against `sys.time_zone_info`). | One clock for all application servers, with no dependence on device time or clock skew between servers. |
| TD-10 | **Security counters live in SQL Server**: account lockout, OTP failures, registration attempts and nonce replay. The in-process ASP.NET Core rate limiter is only first-line throttling. | ASP.NET Core rate-limiter state is per process and does not coordinate across IIS instances, so it cannot be the security control in a multi-server deployment (§45). |
| TD-11 | **Rejected components** | **freeRASP:** the free tier sends telemetry to Talsec and cannot be disabled without the commercial plan, which violates §2.1. **flutter_jailbreak_detection:** no release in 3 years. **Google Play Integrity API:** verdicts come from Google's cloud service, which violates §2.1. |
| TD-12 | **Geodesic distance: Haversine** on mean Earth radius 6,371,008.8 m, implemented in the Domain layer. Tests cross-check against SQL Server `geography::STDistance`. | The spherical model's error is a small fraction of a percent: millimetre to centimetre scale at 5 m. That is far below GPS measurement uncertainty. No Euclidean-on-degrees maths. |
| TD-13 | **API documentation:** `Microsoft.AspNetCore.OpenApi` (built into ASP.NET Core 10), UI via `Swashbuckle.AspNetCore.SwaggerUI` 10.2.3 **enabled only outside Production** | Microsoft guidance: OpenAPI UIs should only be enabled in development environments. |
| TD-14 | **Other verified backend packages:** Dapper 2.1.79 (net10.0), Microsoft.Data.SqlClient 7.0.3 (`Encrypt=Mandatory` default), Asp.Versioning.Mvc 10.2.1, Serilog.AspNetCore 10.0.0, FluentValidation 12.1.1 (core package only; `FluentValidation.AspNetCore` is deprecated), System.Formats.Cbor 10.0.12 (Microsoft), xunit.v3 4.0.0 | Versions and target frameworks checked on nuget.org 2026-09-11. Exact pins are recorded in `Directory.Packages.props` during implementation. |
| TD-15 | **Database-enforced privilege separation**: mobile API and Admin portal use **different SQL logins**. Each gets `EXECUTE` only on its own schema of stored procedures (`mobile.*`, `admin.*`) and no table permissions. | A compromised internet-facing API process cannot call administrative procedures, even with full control of the process. |
| TD-16 | **Production connection strings set `Pool Blocking Period=NeverBlock`** and keep a short `Connect Timeout` (the 15-second default, or less) | SqlClient's default is `PoolBlockingPeriod.Auto`, documented as "Blocking period OFF for Azure SQL servers, but ON for all other SQL servers" — so an on-premises instance gets it. With the blocking period on, one failed login makes every later open on that pool fail **instantly with the same cached error** for five seconds, doubling on each further failure to a maximum of one minute (Microsoft.Data.SqlClient reference, checked 2026-09-27). An availability-group failover or a service restart therefore extends into up to a minute of hard failures *after* SQL Server is serving again, and the logged reason is the original error rather than the real one. `NeverBlock` lets the first request after recovery through. **The trade-off:** during a genuine outage each request then waits out the connect timeout instead of failing at once, occupying a request thread — which is why the connect timeout must stay short. Operations may prefer `AlwaysBlock` if failing fast through a long outage matters more than recovering quickly from a short one; either way it belongs in the connection string, not in code. The test suite sets `NeverBlock` for a different reason (T-4) and with much longer timeouts, which are for tests only |
| TD-18 | **Either host creates the database and its objects when it starts**, serialised by `sp_getapplock` taken in `master`. Off by default outside Development | Both hosts are often started together, either may be first, and either may find nothing there. An application lock lives in the database it is taken in and the application database may not exist yet, so `master` is the only place both can queue; the lock is session-scoped because it has to span `CREATE DATABASE` on one connection and the object scripts on another, which no single transaction could. The loser waits, then finds the work done. Three properties make that safe rather than merely ordered: the scripts are idempotent, the fingerprint that records success is written **last** so a half-finished deployment is retried, and closing the connection releases the lock so a killed host blocks nobody. **Default off outside Development** because creating a database needs `CREATE DATABASE`, DDL on every schema and `ENABLE LEDGER` — rights §48 forbids the application logins from holding. Where it is wanted on a server, `Database:ConnectionString` supplies a separate deployment identity used for the seconds of startup and never for a request. Verified 2026-10-09 with the API and the portal started together against an absent database: one created it, the other waited and did nothing, both answered their liveness probe, one fingerprint row |
| TD-17 | **Authenticator step tolerance is the setting `Security.TotpStepTolerance`** (seeded 1, bounded 1–10 steps), read through the administrator and mobile security policies, and passed to `ITotpVerifier.Verify` as an argument rather than carried in `TotpParameters` | It was hard-coded in three places and inconsistently: `MfaEnrolmentRepository` wrote `StepTolerance: 1`, `MobileUserRepository` copied `TotpParameters.Default`, and `AdministratorAuthenticator` ignored the credential's parameters altogether and passed `TotpParameters.Default`. `TotpParameters` documents itself as what was *recorded at enrolment*, which the tolerance is not: it is one policy for every credential and it can change after enrolment, so holding it there invited each repository to invent its own value. Making it an argument means a caller cannot forget to supply one. **The bound matters more than the default:** tolerance N keeps a code usable for about (2N+1)×30 seconds, so an unbounded setting could leave codes valid for hours. Replay protection still consumes the matched time step in the database, so a code is spent once whatever the tolerance — the risk a wider window adds is the time an observed code stays worth stealing, which is why 10 steps (5½ minutes) is the ceiling. Raising it in production should follow evidence of genuine clock drift, not sign-in latency: **a server slow enough to need a wider window is a capacity problem, and widening it hides that.** |

---

## 4. Assumptions (confirm or correct before production)

| ID | Assumption | Why it is needed | If wrong |
|---|---|---|---|
| ASM-01 | The **Admin portal is internal-only** (not published to the internet) | Reduces the attack surface for the most privileged functions | Needs a WAF, stricter MFA and a separate DMZ publication design |
| ASM-02 | **At most one attendance record per employee per attendance day** (one clock-in, one clock-out) | §11 says "already clocked in for the current attendance day". Enforced by a unique index | Shift or multi-session attendance requires a schema change (OPEN-30) |
| ASM-03 | **Attendance day** = calendar date, in the configured business timezone, of the server clock-in timestamp. An open record from a previous day does **not** block today's clock-in. It appears in the *Missing clock-outs* report. | §15 lists "attendance day definition" as configurable, and §11 scopes status to the current day | Midnight-spanning attendance (OPEN-28) needs a different rule |
| ASM-04 | Clock-out needs **no password or TOTP**, only a signed request from the bound device plus location, as specified in §14 | Faithful to §14 | Anyone holding the unlocked phone can clock the employee out. Accepted per requirement; see threat model |
| ASM-05 | The **startup location check (§8) is a user-experience gate**. The authoritative location check is repeated server-side inside Clock-In and Clock-Out. | A client that skips startup gains nothing | None. This is a strictly safer interpretation |
| ASM-06 | **Raw coordinates are not persisted by default.** The system stores matched office, computed distance (metres), reported accuracy and mock-location flag. | Data minimisation (§26, §63) | If audit requires raw coordinates, they are stored encrypted (TD-06) with restricted permission (OPEN-35) |
| ASM-07 | Employee Clock-In passwords are **issued and reset by administrators** in the Admin portal | §17 plus DEC-01 local store | Self-service password change needs a new mobile flow (OPEN-40) |
| ASM-08 | Employees can install the app from the official store or an MDM channel, and devices have **internet access** at the office | DEC-03 | Offline or captive networks prevent attendance by design (§52) |

---

## 5. Conflicts & platform realities

These are not solved by pretending. Each one is surfaced to the business owner.

### CON-01 — A 5-metre radius is at or below typical smartphone location uncertainty
- The device reports its own horizontal accuracy estimate. Indoors and near buildings this is frequently **worse than 5 m**, so genuine employees standing in the office may be rejected.
- **The accuracy values are not directly comparable across platforms.**
  - Android `Location.getAccuracy()` is a radius at **68% confidence**: roughly one time in three the true position is *outside* the reported circle.
  - iOS `CLLocation.horizontalAccuracy` is documented only as "the radius of uncertainty", with no confidence level stated. A negative value means the coordinates are invalid.
  - The server stores the platform alongside accuracy so reports and future tuning can separate the two.
- The system **never widens the radius** to compensate for poor accuracy (§26).
- **Interim rule (strict, pending OPEN-25):** reject when accuracy is unavailable or worse than the matched office's `AllowedRadiusMeters`.
- **Recommendation:** run a **pilot measurement at each office** (reported accuracy and distance at the actual clock-in spots) before go-live. Use per-office radius configuration, with business-owner approval, based on the measured data.
- Consumer GPS **cannot reliably distinguish 4.9 m from 5.1 m**. Boundary tests (§51) verify the arithmetic, not real-world GPS precision.

### CON-02 — "No cloud" vs. platform attestation services
- **iOS App Attest:** `DCAppAttestService.attestKey` "asks Apple to attest to the validity of a generated cryptographic key". The device must reach Apple's App Attest service (documented `serverUnavailable` error). The resulting attestation is verified on **our** servers.
- **Android Key Attestation:** certificate chains are verified on **our** servers against Google's published root certificates. Revocation status is published only at `https://android.googleapis.com/attestation/status`, so our server needs a periodic outbound fetch or a mirror.
- Neither makes our servers depend on a SaaS for request processing. Both do involve a platform vendor's online service. **Decision required: OPEN-33.** Without them, the server cannot verify that a device key is hardware-backed.

### CON-03 — "All requests and responses encrypted"
Met by mandatory TLS per DEC-02. No application-layer payload encryption exists. This must not be described otherwise in any document.

### CON-04 — Root/jailbreak blocking cannot be guaranteed
- Client-side detection is bypassable on a device the attacker controls (§25, §65).
- The strongest signal available without Google Play Integrity is **Android Key Attestation**: `verifiedBootState` and `deviceLocked`, signed by the device's secure hardware and verified server-side.
- iOS has no equivalent server-verifiable boot-state signal. iOS relies on client heuristics plus Secure Enclave key binding.

### CON-05 — There is no permanent, permitted hardware device ID
- Android 10+ restricts IMEI/serial to privileged apps.
- iOS `identifierForVendor` changes when all of the vendor's apps are removed and reinstalled.
- **Design:** the server issues a `DeviceId` at registration and binds it to the hardware-backed public key. **The key pair is the device credential. The OS identifier is recorded as metadata only.**
- App reinstall, device reset or restore creates a new key, and therefore requires re-registration (with administrator approval per DEC-04).

### CON-06 — iOS builds require macOS
- Flutter iOS builds, signing and App Store/enterprise distribution require **macOS with Xcode**. The development workstation is Windows.
- An on-premises macOS build agent is required for iOS (see OPEN-42).
- The workstation's Android toolchain also reports a missing `aapt` in build-tools 36.1.0, which must be repaired (reinstall build-tools).

### CON-07 — Mock-location detection is partial
- Android exposes `isMocked` for mock-provider locations (geolocator `Position.isMocked`).
- iOS 15+ exposes `CLLocationSourceInformation.isSimulatedBySoftware`. geolocator does not surface it, so a native channel is required.
- Neither detects hooked location APIs on compromised devices or external radio-level GNSS spoofers.

### CON-08 — iOS jailbreak-detection library licensing
IOSSecuritySuite is under a custom EULA: free for organisations with 0–99 employees, paid above that. If selected, licensing must be approved. Otherwise the heuristics are implemented in-house (non-cryptographic checks, no licence dependency). Decided in Phase 15.

### CON-09 — Error-code granularity vs. credential enumeration
- §60 lists both `INVALID_CREDENTIALS` and `INVALID_OTP`.
- On an internet-facing endpoint, returning `INVALID_OTP` confirms that the password was correct, which turns a password-guessing attack into a confirmation oracle.
- **Recommendation:** the mobile API returns `INVALID_CREDENTIALS` for unknown user, wrong password **and** wrong OTP. The precise reason is recorded only in the security event log. **Decision required: OPEN-38.**

### CON-10 — The authenticator app may live on the same phone
If the TOTP authenticator is on the attendance phone, possession of that phone plus the password satisfies both factors. The system cannot technically detect or prevent this. It is a policy matter (OPEN-37).

### CON-11 — Tamper-evident audit vs. data-retention deletion
- SQL Server 2022 **append-only ledger tables** are available in every edition and give tamper evidence against DBAs and administrators.
- However, Microsoft documents that **deleting older data in append-only ledger tables isn't supported** and `TRUNCATE TABLE` isn't supported. Ledger tables also can't be converted back to regular tables.
- If OPEN-14 requires audit or security events to be purged after a retention period, ledger tables cannot satisfy that requirement row by row.
- **Interim design:**
  - `AuditLog` and `SecurityEvent` are append-only ledger tables, with **no purge**.
  - Attendance and location evidence stay in regular tables protected by `EXECUTE`-only permissions, so retention purges remain possible.
  - If Legal mandates deletion of audit data, the design switches to periodic (for example yearly) audit tables whose whole table can be dropped at end of retention. SQL Server keeps dropped ledger tables renamed for audit consistency, so this must be validated with Legal before adoption.

---

## 6. OPEN REQUIREMENT register

"Interim technical behaviour" is what the software does **until the owner decides**. It is never presented as a business decision.

### 6.1 Items from `Claude.md` §68

| # | Item | Status | Interim technical behaviour | Owner |
|---|---|---|---|---|
| 1 | Identity source for employee passwords | **RESOLVED** (DEC-01) | Local store, pluggable | — |
| 2 | TOTP enrolment process | **OPEN REQUIREMENT** | Admin-initiated enrolment in the portal: secret shown once as QR/key, activated only after the employee proves a valid code. No self-enrolment path exists | Security / HR |
| 3 | One employee, multiple devices | **RESOLVED** (DEC-04) | Max 1 active | — |
| 4 | One device, multiple employees | **RESOLVED** (DEC-04) | Not allowed | — |
| 5 | Attendance timezone | **RESOLVED** (DEC-06) | Nigerian time: `W. Central Africa Standard Time`, UTC+1, no daylight saving | — |
| 6 | Clock-in closing time **and** behaviour after closing | **RESOLVED** (DEC-06) | Clock-in 00:00–08:30; after 08:30 **rejected** | — |
| 7 | Clock-out opening time **and** behaviour before opening | **RESOLVED** (DEC-06) | No fixed opening time; clock-out from 1 minute after the employee's own clock-in until 23:59 | — |
| 8 | Late clock-in permitted | **RESOLVED** (DEC-06) | Not permitted: after 08:30 a clock-in is refused | — |
| 9 | Early clock-out permitted | **RESOLVED** (DEC-06) | No early-clock-out concept: any time from 1 minute after clock-in is a normal clock-out | — |
| 10 | Attendance corrections permitted | **RESOLVED** (DEC-08) | Permitted. Portal: Corrections, and "Correct…" on each attendance report row | — |
| 11 | Who can correct attendance | **RESOLVED** (DEC-10) | Attendance Administrator requests (`Attendance.Correct`, and cannot approve); Super Administrator approves (`Attendance.ApproveCorrection`). Requester ≠ approver is a database rule, not a permission split | — |
| 12 | Corrections require approval | **RESOLVED** (DEC-08) | Yes: a second administrator approves; the requester can never approve their own (procedure and table constraint) | — |
| 13 | Required reports | **OPEN REQUIREMENT** | All §20 reports the data model supports are built. *Late arrivals* and *early clock-outs* depend on #6/#7 | Business owner |
| 14 | Data retention periods | **RESOLVED** (DEC-07) | Keep indefinitely: retention settings confirmed blank; the purge job deletes nothing. Legal confirmation under the NDPA remains OPEN-41 | — |
| 15 | Expected number of users | **OPEN REQUIREMENT** | Affects sizing only | IT Ops |
| 16 | Expected transaction volume (esp. morning peak) | **OPEN REQUIREMENT** | Affects sizing, rate limits and hashing capacity | IT Ops |
| 17 | Availability requirements | **OPEN REQUIREMENT** | Reference topology: 2 API nodes behind a load balancer; SQL HA option depends on answer | IT Ops |
| 18 | Disaster recovery (RPO/RTO) | **OPEN REQUIREMENT** | None assumed | IT Ops |
| 19 | Backup requirements | **OPEN REQUIREMENT** | Must include DB **and** Data Protection key ring + certificate | IT Ops |
| 20 | Internal-only or internet-facing | **RESOLVED** (DEC-03) | Internet-facing via DMZ | — |
| 21 | Organisation PKI / certificates | **OPEN REQUIREMENT** | Internet-facing API needs a certificate trusted by stock Android/iOS (public CA) unless MDM deploys a private root. Internal hops can use organisation PKI | IT Security |
| 22 | Active Directory integration required | **RESOLVED** (DEC-01: not now) | Extension point only | — |
| 23 | App-layer payload encryption mandatory | **RESOLVED** (DEC-02: no; TLS + signing) | — | — |
| 24 | Certificate pinning required | **OPEN REQUIREMENT** | Not implemented. If required: pin SPKI of organisation-controlled keys with a pre-provisioned backup pin and a documented rotation/recovery runbook **before** enabling | IT Security |
| 25 | Accept/reject based on reported accuracy | **RESOLVED** (DEC-09) | Accept readings with reported accuracy up to **20 m**; the office radius still decides the distance (CON-01) | — |
| 26 | GPS spoofing detection required | **OPEN REQUIREMENT** | Security default: reject Android `isMocked` and iOS `isSimulatedBySoftware` locations; switchable only with business approval | Business owner / Security |
| 27 | Clock in at multiple approved offices | **OPEN REQUIREMENT** | Any active office location is accepted | Business owner |
| 28 | Attendance spanning midnight | **OPEN REQUIREMENT** | Not supported (ASM-03) | Business owner |
| 29 | Weekends / public holidays relevant | **OPEN REQUIREMENT** | Not considered; no calendar model | Business owner |
| 30 | Shift-based attendance | **OPEN REQUIREMENT** | Not supported; single organisation-wide window | Business owner |

### 6.2 Additional items raised by this analysis

| # | Item | Status | Interim technical behaviour | Owner |
|---|---|---|---|---|
| 31 | Minimum supported Android / iOS versions | **OPEN REQUIREMENT** | Flutter 3.47 supports Android API 24–37 and iOS 15–26. **Recommended minimum: Android 9 (API 28)**, which is where hardware-enforced "unlocked device required" keys and cleartext-disabled-by-default networking begin, **and iOS 15**, the Flutter floor, which also provides `isSimulatedBySoftware`. Devices below the minimum cannot register | Business owner / IT |
| 32 | Does a **first** device registration need admin approval (DEC-04 covers replacements)? | **OPEN REQUIREMENT** | Yes: new registrations are `PendingApproval` | Business owner / Security |
| 33 | Accept iOS App Attest (device → Apple) and the Android attestation revocation-list fetch (server → Google)? | **RESOLVED** (DEC-05, 2026-09-12) for iOS: App Attest is required. The Android revocation-list fetch remains a server-side outbound call, unchanged | `Security.RequireHardwareAttestationIos` = `true`. Registration fails with a distinct retryable code if Apple is unreachable; attendance itself never depends on it. App Attest is unavailable on simulators and in app extensions, which is consistent with the iOS 15 floor proposed in OPEN-31 | — |
| 34 | MFA mandatory for administrators | **OPEN REQUIREMENT** | Yes: admin TOTP required (security recommendation) | Security |
| 35 | Persist raw clock-in/out coordinates | **OPEN REQUIREMENT** | No (ASM-06) | Legal / DPO |
| 36 | Lost/stolen device and employee-exit procedure (revocation SLA) | **OPEN REQUIREMENT** | Admin revocation available immediately; process not defined | HR / Security |
| 37 | Authenticator app allowed on the attendance phone | **OPEN REQUIREMENT** | Not enforceable technically (CON-10) | Security |
| 38 | Credential-failure error granularity | **OPEN REQUIREMENT** | Collapse to `INVALID_CREDENTIALS` on mobile API (CON-09) | Security |
| 39 | Admin portal internal-only | **OPEN REQUIREMENT** (ASM-01) | Internal-only | IT Security |
| 40 | Employee password lifecycle: initial issue, expiry, self-service change | **OPEN REQUIREMENT** | Admin issue/reset only (ASM-07) | HR / Security |
| 41 | Nigerian data-protection compliance | **OPEN REQUIREMENT** | Not assessed by engineering. The Nigeria Data Protection Act 2023 is in force and enforced by the NDPC. Legal must confirm the obligations for employee attendance and location processing, including whether an impact assessment is required, before production | Legal / DPO |
| 42 | macOS build agent and Apple Developer account (store/enterprise distribution) | **OPEN REQUIREMENT** | iOS deliverables are source-complete but cannot be built on the Windows workstation (CON-06) | IT Ops |
| 43 | Mobile distribution channel (public stores vs. MDM) | **OPEN REQUIREMENT** | Affects the attestation `attestationApplicationId` / App ID configuration and certificate trust | IT Ops |
| 45 | **Provisioning of Google's attestation revocation status list.** Google publishes the list of revoked attestation keys over the internet. Fetching it live would make device registration depend on an external service being reachable, which §2.1 rules out — and would mean an outage at Google, or blocked egress, stops employees enrolling | **OPEN REQUIREMENT** | The verifier reads a locally provisioned copy, refreshed by the operator on a schedule. When no file is configured the check does not run: chain validation, challenge binding, boot state and application identity still apply, but a key Google has declared compromised would pass. IT Ops must decide the refresh mechanism and cadence | IT Ops / Security |
| 44 | What the **startup location check may reveal to an unregistered caller**. §8.1 runs this check before device registration, so it is the one mobile endpoint an unauthenticated caller can reach, and a truthful answer to arbitrary coordinates makes it a search oracle: submit, observe, and walk the boundary until every office is located to within metres | **OPEN REQUIREMENT** | Interim behaviour is minimal disclosure. An unauthenticated caller learns only accepted/refused; the matched office identifier is returned solely to a device whose signature verified against an active registration, and the measured distance is returned to nobody. If the business needs the office name shown before registration, the endpoint must be rate-limited and the exposure accepted explicitly | Security / Business owner |

---

## 7. Sign-off

| Role | Name | Decision | Date |
|---|---|---|---|
| Business owner | | | |
| Information security | | | |
| Legal / Data protection | | | |
| IT operations | | | |

## 8. Sources consulted (2026-09-11)

- .NET support policy — https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- Flutter release manifest — https://storage.googleapis.com/flutter_infra_release/releases/releases_windows.json
- OWASP Password Storage Cheat Sheet — https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html
- dotnet/runtime Argon2 discussion — https://github.com/dotnet/runtime/discussions/117822
- ASP.NET Core Data Protection configuration — https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0
- ASP.NET Core rate limiting — https://learn.microsoft.com/aspnet/core/performance/rate-limit?view=aspnetcore-10.0
- ASP.NET Core OpenAPI — https://learn.microsoft.com/aspnet/core/fundamentals/openapi/using-openapi-documents?view=aspnetcore-10.0
- Android Key Attestation — https://developer.android.com/privacy-and-security/security-key-attestation ; schema https://source.android.com/docs/security/features/keystore/attestation
- Android identifier best practices — https://developer.android.com/identity/user-data-ids
- Apple `DCAppAttestService.attestKey` — https://developer.apple.com/documentation/devicecheck/dcappattestservice/attestkey(_:clientdatahash:completionhandler:)
- Apple `SecureEnclave.P256.Signing.PrivateKey` — https://developer.apple.com/documentation/cryptokit/secureenclave/p256/signing/privatekey
- Apple `isSimulatedBySoftware` — https://developer.apple.com/documentation/corelocation/cllocationsourceinformation/issimulatedbysoftware
- geolocator `Position` — https://pub.dev/documentation/geolocator_platform_interface/latest/geolocator_platform_interface/Position-class.html
- flutter_secure_storage — https://pub.dev/packages/flutter_secure_storage
- flutter_riverpod — https://pub.dev/packages/flutter_riverpod
- freeRASP data collection — https://docs.talsec.app/freerasp/terms-of-service/user-data-policies
- IOSSecuritySuite licence — https://github.com/securing/IOSSecuritySuite
- NuGet: Otp.NET, Dapper, Microsoft.Data.SqlClient, Asp.Versioning.Mvc, Serilog.AspNetCore, FluentValidation, Swashbuckle.AspNetCore.SwaggerUI, System.Formats.Cbor, xunit.v3
- NDPC — https://ndpc.gov.ng/download/nigeria-data-protection-act-2023
