using System.Globalization;
using Homestead.Data.Entities;
using Homestead.Features.Themes;

namespace Homestead.Features.Publishing;

public static class SocialSharing
{
    // Only server-owned SVG markup is returned; labels never become raw HTML.
    public static string RenderIcon(string label)
    {
        var content = label switch
        {
            "Bluesky" => """
                <path d="M12 10.8C10.9 8.7 7.9 4.7 5.1 2.8 2.4 1 1.4 1.3.7 1.6 0 1.9 0 3 0 3.6s.3 5 .5 5.8c.7 2.5 3.1 3.3 5.4 3-4 .6-7.5 2-2.8 7.2 5.2 5.4 7.1-1.2 8.1-4.5.2-.6.3-.8.8-.8s.6.2.8.8c1 3.3 2.9 9.9 8.1 4.5 4.7-5.2 1.2-6.6-2.8-7.2 2.3.3 4.7-.5 5.4-3 .2-.8.5-5.2.5-5.8s0-1.7-.7-2c-.7-.3-1.7-.6-4.4 1.2-2.8 1.9-5.8 5.9-6.9 8z"/>
                """,
            "Facebook" => """
                <path d="M14 22v-9h3l.5-4H14V6.5c0-1.1.3-1.5 1.5-1.5H18V1h-3c-3.6 0-5 2.2-5 5v3H7v4h3v9z"/>
                """,
            "X" => """
                <path d="M18.9 2H22l-6.8 7.8L23.2 22h-6.3L12 14.6 5.5 22H2.3l8.2-9.4L.8 2h6.5l5.2 7.9L18.9 2zM17.8 20h1.7L6.3 4H4.5z"/>
                """,
            "LinkedIn" => """
                <path d="M3 8h4v13H3zM9 8h4v1.8C13.8 8.5 15 8 16.5 8 20 8 21 10.2 21 13.5V21h-4v-7c0-1.8-.3-3-2-3s-2 1.4-2 3v7H9z"/><circle cx="5" cy="4.5" r="2.2"/>
                """,
            "Reddit" => """
                <g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="15" rx="9" ry="6"/><path d="m12 9 1.5-6 4.5 1M4 12a2.5 2.5 0 1 1-2 4M20 12a2.5 2.5 0 1 0 2 4M8 17c2 2 6 2 8 0"/><circle cx="20" cy="4" r="2"/></g><circle cx="8" cy="14" r="1.3"/><circle cx="16" cy="14" r="1.3"/>
                """,
            "Email" => """
                <g fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="16" rx="2"/><path d="m3 6 9 7 9-7"/></g>
                """,
            _ => """
                <g fill="none" stroke="currentColor" stroke-width="1.8"><circle cx="18" cy="4" r="3"/><circle cx="6" cy="12" r="3"/><circle cx="18" cy="20" r="3"/><path d="m8.5 10.3 7-4.6m-7 8 7 4.6"/></g>
                """
        };
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" fill=\"currentColor\" aria-hidden=\"true\" focusable=\"false\">{content}</svg>";
    }

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
