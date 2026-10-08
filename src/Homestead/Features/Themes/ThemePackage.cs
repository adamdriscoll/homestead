using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Homestead.Features.Themes;

public sealed record ThemeVariant(int EngineVersion, string[] PostTypes, string[] Styles, string[] Scripts,
    bool SupportsColors = false, string? Layout = null);
public sealed record ThemeManifest(int FormatVersion, string Name, string Version, ThemeVariant[] Variants);
public sealed record ThemeSnapshot(string Id, string Name, string Version, int EngineVersion, bool SupportsColors = false);
public sealed record ThemePackage(string Id, ThemeManifest Manifest, ThemeVariant Variant, Dictionary<string, byte[]> Files)
{
    public const int EngineVersion = 1;
    public const string DefaultLayout = "{{header}}{{main}}{{footer}}";
    public ThemeSnapshot Snapshot => new(Id, Manifest.Name, Manifest.Version, EngineVersion, Variant.SupportsColors);
    public string Layout => Variant.Layout is { } file ? System.Text.Encoding.UTF8.GetString(Files[file]) : DefaultLayout;
    public string AssetUrl(string file, bool preview) => preview ? $"/admin/themes/{Id}/{file}" : $"/assets/theme/{file}";

    public static bool IsColor(string? color) => string.IsNullOrEmpty(color) || Regex.IsMatch(color, "\\A#[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant);
    public static bool IsAssetPath(string path) => path.Length <= 180 && path.Split('/').All(segment =>
        Regex.IsMatch(segment, "\\A[a-zA-Z0-9][a-zA-Z0-9._-]*\\z", RegexOptions.CultureInvariant) && !segment.EndsWith('.') &&
        !Regex.IsMatch(segment.Split('.')[0], "^(con|prn|aux|nul|com[1-9]|lpt[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    // Layouts arrange server-rendered regions; they cannot introduce scripts, forms, URLs, or executable Razor.
    public static void ValidateLayout(string layout)
    {
        foreach (var slot in new[] { "{{header}}", "{{main}}", "{{footer}}" })
            if (layout.Split(slot, StringSplitOptions.None).Length != 2) throw new InvalidDataException("Layouts must include each header, main, and footer slot exactly once.");
        var plain = layout.Replace("{{header}}", "").Replace("{{main}}", "").Replace("{{footer}}", "");
        if (plain.Contains("{{") || plain.Contains("}}")) throw new InvalidDataException("Unknown layout slot.");
        try
        {
            using var reader = XmlReader.Create(new StringReader("<layout>" + layout + "</layout>"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var root = XDocument.Load(reader).Root!;
            foreach (var node in root.DescendantNodes())
            {
                if (node is XCData) throw new InvalidDataException("Layouts cannot use CDATA.");
                if (node is XText text && !string.IsNullOrWhiteSpace(text.Value.Replace("{{header}}", "").Replace("{{main}}", "").Replace("{{footer}}", "")))
                    throw new InvalidDataException("Layout text must be a region slot.");
                if (node is XElement element)
                {
                    if (element.Name.NamespaceName != "" || element.Name.LocalName is not ("div" or "section" or "aside"))
                        throw new InvalidDataException("Layout wrappers may only use div, section, or aside.");
                    if (element.Attributes().Any(a => a.Name.NamespaceName != "" || a.Name.LocalName is not ("class" or "aria-label") || a.Value.Contains('{')))
                        throw new InvalidDataException("Layout wrappers may only use class and aria-label attributes.");
                }
                else if (node is not XText) throw new InvalidDataException("Unsupported layout node.");
            }
        }
        catch (XmlException ex) { throw new InvalidDataException("Layout must use balanced HTML wrappers.", ex); }
    }
}
