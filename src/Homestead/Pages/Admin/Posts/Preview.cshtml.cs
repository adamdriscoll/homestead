using Homestead.Data;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin.Posts;

public sealed class PreviewModel(ArticleService articles, HomesteadDbContext db, IStaticPageRenderer renderer) : PageModel
{
    public async Task<IActionResult> OnGetAsync(Guid id, Guid? image = null)
    {
        var post = await articles.FindAsync(id); if (post is null) return NotFound();
        var snapshot = ArticleService.Snapshot(post, post.Slug ?? "draft", DateTime.UtcNow);
        if (image is not null && (snapshot.Type != Homestead.Data.Entities.PostType.Gallery || !snapshot.Media.Any(x => x.Id == image)))
            return NotFound();
        if (snapshot.Type == Homestead.Data.Entities.PostType.Fitness && snapshot.Activity is not null)
        {
            Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: https://tile.openstreetmap.org; object-src 'none'; base-uri 'self'; frame-ancestors 'self'; form-action 'self'";
        }
        var settings = await db.SiteSettings.AsNoTracking().SingleAsync();
        var ids = new[] { settings.AvatarMediaId, settings.LogoMediaId, settings.HeaderMediaId, settings.FaviconMediaId }.OfType<Guid>().ToArray();
        var branding = (await db.MediaAssets.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync())
            .Select(x => new ArticleMedia(x.Id, x.StoredFilename, x.MimeType, settings.Title)).ToArray();
        return Content(await renderer.RenderAsync(new(settings, snapshot, [], true,
            Path: image is { } imageId ? GalleryImages.Path(snapshot, snapshot.Media.Single(x => x.Id == imageId)) : $"/posts/{snapshot.Slug}/",
            Branding: branding)
        { GalleryImageId = image }), "text/html");
    }
}
