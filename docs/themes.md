# Public themes

Select a theme in **Settings → Public theme** and save. Porch is the original
single-column feed, Journal uses a narrower serif reading layout, and Ledger uses
a wider masthead and two-column feed on larger screens. Each supports every post
type, ordinary navigation without JavaScript, system light/dark appearance, the
four accent presets, and optional primary, background, and text colors.

Colors use six-digit hex values such as `#25613e`. Empty fields restore theme
defaults. Overrides apply in both light and dark mode, so check that your text and
background remain readable in each. Primary foreground color is chosen
automatically for contrast. A custom theme can decline color overrides.

Administration has a separate **Appearance** selector for System, Light, or Dark.
That preference is stored in the current browser and does not change public pages.

Settings are saved before building. A successful build atomically activates all
theme changes. If it fails, the previous complete site stays public; Settings
shows the active theme and the rebuild failure. Use **Rebuild site** to retry.

## Install a custom theme

Upload a ZIP in **Settings → Install a custom public theme**, then select the
installed theme and save. Uploading alone does not change the public site. An
identical upload reuses its installation. Changed packages install separately,
allowing you to select an earlier version. Save text settings before uploading.

Themes can contain JavaScript that runs on the public site's origin. Install only
packages from trusted authors. Private previews render the selected layout and
CSS with authenticated asset URLs, omit theme scripts, and prohibit scripts with
Content Security Policy. Public content and navigation must work without scripts.

ZIPs may be at most 5 MB compressed and 20 MB expanded, with at most 128 entries.
Put `theme.json` at the ZIP root; don't wrap the package in an extra directory.
Directory entries are allowed. Filenames use letters, numbers, dots, underscores,
and hyphens, start with a letter or number, and use `/` between directories.
Traversal, absolute paths, symbolic links, duplicate paths (including differences
only in case), Windows device names, and unsupported files are rejected. Paths
are case-sensitive when referenced by the manifest.

Supported files are `.css`, `.js`, `.html`, `.png`, `.jpg`, `.jpeg`, `.webp`,
`.woff`, and `.woff2`, plus the root `theme.json`. HTML files are layout sources
and are never served as assets. C# and executable Razor files are not accepted.

## Package format and compatibility

The manifest has its own `formatVersion`. Each variant declares an independent
theme engine version, supported post types, and entry resources. Engine **1** is
the current shared Razor rendering contract. Package `version` is the author's
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
      "postTypes": ["microblog", "article", "review", "guide", "gallery"],
      "styles": ["v1/theme.css"],
      "scripts": ["v1/theme.js"],
      "supportsColors": true,
      "layout": "v1/layout.html"
    },
    {
      "engineVersion": 2,
      "postTypes": ["microblog", "article", "review", "guide", "gallery"],
      "styles": ["v2/theme.css"],
      "scripts": []
    }
  ]
}
```

One variant per engine version is allowed. Homestead chooses only the current
engine variant and rejects packages that lack it or omit any current post type.
Engine 1 calls notes `microblog` in the manifest. Other variants can bundle files
for future or earlier engines; they do not alter which styles or scripts load.
All referenced current styles/scripts/layouts must exist. `styles` needs at least
one entry; `scripts` can be empty. Both arrays have a maximum of 16 entries.
`supportsColors` defaults to false, and `layout` is optional. Names have a maximum
of 80 characters, release labels 40, and manifests/layouts 64 KB each.

## Layouts and Razor source

Homestead compiles its public Razor components with the application.
[`SiteDocument.razor`](../src/Homestead/Components/Public/SiteDocument.razor)
renders the document head, header, main content, and footer;
[`PostContent.razor`](../src/Homestead/Components/Public/PostContent.razor)
renders all five post types. This keeps escaping, metadata, media resolution,
previews, and no-JavaScript navigation consistent across themes.

Custom packages arrange those rendered regions using a constrained HTML layout.
They do not execute server-side code. Include each slot exactly once:

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
identity/version in the generation manifest. Theme manifests and layout sources
are excluded from the public allowlist. Active pages never read theme files from
private storage or SQLite while serving traffic.

Static exports contain the active generation's theme and colors, even after a
failed desired change. Hugo exports carry the selected saved theme's layout,
assets, and colors in their migration layouts, plus the complete source package
under `homestead-theme/`. Hugo remains a portable starting point rather than an
exact reproduction of the public Razor templates.

Backups include all installed custom packages and checksum them. Restore validates
their package identities and current engine compatibility, and requires the
selected custom package to be present. Restore with the same application version
into an empty data directory; startup rebuilds public output. Existing databases
and generation manifests default to Porch when theme fields are absent.
