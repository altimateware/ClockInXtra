# 05 — Security configuration

What protects each part of the system, where it is configured, and what remains for the organisation to decide. The threat model behind these controls is `docs/architecture/02-threat-model.md`.

---

## 1. Controls and where they are set

### Transport (§22, §39)

| Control | Where |
|---|---|
| HTTPS only. The mobile app refuses any non-`https` API address in a release build | App build (`CLOCKINXTRA_API_BASE_URL`); `usesCleartextTraffic=false` in the Android manifest |
| HSTS on both applications outside Development | Code |
| TLS certificates from the organisation's PKI on the proxies, IIS sites and SQL Server (`Encrypt=True`, certificate validated) | Operations (OPEN-21) |
| Certificate pinning: **not implemented**. §39 forbids adding it without a rotation and recovery design, and OPEN-24 is undecided | — |

### Mobile request integrity (§37, §40)

| Control | Setting |
|---|---|
| Every attendance request is signed with a hardware-bound P-256 key (RFC 9421 message signatures) and carries a SHA-256 content digest | Code |
| Signature age limit | `Security.SignatureSkewSeconds` = 120 |
| Replay: each signature nonce accepted once, across all nodes (database unique key) | Code; nonces purged after `Retention.RequestNonceHours` = 2 |
| Idempotency keys on clock-in/clock-out; retries of the same request cannot create a second record | Code |
| Registration challenge lifetime | `Security.ChallengeLifetimeSeconds` = 300 |
| Device registration needs hardware attestation and an administrator's approval; one active device per employee | `Security.RequireHardwareAttestationAndroid` / `…Ios` = true, `Security.DeviceRegistrationRequiresApproval` = true, `Attestation:*` on the API (`02-iis.md` §3) |
| Proxies whose forwarded headers are trusted: listed IPs only, never a wildcard | `Api:KnownProxies` |

### Employee authentication (§12, §13, §24)

| Control | Setting |
|---|---|
| Password (PBKDF2-HMAC-SHA512, 220,000 iterations) plus a six-digit TOTP code at clock-in and registration | Code |
| Each TOTP time step usable once | Code (`usp_MfaCredential_TryConsumeTimeStep`) |
| Lockout | `Security.MobileLockoutThreshold` = 5 failures, `Security.MobileLockoutMinutes` = 15 |
| One error code for a wrong password, wrong code or replayed code, so the API cannot be used to test passwords | `Security.CollapseCredentialErrorCodes` = true. The precise reason is kept in the security ledger |

### Administration portal (§16, §41)

| Control | Setting |
|---|---|
| Password + TOTP required | `Security.RequireAdministratorMfa` = true |
| Lockout | `Security.AdministratorLockoutThreshold` = 5, `Security.AdministratorLockoutMinutes` = 15 |
| Cookie: `HttpOnly`, `Secure`, `SameSite=Strict`, 30 minutes sliding | Code |
| Session revalidated on every request against the account's security stamp. A password change, deactivation, role change or **sign-out** ends every session of that account, including copies of the cookie | Code |
| Anti-forgery tokens on every form post | Code |
| Permission-based authorization; administrators cannot act on themselves, grant what they do not hold, or remove the last administrator manager | Code and stored procedures |
| New and reset passwords are generated, shown once, and must be changed at first sign-in | Code |
| Break-glass recovery of a locked-out administrator is a console command, not a portal feature. It runs under the operator's Windows identity against the `recovery` schema, which every application account is **denied**; it replaces both factors, ends sessions and is audited with the operator's login | `recovery.usp_Administrator_RecoverAccess`; `LeastPrivilegeTests`, portal tests |

### Response and request hygiene (§22, §34, §43)

| Control | Where |
|---|---|
| Request bodies over 64 KB refused with 413 before being read | `RequestSizeLimitMiddleware` |
| Rate limits: attendance 10/min, registration 5/min, reads 60/min — counted per **verified device** for signed requests, per client address for unsigned ones (registration challenge, startup location check, configuration). Per process: the database lockout is the authoritative brute-force control (TD-10). Raise `RegistrationPerMinute` for a rollout day when a whole office registers from one NAT address | `Api:RateLimits` |
| Security headers: `nosniff`, `X-Frame-Options: DENY`, restrictive CSP, `Referrer-Policy`, `Permissions-Policy`, `Cache-Control: no-store` (API) — applied to error responses too | Code |
| Errors: a code, a message and a correlation id. Never stack traces, SQL or internal names | Code |
| OpenAPI document served in Development only | Code |

### Data (§26, §47, §48)

| Control | Where |
|---|---|
| Database access only through stored procedures; each application's account can execute its own schema and nothing else, and cannot read tables | `database/security/10_security_users_grants.sql` (tested by `LeastPrivilegeTests`) |
| TOTP secrets encrypted with ASP.NET Core Data Protection; the key ring shared by API and portal, stored on an ACL'd share, **encrypted with a certificate**. Hosts refuse to start without this outside Development | `DataProtection:*` (`02-iis.md` §4) |
| No raw coordinates in reports or logs; distance and accuracy only | Code and procedures |
| Contact details are recorded in the audit log as *changed*, never by value, because the audit log can never erase them | `usp_MobileUser_Update` |
| Secrets are not in source control. `.gitignore` excludes environment settings, keystores and certificates | Repository |

### Audit (§32)

| Control | Where |
|---|---|
| Audit log and security events are append-only ledger tables | Database (DB-08) |
| Ledger digests exported to write-once storage and verified; exit code 2 on tampering | `ops/Export-LedgerDigest.ps1`, `ops/Test-LedgerIntegrity.ps1` (`01-sql-server.md` §6) |
| Every administrative change, sign-in, sign-out and refusal is recorded with a correlation id | Code and procedures |

## 2. Security review findings (Phases 19–24)

Found by testing against the real database and real deployment conditions rather than development defaults, and fixed:

| Finding | Consequence if shipped | Fix |
|---|---|---|
| The validation-failure report matched reason codes the server never writes, and listed successful sign-ins as failures | Location and device failure reports near-empty; misleading evidence | Classification by event type (`ufn_Report_ValidationFailureCategory`), with tests |
| "Missing clock-out" compared against the UTC date | Wrong flags for a business outside UTC | Business-local date from `Attendance.BusinessTimeZoneId` |
| Administrator sign-out was not audited and left copies of the session cookie valid | §32 gap; stolen cookie usable after sign-out | `usp_Administrator_RecordLogout` rotates the security stamp |
| Employee email and phone written by value into the append-only audit log | Personal data that can never be erased | Recorded as changed-flags only |
| Audit and failure reports needed `VIEW LEDGER CONTENT`, which the portal account lacked | Both pages broken in production (development connects as owner) | Grant in the security script; `LeastPrivilegeTests` |
| The security script failed when the instance collation differed from the database's | Deployment stops (error 468) on common installations | `COLLATE DATABASE_DEFAULT` |
| Ledger digests were specified but no tooling existed | Tamper evidence unverifiable | `job.usp_Maintenance_*LedgerDigest/VerifyLedger` and `ops/` scripts |
| Key ring optional and unencrypted | Cross-node MFA failures; TOTP secrets readable from the share | Startup enforcement with certificate protection |
| **Rate limiting never applied**: the limiter was registered before routing, so no endpoint policy was ever found | No throttling at all on the internet-facing API | Moved after routing; `RateLimitTests` observes the 429s |
| Rate limits counted per client address | An office behind one NAT address would share a single 10/min attendance quota | Counted per verified device for signed requests |
| Unsigned endpoints accepted ~30 MB bodies | Cheap denial of service | 64 KB limit |
| Error responses lost their security headers and correlation id | Weaker error responses; harder support | Headers applied on response start |
| **A request that never verifies never met a limiter.** Signature verification runs before the rate limiter (so signed requests count per device, not per shared office address), and every refusal writes a row to the append-only security ledger, which is never purged | An unauthenticated caller could grow the ledger without bound from one address, cheaply, and bury real security events under the noise | A per-address budget of failed signatures, checked before the device lookup: past it, refusals cost a 429 and nothing else. One row records that the budget was spent, so the trail shows where individual recording stopped. `Api:Abuse:SignatureFailuresPerAddressPerMinute`, default 60; `SignatureFailureBudgetTests` |

## 3. Residual risks and open decisions

These are known and accepted for now, or wait on a business decision (§68):

- **GPS can be spoofed** (§65). Mocked locations are rejected where the platform reports them (`Location.RejectMockedLocations`), and hardware attestation stops modified apps, but a rooted phone with a hidden location spoofer cannot be fully excluded. OPEN-26 (spoofing detection beyond this).
- **5 m accuracy threshold** (`Location.MaxAcceptedAccuracyMeters`) is unset pending OPEN-25; the failure report measures how often accuracy is the reason for refusals.
- **No per-IP rate limit on the portal sign-in.** Per-account lockout and the mandatory TOTP code make password spraying ineffective, and the portal is internal. Adding one would require the portal to trust a proxy's forwarded address.
- **Revocation of Android attestation keys** is checked only when the operator provisions Google's revocation list (`02-iis.md` §3).
- **iOS is not deployable** until its native integrity and App Attest code exist (`04-ios.md`).
- **Retention** (DEC-07): attendance data is kept indefinitely by business decision, and the audit ledger is never purged. Whether that satisfies the NDPA's storage-limitation principle is for Legal to confirm (OPEN-41).
- **Application-level payload encryption** beyond TLS is not implemented; OPEN-23. Requests are signed, not encrypted, at the application layer.
