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
        return Content(await renderer.RenderAsync(new(await db.SiteSettings.AsNoTracking().SingleAsync(), snapshot, [], true)), "text/html");
    }
}
