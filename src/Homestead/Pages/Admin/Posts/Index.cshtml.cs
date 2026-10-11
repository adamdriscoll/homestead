using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin.Posts;

public sealed class IndexModel(HomesteadDbContext db, ActiveSite active, PublishingService publishing) : PageModel
{
    public List<Post> Posts { get; private set; } = [];
    public string TimeZone { get; private set; } = "UTC";
    public PostType[] EnabledPostTypes { get; private set; } = [];
    public ActiveSite Site => active;
    public static string DraftTitle(Post post) =>
        post.Type == PostType.Review && !string.IsNullOrWhiteSpace(post.Review?.Subject) ? post.Review.Subject :
        !string.IsNullOrWhiteSpace(post.Draft?.Title) ? post.Draft.Title :
        post.Type == PostType.Checkin && !string.IsNullOrWhiteSpace(post.Draft?.LocationName) ? $"Check-in at {post.Draft.LocationName}" :
        $"Untitled {post.Type switch { PostType.Microblog => "note", PostType.Checkin => "check-in", PostType.Fitness => "fitness activity", _ => post.Type.ToString().ToLowerInvariant() }}";
    [TempData] public string? StatusMessage { get; set; }
    public async Task OnGetAsync(PostStatus? status, bool scheduled = false)
    {
        var settings = await db.SiteSettings.AsNoTracking().SingleAsync();
        EnabledPostTypes = settings.EnabledPostTypes;
        TimeZone = settings.TimeZone;
        var liveIds = active.Current?.Articles.Select(x => x.PostId).ToArray() ?? [];
        var query = db.Posts.AsSplitQuery().Include(x => x.Draft).Include(x => x.PublishedSnapshot).Include(x => x.Schedule)
            .Include(x => x.Tags).ThenInclude(x => x.Tag).Include(x => x.Media)
            .Include(x => x.Review).Include(x => x.Steps).Include(x => x.Supplies)
            .Where(x => x.Status != PostStatus.Deleted || liveIds.Contains(x.Id));
        query = query.Where(x => EnabledPostTypes.Contains(x.Type) || scheduled && x.Schedule != null);
        if (status is not null) query = query.Where(x => x.Status == status);
        if (scheduled) query = query.Where(x => x.Schedule != null);
        Posts = await query.OrderByDescending(x => x.UpdatedAtUtc).ToListAsync();
    }
    public async Task<IActionResult> OnPostNewAsync(PostType type = PostType.Article)
    {
        if (!ModelState.IsValid || !Enum.IsDefined(type)) return BadRequest();
        if (!(await db.SiteSettings.AsNoTracking().SingleAsync()).IsPostTypeEnabled(type))
        {
            StatusMessage = "This post type is disabled. Enable it in Settings to start writing.";
            return RedirectToPage();
        }
        return RedirectToPage("Edit", new { id = Guid.NewGuid(), newPostType = type });
    }
    public async Task<IActionResult> OnPostRebuildAsync()
    {
        StatusMessage = (await publishing.RebuildAsync()).Message; return RedirectToPage();
    }
}
