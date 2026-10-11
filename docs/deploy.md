# Deploy Homestead

Homestead runs as one application instance with SQLite and files in `/data`.
The supplied Compose configuration adds Caddy for HTTPS. It serves administration,
article authoring/uploads/preview, and a generated public site. Ordinary public requests
read the active generation's HTML, CSS, and selected media without querying SQLite.

## Scheduled publication

In a post editor, open **Details → Scheduled publication**, choose a future date
and time in the saved site timezone, and select **Schedule post**. Scheduling saves
outstanding edits and freezes a validated copy. Later draft edits remain private;
**Replace schedule** saves a new copy and time. A published post keeps its current
public copy until the scheduled update activates. **Posts → Scheduled** lists
pending schedules and their retry status.

Dates are converted to UTC when saved. Changing the site timezone afterwards
does not move a schedule. A time skipped by daylight saving is rejected. For a
time that occurs twice when clocks move back, enter the matching UTC offset to
choose which occurrence, for example `-05:00` or `-06:00` for America/Chicago.

An in-process worker checks at startup and every 30 seconds while Homestead is
running. It processes overdue schedules after downtime through the shared
publication lock, commits desired snapshots, then builds and atomically activates
the entire site. Several due posts share one build. New posts use the scheduled
instant as their first-publication date; updates retain the existing first date
and URL. Publication can occur later than the selected time because of downtime,
another mutation, a backup, or a build failure. No new environment settings,
external scheduler, or runtime service is needed; run only one instance.

**Cancel schedule** removes an uncommitted schedule without changing the draft or
current public post. Once the worker has committed the desired publication, use
**Unpublish** to withdraw it, **Publish** to replace it, or **Rebuild site** to retry.
Immediate publish, unpublish, and delete clear the schedule. Scheduling and
cancellation enforce draft versions, so stale tabs cannot replace newer choices.

Build failures preserve the previous complete site and disclose pending publication
in the editor and Posts list. The worker retries every five minutes and never reads
later draft edits into a retry. Any successful rebuild can activate already committed
changes. Disabled post types wait and retry until enabled, or can be cancelled
before commitment. Saved schedules survive restart and full backup/restore.

## Optional Brevo newsletter

Open **Settings → Newsletter**, enter a Brevo API key, verified sender email, and
sender name, then enable and save. This uses Brevo's
[transactional email API](https://developers.brevo.com/reference/send-transac-email)
with separate messages for each reader. The server needs outbound HTTPS access to
`api.brevo.com`. Authenticate your sending domain and verify the sender in Brevo;
account sending limits and charges apply. No new environment variables or runtime
services are required. Blank API key fields retain the protected saved key.

Readers choose instant notices or weekly summaries and any of the seven post types
at `/newsletter/`. Confirmation links expire after 24 hours and require an explicit
form submission. Repeated confirmation requests for an address are throttled; POST
requests are also limited by peer IP. Link-scanning GET requests never subscribe,
change preferences, or unsubscribe. Each newsletter has a preferences/unsubscribe
link; keep those bearer links private. Management remains available when sending
is disabled. The subscription endpoints are the explicit dynamic exception to
static public serving and require Homestead, SQLite, and its saved key ring.

An in-process worker checks once a minute. Instant notices normally send on the
next check. Weekly summaries begin seven days after confirmation or a preferences
change, then run seven days after the previous check. Downtime is included in the
next summary. Empty windows send nothing. Only a post's first successful public
activation is announced; edits and republishing do not repeat it. Enabling or
re-enabling starts with future posts. Removed or disabled-type posts are omitted
before sending; an attempted delivery is cancelled if its post set is removed.
Private drafts and failed activations never enter mail content.

Delivery records, schedules, and first-activation snapshots persist in private
SQLite. Failed sends retry after two minutes with backoff up to one hour, without
blocking site activation. Settings shows safe errors and the last accepted email;
Brevo's email logs distinguish acceptance from delivery to the recipient. The
worker serializes private changes and briefly holds the publication lock during
each provider request (15-second timeout), releasing between recipients.

Retries reuse a durable UUID and frozen message content with Brevo's
[idempotency header](https://developers.brevo.com/docs/heterogenous-versions-batch-emails).
Provider deduplication has a limited lifetime: a crash or uncertain network result
followed by a retry after that lifetime can duplicate an email. The local sent
history prevents ordinary rebuild/restart duplicates, but cannot guarantee
exactly-once remote delivery. Restoring an older backup can also replay sends
accepted after that backup; disable newsletters before resuming a restored copy
if needed. Test with your own subscription before inviting readers.

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

Enable **Settings → Posts → Enable location map** to add `/map/` and a Map
navigation link. It starts disabled. The map uses published coordinates from
enabled post types; places with only a name or address are omitted. Draft location
edits appear only after Publish/Update. Leaflet provides pan, zoom, and one marker
per post. Marker popups link to posts; shared coordinates list all posts at that
spot. A linked list below also works without JavaScript. Leaflet 1.9.4 and its
assets are bundled locally. The map loads online tiles directly from
`https://tile.openstreetmap.org` with OpenStreetMap attribution, without an API key.
Map responses and sites with published GPX activity routes allow that tile host
in the image Content Security Policy and send an origin referrer. Fitness routes
use the same bundled Leaflet and tiles independently of the location-map setting.
Private activity editors/previews require **Show route map** before requesting
tiles; draft coordinates stay in the authenticated page. Tile failures leave the
route lines, markers, and toolbar available. Maps require JavaScript; activity
statistics and charts remain available without it. Follow the [OpenStreetMap tile policy](https://operations.osmfoundation.org/policies/tiles/);
tiles are subject to provider availability and are not bundled for offline use.
If tiles fail, markers and the post list remain available. Static exports preserve
the active map and its Leaflet assets; Hugo exports include a map
layout when the saved setting is enabled. Disabling removes the page and its
navigation link after a successful rebuild. A failed rebuild keeps the previous
complete site, including its map.

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
Track real-device publishing acceptance in the
[physical-phone checklist](https://github.com/PoshTools/homestead/issues/3).

## Exports, backup cadence, and upgrades

Use **Maintenance** for static ZIP, Hugo ZIP, full backup, and rebuild. Static
ZIPs are public and contain the active site. Hugo ZIPs include private drafts and
original photos; use Hugo 0.152.2 to build them. Backups additionally contain
credentials and encryption keys. **Settings → Backup** can upload full backups to
a private S3-compatible bucket before publication, daily, or weekly. Configure an
HTTPS endpoint (or leave it blank for Amazon S3), signing region, bucket, prefix,
and credentials. See [automated backups](backup-restore.md#automated-backups-to-s3-compatible-storage)
for scheduling, failure behavior, permissions, and retention. Store backup ZIPs securely off-host daily (or
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
domain to service only after health and content checks pass. Use this procedure
for upgrades even when the new release has no database migrations.

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

Use the [release acceptance checklist](https://github.com/PoshTools/homestead/issues/22)
to record actual public-DNS,
physical-phone, keyboard/contrast, share-preview and upgrade/rollback verification
before promoting the release candidate.
