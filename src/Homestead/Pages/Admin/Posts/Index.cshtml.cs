using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin.Posts;

public sealed class IndexModel(HomesteadDbContext db, ArticleService articles, ActiveSite active, PublishingService publishing) : PageModel
{
    public List<Post> Posts { get; private set; } = [];
    public ActiveSite Site => active;
    [TempData] public string? StatusMessage { get; set; }
    public async Task OnGetAsync(PostStatus? status)
    {
        var liveIds = active.Current?.Articles.Select(x => x.PostId).ToArray() ?? [];
        var query = db.Posts.AsSplitQuery().Include(x => x.Draft).Include(x => x.PublishedSnapshot)
            .Include(x => x.Tags).ThenInclude(x => x.Tag).Include(x => x.Media)
            .Where(x => x.Status != PostStatus.Deleted || liveIds.Contains(x.Id));
        if (status is not null) query = query.Where(x => x.Status == status);
        Posts = await query.OrderByDescending(x => x.UpdatedAtUtc).ToListAsync();
    }
    public async Task<IActionResult> OnPostNewAsync()
    {
        var post = await articles.CreateAsync();
        return RedirectToPage("Edit", new { id = post.Id });
    }
    public async Task<IActionResult> OnPostRebuildAsync()
    {
        StatusMessage = (await publishing.RebuildAsync()).Message; return RedirectToPage();
    }
}
