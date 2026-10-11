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
- Deployment, upgrade/rollback, and backup scheduling guides, with release
  acceptance tracked in GitHub issues.
- SkiaSharp upload validation with metadata isolation, bounded full first-frame
  decoding, and original-byte preservation; ImageSharp and its audit suppressions
  removed from the application and tests.

Release candidate preparation; real-phone and public-DNS Droplet checks remain
pending in [release readiness](https://github.com/PoshTools/homestead/issues/22).
Image decoder requirements and replacement evidence are documented in
[agent guidance](AGENTS.md#privacy-security-and-configuration) and
[#8](https://github.com/PoshTools/homestead/issues/8).
