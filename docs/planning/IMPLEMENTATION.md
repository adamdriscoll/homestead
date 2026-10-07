# Homestead v2 — Agent Implementation Plan

**Status:** Ready for incremental implementation  
**Version:** 0.1  
**Last updated:** 2026-10-07  
**Authoritative requirements:** [SPEC.md](SPEC.md)  
**Technical design:** [ARCHITECTURE.md](ARCHITECTURE.md)

## 1. Instructions for the coding agent

Build **Homestead v2**, an opinionated five-type, single-author publishing platform. **Do not build a generic CMS.** Read `SPEC.md` and `ARCHITECTURE.md` completely before beginning work.

### Nonnegotiable rules

1. Use **.NET 10, ASP.NET Core, EF Core SQLite**, Razor Pages for administration, and server-rendered Razor for static public pages.
2. **Generate public HTML on publication**, not per public visitor request; the entire active site must be replaceable as one complete generation.
3. Support **exactly five post types**: microblog, article, review, guide, gallery.
4. Provide **one theme with limited branding settings**, not theme selection or custom page design.
5. Use a **Markdown-backed editor with a formatting toolbar**, not a WYSIWYG page/block editor.
6. Implement **single-admin** authentication and protect all admin routes.
7. Keep drafts, original media, SQLite, backups, and keys outside the public static root.
8. No client SPA framework, external DB, Redis, job queue, cloud service, social API, AI, custom schema engine, multi-tenancy, or plugin system.
9. All features must work in a **single Linux container** with `/data` persisted; sample deployment uses Caddy as a proxy.
10. Do not begin later phases or speculative enhancements until the active phase's acceptance checks pass.

### Working style

- Work in small, reviewable tasks, ideally one PR per task (or closely related pair).
- Before implementation, state changed files, migration impact, tests to add, and any contradiction with the specs.
- Treat acceptance criteria as executable tests wherever practical.
- Prefer simple concrete types and code over generalized extension points.
- Keep admin JS short, progressively enhanced, and accessible; write HTML/CSS that degrades gracefully.
- When a library or behavior is uncertain, verify the supported .NET/Hugo API rather than guessing.
- For a risky design fork, write a short ADR or issue; **do not** silently widen project scope.
- Do not report a task done if the project does not build or its relevant tests fail.

### Definition of done for every task

- [ ] Code compiles and is formatted consistently.
- [ ] New functionality has automated tests at the appropriate level.
- [ ] Relevant security boundaries are tested, not merely assumed.
- [ ] Any EF schema changes include a working migration.
- [ ] Docs/config/examples are updated for observable behavior changes.
- [ ] No feature outside `SPEC.md` was added opportunistically.
- [ ] Task-specific acceptance checks below were demonstrated.

## 2. Suggested implementation order

```text
Phase 1     Foundation & deployable shell
  F01 -> F02 -> F03 -> F04 -> F05

Phase 2     Article vertical slice, end-to-end static publishing
  P01 -> P02 -> P03 -> P04 -> P05 -> P06

Phase 3     Public experience, microblog & discovery
  W01 -> W02 -> W03 -> W04 -> W05

Phase 4     Reviews, guides, galleries
  T01 -> T02 -> T03 -> T04

Phase 5     Export, operations, hardening
  E01 -> E02 -> E03 -> E04 -> E05 -> E06
```

Tasks can be parallelized only after required models/contracts have been agreed; publishing and security touch shared state and should be integrated serially.

---

## Phase 1 — Foundation and deployable application

**Goal:** A new instance deploys securely, retains its state, and has an authenticated dashboard. No public publishing is expected yet.

### F01 — Create the solution and baseline quality gates

**Implement:**

- `Homestead.slnx`, `src/Homestead`, `tests/Homestead.Tests`.
- ASP.NET Core app with Razor Pages, Tailwind build, and minimal JS setup.
- Basic integration test host, shared build settings, `.editorconfig`, `.gitignore`.
- `README.md` linking to the three design documents.

**Acceptance:** `dotnet restore`, `dotnet build`, and `dotnet test` run in CI; a homepage/admin shell can be served locally; no separate frontend application is needed.

### F02 — EF Core SQLite and initial domain model

**Implement:**

- `HomesteadDbContext`, entities and typed enums described in the architecture.
- Initial EF migration; SQLite file path from `/data` configuration.
- Unique slug/tag constraints, associations for tags/media/steps, timestamps normalized to UTC.
- Draft/public snapshot separation (published snapshot may be initially unused).

**Acceptance:** Migration runs on a fresh SQLite DB; restart preserves records; tests enforce unique slugs, rating bounds, key relationships, and immutable first-published slug behavior.

**Dependency:** F01.

### F03 — Single-admin setup and authentication

**Implement:**

- Identity with a single account and no public registration.
- First-run `/setup` protected by a server-supplied bootstrap secret (or documented secure CLI initialization).
- Login/logout, session cookies, secure reverse proxy handling, antiforgery tokens, protected `/admin/*` routes.
- Persist Data Protection keys under `/data/keys`.

**Acceptance:** Anonymous visitors cannot access admin or private media. Setup without bootstrap secret fails. A second user cannot be registered. Setup closes after the first user exists. POST without an antiforgery token fails.

**Dependency:** F02.

### F04 — Settings and admin shell

**Implement:**

- Site title, canonical URL, display author, description, timezone, About text, basic branding fields.
- Dashboard navigation; placeholder create actions for five types; settings form.
- Validate canonical URL and allowed accent-color presets.
- Admin layout responsive on phone and desktop.

**Acceptance:** Settings survive restart, unsafe URLs are rejected, and the dashboard is only accessible after login. No theme/customization engine is introduced.

**Dependency:** F03.

### F05 — Docker, Compose, and deployment smoke test

**Implement:**

- Multi-stage Dockerfile, persistent `/data` mount, non-root runtime.
- `deploy/compose.yaml`, `Caddyfile`, `.env.example`, sample production instructions.
- Startup migration strategy for one instance; health endpoint; useful startup logs.

**Acceptance:** A fresh VM or Docker environment can start the app, create the admin, restart containers, and retain DB/login keys/settings. No external service besides the optional proxy is required.

**Dependency:** F04.

### Phase 1 exit gate

- [ ] Clean deployment using documented Compose instructions.
- [ ] Secure setup, login, logout, and no public account signup.
- [ ] EF migration survives restart and domain constraints have tests.
- [ ] No secret or database file committed to source control.

---

## Phase 2 — First complete publishing vertical slice (articles)

**Goal:** Author an article on a phone, publish it, and serve a database-independent generated page. This is the most important product checkpoint.

### P01 — Post list, article draft, and Markdown editor

**Implement:**

- List drafts/published/unpublished posts; new Article editor.
- `PostDraft` with title, Markdown body, summary, cover media reference, tags.
- Formatting toolbar (bold/italic/heading/link/list/quote/code/image), textarea and preview toggle.
- Debounced authenticated autosave with explicit save status and safe cross-tab conflict detection.

**Acceptance:** Article draft persists across reload/restart; toolbar creates syntactically valid Markdown; incomplete draft can save; errors are not falsely reported as saved; published data is unchanged by autosave.

**Dependency:** F02–F04.

### P02 — Media uploads, private originals, and preview

**Implement:**

- Authenticated multipart media upload; JPEG/PNG/WebP signature and size validation.
- Server-generated file identity, safe path layout, metadata, alt text fields.
- Authenticated media route for drafts, Markdown media resolver, image insertion into textarea.
- Preview rendered from current draft with the public component layout; cache disabled.
- Explicit handling/testing of phone photo formats including HEIC/HEIF; document supported conversion/fallback.

**Acceptance:** Upload from a phone and desktop, see the image in draft preview, and verify the same private original URL is not anonymously accessible. Rejected malicious file/filename/path does not create public content.

**Dependency:** P01.

### P03 — Article public HTML renderer

**Implement:**

- Public Razor layout and article view component; Markdown-to-safe-HTML conversion.
- Shared header/footer, typography, static CSS, canonical/description metadata.
- Testable renderer accepting DTOs and returning complete HTML documents.

**Acceptance:** A sample article with headings, links, images, code, and lists renders valid self-contained HTML. Script payloads/raw HTML cannot execute. Rendering does not depend on an incoming HTTP request or a browser JS framework.

**Dependency:** P01–P02.

### P04 — Static generation and active-generation switch

**Implement:**

- Published snapshot creation independent of work-in-progress drafts.
- Full-site build to a nonpublic generation directory; page rendering, asset copying, default index, relevant article pages.
- Atomic manifest/pointer promotion, serialized builds, rollback-safe failure handling, a last-successful-generation record.
- Runtime read-only static serving of active generation; `/posts/{slug}/` resolution.

**Acceptance:** After Publish, `/posts/{slug}/` serves generated HTML; after editing the draft without Update, public HTML remains unchanged; a simulated render or disk failure leaves the previous public page available; reboot recovers the active generation.

**Dependency:** P03.

### P05 — Publish, update, unpublish, delete flows

**Implement:**

- Server-side publish validation, slug generation/collision handling, status reporting.
- Update published snapshot only on explicit update.
- Unpublish/delete rebuild with accurate admin status if a build fails.
- Avoid exposing unpublished or orphaned originals by generation.

**Acceptance:** Lifecycle test covers `Draft -> Published -> Edited draft -> Updated -> Unpublished -> Republished -> Deleted`; every public state transition has corresponding public URL assertions, and unsuccessful unpublish displays that old content may still be live.

**Dependency:** P04.

### P06 — Article mobile usability checkpoint

**Implement:**

- Responsive editor, upload/toolbar ergonomics, publish result link, PWA manifest and installable shell where supported.
- Basic accessible focus/keyboard support and light/dark variables for public theme.

**Acceptance:** On one real phone, sign in, create article, upload photo, preview, publish, and share the resulting URL with no desktop-only interaction. This must be demonstrated before starting Phase 3.

**Dependency:** P05.

### Phase 2 exit gate — Mandatory demo

- [ ] From a fresh instance, author and publish one real article including a photo.
- [ ] The public HTML is generated, contains correct metadata, and remains available without an author session.
- [ ] Editing after publication does not affect the public page until Update.
- [ ] Generator failure does not expose a partially built site.
- [ ] Reboot preserves the generated site, uploaded image, and draft.

---

## Phase 3 — Public website, microblog, RSS, and metadata

**Goal:** A coherent personal website exists with posts of two types, basic branding, feeds, discoverability, and attractive social sharing.

### W01 — Home feed, listings, About, tags

**Implement:**

- Static `/`, `/articles/`, `/notes/`, `/reviews/`, `/guides/`, `/galleries/`, `/tags/{slug}/`, `/about/`.
- Consistent post cards and date/type labels; layouts responsive and accessible.
- No dynamic public controllers or infinite scroll; use simple linked pagination only if necessary.

**Acceptance:** Pages work with JavaScript disabled, include only published posts, and use stable links. Empty lists show a useful empty state, not errors.

**Dependency:** P05.

### W02 — Microblog editor and public template

**Implement:**

- Short-form editor with optional title, Markdown body, photos, tags.
- A publish rule requiring text or at least one photo.
- Compact feed card, individual permalink, untitled fallback for page title/social metadata.

**Acceptance:** Text-only, image-only, and text-plus-image microblogs publish correctly; blank ones cannot publish. Both articles and microblogs appear in chronological feed in proper order.

**Dependency:** W01.

### W03 — RSS, sitemap, robots, canonical URL

**Implement:**

- RSS 2.0 for published posts across both types (later all five), XML-safe content and dates.
- RSS autodiscovery on public HTML; static sitemap and `robots.txt`.
- Correct absolute URL resolution from canonical base setting.

**Acceptance:** XML parsers accept RSS and sitemap. Updating/unpublishing removes or updates entries. No draft, admin, or backup URL appears. Changing base URL and rebuilding updates canonical links.

**Dependency:** W02.

### W04 — Open Graph and share cards

**Implement:**

- Title, description, URL, image, type for every public page.
- Twitter/X summary card fields and fallback social image.
- Use cover/first image when present; no generated illustration service.

**Acceptance:** Tests inspect produced HTML on articles and microblogs for complete absolute metadata. A sample shared link presents a reasonable preview when the receiving service fetches the page.

**Dependency:** W03.

### W05 — Branding UI and theme polish

**Implement:**

- Fixed preset accents, logo/avatar/header assets, About content, external profile links.
- Single theme, responsive typography and gallery-ready tokens, system light/dark theme.
- Settings changes trigger complete generation, with same failure behavior as publishing.

**Acceptance:** Customization is limited to SPEC-listed fields. Changed title/color/avatar appear in new static output; old site remains on failure; public UI passes basic accessibility and mobile smoke tests.

**Dependency:** W04.

### Phase 3 exit gate

- [ ] Public website includes mixed feed, two working types, About, tags, and listings.
- [ ] RSS/sitemap/social tags are generated from active public content.
- [ ] Branding can change without layout/theme authoring.
- [ ] Home/feed/post layouts work without JavaScript.

---

## Phase 4 — Review, guide, and gallery types

**Goal:** Finish the five fixed experiences using the shared post lifecycle, renderer, and media infrastructure. Do not create a universal widget system.

### T01 — Reviews

**Implement:** Dedicated review form (`subject`, `rating` 1–5, review Markdown, optional URL/images/tags), card, and page.

**Acceptance:** Publish-time validation rejects missing subject, rating outside 1–5, and blank review. Output displays rating and subject; external URLs are validated against safe protocols; item appears in feed, listing, RSS, sitemap.

**Dependency:** Phase 3.

### T02 — Guides

**Implement:** Guide editor with ordered steps and Markdown bodies, move/reorder controls, optional step photos, intro; guide page and summary card.

**Acceptance:** At least one nonblank step required to publish. Reordering steps persists and is reflected in output/export. Preview and public page render steps consistently; drafts may contain incomplete steps.

**Dependency:** T01.

### T03 — Galleries

**Implement:** Multi-image selection/upload, image order, captions/alt text, optional title/introduction, light responsive image grid without mandatory JS.

**Acceptance:** Gallery cannot publish without an image. Order/captions/alt text persist and match generated HTML; public page is functional with JavaScript disabled; tests confirm unpublished images are inaccessible.

**Dependency:** T02.

### T04 — Five-type consistency and content fixtures

**Implement:** Comprehensive sample fixtures, type listing/feed styling, lifecycle and security tests for all five, shared publish validation.

**Acceptance:** For every type: draft -> preview -> publish -> edit draft -> update -> unpublish -> republish -> delete; RSS, sitemap, permalink, tags, share metadata, and media visibility all behave correctly.

**Dependency:** T03.

### Phase 4 exit gate

- [ ] All five editors work from mobile and desktop.
- [ ] No new user-configurable schema/type system was added.
- [ ] All types share fixed URLs and static output behavior.
- [ ] Regression suite checks per-type lifecycle and safety.

---

## Phase 5 — Portability, operations, and release readiness

**Goal:** Deliver independent exports, safe restore, documented deployment, and a reliable v0.1 release.

### E01 — Static site ZIP export

**Implement:** Download a ZIP of the last successfully activated generation, including images/CSS, RSS, sitemap, `robots.txt`, and optional README.

**Acceptance:** Extract ZIP, serve it with a basic static server **without Homestead running**, and verify every public route/resource works. Drafts are absent. The export contains only active public content, even if a requested rebuild failed.

**Dependency:** Phase 4.

### E02 — Hugo bundle export

**Implement:** Generate Hugo project, leaf bundles (`content/posts/{slug}/index.md`), metadata, media, minimal layouts/theme supporting all five content types; rewrite internal media references to local filenames.

**Acceptance:** Hugo CLI successfully builds a fixture with one post of each type, a draft, tags, media, and non-ASCII content. URLs are preserved; reviews' ratings, guides' steps, gallery captions/order, and photos survive. Drafts do not render by default.

**Dependency:** E01.

### E03 — Backup and restore

**Implement:** SQLite-consistent backup, original media capture, versioned manifest/checksums; documented offline restore on a clean data volume; maintenance UI or CLI trigger.

**Acceptance:** Restore a fixture installation containing all five types, originals, settings, and drafts onto a fresh instance of the same version; verify content/checksums and rebuild public site. Corrupt/incomplete archives fail with useful diagnostics.

**Dependency:** E02.

### E04 — Reliability and security regression pass

**Implement:** Failure injection around staging, activation, media upload, publication reconciliation; static path traversal checks, auth and antiforgery checks, output sanitization, upload signature verification, concurrent publish test.

**Acceptance:** All described failure modes in `ARCHITECTURE.md` have automated coverage or a written repeatable manual test. In particular, unpublished content cannot leak and no partially generated site is served.

**Dependency:** E03.

### E05 — Deployment and operations documentation

**Implement:** `README.md`, deployment guide for a Linux Droplet, Caddy/TLS configuration, environment variable reference, upgrades/migrations, data-volume permissions, backup schedule advice, logs, health checks, troubleshooting.

**Acceptance:** A reader who did not write the application can deploy it following the guide, set up the admin, publish, back up, restore, and upgrade with no hidden dependencies.

**Dependency:** E04.

### E06 — Release candidate and full acceptance test

**Implement:** Version tagging, CHANGELOG, tested Docker image build, final UI/accessibility pass, supported Hugo version pinned/documented, sample import/export fixtures and final regression run.

**Acceptance:** All 11 definition-of-done items in `SPEC.md` pass on a clean release candidate. The release contains no major out-of-scope subsystem.

**Dependency:** E05.

### Phase 5 exit gate

- [ ] Static ZIP works on static-only hosting.
- [ ] Hugo ZIP builds and renders all five types without Homestead.
- [ ] Complete backup/restore works, including originals and unpublished work.
- [ ] Fresh Droplet deployment, restart, upgrade, and failure recovery are documented and exercised.

---

## 3. Cross-cutting test cases to automate

Create fixtures representing at least:

| Fixture | Why it matters |
|---|---|
| Untitled text microblog | Stable slug, readable fallback title |
| Photo-only microblog | Media counts as content |
| Article with headings, code, links, image | Markdown correctness and sanitization |
| Review with one-star and five-star ratings | Valid range boundaries |
| Guide with three reordered steps | Order and step-image references |
| Gallery with reordered and captioned photos | Ordered resource export |
| Draft with confidential text and attachment | Anonymous-public leak regression |
| Published post with unsaved/pending changes | Public snapshot isolation |
| Post title containing Unicode and punctuation | Escaping, slug fallback, filename safety |
| Two posts with the same title | Unique immutable permalink generation |
| Rebuild with injected template exception | Atomic activation, error status |
| Site base URL change | Canonical/OG/RSS/sitemap regeneration |
| Backup with new media added during authoring | Consistency/serialization |
| Invalid upload disguised with `.jpg` | MIME and signature enforcement |
| Edited published post after failed generation | Admin accurately distinguishes desired vs active publication |

Minimum continuous integration checks:

```bash
dotnet restore
dotnet build --no-restore
dotnet test --no-build
# For integration/release CI:
docker compose -f deploy/compose.yaml config
# Run a Hugo build on an exported test fixture.
# Run extracted static-output smoke tests with a simple HTTP server.
```

Commands are illustrative: agent must add actual CI jobs and fixture paths rather than leaving comments as its implementation of the tests.

## 4. Agent handoff format

For each completed task, provide:

```text
Task: <ID and title>
Status: Complete | Blocked

Changed:
- <paths / essential changes>

Migrations/config:
- <what changed, or "none">

Tests:
- <test command(s) and result(s)>

Acceptance:
- <evidence for each task acceptance criterion>

Risks/follow-ups:
- <remaining issues, scoped to current milestone>

Next:
- <exact next task ID>
```

Do not claim acceptance criteria were verified if they were not executed. If a task is blocked, give a narrowly scoped blocker and smallest viable alternative. Never use an architectural blocker as a reason to build extra frameworks without approval.

## 5. First assignment to send the coding agent

> Implement **F01 only** from `IMPLEMENTATION.md` for Homestead v2. Read `SPEC.md` and `ARCHITECTURE.md` first. Create the .NET 10 solution with one ASP.NET Core Razor Pages application, test project, Tailwind build setup, a simple authenticated-UI-ready shell, standard build quality files, and CI validation. Do not implement posts, publishing, custom theme settings, exports, additional services, or speculative abstractions yet. Add a README linking the design documents. Run `dotnet build` and `dotnet test`, report the results, and stop for review before F02.

After F01–F05, the **first meaningful product demo** is Phase 2: publish a real article with an image from a phone to a generated static site on a Droplet. Protect that milestone; it is the strongest evidence that the simplified architecture works.

## 6. Deferred backlog — not authorized by this plan

Add issues rather than code for: inbound OS share-sheet integration, additional Markdown editing polish, advanced image conversion, pagination after real performance measurement, URL redirects, scheduled posts, revision history, automatic GitHub Pages deploy, webmentions/federation, import from social platforms, analytics, AI, custom themes, or hosted SaaS.

**Rule for scope changes:** Modify `SPEC.md` first, explain the user-visible need, update `ARCHITECTURE.md` only if necessary, then insert a new task with acceptance criteria. Do not silently expand v0.1.
