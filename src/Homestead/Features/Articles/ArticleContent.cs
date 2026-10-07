using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Homestead.Data.Entities;

namespace Homestead.Features.Articles;

public sealed class ArticleInput
{
    [StringLength(300)] public string? Title { get; set; }
    [StringLength(200_000), DisplayFormat(ConvertEmptyStringToNull = false)] public string BodyMarkdown { get; set; } = "";
    [StringLength(1000)] public string? Summary { get; set; }
    [StringLength(1000)] public string? Tags { get; set; }
    public Guid? CoverMediaId { get; set; }
    public long Version { get; set; }
    public Dictionary<Guid, string> AltText { get; set; } = [];
    [StringLength(300)] public string? Subject { get; set; }
    [Range(1, 5)] public int? Rating { get; set; }
    [StringLength(2000)] public string? ExternalUrl { get; set; }
    public List<GuideStepInput> Steps { get; set; } = [];
    public List<Guid> MediaOrder { get; set; } = [];
    public List<Guid> RemovedMediaIds { get; set; } = [];
    public Dictionary<Guid, string> Captions { get; set; } = [];
    public Dictionary<Guid, Guid?> StepImages { get; set; } = [];

    public static ArticleInput From(Post post) => new()
    {
        Title = post.Draft!.Title,
        BodyMarkdown = post.Draft.BodyMarkdown,
        Summary = post.Draft.Summary,
        Version = post.Draft.Version,
        CoverMediaId = post.Draft.CoverMediaId,
        Tags = string.Join(", ", post.Tags.Select(x => x.Tag.Name)),
        AltText = post.Media.ToDictionary(x => x.MediaAssetId, x => x.AltText ?? ""),
        Subject = post.Review?.Subject,
        Rating = post.Review?.Rating,
        ExternalUrl = post.Review?.ExternalUrl,
        Steps = post.Steps.OrderBy(x => x.Order).Select(x => new GuideStepInput { Id = x.Id, BodyMarkdown = x.BodyMarkdown }).ToList(),
        MediaOrder = post.Media.OrderBy(x => x.SortOrder).Select(x => x.MediaAssetId).ToList(),
        Captions = post.Media.ToDictionary(x => x.MediaAssetId, x => x.Caption ?? ""),
        StepImages = post.Media.ToDictionary(x => x.MediaAssetId, x => x.GuideStepId)
    };
}

public sealed class GuideStepInput
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [StringLength(200_000), DisplayFormat(ConvertEmptyStringToNull = false)] public string BodyMarkdown { get; set; } = "";
}

public sealed record ReviewSnapshot(string Subject, int? Rating, string? ExternalUrl);
public sealed record GuideStepSnapshot(Guid Id, string BodyMarkdown);

public sealed record ArticleMedia(Guid Id, string Filename, string MimeType, string AltText)
{
    public string? Caption { get; init; }
    public Guid? GuideStepId { get; init; }
    public string Url => $"/media/{Id:D}/{Filename}";
    public string PrivateUrl => $"/admin/media/{Id:D}";
}

public sealed record ArticleSnapshot(int SchemaVersion, Guid PostId, string Slug, string Title,
    string BodyMarkdown, string? Summary, DateTime PublishedAtUtc, DateTime ModifiedAtUtc,
    long DraftVersion, string[] Tags, Guid? CoverMediaId, ArticleMedia[] Media)
{
    // Missing fields in phase 2 snapshots retain their article semantics.
    public PostType Type { get; init; } = PostType.Article;
    public Dictionary<string, string> TagSlugs { get; init; } = [];
    public ReviewSnapshot? Review { get; init; }
    public GuideStepSnapshot[] Steps { get; init; } = [];
    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? Type switch
        {
            PostType.Microblog => $"Note · {PublishedAtUtc:yyyy-MM-dd}",
            PostType.Review => string.IsNullOrWhiteSpace(Review?.Subject) ? "Untitled review" : Review.Subject,
            PostType.Gallery => $"Gallery · {PublishedAtUtc:yyyy-MM-dd}",
            _ => $"Untitled {Type.ToString().ToLowerInvariant()}"
        } : Title;
    public string TagSlug(string name) => TagSlugs.GetValueOrDefault(name) ?? Slugs.Create(name);
}

public static partial class Slugs
{
    public static string Create(string title)
    {
        var text = new StringBuilder();
        foreach (var c in title.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                text.Append(c is >= 'A' and <= 'Z' ? char.ToLowerInvariant(c) : c);
        var slug = Separators().Replace(text.ToString(), "-").Trim('-');
        return slug.Length == 0 ? "article" : slug[..Math.Min(150, slug.Length)].TrimEnd('-');
    }
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Separators();
}
