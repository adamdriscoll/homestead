# Phase 4: reviews, guides, and galleries

Phase 4 implements T01–T04 using the existing post lifecycle and static generator.
All five create actions are available on the dashboard and Posts screen.

## Focused editors

**Reviews** have a free-text subject, optional integer rating while drafting,
review Markdown, optional HTTP(S) subject URL, photos, and tags. Publication requires
a subject, an integer rating from 1 through 5, and review text. URLs with unsafe
protocols or credentials are rejected. The subject supplies the public title and
first permalink; later subject changes keep that permalink. Cards and full pages
show the subject and rating. This does not add structured ratings markup.

**Guides** have a title, optional Markdown introduction, ordered Markdown steps,
photos, and tags. Add, move, or remove steps with ordinary buttons. Formatting
actions use the last focused Markdown field, including a step's instructions.
Photos can be assigned to the introduction or a specific step. Publication requires
a title, at least one step, and instructions in every included step, following the
product specification. Incomplete steps remain valid drafts. Removing a step moves
its photos to the introduction. Step IDs remain stable when reordered.

**Galleries** have optional title/introduction, photos, tags, plain-text captions,
and alt text. Select up to five JPEG/PNG/WebP photos per upload, each up to 20 MB.
The shared signature/decoding and 40 megapixel limits still apply. HEIC/HEIF produces
the existing explicit JPEG conversion guidance. Move photos up/down and remove
them with buttons. Missing alt text appears as an editor warning; publication
requires at least one photo. Untitled galleries use a date-based display title and
stable date/ID permalink. The public responsive grid needs no JavaScript.

Save the main draft before submitting the separate upload form with JavaScript
disabled. Online uploads save it automatically. If a batch fails partway through,
earlier successful uploads remain private in the editor; review them before retrying.
Removing a photo detaches it from the draft; its original remains private for the
Phase 5 backup workflow. Updating the published post removes it from the public
asset allowlist. Markdown references to a removed photo must also be removed.

## Shared publication behavior

Structured fields, step order, image order, captions, and alt text are copied into
the immutable published snapshot. Draft edits and newly uploaded photos do not
enter rebuilds until Update is chosen. Preview, public pages, and full RSS content
use the same content renderer; RSS image links are absolute. All types appear in
the mixed feed, their fixed type listing, tags, sitemap, and social metadata.

Shared publish validation enforces the fixed five formats. Media/step references
must belong to their post. Raw HTML is encoded, unsafe Markdown links are blocked,
and captions, subjects, and image descriptions are escaped. Draft saves use the
existing version conflict check. Reordering uses a SQLite transaction with temporary
order ranges to preserve the existing uniqueness constraints.

No database migration is needed: the foundation already created review details,
guide steps, media roles, caption fields, and sort order. Snapshot fields are additive;
older article/note snapshots retain their defaults. Existing active generations
remain readable. The next publish or **Posts → Rebuild site** regenerates them with
the shared renderer. Export endpoints remain Phase 5 work; persisted structured
content and snapshots retain the fields their exporters will need.

## Verification

On 2026-10-07:

- Release build and all 113 unit/integration tests passed. The 28 new phase 4 cases
  include each type's complete lifecycle and restart, specialized validation,
  structured snapshot isolation, foreign-media/step rejection, unsafe Markdown,
  step/photo order, captions, alt text, batch upload, partial upload failure,
  step removal, version conflicts, and failed generation/retry.
- `dotnet format --verify-no-changes --no-restore`, JavaScript syntax checks,
  `git diff --check`, and the EF pending-model check passed.
- Production Docker/Compose smoke passed with HTTPS, all five published types,
  gallery batch uploads/order/captions, public discovery, private draft assets,
  a non-root runtime, and restart without the setup secret. The Docker CSS stage
  includes the shared renderer so its responsive grid classes ship in production.
- Chromium at 390 × 844 completed review autosave/publication, guide step creation
  and reordering, photo upload/assignment/publication, and gallery batch upload,
  captions, alt text, photo reordering, private preview, and publication. At
  1280 × 900 the three new editors had one h1 and no horizontal overflow.
- With JavaScript disabled, the mixed feed, three new listings, and their permalinks
  rendered at mobile width with one h1 and no overflow. Gallery order/captions were
  checked in the DOM. Mobile, desktop, and dark gallery screenshots were visually
  inspected. Evidence is in the ignored `output/playwright/phase4-*.png` files.

Physical-phone and public-VM acceptance remain manual. Follow the
[real-phone demonstration](phase-2.md#required-real-phone-demonstration) for each
new type, including contemporary phone photos and the HEIC/HEIF fallback. Publish
one review, guide, and gallery on the public VM; verify their feed-reader content
and messaging-service share previews. Record device/browser and receiving services.
