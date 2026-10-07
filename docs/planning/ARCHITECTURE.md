# Homestead v2 — Technical Architecture

**Status:** Implementation baseline  
**Version:** 0.1  
**Last updated:** 2026-10-07  
**Companion documents:** [Product specification](SPEC.md) · [Implementation plan](IMPLEMENTATION.md)

## 1. Architecture at a glance

Homestead is a **single deployable ASP.NET Core application** that authors content in SQLite and **materializes a static website** from that content. No database query or dynamic request handler is needed to render public post pages.

```text
                      AUTHOR (authenticated)
                        phone / desktop
                               |
                         ASP.NET Core
                 Razor Pages + small JS + APIs
                     /          |           \
             SQLite/EF      Media store     Admin preview
             /data/*.db     /data/media     (authenticated)
                     \          |           /
                      Publishing service
                Markdown + Razor -> static HTML
                               |
                   /data/site/builds/{id}/
                         generation output
                               |
                 atomic active-generation switch
                               |
      PUBLIC VISITOR -> ASP.NET static-file serving -> HTML/CSS/media

                EXPORT -> static ZIP / Hugo ZIP
                BACKUP -> SQLite + original media
```

Use a **modular monolith**: separate services/namespaces for content editing, publishing, media, exporting, and settings, but one web process and one application database. Resist additional projects until there is a demonstrated boundary that needs it.

## 2. Stack and dependencies

| Concern | Technology | Rationale |
|---|---|---|
| Framework | .NET 10 / ASP.NET Core | LTS, cross-platform, minimal deployment |
| Admin UI | Razor Pages | Forms and authentication without a SPA |
| Public templates | Razor components rendered to HTML with `HtmlRenderer` | One server-side renderer for generated pages |
| Data access | EF Core 10 + SQLite | Straightforward single-user persistence |
| Auth | ASP.NET Core Identity with one `ApplicationUser` | Secure login without an identity service |
| Markdown | Established .NET CommonMark-compatible library (e.g. Markdig) | Storage and rendering; disable untrusted raw HTML |
| Styling | Tailwind CSS at build time | Small consistent site; generated CSS shipped with app |
| Client scripts | Plain JavaScript | Autosave, formatting toolbar, file upload, preview |
| Reverse proxy | Caddy (sample Compose deployment) | HTTPS and straightforward configuration |
| Media | Persistent filesystem | No object-storage service required |
| Exports | .NET ZIP APIs and simple Hugo project templates | Runtime independence |

Pin exact package versions in central package management or explicit project references. No Node runtime is required in production; a Node-based Tailwind build step is acceptable at build time. Avoid Blazor interactive hosting, a separate frontend application, Redis, SignalR, distributed background workers, or domain event infrastructure.

### Official platform references

- Razor rendering without an HTTP request: <https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-components-outside-of-aspnetcore?view=aspnetcore-10.0>
- ASP.NET Core static-file middleware: <https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-10.0>
- EF Core SQLite provider: <https://learn.microsoft.com/en-us/ef/core/providers/sqlite/>
- SQLite provider limitations: <https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations>
- Hugo leaf bundles: <https://gohugo.io/content-management/page-bundles/>

## 3. Suggested repository layout

```text
homestead/
  SPEC.md
  ARCHITECTURE.md
  IMPLEMENTATION.md
  README.md
  Homestead.slnx
  src/
    Homestead/
      Homestead.csproj
      Program.cs
      Data/
        HomesteadDbContext.cs
        Entities/
        Migrations/
      Features/
        Posts/              # commands, validators, edit models
        Media/              # upload, private originals, metadata
        Publishing/         # public snapshot, builder, generation switch
        Export/             # static/Hugo/backup
        Settings/
        Authentication/
      Pages/
        Admin/              # Razor Pages + page models
        Setup/
        Account/
      Public/               # noninteractive Razor components/templates
        Layouts/
        Cards/
        Posts/
      Infrastructure/
        Storage/
        Security/
      wwwroot/              # compiled admin assets only, NOT drafts
  tests/
    Homestead.Tests/       # domain, integration, publishing, security tests
  deploy/
    Dockerfile
    compose.yaml
    Caddyfile
    .env.example
  docs/
    deploy.md
    backup-restore.md
    contributing.md
```

There is **one** main application project; test organization can be expanded later. Deploy static generation output on a persistent volume, not in the container image.

## 4. Data and ownership model

### Primary entities

| Entity | Fields / important relationships | Notes |
|---|---|---|
| `Post` | `Id`, `Type`, `Status`, `Slug`, `CreatedAtUtc`, `UpdatedAtUtc`, `FirstPublishedAtUtc?`, `LastPublishedAtUtc?` | Stable identity; slug locked after first publication |
| `PostDraft` (1:1) | `PostId`, `Title?`, `BodyMarkdown`, `Summary?`, draft metadata | Editable working copy; includes type-specific fields or relations |
| `PostPublishedSnapshot` (0:1) | `PostId`, `SchemaVersion`, `SnapshotJson`, `PublishedAtUtc`, `IsActive` | Validated fixed-schema DTO for most recently **requested** public state; only activated generation defines what visitors currently see |
| `PostMedia` | `PostId`, `MediaAssetId`, `Role`, `SortOrder`, `Caption?`, `AltText?`, `GuideStepId?` | Ordered relationships, not duplicate files |
| `MediaAsset` | `Id`, `StoredFilename`, `OriginalFilename`, `MimeType`, `Size`, dimensions, upload time | Immutable original in private storage |
| `ReviewDetails` | `PostId`, `Subject`, `Rating` (1–5), `ExternalUrl?` | Draft-side specialization |
| `GuideStep` | `Id`, `PostId`, `Order`, `BodyMarkdown` | Draft-side ordered steps |
| `Tag` + `PostTag` | `Id`, `Name`, `Slug`, join | Case-insensitive normalized uniqueness |
| `SiteSettings` | singleton; title, domain, author, branding, About, timezone | Settings updates build new public generation |
| `SiteGeneration` | `Id`, `BuiltAtUtc`, `ActivatedAtUtc?`, output root, result/status | Operational build records |
| `ApplicationUser` | Identity fields | Enforce single-account creation |

**Guidance:** The immutable published snapshot contains a **typed, versioned serialized representation** of all publishable content (shared fields, ordered media, review fields, guide steps, tags). This is a fixed format controlled by code, **not** a user-defined content schema. Preserve structured relational entities for editing as helpful; publishing reads the stable snapshot so autosaving work-in-progress cannot leak into future rebuilds.

Use `DateTime` normalized to UTC in SQLite and convert to the configured display timezone only at the UI boundary. Database constraints/indices should include unique slug, media IDs, tag slugs, and foreign keys. Never trust client-supplied publication timestamps or file paths.

### Example published snapshot DTO (illustrative)

```csharp
public sealed record PublishedPostSnapshot(
    int SchemaVersion,
    Guid PostId,
    PostType Type,
    string Slug,
    string? Title,
    string BodyMarkdown,
    string? Summary,
    DateTime PublishedAtUtc,
    DateTime LastModifiedAtUtc,
    IReadOnlyList<string> Tags,
    IReadOnlyList<PublishedMedia> Media,
    ReviewSnapshot? Review,
    IReadOnlyList<GuideStepSnapshot> Steps);
```

`PublishedMedia` contains **IDs** and safe captions/alt text; actual file paths are resolved by the media service. Public serialization must never embed absolute `/data` paths.

### Publish transaction and consistency

A full database + filesystem commit is not atomic, so explicitly handle the gap:

1. Serialize publication requests through a single in-process publication lock. A single-instance deployment is a requirement.
2. Validate draft and create/update the stored `PostPublishedSnapshot` (or mark inactive for unpublish), committing the intended public state to SQLite.
3. Build from those stored snapshots into an isolated **new generation**. Only committed snapshots are eligible, never draft tables.
4. Validate generated output; **activate** the completed generation via an atomic active-generation pointer replacement.
5. Mark the generation successful and report completion to the user.
6. If any build or activation step fails, keep the previous generation serving. Show the failure and **accurate active-public status** in admin, with **Retry/Rebuild** available.

A saved snapshot may temporarily be ahead of the active site if a build fails or a process crashes. That is acceptable **only if** the UI exposes the discrepancy and the public files remain correct for the active generation. On restart, reconcile the recorded desired state with the active-generation manifest and make a failed/pending rebuild visible. Do not report an unpublish as complete if its old page remains in the active generation.

**No partial activation.** Do not write or modify files underneath the active generation.

## 5. Site generation pipeline

```text
Acquire publish lock
  -> Read published snapshots + public settings
  -> Create /data/site/builds/{new-id}/
  -> Render content into HTML (Razor components)
  -> Render home feed, type listings, tags, about
  -> Copy published media, CSS, default social image
  -> Build RSS, sitemap, robots.txt
  -> Validate required files, page links and metadata
  -> Replace /data/site/current.json atomically
  -> Retain limited previous builds for rollback
  -> Return active URL and generation ID
```

### Output example

```text
/data/site/
  current.json               # { "generationId": "..." }; atomically replaced
  builds/
    {generationId}/
      index.html
      articles/index.html
      notes/index.html
      reviews/index.html
      guides/index.html
      galleries/index.html
      tags/{slug}/index.html
      posts/{slug}/index.html
      about/index.html
      rss.xml
      sitemap.xml
      robots.txt
      assets/site.css
      assets/default-social.png
      media/{mediaId}/{filename}
```

**Serving strategy:** Reserve `/admin`, `/account`, `/setup`, `/health` (and internal API paths) for dynamic handling. For remaining public requests, use a read-only static-file handler or dynamic `IFileProvider` that resolves each request against the **currently active generation**; do not configure a static provider that forever points to an old generation. Validate normalized requested paths and appropriate content types. `UseStaticFiles` is suitable for runtime-generated files; `MapStaticAssets` build-time manifests are not sufficient for newly published files. The active generation pointer is loaded at startup and updated atomically on successful publication. A public 404 has no fallback to the admin shell.

### Content and asset rules

- Public URLs are `/posts/{slug}/`, independent of type, including for untitled posts.
- Requests with a trailing slash map to `index.html`; static export uses directory indexes. Preserve canonical trailing slashes.
- HTML gets conservative/no-cache headers to make publication visible quickly. Fingerprinted/revision-safe CSS and media may use longer caching.
- All absolute metadata URLs use `SiteSettings.CanonicalBaseUrl`, not the incoming host header.
- All published assets are copied into that generation only when referenced by a published snapshot.
- Avoid calling the database during public site requests.
- Rebuild all pages on each change. Incremental build optimization is explicitly deferred.
- Render a valid empty-site home page immediately after setup.

### Shared rendering

Use a small set of Razor components (`SiteLayout`, `PostCard`, `ArticlePage`, `MicroblogPage`, `ReviewPage`, `GuidePage`, `GalleryPage`, `ListingPage`). Render complete HTML documents, including `<head>` metadata, through a dedicated `IStaticPageRenderer` wrapping `HtmlRenderer` correctly on its dispatcher. A preview controller loads a draft view model and invokes the same rendering components **without writing** anything into the public generation.

Use the same Markdown-to-HTML service for public builds and previews; raw Markdown HTML is disabled by default. Sanitize `href` and image URL schemes; do not accept arbitrary script/iframe content.

## 6. Editor and admin application

### Routes

```text
/setup                # only before setup completion + bootstrap secret
/account/login        # login
/account/logout       # logout POST
/admin                # dashboard
/admin/posts          # filter/list
/admin/posts/new/{type}
/admin/posts/{id}/edit
/admin/posts/{id}/preview
/admin/settings
/admin/maintenance    # build status, rebuild, exports, backups
/admin/media/{id}     # authenticated draft-media retrieval
/health/live          # minimal liveness; no sensitive content
```

All routes under `/admin` are authenticated. State-changing operations use antiforgery validation. API handlers for autosave/upload also enforce authenticated identity, upload limits, and same-origin behavior.

### Draft editing

- Toolbar manipulates selected ranges in a textarea and inserts Markdown tokens; no WYSIWYG block model.
- Debounced autosave uses an explicit draft version/concurrency token to detect cross-tab overwrites; expose a clear conflict message instead of losing edits silently.
- Preview is no-store and authenticated; source links to uploaded media use an authenticated media route.
- On a published post, autosave updates only draft state; **Update published post** creates a new snapshot and rebuilds.
- Validate type-specific fields at publish time, not on every incomplete draft autosave.

### Upload path strategy

Original uploads reside at `/data/media/originals/{mediaId}/{safeFilename}` and are inaccessible to public static middleware. An editor may insert a canonical Markdown reference such as `![Alt text](/media/{mediaId}/{safeFilename})`; in authenticated preview, a resolver maps it to a protected admin endpoint. At publication, the builder copies referenced media to the same `/media/{mediaId}/...` relative path under that generation. Hugo export rewrites references to colocated bundle asset filenames. Enforce server-owned media IDs and safe file extensions; never trust the path submitted by the browser.

**Note:** Public asset path references in Markdown are an application convention; the **Hugo export must rewrite them**, preserving portability. Keep the media resolver in one service so future path changes do not require editing every template.

## 7. RSS, search metadata, and SEO

Create these files during every public build:

- `/rss.xml`: RSS 2.0 for all published types, ordered by public publish time; use stable GUID based on permalink or post ID. Use XML encoding and a deterministic item limit (e.g. latest 50), documented in config/code, not an admin setting.
- `/sitemap.xml`: home, listings, About, published post pages, and public tags, with canonical absolute URLs.
- `/robots.txt`: ordinary indexing and sitemap path.
- Per-page `<link rel="canonical">`, Open Graph fields, Twitter/X card fields, and the RSS discovery link.

A post's first public photo is the share image; otherwise use the fixed default social image. Don't add an image-generation service. Never include drafts, unpublishes, admin URLs, or private media in public metadata.

## 8. Export contracts

### Static site ZIP

Package the active generation. Include the directory structure above and a small README explaining that the site assumes deployment at the configured domain/root path. It must serve with a simple static HTTP server and produce no ASP.NET calls.

### Hugo ZIP

```text
hugo.toml
content/
  about/index.md
  posts/
    {slug}/
      index.md
      cover.jpg
      other-media.jpg
themes/homestead/
  layouts/...
  static/...            # stylesheet + fallback image
README.md
```

Each post is a Hugo **leaf bundle**, with front matter similar to:

```yaml
---
title: "A weekend in the woods"
date: 2026-10-07T15:00:00Z
lastmod: 2026-10-07T15:30:00Z
draft: false
type: "gallery"
url: "/posts/weekend-in-the-woods/"
tags: ["hiking"]
homestead_id: "00000000-0000-0000-0000-000000000001"
gallery:
  - file: "lake.jpg"
    caption: "Morning light"
    alt: "A lake at sunrise"
---
```

A review also uses documented `review: { subject, rating, external_url }` front matter; guides use an ordered `steps:` list containing Markdown text and image references. For maximum portability, include readable step/photograph content in the Markdown body as well, not only in theme-specific metadata. Drafts set `draft: true`; unpublished posts may be included with `draft: true` and a custom `homestead_unpublished: true` marker. Deleted posts are omitted.

**Acceptance:** Running the supported Hugo CLI's `hugo` command produces a browsable static site with functioning layouts for all five content types and no dependency on Homestead. Test expected URL paths and image resources; do not assume front matter alone creates a usable theme.

### Full backup

Use SQLite's online backup mechanism (or a controlled offline copy) to obtain a consistent database. Serialize backup with media mutations long enough to capture a consistent manifest of media records and originals; include a manifest with application version, date, checksums, and files. Back up `SiteSettings`; regenerated public HTML need not be included. Document separately how to handle encrypted authentication keys and secrets. Restoration occurs with the app stopped into an empty persistent volume; then validate and rebuild the site.

## 9. Filesystem layout and deployment

```text
/data/
  homestead.db
  homestead.db-wal         # may exist depending on SQLite mode
  homestead.db-shm
  media/originals/
  site/current.json
  site/builds/
  backups/
  keys/                   # ASP.NET Data Protection key ring
```

Use one persistent Docker volume or bind mount containing `/data`. Run the container as an unprivileged user with write access only where required; set suitable file ownership. Deploy via Compose with `homestead` and a Caddy reverse-proxy service. Caddy exposes 80/443, terminates TLS, and forwards to the application on a private network. Document direct reverse-proxy alternatives without expanding the deployment matrix.

### Configuration contract (initial)

| Setting | Required? | Description |
|---|---|---|
| `HOMESTEAD_BASE_URL` | Yes | Absolute canonical public URL, e.g. `https://example.com` |
| `HOMESTEAD_DATA_PATH` | Defaults `/data` | Persistent data directory |
| `HOMESTEAD_SETUP_TOKEN` | Initial setup only | One-time secret required to create administrator; remove after setup |
| `ASPNETCORE_ENVIRONMENT` | Defaults `Production` | Standard ASP.NET setting |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | Deployment-specific | Configure trusted proxy headers explicitly; don't trust arbitrary proxies |

Most ordinary author-facing settings belong in `SiteSettings`, not environment variables. Avoid adding configuration parameters without a real user requirement.

### Database lifecycle

Run EF Core migrations in a controlled **single-instance** startup/upgrade step and fail fast when they cannot be applied. Back up before upgrades. Do not run competing migrations from multiple replicas. Test SQLite schema rebuild and upgrade paths when changing tables; SQLite has material limitations for certain migration operations.

## 10. Security model

Threats to actively address:

1. **First-visitor takeover:** Require the setup secret (or secure CLI bootstrap); close setup after the first user exists.
2. **Private-content disclosure:** Drafts and originals never live under the public root; only active generation is reachable anonymously.
3. **Stored XSS:** Markdown is parsed with raw HTML disabled and safe URI rules; validate links, captions, tags, and template encoding.
4. **Malicious upload:** Allowlisted signatures and extensions, max size, image decoding checks, randomized server file IDs, no SVG, and safe image responses.
5. **CSRF/session theft:** Antiforgery on writes, appropriate cookie policy, login throttling/lockout, HTTPS.
6. **Path traversal:** Normalize and validate public paths, export entry names, upload filenames, and media references.
7. **Exposed backups/keys:** Keep `/data/backups` and `/data/keys` outside public serving and document restrictive permissions.
8. **Host header poisoning:** Generate public absolute URLs from configured canonical base URL.

Public static files intentionally have **no authorization**. Everything in a successful generation is public; keep the generator's published-only filter simple and extensively tested.

## 11. Reliability and failure behavior

| Failure | Expected behavior |
|---|---|
| Markdown/template render fails | Build fails; previous public generation remains active; admin shows error |
| Insufficient disk during build | Old generation remains; new staging build cleaned up or flagged for cleanup |
| Publish request retried | Idempotent or safely repeated without duplicate posts or corrupted output |
| Multiple simultaneous publish clicks | Serialized; one active generation at a time |
| Crash during build | Ignore incomplete build on startup; continue to serve manifest's last active generation |
| Crash after activation | Recover active generation from `current.json`; reconcile any incomplete DB build status |
| Missing/corrupt active generation | Report readiness failure; do not silently serve draft files; admin provides rebuild path |
| Unpublish build fails | Old public page may remain visible; disclose that clearly until successful activation |
| Rebuild after settings change | Re-render all metadata and links using current canonical URL |

Keep a small bounded number of previous generations (e.g. one or two) for quick recovery, with cleanup after successful activation. The retention count is a code constant, not a settings panel.

## 12. Verification strategy

### Unit tests

- Publish-time validation for all five types.
- Immutable slug generation and collision handling.
- Markdown formatting and safe HTML output.
- Front-matter serialization and export path sanitization.
- Public/private media reference resolution.

### Integration tests

- Fresh SQLite migration and single-user bootstrap.
- Authenticated administration; anonymous draft/backup/media access rejected.
- Draft autosave never affects active public output.
- Publish/update/unpublish/delete rebuild expected files and feeds.
- Failed build leaves `current.json` unchanged and old HTML readable.
- Concurrent publish commands produce only complete active generations.
- RSS validates as XML; canonical/OG metadata uses correct base URL.
- Static export works with ASP.NET Core stopped.
- Hugo ZIP builds with the selected supported Hugo binary in CI.
- Backup/restore round trip preserves text and original photos.

### Manual acceptance on real devices

- One desktop browser and one phone browser, including camera-roll photo upload.
- Keyboard navigation and contrast in light/dark modes.
- Share a generated permalink to a service that supports social previews.
- Upgrade and restart the container without losing SQLite, media, or login sessions.

## 13. Architecture decision records

| ADR | Decision | Rationale | Revisit only if |
|---|---|---|---|
| ADR-001 | Full static generation on publish | Simplest public serving and export | Builds become measurably impractical |
| ADR-002 | One predefined theme | Avoid building a website builder | A real user need emerges after MVP |
| ADR-003 | Markdown plus toolbar | Authoring simplicity and portability | Mobile authoring tests expose hard blockers |
| ADR-004 | SQLite and local media | One-container operation | Multi-writer/large-scale use becomes a requirement |
| ADR-005 | Razor Pages + static Razor rendering | Shared .NET stack, minimal JS | Renderer cannot satisfy documented content types |
| ADR-006 | One administrator | Focused security and UX | Product requirements explicitly expand |

## 14. Architectural non-goals

No multi-tenancy, plugin architecture, theme engine, external messaging/queues, distributed generation workers, social federation, headless CMS API, third-party sync, AI integration, or automatic static-host deployment. No abstraction should be introduced solely to anticipate these possibilities.
