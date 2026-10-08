---
name: homestead-local
description: Run Homestead locally in Docker with a disposable latest-source sandbox and a persistent instance for testing container upgrades, backups, and rollback.
---

# Homestead locally

Use [scripts/homestead-local.ps1](scripts/homestead-local.ps1) with PowerShell 7 and Docker Desktop's Linux engine. Run from the Homestead checkout, or pass `-RepoPath` explicitly. Read `docs/deploy.md` in that checkout when diagnosing application startup, storage, or proxy failures.

## Choose the environment

- **Sandbox:** `-Environment Sandbox -Action Start` builds the current working tree, refreshes the container, and retains sandbox data. `-Action Reset` explicitly discards only sandbox data and starts fresh. Use reset when the request calls for a clean installation.
- **Persistent:** `-Environment Persistent -Action Start` builds only on first use, then resumes its saved image and data. `-Action Upgrade` builds current source, stops the app, snapshots its whole `/data` volume and pins the old image, then starts the new image against the same data.

There is currently no published Homestead image workflow. "Latest" means the current checkout including local changes, not a guessed registry tag. To use a supplied image instead, add `-Image <reference>` to Start, Reset, or Upgrade. Images are resolved to immutable IDs; mutable tags cannot silently upgrade the persistent instance. Start refuses to replace an existing persistent image; use Upgrade for that operation. A supplied local tag is used as-is; pull it explicitly first if the user wants a registry refresh.

```powershell
$runner = Join-Path (Get-Location) '.agents/skills/homestead-local/scripts/homestead-local.ps1'
pwsh -File $runner -Environment Sandbox -Action Start
pwsh -File $runner -Environment Persistent -Action Start
pwsh -File $runner -Environment Persistent -Action Upgrade
```

Defaults: sandbox `https://localhost:5443`, persistent `https://127.0.0.1:5444`. Distinct hosts isolate authentication cookies. Both run the Production app behind Caddy's internal TLS, bound only to host loopback. Ordinary Start checks readiness with certificate verification skipped and leaves host trust unchanged.

When the user requests trusted local HTTPS or asks to fix a local certificate trust error, use `-Environment <environment> -Action TrustCertificate` on Windows. It exports only the public root CA from that environment's running Caddy container, validates it, adds it to `CurrentUser/Root`, and verifies an HTTPS readiness request with certificate validation enabled. Windows may display a Security Warning dialog and wait for the user to confirm; identify the CA and thumbprint from the script output and tell the user which dialog needs attention. Each environment has its own CA. A browser may need restarting after the import; browsers with separate trust stores may need the exported `.homestead-local/<environment>/root.crt` imported there. Record the reported thumbprint; to undo trust remove that specific certificate from `Cert:\CurrentUser\Root`. For other certificate errors, inspect the served certificate's hostname, validity period, and chain before changing trust. [Caddy's local HTTPS guidance](https://caddyserver.com/docs/running#local-https-with-docker) explains the separate container and host trust stores.

Project names include a state-root-path hash. Each environment owns separate app/TLS volumes and network. On initialization, `-Port`, `-Subnet` (an IPv4 /24, default `172.30.80.0/24` or `172.30.81.0/24`), and `-StateRoot` override local defaults. Existing environments retain saved settings. If Docker reports a subnet/port collision, choose an unused value before initialization or edit the stopped environment's state deliberately.

## Setup and everyday operations

Start reports the URL and local state path. Open `/setup`; retrieve its random bootstrap secret with `-Action Token` only when needed, and enter the user's chosen administrator credentials. Avoid printing the secret into ordinary agent logs. State under the ignored `.homestead-local` directory includes that secret and backups containing private data; keep it out of commits and shared reports.

After setup, run `-Action CompleteSetup`. It requires `/setup` to be closed, clears the saved bootstrap secret, and recreates the app. Confirm existing login still works.

`-Action Status` shows readiness, image, volume, and latest backup. `-Action Logs` prints recent app/proxy logs. `-Action Stop` removes this environment's containers/network and preserves all volumes, image selections, and backups. Start resumes it. Routine cleanup must preserve persistent data; Reset is supported only for Sandbox. Preserve `.homestead-local` to retain image/volume selections.

## Upgrade and recovery

Use `-Action Backup` to take a stopped-app snapshot without upgrading. It archives the entire data volume, including SQLite journal files, uploads, generated site, and Data Protection keys, and restarts the original app if it was running. Backup metadata pins the exact image and records the local configuration. A failed backup prevents Upgrade from replacing the app.

Before an upgrade, record representative published URLs, settings, media, and an authenticated session when available. After Upgrade, verify readiness, administrator/login availability, saved settings, drafts, published content/media, and session continuity. Readiness alone is startup validation, not proof of migration/content compatibility. If the user requested startup only, leave first-run setup for them; do not invent administrator credentials or sample content.

`-Action Rollback` restores the latest snapshot into a **new volume** and recreates the app using its pinned pre-upgrade image. Add `-BackupPath <directory>` to choose an older backup reported by the script. Metadata must belong to this environment. This changes the active instance to the snapshot; use it when rollback/recovery is requested. The newer data volume remains available for investigation. Never run an older image against migrated data as a substitute for restoring its paired snapshot.

On startup/readiness failure, inspect Status/Logs and the reported backup directory. Retain the failed instance and data for diagnosis; report the failure instead of repeatedly upgrading or resetting. The script serializes operations per environment and checks Docker exit codes.

Completion: report both URLs, whether each is ready or needs setup, the tested image IDs, and any unverified content/migration checks. For upgrade work also report the backup location and rollback outcome if used.

When modifying the runner, use [scripts/verify-local.mjs](scripts/verify-local.mjs) from the checkout with Node.js 24: `node .agents/skills/homestead-local/scripts/verify-local.mjs <baseline-image> <upgrade-image>`. It exercises setup, publication/media, private drafts, retained login/settings, reset isolation, backups, and rollback in temporary environments on ports 5543/5544 and subnets `172.30.82.0/24`/`172.30.83.0/24`. Supply locally available images; the default names match existing deployment smoke-test images.
