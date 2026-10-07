using Homestead.Data;
using Homestead.Features.Publishing;
using Homestead.Features.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin;

public sealed class SettingsModel(HomesteadDbContext db, IWebHostEnvironment environment, PublicationLock writer, PublishingService publishing) : PageModel
{
    [BindProperty] public SettingsInput Input { get; set; } = new();
    [TempData] public string? StatusMessage { get; set; }
    public async Task OnGetAsync() => Input = SettingsInput.From(await db.SiteSettings.SingleAsync());
    public async Task<IActionResult> OnPostAsync()
    {
        if (!environment.IsDevelopment() && !(Input.CanonicalBaseUrl ?? "").StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ModelState.AddModelError("Input.CanonicalBaseUrl", "Use HTTPS in production.");
        if (!ModelState.IsValid) return Page();
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
