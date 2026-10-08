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
