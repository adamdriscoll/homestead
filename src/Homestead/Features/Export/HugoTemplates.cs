using System.Text;
using System.Text.Encodings.Web;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Homestead.Features.Themes;

namespace Homestead.Features.Export;

internal static class HugoTemplates
{
    public static string SharingPartial(SiteSettings settings)
    {
        var templates = SocialSharing.Templates(settings).ToArray();
        if (templates.Length == 0) return "";
        var html = new StringBuilder("{{ if and (eq .Section \"posts\") (not .Draft) }}" +
            "{{ $title := replaceRE \"[[:cntrl:]]\" \"\" .Title }}" +
            "{{ $url := .Permalink | urlquery }}{{ $subject := $title | urlquery }}" +
            "{{ $text := printf \"%s %s\" $title .Permalink | urlquery }}" +
            "<nav aria-label=\"Share post\" class=\"flex flex-wrap items-center gap-2\" data-social-sharing>");
        foreach (var (label, format) in templates)
        {
            var urlFormat = format.Replace("{0}", "%[1]s", StringComparison.Ordinal)
                .Replace("{1}", "%[2]s", StringComparison.Ordinal).Replace("{2}", "%[3]s", StringComparison.Ordinal);
            // safeURL permits only these server-owned HTTPS/mailto templates after escaping their parameters.
            html.Append($"<a class=\"btn btn-ghost btn-square border-0 shadow-none min-h-11 min-w-11\" href=\"{{{{ printf {System.Text.Json.JsonSerializer.Serialize(urlFormat)} $url $subject $text | safeURL }}}}\" rel=\"noreferrer\" title=\"Share via {label}\" aria-label=\"Share via {label}\">{SocialSharing.RenderIcon(label)}</a>");
        }
        return html.Append("</nav>{{ end }}").ToString();
    }

    public static string ThemedLayout(SiteSettings settings, ThemePackage theme, bool activityMaps = false, ArticleMedia? favicon = null)
    {
        var head = new StringBuilder($"<!doctype html>\n<html lang=\"en\" data-public-theme=\"{HtmlEncoder.Default.Encode(theme.Id)}\" data-accent=\"{HtmlEncoder.Default.Encode(settings.AccentColor.ToLowerInvariant())}\"><head>" +
            "<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<title>{{ .Title }} · {{ .Site.Title }}</title><link rel=\"canonical\" href=\"{{ .Permalink }}\">" +
            "<link rel=\"stylesheet\" href=\"{{ \"assets/site.css\" | relURL }}\">");
        head.Append("<link rel=\"icon\" href=\"{{ \"" + (favicon?.Url.TrimStart('/') ?? "assets/favicon.png") +
            "\" | relURL }}\" type=\"" + (favicon?.MimeType ?? "image/png") + "\">");
        foreach (var file in theme.Manifest.Styles)
            head.Append("<link rel=\"stylesheet\" href=\"{{ \"assets/theme/" + file + "\" | relURL }}\">");
        head.Append("<link rel=\"stylesheet\" href=\"{{ \"assets/theme-colors.css\" | relURL }}\">");
        head.Append("<script type=\"module\" src=\"{{ \"assets/mermaid/diagrams.mjs\" | relURL }}\"></script>");
        if (settings.EnableLocationMap || activityMaps)
        {
            if (!activityMaps) head.Append("{{ if eq .Layout \"location-map\" }}");
            head.Append("<meta name=\"referrer\" content=\"strict-origin-when-cross-origin\">" +
                "<link rel=\"stylesheet\" href=\"{{ \"assets/map/leaflet/leaflet.css\" | relURL }}\">" +
                "<script defer src=\"{{ \"assets/map/leaflet/leaflet.js\" | relURL }}\"></script>");
            if (!activityMaps) head.Append("{{ end }}");
            if (activityMaps) head.Append("<script defer src=\"{{ \"assets/map/fitness-map.js\" | relURL }}\"></script>");
            if (settings.EnableLocationMap) head.Append("{{ if eq .Layout \"location-map\" }}<script defer src=\"{{ \"assets/map/location-map.js\" | relURL }}\"></script>{{ end }}");
        }
        foreach (var file in theme.Manifest.Scripts)
            head.Append("<script defer src=\"{{ \"assets/theme/" + file + "\" | relURL }}\"></script>");
        head.Append("{{ with .OutputFormats.Get \"rss\" }}<link rel=\"alternate\" type=\"application/rss+xml\" href=\"{{ .Permalink }}\">{{ end }}</head><body>" +
            "<a href=\"#main\" class=\"btn absolute -top-20 focus:top-2 left-2\">Skip to content</a>");
        var regions = new Dictionary<string, string>
        {
            ["header"] = "<header class=\"max-w-3xl mx-auto px-4 py-8 border-b border-base-300 space-y-4\"><div><a class=\"text-2xl font-semibold\" href=\"{{ .Site.Home.RelPermalink }}\">{{ .Site.Title }}</a></div><nav aria-label=\"Main navigation\"><a class=\"btn\" href=\"{{ .Site.Home.RelPermalink }}\">Home</a> <a class=\"btn\" href=\"{{ \"about/\" | relURL }}\">About</a>" +
                (settings.EnableLocationMap ? " <a class=\"btn\" href=\"{{ \"map/\" | relURL }}\">Map</a>" : "") +
                (settings.EnableNewsletter ? $" <a class=\"btn\" href=\"{HtmlEncoder.Default.Encode(DiscoveryFiles.Absolute(settings, "/newsletter/"))}\">Newsletter</a>" : "") +
                $" <a class=\"btn\" href=\"{HtmlEncoder.Default.Encode(DiscoveryFiles.Absolute(settings, "/admin"))}\">Admin</a></nav></header>",
            ["main"] = "<main id=\"main\" class=\"max-w-3xl mx-auto px-4 py-8 space-y-6\">{{ block \"main\" . }}{{ end }}</main>",
            ["footer"] = "<footer class=\"max-w-3xl mx-auto p-4 border-t border-base-300 space-y-4\"><a class=\"underline\" href=\"{{ \"index.xml\" | relURL }}\">RSS feed</a>" +
                (settings.HideHomesteadCredit ? "" : "<p class=\"text-sm\">Made at home with <a class=\"link inline-flex items-center gap-1 align-middle\" href=\"https://github.com/PoshTools/homestead\"><img src=\"{{ \"assets/favicon.png\" | relURL }}\" alt=\"\" width=\"20\" height=\"20\" class=\"size-5 shrink-0\">Homestead</a></p>") + "</footer>"
        };
        var body = regions["header"] + regions["main"] + regions["footer"];
        return head + body + "</body></html>";
    }

    public const string LocationMap = """
        {{ define "main" }}
        {{ $posts := slice }}
        {{ range (where (where .Site.RegularPages "Section" "posts") "Draft" false).ByDate.Reverse }}
          {{ $post := . }}
          {{ with .Params.location }}
            {{ if and (ne .latitude nil) (ne .longitude nil) }}
              {{ if and (ge .latitude -90) (le .latitude 90) (ge .longitude -180) (le .longitude 180) }}
                {{ $posts = $posts | append $post }}
              {{ end }}
            {{ end }}
          {{ end }}
        {{ end }}
        <h1 class="text-3xl font-semibold">Location map</h1>
        <p>Explore posts by place. Select a marker to open its posts, or use the list below.</p>
        <div id="location-map" class="h-[28rem] w-full isolate rounded-lg border border-base-300" role="region" aria-label="World map" hidden></div>
        <p data-map-status role="status" hidden>Map tiles could not be loaded. Markers and the post list are still available.</p>
        <noscript><p>Enable JavaScript to explore the interactive map, or choose a post below.</p></noscript>
        {{ if eq (len $posts) 0 }}<p>No published posts with coordinates yet. Explore <a class="link" href="{{ .Site.Home.RelPermalink }}">the home feed</a>.</p>
        {{ else }}
          <section aria-labelledby="mapped-posts-title" class="space-y-3">
            <h2 id="mapped-posts-title" class="text-xl font-semibold">Posts on the map</h2>
            <ul class="space-y-3">{{ range $posts }}
              <li data-map-post data-latitude="{{ .Params.location.latitude }}" data-longitude="{{ .Params.location.longitude }}"><a class="link inline-flex items-center min-h-11 break-words" href="{{ .RelPermalink }}">{{ .Title }}</a>
              <p>{{ with .Params.location.name }}{{ . }} · {{ end }}{{ with .Params.location.address }}{{ . }} · {{ end }}{{ .Params.location.latitude }}, {{ .Params.location.longitude }}</p></li>
            {{ end }}</ul>
          </section>
        {{ end }}
        {{ end }}
        """;

    public static readonly (string Path, string Text)[] Files =
    [
        ("layouts/_markup/render-codeblock-mermaid.html", """
        <figure data-mermaid><details open><summary>Diagram source</summary><pre><code class="language-mermaid">{{ .Inner }}</code></pre></details></figure>
        """),
        ("layouts/_markup/render-image.html", """
        {{ $alt := .PlainText }}{{ $destination := .Destination }}
        {{ range .Page.Params.images }}{{ if eq .file $destination }}{{ $alt = .alt }}{{ end }}{{ end }}
        <img src="{{ .Destination }}" alt="{{ $alt }}" loading="lazy" class="w-full rounded-lg">
        """),
        ("layouts/baseof.html", """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{ .Title }} · {{ .Site.Title }}</title><link rel="canonical" href="{{ .Permalink }}">
        <link rel="stylesheet" href="{{ "assets/site.css" | relURL }}">
        {{ with .OutputFormats.Get "rss" }}<link rel="alternate" type="application/rss+xml" href="{{ .Permalink }}">{{ end }}
        </head><body><header class="max-w-5xl mx-auto p-4 border-b border-base-300"><a class="btn" href="{{ .Site.Home.RelPermalink }}">{{ .Site.Title }}</a> <a class="btn" href="{{ "about/" | relURL }}">About</a></header>
        <main class="max-w-5xl mx-auto p-4">{{ block "main" . }}{{ end }}</main></body></html>
        """),
        ("layouts/page.html", """
        {{ define "main" }}<article class="prose-content"><h1>{{ .Title }}</h1><p>{{ .Date.Format "2006-01-02" }} · {{ .Type }}</p>{{ .Content }}{{ partial "fitness.html" . }}
        <p>{{ range .GetTerms "tags" }}<a href="{{ .RelPermalink }}">{{ .LinkTitle }}</a> {{ end }}</p>{{ partial "social-sharing.html" . }}</article>{{ end }}
        """),
        ("layouts/list.html", """
        {{ define "main" }}<h1 class="text-3xl font-bold">{{ .Title }}</h1>{{ .Content }}
        {{ range .Pages.ByDate.Reverse }}<article class="card card-border my-4"><div class="card-body"><h2 class="card-title"><a href="{{ .RelPermalink }}">{{ .Title }}</a></h2><p>{{ .Summary }}</p>{{ partial "social-sharing.html" . }}</div></article>{{ end }}{{ end }}
        """),
        ("layouts/home.html", """
        {{ define "main" }}<h1 class="text-3xl font-bold">{{ .Site.Title }}</h1>
        {{ range (where .Site.RegularPages "Section" "posts").ByDate.Reverse }}<article class="card card-border my-4"><div class="card-body"><h2 class="card-title"><a href="{{ .RelPermalink }}">{{ .Title }}</a></h2><p>{{ .Type }} · {{ .Date.Format "2006-01-02" }}</p><div class="prose-content">{{ .Content }}{{ partial "fitness.html" . }}</div>{{ partial "social-sharing.html" . }}</div></article>{{ end }}{{ end }}
        """),
        ("layouts/partials/fitness.html", """
        {{ if eq .Type "fitness" }}{{ with .Resources.Get "homestead-activity.html" }}{{ .Content | safeHTML }}{{ end }}{{ end }}
        """)
    ];
}
