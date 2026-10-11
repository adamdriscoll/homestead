using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Homestead.Data.Entities;
using Homestead.Features.Themes;

namespace Homestead.Features.Settings;

public sealed class ProfileLink
{
    [StringLength(80)] public string? Label { get; set; }
    [StringLength(2048)] public string? Url { get; set; }
}

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
    [Display(Name = "Hide Homestead credit")] public bool HideHomesteadCredit { get; set; }
    [Display(Name = "Enable location map")] public bool EnableLocationMap { get; set; }
    [Display(Name = "Bluesky")] public bool ShareBluesky { get; set; }
    [Display(Name = "Facebook")] public bool ShareFacebook { get; set; }
    [Display(Name = "X")] public bool ShareX { get; set; }
    [Display(Name = "LinkedIn")] public bool ShareLinkedIn { get; set; }
    [Display(Name = "Reddit")] public bool ShareReddit { get; set; }
    [Display(Name = "Email")] public bool ShareEmail { get; set; }
    [Display(Name = "Notes")] public bool EnableNotes { get; set; } = true;
    [Display(Name = "Articles")] public bool EnableArticles { get; set; } = true;
    [Display(Name = "Reviews")] public bool EnableReviews { get; set; } = true;
    [Display(Name = "Guides")] public bool EnableGuides { get; set; } = true;
    [Display(Name = "Galleries")] public bool EnableGalleries { get; set; } = true;
    [Display(Name = "Check-ins")] public bool EnableCheckins { get; set; } = true;
    [Display(Name = "Fitness activities")] public bool EnableFitness { get; set; } = true;
    [EnumDataType(typeof(FitnessUnitSystem)), Display(Name = "Fitness units")] public FitnessUnitSystem FitnessUnits { get; set; }
    [Required, Display(Name = "Accent preset")] public string AccentColor { get; set; } = "Porch";
    [Required, StringLength(80), Display(Name = "Public theme")] public string ThemeId { get; set; } = "porch";
    [StringLength(7), Display(Name = "Primary color")] public string? ThemePrimaryColor { get; set; }
    [StringLength(7), Display(Name = "Background color")] public string? ThemeBackgroundColor { get; set; }
    [StringLength(7), Display(Name = "Text color")] public string? ThemeTextColor { get; set; }
    public List<ProfileLink> ProfileLinks { get; set; } = [];

    public static bool IsCanonicalUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/" && !value.Contains('\\');

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IsCanonicalUrl(CanonicalBaseUrl))
            yield return new("Use an absolute HTTP(S) URL at the site root, without credentials, a query, or a fragment.", [nameof(CanonicalBaseUrl)]);
        if (!Accents.Contains(AccentColor))
            yield return new("Choose an available accent preset.", [nameof(AccentColor)]);
        foreach (var (value, member) in new[] { (ThemePrimaryColor, nameof(ThemePrimaryColor)),
            (ThemeBackgroundColor, nameof(ThemeBackgroundColor)), (ThemeTextColor, nameof(ThemeTextColor)) })
            if (!ThemePackage.IsColor(value)) yield return new("Use a six-digit hex color, such as #25613e, or leave blank for the theme default.", [member]);
        if (!IsTimeZone(TimeZone))
            yield return new("Choose a valid time zone identifier, such as America/Chicago or UTC.", [nameof(TimeZone)]);
        if (ProfileLinks.Count > 5)
            yield return new("Use at most five external profile links.", [nameof(ProfileLinks)]);
        foreach (var link in ProfileLinks)
            if ((!string.IsNullOrWhiteSpace(link.Label) || !string.IsNullOrWhiteSpace(link.Url)) &&
                (string.IsNullOrWhiteSpace(link.Label) || !IsProfileUrl(link.Url)))
                yield return new("Each profile needs a label and an absolute HTTP(S) URL without credentials.", [nameof(ProfileLinks)]);
    }

    public static bool IsProfileUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) &&
        !value.Contains('\\') && !value.Any(char.IsControl);

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
        HideHomesteadCredit = s.HideHomesteadCredit,
        EnableLocationMap = s.EnableLocationMap,
        ShareBluesky = s.ShareBluesky,
        ShareFacebook = s.ShareFacebook,
        ShareX = s.ShareX,
        ShareLinkedIn = s.ShareLinkedIn,
        ShareReddit = s.ShareReddit,
        ShareEmail = s.ShareEmail,
        EnableNotes = s.EnableNotes,
        EnableArticles = s.EnableArticles,
        EnableReviews = s.EnableReviews,
        EnableGuides = s.EnableGuides,
        EnableGalleries = s.EnableGalleries,
        EnableCheckins = s.EnableCheckins,
        EnableFitness = s.EnableFitness,
        FitnessUnits = s.FitnessUnits,
        AccentColor = s.AccentColor,
        ThemeId = s.ThemeId,
        ThemePrimaryColor = s.ThemePrimaryColor,
        ThemeBackgroundColor = s.ThemeBackgroundColor,
        ThemeTextColor = s.ThemeTextColor,
        ProfileLinks = s.ProfileLinks.ToList()
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
        s.HideHomesteadCredit = HideHomesteadCredit;
        s.EnableLocationMap = EnableLocationMap;
        s.ShareBluesky = ShareBluesky;
        s.ShareFacebook = ShareFacebook;
        s.ShareX = ShareX;
        s.ShareLinkedIn = ShareLinkedIn;
        s.ShareReddit = ShareReddit;
        s.ShareEmail = ShareEmail;
        s.EnableNotes = EnableNotes;
        s.EnableArticles = EnableArticles;
        s.EnableReviews = EnableReviews;
        s.EnableGuides = EnableGuides;
        s.EnableGalleries = EnableGalleries;
        s.EnableCheckins = EnableCheckins;
        s.EnableFitness = EnableFitness;
        s.FitnessUnits = FitnessUnits;
        s.AccentColor = AccentColor;
        s.ThemeId = ThemeId;
        s.ThemePrimaryColor = ThemePrimaryColor ?? "";
        s.ThemeBackgroundColor = ThemeBackgroundColor ?? "";
        s.ThemeTextColor = ThemeTextColor ?? "";
        s.ProfileLinksJson = JsonSerializer.Serialize(ProfileLinks.Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .Select(x => new ProfileLink { Label = x.Label?.Trim(), Url = x.Url?.Trim() }).ToArray());
    }
}
