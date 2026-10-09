using System.Text.Encodings.Web;
using Homestead.Components.Public;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Themes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Homestead.Features.Publishing;

public sealed record PublicPage(SiteSettings Settings, ArticleSnapshot? Article, ArticleSnapshot[] Articles,
    bool Preview = false, string Path = "/", string Heading = "Welcome home.", ArticleMedia[]? Branding = null, ThemePackage? Theme = null)
{
    public IReadOnlyDictionary<Guid, string>? RenderedPosts { get; init; }
}

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
            var compiler = page.Theme.Variant.Templates is { Count: > 0 } ? services.GetRequiredService<RazorThemeCompiler>() : null;
            var model = compiler is null ? null : ThemePageModel.From(page, services.GetRequiredService<SafeMarkdown>());
            async Task<string> Template(Type type, ThemePageModel templateModel)
            {
                var component = await renderer.RenderComponentAsync(type, ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(ThemeComponent.Model)] = templateModel }));
                return component.ToHtmlString();
            }
            var posts = new Dictionary<Guid, string>();
            foreach (var post in page.Article is { } article ? [article] : page.Articles)
            {
                if (model is null) break;
                var name = post.Type switch { PostType.Microblog => "Note", PostType.Checkin => "Checkin", _ => post.Type.ToString() };
                var type = compiler!.Resolve(page.Theme, name) ?? compiler.Resolve(page.Theme, "Post");
                if (type is null) continue;
                var fallback = await renderer.RenderComponentAsync<PostDocument>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(PostDocument.Page)] = page, [nameof(PostDocument.Post)] = post, [nameof(PostDocument.Listing)] = page.Article is null }));
                posts[post.PostId] = await Template(type, model with
                {
                    Post = model.Post ?? model.Posts.Single(x => x.Id == post.PostId),
                    DefaultContent = new(fallback.ToHtmlString())
                });
            }
            page = page with { RenderedPosts = posts };
            var regions = new Dictionary<string, string>();
            foreach (var region in new[] { "head", "header", "main", "footer" })
            {
                var component = await renderer.RenderComponentAsync<SiteDocument>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(SiteDocument.Page)] = page, [nameof(SiteDocument.Region)] = region }));
                regions[region] = component.ToHtmlString();
            }
            if (model is not null) model = model with { Head = new(regions["head"]) };
            foreach (var (name, region) in new[] { ("Header", "header"), ("Content", "main"), ("Footer", "footer") })
            {
                if (model is null) break;
                model = model with { Header = new(regions["header"]), Content = new(regions["main"]), Footer = new(regions["footer"]) };
                if (compiler!.Resolve(page.Theme, name) is { } type)
                    regions[region] = await Template(type, model with { DefaultContent = new(regions[region]) });
            }
            // Replace slots in one pass: authored content containing slot-like text is never reinterpreted.
            var body = System.Text.RegularExpressions.Regex.Replace(page.Theme.Layout, "\\{\\{(header|main|footer)\\}\\}",
                match => regions[match.Groups[1].Value]);
            var document = $"<!doctype html>\n<html lang=\"en\" data-public-theme=\"{HtmlEncoder.Default.Encode(page.Theme.Id)}\" data-accent=\"{HtmlEncoder.Default.Encode(page.Settings.AccentColor.ToLowerInvariant())}\">" +
                regions["head"] + "<body><a href=\"#main\" class=\"btn absolute -top-20 focus:top-2 left-2\">Skip to content</a>" + body + "</body></html>";
            if (model is null) return document;
            model = model with { Header = new(regions["header"]), Content = new(regions["main"]), Footer = new(regions["footer"]), DefaultContent = new(document) };
            return compiler!.Resolve(page.Theme, "Main") is { } main ? await Template(main, model) : document;
        });
    }
}
