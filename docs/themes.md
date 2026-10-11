# Public themes

Select a theme in **Settings → Public theme** and save. Porch is the original
single-column feed, Journal uses a narrower serif reading layout, Ledger uses
a wider masthead and two-column feed on larger screens, and Folio pairs a bold
accent-colored cover with spacious typography and serif reading text. Town Square
uses a Facebook-inspired social feed with author avatars, rounded post cards,
a profile cover, and a full-width content column without an introduction sidebar.
Each supports every post type, ordinary navigation without JavaScript, system
light/dark appearance, the four accent presets, and optional primary, background,
and text colors.

Folio uses Razor templates for the document, navigation, footer, and
post layouts, including a distinct note layout and structured review details in
listings. Its original templates and styling take inspiration from
[Typology by Meks on ThemeForest](https://themeforest.net/item/typology-text-based-minimal-wordpress-blog-theme/19547842).
No purchased assets or external fonts are required. The sources in
`src/Homestead/Themes/folio` also serve as a built-in Razor authoring example.

Town Square is included as a built-in option under **Settings → Public theme**.
Its Razor sources live in `src/Homestead/Themes/townsquare`. Feeds, post pages,
and private previews use the full content width within the centered page layout;
the About page remains accessible from the navigation and home profile cover. Notes,
galleries, and check-ins show their full content in the feed; articles, reviews,
and guides link from previews to their complete post. All navigation works without
JavaScript. Styling is original, with no Facebook assets or external fonts.

Colors use six-digit hex values such as `#25613e`. Empty fields restore theme
defaults. Overrides apply in both light and dark mode, so check that your text and
background remain readable in each. Primary foreground color is chosen
automatically for contrast. A custom theme can decline color overrides.

Administration has a separate **Appearance** selector for System, Light, or Dark.
That preference is stored in the current browser and does not change public pages.

Settings are saved before building. A successful build atomically activates all
theme changes. If it fails, the previous complete site stays public; Settings
shows the active theme and the rebuild failure. Use **Rebuild site** to retry.

If **Settings → Backup** is configured to back up before publication, the engine
uploads a complete private backup before rendering any theme. A failed upload
keeps the active generation and theme unchanged, with saved changes pending.
This applies to all built-in and custom themes. Backup configuration and credentials
are private and are absent from public theme models and generation manifests.
Theme templates need no backup handling; private previews do not trigger backups.

The shared footer shows “Made at home with Homestead”,
with Homestead linking to the project repository. To remove it, check
**Settings → About → Hide Homestead credit** and save. The preference also
applies to private previews and Hugo exports; static exports keep the currently
active footer. Razor themes replacing the footer or entire document must honor
`Model.Site.HideHomesteadCredit` themselves, or include the shared footer.

Enable optional sharing buttons in **Settings → Social**. Bluesky, Facebook, X,
LinkedIn, Reddit, and email can each be selected independently; all start disabled.
Every built-in theme shows enabled buttons on post details and home/type/tag feed
cards for all six post types. Buttons are ordinary links using the published
display title and the saved canonical URL, with no third-party scripts or requests
until a reader follows a link. Private previews omit sharing, including previews
of posts that are already published. Saving rebuilds the site; a failed build
leaves the active buttons unchanged until a successful rebuild. Static ZIPs retain
the active buttons. Hugo exports use the saved selections and Hugo permalinks,
so changing Hugo's base URL also changes the shared URLs.

Post templates replacing shared rendering should iterate `Model.Post.ShareLinks`
to honor these choices, including in listings. Each entry has an encoded sharing
destination in `Url` and a service name in `Label`; render `Url` as a normal Razor
attribute and `Label` as text. The array is empty when sharing is disabled or the
page is a private preview. Use accessible link labels, wrap buttons on narrow
screens, and preserve keyboard focus, touch targets, and light/dark colors.

## Newsletter navigation and forms

**Settings → Newsletter** optionally enables Brevo email delivery. Every built-in
theme includes a Newsletter link in its shared or custom header. Porch, Journal,
and Ledger use the shared header; Folio and Town Square iterate `Model.Navigation`.
The link is an absolute URL to the saved canonical site's `/newsletter/` endpoint,
so static and Hugo exports can still direct readers to the original subscription
service. Exports contain no subscriber addresses, API keys, or subscription forms.
Keep the original Homestead service available for subscription links, or remove
the link when migrating away from it.

The public form uses the active theme's shared regions, CSS, and ordinary POST
forms, including confirmation, preferences, and unsubscribe. It works without
JavaScript and supports system light/dark appearance. These request-only pages
are never generated or added to the public file allowlist. They have no-store,
no-referrer, and noindex responses; their explicit opt-in routes use private SQLite
for subscription handling. Regular public pages remain static. Private post
previews show the navigation but never embed subscriber forms or reader data.

`ThemeSiteModel.EnableNewsletter` exposes only the active public navigation
preference. `Model.Navigation` includes the canonical Newsletter URL when enabled.
Header and Main overrides should preserve this navigation; compare link URLs to
`Model.CanonicalUrl` as well as `Model.Path` for the current-page indication.
Content overrides must preserve `Model.DefaultContent` on `/newsletter/` to render
the forms. `Model.IsListing` is false there. No credentials or reader records are
exposed as theme model members. `DefaultContent` contains request-specific form
markup and its antiforgery token on these dynamic pages. Disabling stops subscriptions and sending
immediately; a failed rebuild can temporarily retain the old navigation link.

## Install a custom theme

Upload a ZIP in **Settings → Install a custom public theme**, then select the
installed theme and save. Uploading alone does not change the public site. An
identical upload reuses its installation. Changed packages install separately,
allowing you to select an earlier version. Save text settings before uploading.

Themes can contain JavaScript that runs on the public site's origin. Razor
templates also execute C# on the server, with the application's permissions
and access to private data, credentials, and files. They are **trusted code, not
sandboxed templates**. Install only packages from authors you trust. Installation
compiles templates and reports syntax/model errors before saving the package.
Rendering errors prevent activation and leave the previous complete site public.

Private previews use the same templates with authenticated media URLs. The shared
head omits theme scripts, and Content Security Policy prohibits scripts even if a
custom template emits one. Razor server code still runs in previews. Public
content and navigation must work without scripts.

ZIPs may be at most 5 MB compressed and 20 MB expanded, with at most 128 entries.
Put `theme.json` at the ZIP root; don't wrap the package in an extra directory.
Directory entries are allowed. Filenames use letters, numbers, dots, underscores,
and hyphens, start with a letter or number, and use `/` between directories.
Traversal, absolute paths, symbolic links, duplicate paths (including differences
only in case), Windows device names, and unsupported files are rejected. Paths
are case-sensitive when referenced by the manifest.

Supported files are `.css`, `.js`, `.png`, `.jpg`, `.jpeg`, `.webp`,
`.woff`, `.woff2`, and `.razor`, plus the root `theme.json`. Razor files
are private sources and are never served as assets. Standalone C# files, DLLs,
project files, and `.cshtml` are not accepted.

## Package format

Themes use Razor templates and shared public components. The manifest declares
supported post types, styles, scripts, optional colors, and template mappings.
`formatVersion` identifies the package schema; `version` is the author's release
label. Theme rendering has one contract.

```text
theme.json
theme.css
Main.razor
Header.razor
Note.razor
fonts/reading.woff2
```

Every theme must declare all six post types. Notes use `microblog`, and check-ins
use `checkin` in the manifest. All referenced styles, scripts, and templates must
exist. `styles` needs at least one entry; `scripts` can be empty. Both arrays have
a maximum of 16 entries. `supportsColors` defaults to false. Names have a maximum
of 80 characters, release labels 40, and manifests/Razor sources 64 KB each.
Unknown manifest fields are rejected.

Homestead compiles its shared public Razor components with the application.
[`SiteDocument.razor`](../src/Homestead/Components/Public/SiteDocument.razor)
renders the document head, header, main content, and footer;
[`PostContent.razor`](../src/Homestead/Components/Public/PostContent.razor)
renders all six post types and optional locations. Themes can replace these
regions with Razor templates or use the shared defaults. This keeps escaping,
metadata, media resolution, previews, and navigation consistent across themes.

## Razor templates

Start with the [Razor Garden example](../examples/themes/razor-garden/theme.json).
ZIP the contents of that directory with `theme.json` at the root. Its `Main.razor`
replaces the document, `Header.razor` replaces the header, and `Note.razor` replaces
notes. Remove any template to use the shared default for that part.

A theme can use named files at the ZIP root (`Main.razor`,
`Header.razor`, etc.) or map names to source files explicitly:

```json
{
  "formatVersion": 1,
  "name": "Razor Garden",
  "version": "1.0.0",
  "postTypes": ["microblog", "article", "review", "guide", "gallery", "checkin"],
  "styles": ["theme.css"],
  "scripts": [],
  "supportsColors": true,
  "templates": {
    "Main": "templates/Main.razor",
    "Header": "templates/Header.razor",
    "Post": "templates/Post.razor",
    "Note": "templates/Note.razor"
  }
}
```

Names and paths are case-sensitive. Explicit mappings take precedence over
root filenames. A mapping must name one of the following templates and reference
an existing `.razor` source. All mapped templates compile together, including
ones that currently have no matching posts. Unreferenced sources remain private.

| Template | Replacement and fallback |
| --- | --- |
| `Main` | Entire HTML document. Without it, Homestead supplies the document, head, skip link, and shared regions. |
| `Header` | Shared header on every page. |
| `Content` | Entire main region on feeds, post pages, About, tag pages, and the map. Include `main id="main"` for the skip link. |
| `Footer` | Shared footer on every page. |
| `Post` | Entire post detail or feed card, when there is no matching type template. |
| `Note`, `Article`, `Review`, `Guide`, `Gallery`, `Checkin` | Entire post detail or feed card of that type, preferred over `Post`. |

Absent post templates use shared `PostDocument.razor`/`PostContent.razor` rendering.
`Model.IsListing` distinguishes feed cards from post details. Post overrides are
rendered first, then Header, Content, Footer, and finally Main. A custom Content
template can include the composed default via `Model.DefaultContent`; if it
iterates `Model.Posts` itself, it owns the post presentation.

Every template inherits `ThemeComponent` and receives an immutable
[`ThemePageModel`](../src/Homestead/Features/Themes/ThemePageModel.cs) as `Model`.
No parameter declaration or `@inherits` directive is needed. Use HTML markup,
Razor expressions, `@if`, `@foreach`, and C# helpers in `@code`. The public Razor
language compiler is pinned to 6.0.36, with Roslyn 5.0.0 targeting the application's
.NET 10 runtime. Newer framework directives, interactive rendering, child
component tags, `_Imports.razor`, and code-behind files are not supported. Common
imports include `System`, `System.Linq`, `Microsoft.AspNetCore.Components`,
`Homestead.Features.Themes`, and `Homestead.Data.Entities`; other `@using` directives
can reference assemblies already shipped with Homestead. No NuGet restore or SDK
is needed on the server. Compilation is cached for the current theme; active
public requests read only generated static files.

### Page model

| Member | Meaning |
| --- | --- |
| `Site` | `ThemeSiteModel`, the saved public settings described below. |
| `Path`, `Heading` | Root-relative page URL and listing/page heading. |
| `Preview` | True only for an authenticated draft preview. |
| `IsListing` | True for home/type/tag listings; false for post details, About, and map pages. |
| `ThemeId`, `CanonicalUrl` | Installation ID and canonical page URL derived from saved settings. |
| `Navigation` | Ordered `ThemeLinkModel` entries (`Label`, `Url`) for enabled types, Home, About, and optional Map. |
| `Post` | Current `ThemePostModel` in a post detail/type/Post template; otherwise null. |
| `Posts` | Ordered snapshots for the current listing, or map's posts; empty on details/About. |
| `Head` | Shared encoded metadata, stylesheet links, and public-only script links as a `MarkupString`. Available to region/Main templates. |
| `Header`, `Content`, `Footer` | Rendered regions as `MarkupString`s. Main receives all replacements. Region templates see replacements completed earlier in the order above. |
| `DefaultContent` | Shared post/card for a post template; shared/composed region for a region template; complete composed document for Main. |

`ThemeSiteModel` includes `Title`, `Description`, `CanonicalBaseUrl`,
`DisplayAuthor`, `AuthorBio`, `FooterText`, `HideHomesteadCredit`, `TimeZone`,
`AccentColor`, `EnableLocationMap`, `EnabledPostTypes`, `ProfileLinks` (Label/Url),
sanitized `AboutHtml`, and optional `Avatar`, `Logo`, and `HeaderImage` media.
It contains public settings only, with no database entities, credentials, or
private filesystem paths.

### Post and structured models

Public models come from committed published snapshots. Private preview models
come from the saved draft and never write public output. Immutable arrays retain
snapshot ordering. Every type uses the same `ThemePostModel`, with optional
structured fields:

| Members | Meaning |
| --- | --- |
| `Id`, `Type`, `TypeLabel` | Stable ID, `PostType` enum (notes use `Microblog`), display label. |
| `Title`, `DisplayTitle`, `Url` | Authored title, title with type/date/place fallback, stable `/posts/{slug}/` link. |
| `Summary`, `Description` | Optional authored summary and generated plain-text excerpt. |
| `PublishedAtUtc`, `ModifiedAtUtc`, `DisplayDate` | Snapshot timestamps and first-publication date formatted in the site's time zone. |
| `BodyHtml`, `ContentHtml` | Sanitized Markdown body; full shared content including location, review, photos, and guide steps. Both are `MarkupString`s. |
| `Tags` | Ordered `ThemeTagModel` entries (`Name`, `Url`), preserving collision-safe tag URLs. |
| `Media`, `Cover` | Ordered media and optional selected cover. Each has `Id`, `Url`, `MimeType`, `AltText`, `Caption`, `GuideStepId`. |
| `Review` | Optional subject, nullable integer rating, and external URL. Preview ratings can be null. |
| `Tools`, `Materials` | Ordered guide supplies with a plain-text `Name` and optional HTTP(S) `Url`. Materials also have optional text `Quantity`. Empty on older snapshots; older materials have no quantity. |
| `ShareLinks` | Ordered `ThemeLinkModel` entries (`Label`, `Url`) for enabled sharing services, built from the published display title and canonical post URL. Empty in private previews. |
| `Steps` | Ordered `ThemeStepModel` entries with stable `Id`, optional `Title`, sanitized `BodyHtml`, step `Media`, optional `DurationSeconds`, and Humanizer-formatted `DurationText`. |
| `Location` | Optional name/address and paired nullable latitude/longitude; `Coordinates` provides display text. |

All model media URLs use authenticated `/admin/media/{id}` URLs in previews and
snapshot-owned `/media/{id}/{filename}` URLs publicly. Use these URLs instead of
constructing paths. Razor encodes strings automatically: use `@Model.Post.Title`,
not casts to `MarkupString`. Only the provided sanitized HTML and rendered regions
should bypass encoding. Incomplete preview drafts can have empty text/media/steps
and null review/location/cover data. Templates must handle those cases.

For example, `Header.razor` can replace the header while retaining navigation:

```razor
<header>
    <a class="link" href="/">@Model.Site.Title</a>
    <nav aria-label="Main navigation">
        @foreach (var link in Model.Navigation)
        {
            <a class="link" href="@link.Url"
               aria-current="@(Model.Path == link.Url ? "page" : null)">@link.Label</a>
        }
    </nav>
</header>
```

`Main.razor` can replace the full layout while preserving metadata and composed
content. Main owns accessibility, the document's theme/accent attributes, metadata,
the skip link, preview disclosure, and the footer if it omits shared regions:

```razor
<!DOCTYPE html>
<html lang="en" data-public-theme="@Model.ThemeId"
      data-accent="@Model.Site.AccentColor.ToLowerInvariant()">
    @Model.Head
    <body>
        <a class="link" href="#main">Skip to content</a>
        @Model.Header
        @Model.Content
        @Model.Footer
    </body>
</html>
```

## CSS and JavaScript

The root has `data-public-theme` (the installation ID) and `data-accent` (the
lowercase accent preset). Style the semantic `header`, `main#main`, `footer`,
`article`, `nav`, and `figure` elements and existing daisyUI classes such as
`.card`, `.card-body`, `.card-title`, `.btn`, and `.prose-content`. Example:

```css
html[data-public-theme] body {
  background: var(--color-base-100);
  color: var(--color-base-content);
  font-family: Georgia, serif;
}
html[data-public-theme] .card {
  border-radius: 0;
}
```

Compiled Homestead CSS loads first, theme styles next in manifest order, and
optional color overrides last. These overrides set `--color-primary`,
`--color-primary-content`, `--color-base-100`, and `--color-base-content` on
`html[data-public-theme]`. Use these variables if declaring `supportsColors`.
Keep other surface colors compatible in both light and dark mode.

Styles are already compiled CSS: uploaded utility classes do not cause Tailwind
to run. Ship CSS for any additional layout classes. Use relative local references
for fonts and images (for example `url("fonts/reading.woff2")` from `theme.css`).
Homestead serves only same-origin theme assets under its Content Security Policy.
External fonts, inline styles/scripts, and embedded script handlers are unsupported.
Script files load with `defer` on public pages only. Keep scripts optional and
preserve keyboard, touch, narrow-screen, and reduced-motion usability.

## Storage, exports, and backups

Installed custom ZIPs have immutable content-hash IDs under private `data/themes`.
Built-ins are application files outside `wwwroot`. Each build copies theme assets
to its own `assets/theme/`, writes `assets/theme-colors.css`, and records the theme
identity and release version in the generation manifest. Theme manifests and
Razor sources are excluded from the public allowlist. Active pages never read
theme files from private storage or SQLite while serving traffic.

Static exports contain the active generation's theme and colors, even after a
failed desired change. Hugo exports carry the selected saved theme's
assets and colors in their migration layouts, plus the complete source package
under `homestead-theme/`. Razor sources never enter Hugo's `static/` or `public/`.
Hugo cannot execute Razor: exports use the shared Hugo layouts, carrying all
structured content and assets. Translate the preserved Razor templates to Hugo
templates when migrating their custom UI.

Backups include all installed custom packages and checksum them. Restore validates
package identities, manifests, and Razor templates, and requires the selected
custom package to be present. Restore with the same application version
into an empty data directory; startup rebuilds public output. Existing databases
and generation manifests default to Porch when theme fields are absent.
