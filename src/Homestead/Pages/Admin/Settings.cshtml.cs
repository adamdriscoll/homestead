using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Homestead.Features.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin;

[RequestSizeLimit(MediaService.MaximumBytes + 1024 * 1024)]
public sealed class SettingsModel(HomesteadDbContext db, IWebHostEnvironment environment, PublicationLock writer, PublishingService publishing, MediaService media) : PageModel
{
    [BindProperty] public SettingsInput Input { get; set; } = new();
    [TempData] public string? StatusMessage { get; set; }
    [BindProperty] public IFormFile? Upload { get; set; }
    [BindProperty] public string? Slot { get; set; }
    public SiteSettings Settings { get; private set; } = null!;
    public async Task OnGetAsync()
    {
        Settings = await db.SiteSettings.SingleAsync(); Input = SettingsInput.From(Settings); PadProfiles();
    }
    private void PadProfiles() { while (Input.ProfileLinks.Count < 5) Input.ProfileLinks.Add(new()); }

    public async Task<IActionResult> OnPostUploadAsync()
    {
        try
        {
            if (Upload is null) throw new InvalidOperationException("Choose an image first.");
            await media.UploadBrandingAsync(Slot ?? "", Upload);
            StatusMessage = "Branding image saved. " + (await publishing.RebuildAsync()).Message;
        }
        catch (InvalidOperationException ex) { StatusMessage = ex.Message; }
        return RedirectToPage();
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
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!environment.IsDevelopment() && !(Input.CanonicalBaseUrl ?? "").StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ModelState.AddModelError("Input.CanonicalBaseUrl", "Use HTTPS in production.");
        if (!ModelState.IsValid)
        {
            Settings = await db.SiteSettings.AsNoTracking().SingleAsync(); PadProfiles(); return Page();
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
        return RedirectToPage();
    }
}
