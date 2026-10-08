# Deploy Homestead

Homestead runs as one application instance with SQLite and files in `/data`.
The supplied Compose configuration adds Caddy for HTTPS. It serves administration,
article authoring/uploads/preview, and a generated public site. Public requests
read the active generation's HTML, CSS, and selected media without querying SQLite.

## Production on Linux

For a fresh Ubuntu LTS Droplet, create an ordinary sudo-capable operator account,
allow SSH plus TCP 80/443 in the host/cloud firewall, and point your domain's
A record (and AAAA record only if IPv6 is configured) at the server. Install
Docker Engine and the Compose plugin using the
[official Ubuntu instructions](https://docs.docker.com/engine/install/ubuntu/).
Keep SSH restricted to your administrative access. Clone this repository and
check out the intended release tag before proceeding. No Node, .NET SDK, Hugo,
or database server is needed on the host: the Docker build installs its build
dependencies and the final image contains the runtime. Size disk storage for
original photos, three public generations, exports/backups, and restore staging.

Requirements: Docker Engine with Compose 2.24.4 or later, a domain pointing to the
server, and inbound TCP ports 80/443 (UDP 443 is optional). Run from the checkout:

```sh
cd deploy
cp .env.example .env
chmod 600 .env
```

Edit `.env`, set `HOMESTEAD_DOMAIN` to your domain without a scheme or path, and set
`HOMESTEAD_SETUP_TOKEN` to a new random secret. Generate a secret locally with
`openssl rand -hex 32`. Keep it out of source control and shell command arguments.

```sh
docker compose up -d --build
docker compose logs homestead
```

Visit `https://your-domain/setup`. Enter the configured secret, your email address,
and a password of at least 12 characters with uppercase/lowercase letters, a number,
and a symbol. Setup creates exactly one administrator and then returns 404 for
every subsequent attempt. There is no registration endpoint. Sign in at
`/account/login` and set your site title, display name, canonical URL, and time zone.

After setup, clear `HOMESTEAD_SETUP_TOKEN` in `.env` and recreate the app container:

```sh
docker compose up -d --force-recreate homestead
```

The database, settings, login keys, private originals, and generated public site
remain on the `homestead_data` volume.
Verify `/admin` with your existing session. To restart both containers:

```sh
docker compose restart
```

Ordinary `docker compose down` preserves volumes. `down --volumes` deletes your
content and administrator; use it only for a disposable installation.

## Configuration

| Variable | Behavior |
|---|---|
| `HOMESTEAD_BASE_URL` | Required valid absolute site-root URL. HTTPS is required in production. Seeds the canonical setting on the first database creation; it does not overwrite saved settings at restart. |
| `HOMESTEAD_DATA_PATH` | Defaults to `/data`; must be outside `wwwroot` and writable by the app user. |
| `HOMESTEAD_SETUP_TOKEN` | Required until the administrator exists, 32–1024 characters. Remove after setup. |
| `HOMESTEAD_TRUSTED_PROXIES` | Comma-separated proxy IP addresses. Only these peers may supply forwarded scheme/client-IP headers; loopback is the default. The sample trusts Caddy at `172.30.0.3`. |
| `AllowedHosts` | ASP.NET host allowlist. Compose permits the configured domain and `localhost`. |
| `ASPNETCORE_ENVIRONMENT` | Production in the container; Development is for local work. |

The sample private subnet is `172.30.0.0/24`. If it conflicts with an existing
network, change the subnet, both fixed service addresses, and the trusted proxy
address together. The application port is not published on the host. Caddy
terminates TLS and supplies the forwarded HTTPS scheme. Production rejects HTTP
requests except internal health probes. The broad
`ASPNETCORE_FORWARDEDHEADERS_ENABLED` switch is intentionally disabled by the app.

For another reverse proxy, allowlist its IP, forward `X-Forwarded-Proto` and
`X-Forwarded-For`, limit backend network access to that proxy, and serve HTTPS.
This follows [ASP.NET Core's trusted proxy guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

## Storage, upgrades, and health

The image runs as the unprivileged .NET `app` user. A named volume inherits `/data`
ownership from the image. For a bind mount, prepare the directory for that user
before starting (obtain the UID using `docker run --rm --entrypoint id homestead-smoke -u`
after building the image). The app restricts its data directories to owner access
on Linux. Data Protection keys are persisted under `/data/keys`; they are protected
by filesystem permissions and should be treated as sensitive backup material.

SQLite migrations run once at startup before requests are accepted. Migration or
storage errors stop startup with an error in the container logs. Run one replica;
do not share a SQLite data directory between multiple application processes. Stop
the application and preserve the whole volume before an upgrade. Use
**Maintenance → Download full backup** before stopping, and follow the
[backup and restore guide](backup-restore.md); do not copy a running SQLite file alone.

`/health/live` returns `Healthy` while the process is serving. `/health/ready`
returns `Ready` when SQLite is connectable after startup migrations and the active
generation has recovered successfully (or setup has not yet created an account). Neither
endpoint exposes content or configuration. From inside the private network, probe
`http://homestead:8080/health/ready`; through Caddy, use the HTTPS URL.

## Repeatable deployment smoke test

From the repository root with Docker and Node.js 24 available:

```sh
docker build -f deploy/Dockerfile -t homestead-smoke .
node tests/smoke/docker-smoke.mjs
```

The test launches the actual Compose topology under a unique project name with
temporary volumes, a random setup secret, a dynamically assigned localhost port,
and Caddy's local TLS CA. It tests setup, login, settings, private paths, compiled
CSS/JavaScript, article photo upload, publication and update, draft isolation,
the non-root user, container recreation without the secret, retained session
keys/content/media, anonymous public access, and logout. It removes only its own project and disposable volumes. The test
client accepts that local CA's certificate; production uses Caddy's public HTTPS
certificate flow. A real public DNS/ACME deployment requires a reachable domain.

## Publishing and recovery

Use **Posts → New article**. Save or allow autosave, upload a JPEG/PNG/WebP image,
preview, then publish. Originals remain in `/data/media/originals`; only images
referenced by the saved published snapshot enter the active public generation.
HEIC/HEIF requires exporting a JPEG or using the phone's Most Compatible setting.

Builds stage in `/data/site/builds/{id}`. `/data/site/current.json` atomically
selects the complete active generation and records its post revisions and settings.
The runtime serves only files listed in that manifest. It retains the current
generation and two previous successful builds, removing failed staging output.

If a build fails, the saved desired changes remain pending and the old site stays
active. An unsuccessful unpublish/delete can leave its old page public; the UI
reports this. Use **Posts → Rebuild site** to retry. Any later successful publish
also applies all previously saved pending publication/settings changes. Editing
a draft alone never updates its public snapshot.

Restart recovers the active manifest and reconciles interrupted build records.
A corrupt or incomplete active generation makes readiness return 503; sign in and
rebuild to recover. Keep the entire `/data` volume during container replacement.
See [Phase 2](phase-2.md) for implementation decisions and phone acceptance steps.

## Exports, backup cadence, and upgrades

Use **Maintenance** for static ZIP, Hugo ZIP, full backup, and rebuild. Static
ZIPs are public and contain the active site. Hugo ZIPs include private drafts and
original photos; use Hugo 0.152.2 to build them. Backups additionally contain
credentials and encryption keys. Store backup ZIPs securely off-host daily (or
after substantial authoring), weekly, and before upgrades. Rehearse restoration
monthly. See [backup/restore](backup-restore.md) for commands and archive limits.

For an upgrade, retain the pre-upgrade image and download a full backup first.
From `deploy`, record the current image ID (`docker compose images -q homestead`)
and tag that ID as `homestead:rollback`. Record its application version alongside
the backup. Stop Homestead, check out the new release, and rebuild/recreate:

```sh
docker compose stop homestead
# Check out the intended release in the repository before building.
docker compose build homestead
docker compose up -d --no-deps homestead
docker compose logs --tail 100 homestead
curl --fail https://your-domain/health/ready
```

Check the public site, original photos, drafts, settings, and administrator login.
Migrations execute on startup; do not run a second instance against the volume.
Never downgrade a migrated database in place. To roll back, stop the new app,
restore the pre-upgrade backup into a **fresh** volume using its matching old
image, and configure Compose to use that volume and the saved `homestead:rollback`
image with an override. Keep the failed/new volume for diagnosis. Return the
domain to service only after health and content checks pass. Phase 5 itself adds
no migrations, but the same procedure applies to future schema upgrades.

## Logs and troubleshooting

Use `docker compose logs --tail 100 homestead caddy`; add `--follow` while
diagnosing startup/publication. Logs include migration/setup status, generation
IDs and failures, export failures, and recovery diagnostics. Do not publish raw
backups, key files, setup secrets, or private content when sharing diagnostics.

| Symptom | Check/action |
|---|---|
| Caddy cannot issue TLS | Verify DNS reaches this host, firewall ports 80/443, domain spelling, and Caddy logs. |
| HTTPS required / unexpected login cookie behavior | Verify forwarded scheme and the exact trusted proxy IP; do not blanket-trust forwarded headers. |
| Container exits before setup | Inspect required base URL, setup secret length, volume ownership, SQLite migration errors, and free disk. |
| Readiness 503 while liveness succeeds | Inspect damaged active generation/SQLite; sign in and use Maintenance rebuild. |
| Old content remains after publish/unpublish | Read the build failure notice/logs; fix disk/media issues and rebuild to apply pending public state. |
| Export or backup fails | Check free space, originals, data-directory write access, and server logs. No partial archive is offered. |
| Photos rejected | Use matching JPEG/PNG/WebP contents, extension and MIME, ≤20 MB, ≤40 MP. HEIC/HEIF requires JPEG conversion. |
| Login fails after restoring without keys | Sign in again; only the complete key backup preserves compatible sessions. |

Use [Phase 5's acceptance checklist](phase-5.md) to record actual public-DNS,
physical-phone, keyboard/contrast, share-preview and upgrade/rollback verification
before promoting the release candidate.
