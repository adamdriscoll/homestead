using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin;

public sealed class IndexModel(HomesteadDbContext db, ActiveSite active) : PageModel
{
    public string SiteTitle { get; private set; } = "";
    public PostType[] EnabledPostTypes { get; private set; } = [];
    public ActiveSite Site => active;
    public List<Post> RecentPosts { get; private set; } = [];
    public SiteGeneration? LatestBuild { get; private set; }
    public async Task OnGetAsync()
    {
        var settings = await db.SiteSettings.AsNoTracking().SingleAsync();
        SiteTitle = settings.Title; EnabledPostTypes = settings.EnabledPostTypes;
        RecentPosts = await db.Posts.Include(x => x.Draft).Include(x => x.Review).Where(x => x.Status != PostStatus.Deleted && EnabledPostTypes.Contains(x.Type)).OrderByDescending(x => x.UpdatedAtUtc).Take(5).ToListAsync();
        LatestBuild = await db.SiteGenerations.OrderByDescending(x => x.BuiltAtUtc).FirstOrDefaultAsync();
    }
}
