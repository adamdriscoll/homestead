using Homestead.Data;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin.Posts;

public sealed class PreviewModel(ArticleService articles, HomesteadDbContext db, IStaticPageRenderer renderer) : PageModel
{
    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var post = await articles.FindAsync(id); if (post is null) return NotFound();
        var snapshot = ArticleService.Snapshot(post, post.Slug ?? "draft", DateTime.UtcNow);
        var settings = await db.SiteSettings.AsNoTracking().SingleAsync();
        var ids = new[] { settings.AvatarMediaId, settings.LogoMediaId, settings.HeaderMediaId }.OfType<Guid>().ToArray();
        var branding = (await db.MediaAssets.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync())
            .Select(x => new ArticleMedia(x.Id, x.StoredFilename, x.MimeType, settings.Title)).ToArray();
        return Content(await renderer.RenderAsync(new(settings, snapshot, [], true, Branding: branding)), "text/html");
    }
}
