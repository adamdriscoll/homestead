using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Analytics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin;

public sealed class AnalyticsModel(HomesteadDbContext db, AnalyticsService analytics) : PageModel
{
    [BindProperty] public bool Enabled { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [TempData] public string? StatusMessage { get; set; }
    public long TotalHits { get; private set; }
    public int TotalPages { get; private set; }
    public List<PageViews> Pages { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Enabled = analytics.Enabled;
        TotalHits = await db.PageViews.SumAsync(x => (long?)x.Hits) ?? 0;
        var count = await db.PageViews.CountAsync();
        TotalPages = Math.Max(1, (int)Math.Ceiling(count / 50d));
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        Pages = await db.PageViews.AsNoTracking().OrderByDescending(x => x.Hits).ThenBy(x => x.Path)
            .Skip((PageNumber - 1) * 50).Take(50).ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return BadRequest();
        await analytics.SetEnabledAsync(Enabled, HttpContext.RequestAborted);
        StatusMessage = Enabled ? "Analytics enabled. New anonymous page views will be counted." : "Analytics disabled. Existing counts are retained.";
        return RedirectToPage();
    }
}
