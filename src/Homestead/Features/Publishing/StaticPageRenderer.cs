using Homestead.Components.Public;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Homestead.Features.Publishing;

public sealed record PublicPage(SiteSettings Settings, ArticleSnapshot? Article, ArticleSnapshot[] Articles, bool Preview = false);

public interface IStaticPageRenderer
{
    Task<string> RenderAsync(PublicPage page);
}

public sealed class StaticPageRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : IStaticPageRenderer
{
    public async Task<string> RenderAsync(PublicPage page)
    {
        await using var renderer = new HtmlRenderer(services, loggerFactory);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<SiteDocument>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(SiteDocument.Page)] = page }));
            return "<!doctype html>\n" + component.ToHtmlString();
        });
    }
}
