using System.Text;
using System.Text.Encodings.Web;
using Homestead.Data.Entities;
using Homestead.Features.Themes;

namespace Homestead.Features.Export;

internal static class HugoTemplates
{
    public static string ThemedLayout(SiteSettings settings, ThemePackage theme)
    {
        var head = new StringBuilder($"<!doctype html>\n<html lang=\"en\" data-public-theme=\"{HtmlEncoder.Default.Encode(theme.Id)}\" data-accent=\"{HtmlEncoder.Default.Encode(settings.AccentColor.ToLowerInvariant())}\"><head>" +
            "<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<title>{{ .Title }} · {{ .Site.Title }}</title><link rel=\"canonical\" href=\"{{ .Permalink }}\">" +
            "<link rel=\"stylesheet\" href=\"{{ \"assets/site.css\" | relURL }}\">");
        foreach (var file in theme.Variant.Styles)
            head.Append("<link rel=\"stylesheet\" href=\"{{ \"assets/theme/" + file + "\" | relURL }}\">");
        head.Append("<link rel=\"stylesheet\" href=\"{{ \"assets/theme-colors.css\" | relURL }}\">");
        foreach (var file in theme.Variant.Scripts)
            head.Append("<script defer src=\"{{ \"assets/theme/" + file + "\" | relURL }}\"></script>");
        head.Append("{{ with .OutputFormats.Get \"rss\" }}<link rel=\"alternate\" type=\"application/rss+xml\" href=\"{{ .Permalink }}\">{{ end }}</head><body>" +
            "<a href=\"#main\" class=\"btn absolute -top-20 focus:top-2 left-2\">Skip to content</a>");
        var regions = new Dictionary<string, string>
        {
            ["header"] = "<header class=\"max-w-3xl mx-auto px-4 py-8 border-b border-base-300 space-y-4\"><div><a class=\"text-2xl font-semibold\" href=\"{{ .Site.Home.RelPermalink }}\">{{ .Site.Title }}</a></div><nav aria-label=\"Main navigation\"><a class=\"btn\" href=\"{{ .Site.Home.RelPermalink }}\">Home</a> <a class=\"btn\" href=\"{{ \"about/\" | relURL }}\">About</a></nav></header>",
            ["main"] = "<main id=\"main\" class=\"max-w-3xl mx-auto px-4 py-8 space-y-6\">{{ block \"main\" . }}{{ end }}</main>",
            ["footer"] = "<footer class=\"max-w-3xl mx-auto p-4 border-t border-base-300 space-y-4\"><a class=\"underline\" href=\"{{ \"index.xml\" | relURL }}\">RSS feed</a>" +
                (settings.HideHomesteadCredit ? "" : "<p class=\"text-sm\">Made at home with <a class=\"link\" href=\"https://github.com/PoshTools/homestead\">Homestead</a></p>") + "</footer>"
        };
        var body = System.Text.RegularExpressions.Regex.Replace(theme.Layout, "\\{\\{(header|main|footer)\\}\\}", m => regions[m.Groups[1].Value]);
        return head + body + "</body></html>";
    }

    public static readonly (string Path, string Text)[] Files =
    [
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
        {{ define "main" }}<article class="prose-content"><h1>{{ .Title }}</h1><p>{{ .Date.Format "2006-01-02" }} · {{ .Type }}</p>{{ .Content }}
        <p>{{ range .GetTerms "tags" }}<a href="{{ .RelPermalink }}">{{ .LinkTitle }}</a> {{ end }}</p></article>{{ end }}
        """),
        ("layouts/list.html", """
        {{ define "main" }}<h1 class="text-3xl font-bold">{{ .Title }}</h1>{{ .Content }}
        {{ range .Pages.ByDate.Reverse }}<article class="card card-border my-4"><div class="card-body"><h2 class="card-title"><a href="{{ .RelPermalink }}">{{ .Title }}</a></h2><p>{{ .Summary }}</p></div></article>{{ end }}{{ end }}
        """),
        ("layouts/home.html", """
        {{ define "main" }}<h1 class="text-3xl font-bold">{{ .Site.Title }}</h1>
        {{ range (where .Site.RegularPages "Section" "posts").ByDate.Reverse }}<article class="card card-border my-4"><div class="card-body"><h2 class="card-title"><a href="{{ .RelPermalink }}">{{ .Title }}</a></h2><p>{{ .Type }} · {{ .Date.Format "2006-01-02" }}</p><div class="prose-content">{{ .Content }}</div></div></article>{{ end }}{{ end }}
        """)
    ];
}
