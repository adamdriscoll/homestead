using Homestead.Features.Articles;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Homestead.Features.Publishing;

public sealed class SafeMarkdown
{
    private readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public ArticleMedia[] ReferencedMedia(ArticleSnapshot article)
    {
        var urls = InlineImageUrls(article);
        if (urls.Any(url => !article.Media.Any(media => media.Url == url)))
            throw new InvalidOperationException("Inline images must reference images uploaded to this article. Use the Image button to insert them.");
        if (article.CoverMediaId is { } id && !article.Media.Any(x => x.Id == id))
            throw new InvalidOperationException("The cover image is missing.");
        return article.Media.Where(x => article.Type == Data.Entities.PostType.Microblog || urls.Contains(x.Url) || x.Id == article.CoverMediaId).ToArray();
    }

    private HashSet<string?> InlineImageUrls(ArticleSnapshot article) => Markdown.Parse(article.BodyMarkdown, pipeline)
        .Descendants<LinkInline>().Where(x => x.IsImage).Select(x => x.Url).ToHashSet(StringComparer.Ordinal);

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
