using System.Text.RegularExpressions;

namespace Homestead.Features.Themes;

public sealed record ThemeManifest(int FormatVersion, string Name, string Version, string[] PostTypes, string[] Styles,
    string[] Scripts, bool SupportsColors = false, Dictionary<string, string>? Templates = null);
public sealed record ThemeSnapshot(string Id, string Name, string Version, bool SupportsColors = false);
public sealed record ThemePackage(string Id, ThemeManifest Manifest, Dictionary<string, byte[]> Files)
{
    public ThemeSnapshot Snapshot => new(Id, Manifest.Name, Manifest.Version, Manifest.SupportsColors);
    public static bool IsSource(string file) => file == "theme.json" || Path.GetExtension(file) == ".razor";
    public string AssetUrl(string file, bool preview) => preview ? $"/admin/themes/{Id}/{file}" : $"/assets/theme/{file}";

    public static bool IsColor(string? color) => string.IsNullOrEmpty(color) || Regex.IsMatch(color, "\\A#[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant);
    public static bool IsAssetPath(string path) => path.Length <= 180 && path.Split('/').All(segment =>
        Regex.IsMatch(segment, "\\A[a-zA-Z0-9][a-zA-Z0-9._-]*\\z", RegexOptions.CultureInvariant) && !segment.EndsWith('.') &&
        !Regex.IsMatch(segment.Split('.')[0], "^(con|prn|aux|nul|com[1-9]|lpt[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
}
