# Working on Homestead

## Scope and sources of truth

Homestead is a single-author, self-hosted publishing application with five fixed
post types: microblog (called a note in the UI), article, review, guide, and gallery.
Keep focused Markdown editors, private drafts, portable content, and a static
public website. Prefer concrete services within the existing application.

Track unfinished work and proposals in [GitHub issues](https://github.com/PoshTools/homestead/issues).
The old implementation phases are delivered; keep planning and acceptance work
in issues rather than recreating local phase documents. Deferred proposals need
explicit scope before implementation. The current public site has one theme with
branding presets; [Theme Engine #1](https://github.com/PoshTools/homestead/issues/1)
proposes public themes with light/dark administration kept separate. Historical
MVP exclusions are not permanent bans on explicitly requested features.

- For development checks, tools, or migrations, read [contributing](docs/contributing.md).
- For configuration, Docker, proxies, upgrades, or troubleshooting, read [deployment](docs/deploy.md).
- For backup handling, restore, or server migration, read [backup/restore](docs/backup-restore.md).
- For release acceptance/promotion, use [release readiness #22](https://github.com/PoshTools/homestead/issues/22).
  Browser emulation and local Caddy TLS do not satisfy physical-phone or public-DNS acceptance.

## Architecture and navigation

One ASP.NET Core .NET 10 application lives in `src/Homestead`; xUnit tests live in
`tests/Homestead.Tests`. Administration uses Razor Pages with plain JavaScript
and progressively enhanced forms. Public documents use noninteractive Razor
components and `HtmlRenderer` on its dispatcher, independently of a request.
Tailwind/daisyUI CSS is compiled locally; Node.js and Hugo are build/test tools,
not production runtime services.
Public navigation/content must work without JavaScript and use ordinary links;
keep layouts semantic, responsive, and usable by keyboard and touch.

- `Program.cs`: startup migrations/recovery, middleware, authentication,
  health, private media, and the public static fallback.
- `Data/`: EF Core SQLite entities, constraints, migrations, and UTC conversion.
- `Features/Articles/`: shared editing, snapshots, validation, and slugs for all
  five types despite the historical namespace name.
- `Features/Publishing/`: Markdown, rendering/discovery, serialized builds,
  atomic activation, and active-site serving/recovery.
- `Features/Media/` and `Features/Export/`: originals, static/Hugo ZIPs, backup/restore.
- `Pages/` and `Components/Public/`: authoring pages and shared public/preview templates.
- `Infrastructure/Storage/StoragePaths.cs`: private paths. `deploy/`: image and Caddy.

Avoid adding a SPA, interactive Blazor host, external database, queue, or runtime
service without an approved need. Run one application instance: the publication
lock and SQLite storage assume a single writer process.

## Publication and compatibility

Drafts and typed, versioned published snapshots are separate. Autosave changes
only drafts. Publish/Update validates and commits desired public state before
building the entire site; unpublish/delete also save desired state first. Build
from committed snapshots/settings, never working draft tables.

Draft saves use `PostDraft.Version` for optimistic concurrency; stale tabs receive
409 and must not overwrite newer edits. Preview and publication save outstanding
edits first. Browser publication resubmission runs on a later event-loop task to
avoid ignored reentrant form submissions. Without JavaScript, save the main draft
before using its separate upload form. Autosave requires connectivity; the install
manifest does not provide offline draft durability.

`PublicationLock` serializes draft/media/settings/publication and export/backup
mutations. Call `BuildLockedAsync` only while holding it; reacquiring the same
semaphore would deadlock. Public traffic continues reading static files.

Stage builds in `site/builds/{id}` and activate by atomically replacing
`site/current.json` on the same volume. The manifest records activated snapshots,
revisions, settings, branding, and an exact public file allowlist. It determines
what is actually live; SQLite may be ahead after failure. Never modify an active
generation in place or query SQLite to serve public pages. `ActiveSite` coordinates
file opening and generation removal so responses can finish.

Failed staging/activation keeps the previous complete site. Failed unpublish/delete
may leave an old page public; status must disclose that. Every successful rebuild
applies all saved desired changes, including earlier failed removals/settings.
Pointer activation stays authoritative even if the final database build-record
write fails; startup reconciles interrupted records. Damaged active output fails
readiness and offers an explicit rebuild. Retain three successful generations.

`ArticleSnapshot` and `GenerationManifest.Articles` carry all five types. Preserve
legacy JSON compatibility: absent type means article and additive fields have
defaults. Legacy tag maps resolve through stored tags so colliding names such as
C++/C# retain distinct URLs.

Every type uses `/posts/{slug}/`. A SQLite trigger in the initial migration locks
the first-published slug/timestamp; recreate it explicitly if a migration rebuilds
`Posts`. Persist timestamps in UTC, converting only for display. Feeds sort by
first publication with an ID tie-breaker; updates keep the original date/URL.

Incomplete drafts are valid; publish requirements differ by type: notes need text
or a photo, articles title/body, reviews subject/text and integer rating 1–5,
guides title and instructions in every included step, galleries a photo. Draft
ratings may be null. Untitled notes/galleries have date/ID fallbacks. Media/step
IDs must belong to the post. Preserve step IDs and transactional reordering that
respects unique sort constraints; removing a guide step moves photos to its intro.

## Privacy, security, and configuration

Keep SQLite, originals, keys, temporary ZIPs, and backups in private data outside
`wwwroot`. Anonymous generated-site serving allows only active-manifest files;
copy only snapshot-referenced post/branding assets into each generation. Detached
or replaced originals stay private for editing/backup.

The database enforces exactly one Identity administrator. Create it solely through
secret-protected setup, which closes afterwards. Require authentication for all
admin/preview/media/download routes and antiforgery for writes; persist Data
Protection keys. Preview shares the public renderer with authenticated media and
no-store responses, writes no public files, and allows same-origin framing only.

Disable raw Markdown HTML, restrict link protocols, encode structured text, and
resolve server-owned media IDs/paths. Validate matching JPEG/PNG/WebP extension,
MIME, and detected format before image identification/decoding; decode to reject
corruption. Preserve original bytes; limits are 20 MB/40 MP, one decoded frame,
and skipped metadata. Gallery batches allow five files. HEIC/HEIF currently needs
explicit JPEG/Most Compatible fallback guidance. Validate archive/public paths
and uploaded filenames against traversal; uploads use server-owned filenames.

`HOMESTEAD_BASE_URL` is required/validated but seeds only initial settings. Saved
`CanonicalBaseUrl` is authoritative across restarts/rebuilds; generate absolute
metadata from it rather than request Host headers. Site-root HTTP(S) URLs exclude
credentials/query/fragment; production requires HTTPS. Trust forwarded headers
only from `HOMESTEAD_TRUSTED_PROXIES`; blanket framework proxy trust is disabled.

ImageSharp 3.1.12 has five scoped audit suppressions in `Directory.Build.props`.
The recorded rationale is skipped metadata (ICC), unused histogram processing/TIFF
encoding, and TIFF/BigTIFF rejection before decoding. These are reachability
assumptions, not a globally fixed dependency. Keep other audits enabled; reassess
before adding metadata parsing, image processing, or formats. Dependency/licensing
follow-up and the advisory IDs are in [#8](https://github.com/PoshTools/homestead/issues/8).

## Portability and verification

Static ZIPs contain only the active allowlist even after a failed desired build,
and must serve independently of Homestead. Hugo ZIPs include all nondeleted posts,
with drafts/unpublished work marked `draft: true`; pending edits get a separate
private working bundle beside the published bundle. Preserve stable IDs/URLs,
tags, structured fields, ordering, captions, and rewritten local media references.
Ship layouts that actually build all five types. Deleted posts are omitted even
if a failed deletion left an old page active on Homestead.

Backups capture SQLite through its online backup API plus every recorded original
and the key ring under the shared mutation lock. Generated output is excluded.
Restore offline with the same application version into an empty directory;
validate manifest/version/paths/checksums/SQLite before installing. Backups/Hugo
ZIPs contain private data. Environment/proxy secrets and images are separate.
Never downgrade a migrated database in place.

Tests use real migrated SQLite files and isolated temporary data via
`TestApplication`; use these fixtures rather than EF's in-memory provider. Select
regressions for affected domain/auth/settings, five-type lifecycle, draft privacy,
snapshots/upgrades, generation/recovery failure, or portability. Check desired vs.
active state as well as success. Use contributing's build/test/format/model checks
and report only checks actually run, migration/config impact, and remaining issues.

Hugo 0.152.2 is required by portability tests. `HOMESTEAD_HUGO` overrides its binary
path; `HOMESTEAD_FIXTURE_OUTPUT` retains ZIP fixtures. Linux CI builds the container
and runs `tests/smoke/docker-smoke.mjs` with disposable volumes, Caddy TLS,
independent static hosting, and fresh-volume restore. Use isolated test data rather
than a developer's persistent installation.

For CSS changes, keep `Styles/site.css` source discovery and Docker CSS-stage
inputs in sync: `source(none)` requires explicitly scanned sources for new classes.
Commit exact package versions/lockfile updates. Follow `.editorconfig` (UTF-8, LF,
file-scoped C# namespaces); build warnings are errors.

Abrupt termination may leave private `backups/download-*.zip` or `sqlite-*.db`.
If cleanup is needed, stop the app and remove only those temporary files.
`docker compose down --volumes` destroys content; use it only for disposable tests.
