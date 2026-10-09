# 06 — Implementation Status

| Item | Value |
|---|---|
| Document | ClockInXtra — Implementation status and verification record |
| Version | 0.2 |
| Date | 2026-09-19 (phases 1–12: 2026-09-12) |
| Verified against | SQL Server 2022 Developer 16.0.1200.5 (local instance), .NET SDK 10.0.300 |

This is the running record of what exists, what has actually been **executed
and verified**, and what is still outstanding. Claims here are backed by a
command that ran, not by inspection.

---

## 1. Phase status (`Claude.md` §66)

| # | Phase | Status |
|---|---|---|
| 1 | Requirements clarification and assumptions register | **Complete** — `01-requirements-register.md` |
| 2 | Threat model | **Complete** — `02-threat-model.md` |
| 3 | Solution architecture | **Complete** — `03-solution-architecture.md` |
| 4 | Database architecture | **Complete** — `04-database-architecture.md` |
| 5 | SQL schema | **Complete and deployed** |
| 6 | Stored procedures | **Complete** — 75 objects (74 procedures, 1 function); files, deploy script and database agree |
| 7 | Domain model | **Substantially complete** — coordinates, geodesic distance, location policy, result codes; 59 unit tests |
| 8 | Application layer | **Complete for the mobile surface** — 7 use cases, 16 ports, 1 shared service; 61 unit tests |
| 9 | Infrastructure / Dapper | **Complete** — every application port has an implementation; persistence proven against real SQL Server, attestation proven against synthetic chains. One registration point wires it all up |
| 10 | API security | **Complete** — RFC 9421 signature verification, nonce replay, content digest, rate limiting, correlation ids, security headers, error model, health probes |
| 11 | API endpoints | **Complete** — all eight mobile endpoints at the specified paths, versioned, verified end to end against a real database |
| 12 | Administration portal | **Complete** — setup, sign-in/out, forced password change, settings, employees, authenticator enrolment, office locations, device approval, administrators and roles, reports and audit. Attendance corrections enabled with second-person approval (DEC-08), requested by Attendance Administrators and approved by Super Administrators (DEC-10) |
| 13 | Flutter core infrastructure | **Complete** — API client with request signing, secure storage, environment rules, DI |
| 14 | Flutter location validation | **Complete** — startup gate (§8.1) |
| 15 | Flutter device security | **Complete on Android** — integrity channel, hardware key with attestation. **iOS native code absent** (CON-06, `docs/deployment/04-ios.md`) |
| 16 | Flutter user status | **Complete** — server is the source of truth |
| 17–18 | Flutter clock-in / clock-out | **Complete** — verified end to end on the Android emulator against the API and database (Phase 18) |
| 19 | Reporting | **Complete** — daily attendance with user/department/office/status/exception filters; validation-failure report; business-local missing clock-out |
| 20 | Audit | **Complete** — logout audited, contact details minimised, ledger digest export and verification |
| 21 | Automated testing | **Complete** — concurrency races and least-privilege tests against SQL Server; Flutter on-device integration tests |
| 22 | Deployment | **Documented** — `docs/deployment/01`–`04`. IIS steps not yet executed on a server farm |
| 23 | Security hardening | **Complete** — findings and fixes in `docs/deployment/05-security-configuration.md` §2 |
| 24 | Documentation | **Complete** — root `README.md`, `mobile/README.md`, deployment and security guides, this record |

### Verified test totals (2026-09-19)

Every figure is from a run on that date (`dotnet test --project <dir>`, `sqlcmd`, `flutter test`).

| Suite | Passing | Notes |
|---|---|---|
| Solution build | clean | Warnings as errors; no vulnerable packages (`dotnet list package --vulnerable --include-transitive`) |
| Domain | 59 | |
| Application | 75 | |
| Infrastructure | 161 | Includes repository integration tests against SQL Server, attestation (with a golden sample from a real emulator), key-ring configuration |
| API | 57 | Signed requests against the hosted API, request size limit, rate limiting (including per-device counting and the failed-signature budget), readiness checks. Also verified under starvation — see T-4 |
| Admin portal | 95 | Real signed-in accounts with real TOTP; reports; sign-out ending all sessions; health probes |
| Database | 18 | Five concurrency races; least-privilege checks applying the real security script |
| Automatic deployment | 5 (within Infrastructure) | Creates a throwaway database from nothing and drops it again; includes two hosts deploying at the same moment |
| SQL smoke suite | 194 | `database/tests/smoke_attendance.sql`; restores every setting exactly when it finishes |
| Flutter unit and widget | 85 | |
| Flutter on-device integration | 6 | Android emulator: Keystore, hardware key and signing, integrity channel, full clock-in/out flow |
| Capacity harness | not a test | `tests/Attendance.LoadTest`, run on demand: clocks N virtual employees in through the real pipeline and reports throughput and latency. Measured about 20–25 clock-ins/second on a development workstation (`docs/operations/02-capacity.md`) |

Several of the newer tests were checked by **sabotage**: the fixed code was temporarily reverted, and the test was confirmed to fail before the fix was restored. This was done for the business-date comparison, audit privacy, the ledger grant, the clock-in race (all three guards removed), per-device rate limiting, the manager-remains rule, the failed-signature budget, and the rule that keeps every seeded role grantable.

Every suite that reaches SQL Server is additionally verified **under CPU starvation**, because the API suite had been failing in bulk on a loaded machine while the code under test was sound. Measured 2026-09-27 on 12 cores saturated by 36 high-priority busy loops:

| Suite | Idle | Starved | Slowdown | Result |
|---|---|---|---|---|
| API | 1.7 s | 278 s | ~160x | 57/57 |
| Database | 1.1 s | 169 s | ~150x | 18/18 |
| Infrastructure | 3.6 s | 118 s | ~33x | 156/156 |

Toolchain finding T-4 records what had to change and why none of it weakens an assertion. The incidents that prompted the work were roughly 25-fold slowdowns, so these runs are an order of magnitude harsher than the conditions being guarded against.

**The portal suite has a further sensitivity that T-4 does not cure**, recorded as a known consequence below. Starved to roughly a 250-fold slowdown, 27 of its 95 tests failed because a sign-in took longer than the authenticator code it was carrying stays valid. Making the tolerance a setting (TD-17) and widening it to 6 steps in the test host reduced that to **4 of 95**, measured 2026-09-27 over a 12,167-second run: three still fail to sign in, and one aborted at what was then a five-minute request timeout. That timeout is now ten minutes — twice the worst request the run measured — which should leave three; it has not been re-measured, because only another full starved run would show it. In that run a single sign-in POST took **306 seconds** — longer than the 180 seconds a 6-step window covers, and longer than the 300 seconds the bounded maximum of 10 steps would cover. **A widened window cannot win a race against unbounded slowdown**, which is the point TD-17 makes about not absorbing latency in the authenticator window. Closing the remaining four would mean giving the portal's test host a controlled clock, as `TotpVerifier` already allows, rather than loosening anything further.

### Phase 8 — the use cases that exist

| Use case | Handler | Notes |
|---|---|---|
| Clock-in | `ClockInHandler` | Lockout → location → credentials → time step → idempotency → transaction |
| Clock-out | `ClockOutHandler` | No password or code, per ASM-04; residual risk RR-06 |
| Device registration | `DeviceRegistrationHandler` | Challenge issue and register; proof of possession and attestation |
| Device status | `DeviceStatusHandler` | Reachable by a device that is not yet active |
| User status | `UserStatusHandler` | Server is the source of truth (§11) |
| Location validation | `ValidateLocationHandler` | Startup gate; minimal disclosure (OPEN-44) |
| Mobile configuration | `GetMobileConfigurationHandler` | Database allow-list; no second filter |

**The authentication sequence exists once.** Clock-in and device registration both
verify a password and an authenticator code, and the database's registration
procedure requires its caller to have done so in a specific order. That sequence
lives in `EmployeeAuthenticator` rather than being written out in both handlers,
because two copies of a security ordering drift, and the copy that drifts is the
one nobody re-reads. The lockout check is a separate call on that service, because
clock-in must evaluate location *between* the lockout check and the password —
cheap before expensive, so flooding the endpoint cannot force PBKDF2 work.

The unit tests assert the ordering itself, not only the returned codes: that a
locked account never reaches password verification, that location is evaluated
before hashing, that a wrong password and a wrong code produce the identical
collapsed code, that the time step is consumed before the attendance transaction,
and that an unreadable TOTP secret reports an internal error rather than invalid
credentials. They run against the real `EmployeeAuthenticator` over faked ports,
so the sequence under test is the production one.

The integration tests run inside a transaction that is always rolled back, which
also exercises decision DB-13 continuously: a procedure that regressed to a bare
`ROLLBACK` would destroy the test's transaction and fail immediately.

---

## 2. What is deployed and verified

Rebuilt from scripts and verified on 2026-09-12:

| Object | Count |
|---|---|
| Tables (`core` + `audit`) | 21 |
| Append-only ledger tables | 2 (`audit.AuditLog`, `audit.SecurityEvent`) |
| Stored procedures | 53 |
| Foreign keys | 32 |
| Check constraints | 54 |
| Filtered indexes | 5 (including `UX_Device_ActiveUser`, which enforces DEC-04) |

**Integration tests: 70 of 70 passing** (`database/tests/smoke_attendance.sql`).

**Rebuilt from scratch, not patched into shape.** The database was dropped and
recreated from the scripts alone, and the suite was run against that fresh
database. A check also compares every `.sql` file under `database/programmability`
against the `:r` lines in `deploy/01_run_all.sql`, because a procedure that exists
on disk but is missing from the runner would work on a developer machine —
deployed by hand — and be absent from a real installation. All 50 files are
listed.

### Phase 9 — the persistence chain, proven

Every application port now has a Dapper implementation, and each is exercised
against the real procedures rather than a faked connection. That distinction
matters more here than anywhere else in the codebase: a repository that passes
`@Subject` where the procedure declares `@SubjectType` **compiles cleanly** and
throws the first time an employee tries to clock in. Only execution finds it.

| Port | Implementation | Proven by |
|---|---|---|
| `IDeviceRepository` | `DeviceRepository` | Challenge issue, status, signature material, revocation via result code, DEC-04 refusal, consumed-challenge replay refusal, last-seen |
| `IAttendancePolicyProvider` | `AttendancePolicyProvider` | Parses all 12 settings; unparseable value falls back to the restrictive one |
| `IOfficeLocationRepository` | `OfficeLocationRepository` | Active office returned with radius; disabled office excluded |
| `IIdempotencyStore` | `IdempotencyStore` | All four dispositions in one sequence |
| `ISecurityEventRecorder` | `SecurityEventRecorder` | Row lands in the append-only ledger |
| `IAuthenticationAttemptRepository` | `AuthenticationAttemptRepository` | Counts to the threshold, locks, and reset genuinely clears |
| `IMfaCredentialRepository` | `MfaCredentialRepository` | Time step consumed once, refused twice; unenrolled employee distinguished |
| `IMobileConfigurationRepository` | `MobileConfigurationRepository` | Allow-list holds: no security parameter appears |

### Phases 10–11 — the signing profile, proven against an independent client

The API tests host the real application and drive it with a client that builds
the RFC 9421 signature base **from the specification, not from the server's own
builder**. That is the whole point: the server constructs a byte sequence from
the request and verifies a signature over it, and the client constructs what it
believes is the same sequence. A disagreement of one byte — an extra newline, a
differently spelled authority — makes every genuine request fail with a symptom
indistinguishable from a wrong key. Nothing short of an independent
implementation catches it.

A correctly signed request is accepted. Each way of getting it wrong is refused:
a replayed nonce (401), a stale or future `created` (401), a body swapped after
the digest was computed (400), another application's tag (401), a signature
covering too few components (401), a signature made with a different key (401),
a revoked device (403), and a body claiming an employee the device is not bound
to (403).

**`AllowUnsignedRequest` means optional, not ignored.** An endpoint reachable
before registration still verifies a signature when one is offered. The first
implementation skipped verification entirely for those endpoints, which silently
disabled the body-digest check on the startup location check and meant a
registered device could never be recognised there — so it could never be told
which office it matched, which is the one thing that endpoint returns to a known
device.

### Phase 12 — the portal's session model

Verified against the running application: the sign-in page renders anonymously
with an anti-forgery token and an empty password field; an unauthenticated
request to any other page redirects to sign-in carrying its return path; a POST
without an anti-forgery token is refused with 400; and the session cookie is
issued `secure; samesite=strict; httponly`.

**The portal refuses to operate over plain HTTP.** The anti-forgery cookie is
`SecurePolicy = Always`, so the system declines to issue a CSRF token on an
insecure channel and the sign-in page fails outright. That is the intended
behaviour rather than a defect — but it means TLS is a deployment prerequisite,
not a hardening step to apply later, and a smoke test over HTTP will look broken.

**A session is revalidated on every request**, not merely decrypted. The security
stamp in the cookie is compared with the stored one, and a password change, a
disabled account or an altered role assignment rotates that stamp — so those
changes end live sessions at once instead of leaving a signed cookie usable until
it expires (threat TH-35). Permissions are refreshed from the same read, so a
withdrawn permission stops applying to the request in flight.

Authorization is one policy per permission code, registered from a single list,
with a fallback policy requiring authentication. A portal defaulting to anonymous
would be one missing attribute away from a public page.

### Phase 12 — the privileged path, covered

The portal tests sign in as a real administrator with a real authenticator. The
account's TOTP secret is protected with the same Data Protection key ring the
hosted portal decrypts with, and codes are generated the way a phone generates
them — so the second factor is exercised rather than switched off for
convenience.

What that proves, rather than asserts:

- A correct password **without** a code is not a sign-in, because
  `Security.RequireAdministratorMfa` is true.
- An unknown account and a wrong password produce the identical message.
- **A code cannot be used twice.** The first sign-in succeeds and the second with
  the same code is refused — which is `admin.usp_Administrator_TryConsumeTimeStep`
  doing the job it was added for.
- A permission genuinely gates a page: an account with no role reaches the
  dashboard and is redirected away from device approval and settings.
- **Rotating the security stamp ends a live session**, as does disabling the
  account. The cookie is still valid and correctly signed in both cases, and stops
  working anyway (threat TH-35).

### Authenticator enrolment — the last link in the employee chain

An employee can clock in only with all three of a password, an **active**
authenticator and an approved device. The employee list therefore leads with who
is *not* ready and names the missing piece, because "cannot clock in" is the only
question anybody is really asking.

Three properties the tests hold in place:

- **Enrolment does not activate.** The credential is created pending and becomes
  usable only once a code from the employee's own phone is accepted. Activating on
  creation would leave anyone whose secret was never successfully scanned stuck at
  a door the next morning, with nobody able to say why.
- **The secret is shown exactly once**, on a `no-store` response, and is protected
  before the page renders. There is no way to display it again; if it is not
  scanned, enrolment is repeated and a fresh secret issued.
- **The permission genuinely gates it.** An administrator without `Mfa.Enrol` is
  redirected away.

`admin.usp_MfaCredential_GetForActivation` was added to make activation possible
— verifying the code requires reading the secret back, and nothing exposed it.
It is **restricted to credentials still awaiting activation**: an active
authenticator's secret is never returned, so a portal account cannot read the
second factor of an employee already clocking in. Replacing an active
authenticator remains possible, but only through the enrolment path that revokes
the old one and audits both facts — that route is visible, whereas quietly reading
an existing secret would not be.

### Office locations — configuration answered with evidence

The office screen shows, beside each configured radius, the accuracy employees'
devices **actually reported there**: median, worst, and the greatest distance
accepted. That turns CON-01 from a warning in a document into a number an
administrator can act on. If the median accuracy at a site is worse than its
radius, half of all honest attempts are being asked for precision the hardware
does not have — and the screen says so in those terms.

Five metres remains the default because that is what the requirement specifies.
The observed figures are what tell somebody whether it is achievable at a given
building.

Coordinate ranges are enforced in the form **and** in the stored procedure. An
impossible latitude would otherwise reach the geodesic calculation and quietly
produce a meaningless distance rather than an error.

A change here reaches the mobile API within about thirty seconds, because the API
caches active offices in its own process. That is stated on the screen. It is
deliberately unlike device revocation, which is never cached.

### The complete path from empty database to a working clock-in

Four separate links in this chain were missing at the start of Phase 12, each of
which alone made the product unusable. All four are closed and the whole path is
now exercised by tests:

1. `Attendance.Admin --create-first-administrator` creates the only account that
   can exist before any other (defect 12).
2. That administrator signs in with a password **and** an authenticator code,
   where the code cannot be reused (defect 11).
3. The settings screen records the thirteen outstanding business decisions.
4. The employee screen enrols an authenticator and activates it against a real
   code from the employee's own phone.
5. The locations screen establishes at least one active office.
6. The device screen approves the employee's registration, revoking any previous
   device in the same transaction (DEC-04).

### Reporting and audit

The attendance report shows **distance and accuracy side by side**, because one
without the other says very little: two metres measured with fifty metres of
uncertainty is not the same evidence as two metres measured with three. Missing
clock-outs can be isolated, since those are the rows somebody must act on — an
employee left without closing their record and only a correction completes it.

The audit view shows the action trail and the security event timeline **in one
window**. An investigation needs both: a successful sign-in is only interesting
beside the forty failures that preceded it. It is also the only place where the
collapsed credential errors are distinguishable — the mobile API answers wrong
user, wrong password and wrong code identically so that an internet-facing
endpoint cannot confirm a correct password, and the precise reason survives here
and nowhere else.

Both tables are append-only ledgers, and the view surfaces SQL Server's own
ledger commit time and committing principal rather than the application's — so a
row cannot be back-dated by whoever wrote it, and a write by a principal that is
not the application is visible as such.

A test signs in and then finds that very sign-in in the trail, which proves the
audit is being written rather than merely being queryable.

### Platform attestation (CON-02, DEC-05)

Both verifiers run entirely on our own servers against operator-provisioned root
certificates. **No attendance decision depends on Google or Apple being
reachable** — only registration involves them, and only through material the
device itself carries.

| Check | Android key attestation | Apple App Attest |
|---|---|---|
| Chain | X.509 to a provisioned Google root, custom trust only | X.509 to a provisioned Apple root |
| Challenge binding | `attestationChallenge` in extension `1.3.6.1.4.1.11129.2.1.17` | nonce = SHA-256(authData ‖ SHA-256(challenge)) in extension `1.2.840.113635.100.8.2` |
| Key binding | Attested key equals the key being registered | Same, plus key id = SHA-256(attested key) |
| Hardware | Security level ≥ TrustedEnvironment (StrongBox optional) | Secure Enclave implied by a valid attestation |
| Device integrity | Bootloader locked and verified boot state `Verified` | — |
| Application identity | Package name **and** signing-certificate digest | `SHA-256("<teamId>.<bundleId>")` against the relying-party hash |
| Freshness | — | Counter must be zero |
| Environment | — | Development attestations refused unless deliberately enabled |
| Revocation | Locally provisioned status list (OPEN-45) | — |

Every verifier refuses to operate when unconfigured. A verifier without a root
certificate or an expected package name cannot tell this organisation's
application from any other, so "not configured" produces a rejected registration
rather than an accepted one — the failure is loud instead of silent.

**Malformed input is an expected outcome, not an exception.** Every parse path
returns a rejection rather than throwing, and the tests drive arbitrary bytes at
both verifiers at several lengths. An unhandled exception here would be a denial
of service reachable by anyone who can call the registration endpoint — the same
class of defect as recorded defect 7.

Caching is asymmetric on purpose. Office locations and the policy snapshot are
cached for 30 seconds; **device status is never cached**, because revocation has
to take effect immediately (§18). An office moved a moment ago and still accepted
for a few more seconds is an administrative inconvenience; a revoked handset that
still records attendance is a security failure.

### Geodesic model, measured rather than assumed

TD-12 chooses a spherical Haversine calculation on the IUGG mean radius
(6,371,008.8 m) over an ellipsoidal formula. That choice was checked against
SQL Server's ellipsoidal WGS 84 `geography::STDistance` at the reference office
latitude (6.465°N), by projecting each §51 boundary distance with the spherical
model and asking SQL Server how far the resulting point actually is:

| Projected distance | Ellipsoidal, northward | Ellipsoidal, eastward |
|---|---|---|
| 1 m | 0.9945 m | 1.0012 m |
| 3 m | 2.9836 m | 3.0035 m |
| 4.9 m | 4.8733 m | 4.9057 m |
| **5.0 m** | **4.9727 m** | **5.0058 m** |
| 5.1 m | 5.0722 m | 5.1059 m |
| 10 m | 9.9454 m | 10.0116 m |

The spherical model runs about **0.55% short north–south** and **0.12% long
east–west** at this latitude. At the 5 metre radius that is roughly **3 cm** —
two orders of magnitude below the accuracy a phone reports, which is metres at
best. The model choice is therefore immaterial to the outcome, and an
ellipsoidal formula would add complexity without changing a single decision.

What this measurement does *not* do is make a 5 metre radius workable: the
limiting factor is GPS accuracy, not the arithmetic (CON-01).

Behaviour proven by those tests, rather than asserted:

- A duplicate clock-in on the same attendance day is refused (1050), and the guarantee rests on a unique index plus held key-range locks, not on application checks.
- Clock-out closes the record once; a second attempt is refused (1055); clock-out without clock-in is refused (1051).
- A revoked device cannot clock in (1022); a device bound to another employee cannot be used (1023); a disabled office is refused (1043).
- With a mandatory setting unset, attendance refuses with `ATTENDANCE_NOT_CONFIGURED` (1053) instead of assuming a value — the behaviour §15 and §68 require.
- A replayed signature nonce is refused (1030) by the insert itself, with no read-then-write race.
- Idempotency distinguishes first attempt, in-flight retry (1034), completed replay returning the original result (1033), and key reuse for a different request (1032).
- A TOTP time step cannot be used twice (1013), including the code that proved enrolment.
- Lockout counts across calls and locks at the threshold (1011), and reset genuinely clears it.
- **Approving a replacement device revokes the previous one in the same transaction, leaving exactly one active device** — the whole of DEC-04, verified end to end.
- A stale `RowVersion` is refused (1071) rather than silently overwriting another administrator's change.
- An invalid time zone (1076) and a value outside `AllowedValues` (1001) are refused at the point of entry.

---

## 3. Defects found by executing the work

Recorded because each one would have reached production as a silent failure.

| # | Defect | How it would have failed | Fix |
|---|---|---|---|
| 1 | `sqlcmd` defaults `QUOTED_IDENTIFIER` to OFF, and filtered indexes cannot be created without it | Deployment aborted midway, leaving `core.MobileUser` present but without its indexes — a half-built schema that re-running would skip | Every script now sets `ANSI_NULLS` and `QUOTED_IDENTIFIER` itself instead of depending on how it is invoked |
| 2 | `LIKE` character ranges are evaluated in **collation order, not code-point order**. Under `Latin1_General_100_CI_AS` the tilde sorts before the letters, so the "printable ASCII" constraint `'%[^ -~]%'` matched ordinary capitals | `CK_Device_DeviceModel_Printable` rejected the plain value `TESTDEVICE`, and would have rejected **every real device model in production** | The comparison is forced to `Latin1_General_100_BIN2`, so the range means code points 0x20–0x7E. Tests now assert both directions: a realistic model name is accepted, a control character is refused |
| 3 | In T-SQL, `SELECT @var = col` that matches **no rows leaves the variable unchanged** — and several procedures assigned straight into OUTPUT parameters | `usp_AuthenticationAttempt_Check` reported a cleared account as still locked. Worse, `usp_ApplicationSetting_GetAttendanceRules` could return a stale value for an absent setting, which would let attendance proceed under a rule nobody configured — defeating the central "unset means unset" safeguard | All seven affected procedures now initialise every OUTPUT parameter before use |
| 4 | An `EXEC` argument must be a constant or a variable; an expression or subquery is a parse error | Three administration procedures passed a `FOR JSON` subquery as `@Details`, and would have failed to compile at deployment | Values are built into variables first |
| 5 | **CVE-2026-49451 (high, CVSS 7.5)** — the ASP.NET Core Web API template pulls in `Microsoft.OpenApi` 2.0.0 transitively via `Microsoft.AspNetCore.OpenApi` 10.0.8. A malformed OpenAPI document with circular schema references causes uncontrolled recursion and a stack overflow that terminates the process | A vulnerable dependency shipped in the internet-facing API from the first commit, arriving through a Microsoft template rather than a choice anyone made (threat TH-33) | Central package management with **transitive pinning** overrides it to 2.12.2. Note the advisory names 2.7.5 as patched, but nuget.org marks 2.7.5 itself as carrying a high-severity vulnerability, so the advisory's own "fixed" version is not a safe target. The 3.x line is not usable: ASP.NET Core 10 is built against the 2.x API. `NuGetAudit` now runs on every restore at `low` level in `all` mode, with warnings as errors |
| 6 | **`ROLLBACK TRANSACTION` in T-SQL rolls back every nesting level and sets `@@TRANCOUNT` to 0.** Eighteen procedures opened a transaction and rolled back on ordinary business rejections — "already clocked in", "device revoked", "privilege escalation refused" | Called inside a caller's transaction, a routine refusal destroyed **the caller's** work and SQL Server raised error 266 on return. Latent today because Dapper calls each procedure standalone, but it would have surfaced the first time two procedures were composed — or the first time a test wrapped one in a transaction, which is exactly how it was found | Every writing procedure now captures `@@TRANCOUNT` first, issues `BEGIN TRANSACTION` only when outermost and `SAVE TRANSACTION` otherwise, and rolls back to the savepoint (decision DB-13). A `sys.sql_modules` query audits that no procedure still uses the bare pattern, and the smoke suite carries a permanent regression case asserting the caller's transaction survives a rejection |

Defects 2 and 3 were found only because the procedures were executed against real rows. Neither is visible by reading the code.

| 8 | **No stored procedure exposed the security policy settings.** The API needs lockout thresholds, the signature skew window, the attestation flags and the challenge lifetime in order to enforce them, but `usp_ApplicationSetting_GetMobileRuntime` deliberately returns only mobile-visible rows — and those settings are excluded from it precisely because a handset must not learn them | The application layer had a port (`IAttendancePolicyProvider`) with nothing behind it. The only ways to implement it were inline SQL, which §4 prohibits, or widening the mobile procedure, which would have published the lockout threshold and the replay window to every phone in the organisation | Added `mobile.usp_ApplicationSetting_GetSecurityPolicy`, a server-side allow-list of exactly 12 keys, registered in the runner and deployed. The allow-list is explicit rather than "everything in the Security category", so a setting filed there in future is not silently exported |

| 13 | **The deployment runner was not re-runnable, contrary to its own header.** Five deferred foreign keys in `schema/02_tables_identity.sql` were guarded by `IF OBJECT_ID(N'FK_...', N'F') IS NULL`. A constraint belongs to the schema of its table — `core` — but an **unqualified `OBJECT_ID` resolves against the caller's default schema**, `dbo` for the deployment account. Every one of those guards was therefore always NULL | A second deployment failed with "There is already an object named 'FK_Administrator_Administrator_CreatedBy'", half way through the identity tables. Nobody had hit it because nobody re-ran the runner against a populated database; it surfaced the moment deployment started running on **every host start**, and it would have surfaced in production on the first upgrade | The guards are schema-qualified. Both the runner and automatic deployment are now verified re-runnable: `01_run_all.sql` executed twice by hand in succession, and 465 tests pass with every host deploying at startup |
| 14 | **The deployer could not open the database it had just created**, failing with "Cannot open database ... the login failed" naming a database that existed. The fast path probes the application database *before* it is known to exist; that probe fails, and with SqlClient's **pool blocking period** at its default the failure is cached against the pool and replayed for the next five seconds (TD-16) | A host would create the database and then fail to start — and succeed on a later restart, which is the worst shape of bug to diagnose. **The tests passed throughout**, because `TestEnvironment` sets `Pool Blocking Period=NeverBlock` so a slow login cannot fail a whole class: the fixture's tolerance hid a defect in the code it was testing | The deployer builds its own connection strings with pooling **off**, so its correctness no longer depends on an option the caller supplies. `DatabaseDeployerTests` now restores the production pool defaults explicitly, so the case stays covered. Found by running the two real host processes, which is what the unit tests could not show |
| 12 | **Nothing could create the first administrator.** The seed establishes roles and permissions but no account, and `usp_Administrator_Create` requires `@CreatedByAdministratorId` — on a fresh deployment there is nobody to name | **The portal could not be signed into at all.** Since device approval and the settings editor live behind that sign-in, a correctly deployed system would have had no way to approve a device or decide any of the thirteen outstanding business settings — so no employee could ever have clocked in. Every test passed throughout, because none of them signs in | Added `admin.usp_Administrator_Bootstrap`, which **refuses when any administrator row exists** — it creates the first, never an additional one, so it cannot become a standing back door. Driven by `Attendance.Admin --create-first-administrator`, which reads the password from the console rather than an argument (arguments reach shell history and the process list) and sets `MustChangePassword` |

| 11 | **No procedure consumed an administrator's authenticator time step.** `core.Administrator` carries `MfaLastAcceptedTimeStep`, but nothing wrote it — the employee equivalent lives in `core.MfaCredential` and has its own procedure, and the administrator case had been missed | The portal could verify a code and never spend it, leaving one code usable for its whole window — about 90 seconds with the ±1 step tolerance. On the accounts that can revoke devices, approve replacements and rewrite attendance rules | Added `admin.usp_Administrator_TryConsumeTimeStep`, conditional-UPDATE in the same shape as the employee procedure so two concurrent sign-ins cannot both spend one code. Deployed; 51 procedures |

| 9 | **The composition root was incomplete.** `ISecretProtector` and `INonceStore` had no registration, and ASP.NET Core Data Protection was never configured | The API failed at startup on service validation. No test caught it: every unit test supplies its own fake, so the container was never exercised until the application actually ran. Had validation been off, the failure would have moved to the first clock-in instead | Both registered, with Data Protection configured under a fixed shared application name (TD-06). Two hosts with different application names derive different keys, and the symptom would be every clock-in failing to read an intact secret |
| 10 | **Health probes were refused as unsigned.** They were mapped with `AllowAnonymous()`, which speaks to ASP.NET Core *authorization* — a mechanism this API does not use. Authentication here is the request signature, and its middleware reads its own marker | Every probe returned 401. A load balancer would have marked every node unhealthy and taken the entire service out of rotation — a total outage caused by a security control working exactly as written | The probes carry `AllowUnsignedRequestAttribute` as endpoint metadata, with a comment recording why `AllowAnonymous` is not sufficient. Found by starting the application and watching the readiness poll fail |

| 7 | An **off-curve public key** makes `ECDsa.Create` throw `PlatformNotSupportedException` on Windows CNG, not the `CryptographicException` the code caught | The device public key is attacker-chosen data arriving at registration. An unhandled exception on the signature path means a **500 from the internet-facing API on demand** — trivially reachable, and the kind of thing that reads as a server fault rather than a rejected request | Both exception types are caught and reported as a failed verification. Found by the off-curve test case, which is why it exists |

#### Phases 13–18 (mobile, and the first contact with real device output)

| # | Defect | How it would have failed | Fix |
|---|---|---|---|
| 13 | The Android attestation parser expected `attestationApplicationId` [709] **implicitly** tagged and in the hardware-enforced list. Real KeyMint output tags every field explicitly and places [709] in the software-enforced list | **Every real Android registration refused.** The synthetic tests passed because they were built from the same misreading | Parser follows the real structure; root of trust still accepted from hardware-enforced only; a 353-byte golden sample captured from the emulator is now a permanent test |
| 14 | The revocation list threw on an empty configured path | Every registration answered 500 while revocation was unconfigured | "Not configured" is checked first and recorded on the registration |
| 15 | The bootstrapped administrator had no way to enrol an authenticator, and MFA was required | The first administrator could never sign in | Account and pending authenticator are created atomically by the bootstrap |
| 16 | A pending device was refused (403) by the status endpoint it needed to learn it had been approved | Devices stuck on "waiting for approval" forever | `[AllowInactiveDevice]`: status reachable while pending or revoked, signature still fully verified |
| 17 | The "a manager must remain" check had a NULL-logic error | Could remove the last administrator able to manage administrators | Rewritten; six smoke cases fail when the bug is reintroduced |

#### Phases 19–23

Ten further defects — among them rate limiting that never applied, reports broken for the production database account, an unencrypted optional key ring, personal data written into the permanent audit ledger, and a security script that failed on a common instance collation — are recorded with their consequences and fixes in `docs/deployment/05-security-configuration.md` §2.

### Toolchain findings

| # | Finding | Resolution |
|---|---|---|
| T-1 | The `dotnet new xunit` template produces a **VSTest**-era project (`xunit` 2.9.3, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `coverlet.collector`). TD-14 specifies xunit.v3 4.0.0, which ships **Microsoft.Testing.Platform 2.x** — and MTP 2.x dropped the legacy VSTest path on the .NET 10 SDK, so `dotnet test` fails before a single test runs | `global.json` opts into MTP mode (`"test": { "runner": "Microsoft.Testing.Platform" }`), and the VSTest-only packages were removed from all six test projects. Coverage will come from `Microsoft.Testing.Extensions.CodeCoverage` rather than coverlet. Each test project builds as a self-hosting executable, so the assemblies can also be run directly — a dependable fallback while the `dotnet test` integration for MTP 2.x settles down |
| T-2 | .NET 10 creates solutions in the new **`.slnx`** XML format, so `dotnet sln add` against `ClockInXtra.sln` fails | Scripts and documentation reference `ClockInXtra.slnx` |
| T-4 | Under heavy CPU starvation the API test project failed **in bulk** — 21 of 57 in one observed run, 22 when reproduced — while the code under test was sound. Saturating every core exposed four waiting limits, none of them anything a test asserts: SQL Server's 15-second connect default (an observed login took 37 s); SqlClient's pool **blocking period**, which after one failed open fails every subsequent open on that pool instantly with the same cached error, taking down a whole class with an identical message; `HttpClient`'s 100-second default, exceeded by the first request because it also builds the hosted API; and Dapper's 30-second command default. Two latent sensitivities compounded it: the one-minute rate-limit and failure-budget windows could roll over mid-test, granting a second allowance; and the failed-signature budget test counted *every* row in the append-only ledger, so it depended on what other classes, running in parallel, happened to refuse at the same moment | `tests/Shared/TestEnvironment.cs` holds one definition of the database the suite runs against and of the limits it tolerates — 120-second connect and command timeouts, `Pool Blocking Period=NeverBlock`, a 10-minute request timeout — and is linked into all four test projects that open a connection or host an application (Api, Admin, Database, Infrastructure), replacing five separate copies of the bare connection string. The limits are applied to `CLOCKINXTRA_TEST_CONNECTION` as well, so a build agent's own instance gets them. `TestHost` (API) and `PortalHost` (portal) hand the **hosted application** the same connection settings, because its own database calls were failing the same way and surfacing as 500s that look nothing like the cause. The two one-minute windows are lengthened to an hour **for tests only**: `Api:RateLimits:WindowSeconds` and `Api:Abuse:WindowSeconds` were added to `Program.cs` with a 60-second default that deployments keep. The budget test stamps every request with its own correlation id, counts only its own rows, and asserts the **exact** count plus a single exhaustion marker — stricter than the range it asserted before. Only waiting limits were lengthened; no assertion was weakened and no retry was added. A production connection string is a separate decision, with the opposite trade-off: **TD-16** |
| T-3 | .NET 10 prunes framework-provided packages and raises **NU1510** for redundant references. `System.Formats.Cbor` is now in the shared framework | The reference was removed. Apple App Attest CBOR parsing and Android attestation ASN.1 parsing both use in-box types, so attestation needs no NuGet packages at all |

---

## 4. Known consequences carried forward

- **Audit rows cannot be purged.** `audit.AuditLog` and `audit.SecurityEvent` are append-only ledger tables, so test runs leave rows behind permanently and a test database must be rebuilt rather than cleaned. If Legal requires audit deletion (OPEN-14, OPEN-41), the design must move to period-scoped audit tables before production (CON-11).
- **13 settings remain unset**, which is intended. They are business decisions, and the system refuses the affected operations until they are made. They are listed by every deployment run and by `admin.usp_ApplicationSetting_GetAll`.
- **iOS is not deployable** (CON-06, OPEN-42). The Flutter code is shared, but the native integrity and App Attest channels need Swift written and run on a Mac; until then the app refuses to start on iOS by design (`docs/deployment/04-ios.md`).
- **Attestation against real hardware.** The Android parser has now processed real emulator output (defect 13, golden sample), which confirmed the structure. An emulator's key is software-backed, so the full acceptance path — hardware security level, verified boot, Google root — still needs one run on a physical Android phone before production. Apple App Attest has never received a real object.
- **The startup location check is reachable before registration** (§8.1), so it is the one mobile endpoint an unauthenticated caller can reach. Behaviour is minimal disclosure (OPEN-44). It is rate limited per client address — which, until Phase 23, it was not in fact (see the security guide).
- **A sign-in has to finish inside an authenticator code's lifetime, and that is a capacity limit, not a test artefact.** Password verification is PBKDF2 at 220,000 iterations (TD-05): about 0.2 seconds on an idle machine, but **49 seconds inside the action** when starved to roughly a 250-fold slowdown. By then the code was older than the 30-second tolerance, the sign-in was refused, the next request went out anonymous, and 27 of the portal's 95 tests failed on a 302 they expected to be a 200. The same arithmetic holds in production: **a server so loaded that a sign-in takes tens of seconds will reject real users' codes too.** The tolerance is now the setting `Security.TotpStepTolerance` (TD-17) and the portal's test host widens it to 6 steps in process only, without touching the database. That took the starved suite from 27 failures to 4; the residue is not a window that is too narrow but a sign-in that took 306 seconds, which no permitted tolerance covers. Raising it on a real deployment would be treating the symptom — the number to watch is sign-in latency, and the authenticator window is not where that should be absorbed. The tests are robust at the roughly 25-fold slowdowns that prompted this work, and the honest ceiling above which the portal's sign-in tests stop being meaningful is a sign-in latency of about three minutes.
- **IIS deployment** is documented but has not been executed on a server; the first test-environment deployment verifies `docs/deployment/02-iis.md`.

---

## 5. How to rebuild and verify from scratch

```bat
cd database
sqlcmd -S localhost -E -C -b -I -i "deploy\00_create_database.sql"
sqlcmd -S localhost -d ClockInXtra -E -C -b -I -i "deploy\01_run_all.sql"
sqlcmd -S localhost -d ClockInXtra -E -C -b -I -i "tests\smoke_attendance.sql"
```

Least-privilege grants are applied separately, after the application logins exist:

```bat
sqlcmd -S localhost -d ClockInXtra -E -C -b -I -i "security\10_security_users_grants.sql"
```

`-I` is belt and braces; the scripts set the option themselves.

---

## 6. Definition of Done (`Claude.md` §69)

| Criterion | Status |
|---|---|
| Mobile app runs on Android | **Met** — emulator, end to end against the API (Phase 18); on-device integration tests |
| Mobile app runs on iOS | **Not met** — native iOS code needs a Mac (CON-06). The app fails closed on iOS |
| Validates location before normal operation | Met |
| Rooted/jailbroken devices blocked as far as the platform allows | Met on Android (client signals + server attestation); iOS pending |
| User ID securely stored | Met (Keystore / Keychain via flutter_secure_storage; tested on device) |
| User status from the server | Met |
| Clock-in, clock-out | Met |
| MFA/TOTP | Met, with one-time use of each time step (race-tested) |
| Device validation | Met (attestation, approval, revocation, one active device) |
| Location validation; 5 m geodesic calculation | Met; boundary tests at 0–10 m. Whether 5 m is achievable in practice is OPEN-25 |
| Attendance rules configurable | Met; unset rules refuse attendance rather than guess |
| Administrative authentication, user, device and office management | Met |
| Reporting | Met (daily attendance with filters, exceptions, validation failures, audit) |
| Audit logging | Met, with ledger digest verification |
| API security; TLS enforced | Met |
| SQL Server via Dapper and stored procedures only; no EF; no inline SQL in application code | Met. Inline SQL exists only in test fixtures and operations scripts, never in repositories, controllers or services |
| All required procedures exist and are complete | Met (75 objects) |
| Transactions, duplicates, concurrency | Met; proven by concurrent races against SQL Server |
| Tests exist | Met (see §1) |
| Swagger/OpenAPI available | Met in Development; deliberately not served in production |
| IIS, SQL deployment and security configuration documented | Met (`docs/deployment/`); IIS not yet exercised on a server |
| No cloud dependency | Met. Google's attestation revocation list is an optional, operator-provisioned file |
| Known platform limitations documented | Met (`docs/deployment/03-android.md` §8, `04-ios.md`, `05-security-configuration.md` §3) |
| No fabricated technology presented as fact | Claims in this record are backed by runs; unexecuted steps are marked as such |

**Not production-ready until:** the iOS native code exists (or iOS is formally descoped), a physical Android device has registered against a production-configured API, the IIS guide has been executed in a test environment, and the remaining business decisions in `01-requirements-register.md` marked OPEN — above all the office radius in the light of real readings (CON-01) — have been made, and Legal has confirmed NDPA compliance (OPEN-41). The time zone, attendance times, retention, corrections, accuracy threshold and who may correct attendance were decided on 2026-09-19 (DEC-06 to DEC-10).
