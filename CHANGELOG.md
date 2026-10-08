# Changelog

## 0.1.0-rc.1 — 2026-10-08

- Complete five-type personal publishing with private drafts, static publication,
  branding, tags, feeds, social metadata, and atomic build recovery.
- Maintenance screen with active static ZIP, usable Hugo export (supported Hugo
  0.152.2), complete original-media/database/key backups, and manual rebuild.
- Offline version/checksum/SQLite-validated backup restore into a fresh volume.
- Regression coverage for archive privacy, all-type Hugo builds, corruption and
  traversal rejection, activation/commit failures, backup concurrency, and real
  Docker restore plus static-only hosting.
- Deployment, upgrade/rollback, backup scheduling and release acceptance guides.

Release candidate preparation; real-phone and public-DNS Droplet checks remain
pending. See [Phase 5 verification and limitations](docs/phase-5.md), including the
documented ImageSharp audit suppressions for unreachable processing paths.
