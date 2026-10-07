# Phase 3: public website and microblogs

Phase 3 implements W01–W05. Physical-phone/public-VM acceptance from Phase 2 and
an actual messaging-service share preview remain manual checks; the browser and
container checks below do not claim to replace them.

## Public experience

The complete generated site includes `/`, `/articles/`, `/notes/`, `/reviews/`,
`/guides/`, `/galleries/`, `/tags/{slug}/`, `/about/`, and `/posts/{slug}/`.
The home feed mixes published articles and notes, newest first by initial
publication time, with a deterministic ID tie-breaker. Updates retain the original
date and permalink. Each listing uses the same published snapshots; the three
Phase 4 listings have useful empty states until their editors arrive.

Pages contain semantic landmarks, date/type labels, linked tags, keyboard-accessible
navigation, and a skip link. The public site has no JavaScript dependency. Notes
show their Markdown and photos in feed cards and on their individual pages;
articles show summaries and optional covers in cards and full content on permalinks.

## Notes

Choose **New note** in Posts or **Create note** on the dashboard. The short-form
screen has an optional title, Markdown, photos, alt text, and tags. It shares the
existing save/conflict/preview/upload/publication lifecycle. A note requires
nonblank body text or at least one uploaded photo; a title alone cannot publish.
All uploaded note photos are included, with inline photos placed in the Markdown
and other photos shown below it. Draft photos stay private until publication.

Untitled notes use a `Note · YYYY-MM-DD` title in HTML/RSS/social metadata and a
`note-YYYY-MM-DD-{id-prefix}` slug. A later title change does not change that slug.
Raw HTML is encoded and unsafe links remain blocked by the shared Markdown renderer.

Repeated PNG/JPEG/WebP uploads now have unique server-owned stored filenames.
The editor also resubmits publication forms on the next event-loop task after
autosave, preventing browsers from ignoring a reentrant submission when the draft
is already saved. The script supports note uploads without an article cover selector.

## Discovery and branding

Every generation contains RSS 2.0, an XML sitemap, and robots.txt. RSS includes
titles, descriptions, stable permalink GUIDs, UTC RFC 1123 dates, tags, rendered
content, covers, and note photos. Feed links and media use the saved canonical base
URL. The sitemap contains only generated public pages; drafts, admin, originals,
and backups are excluded. Public HTML includes RSS autodiscovery, canonical URLs,
Open Graph title/description/URL/image/type, and Twitter/X card/title/description/image.
Post images take priority for sharing, followed by header/avatar/logo branding,
then a bundled static 1200 × 630 PNG. Markdown is converted to plain text for
descriptions, with useful fallback descriptions for empty sites and photo-only notes.

Settings expose only the specification's fixed fields. Upload or remove an avatar,
logo, or header independently of the text form. JPEG/PNG/WebP validation and the
20 MB/40 megapixel limits apply to branding too. Save text settings before using
the separate upload forms. Up to five external profiles accept labeled absolute
HTTP(S) URLs without credentials; blank profile rows are optional.

Four fixed accents (Porch, Forest, Ocean, Plum) use paired light/dark colors within
one responsive theme. Light/dark appearance follows the system preference. About
Markdown uses the same safe renderer. No layout designer, arbitrary CSS, or HTML
injection settings were added.

## Consistency and upgrades

All pages, discovery files, metadata, CSS, and referenced branding/post assets are
built before the existing atomic activation. Failed settings/image/publication
changes keep the previous site, feeds, and images active together. Rebuilding retries
the saved desired changes. Replaced or removed branding originals remain private
for future backup support and disappear from the public file allowlist on activation.
Public requests continue to serve the active manifest without querying SQLite.

The migration adds `SiteSettings.ProfileLinksJson`, initializing existing rows to
`[]`, and preserves the existing media foreign keys and publication trigger.
Snapshots gain an additive type discriminator and saved tag-slug map. Phase 2
snapshots deserialize as articles; rebuilds resolve their tag names against the
existing tag records so colliding names such as C++ and C# retain distinct URLs.
Admin pending-publication comparisons account for that legacy normalization.

On upgrade, sign in and choose **Posts → Rebuild site** to replace an existing
Phase 2 generation with the new public pages and discovery files. Active generations
remain available during startup; the canonical environment variable still seeds
only initial settings and does not override saved settings.

## Verification

On 2026-10-07:

- Release build and all 85 unit/integration tests passed. The tests parse generated
  RSS/sitemap XML, exercise note combinations and removal, inspect article/note social
  fields and absolute URLs, verify draft isolation, tag collisions, branding
  replacement/failure/retry/restart, and test a real Phase 2 SQLite upgrade.
- `dotnet format --verify-no-changes --no-restore` and the EF pending-model check
  passed. JavaScript syntax checks passed.
- The production Docker/Compose smoke passed with local HTTPS, non-root runtime,
  article/media lifecycle, notes, public listings/About/tags, RSS/sitemap/social
  fields, unpublish, and restart without a setup secret.
- Chromium at 390 × 844 completed note autosave, image upload, private preview,
  publication after an already-completed save, and branding changes. With JavaScript
  disabled, the home, all five type listings, About, and a photo-note permalink
  rendered with one h1 and no horizontal overflow. Light/dark screenshots were
  visually inspected; keyboard focus reached the skip link first. Measured accent
  title contrast ranged from 7.16–7.71:1 in light mode and 8.53–9.20:1 in dark mode.

For manual acceptance, complete the [physical-phone checklist](phase-2.md#required-real-phone-demonstration),
publish a note and article on the public VM, subscribe to its RSS URL in a feed
reader, and share their URLs in a messaging service. Record the device/browser,
feed reader, and receiving service alongside the resulting preview.
