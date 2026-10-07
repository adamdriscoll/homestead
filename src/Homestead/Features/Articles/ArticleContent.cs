using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
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

    public static ArticleInput From(Post post) => new()
    {
        Title = post.Draft!.Title,
        BodyMarkdown = post.Draft.BodyMarkdown,
        Summary = post.Draft.Summary,
        Version = post.Draft.Version,
        CoverMediaId = post.Draft.CoverMediaId,
        Tags = string.Join(", ", post.Tags.Select(x => x.Tag.Name)),
        AltText = post.Media.ToDictionary(x => x.MediaAssetId, x => x.AltText ?? "")
    };
}

public sealed record ArticleMedia(Guid Id, string Filename, string MimeType, string AltText)
{
    public string Url => $"/media/{Id:D}/{Filename}";
    public string PrivateUrl => $"/admin/media/{Id:D}";
}

public sealed record ArticleSnapshot(int SchemaVersion, Guid PostId, string Slug, string Title,
    string BodyMarkdown, string? Summary, DateTime PublishedAtUtc, DateTime ModifiedAtUtc,
    long DraftVersion, string[] Tags, Guid? CoverMediaId, ArticleMedia[] Media);

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
