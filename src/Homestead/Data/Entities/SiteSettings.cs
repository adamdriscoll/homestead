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
    public Guid? AvatarMediaId { get; set; }
    public Guid? LogoMediaId { get; set; }
    public Guid? HeaderMediaId { get; set; }
}
