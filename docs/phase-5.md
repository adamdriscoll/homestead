# Phase 5 — Portability and operations

## Delivered scope

- **E01:** Authenticated, antiforgery-protected static ZIP download packages only
  the active manifest's files, under the publication lock. A failed desired build
  does not change the export. Private temporary downloads disappear after serving.
- **E02:** Hugo leaf bundles include local media, JSON front matter, a complete
  minimal layout and compiled CSS. The supported CLI is
  [Hugo 0.152.2](https://github.com/gohugoio/hugo/releases/tag/v0.152.2).
  Reviews, ordered steps/photos, gallery captions/alt text, tags, Unicode, stable
  IDs, timestamps, and URLs survive. All nondeleted posts are included; private
  work is `draft: true`. Published posts with edits get a separate working bundle
  while their active version builds normally. Deleted posts are omitted even if
  an earlier failed deletion left them temporarily public on Homestead.
- **E03:** Online SQLite backup plus original media, credentials/settings and
  keys; versioned file/checksum manifest; verified offline CLI restore on an
  empty volume. See [backup and restore](backup-restore.md).
- **E04:** Explicit activation boundary permits pointer-replacement failure
  injection. Regression tests cover failed staging/missing originals, failed
  activation, post-activation DB failure/reconciliation, upload DB failure,
  upload/backup serialization, retries, malformed archives, auth/CSRF, paths,
  output escaping, and concurrent publication.
- **E05:** [Deployment guide](deploy.md), backup guide, troubleshooting and
  upgrade/rollback instructions, environment reference, and health/log guidance.
- **E06:** `0.1.0-rc.1` assembly/package version, [changelog](../CHANGELOG.md),
  tag validation in CI, tested Docker build, sample ZIP fixture artifacts in CI,
  supported Hugo installation with checksum verification, and release checklist.

No database migration or new runtime service is required. **Maintenance** is the
new administration navigation entry. Export/backup assembly holds the existing
single-instance publication lock; public traffic continues reading static files.
ZIPs are assembled on private disk instead of buffering an entire backup in RAM.
Interrupted downloads may leave private temporary files after an abrupt process
termination; remove `backups/download-*.zip` and `backups/sqlite-*.db` with the app
stopped if needed. Neither location is anonymously served.

## Regression and acceptance evidence

Verified locally on 2026-10-08: Release build (zero warnings/errors), all 133
tests, formatting verification, migration-model check, Compose configuration,
Linux Docker image build, and the full production Compose/static-hosting/restore
smoke. No database model changes are pending.

Run from the repository root with .NET 10, Node.js 24, Docker, and Hugo 0.152.2:

```sh
dotnet restore
dotnet build --no-restore --configuration Release
dotnet test --no-build --configuration Release
dotnet format --verify-no-changes --no-restore
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project src/Homestead --no-build --configuration Release
docker build -f deploy/Dockerfile -t homestead-smoke .
node tests/smoke/docker-smoke.mjs
```

`PortabilityTests` builds a fixture containing all five types, original photos,
Unicode, tags, a draft, unpublished work, deleted work, and unpublished edits. It
invokes the real Hugo binary with `--panicOnWarning`, checks preserved routes and
media, and confirms default output has no private work. Set `HOMESTEAD_HUGO` to a
binary path if it is not on PATH. Set `HOMESTEAD_FIXTURE_OUTPUT` to a directory to
retain static/Hugo ZIPs; CI uploads these as `portability-fixtures` artifacts.

The Compose smoke uses disposable volumes, non-root Homestead, and Caddy TLS.
It downloads all three ZIPs, checks auth/CSRF and private temporary-file cleanup,
stops Homestead, extracts and serves the static ZIP in an independent Caddy
container, and requests public routes plus linked assets. It then restores the
backup via the image's actual CLI into a fresh volume and verifies the same
author session, originals, settings, all five types, and public rebuild. Finally
it returns to the original test volume and verifies ordinary recreation/restart.

### Architecture failure matrix

| Failure | Automated evidence |
|---|---|
| Render/staging failure | `PublishingTests`, per-type `FiveTypeTests` injected renderer failure; old HTML/feeds/pointer stay active. |
| Insufficient build storage/missing source | IOException injection and missing-original portability test; failed staging is removed. Real disk exhaustion: repeat on a disposable small loopback filesystem, fill it, publish, verify old pointer/site and failure notice, free space and rebuild. |
| Retried publication | `ReliabilityTests` repeats publish; one post/slug/public snapshot remains. |
| Concurrent publication | `PublishingTests` simultaneous scoped commands serialize with unique slugs. |
| Crash during build | `PublishingTests` interrupted generation recovery keeps the active manifest. |
| Pointer activation failure | `ReliabilityTests` activation injection preserves pointer, output, and readiness; retry succeeds. |
| Crash after activation | `ReliabilityTests` failed DB completion followed by restart reconciles active generation. |
| Damaged/missing active generation | `PublishingTests` readiness 503, administration survives, explicit rebuild recovers. |
| Failed unpublish | `PublishingTests` and `FiveTypeTests` old public snapshot persists and admin discloses it. |
| Settings/canonical change | `SettingsTests`, `PublicExperienceTests`, production Compose smoke. |
| Upload storage/DB failure | `ReliabilityTests` failed commit deletes original; `FiveTypeTests` partial batch preserves earlier private uploads. |
| Backup/upload race | `ReliabilityTests` shared-lock capture restores consistently. |

### ImageSharp advisory reachability

NuGet began reporting five advisories against the existing ImageSharp 3.1.12
dependency. The fixed 4.1.2 release requires a Six Labors license key during
Release builds, which this open-source project does not supply. The project
retains 3.1.12 and suppresses **only** these five audit IDs; all other audit
failures remain enabled. This does not claim the dependency is globally fixed.

- [ICC CLUT allocation](https://github.com/advisories/GHSA-gwg2-r3hj-4w44):
  decoders use `SkipMetadata = true`, and no code reads ICC entries.
- [Histogram equalization indexing](https://github.com/advisories/GHSA-j3p4-wp97-rph4):
  Homestead performs no histogram/equalization processing.
- [TIFF T4 encoding](https://github.com/advisories/GHSA-j9gm-c75j-xc9q) and
  [TIFF T6 encoding](https://github.com/advisories/GHSA-jjfr-hcj7-qf5w):
  Homestead never encodes TIFF or accepts TIFF uploads.
- [BigTIFF decoder loop](https://github.com/advisories/GHSA-wmxv-xphr-5c9g):
  extension and detected format/MIME must match JPEG, PNG or WebP **before**
  image identification/decoding. Regression tests reject disguised TIFF/BigTIFF
  data. Reassess these suppressions before enabling metadata parsing, image
  processing, or another image format. A future licensed update or decoder
  replacement should remove the suppressions.

## Release candidate checks that require real infrastructure/devices

Automated tests demonstrate the data, security, exports and container workflows.
They do not substitute for a physical-phone or public-DNS Droplet acceptance run.
Before tagging a release candidate, record these checks:

1. Follow the deployment guide on a fresh Linux Droplet and public domain; verify
   Caddy obtains a public certificate and both health endpoints respond.
2. In a desktop browser, keyboard through Maintenance and each editor; check
   visible focus, form labels, download errors, and light/dark contrast.
3. On a real phone, publish a text post and a camera-roll image post; check all
   five editors, touch controls, and the explicit HEIC/HEIF JPEG fallback.
4. Share a permalink and verify its canonical/social preview.
5. Restore a pre-upgrade backup on the same version, upgrade, restart, verify
   settings/drafts/media/session persistence, and rehearse rollback using the
   pre-upgrade data with its matching image.

These manual checks remain pending until executed on real devices/infrastructure.
No release tag or remote release is created by implementing this phase in an
uncommitted checkout. After review/commit and acceptance, tag the clean commit:

```sh
git tag -a v0.1.0-rc.1 -m "Homestead v0.1.0 release candidate 1"
git push origin v0.1.0-rc.1
```

CI rejects a tag that differs from the source version and runs the complete
regression, Hugo, Docker, static-hosting and restore checks on the tagged source.
Next milestone: execute the manual acceptance list before calling v0.1 released.
