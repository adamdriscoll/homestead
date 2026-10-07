# Phase 2: article publishing

Phase 2 implements P01–P06's application functionality. A physical-phone demo and
public VM deployment remain manual acceptance checks; browser viewport emulation
does not replace the plan's required real-device demonstration. Phase 3 functionality
is now documented separately in [Phase 3](phase-3.md); the real-device gate remains
unverified.

## Authoring

Create articles from the dashboard or `/admin/posts`. Save incomplete drafts,
filter draft/published/unpublished posts, and edit title, summary, body, tags,
cover, and image alt text. The Markdown toolbar inserts bold, italic, headings,
links, lists, quotes, fenced code, and uploaded images. Plain forms still work
without JavaScript: save first, upload, then insert the displayed Markdown reference.

With JavaScript, edits save after a 900 ms debounce. Each save submits the draft's
version; stale tabs receive 409 and a conflict message without overwriting edits.
Save/upload failures remain visible. Preview saves outstanding edits first and
uses the same Razor document and safe Markdown rules as public output, with private
media URLs and no-store responses. Public HTML contains no authoring JavaScript.

JPEG, PNG, and WebP uploads require matching extensions, MIME types, detected
signatures, and successful decoding. Limits are 20 MB and 40 megapixels, with one
decoded frame. Originals retain their bytes in private, server-owned paths.
Unsafe filenames, SVG, corrupt/spoofed images, and HEIC/HEIF are rejected. HEIC/HEIF
feedback explains exporting as JPEG or selecting Most Compatible in camera settings.
Desktop paste/drop and mobile file selection use the same upload endpoint.

The shell includes an install manifest and 192/512 px icons; installation depends
on browser support and HTTPS. Drafts require a connection; offline durability is
not provided. Touch controls wrap at narrow widths, and preview works in place.

## Publication decisions

The migration adds nullable `PostDraft.CoverMediaId`; the existing draft version
is the concurrency token. The first migration's immutable-slug trigger is unchanged.
Referenced covers must belong to the article. Image descriptions and tags are
copied into typed version-1 article snapshots on explicit Publish/Update.

One in-process lock serializes draft/media/publication/settings writes. Desired
public snapshots commit before rendering. The generator reads only those snapshots,
builds a default home and article permalinks, copies CSS and referenced images,
validates complete HTML documents, and flushes a temporary manifest before replacing
`site/current.json` on the same volume. This follows the architecture's
[request-independent Razor rendering approach](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-components-outside-of-aspnetcore?view=aspnetcore-10.0).

The private active manifest contains generation ID, settings, full activated article
snapshots (including draft revisions), and the exact public file list. Public serving
uses this in-memory manifest without opening the database, resolving directory
indexes and only allowlisted files. Images not referenced by an active snapshot
are absent. Draft changes do not alter snapshots, including during unrelated rebuilds.
An empty public home is generated after setup, or on upgrade from Phase 1.

The manifest resolves planning-review question 3: failed desired changes remain
pending; every subsequent build retries all desired snapshots/settings, including
failed removals. Admin status compares actual active membership/content with desired
state. Interrupted builds are reconciled at startup. A completed pointer activation
remains authoritative even if the final database build-record write is interrupted.
Missing required active files fail readiness and require an explicit rebuild.

The generator retains three successful generations and removes failed staging
output. File opening and cleanup share a gate so existing responses can finish.
Private originals are retained for future editing/backup and never served anonymously.
`HOMESTEAD_BASE_URL` continues to seed only the initial setting; saved canonical URL
changes rebuild output and survive restart. Accent/branding polish, type listings,
RSS, sitemap, About, and social cards remain Phase 3 work.

Markdig disables raw HTML and the application allows safe link protocols only.
Image URLs must refer to the article's uploads. Razor encodes title/summary/settings.
ImageSharp 3.1.12 supplies image validation; open-source use is covered by its
[Apache license grant](https://github.com/SixLabors/ImageSharp/blob/v3.1.12/LICENSE).

## Verification

Automated integration/unit coverage exercises complete lifecycle, persisted drafts,
photo privacy, orphan image removal, immutable slugs/collisions, snapshot isolation,
CSRF/authentication, stale saves, rejected uploads, unsafe Markdown, request-independent
Razor rendering, concurrent publishes, render/disk failure, interrupted desired-state
recovery, unrelated-publish retry, corruption/readiness, and database-free static serving.
The Docker smoke script additionally exercises production HTTPS, non-root runtime,
article/media publication, update isolation, and restart without the setup secret.

Browser verification at 390 × 844 checks setup/login, new article, autosave, formatting,
image insertion, in-place preview, publication, and layout. The preview is allowed
to frame only from the same origin; other pages retain `frame-ancestors 'none'`.

On 2026-10-07, `dotnet test` passed all 68 tests, the EF pending-model check passed,
and the production Docker/Compose smoke passed. Browser measurements confirmed no
horizontal overflow at 390 px and 44 px formatting buttons, with the private preview
rendered inside the editor. Physical-phone and public-VM acceptance were not performed.

## Required real-phone demonstration

1. Deploy a fresh instance with the documented Compose configuration on the target VM.
2. On a physical phone, complete setup/login and choose New article.
3. Enter a real title/story, wait for Saved, and reload to confirm persistence.
4. Pick a camera-roll JPEG/PNG/WebP, enter alt text, and verify private preview.
5. Try HEIC/HEIF and confirm the actionable conversion message; convert and retry.
6. Publish and open/share the permalink in a signed-out browser; verify its image.
7. Edit the draft, confirm public content stays unchanged, then Update published post.
8. Restart the container and verify the draft, generated article, and image remain.
9. Test keyboard focus and light/dark appearance on desktop and phone.

Record device/browser and VM details when this demo is performed. Until then, the
mandatory Phase 2 real-device exit gate remains unverified.
