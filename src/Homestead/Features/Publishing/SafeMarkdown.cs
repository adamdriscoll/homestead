using Homestead.Features.Articles;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Homestead.Features.Publishing;

public sealed class SafeMarkdown
{
    private static readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    // Full-content surfaces share this renderer, including private preview and RSS.
    public string RenderContent(ArticleSnapshot post, bool preview, string? canonicalBaseUrl = null, Data.Entities.FitnessUnitSystem fitnessUnits = Data.Entities.FitnessUnitSystem.Metric)
    {
        static string Encode(string? text) => System.Net.WebUtility.HtmlEncode(text ?? "");
        string Photo(ArticleMedia image)
        {
            var photo = $"<img src=\"{Encode(preview ? image.PrivateUrl : canonicalBaseUrl is null ? image.Url : canonicalBaseUrl.TrimEnd('/') + image.Url)}\" alt=\"{Encode(image.AltText)}\" class=\"w-full rounded-lg\" loading=\"lazy\">";
            if (post.Type == Data.Entities.PostType.Gallery)
            {
                var path = GalleryImages.Path(post, image, preview);
                photo = $"<a class=\"block\" href=\"{Encode(canonicalBaseUrl is null || preview ? path : canonicalBaseUrl.TrimEnd('/') + path)}\" aria-label=\"{Encode("View photo: " + GalleryImages.Title(post, image))}\">{photo}</a>";
            }
            return "<figure class=\"block\" data-post-photo>" + photo +
                (string.IsNullOrWhiteSpace(image.Caption) ? "" : $"<figcaption>{Encode(image.Caption)}</figcaption>") + "</figure>";
        }
        var html = PostIntro(post);
        if (post.Type == Data.Entities.PostType.Review && post.Review is { } review)
        {
            html += $"<div class=\"flex flex-wrap items-center gap-2\"><strong>{Encode(review.Subject)}</strong>{RenderRating(review.Rating)}</div>";
            if (!string.IsNullOrWhiteSpace(review.ExternalUrl) && PostValidation.IsExternalUrl(review.ExternalUrl))
                html += $"<p><a href=\"{Encode(review.ExternalUrl)}\" rel=\"noopener noreferrer\">Visit reviewed subject</a></p>";
        }
        if (post.Type == Data.Entities.PostType.Article && post.Media.SingleOrDefault(x => x.Id == post.CoverMediaId) is { } cover) html += Photo(cover);
        var body = Render(post, preview, canonicalBaseUrl);
        html += post.Type == Data.Entities.PostType.Gallery && canonicalBaseUrl is null && body.Length > 0
            ? $"<div data-gallery-body>{body}</div>" : body;
        if (post.Type == Data.Entities.PostType.Fitness && post.Activity is { } activity) html += FitnessRendering.Render(activity, fitnessUnits, preview);
        if (post.Type == Data.Entities.PostType.Guide)
        {
            string Supplies(string heading, GuideSupplySnapshot[] supplies) => supplies.Length == 0 ? "" :
                $"<section><h2>{heading}</h2><ul>" + string.Concat(supplies.Select(supply => "<li>" +
                    (!string.IsNullOrWhiteSpace(supply.Url) && PostValidation.IsExternalUrl(supply.Url)
                        ? $"<a href=\"{Encode(supply.Url)}\" rel=\"noopener noreferrer\">{Encode(supply.Name)}</a>" : Encode(supply.Name)) +
                    (string.IsNullOrWhiteSpace(supply.Quantity) ? "" : " — " + Encode(supply.Quantity)) + "</li>")) + "</ul></section>";
            html += Supplies("Tools", post.Tools) + Supplies("Materials", post.Materials);
            html += string.Concat(PhotosOutsideBody(post).Where(x => x.GuideStepId is null).Select(Photo));
            html += "<ol class=\"list-decimal pl-6 space-y-6\">";
            foreach (var step in post.Steps)
            {
                html += "<li>" + (string.IsNullOrWhiteSpace(step.Title) ? "" : $"<h2>{Encode(step.Title)}</h2>") +
                    (step.DurationText is { } duration ? $"<p>Time: <time datetime=\"PT{step.DurationSeconds}S\">{Encode(duration)}</time></p>" : "") +
                    Render(post with { BodyMarkdown = step.BodyMarkdown }, preview, canonicalBaseUrl);
                html += string.Concat(PhotosOutsideBody(post).Where(x => x.GuideStepId == step.Id).Select(Photo)) + "</li>";
            }
            html += "</ol>";
        }
        else if (post.Type is Data.Entities.PostType.Microblog or Data.Entities.PostType.Review or Data.Entities.PostType.Gallery or Data.Entities.PostType.Checkin or Data.Entities.PostType.Fitness)
        {
            var photos = PhotosOutsideBody(post);
            if (photos.Length > 0)
            {
                var layout = post.Type == Data.Entities.PostType.Gallery && canonicalBaseUrl is null
                    ? "class=\"grid gap-3\" data-gallery-photos" : "class=\"grid gap-3 sm:grid-cols-2\" data-post-photos";
                html += $"<div {layout}>" + string.Concat(photos.Select(Photo)) + "</div>";
            }
        }
        return html;
    }

    public string RenderGalleryIntro(ArticleSnapshot post, bool preview) =>
        PostIntro(post) + Render(post, preview, canonicalBaseUrl: null, galleryImagesAsLinks: true);

    private static string PostIntro(ArticleSnapshot post) => RenderLocation(post.Location) +
        (post.Summary is { Length: > 0 } ? $"<p>{System.Net.WebUtility.HtmlEncode(post.Summary)}</p>" : "");

    public static string RenderLocation(LocationSnapshot? location)
    {
        if (location is null) return "";
        var details = new[] { location.Name, location.Address, location.Coordinates }.Where(x => !string.IsNullOrWhiteSpace(x));
        return "<p>Location: " + string.Join(" · ", details.Select(System.Net.WebUtility.HtmlEncode)) + "</p>";
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

    private static HashSet<string?> InlineImageUrls(ArticleSnapshot article) => ImageUrls(new[] { article.BodyMarkdown }.Concat(article.Steps.Select(x => x.BodyMarkdown)));

    public static HashSet<string?> ImageUrls(IEnumerable<string> bodies) => bodies
        .SelectMany(text => Markdown.Parse(text, pipeline).Descendants<LinkInline>().Where(x => x.IsImage).Select(x => x.Url)).ToHashSet(StringComparer.Ordinal);

    public static string RenderRating(int? rating)
    {
        if (rating is null) return "<span>Not rated</span>";
        var stars = string.Concat(Enumerable.Range(1, 5).Select(star =>
            $"<div class=\"mask mask-star\" aria-hidden=\"true\"{(star == rating ? " aria-current=\"true\"" : "")}></div>"));
        return $"<div class=\"rating\" role=\"img\" aria-label=\"Rated {rating} out of 5\">{stars}</div><span class=\"sr-only\">{rating}/5</span>";
    }

    public ArticleMedia[] PhotosOutsideBody(ArticleSnapshot article)
    {
        var urls = InlineImageUrls(article);
        return article.Media.Where(x => !urls.Contains(x.Url)).ToArray();
    }

    public string Render(ArticleSnapshot article, bool preview, string? canonicalBaseUrl = null) =>
        Render(article, preview, canonicalBaseUrl, galleryImagesAsLinks: false);

    private static string Render(ArticleSnapshot article, bool preview, string? canonicalBaseUrl, bool galleryImagesAsLinks)
    {
        var document = Markdown.Parse(article.BodyMarkdown, pipeline);
        foreach (var link in document.Descendants<LinkInline>().ToArray())
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
                if (media is not null && article.Type == Data.Entities.PostType.Gallery)
                {
                    var path = GalleryImages.Path(article, media, preview);
                    var destination = canonicalBaseUrl is null || preview ? path : canonicalBaseUrl.TrimEnd('/') + path;
                    if (link.Parent is LinkInline parent && !parent.IsImage)
                    {
                        parent.Url = destination;
                        if (galleryImagesAsLinks)
                        {
                            link.InsertBefore(new LiteralInline(GalleryImages.Title(article, media)));
                            link.Remove();
                        }
                    }
                    else if (galleryImagesAsLinks)
                    {
                        link.IsImage = false;
                        link.Url = destination;
                        if (link.FirstChild is null) link.AppendChild(new LiteralInline(GalleryImages.Title(article, media)));
                    }
                    else
                    {
                        var wrapper = new LinkInline { Url = destination };
                        link.InsertBefore(wrapper);
                        link.Remove();
                        wrapper.AppendChild(link);
                    }
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
        pipeline.Setup(renderer);
        renderer.ObjectRenderers.Insert(0, new MermaidCodeRenderer());
        renderer.Render(document);
        return output.ToString();
    }

    private static bool IsSafeLink(string? url) => url is not null &&
        (url.StartsWith('#') || (url.StartsWith('/') && !url.StartsWith("//") && !url.Contains('\\')) ||
         (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto"));
}
