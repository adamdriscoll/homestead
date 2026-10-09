using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Homestead.Features.Settings;

namespace Homestead.Data.Entities;

public sealed class SiteSettings
{
    public int Id { get; set; } = 1;
    public string Title { get; set; } = "Homestead";
    public string CanonicalBaseUrl { get; set; } = "";
    public string DisplayAuthor { get; set; } = "";
    public string Description { get; set; } = "";
    public string TimeZone { get; set; } = "UTC";
    public string AboutMarkdown { get; set; } = "";
    public string AuthorBio { get; set; } = "";
    public string FooterText { get; set; } = "";
    public bool HideHomesteadCredit { get; set; }
    public bool EnableLocationMap { get; set; }
    public bool ShareBluesky { get; set; }
    public bool ShareFacebook { get; set; }
    public bool ShareX { get; set; }
    public bool ShareLinkedIn { get; set; }
    public bool ShareReddit { get; set; }
    public bool ShareEmail { get; set; }
    public bool EnableNotes { get; set; } = true;
    public bool EnableArticles { get; set; } = true;
    public bool EnableReviews { get; set; } = true;
    public bool EnableGuides { get; set; } = true;
    public bool EnableGalleries { get; set; } = true;
    public bool EnableCheckins { get; set; } = true;
    [NotMapped] public PostType[] EnabledPostTypes => Enum.GetValues<PostType>().Where(IsPostTypeEnabled).ToArray();

    public bool IsPostTypeEnabled(PostType type) => type switch
    {
        PostType.Microblog => EnableNotes,
        PostType.Article => EnableArticles,
        PostType.Review => EnableReviews,
        PostType.Guide => EnableGuides,
        PostType.Gallery => EnableGalleries,
        PostType.Checkin => EnableCheckins,
        _ => false
    };
    public string AccentColor { get; set; } = "Porch";
    public string ThemeId { get; set; } = "porch";
    public string ThemePrimaryColor { get; set; } = "";
    public string ThemeBackgroundColor { get; set; } = "";
    public string ThemeTextColor { get; set; } = "";
    public Guid? AvatarMediaId { get; set; }
    public Guid? LogoMediaId { get; set; }
    public Guid? HeaderMediaId { get; set; }
    public string ProfileLinksJson { get; set; } = "[]";
    [NotMapped] public ProfileLink[] ProfileLinks => JsonSerializer.Deserialize<ProfileLink[]>(ProfileLinksJson) ?? [];
}
