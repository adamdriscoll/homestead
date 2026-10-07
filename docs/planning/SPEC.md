# Homestead v2 — Product Specification

**Status:** Baseline for implementation  
**Version:** 0.1 (MVP scope)  
**Last updated:** 2026-10-07  
**Owner:** Single-author, self-hosted open-source project  
**Companion documents:** [Architecture](ARCHITECTURE.md) · [Implementation plan](IMPLEMENTATION.md)

## 1. Purpose

Homestead is a lightweight personal publishing platform: **your home on the internet**. An individual can post a thought, article, review, how-to guide, or photo gallery from a phone or computer without surrendering ownership of the material or designing a website.

Homestead is **not** a general CMS, website builder, social network, or SaaS management platform. It is an opinionated, five-format blog with excellent single-person authoring, portable content, and a public site that consists of static files.

### Product promise

> Publishing should be as easy as social media, with the ownership and permanence of a personal website.

### Success criteria

- One person can deploy a functional instance using Docker Compose on an inexpensive Linux VM.
- That person can publish each of five predefined content types from desktop and mobile.
- Public visitors receive generated HTML, CSS, and media; no database-backed page rendering is needed for public traffic.
- RSS, social previews, and canonical URLs work without extra services.
- The author can download a standalone static website and a Hugo-compatible Markdown export.
- Moving the application to a new server requires only a backup restore, not a vendor migration.

## 2. Decisions that are closed for the MVP

| Topic | Decision |
|---|---|
| Runtime | ASP.NET Core on **.NET 10 LTS** |
| Database | EF Core with SQLite |
| Frontend | Razor Pages for administration; server-rendered Razor templates for public HTML |
| Styling | Tailwind CSS; minimal plain JavaScript |
| Public rendering | **Full static site regeneration on every successful publish/update/unpublish or public settings change** |
| Appearance | **One built-in theme** with a few branding fields |
| Editor | **Simple formatting toolbar over Markdown-backed text editing** |
| Accounts | **Exactly one administrator**; no visitor accounts |
| Content types | **Microblog, Article, Review, Guide, Gallery** — fixed in code |
| Media | Files on a persistent local volume |
| Hosting | One ASP.NET Core application/container; Caddy reverse proxy in deployment sample |
| Exports | Static website ZIP; Hugo-compatible ZIP; full backup/restore |

An agent must not revisit these decisions unless a blocking implementation issue is demonstrated and documented.

## 3. Target user and core journeys

**Primary user:** a technically capable individual who wants the convenience of a hosted blogging product but prefers a personal domain, self-hosting, and the ability to leave at any time. Authoring must not *require* technical knowledge.

### Journey A — Share a thought

1. Open Homestead on phone or desktop and sign in.
2. Choose **Microblog** and type or paste text, a URL, and/or photos.
3. Optionally add tags. Click **Publish**.
4. Receive a stable post URL that displays correctly in messaging apps and social networks.

### Journey B — Publish a long article

1. Choose **Article**, enter a title, write with a simple formatting toolbar, and add images.
2. Edits save automatically as a draft; preview matches the public layout.
3. Publish and verify the article appears on the chronological home feed, the article listing, and RSS.
4. Later edits remain unpublished until **Update published post** is explicitly chosen.

### Journey C — Move away from Homestead

1. Choose **Export static website** for a self-contained public website; or **Export for Hugo** for Markdown-based migration.
2. Download the archive with associated media.
3. Deploy or build the export without a running Homestead server or access to its database.

## 4. Five predefined post types

Every type is a post: it shares identity, URL, creation/publication timestamps, tags, public visibility, social metadata, and inclusion in chronological feeds. Editors and public layouts differ only where useful.

| Type | Publish-time requirements | Optional data | Rendering behavior |
|---|---|---|---|
| **Microblog** | At least some text or one image | Optional title, tags, multiple images | Compact feed card and individual permalink |
| **Article** | Nonblank title and body | Cover image, summary, tags, inline images | Editorial long-form layout |
| **Review** | Subject name, integer rating 1–5, review text | External URL, photos, tags | Rating and subject emphasized; no proprietary place database |
| **Guide** | Title and one or more steps, each with instructions | Introduction, step images, tags | Ordered steps; optional images in each step |
| **Gallery** | One or more images | Title, description, captions, tags | Responsive image grid with image captions |

**Constraints:**

- Types cannot be added or modified by administrators. No custom fields, dynamic schemas, or plugins.
- Each type has a focused editor instead of one universal block designer.
- Text content, including guide steps and image captions, is stored as Markdown or plain text as appropriate; specialized fields remain structured data.
- Posts may be created as drafts even before publish-time required fields are complete. Validation is stricter on publication.
- The review's subject is free text, not a third-party lookup. A review does not imply automatic schema.org ratings eligibility.
- Gallery images have author-editable alt text and captions; missing alt text is surfaced to the author before publication.

## 5. Publication states and lifecycle

The UI presents **Draft**, **Published**, and **Unpublished** states. In addition, published posts may have **unpublished edits**.

- **Draft:** Never publicly released; editable and previewable after login.
- **Published:** Has a committed public snapshot and a currently generated public page.
- **Published with edits:** Public visitors see the last successfully published snapshot until the author selects **Update**.
- **Unpublished:** Removed from listings, RSS, sitemap, and generated permalink once a new build successfully activates; the author may continue editing it.
- **Delete:** Requires confirmation; removes the post from future builds. Its old URL returns 404 (no automatic redirect).

The author must never see a successful publish/unpublish message before the static build has become active. If generation fails, the last working public site remains available and the admin UI explicitly states whether the old post/page is still public. Draft content must not leak into static outputs.

**MVP does not include** scheduled publishing, complicated revision history, content approval, or a trash/recovery UI. Stable drafts and a last-published snapshot are necessary; a complete version history is not.

## 6. Public site information architecture

| Path | Content |
|---|---|
| `/` | Home feed: all published types, newest first |
| `/posts/{slug}/` | Permalink for any post, independent of type |
| `/notes/` | Microblogs |
| `/articles/` | Articles |
| `/reviews/` | Reviews |
| `/guides/` | Guides |
| `/galleries/` | Galleries |
| `/tags/{slug}/` | Filtered post list |
| `/about/` | About page from site settings |
| `/rss.xml` | RSS 2.0 feed, newest published items first |
| `/sitemap.xml` | Public page URLs |
| `/robots.txt` | Crawling directions and sitemap reference |

Listing/feed pagination can be added in the MVP if needed for an ordinary content volume; do not introduce an infinite-scroll API. Static pages must have ordinary navigable links. The initial layout should work without client-side JavaScript.

**Permalinks:** Generated from a title (or a concise date/ID fallback for untitled posts), made unique, and immutable after first publication. Reserve system paths (`admin`, `setup`, `health`, etc.). There is no category prefix in post URLs.

**Home feed:** Each type has a recognizable card. A mixed chronological feed is the main experience. Type-specific listings use the same content source with type filters.

## 7. Appearance and customization

Ship **one built-in, responsive theme**. Design language: warm, personal, approachable, restrained. The historical **Porch Light** direction can inspire colors and tone, but there is no theme picker or theming engine.

Allowed settings:

- Site name and short tagline/description.
- Author display name, short bio, and avatar.
- Optional site logo/wordmark and header image.
- Accent color from a fixed, accessible preset palette.
- About-page Markdown; footer text; a short list of external profile links.
- Primary canonical site URL and time zone for display.

System-preference light/dark appearance is supported within the one theme. No arbitrary layouts, user CSS, custom HTML injections, navigation editor, configurable templates, theme marketplace, or settings-driven modules.

## 8. Authoring experience

### Required screens

1. **Sign in / first-run setup:** Create one administrator with a safe first-run bootstrap mechanism.
2. **Dashboard:** Primary create actions for each type, recent drafts and posts, build status.
3. **Post list:** Draft/published/unpublished filters, type, title/summary, edit link, publish state.
4. **Type-specific editors:** Shared controls plus specialized fields for rating, steps, or images.
5. **Preview:** Authenticated, uses the public rendering rules; never writes a public page.
6. **Settings and maintenance:** Identity and branding, site URL, rebuild, exports, backup/restore help.

### Editor interaction

- Plain text editing with **Bold, Italic, Heading, Link, List, Quote, Code, Image** formatting actions.
- Formatting inserts valid Markdown around selected text; the stored representation is Markdown.
- Clear text preview, keyboard-friendly controls, visible autosave indicator, explicit Publish/Update action.
- Debounced autosave when online, with explicit Save action and meaningful save-error feedback. Do **not** claim offline draft durability in the MVP.
- Paste and drag-and-drop uploads on desktop; photo-library/file-picker uploads on mobile.
- Responsive touch targets; a PWA manifest and installable shell where supported.
- No full-page JavaScript SPA, drag-and-drop page builder, embedded rich-text document editor, or collaborative editing.

**Media:** Upload JPEG, PNG, and WebP at minimum; validate MIME signatures and file size. Mobile acceptance testing must explicitly cover contemporary phone photo formats (including HEIC/HEIF). Either support conversion with a documented implementation or provide a clear fallback; no silent failures. Allow author-entered alt text. Preserve originals in private media storage; generated site receives only published assets.

## 9. Sharing, feeds, discoverability

Required on initial launch:

- A working RSS 2.0 feed (`/rss.xml`) with consistent GUID/permalink, title or sensible untitled fallback, date, and text/content; discoverable via HTML `<link rel="alternate">`.
- Per-page canonical URL and Open Graph (`og:title`, `og:description`, `og:url`, `og:image`, `og:type`) metadata.
- Twitter/X summary card metadata, falling back to a site-wide image when a post has no public image.
- A static default social preview image and a meaningful per-post title/description. **Dynamic image composition is not required.**
- XML sitemap and `robots.txt` generated from the **published public state only**.
- Human-readable semantic HTML, responsive images where implemented, reasonable performance and accessibility.

Sharing means sharing URLs. There is no social account connection, posting API, comment system, reaction system, social graph, ActivityPub, or cross-platform synchronization.

## 10. Export, backup, and portability

These are separate functions, not interchangeable:

### A. Static website export

- ZIP contains the entire active public site: HTML, styles, images, sitemap, RSS, and `robots.txt`.
- Links and asset paths work when deployed at the configured site's root with the same canonical domain or a newly configured domain followed by rebuild.
- No ASP.NET Core runtime, database, or extra JavaScript build tooling required to serve.
- Drafts and unpublished edits are never included.

### B. Hugo export

- ZIP includes a usable Hugo project (`hugo.toml`, `content/`, and a small working layout/theme).
- Each post uses a **leaf bundle**: `content/posts/{slug}/index.md` with associated media.
- Front matter includes `title`, `date`, `lastmod`, `draft`, `tags`, `type`, `url`, and stable Homestead ID (for traceability).
- Review rating/subject, guide steps, and gallery image order/captions are preserved as documented front matter and/or readable Markdown. The included Hugo layouts render them.
- Include **all nondeleted posts**, including drafts marked `draft: true`; build defaults omit drafts.
- A fresh supported Hugo CLI can build the exported project without accessing Homestead.

### C. Full backup and restore

- Consistent SQLite backup plus **original** media, configuration needed for content, and documented restoration steps.
- Never substitute the generated static site for the authoritative content backup.
- Authentication secrets/keys and credentials are sensitive; backup documentation explains handling and restore implications.
- Restore verified on a fresh installation of the same app version (version upgrades are a separate concern).

## 11. Deployment and operations

- Distributed as one ASP.NET Core container with a persistent `/data` volume. Compose example optionally starts Caddy for HTTPS.
- Runtime dependencies: SQLite filesystem and .NET runtime contained in the application image. **No Redis, queue, cloud database, object storage, or hosted SaaS account.**
- First-run bootstrap securely creates one admin; no open signup endpoint after setup.
- Credentials and bootstrap secret must not be checked into git.
- Provide health/readiness endpoint(s), concise logs, basic Docker documentation, and persistent ASP.NET Core Data Protection keys.
- Provide a reproducible backup/restore procedure and a manual **Rebuild site** command.
- The instance is usable on a single Droplet; no cluster/high-availability design in the MVP.

## 12. Security and privacy requirements

- All authoring and preview endpoints require authentication. No public registration, comments, or visitor accounts.
- Protect all state-changing admin operations against CSRF; use secure, HttpOnly, SameSite cookies and HTTPS behind the proxy.
- Treat author-entered Markdown, links, filenames, and upload metadata as untrusted; disable raw HTML or sanitize to a narrowly documented allowlist.
- Validate media signatures, extensions, sizes, and paths; avoid path traversal and SVG/script execution through uploads.
- Keep private originals, drafts, database, and backup files **outside** the static-public root.
- Protect setup with an explicit bootstrap secret or CLI-based initialization (not first-visitor ownership).
- Never expose a partially built site; failed publication must preserve the prior active generation.

## 13. Non-goals and anti-scope-creep rules

The MVP must **not** include:

- Custom post types, custom fields, arbitrary schemas, configurable page layouts, or plugin SDKs.
- Multiple authors, invites, visitor sign-in, permissions models, or organizations.
- Comments, reactions, followers, federation, webrings, or messaging.
- Social network backup/import, one-way synchronization, or two-way synchronization.
- Hosted multi-tenant SaaS, billing, domain sales, or third-party deployment integrations.
- AI writing assistants, agent orchestration, MCP authoring, or AI content generation.
- Advanced analytics, full-text search, scheduled posts, sophisticated revisions, or native mobile apps.
- Automated push to GitHub Pages/Cloudflare Pages (manual export is sufficient).

An enhancement proposal must cite a real MVP acceptance criterion it enables, or be deferred until after v0.1.

## 14. MVP definition of done

The release is complete when all are demonstrated on a fresh instance:

1. Docker Compose deployment with persisted SQLite, media, and encryption keys.
2. Secure single-admin setup and authentication; no guest authoring access.
3. Create, save, preview, publish, update, unpublish, and delete every post type.
4. Public pages, home feed, type listings, tags, About, RSS, sitemap, and social tags are regenerated as static output.
5. New edits to published content are not public until a successful Update.
6. Failed builds do not replace working public output; failures are visible in administration.
7. Mobile workflow can publish an image post and a text post successfully.
8. One built-in theme with branding settings and accessible light/dark appearance.
9. Static ZIP serves independently of Homestead.
10. Hugo ZIP builds using the supported Hugo CLI and faithfully represents all five post types.
11. Documented backup/restore works on a clean instance; automated security and lifecycle tests pass.

## 15. Deferred decisions (do not block implementation)

- Whether to add optional pagination above the initial site-size threshold.
- Whether to support native OS share-sheet inbound sharing where browsers permit.
- Which optional image conversion library can cover HEIC/HEIF while keeping distribution simple.
- Whether to introduce post redirects after a future slug-edit feature.
- Whether hosted publishing is ever worth offering; it is explicitly outside this version.

These should be captured as issues, not implemented speculatively.
