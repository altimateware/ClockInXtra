# 01 — Operations runbook

| Item | Value |
|---|---|
| Document | ClockInXtra — daily operations and incident playbooks |
| Audience | Whoever runs this system day to day: service desk, IT operations, the attendance administrator |
| Companion documents | `docs/deployment/` (how it was installed), `docs/architecture/05-result-codes.md` (what every code means) |
| Date | 2026-09-19 |

This runbook assumes the system is already deployed as `docs/deployment/` describes.
It answers the questions an operator actually gets asked, in the order they arrive
in the morning.

---

## 1. What this system is, in one paragraph

Employees clock in and out from a registered phone. The phone proves who it is with
a signature; the employee proves who they are with a password and a six-digit
authenticator code; the phone's reported position must be inside an approved
office's radius. The server decides the time — never the phone. Administrators
work in the portal: employees, devices, office locations, settings, reports,
corrections and the audit trail. Attendance and audit records are held in SQL
Server; audit records are append-only.

**The one-line mental model for incidents:** the phone is not trusted, the server
decides, and everything that was refused is written down. When someone says "it
didn't work", the answer is in **Reports → Failures**.

---

## 2. Daily — about five minutes

| Check | Where | What good looks like |
|---|---|---|
| Both applications healthy | `GET /health/ready` on the API and the portal | HTTP 200. A 503 means that node is unusable — see §5.7 |
| No Degraded health checks | the same JSON body | Every check `Healthy`. `attendance-configuration` going `Degraded` means a business setting was blanked or no office is active (§5.8) |
| Ledger verified | the nightly `Test-LedgerIntegrity.ps1` job | Exit code 0. **Exit code 2 is a security incident — §5.9** |
| Overnight maintenance ran | SQL Server Agent job history | Every maintenance job succeeded |
| Refusals look ordinary | Portal → **Reports → Failures**, last 24 hours | A handful of location and credential refusals is normal. §3 says what "not ordinary" looks like |
| Missing clock-outs | Portal → **Reports → Attendance**, exception filter *missing clock-out* | Expect a few. Each one needs a correction (§5.3) or it stays open for good |
| Devices waiting | Portal → **Devices** | Approve or reject the same day; an employee whose device is unapproved cannot clock in |

---

## 3. Reading the Failures report

Every refusal is classified into one of four categories, and the report shows a
summary by reason before the individual events. Read the summary first — the
pattern matters far more than any single row.

| Category | What it covers |
|---|---|
| **Location** | The position was refused: too far, too vague, or reported as mocked |
| **Device** | The phone was refused: not registered, awaiting approval, revoked, or attestation rejected |
| **Identity** | The person was refused: wrong password, missing or replayed authenticator code, locked out |
| **Integrity** | The request itself was refused: bad signature, replayed nonce, clock skew, altered body. **These are the ones that mean something is wrong beyond a forgotten password** |

What to do about the common reasons:

| Reason shown | Likely meaning | Action |
|---|---|---|
| `LOCATION_NOT_ALLOWED`, many employees, one office | The office radius is too tight for the building, not an attack | Collect a week of it, then change that office's radius (§5.5). This is the expected consequence of a 5 m radius (CON-01) |
| `LOCATION_ACCURACY_INSUFFICIENT`, one office | Poor GPS reception: indoors, basements, tall buildings | Clock in from a different spot, or raise `Location.MaxAcceptedAccuracyMeters` with business approval |
| `LOCATION_SOURCE_UNTRUSTED`, one employee | Their phone reported a mocked position | A conduct matter, not a fault. The event names the device and the employee |
| `INVALID_PASSWORD` or `MFA_CODE_MISSING` repeatedly, one employee | Forgotten password, wrong authenticator, or phone clock drift | §5.4 |
| `OtpReplayed` | The same authenticator code was submitted twice — usually a double tap | Ask them to wait for a fresh code. Persistent cases with no user action behind them are worth escalating |
| `INVALID_PASSWORD` or `UnknownUser` across many employees | Credential stuffing | Escalate to security. Lockout already applies: 5 attempts, then 15 minutes |
| `DeviceRevoked`, `DeviceNotApproved`, `DeviceNotRegistered` | Ordinary device lifecycle | Approve the device, or explain the revocation |
| `ATTESTATION_SECURITY_LEVEL_INSUFFICIENT`, `ATTESTATION_EXTENSION_MALFORMED` | Rooted device, modified app, or unprovisioned attestation material | §5.6 |
| `ANDROID_ATTESTATION_NOT_CONFIGURED` | The server has no attestation roots or app identity provisioned | Configuration, not the phone: `docs/deployment/03-android.md` §5 |
| `SIGNATURE_CLOCK_SKEW` | The phone's clock is more than two minutes out | Switch on automatic date and time |
| `SIGNATURE_NONCE_REPLAYED`, `CONTENT_DIGEST_MISMATCH`, `SIGNATURE_INVALID` | A request was replayed or altered in flight | **Escalate to security.** These do not happen by accident |
| `SIGNATURE_FAILURE_BUDGET_EXHAUSTED` | One address produced so many unverifiable requests that individual refusals stopped being recorded, to keep the ledger bounded | **Escalate to security.** Take the address from the event and block it at the network. Refusal counts below this marker are no longer complete |
| `NO_ACTIVE_OFFICE_LOCATIONS` | Every office is disabled | Nobody can clock in anywhere. Fix in **Locations**, at once |
| `RATE_LIMITED` responses | One device retrying hard — the limit is 10 attendance calls a minute per device | Usually a stuck app: ask them to close and reopen it |

---

## 4. Weekly, monthly, and once a quarter

**Weekly**

- Skim **Reports → Audit** for administrative activity: role grants, device revocations, setting changes, corrections. Everyone who changed authority should be someone you expected.
- Check the digest store is still filling and still write-protected — §5.9 depends on it.
- Check disk space on the log folders and the database volume.

**Monthly**

- Review administrator accounts: leavers removed, roles still justified. Each administrator should hold the least they need.
- Review office locations: disabled sites still disabled, radii still what the business agreed.
- Review **Settings** for anything left unconfirmed.

**Quarterly**

- **Restore drill.** Restore the database *and* the Data Protection key ring with its certificate into a test environment, then sign in. A database restored without the key ring leaves every authenticator secret undecryptable and every employee needing re-enrolment. This is the most expensive mistake available, and only a drill finds it.
- Check certificate expiry: the TLS certificate the phones trust, and the key-ring protection certificate. Both are silent until the day they fail.
- Re-read the residual risks in `docs/deployment/05-security-configuration.md` §3 and confirm they are still accepted.

---

## 5. Playbooks

### 5.1 "I can't clock in"

Ask what the app said, then look the employee up in **Reports → Failures** for the
last hour. The message the app shows comes straight from the server and maps to
one cause:

| What the employee sees | Cause | Fix |
|---|---|---|
| "You do not appear to be at an approved office location." | The position is not inside any active office's radius | Check they are at the right site and the office is enabled; then consider the radius (§5.5) |
| "Your location could not be determined accurately enough…" | The GPS reading is too vague — worse than 20 m | Move outdoors or near a window and retry |
| "The reported location could not be trusted." | The phone reported a mocked or simulated position | Not a technical fault. §3 |
| "That action is outside the permitted hours." | Clock-in runs 00:00–08:30 Lagos time and late clock-in is **not** permitted; clock-out is allowed from a minute after their own clock-in until 23:59 (DEC-06) | A correction after the fact (§5.3), or a business decision to move the window |
| "You are already clocked in for today." | A record exists for today | Nothing to fix; they clock out later |
| "There is no open attendance record to close." | They never clocked in today, or already clocked out | If they worked and the clock-in is genuinely missing, that needs a correction (§5.3) |
| Sign-in failed | Wrong password, wrong or reused authenticator code, or a phone clock that is off. The app cannot say which, on purpose — it would tell an attacker which half they guessed right | §5.4 |
| "This device could not be verified." | Attestation rejected | §5.6 |
| "This device's clock is too far from the server's…" | Phone clock more than two minutes out | Switch on automatic date and time |
| Device not approved, or revoked | Device lifecycle | **Devices** page, §5.2 |
| "Attendance is not yet configured…" | A mandatory setting is unset — the system refuses rather than guessing | **Settings**, and §5.8 |
| Update required | The app is below `Mobile.MinimumAppVersion` | Distribute the current app |

### 5.2 New, lost or stolen phone

1. **Lost or stolen:** revoke the device in **Devices**, immediately. A revoked device can do nothing from that moment. Record who asked and why; the revocation is audited.
2. **Replacement:** the employee installs the app and registers. The registration appears as pending in **Devices**.
3. Approving the replacement **automatically revokes the old device** (DEC-04): one employee, one active phone.
4. The authenticator enrolment belongs to the employee, not the phone. If their authenticator app went with the phone, reset MFA on the employee's page — that produces a new enrolment, which they confirm with a valid code.

### 5.3 A missing clock-out, or a wrong time

A record not closed by midnight stays open and is reported as a missing clock-out.
Only a correction closes it.

1. An **Attendance Administrator** opens **Reports → Attendance**, finds the row, clicks **Correct…**, enters the time in Lagos time and gives a reason.
2. A **Super Administrator** opens **Corrections** and approves or rejects it. Rejecting requires a reason.
3. The requester can never approve their own request; the database refuses it (DEC-10). If only one administrator is available, the correction waits — that is the control working, not a fault.
4. The corrected time must fall on the record's own day and cannot be in the future. The original times are kept permanently, and both the request and the decision are in the audit ledger.

### 5.4 An employee is locked out, or their codes are rejected

- **Lockout** is 5 failed attempts, then 15 minutes. Waiting is a valid fix.
- **Password:** an administrator resets it on the employee's page. There is no self-service (ASM-07).
- **Authenticator codes always rejected:** almost always the phone's clock. Switch on automatic date and time. Codes are accepted within a small window, and each code works only once — retrying the same code fails on purpose.
- If the employee has no enrolment at all, enrol them (`Mfa.Enrol`). The secret is shown **once**.

### 5.5 Changing an office radius, or adding an office

1. **Locations → Edit**, change the radius, save. It applies immediately: no restart, no deployment.
2. Before widening, take the evidence from **Reports → Failures**. A radius is a business decision about how precisely presence must be proven, and widening it accepts a larger area as "at the office".
3. Latitude and longitude must come from a reliable source for the building — not from a phone reading taken in the lobby.

### 5.6 Attestation rejections

`ATTESTATION_REJECTED` means the device could not prove it is a genuine, unmodified
phone running your app. Ordinary causes: a rooted phone, a sideloaded or repackaged
app, an emulator. Configuration causes: the attestation roots or the expected app
identity are not provisioned on the server (`docs/deployment/03-android.md` §5).
If *every* registration fails this way, suspect configuration; if one employee's
does, suspect the phone.

### 5.7 `/health/ready` returns 503

The node cannot serve. The JSON body names the failing check.

- `database` — SQL Server unreachable, credentials wrong, or the database offline. If the server was rebooted while BitLocker was pending, the database can be left in RECOVERY_PENDING: take it offline and online again.
- `key-ring` — the key-ring path is unreachable, or the protection certificate is missing, expired, or unreadable by the application pool identity. Sessions and protected data depend on it.

Take the node out of the load balancer (the probe does this for you if it points at
`/health/ready`), fix the dependency, put it back. Nothing needs reinstalling.

### 5.8 `attendance-configuration` is Degraded

The node is up and serving — HTTP 200 — but attendance will be refused with
`ATTENDANCE_NOT_CONFIGURED`, because a mandatory business setting is unset or no
office location is active. This is deliberate: the system refuses rather than
inventing a business rule. Open **Settings**; the health body names what is missing.

### 5.9 Ledger verification failed — security incident

`Test-LedgerIntegrity.ps1` exiting **2** means the audit ledger no longer matches a
digest exported earlier. Either someone with database rights altered or removed
audit history, or the database was restored to a point that contradicts the digests.

**Do not attempt a repair. Do not re-export digests.**

1. Preserve everything: the database, its backups, and the digest store. Take a fresh backup and set it aside.
2. Escalate to security leadership immediately. This is the one alert meaning the system's own record of what happened is in question.
3. Capture which digest failed and when it was exported — the script reports both.
4. Treat attendance data for the affected period as unreliable until the cause is known.

Exit code **1** is a different thing: verification could not run at all (server
unreachable, no digests present). Fix the job. That is availability, not integrity.

### 5.10 Nobody can sign in to the portal

The last Super Administrator has lost their password or their phone, or has been
deactivated, and there is nobody left who can reset them from inside the portal.

On the portal server, **as a Windows account that is a database administrator**
(db_owner) on the ClockInXtra database:

```bash
cd <portal folder>
set ASPNETCORE_ENVIRONMENT=Production
dotnet Attendance.Admin.dll --reset-administrator <user name>
```

1. It shows which server and database it will change, and under which Windows account, then asks you to type the user name again.
2. You type a **temporary** password twice. It is never shown and never passed on the command line.
3. It prints a new authenticator QR link **once**. Have the administrator scan it there and then.
4. The administrator signs in with the temporary password and the new code, and is made to choose a new password immediately.

What it also does: ends every session the account had open, stops the old
authenticator working, clears its lockout, and writes a permanent audit entry
naming your login and the machine you ran it on. Add `--reactivate` only if the
account was deactivated and restoring it is intended — it will not do that
silently. It does not change roles.

If it answers *"Refused by SQL Server"*, the Windows account you are using is not
a database administrator. That is the control: the portal's own database login is
denied this command outright, so a compromised web server cannot use it.

Afterwards, check **Reports → Audit** for `Administrator.AccessRecovered` and make
sure someone expected it.

### 5.11 Restoring the database

Restore the database **and** the Data Protection key ring with its certificate,
together, to the same point. Then sign in to the portal and clock in from a test
device before calling it done. See `docs/deployment/01-sql-server.md` §7.

---

## 6. Things an operator must never do

- **Never run `database/tests/smoke_attendance.sql` against production.** It creates and deletes test data, and the audit rows it writes stay in the ledger permanently.
- **Never delete or edit rows in `audit.AuditLog` or `audit.SecurityEvent`.** They are append-only by design; the attempt is exactly what integrity verification exists to catch.
- **Never grant the application accounts more database rights than they were deployed with**, and never make them `db_owner`.
- **Never give the SQL Server administrators write access to the digest store.** That separation is the whole point: digests are evidence only if the people who could alter the ledger cannot alter them too.
- **Never put a password, an authenticator code or a signature into a ticket, a log or an email.** If a user sends one, have them change it.
- **Never "fix" attendance with direct SQL.** Corrections exist, need a second person, and are audited. A direct update is indistinguishable from tampering.
- **Never re-point the app at a server whose certificate the phones do not trust.** Registration and attendance both fail closed.

---

## 7. Escalation

| Situation | Who | How fast |
|---|---|---|
| Ledger verification failed (exit 2) | Security leadership | Immediately |
| Credential attack across many employees | Security | Same day |
| Every API node unhealthy | IT operations | Immediately — nobody can clock in |
| Database unavailable | Database administrator | Immediately |
| Key-ring certificate expired or lost | IT operations and security | Immediately; every session and protected secret depends on it |
| An `Administrator.AccessRecovered` entry nobody expected | Security | Immediately: someone with database rights reset an administrator |
| Systematic location refusals at one site | Attendance administrator, then the business owner | Within the week, with evidence |
| A disputed correction | The business owner; the audit trail holds the original values | As the business requires |

---

## 8. Handing over the attendance record as evidence

When attendance or audit records go to HR, an auditor, or a court:

1. Run `Test-LedgerIntegrity.ps1` first and keep its output with the evidence. Records with no verification result are just rows in a table.
2. Export from the portal's reports rather than querying the database by hand.
3. State the business time zone (Lagos, UTC+1) on anything handed over: stored times are UTC, displayed times are local, and the difference is an hour of someone's working day.
4. Say plainly what the system does **not** prove. GPS shows that a phone reported a position, not that a person was inside the building (CON-01). Let the report carry no more weight than it can bear.
