using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Homestead.Pages.Admin.Posts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin;

public sealed class IndexModel(HomesteadDbContext db, ActiveSite active, ArticleService articles, MediaService media, PublishingService publishing)
    : PostEditorModel(articles, media, publishing, active, db)
{
    public string SiteTitle { get; private set; } = "";
    public string TimeZone { get; private set; } = "UTC";
    public List<Post> RecentPosts { get; private set; } = [];
    public SiteGeneration? LatestBuild { get; private set; }
    public override string EditorPage => "/Admin/Index";
    public override async Task<IActionResult> OnGetAsync(Guid id)
    {
        await LoadDashboardAsync();
        if (!ModelState.IsValid || NewPostType is { } type && !Enum.IsDefined(type)) return BadRequest();
        if (NewPostType is { } requestedType && requestedType != PostType.Microblog)
            return RedirectToPage("/Admin/Posts/Edit", new { id = id == Guid.Empty ? Guid.NewGuid() : id, newPostType = requestedType });
        if (id != Guid.Empty && await Database.Posts.AsNoTracking().AnyAsync(x => x.Id == id && x.Type != PostType.Microblog && x.Status != PostStatus.Deleted))
            return RedirectToPage("/Admin/Posts/Edit", new { id });
        if (!EnabledPostTypes.Contains(PostType.Microblog)) return Page();
        if (id == Guid.Empty) { id = Guid.NewGuid(); NewPostType = PostType.Microblog; }
        return await base.OnGetAsync(id);
    }
    protected override async Task<bool> LoadEditorAsync(Guid id)
    {
        await LoadDashboardAsync();
        return await base.LoadEditorAsync(id) && Post.Type == PostType.Microblog;
    }
    protected override IActionResult AfterTransition(Guid id, string action, PublicationResult result)
        => action == "publish" && result.Succeeded ? RedirectToPage() : base.AfterTransition(id, action, result);
    private async Task LoadDashboardAsync()
    {
        var settings = await Database.SiteSettings.AsNoTracking().SingleAsync();
        SiteTitle = settings.Title; EnabledPostTypes = settings.EnabledPostTypes;
        TimeZone = settings.TimeZone;
        RecentPosts = await Database.Posts.Include(x => x.Draft).Include(x => x.Review).Where(x => x.Status != PostStatus.Deleted && EnabledPostTypes.Contains(x.Type)).OrderByDescending(x => x.UpdatedAtUtc).Take(5).ToListAsync();
        LatestBuild = await Database.SiteGenerations.OrderByDescending(x => x.BuiltAtUtc).FirstOrDefaultAsync();
    }
}
