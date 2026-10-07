using Homestead.Features.Articles;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Homestead.Features.Publishing;

public sealed class SafeMarkdown
{
    private readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    // Full-content surfaces share this renderer, including private preview and RSS.
    public string RenderContent(ArticleSnapshot post, bool preview, string? canonicalBaseUrl = null)
    {
        static string Encode(string? text) => System.Net.WebUtility.HtmlEncode(text ?? "");
        string Photo(ArticleMedia image) => $"<figure><img src=\"{Encode(preview ? image.PrivateUrl : canonicalBaseUrl is null ? image.Url : canonicalBaseUrl.TrimEnd('/') + image.Url)}\" alt=\"{Encode(image.AltText)}\" class=\"w-full rounded-lg\" loading=\"lazy\">" +
            (string.IsNullOrWhiteSpace(image.Caption) ? "" : $"<figcaption>{Encode(image.Caption)}</figcaption>") + "</figure>";
        var html = post.Summary is { Length: > 0 } ? $"<p>{Encode(post.Summary)}</p>" : "";
        if (post.Type == Data.Entities.PostType.Review && post.Review is { } review)
        {
            html += $"<p><strong>{Encode(review.Subject)}</strong> · {review.Rating}/5</p>";
            if (!string.IsNullOrWhiteSpace(review.ExternalUrl) && PostValidation.IsExternalUrl(review.ExternalUrl))
                html += $"<p><a href=\"{Encode(review.ExternalUrl)}\" rel=\"noopener noreferrer\">Visit reviewed subject</a></p>";
        }
        if (post.Type == Data.Entities.PostType.Article && post.Media.SingleOrDefault(x => x.Id == post.CoverMediaId) is { } cover) html += Photo(cover);
        html += Render(post, preview, canonicalBaseUrl);
        if (post.Type == Data.Entities.PostType.Guide)
        {
            html += string.Concat(PhotosOutsideBody(post).Where(x => x.GuideStepId is null).Select(Photo));
            html += "<ol class=\"list-decimal pl-6 space-y-6\">";
            foreach (var step in post.Steps)
            {
                html += "<li>" + Render(post with { BodyMarkdown = step.BodyMarkdown }, preview, canonicalBaseUrl);
                html += string.Concat(PhotosOutsideBody(post).Where(x => x.GuideStepId == step.Id).Select(Photo)) + "</li>";
            }
            html += "</ol>";
        }
        else if (post.Type is Data.Entities.PostType.Microblog or Data.Entities.PostType.Review or Data.Entities.PostType.Gallery)
            html += "<div class=\"grid gap-3 sm:grid-cols-2\">" + string.Concat(PhotosOutsideBody(post).Select(Photo)) + "</div>";
        return html;
    }

    public ArticleMedia[] ReferencedMedia(ArticleSnapshot article)
    {
        var urls = InlineImageUrls(article);
        if (urls.Any(url => !article.Media.Any(media => media.Url == url)))
            throw new InvalidOperationException("Inline images must reference images uploaded to this article. Use the Image button to insert them.");
        if (article.CoverMediaId is { } id && !article.Media.Any(x => x.Id == id))
            throw new InvalidOperationException("The cover image is missing.");
        return article.Media.Where(x => article.Type != Data.Entities.PostType.Article || urls.Contains(x.Url) || x.Id == article.CoverMediaId).ToArray();
    }

    private HashSet<string?> InlineImageUrls(ArticleSnapshot article) => new[] { article.BodyMarkdown }.Concat(article.Steps.Select(x => x.BodyMarkdown))
        .SelectMany(text => Markdown.Parse(text, pipeline).Descendants<LinkInline>().Where(x => x.IsImage).Select(x => x.Url)).ToHashSet(StringComparer.Ordinal);

    public ArticleMedia[] PhotosOutsideBody(ArticleSnapshot article)
    {
        var urls = InlineImageUrls(article);
        return article.Media.Where(x => !urls.Contains(x.Url)).ToArray();
    }

    public string Render(ArticleSnapshot article, bool preview, string? canonicalBaseUrl = null)
    {
        var document = Markdown.Parse(article.BodyMarkdown, pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.IsImage)
            {
                var media = article.Media.SingleOrDefault(x => x.Url == link.Url);
                link.Url = media is null ? "/missing-image" : preview ? media.PrivateUrl : media.Url;
                if (media?.AltText is { Length: > 0 })
                {
                    while (link.FirstChild is { } child) child.Remove();
                    link.AppendChild(new LiteralInline(media.AltText));
                }
            }
            else if (!IsSafeLink(link.Url)) link.Url = "#";
            if (canonicalBaseUrl is not null && link.Url is { } url)
            {
                if (url.StartsWith('/')) link.Url = canonicalBaseUrl.TrimEnd('/') + url;
                else if (url.StartsWith('#')) link.Url = canonicalBaseUrl.TrimEnd('/') + $"/posts/{article.Slug}/" + url;
            }
        }
        using var output = new StringWriter();
        var renderer = new Markdig.Renderers.HtmlRenderer(output);
        pipeline.Setup(renderer); renderer.Render(document);
        return output.ToString();
    }

    private static bool IsSafeLink(string? url) => url is not null &&
        (url.StartsWith('#') || (url.StartsWith('/') && !url.StartsWith("//") && !url.Contains('\\')) ||
         (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto"));
}
