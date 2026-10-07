using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin;

public sealed class IndexModel(HomesteadDbContext db, ActiveSite active) : PageModel
{
    public string SiteTitle { get; private set; } = "";
    public ActiveSite Site => active;
    public List<Post> RecentPosts { get; private set; } = [];
    public SiteGeneration? LatestBuild { get; private set; }
    public async Task OnGetAsync()
    {
        SiteTitle = await db.SiteSettings.Select(x => x.Title).SingleAsync();
        RecentPosts = await db.Posts.Include(x => x.Draft).Where(x => x.Status != PostStatus.Deleted).OrderByDescending(x => x.UpdatedAtUtc).Take(5).ToListAsync();
        LatestBuild = await db.SiteGenerations.OrderByDescending(x => x.BuiltAtUtc).FirstOrDefaultAsync();
    }
}
