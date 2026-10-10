# 06 — Ubuntu VPS deployment (systemd, nginx, GitHub Actions)

How the API and the administration portal run on an Ubuntu VPS, and exactly what has to exist on the server before `.github/workflows/deploy.yml` can install a release.

This is an alternative to `02-iis.md`, not a replacement. Both are supported; this one exists because the deployment target chosen for this installation is a Linux VPS.

---

## 1. What changes by not being on Windows, and what it costs

These are deviations from decisions recorded elsewhere. None of them is hidden by the workflow.

| Decision | On Windows | Here | Consequence |
|---|---|---|---|
| **§3.2, §46** — IIS on Windows Server | IIS application pools, ASP.NET Core Module | Kestrel under `systemd`, nginx in front for TLS | No application-pool recycling, no Windows event log. Restart and log behaviour are systemd's |
| **§48, `01-sql-server.md`** — gMSA logins (`ALTIMATEWARE\gmsa-cix-api$`) | Windows authentication, no password anywhere | **SQL authentication**, one login per application | A password now exists and must be protected. It lives only in `/etc/clockinxtra/*.env` on the server, never in the repository or in GitHub (§47). Kerberos against AD is possible on Linux but needs a keytab per service and is not covered here |
| **TD-06** — key ring protected by an organisation-PKI certificate | Thumbprint resolved from `LocalMachine\My` | **`DataProtection:CertificatePath`**, a PKCS#12 file | A thumbprint cannot work here: `LocalMachine\My` is a Windows store, and on Linux .NET maps it to a directory it will not write to, so a host would never find the certificate and would refuse to start. `KeyRingOptions` gained `CertificatePath`, `CertificatePassword` and `PreviousCertificatePaths` for this; a thumbprint still works on Windows, and a path wins when both are set. The PFX and its password are now files on disk — **back them up with the key ring**, because losing either forces every authenticator to be re-enrolled |
| **§2.1** — no cloud | — | **GitHub-hosted runners** build and connect in over SSH | Source and SSH deploy credentials pass through GitHub's infrastructure. Database credentials and the key ring do not. If that is unacceptable, install a self-hosted runner and change `runs-on` — nothing else in the workflow has to change |
| **§2.1**, TD-06 — organisation PKI | Internal CA | **Let's Encrypt** for the public TLS certificate | A public CA issues the certificate for the public names. The Data Protection key ring is unaffected and still uses your own certificate |
| **§69** — ledger audit tables | SQL Server 2022 | SQL Server 2022 **for Linux** | Ledger is supported. The instance must still be 2022 or later; the deployment refuses an older one |

**OPEN REQUIREMENT:** whether a single VPS satisfies the availability and disaster-recovery requirements (OPEN-17, OPEN-18, OPEN-19) is not settled by this document. One box hosting the database and both applications has no redundancy, and §46 explicitly warns against assuming one server is enough. Backups are **your** responsibility and are not automated here.

---

## 2. Topology

```text
        Internet
           │ 443
           ▼
      ┌─────────┐   TLS terminates here (Let's Encrypt)
      │  nginx  │
      └────┬────┘
           │ http, loopback only
     ┌─────┴──────┐
     ▼            ▼
127.0.0.1:6200  127.0.0.1:6100
 Attendance.Api  Attendance.Admin
 api.clockinxtra  clockinxtra
   .xwoks.com      .xwoks.com
     └─────┬──────┘
           ▼
   SQL Server 2022 (localhost:1433)
```

Neither application listens on a public interface. nginx is the only thing bound to 443, which is what makes `Admin:KnownProxies` meaningful: the portal trusts `X-Forwarded-*` from `127.0.0.1` and from nothing else.

---

## 3. Server preparation

Run once, as root, on a fresh Ubuntu 22.04 or 24.04 host.

### 3.1 Runtime

```bash
# Microsoft package feed
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O /tmp/ms.deb
dpkg -i /tmp/ms.deb && apt-get update

# ASP.NET Core 10 runtime only — the SDK is not needed, the runner publishes
apt-get install -y aspnetcore-runtime-10.0 nginx rsync curl
```

### 3.2 SQL Server 2022

```bash
curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg
curl -fsSL https://packages.microsoft.com/config/ubuntu/22.04/mssql-server-2022.list \
  > /etc/apt/sources.list.d/mssql-server-2022.list
apt-get update && apt-get install -y mssql-server mssql-tools18
/opt/mssql/bin/mssql-conf setup          # choose Developer or your licensed edition
systemctl enable --now mssql-server
```

SQL Server must **not** be reachable from the internet. On this server that is already the case, verified 2026-10-09:

```text
Status: active
Default: deny (incoming), allow (outgoing)
22/tcp   ALLOW IN   Anywhere
80/tcp   ALLOW IN   Anywhere
443/tcp  ALLOW IN   Anywhere
```

Port 1433 is deliberately absent, and the applications reach the database over loopback. Note that `sqlservr` does bind `0.0.0.0:1433`, so the listener is open on every interface and it is **only** the firewall standing between the database and the internet. Do not add a 1433 rule, and if the default incoming policy is ever relaxed, bind SQL Server to loopback instead:

```bash
ufw status verbose          # confirm 1433 is still absent
```

### 3.2a Drop the empty database and let the deployment create it

**Do this before the first deployment.** On this server, `sys.databases` shows a database `clockinxtra` with **0 tables** and collation `SQL_Latin1_General_CP1_CI_AS`. It was prepared by hand ahead of time, and keeping it would be the wrong baseline for three reasons:

| | It has | It should have |
|---|---|---|
| Collation | `SQL_Latin1_General_CP1_CI_AS` | `Latin1_General_100_CI_AS` |
| `READ_COMMITTED_SNAPSHOT` | off | **on** |
| `ALLOW_SNAPSHOT_ISOLATION` | off | on |
| Compatibility level | instance default | 160 |

The reason it matters is a consequence of how automatic deployment works. `00_create_database.sql` sets all four, and it runs **only when the deployment creates the database** — because it contains `ALTER DATABASE … SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE`, which disconnects every open session and must never fire at the startup of one host among several serving traffic (TD-18). So a database that already exists receives its objects and never its options.

Objects deploy into it perfectly. The difference shows up later, as comparison and blocking behaviour that no test reproduces: `READ_COMMITTED_SNAPSHOT` off means the portal's reports take shared locks and **block attendance writes**, which is the specific problem that setting was chosen to avoid, and a different collation changes string comparison and sort order in a system whose every test and all 194 smoke cases run on the documented one.

While it is empty this costs nothing to fix:

```sql
-- Nothing to lose: 0 tables. Verify that first.
SELECT COUNT(*) FROM clockinxtra.sys.tables;   -- expect 0

DROP DATABASE clockinxtra;
```

The first deployment then creates `ClockInXtra` with all four options correct, because it is the one creating it.

> **If you keep it instead**, deployment still works and the hosts still start. The deployer now reports the difference on every start as a warning naming each one, so the divergence is at least visible in the journal rather than silent. But it will diverge from every environment this system is tested in, and correcting it afterwards means rebuilding the database with data in it.

Note also that the database name is then `ClockInXtra`, matching `Database__Name` and the `Database=` in each connection string. The existing `clockinxtra` differs only in case, which both SQL Server (server collation is case-insensitive here) and the deployer's own check tolerate — but there is no reason to keep the discrepancy.

### 3.3 Application logins

Create one login per application, each with its own password, and give them nothing beyond what the security script grants. Generate the passwords on the server; do not reuse them anywhere.

```sql
CREATE LOGIN clockinxtra_api   WITH PASSWORD = '<generated>', CHECK_POLICY = ON;
CREATE LOGIN clockinxtra_admin WITH PASSWORD = '<generated>', CHECK_POLICY = ON;
CREATE LOGIN clockinxtra_jobs  WITH PASSWORD = '<generated>', CHECK_POLICY = ON;

-- Deployment identity. It needs rights the three above must never hold, and it
-- is used for seconds at a time by --deploy-database.
CREATE LOGIN clockinxtra_deploy WITH PASSWORD = '<generated>', CHECK_POLICY = ON;
ALTER SERVER ROLE dbcreator ADD MEMBER clockinxtra_deploy;
```

After the first deployment has created the database, apply least privilege and grant the deployment account what the ledger tables need.

The security script is **not** in the published output — `dotnet publish` ships assemblies, and the scripts travel inside `Attendance.Infrastructure.dll` as embedded resources, where `sqlcmd` cannot reach them. Get the `database` folder onto the server once, from a checkout:

```bash
git clone --depth 1 https://github.com/altimateware/ClockInXtra.git /var/www/clockinxtra/scripts
cd /var/www/clockinxtra/scripts/database

sqlcmd -S localhost -U sa -C -d ClockInXtra -b -I -i security/10_security_users_grants.sql \
  -v MobileUser="clockinxtra_api" AdminUser="clockinxtra_admin" JobUser="clockinxtra_jobs"
```

Keep that checkout only for the SQL scripts and the smoke suite. The application itself is never run from it.

```sql
USE ClockInXtra;
CREATE USER clockinxtra_deploy FOR LOGIN clockinxtra_deploy;
ALTER ROLE db_owner ADD MEMBER clockinxtra_deploy;   -- DDL across every schema
GRANT ENABLE LEDGER TO clockinxtra_deploy;           -- required by audit.*
```

> `db_owner` for the deployment login is a conscious exception to §48, scoped to an identity that never serves a request. The three application logins stay least-privilege, and `LeastPrivilegeTests` proves it on every CI run.

### 3.4 Service user, directories and the key ring

```bash
# The units already say User=clockinxtra, but the account does not exist yet,
# which is why both services are installed and disabled. Create it first or
# every start fails with status=217/USER.
adduser --system --group --no-create-home --home /var/www/clockinxtra clockinxtra

# The release directories the units' current symlinks will point into, plus the
# key ring and the environment files they read.
mkdir -p /var/www/clockinxtra/api/releases \
         /var/www/clockinxtra/backoffice/releases \
         /var/lib/clockinxtra/keyring \
         /etc/clockinxtra
chown -R clockinxtra:clockinxtra /var/www/clockinxtra /var/lib/clockinxtra
chmod 750 /var/lib/clockinxtra/keyring

# The key ring protection certificate (TD-06). Both hosts must use the SAME
# one, or the portal will enrol authenticators the API cannot read.
# Supply this from your own PKI; a self-signed one is acceptable only for a
# test environment.
chown clockinxtra:clockinxtra /etc/clockinxtra/keyring.pfx
chmod 400 /etc/clockinxtra/keyring.pfx
```

**Back up `/var/lib/clockinxtra/keyring` and `keyring.pfx` together.** Losing either forces every employee to re-enrol their authenticator.

To rotate the certificate later, point `DataProtection__CertificatePath` at the new PFX and list the old one as `DataProtection__PreviousCertificatePaths__0`. Keys protected by the old certificate stay readable; new keys use the new one. Removing the old path before every key has been re-protected makes the authenticator secrets it protected unreadable.

### 3.5 Environment files

Root-owned, readable by the service user, never in the repository.

`/etc/clockinxtra/api.env`:

```ini
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:6200
SqlServer__ConnectionString=Server=localhost,1433;Database=ClockInXtra;User ID=clockinxtra_api;Password=<api password>;Encrypt=True;TrustServerCertificate=True;Pool Blocking Period=NeverBlock
DataProtection__KeyRingPath=/var/lib/clockinxtra/keyring
DataProtection__CertificatePath=/etc/clockinxtra/keyring.pfx
DataProtection__CertificatePassword=<pfx password>
AllowedHosts=api.clockinxtra.xwoks.com
Api__KnownProxies__0=127.0.0.1
Attestation__Android__ExpectedPackageName=com.altimateware.clockinxtra
Attestation__Android__RootCertificatePemPath=/etc/clockinxtra/google-attestation-roots.pem
Attestation__Android__ExpectedSigningCertificateDigests__0=<SHA-256 of your release signing certificate>
Database__AutoDeploy=false
```

`/etc/clockinxtra/backoffice.env`, named by the unit's `EnvironmentFile`:

```ini
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:6100
SqlServer__ConnectionString=Server=localhost,1433;Database=ClockInXtra;User ID=clockinxtra_admin;Password=<admin password>;Encrypt=True;TrustServerCertificate=True;Pool Blocking Period=NeverBlock
DataProtection__KeyRingPath=/var/lib/clockinxtra/keyring
DataProtection__CertificatePath=/etc/clockinxtra/keyring.pfx
DataProtection__CertificatePassword=<pfx password>
AllowedHosts=clockinxtra.xwoks.com
Admin__KnownProxies__0=127.0.0.1
Database__AutoDeploy=false
```

`/etc/clockinxtra/deploy.env` — used **only** by `--deploy-database`:

```ini
ASPNETCORE_ENVIRONMENT=Production
SqlServer__ConnectionString=Server=localhost,1433;Database=ClockInXtra;User ID=clockinxtra_deploy;Password=<deploy password>;Encrypt=True;TrustServerCertificate=True
DataProtection__KeyRingPath=/var/lib/clockinxtra/keyring
DataProtection__CertificatePath=/etc/clockinxtra/keyring.pfx
DataProtection__CertificatePassword=<pfx password>
Database__AutoDeploy=true
Database__Name=ClockInXtra
```

```bash
chmod 640 /etc/clockinxtra/*.env
chown root:clockinxtra /etc/clockinxtra/*.env
```

Note `Database__AutoDeploy=false` for the serving hosts and `true` only for the deployment step. The applications therefore never hold an identity that could create or alter the schema, which is the whole point of TD-18's default.

### 3.6 systemd units

**Both unit files already exist** and are correct apart from one line each. Do not replace them; change the assembly they start.

`ExecStart` currently names `ClockInXtra.Api.dll` and `ClockInXtra.BackOffice.dll`, but this solution builds **`Attendance.Api.dll`** and **`Attendance.Admin.dll`**. The project names were never changed to match, and renaming the assemblies would ripple into `WebApplicationFactory`'s content-root discovery and the pre-compiled Razor views, so the unit files are the safer place to reconcile it:

```bash
sudo sed -i 's#/ClockInXtra\.Api\.dll#/Attendance.Api.dll#' \
  /etc/systemd/system/clockinxtra-api.service
sudo sed -i 's#/ClockInXtra\.BackOffice\.dll#/Attendance.Admin.dll#' \
  /etc/systemd/system/clockinxtra-backoffice.service

sudo systemctl daemon-reload
sudo systemctl enable clockinxtra-api clockinxtra-backoffice
```

The deploy workflow asserts both assemblies were published before it uploads anything, so a future rename fails the job with a clear message instead of leaving systemd restarting a missing file every ten seconds.

Everything else in the existing units is already right: `WorkingDirectory` and `ExecStart` under `/var/www/clockinxtra/{api,backoffice}/current`, `ASPNETCORE_URLS` on 6200 and 6100, `EnvironmentFile=/etc/clockinxtra/{api,backoffice}.env`, `User=clockinxtra`, `Restart=always`, and no `Type=` — which defaults to `simple`, and that is the correct choice (see the note below).

<details>
<summary>Hardening worth adding to both units, optional</summary>

The existing units run without sandboxing. These directives restrict each process to the files it needs, and nothing here requires them:

```ini
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/clockinxtra/keyring /var/www/clockinxtra/api/current/logs
```

`ReadWritePaths` must name the right application's `logs` directory in each unit, and `ProtectSystem=strict` makes everything else read-only — so test a restart after adding them rather than assuming.

</details>

<details>
<summary>For reference: what a unit file looks like from scratch</summary>

`/etc/systemd/system/clockinxtra-api.service`:

```ini
[Unit]
Description=ClockInXtra Attendance API
After=network-online.target mssql-server.service
Wants=network-online.target

[Service]
Type=notify
User=clockinxtra
Group=clockinxtra
WorkingDirectory=/var/www/clockinxtra/api/current
ExecStart=/usr/bin/dotnet /var/www/clockinxtra/api/current/Attendance.Api.dll
EnvironmentFile=/etc/clockinxtra/api.env
Restart=on-failure
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=clockinxtra-api

# The process needs its key ring and its logs, and nothing else.
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/clockinxtra/keyring /var/www/clockinxtra/api/current/logs

[Install]
WantedBy=multi-user.target
```

`/etc/systemd/system/clockinxtra-backoffice.service` is identical with `Attendance.Admin.dll`, `backoffice.env`, the `.../backoffice` paths and `SyslogIdentifier=clockinxtra-backoffice`.

</details>

> **Use `Type=simple`, not `Type=notify`.** `notify` requires the process to signal readiness over `sd_notify`, which ASP.NET Core only does when `Microsoft.Extensions.Hosting.Systemd` is referenced and `UseSystemd()` is called. Neither is in place today, so `notify` would make systemd treat every start as a failure and restart in a loop. Adding that package would be a small improvement worth making later; until then the unit files above must say `Type=simple`.

```bash
```

### 3.7 Letting the deploy user restart the services

The workflow runs `sudo systemctl restart` as the SSH user. Grant exactly that and nothing more:

`/etc/sudoers.d/clockinxtra-deploy` (via `visudo -f`):

```
deploy ALL=(root) NOPASSWD: /usr/bin/systemctl restart clockinxtra-api clockinxtra-backoffice, \
                            /usr/bin/systemctl restart clockinxtra-api, \
                            /usr/bin/systemctl restart clockinxtra-backoffice, \
                            /usr/bin/systemctl status clockinxtra-api clockinxtra-backoffice
```

The deploy account also needs to **read the journal**, or a failed deployment cannot report why. Reading another unit's journal requires membership of `systemd-journal`; without it `journalctl` prints `-- No entries --` and the rollback tells you nothing:

```bash
usermod -aG systemd-journal deploy
```

That is read-only access to the system journal. If your policy forbids it, the workflow says so when it cannot read the journal and prints the `sudo journalctl` command to run by hand instead.

Replace `deploy` with the account named in the `VPS_USER` secret. It needs write access to both release trees:

```bash
usermod -aG clockinxtra deploy
chmod -R g+w /var/www/clockinxtra
```

### 3.8 nginx and TLS

**Both sites already exist and are correct**, verified 2026-10-09: `clockinxtra` proxies to `127.0.0.1:6100`, `clockinxtra-api` to `127.0.0.1:6200`, and both set `Host`, `X-Real-IP`, `X-Forwarded-For` and — the one that matters — `X-Forwarded-Proto $scheme`. Certbot has already added the TLS listeners and the http redirect to each. Nothing below needs doing; it is kept as the reference for what those files must contain.

The only thing absent is `client_max_body_size`, which therefore takes nginx's 1 MB default. That is ample for both applications — the API caps request bodies itself (§22) and the portal posts forms, not files — so it is left alone rather than set for the sake of it.

<details>
<summary>Reference configuration</summary>

`/etc/nginx/sites-available/clockinxtra`:

```nginx
server {
    listen 80;
    server_name clockinxtra.xwoks.com api.clockinxtra.xwoks.com;
    location /.well-known/acme-challenge/ { root /var/www/html; }
    location / { return 301 https://$host$request_uri; }
}

server {
    listen 443 ssl http2;
    server_name api.clockinxtra.xwoks.com;

    # Bodies are small and fixed in shape; the API caps them itself as well.
    client_max_body_size 256k;

    location / {
        proxy_pass http://127.0.0.1:6200;
        proxy_http_version 1.1;
        proxy_set_header Host              $host;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host  $host;
    }
}

server {
    listen 443 ssl http2;
    server_name clockinxtra.xwoks.com;
    client_max_body_size 1m;

    location / {
        proxy_pass http://127.0.0.1:6100;
        proxy_http_version 1.1;
        proxy_set_header Host              $host;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host  $host;
    }
}
```

</details>

`X-Forwarded-Proto` is not optional. Without it the portal redirects to https, nginx forwards over http again, and the browser loops; and its Secure-only session cookie is never issued. Both existing sites set it, and `Admin:KnownProxies` must list `127.0.0.1` for the portal to act on it.

Both sites are already enabled, and the certificate is already issued. Verified 2026-10-09:

```text
Certificate Name: clockinxtra.xwoks.com
  Domains: clockinxtra.xwoks.com api.clockinxtra.xwoks.com
  Expiry:  2027-01-06 (VALID)
```

**One certificate covers both names**, so no further certbot work is needed and the API hostname is not waiting on anything. Renewal is certbot's timer; confirm it with `systemctl list-timers | grep certbot`.

If the sites ever need re-enabling, link only ours — this VPS also serves `marriageregistry` and `onsalesbiz.com`:

```bash
ln -sfn /etc/nginx/sites-available/clockinxtra /etc/nginx/sites-enabled/clockinxtra
ln -sfn /etc/nginx/sites-available/clockinxtra-api /etc/nginx/sites-enabled/clockinxtra-api
nginx -t && systemctl reload nginx
```

**ASM-01 says the portal is internal-only.** Publishing it to the internet contradicts that assumption; if it must be public, restrict it by source address (`allow`/`deny` in the portal's server block) or put it behind a VPN, and revisit the threat model.

---

## 4. GitHub secrets

Four, in **Settings → Secrets and variables → Actions**. None of them is a database credential.

| Secret | Value |
|---|---|
| `VPS_HOST` | Host name or IP of the VPS |
| `VPS_USER` | The deploy account (`deploy` above) |
| `VPS_SSH_KEY` | **Private** key, full PEM including the header and footer lines. Generate a key used for nothing else: `ssh-keygen -t ed25519 -C clockinxtra-deploy -f ./deploy_key`, then append `deploy_key.pub` to that account's `~/.ssh/authorized_keys` |
| `VPS_KNOWN_HOSTS` | Output of `ssh-keyscan -H <host>`. Pinned on purpose: without it the workflow would accept any host key and could hand a release to anything answering that address |

`VPS_PORT` is optional and defaults to 22.

Also create an **environment** named `production` (Settings → Environments). The workflow targets it, so you can add required reviewers and make a deployment need a human approval.

---

## 5. Running it

```bash
git tag v1.0.0 && git push origin v1.0.0     # or run "Deploy to VPS" from the Actions tab
```

The workflow publishes both hosts, uploads to `/var/www/clockinxtra/api/releases/<timestamp>-<sha>` and `/var/www/clockinxtra/backoffice/releases/<timestamp>-<sha>`, deploys the schema with the deployment identity, moves **both** `current` symlinks, restarts both services, and then probes `/health/ready` on each. The two symlinks are switched together and rolled back together, so the API and the back office cannot be left on different releases. **If either probe fails it puts the symlink back, restarts, and fails the job**, so a release that does not serve does not stay deployed. The last five releases are kept as rollback targets.

The database step runs **before** the symlink moves, so the schema is in place for the new code while the old code is still serving. That is safe because the scripts are additive and idempotent; a release containing a breaking schema change is not deployable this way and needs a maintenance window.

### First deployment only

The database is created by the first run, but it has no administrator and thirteen business settings are unset, so nothing can sign in and attendance will refuse to operate until both are dealt with:

```bash
cd /var/www/clockinxtra/backoffice/current
set -a
while IFS= read -r line || [ -n "$line" ]; do
  case "$line" in ''|'#'*) continue ;; esac
  export "${line%%=*}=${line#*=}"
done < /etc/clockinxtra/backoffice.env
set +a
dotnet Attendance.Admin.dll --create-first-administrator
```

> **Do not use `. backoffice.env` to load that file.** systemd reads an
> `EnvironmentFile` as literal `KEY=VALUE` lines; the shell does not. A
> connection string contains semicolons, so sourcing it splits
> `...;User ID=clockinxtra_admin;...` into separate commands and fails with
> `User: command not found`. The loop above reads the file rather than
> executing it, splitting on the first `=` only.

Then sign in at `https://clockinxtra.xwoks.com`, change the password it forced, enrol an authenticator, and decide the settings the deployment listed as unconfigured (above all the business time zone — attendance endpoints answer `ATTENDANCE_NOT_CONFIGURED` until it is set, by design).

---

## 6. Operating it

| Task | Command |
|---|---|
| Logs | `journalctl -u clockinxtra-api -f` |
| Status | `systemctl status clockinxtra-api clockinxtra-backoffice` |
| Readiness | `curl -s localhost:6200/health/ready \| jq` |
| Manual rollback | For each of `/var/www/clockinxtra/api` and `/var/www/clockinxtra/backoffice`: `ln -sfn $root/releases/<older> $root/current.new && mv -Tf $root/current.new $root/current`, then `sudo systemctl restart clockinxtra-api clockinxtra-backoffice`. Move both, or the two hosts run different releases |
| Smoke suite (test environments only — it writes rows) | `sqlcmd -S localhost -U sa -C -d ClockInXtra -i database/tests/smoke_attendance.sql` |

**Still not covered here, and still required before production:** database backups and a tested restore (OPEN-19), log retention and shipping, monitoring and alerting on the readiness probes (§61), and the maintenance job schedule that calls the `job.*` purge procedures.
