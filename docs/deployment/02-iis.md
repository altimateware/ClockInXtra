# 02 — Deploying the API and the administration portal (IIS)

Both applications are ASP.NET Core on .NET 10, hosted in IIS with the ASP.NET Core Module. The topology is set out in `docs/architecture/03-solution-architecture.md` §15: at least two API nodes behind reverse proxies in a DMZ (DEC-03), and the portal on the internal network only.

**Status of these instructions:** they follow Microsoft's documented IIS hosting model, and the configuration they describe is what the code enforces at startup. They have **not** been executed against a production IIS farm in this project; the applications have been run and tested with Kestrel on the development machine. Treat the first deployment to a test environment as the verification of this guide.

---

## 1. Prerequisites on every application server

1. Windows Server with the IIS role (Web Server, with *Windows Authentication* not required — both applications do their own authentication).
2. The **ASP.NET Core 10.0 Hosting Bundle** (the .NET runtime plus the ASP.NET Core Module) from Microsoft's .NET download page. Install it **after** IIS, then restart IIS (`net stop was /y` then `net start w3svc`).
3. A TLS certificate for each site from the organisation's PKI (OPEN-21) in `LocalMachine\My`.
4. The **Data Protection key-encryption certificate** (§4 below) in `LocalMachine\My`, with its private key readable by both application pool identities.
5. Network access: 1433 (or the fixed instance port) to SQL Server; SMB to the key-ring share.

## 2. Build

On the build agent, from the repository root:

```bash
dotnet publish src/Attendance.Api   -c Release -o artifacts/api
dotnet publish src/Attendance.Admin -c Release -o artifacts/admin
```

`dotnet publish` generates the `web.config` that configures the ASP.NET Core Module (in-process hosting). Do not add secrets to `appsettings.json` in the artifact; they come from the server (§3).

## 3. Configuration and secrets

Settings are read in this order, later ones winning: `appsettings.json` → `appsettings.{Environment}.json` → environment variables. On a server, supply the environment-specific values as **environment variables on the application pool**, or in an `appsettings.Production.json` that is deployed from the organisation's secret store and ACL'd to the pool identity and administrators. Neither is committed to source control (§47). Environment variable names use `__` for nesting, e.g. `SqlServer__ConnectionString`.

Set on both application pools:

| Setting | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production`. Anything but `Development` switches on the startup checks below |
| `SqlServer__ConnectionString` | `Server=sql01.corp.local;Database=ClockInXtra;Integrated Security=True;Encrypt=True;Pool Blocking Period=NeverBlock` (gMSA), or a SQL login from the secret store. Never `TrustServerCertificate=True` in production. The blocking period is explained below |
| `DataProtection__KeyRingPath` | `\\keys01\clockinxtra-keyring` — **the same path for the API and the portal** |
| `DataProtection__CertificateThumbprint` | Thumbprint of the key-encryption certificate |
| `AllowedHosts` | The site's own host name(s), e.g. `attendance.contoso.com`. The shipped value `*` accepts any `Host` header |

API only:

| Setting | Value |
|---|---|
| `Api__KnownProxies__0`, `__1`, … | IP address of each reverse proxy. Forwarded headers are honoured from these addresses only, and never from a wildcard: the request signature covers the host the phone saw, so a spoofable `X-Forwarded-Host` would let a signature verify against something the phone never signed |
| `Api__RateLimits__*` | Per-minute limits for attendance, registration and read endpoints (defaults 10 / 5 / 60). `Api__RateLimits__WindowSeconds` and `Api__Abuse__WindowSeconds` set the window those limits are counted over; **leave both at 60**, which is what "per minute" means. They exist because the test suite lengthens them so a slow run cannot cross a window boundary mid-test |
| `Attestation__Android__RootCertificatePemPath` | Google's hardware attestation root certificate(s), downloaded and verified out of band (see `03-android.md` §5) |
| `Attestation__Android__ExpectedPackageName` | `com.contoso.clockinxtra` (or the organisation's own package name) |
| `Attestation__Android__ExpectedSigningCertificateDigests__0` | SHA-256 of the release signing certificate (see `03-android.md` §3) |
| `Attestation__Android__RevocationStatusListPath` | Local copy of Google's attestation revocation list, refreshed on a schedule. Until set, revocation is not checked and each accepted registration records that |

Without the attestation settings every registration is refused with `ANDROID_ATTESTATION_NOT_CONFIGURED`. That refusal is deliberate: an unconfigured check must not pass.

### Why the connection string says `Pool Blocking Period=NeverBlock`

SqlClient's default for this key is `Auto`, which Microsoft documents as "Blocking period OFF for Azure SQL servers, but ON for all other SQL servers" — so an on-premises instance gets the blocking period. With it on, one failed login makes every subsequent connection open on that pool fail **immediately, with the first error replayed from cache**, for five seconds; each further failure doubles the period, up to one minute.

The consequence is a longer outage than the one that actually happened. An availability-group failover or a SQL Server service restart takes a few seconds, but the pool goes on refusing for up to a minute after the database is serving again — and every log entry blames the original error, so the incident looks like it is still in progress. `NeverBlock` lets the first request after recovery reach the database.

The cost is paid in the other direction: during a genuine outage, each request waits out `Connect Timeout` instead of failing at once, and occupies a request thread while it does. **Keep `Connect Timeout` short** — the 15-second default or less — and do not copy the long timeouts the test suite uses, which exist for a different purpose (see TD-16 and T-4 in the architecture documents). If failing fast through a long outage matters more to operations than recovering quickly from a short one, set `AlwaysBlock` instead; it is a deployment decision either way.

### What refuses to start, and why

Outside Development, a host **will not start** if:

- `SqlServer:ConnectionString` is missing.
- `DataProtection:KeyRingPath` is missing: each process would otherwise keep its own keys, and the API could not read authenticators enrolled in the portal.
- `DataProtection:CertificateThumbprint` is missing, or the certificate is not installed, or its private key is not readable: the key ring would otherwise be written to the share **unencrypted**.

The error names the setting or thumbprint. Look in the Windows Event Log (Application, source *IIS AspNetCore Module V2*) and in the application log (§6).

## 4. The shared Data Protection key ring

The key ring encrypts every TOTP secret. Lose it and every employee and administrator must re-enrol their authenticator; expose it and every secret can be decrypted.

1. **Create the share** on a server other than the application servers: `\\keys01\clockinxtra-keyring`. Grant *Modify* to the API and portal pool identities only, and *Full control* to the administrators responsible for it. Nobody else.
2. **Create the key-encryption certificate.** It does not need to chain to a trusted root; it must have an exportable RSA private key. From the organisation's PKI, or for example:

   ```powershell
   New-SelfSignedCertificate -Subject "CN=ClockInXtra Data Protection" `
       -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 3072 `
       -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5) -KeyUsage KeyEncipherment,DataEncipherment
   ```

   Export it with its private key (PFX, strong password from the secret store), import it into `LocalMachine\My` on **every** API and portal server, and grant each application pool identity read access to the private key (*Manage Private Keys* in the Certificates console).
3. **Back up** the PFX and the share together, off-server. They are needed together to restore; a database backup alone is not enough (TH-42).
4. **Rotating the certificate:** install the new certificate everywhere, set `DataProtection__CertificateThumbprint` to it and add the old one to `DataProtection__PreviousCertificateThumbprints__0`. Remove the old thumbprint only once no key it protected remains in the ring.
5. **Never delete keys from the ring.** A deleted key makes every secret it protected permanently undecryptable (architecture §12).

## 5. IIS sites and application pools

For each application (API on the DMZ-facing nodes, portal on the internal node):

1. **Application pool:** *.NET CLR version* = **No Managed Code**; pipeline *Integrated*; identity = the gMSA (`CONTOSO\gmsa-cix-api$` / `gmsa-cix-admin$`) or a dedicated low-privilege account. One pool per application: they are separate database principals (DB-01).
2. **Site:** physical path = the published folder; HTTPS binding on 443 with the site certificate; **no HTTP binding** on the API. The portal may keep an HTTP binding only to redirect.
3. **Folder permissions:** the pool identity needs *Read & execute* on the site folder, and *Modify* on its `logs` folder only.
4. Recycle the pool after changing environment variables.

The portal must **not** be published through the DMZ proxy. Only `/api/v1/mobile/*` is exposed externally; the health endpoints are for the load balancer on the internal side and are not published (architecture §7, §15).

## 6. Logging and health

- Logs: `logs\attendance-api-*.log` and `logs\attendance-admin-*.log` under each site folder, one file per day, 31 days kept (change in `Serilog` settings; point a sink at the organisation's own log collector if one exists — never a cloud service, §61). Logs never contain passwords, authenticator codes, signatures or coordinates (§33).
- Health: `GET /health/live` (process up) and `GET /health/ready` on both applications. Point the load balancer's probe at `/health/ready`: it answers **503** when the database or the key ring is unusable (take the node out), and **200** otherwise. The API's report also shows `attendance-configuration`, which is **Degraded** — still 200, deliberately, since every node shares the settings — while business settings are unset or no office is active. The body is JSON naming each check; alert on `Degraded` as well as on 503.

## 7. First administrator

The portal has no default account. After the database and the portal are deployed, on the portal server, as someone entitled to hold the first Super Administrator account:

```bash
cd <portal folder>
set ASPNETCORE_ENVIRONMENT=Production
dotnet Attendance.Admin.dll --create-first-administrator
```

It prompts for a user name, display name, optional email and the password (not echoed, asked twice), generates an authenticator secret, and prints its `otpauth://` URI **once** for enrolment in an authenticator app. It refuses if any administrator already exists. The account must change its password at first sign-in.

### If every administrator is locked out

There is still no default account and no back door in the portal. Recovery is a
separate console command, run on the server by a **database administrator**:

```bash
dotnet Attendance.Admin.dll --reset-administrator <user name> [--reactivate]
```

It connects with the Windows identity of whoever runs it, never the portal's
configured login, and calls a procedure in the `recovery` schema that every
application account is denied. It replaces the password and the authenticator,
ends existing sessions, and is audited. Full procedure: `docs/operations/01-runbook.md` §5.10.

## 8. After deployment

1. Sign in to the portal and set the business settings the database deployment listed as undecided — above all the **business time zone** (OPEN-5). Attendance endpoints answer `ATTENDANCE_NOT_CONFIGURED` until the required settings exist, by design.
2. Create office locations (with the radius the business approved; §9, CON-01).
3. Schedule the ledger digest export and verification (`01-sql-server.md` §6).
4. Build and distribute the mobile app pointing at the public API URL (`03-android.md`).
