using System.Globalization;
using Homestead.Data.Entities;
using Homestead.Features.Themes;

namespace Homestead.Features.Publishing;

public static class SocialSharing
{
    // The placeholders are the escaped canonical URL, title, and combined title/URL text.
    public static IEnumerable<(string Label, string UrlFormat)> Templates(SiteSettings settings)
    {
        if (settings.ShareBluesky) yield return ("Bluesky", "https://bsky.app/intent/compose?text={2}");
        if (settings.ShareFacebook) yield return ("Facebook", "https://www.facebook.com/sharer/sharer.php?u={0}");
        if (settings.ShareX) yield return ("X", "https://twitter.com/intent/tweet?url={0}&text={1}");
        if (settings.ShareLinkedIn) yield return ("LinkedIn", "https://www.linkedin.com/sharing/share-offsite/?url={0}");
        if (settings.ShareReddit) yield return ("Reddit", "https://www.reddit.com/submit?url={0}&title={1}");
        if (settings.ShareEmail) yield return ("Email", "mailto:?subject={1}&body={0}");
    }

    public static ThemeLinkModel[] Links(SiteSettings settings, string slug, string title, bool preview = false)
    {
        if (preview) return [];
        var url = DiscoveryFiles.Absolute(settings, $"/posts/{slug}/");
        // Mail subjects must not contain header breaks, even when percent-encoded.
        title = string.Concat(title.Where(character => !char.IsControl(character)));
        return Templates(settings).Select(template => new ThemeLinkModel(template.Label,
            string.Format(CultureInfo.InvariantCulture, template.UrlFormat,
                Uri.EscapeDataString(url), Uri.EscapeDataString(title), Uri.EscapeDataString($"{title} {url}")))).ToArray();
    }
}
