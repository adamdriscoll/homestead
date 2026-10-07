# Homestead

A self-hostable home for your digital life.

Homestead v2 is a personal publishing platform for microblogs, articles, reviews,
guides, and photo galleries. The application uses ASP.NET Core on .NET 10,
Razor Pages, and SQLite, with a static public website regenerated on publication.

Phases 1–3 are implemented: secure single-administrator setup, site settings,
a responsive Markdown article editor with autosave and conflict detection, private
JPEG/PNG/WebP uploads, preview, and full static publication. Publish, update,
unpublish, and delete replace the complete public site atomically; failures keep
the previous site available. Notes support text, photos, or both, with optional titles.
The public site includes a mixed feed, type listings, tags, About, RSS, sitemap,
canonical URLs, and social metadata. Branding includes four accent presets,
avatar/logo/header uploads, and external profiles. Review, guide, and gallery editors
arrive in Phase 4.

## Run locally

Install the .NET 10 SDK and Node.js 24. From the repository root in PowerShell:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:HOMESTEAD_BASE_URL = 'http://localhost:5080'
$env:HOMESTEAD_DATA_PATH = Join-Path (Get-Location) 'data'
$env:HOMESTEAD_SETUP_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet restore
dotnet build
dotnet test --no-build
dotnet run --project src/Homestead --no-launch-profile --urls http://localhost:5080
```

Open [first-run setup](http://localhost:5080/setup) and enter the generated secret
(inspect `$env:HOMESTEAD_SETUP_TOKEN` in your local terminal), email, and a strong
password. Sign in, open **Posts**, and choose **New article**. Write a title/body,
upload a photo, preview, then publish. Choose **New note** for a shorter update or
photo-only post. Open **Settings** to personalize the public site; saving settings
or uploading branding images rebuilds it. Later edits stay private until **Update
published post**. Remove the setup token
from your environment after setup; future startups use the saved administrator.

The build compiles Tailwind/daisyUI CSS locally; there is no frontend server or
Node.js dependency in the production runtime. SQLite and Data Protection keys
live in the configured data directory, outside the public assets.

See [deployment instructions](docs/deploy.md) for Linux/Docker Compose,
[contributing](docs/contributing.md) for quality checks, and
[Phase 1 decisions and verification](docs/phase-1.md),
[Phase 2 publishing and verification](docs/phase-2.md), and
[Phase 3 public website and verification](docs/phase-3.md) for acceptance evidence and
the real-phone demo checklist.

## Planning documents

Start with the [planning index and review](docs/planning/README.md), then read:

1. [Product specification](docs/planning/SPEC.md) — MVP scope, user journeys, and release acceptance criteria.
2. [Technical architecture](docs/planning/ARCHITECTURE.md) — application structure, content storage, static publishing, security, and exports.
3. [Implementation plan](docs/planning/IMPLEMENTATION.md) — five delivery phases with task dependencies and acceptance checks.

## License

[MIT](LICENSE).
