# Deploy Homestead

Homestead runs as one application instance with SQLite and files in `/data`.
The supplied Compose configuration adds Caddy for HTTPS. Authoring and static
publication are scheduled for Phase 2; this deployment serves setup, login,
dashboard, settings, a public welcome shell, and health endpoints.

## Production on Linux

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

The database, settings, and login key ring remain on the `homestead_data` volume.
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
the application and preserve the whole volume before an upgrade. Automated backup
and restore tooling belongs to Phase 5; do not copy a running SQLite file alone.

`/health/live` returns `Healthy` while the process is serving. `/health/ready`
returns `Ready` when SQLite is connectable after startup migrations. Neither
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
CSS, the non-root user, container recreation without the secret, retained session
keys, and logout. It removes only its own project and disposable volumes. The test
client accepts that local CA's certificate; production uses Caddy's public HTTPS
certificate flow. A real public DNS/ACME deployment requires a reachable domain.
