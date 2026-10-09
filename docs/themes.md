# Public themes

Select a theme in **Settings → Public theme** and save. Porch is the original
single-column feed, Journal uses a narrower serif reading layout, Ledger uses
a wider masthead and two-column feed on larger screens, and Folio pairs a bold
accent-colored cover with spacious typography and serif reading text. Town Square
uses a Facebook-inspired social feed with author avatars, rounded post cards,
a profile cover, and a desktop introduction sidebar. Each supports every post
type, ordinary navigation without JavaScript, system light/dark appearance, the
four accent presets, and optional primary, background, and text colors.

Folio uses engine 2 Razor templates for the document, navigation, footer, and
post layouts, including a distinct note layout and structured review details in
listings. Its original templates and styling take inspiration from
[Typology by Meks on ThemeForest](https://themeforest.net/item/typology-text-based-minimal-wordpress-blog-theme/19547842).
No purchased assets or external fonts are required. The sources in
`src/Homestead/Themes/folio` also serve as a built-in Razor authoring example.

Town Square is included as a built-in option under **Settings → Public theme**.
Its engine 2 Razor sources live in `src/Homestead/Themes/townsquare`. Notes,
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

The shared footer shows “Made at home with Homestead”,
with Homestead linking to the project repository. To remove it, check
**Settings → About → Hide Homestead credit** and save. The preference also
applies to private previews and Hugo exports; static exports keep the currently
active footer. Razor themes replacing the footer or entire document must honor
`Model.Site.HideHomesteadCredit` themselves, or include the shared footer.

## Install a custom theme

Upload a ZIP in **Settings → Install a custom public theme**, then select the
installed theme and save. Uploading alone does not change the public site. An
identical upload reuses its installation. Changed packages install separately,
allowing you to select an earlier version. Save text settings before uploading.

Themes can contain JavaScript that runs on the public site's origin. Engine 2
Razor templates also execute C# on the server, with the application's permissions
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

Supported files are `.css`, `.js`, `.html`, `.png`, `.jpg`, `.jpeg`, `.webp`,
`.woff`, `.woff2`, and `.razor`, plus the root `theme.json`. HTML and Razor files
are private sources and are never served as assets. Standalone C# files, DLLs,
project files, and `.cshtml` are not accepted.

## Package format and compatibility

The manifest has its own `formatVersion`. Each variant declares an independent
theme engine version, supported post types, and entry resources. Engine **2** adds
Razor templates and typed models. Engine **1** retains the shared regions and
constrained HTML layout contract. Package `version` is the author's
release label, independent of both format and engine versions.

```text
theme.json
v1/theme.css
v1/theme.js
v1/layout.html
v1/fonts/reading.woff2
v2/theme.css
```

```json
{
  "formatVersion": 1,
  "name": "Garden",
  "version": "1.0.0",
  "variants": [
    {
      "engineVersion": 1,
      "postTypes": ["microblog", "article", "review", "guide", "gallery", "checkin"],
      "styles": ["v1/theme.css"],
      "scripts": ["v1/theme.js"],
      "supportsColors": true,
      "layout": "v1/layout.html"
    },
    {
      "engineVersion": 2,
      "postTypes": ["microblog", "article", "review", "guide", "gallery", "checkin"],
      "styles": ["v2/theme.css"],
      "scripts": []
    }
  ]
}
```

One variant per engine version is allowed. Homestead chooses engine 2 if present,
otherwise engine 1. An invalid engine 2 variant is rejected rather than silently
using engine 1. Packages lacking both versions are rejected.
Check-ins use `checkin` in new manifests. Existing engine 1 packages declaring the
original five types remain compatible: shared Razor regions render check-ins and
locations using the existing semantic markup. No engine-version change is needed.
Engine 2 must declare all six post types. Both engines call notes `microblog` in
the manifest. Other variants can bundle files
for future or earlier engines; they do not alter which styles or scripts load.
All referenced current styles/scripts/layouts must exist. `styles` needs at least
one entry; `scripts` can be empty. Both arrays have a maximum of 16 entries.
`supportsColors` defaults to false, and `layout` is optional. Names have a maximum
of 80 characters, release labels 40, and manifests/layouts/Razor sources 64 KB each.

## Engine 1 HTML layouts

Homestead compiles its public Razor components with the application.
[`SiteDocument.razor`](../src/Homestead/Components/Public/SiteDocument.razor)
renders the document head, header, main content, and footer;
[`PostContent.razor`](../src/Homestead/Components/Public/PostContent.razor)
renders all six post types and optional locations. This keeps escaping, metadata,
media resolution, previews, and no-JavaScript navigation consistent across themes.

Engine 1 packages arrange those rendered regions using a constrained HTML layout.
Their layouts do not execute server-side code. Include each slot exactly once:

```html
<div class="garden-layout">
  {{header}}
  <section aria-label="Posts">{{main}}</section>
  {{footer}}
</div>
```

Layouts accept balanced `div`, `section`, and `aside` wrappers with `class` and
`aria-label` attributes. Literal text, other attributes/elements, comments,
processing instructions, and unknown slots are rejected. Without a layout,
regions appear in header/main/footer order. The head and skip link remain
application-owned. Changing the Razor content contract requires an application
build and, when incompatible, an engine-version increment.

## Engine 2 Razor templates

Start with the [Razor Garden example](../examples/themes/razor-garden/theme.json).
ZIP the contents of that directory with `theme.json` at the root. Its `Main.razor`
replaces the document, `Header.razor` replaces the header, and `Note.razor` replaces
notes. Remove any template to use the shared default for that part.

An engine 2 variant can use named files at the ZIP root (`Main.razor`,
`Header.razor`, etc.) or map names to source files explicitly:

```json
{
  "formatVersion": 1,
  "name": "Razor Garden",
  "version": "2.0.0",
  "variants": [{
    "engineVersion": 2,
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
  }]
}
```

Names and paths are case-sensitive. Explicit mappings take precedence over
root filenames. A mapping must name one of the following templates and reference
an existing `.razor` source. All mapped templates compile together, including
ones that currently have no matching posts. Unreferenced sources remain private.

| Template | Replacement and fallback |
| --- | --- |
| `Main` | Entire HTML document. Without it, Homestead supplies the document, head, skip link, and configured HTML layout. |
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

Compiled Homestead CSS loads first, variant styles next in manifest order, and
optional color overrides last. These overrides set `--color-primary`,
`--color-primary-content`, `--color-base-100`, and `--color-base-content` on
`html[data-public-theme]`. Use these variables if declaring `supportsColors`.
Keep other surface colors compatible in both light and dark mode.

Styles are already compiled CSS: uploaded utility classes do not cause Tailwind
to run. Ship CSS for any additional layout classes. Use relative local references
for fonts and images (for example `url("fonts/reading.woff2")` from `v1/theme.css`).
Homestead serves only same-origin theme assets under its Content Security Policy.
External fonts, inline styles/scripts, and embedded script handlers are unsupported.
Script files load with `defer` on public pages only. Keep scripts optional and
preserve keyboard, touch, narrow-screen, and reduced-motion usability.

## Storage, exports, and backups

Installed custom ZIPs have immutable content-hash IDs under private `data/themes`.
Built-ins are application files outside `wwwroot`. Each build copies theme assets
to its own `assets/theme/`, writes `assets/theme-colors.css`, and records the theme
identity/version/selected engine in the generation manifest. Theme manifests and layout/Razor sources
are excluded from the public allowlist. Active pages never read theme files from
private storage or SQLite while serving traffic.

Static exports contain the active generation's theme and colors, even after a
failed desired change. Hugo exports carry the selected saved theme's layout,
assets, and colors in their migration layouts, plus the complete source package
under `homestead-theme/`. Razor sources never enter Hugo's `static/` or `public/`.
Hugo cannot execute Razor: engine 2 exports use the shared Hugo layouts and any
optional HTML layout, carrying all structured content and assets. Translate the
preserved Razor templates to Hugo templates when migrating their custom UI.

Backups include all installed custom packages and checksum them. Restore validates
their package identities and current engine compatibility, and requires the
selected custom package to be present. Restore with the same application version
into an empty data directory; startup rebuilds public output. Existing databases
and generation manifests default to Porch when theme fields are absent.
