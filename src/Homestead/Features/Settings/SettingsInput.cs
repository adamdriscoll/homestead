using System.ComponentModel.DataAnnotations;
using Homestead.Data.Entities;

namespace Homestead.Features.Settings;

public sealed class SettingsInput : IValidatableObject
{
    public static IReadOnlyList<string> Accents { get; } = ["Porch", "Forest", "Ocean", "Plum"];
    [Required, StringLength(100), Display(Name = "Site title")] public string Title { get; set; } = "";
    [Required, StringLength(2048), Display(Name = "Canonical URL")] public string CanonicalBaseUrl { get; set; } = "";
    [Required, StringLength(100), Display(Name = "Author display name")] public string DisplayAuthor { get; set; } = "";
    [StringLength(300)] public string? Description { get; set; }
    [Required, StringLength(100), Display(Name = "Time zone")] public string TimeZone { get; set; } = "UTC";
    [StringLength(50000), Display(Name = "About (Markdown)")] public string? AboutMarkdown { get; set; }
    [StringLength(500), Display(Name = "Author bio")] public string? AuthorBio { get; set; }
    [StringLength(300), Display(Name = "Footer text")] public string? FooterText { get; set; }
    [Required, Display(Name = "Accent preset")] public string AccentColor { get; set; } = "Porch";

    public static bool IsCanonicalUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/" && !value.Contains('\\');

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IsCanonicalUrl(CanonicalBaseUrl))
            yield return new("Use an absolute HTTP(S) URL at the site root, without credentials, a query, or a fragment.", [nameof(CanonicalBaseUrl)]);
        if (!Accents.Contains(AccentColor))
            yield return new("Choose an available accent preset.", [nameof(AccentColor)]);
        if (!IsTimeZone(TimeZone))
            yield return new("Choose a valid time zone identifier, such as America/Chicago or UTC.", [nameof(TimeZone)]);
    }

    private static bool IsTimeZone(string? value)
    {
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(value ?? ""); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    public static SettingsInput From(SiteSettings s) => new()
    {
        Title = s.Title,
        CanonicalBaseUrl = s.CanonicalBaseUrl,
        DisplayAuthor = s.DisplayAuthor,
        Description = s.Description,
        TimeZone = s.TimeZone,
        AboutMarkdown = s.AboutMarkdown,
        AuthorBio = s.AuthorBio,
        FooterText = s.FooterText,
        AccentColor = s.AccentColor
    };

    public void ApplyTo(SiteSettings s)
    {
        s.Title = Title.Trim();
        s.CanonicalBaseUrl = new Uri(CanonicalBaseUrl).GetLeftPart(UriPartial.Authority);
        s.DisplayAuthor = DisplayAuthor.Trim();
        s.Description = Description ?? "";
        s.TimeZone = TimeZone;
        s.AboutMarkdown = AboutMarkdown ?? "";
        s.AuthorBio = AuthorBio ?? "";
        s.FooterText = FooterText ?? "";
        s.AccentColor = AccentColor;
    }
}
