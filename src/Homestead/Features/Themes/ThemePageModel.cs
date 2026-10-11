using System.Collections.Immutable;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Components;

namespace Homestead.Features.Themes;

public sealed record ThemeSiteModel(string Title, string Description, string CanonicalBaseUrl, string DisplayAuthor,
    string AuthorBio, string FooterText, bool HideHomesteadCredit, string TimeZone, string AccentColor,
    bool EnableLocationMap, ImmutableArray<PostType> EnabledPostTypes, ImmutableArray<ThemeLinkModel> ProfileLinks,
    MarkupString AboutHtml, ThemeMediaModel? Avatar, ThemeMediaModel? Logo, ThemeMediaModel? HeaderImage)
{
    public bool EnableNewsletter { get; init; }
}
public sealed record ThemeLinkModel(string Label, string Url);
public sealed record ThemeMediaModel(Guid Id, string Url, string MimeType, string AltText, string? Caption, Guid? GuideStepId);
public sealed record ThemeTagModel(string Name, string Url);
public sealed record ThemeStepModel(Guid Id, MarkupString BodyHtml, ImmutableArray<ThemeMediaModel> Media)
{
    public string? Title { get; init; }
    public int? DurationSeconds { get; init; }
    public string? DurationText { get; init; }
}
public sealed record ThemePostModel(Guid Id, PostType Type, string TypeLabel, string Title, string DisplayTitle,
    string Url, string? Summary, string Description, DateTime PublishedAtUtc, DateTime ModifiedAtUtc, string DisplayDate,
    MarkupString BodyHtml, MarkupString ContentHtml, ImmutableArray<ThemeTagModel> Tags,
    ImmutableArray<ThemeMediaModel> Media, ThemeMediaModel? Cover, ReviewSnapshot? Review,
    ImmutableArray<ThemeStepModel> Steps, LocationSnapshot? Location)
{
    public ImmutableArray<GuideSupplySnapshot> Tools { get; init; } = [];
    public ImmutableArray<GuideSupplySnapshot> Materials { get; init; } = [];
    public ImmutableArray<ThemeLinkModel> ShareLinks { get; init; } = [];
}

public sealed record ThemePageModel(ThemeSiteModel Site, string Path, string Heading, bool Preview, bool IsListing,
    string ThemeId, string CanonicalUrl, ThemePostModel? Post, ImmutableArray<ThemePostModel> Posts,
    MarkupString Head = default, MarkupString Header = default, MarkupString Content = default,
    MarkupString Footer = default, MarkupString DefaultContent = default)
{
    public ImmutableArray<ThemeLinkModel> Navigation { get; init; } = [];
    public static ThemePageModel From(PublicPage page, SafeMarkdown markdown)
    {
        ThemeMediaModel Image(ArticleMedia media) => new(media.Id, page.Preview ? media.PrivateUrl : media.Url,
            media.MimeType, media.AltText, media.Caption, media.GuideStepId);
        ThemeMediaModel? Brand(Guid? id) => page.Branding?.SingleOrDefault(x => x.Id == id) is { } image ? Image(image) : null;
        ThemePostModel Post(ArticleSnapshot post) => new(post.PostId, post.Type, post.TypeLabel, post.Title, post.DisplayTitle,
            $"/posts/{post.Slug}/", post.Summary, DiscoveryFiles.Description(post), post.PublishedAtUtc, post.ModifiedAtUtc,
            TimeZoneInfo.ConvertTimeFromUtc(post.PublishedAtUtc, TimeZoneInfo.FindSystemTimeZoneById(page.Settings.TimeZone)).ToString("MMMM d, yyyy"),
            new(markdown.Render(post, page.Preview)), new(markdown.RenderContent(post, page.Preview)),
            post.Tags.Select(tag => new ThemeTagModel(tag, $"/tags/{post.TagSlug(tag)}/")).ToImmutableArray(),
            post.Media.Select(Image).ToImmutableArray(), post.Media.SingleOrDefault(x => x.Id == post.CoverMediaId) is { } cover ? Image(cover) : null,
            post.Review, post.Steps.Select(step => new ThemeStepModel(step.Id,
                new(markdown.Render(post with { BodyMarkdown = step.BodyMarkdown }, page.Preview)),
                post.Media.Where(x => x.GuideStepId == step.Id).Select(Image).ToImmutableArray())
            { Title = step.Title, DurationSeconds = step.DurationSeconds, DurationText = step.DurationText }).ToImmutableArray(), post.Location)
        {
            Tools = post.Tools.ToImmutableArray(),
            Materials = post.Materials.ToImmutableArray(),
            ShareLinks = SocialSharing.Links(page.Settings, post.Slug, post.DisplayTitle, page.Preview).ToImmutableArray()
        };
        var settings = page.Settings;
        var about = new ArticleSnapshot(1, Guid.Empty, "about", "About", settings.AboutMarkdown, null,
            DateTime.UtcNow, DateTime.UtcNow, 0, [], null, page.Branding ?? []);
        var navigation = new List<ThemeLinkModel> { new("Home", "/") };
        foreach (var (type, label, path) in new[] { (PostType.Microblog, "Notes", "/notes/"), (PostType.Article, "Articles", "/articles/"),
            (PostType.Review, "Reviews", "/reviews/"), (PostType.Guide, "Guides", "/guides/"),
            (PostType.Gallery, "Galleries", "/galleries/"), (PostType.Checkin, "Check-ins", "/checkins/") })
            if (settings.IsPostTypeEnabled(type)) navigation.Add(new(label, path));
        navigation.Add(new("About", "/about/"));
        if (settings.EnableLocationMap) navigation.Add(new("Map", "/map/"));
        if (settings.EnableNewsletter) navigation.Add(new("Newsletter", DiscoveryFiles.Absolute(settings, "/newsletter/")));
        navigation.Add(new("Admin", DiscoveryFiles.Absolute(settings, "/admin")));
        return new(new(settings.Title, settings.Description, settings.CanonicalBaseUrl, settings.DisplayAuthor,
            settings.AuthorBio, settings.FooterText, settings.HideHomesteadCredit, settings.TimeZone, settings.AccentColor,
            settings.EnableLocationMap, settings.EnabledPostTypes.ToImmutableArray(), settings.ProfileLinks.Select(x => new ThemeLinkModel(x.Label ?? "", x.Url ?? "")).ToImmutableArray(),
            new(markdown.Render(about, page.Preview)), Brand(settings.AvatarMediaId), Brand(settings.LogoMediaId), Brand(settings.HeaderMediaId))
        { EnableNewsletter = settings.EnableNewsletter },
            page.Path, page.Heading, page.Preview, page.Article is null && page.Path is not ("/about/" or "/map/" or "/newsletter/"), page.Theme!.Id,
            DiscoveryFiles.Absolute(settings, page.Article is { } article ? $"/posts/{article.Slug}/" : page.Path),
            page.Article is { } current ? Post(current) : null, page.Articles.Select(Post).ToImmutableArray())
        { Navigation = navigation.ToImmutableArray() };
    }
}
