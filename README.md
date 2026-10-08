# Homestead

A self-hostable home for your digital life.

Homestead is a personal publishing platform for one author. Share a short note,
write an article or review, explain something in a step-by-step guide, or collect
photos in a gallery, or check in at a place you visited. Author from a phone or
desktop with Markdown, photo uploads, autosave, and private previews.

Your public website is made of static files. Publishing regenerates the home feed,
post pages, type listings, tags, About page, RSS, sitemap, and sharing metadata as
one complete site. Draft edits stay private until you publish an update. If a build
fails, the previous site stays available and the administrator can retry.

Every post can include a location: a place name, address, and optional paired
latitude/longitude. Choose **New check-in** for a visit; only the place name is
required to publish, with optional title, text, and photos. Location details appear
on public posts, listings, RSS, and exports after publication. Draft changes stay
private, including changes to a published location. Clear the location fields and
publish an update to remove a location from other post types.

The Porch, Journal, and Ledger public themes support light/dark appearance, accent
presets, author profiles, and avatar, logo, and header images. Install custom ZIP
themes and adjust basic colors in Settings; see [public themes](docs/themes.md).
Your content stays portable: download the
public site as a standalone static ZIP, export a Hugo project with Markdown and
photos, or take a complete backup for restoring your installation.

Homestead uses ASP.NET Core on .NET 10, Razor Pages, and SQLite. It runs as one
application with a persistent data directory; the Docker Compose configuration
includes Caddy for HTTPS. Node.js builds the CSS and is not needed at runtime.

## Quick start

Install the .NET 10 SDK and Node.js 24. From the repository root in PowerShell 7:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:HOMESTEAD_BASE_URL = 'http://localhost:5080'
$env:HOMESTEAD_DATA_PATH = Join-Path (Get-Location) 'data'
$env:HOMESTEAD_SETUP_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
# Keep this token for the setup form below.
$env:HOMESTEAD_SETUP_TOKEN
dotnet restore
dotnet build
dotnet run --project src/Homestead --no-build --no-launch-profile --urls http://localhost:5080
```

Open [first-run setup](http://localhost:5080/setup), enter the generated token,
and create your administrator account. Sign in, personalize your site in
**Settings**, then choose a post type in **Posts**. Save, upload photos, preview,
and publish. Your public site is at [localhost:5080](http://localhost:5080/).

After setup, remove `HOMESTEAD_SETUP_TOKEN` from your environment; subsequent
startups use the saved account. Keep the data directory to retain your content
and login keys. For production hosting with Docker and HTTPS, follow the
[deployment guide](docs/deploy.md).

## Documentation and development

- [Contributing](docs/contributing.md): development tools, tests, quality checks, and migrations.
- [Deployment](docs/deploy.md): Docker Compose, HTTPS, configuration, upgrades, and troubleshooting.
- [Backup and restore](docs/backup-restore.md): protecting your data and moving to a new installation.
- [Public themes](docs/themes.md): selecting, customizing, and authoring theme packages.
- [Agent guidance](AGENTS.md): architecture, project conventions, and security boundaries.
- [Changelog](CHANGELOG.md): release history.
- [GitHub issues](https://github.com/PoshTools/homestead/issues): remaining work and feature proposals.

The current version is a release candidate. Real-device and public-deployment
acceptance is tracked in [release readiness](https://github.com/PoshTools/homestead/issues/22).

## License

[MIT](LICENSE).
