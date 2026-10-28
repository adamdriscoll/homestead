using Homestead.Features.Articles;

namespace Homestead.Features.Publishing;

public static class GalleryImages
{
    public static string Path(ArticleSnapshot post, ArticleMedia image, bool preview = false) => preview
        ? $"/admin/posts/{post.PostId}/preview?image={image.Id}"
        : $"/posts/{post.Slug}/photos/{image.Id:N}/";

    public static string Title(ArticleSnapshot post, ArticleMedia image) =>
        !string.IsNullOrWhiteSpace(image.Caption) ? image.Caption :
        !string.IsNullOrWhiteSpace(image.AltText) ? image.AltText : $"{post.DisplayTitle} · Photo {Array.IndexOf(post.Media, image) + 1}";
}
