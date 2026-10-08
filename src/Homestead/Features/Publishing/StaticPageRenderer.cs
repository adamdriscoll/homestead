using System.Text.Encodings.Web;
using Homestead.Components.Public;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Themes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Homestead.Features.Publishing;

public sealed record PublicPage(SiteSettings Settings, ArticleSnapshot? Article, ArticleSnapshot[] Articles,
    bool Preview = false, string Path = "/", string Heading = "Welcome home.", ArticleMedia[]? Branding = null, ThemePackage? Theme = null);

public interface IStaticPageRenderer
{
    Task<string> RenderAsync(PublicPage page);
}

public sealed class StaticPageRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : IStaticPageRenderer
{
    public async Task<string> RenderAsync(PublicPage page)
    {
        page = page with { Theme = page.Theme ?? services.GetRequiredService<ThemeService>().Resolve(page.Settings.ThemeId) };
        await using var renderer = new HtmlRenderer(services, loggerFactory);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var regions = new Dictionary<string, string>();
            foreach (var region in new[] { "head", "header", "main", "footer" })
            {
                var component = await renderer.RenderComponentAsync<SiteDocument>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(SiteDocument.Page)] = page, [nameof(SiteDocument.Region)] = region }));
                regions[region] = component.ToHtmlString();
            }
            // Replace slots in one pass: authored content containing slot-like text is never reinterpreted.
            var body = System.Text.RegularExpressions.Regex.Replace(page.Theme.Layout, "\\{\\{(header|main|footer)\\}\\}",
                match => regions[match.Groups[1].Value]);
            return $"<!doctype html>\n<html lang=\"en\" data-public-theme=\"{HtmlEncoder.Default.Encode(page.Theme.Id)}\" data-accent=\"{HtmlEncoder.Default.Encode(page.Settings.AccentColor.ToLowerInvariant())}\">" +
                regions["head"] + "<body><a href=\"#main\" class=\"btn absolute -top-20 focus:top-2 left-2\">Skip to content</a>" + body + "</body></html>";
        });
    }
}
