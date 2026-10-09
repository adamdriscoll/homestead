# Back up and restore Homestead

## Download a complete backup

Sign in and open **Maintenance → Download full backup**. The ZIP contains a
SQLite online backup, every recorded original photo (including unused and private
photos), installed custom theme ZIPs, saved settings and drafts, the administrator's password hash, and the
Data Protection key ring. `manifest.json` records the application version, archive
format, UTC creation time, file sizes, and SHA-256 checksums. Database capture and
media/theme mutations share the publication lock. Authoring/publication may wait while
the archive is assembled; public static requests continue normally.

The implementation uses [Microsoft.Data.Sqlite's online backup API](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup),
including committed WAL data. Copying a live `homestead.db` alone is unsafe.
Generated pages are reproducible and are excluded. Environment secrets, `.env`,
Caddy certificates, reverse-proxy configuration, and the application image are
not included; preserve those separately. Saved canonical URL and branding are
included in SQLite.

Optional newsletter configuration, protected Brevo keys, subscriber addresses and
preferences, confirmation state, mailing schedules, and delivery history are also
included in SQLite. The key ring is required to decrypt the API key and preserve
existing confirmation/management links. Subscriber data and credentials are
excluded from static and Hugo exports. Treat full backups as private reader data.
An older restore may replay emails sent after the backup; review or disable
Newsletter settings before resuming delivery on a recovered installation.

Keep backup archives private, encrypted at rest, and off the publishing server.
They contain private work, originals that may contain GPS/EXIF metadata, password
hashes, and keys capable of decrypting login cookies. The checksums detect damage;
they do not authenticate an archive supplied by someone else. Only restore your
own trusted backups. Keep at least seven daily and four weekly copies, and one
before each upgrade. Test a restore monthly. For unattended host snapshots, stop
Homestead during capture and preserve the entire data volume, including SQLite's
WAL/SHM files, plus Compose configuration; restore such snapshots using the host's
volume recovery procedure. The CLI below accepts Homestead backup ZIPs.

An export is different from a backup: static exports have public files only;
Hugo exports are a migration project and do not preserve administrator credentials
or the complete authoritative database.

## Automated backups to S3-compatible storage

Open **Settings → Backup** and enable automated backups. Enter the bucket,
signing region, object key prefix, and an access key ID and secret access key.
Leave the endpoint empty for Amazon S3, or enter the compatible provider's HTTPS
service endpoint without a path, credentials, query, or fragment. Path-style
bucket URLs are enabled by default for compatible providers; disable them when
your provider requires virtual-hosted bucket URLs. The bucket must already exist.

Choose one frequency:

- **Before publishing a new version of the site:** upload before every public
  build, including publish/update, unpublish/delete, settings changes, manual
  rebuild, and startup rebuild when no active generation exists. Capture includes
  already committed desired changes and private drafts. A failed backup prevents
  the build, preserves the previous active site, and leaves saved desired changes
  pending. Retry after correcting backup settings, or disable backups and rebuild.
- **Once a day:** upload when first enabled, then 24 hours after the last success.
- **Once a week:** upload when first enabled, then seven days after the last success.

The in-process scheduler checks every minute and catches up once after downtime;
it does not create an archive for each missed interval. Failed scheduled uploads
retry after 15 minutes. Schedule and upload status survive restart. Changing the
destination, frequency, or enabled state starts a fresh schedule. Choose
**Back up now** to try the saved enabled configuration immediately.

Each upload is the same complete, checksum-verified restore format as a manual
backup. It uses a unique UTC timestamp/ID object name beneath the prefix and never
overwrites or deletes previous backups. Large uploads use the AWS SDK's
[multipart transfer utility](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TTransferUtility.html).
Grant object upload and multipart-abort permissions for that prefix. Set bucket
lifecycle rules for retention and abandoned multipart uploads. Uploads have a
30-minute limit. Archive capture and upload hold the shared mutation lock, so
authoring and publication can wait; static public traffic continues. Temporary
ZIP/database files are removed after completion or failure.

Credentials are protected using the persisted Data Protection key ring and never
displayed after saving. Blank credential fields retain saved values; disabling
backups and selecting **Clear saved credentials** removes them. Credentials and
their key ring are included in complete backups, so treat archives as capable of
restoring access to the bucket. A restored installation retains the enabled
schedule; use a separate bucket or disable backups before operating a restored
copy alongside the original. Bucket access, encryption at rest, and retention
remain the operator's responsibility; Homestead sends no public ACL and does not
create buckets. Status in the Backup tab reports attempts, successes, object keys,
and safe failure guidance without signed URLs or provider credentials.

## Offline restore with Docker Compose

Use the **same Homestead application version** shown in the manifest (currently
`0.1.0-rc.1`). Upgrade only after verifying a restore on that version. The restore
command runs without starting the web server, applying migrations, or requiring
a bootstrap token. It refuses any nonempty target, duplicate/extra entries,
unsafe paths, wrong versions, incomplete media, bad checksums, and SQLite integrity
or foreign-key failures. Archives above 20 GB are refused. Validation uses a
private staging directory on the empty target volume and installs only after every check succeeds.

From the repository root, build the matching image:

```sh
docker build -f deploy/Dockerfile -t homestead:0.1.0-rc.1 .
```

On the new server, prepare `deploy/.env` and the domain/proxy setup following the
[deployment guide](deploy.md). Keep the previous server stopped during cutover.
Copy the ZIP into a private directory on the new server. Create a **new** volume:

```sh
chmod 700 /srv/homestead-backups
chmod 600 /srv/homestead-backups/backup.zip
docker volume create homestead-restored
docker run --rm --user root --entrypoint sh \
  -v homestead-restored:/data -v /srv/homestead-backups:/backup:ro \
  homestead:0.1.0-rc.1 -c 'chown app:app /data; chmod 700 /data'
```

The image runs as the unprivileged `app` user. Ensure that user can read the ZIP:
use a private directory owned by its numeric UID (obtain it with
`docker run --rm --entrypoint id homestead:0.1.0-rc.1 -u`), or give that UID a
read-only ACL on the archive directory and file. Restore as the app user:

```sh
docker run --rm -v homestead-restored:/data \
  -v /srv/homestead-backups:/backup:ro \
  homestead:0.1.0-rc.1 restore /backup/backup.zip /data
```

A zero exit code means validation and restoration succeeded. A failure prints a
specific diagnostic on stderr and returns exit code 1. The target remains empty
after validation failure. Allow space for the entire uncompressed backup on the
target volume. Staging stays on that volume so non-root container users can restore
without access to its parent, and installation moves stay on the same filesystem.

Create `deploy/restore.yaml` to select the restored volume:

```yaml
volumes:
  homestead_data:
    external: true
    name: homestead-restored
```

Start one instance, without a setup secret:

```sh
cd deploy
docker compose -f compose.yaml -f restore.yaml up -d --build
docker compose -f compose.yaml -f restore.yaml logs homestead
```

Startup rebuilds the public site from saved published snapshots. Sign in using
the saved account, inspect drafts and photos, check `/health/ready`, and compare
the public posts, RSS, and sitemap. Update the canonical URL in **Settings** if
the domain changed. Remove obsolete servers from public traffic before cutover.

All future Compose commands for this installation must include both files. Do
not run `down --volumes` against authoritative data.

## Local restore and login implications

With the old process stopped, use a new directory:

```sh
dotnet src/Homestead/bin/Release/net10.0/Homestead.dll restore backup.zip /new/data
```

Set `HOMESTEAD_DATA_PATH` to that directory and start the same app version normally.
No fresh administrator is created. The restored key ring preserves sessions where
cookie domain and account security stamp still match. Losing the key ring requires
signing in again with the restored password. To deliberately revoke all previous
cookies, remove the restored `keys/` directory **with the application stopped**;
startup creates a new key ring. If keys or passwords were compromised, rotate the
affected credentials as well.

## Recovery diagnostics

| Diagnostic | Action |
|---|---|
| Nonempty data directory | Select a fresh volume; retain the existing one for rollback. |
| Wrong application version | Restore using the image/version that made the backup. |
| Missing file/checksum mismatch | Obtain another trusted complete backup; do not edit the manifest to bypass validation. |
| Permission denied | Correct app-user ownership and read permissions on the volume/archive. |
| Disk full | Free space on the Docker host and target filesystem; retry against an empty target. |
| Public rebuild failure | Inspect logs and original media; use **Maintenance → Rebuild site**. Drafts stay private. |

The regression suite and production Compose smoke exercise fresh-volume restore,
original checksums, all six types, locations, persisted keys/account, settings, and rebuild.
