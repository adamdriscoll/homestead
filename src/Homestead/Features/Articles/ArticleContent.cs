using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Homestead.Data.Entities;
using Humanizer;

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
    public List<GuideSupplyInput> Tools { get; set; } = [];
    public List<GuideSupplyInput> Materials { get; set; } = [];
    public List<Guid> MediaOrder { get; set; } = [];
    public List<Guid> RemovedMediaIds { get; set; } = [];
    public Dictionary<Guid, string> Captions { get; set; } = [];
    public Dictionary<Guid, Guid?> StepImages { get; set; } = [];
    [StringLength(300)] public string? LocationName { get; set; }
    [StringLength(1000)] public string? LocationAddress { get; set; }
    [Range(-90d, 90d)] public double? Latitude { get; set; }
    [Range(-180d, 180d)] public double? Longitude { get; set; }

    public static ArticleInput From(Post post) => new()
    {
        Title = post.Draft!.Title,
        BodyMarkdown = post.Draft.BodyMarkdown,
        Summary = post.Draft.Summary,
        Version = post.Draft.Version,
        CoverMediaId = post.Draft.CoverMediaId,
        LocationName = post.Draft.LocationName,
        LocationAddress = post.Draft.LocationAddress,
        Latitude = post.Draft.Latitude,
        Longitude = post.Draft.Longitude,
        Tags = string.Join(", ", post.Tags.Select(x => x.Tag.Name)),
        AltText = post.Media.ToDictionary(x => x.MediaAssetId, x => x.AltText ?? ""),
        Subject = post.Review?.Subject,
        Rating = post.Review?.Rating,
        ExternalUrl = post.Review?.ExternalUrl,
        Steps = post.Steps.OrderBy(x => x.Order).Select(GuideStepInput.From).ToList(),
        Tools = GuideSupplies(post, GuideSupplyKind.Tool).Select(x => new GuideSupplyInput { Name = x.Name, Url = x.Url }).ToList(),
        Materials = GuideSupplies(post, GuideSupplyKind.Material).Select(x => new GuideSupplyInput { Name = x.Name, Url = x.Url, Quantity = x.Quantity }).ToList(),
        MediaOrder = post.Media.OrderBy(x => x.SortOrder).Select(x => x.MediaAssetId).ToList(),
        Captions = post.Media.ToDictionary(x => x.MediaAssetId, x => x.Caption ?? ""),
        StepImages = post.Media.ToDictionary(x => x.MediaAssetId, x => x.GuideStepId)
    };

    public static IEnumerable<GuideSupply> GuideSupplies(Post post, GuideSupplyKind kind) => post.Supplies.Where(x => x.Kind == kind).OrderBy(x => x.Order);
}

public sealed class GuideSupplyInput
{
    [StringLength(300)] public string? Name { get; set; }
    [StringLength(2000)] public string? Url { get; set; }
    [StringLength(300)] public string? Quantity { get; set; }
}

public sealed class GuideStepInput
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [StringLength(300)] public string? Title { get; set; }
    [StringLength(200_000), DisplayFormat(ConvertEmptyStringToNull = false)] public string BodyMarkdown { get; set; } = "";
    public TimeSpan? Duration { get; set; }
    [Range(typeof(TimeSpan), "00:00:00", "23:59:59")] public TimeSpan? ReplacementDuration { get; set; }
    public TimeSpan? EffectiveDuration => ReplacementDuration is { } replacement ?
        replacement == TimeSpan.Zero ? null : replacement : Duration;
    public long? DurationSeconds => EffectiveDuration is { } duration ? (long)duration.TotalSeconds : null;

    public static GuideStepInput From(GuideStep step)
    {
        var duration = step.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        return new()
        {
            Id = step.Id,
            Title = step.Title,
            BodyMarkdown = step.BodyMarkdown,
            Duration = duration
        };
    }
}

public sealed record ReviewSnapshot(string Subject, int? Rating, string? ExternalUrl);
public sealed record GuideSupplySnapshot(string Name, string? Url)
{
    public string? Quantity { get; init; }
}
public sealed record GuideStepSnapshot(Guid Id, string BodyMarkdown)
{
    public string? Title { get; init; }
    public int? DurationSeconds { get; init; }
    [JsonIgnore]
    public string? DurationText => DurationSeconds is > 0 ? TimeSpan.FromSeconds(DurationSeconds.Value)
        .Humanize(precision: 3, maxUnit: TimeUnit.Hour, minUnit: TimeUnit.Second, culture: CultureInfo.GetCultureInfo("en-US")) : null;
    public static GuideStepSnapshot From(GuideStep step) => new(step.Id, step.BodyMarkdown) { Title = step.Title, DurationSeconds = step.DurationSeconds };
}
public sealed record LocationSnapshot(string? Name, string? Address, double? Latitude, double? Longitude)
{
    public static LocationSnapshot? From(PostDraft draft) =>
        draft.LocationName is null && draft.LocationAddress is null && draft.Latitude is null && draft.Longitude is null
            ? null : new(draft.LocationName, draft.LocationAddress, draft.Latitude, draft.Longitude);
    [JsonIgnore]
    public string? Coordinates => Latitude is { } latitude && Longitude is { } longitude
        ? $"{latitude.ToString("G", CultureInfo.InvariantCulture)}, {longitude.ToString("G", CultureInfo.InvariantCulture)}" : null;
}

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
    public GuideSupplySnapshot[] Tools { get; init; } = [];
    public GuideSupplySnapshot[] Materials { get; init; } = [];
    public LocationSnapshot? Location { get; init; }
    public FitnessSnapshot? Activity { get; init; }
    [JsonIgnore]
    public string TypeLabel => Type switch { PostType.Microblog => "Note", PostType.Checkin => "Check-in", PostType.Fitness => "Fitness activity", _ => Type.ToString() };
    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? Type switch
        {
            PostType.Microblog => $"Note · {PublishedAtUtc:yyyy-MM-dd}",
            PostType.Review => string.IsNullOrWhiteSpace(Review?.Subject) ? "Untitled review" : Review.Subject,
            PostType.Gallery => $"Gallery · {PublishedAtUtc:yyyy-MM-dd}",
            PostType.Fitness => $"Fitness activity · {PublishedAtUtc:yyyy-MM-dd}",
            PostType.Checkin => string.IsNullOrWhiteSpace(Location?.Name) ? $"Check-in · {PublishedAtUtc:yyyy-MM-dd}" : $"Check-in at {Location.Name}",
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
