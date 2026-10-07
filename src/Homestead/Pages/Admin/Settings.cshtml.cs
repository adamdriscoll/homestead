using Homestead.Data;
using Homestead.Features.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin;

public sealed class SettingsModel(HomesteadDbContext db, IWebHostEnvironment environment) : PageModel
{
    [BindProperty] public SettingsInput Input { get; set; } = new();
    [TempData] public string? StatusMessage { get; set; }
    public async Task OnGetAsync() => Input = SettingsInput.From(await db.SiteSettings.SingleAsync());
    public async Task<IActionResult> OnPostAsync()
    {
        if (!environment.IsDevelopment() && !(Input.CanonicalBaseUrl ?? "").StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ModelState.AddModelError("Input.CanonicalBaseUrl", "Use HTTPS in production.");
        if (!ModelState.IsValid) return Page();
        Input.ApplyTo(await db.SiteSettings.SingleAsync());
        await db.SaveChangesAsync();
        StatusMessage = "Settings saved.";
        return RedirectToPage();
    }
}
