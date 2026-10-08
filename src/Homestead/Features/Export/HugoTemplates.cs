namespace Homestead.Features.Export;

internal static class HugoTemplates
{
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
        {{ define "main" }}<article class="prose"><h1>{{ .Title }}</h1><p>{{ .Date.Format "2006-01-02" }} · {{ .Type }}</p>{{ .Content }}
        <p>{{ range .GetTerms "tags" }}<a href="{{ .RelPermalink }}">{{ .LinkTitle }}</a> {{ end }}</p></article>{{ end }}
        """),
        ("layouts/list.html", """
        {{ define "main" }}<h1 class="text-3xl font-bold">{{ .Title }}</h1>{{ .Content }}
        {{ range .Pages.ByDate.Reverse }}<article class="card card-border my-4"><div class="card-body"><h2 class="card-title"><a href="{{ .RelPermalink }}">{{ .Title }}</a></h2><p>{{ .Summary }}</p></div></article>{{ end }}{{ end }}
        """),
        ("layouts/home.html", """
        {{ define "main" }}<h1 class="text-3xl font-bold">{{ .Site.Title }}</h1>
        {{ range (where .Site.RegularPages "Section" "posts").ByDate.Reverse }}<article class="card card-border my-4"><div class="card-body"><h2 class="card-title"><a href="{{ .RelPermalink }}">{{ .Title }}</a></h2><p>{{ .Type }} · {{ .Date.Format "2006-01-02" }}</p><div class="prose">{{ .Content }}</div></div></article>{{ end }}{{ end }}
        """)
    ];
}
