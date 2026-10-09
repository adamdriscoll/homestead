using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Export;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Homestead.Features.Settings;
using Homestead.Features.Themes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin;

[RequestSizeLimit(MediaService.MaximumBytes + 1024 * 1024)]
public sealed class SettingsModel(HomesteadDbContext db, IWebHostEnvironment environment, PublicationLock writer, PublishingService publishing, MediaService media, ThemeService themes, ActiveSite active, AutomatedBackupService automatedBackups) : PageModel
{
    [BindProperty] public SettingsInput Input { get; set; } = new();
    [BindProperty] public string? Section { get; set; } = "general";
    [TempData] public string? StatusMessage { get; set; }
    [BindProperty] public IFormFile? Upload { get; set; }
    [BindProperty] public string? Slot { get; set; }
    public SiteSettings Settings { get; private set; } = null!;
    [BindProperty] public BackupInput Backup { get; set; } = new();
    public BackupSettings BackupStatus { get; private set; } = new();
    public IReadOnlyList<ThemeSnapshot> Themes { get; private set; } = [];
    public ThemeSnapshot? ActiveTheme => active.Current?.Theme;
    private void LoadThemes() => Themes = themes.Installed();
    public async Task OnGetAsync()
    {
        Settings = await db.SiteSettings.SingleAsync(); Input = SettingsInput.From(Settings); PadProfiles(); LoadThemes();
        BackupStatus = await db.BackupSettings.AsNoTracking().SingleOrDefaultAsync() ?? new();
        Backup = BackupInput.From(BackupStatus);
    }
    private void PadProfiles() { while (Input.ProfileLinks.Count < 5) Input.ProfileLinks.Add(new()); }

    public async Task<IActionResult> OnPostBackupAsync()
    {
        // This independent form does not submit the public settings model.
        foreach (var key in ModelState.Keys.Where(key => !key.StartsWith("Backup.", StringComparison.Ordinal)).ToArray())
            ModelState.Remove(key);
        TryValidateModel(Backup, nameof(Backup));
        if (ModelState.IsValid)
        {
            try
            {
                await automatedBackups.SaveAsync(Backup, HttpContext.RequestAborted);
                StatusMessage = "Backup settings saved.";
                return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "backup");
            }
            catch (InvalidOperationException ex) { ModelState.AddModelError("Backup.Enabled", ex.Message); }
        }
        if (ModelState.TryGetValue("Backup.AccessKey", out var accessState) && accessState.Errors.Count > 0 ||
            ModelState.TryGetValue("Backup.SecretKey", out var secretState) && secretState.Errors.Count > 0)
            ModelState.AddModelError("Backup", "Credential fields must be at most 1024 characters. Enter them again after correcting the form.");
        Backup.AccessKey = null; Backup.SecretKey = null;
        ModelState.Remove("Backup.AccessKey"); ModelState.Remove("Backup.SecretKey");
        Settings = await db.SiteSettings.AsNoTracking().SingleAsync(); Input = SettingsInput.From(Settings); PadProfiles(); LoadThemes();
        BackupStatus = await db.BackupSettings.AsNoTracking().SingleOrDefaultAsync() ?? new();
        Section = "backup";
        return Page();
    }

    public async Task<IActionResult> OnPostBackupNowAsync()
    {
        var result = await automatedBackups.RunAsync(ct: HttpContext.RequestAborted);
        StatusMessage = result.Message;
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "backup");
    }

    public async Task<IActionResult> OnPostThemeAsync()
    {
        try
        {
            if (Upload is null) throw new InvalidDataException("Choose a theme ZIP first.");
            var theme = await themes.InstallAsync(Upload, HttpContext.RequestAborted);
            StatusMessage = $"{theme.Name} {theme.Version} installed. Select it in Theme and save settings to apply it.";
        }
        catch (InvalidDataException ex) { StatusMessage = ex.Message; }
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "theme");
    }

    public async Task<IActionResult> OnPostUploadAsync()
    {
        try
        {
            if (Upload is null) throw new InvalidOperationException("Choose an image first.");
            await media.UploadBrandingAsync(Slot ?? "", Upload);
            StatusMessage = "Branding image saved. " + (await publishing.RebuildAsync()).Message;
        }
        catch (InvalidOperationException ex) { StatusMessage = ex.Message; }
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "images");
    }

    public async Task<IActionResult> OnPostRemoveAsync()
    {
        await writer.Semaphore.WaitAsync();
        try
        {
            var settings = await db.SiteSettings.SingleAsync();
            switch (Slot)
            {
                case "Avatar": settings.AvatarMediaId = null; break;
                case "Logo": settings.LogoMediaId = null; break;
                case "Header": settings.HeaderMediaId = null; break;
                default: return BadRequest();
            }
            await db.SaveChangesAsync();
            StatusMessage = "Branding image removed from settings. " + (await publishing.BuildLockedAsync()).Message;
        }
        finally { writer.Semaphore.Release(); }
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "images");
    }
    public async Task<IActionResult> OnPostAsync()
    {
        try { _ = themes.Resolve(Input.ThemeId); }
        catch (InvalidDataException ex) { ModelState.AddModelError("Input.ThemeId", ex.Message); }
        if (!environment.IsDevelopment() && !(Input.CanonicalBaseUrl ?? "").StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ModelState.AddModelError("Input.CanonicalBaseUrl", "Use HTTPS in production.");
        if (!ModelState.IsValid)
        {
            Settings = await db.SiteSettings.AsNoTracking().SingleAsync(); PadProfiles(); LoadThemes();
            BackupStatus = await db.BackupSettings.AsNoTracking().SingleOrDefaultAsync() ?? new();
            Backup = BackupInput.From(BackupStatus);
            return Page();
        }
        await writer.Semaphore.WaitAsync();
        try
        {
            Input.ApplyTo(await db.SiteSettings.SingleAsync());
            await db.SaveChangesAsync();
            var result = await publishing.BuildLockedAsync();
            StatusMessage = result.Succeeded ? "Settings saved and public site rebuilt." : "Settings saved. " + result.Message;
        }
        finally { writer.Semaphore.Release(); }
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null,
            fragment: Section is "posts" or "social" or "theme" or "about" or "images" ? Section : "general");
    }
}
