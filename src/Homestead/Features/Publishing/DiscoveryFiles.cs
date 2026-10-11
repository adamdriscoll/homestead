using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Homestead.Data.Entities;
using Homestead.Features.Articles;

namespace Homestead.Features.Publishing;

public static partial class DiscoveryFiles
{
    public static string Absolute(SiteSettings settings, string path) => settings.CanonicalBaseUrl.TrimEnd('/') + path;
    public static string SiteDescription(SiteSettings settings) => string.IsNullOrWhiteSpace(settings.Description)
        ? $"Notes and stories from {settings.Title}." : settings.Description;
    public static string Description(ArticleSnapshot post, FitnessUnitSystem units = FitnessUnitSystem.Metric)
    {
        var source = post.BodyMarkdown;
        if (post.Type == PostType.Guide) source += "\n" + string.Join("\n", post.Steps.Select(x => x.BodyMarkdown));
        var text = string.IsNullOrWhiteSpace(post.Summary) ? Markdig.Markdown.ToPlainText(source) : post.Summary;
        text = Whitespace().Replace(text, " ").Trim();
        if (text.Length == 0 && post.Type == PostType.Checkin)
            text = $"Check-in at {post.Location?.Name}";
        if (text.Length == 0 && post.Type == PostType.Fitness)
            text = "Fitness activity" + (post.Activity is { } activity ? $" · {activity.FormatDistance(units)}" : "");
        return text.Length == 0 ? $"{(post.Type == PostType.Gallery ? "A photo gallery" : "A photo note")} · {post.PublishedAtUtc:MMMM d, yyyy}" : text[..Math.Min(280, text.Length)];
    }

    public static string Rss(SiteSettings settings, ArticleSnapshot[] posts, SafeMarkdown markdown)
    {
        XNamespace content = "http://purl.org/rss/1.0/modules/content/";
        XNamespace atom = "http://www.w3.org/2005/Atom";
        return new XDocument(new XElement("rss", new XAttribute("version", "2.0"),
            new XAttribute(XNamespace.Xmlns + "content", content), new XAttribute(XNamespace.Xmlns + "atom", atom),
            new XElement("channel", new XElement("title", settings.Title),
                new XElement("link", Absolute(settings, "/")), new XElement("description", SiteDescription(settings)),
                new XElement(atom + "link", new XAttribute("href", Absolute(settings, "/rss.xml")),
                    new XAttribute("rel", "self"), new XAttribute("type", "application/rss+xml")),
                posts.Select(post =>
                {
                    var url = Absolute(settings, $"/posts/{post.Slug}/");
                    // XML serializes HTML as text; consumers decode it once as RSS content.
                    var html = markdown.RenderContent(post, false, settings.CanonicalBaseUrl, settings.FitnessUnits);
                    return new XElement("item", new XElement("title", post.DisplayTitle), new XElement("link", url),
                        new XElement("guid", new XAttribute("isPermaLink", "true"), url),
                        new XElement("pubDate", post.PublishedAtUtc.ToString("r", CultureInfo.InvariantCulture)),
                        new XElement("description", Description(post, settings.FitnessUnits)), new XElement(content + "encoded", html),
                        post.Tags.Select(tag => new XElement("category", tag)));
                })))).ToString();
    }

    public static string Sitemap(SiteSettings settings, IEnumerable<string> pages)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        return new XDocument(new XElement(ns + "urlset", pages.Select(path =>
            new XElement(ns + "url", new XElement(ns + "loc", Absolute(settings, path)))))).ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
