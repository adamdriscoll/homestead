using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class MarkdownTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("[run](javascript:alert%281%29)")]
    [InlineData("[run](data:text/html,payload)")]
    [InlineData("[run](java&#x73;cript:alert%281%29)")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("![run](https://attacker.test/pixel.png)")]
    public void Markdown_cannot_create_executable_HTML_or_unapproved_images(string markdown)
    {
        var service = new SafeMarkdown();
        var post = Snapshot(markdown);
        var html = service.Render(post, preview: false);
        Assert.DoesNotContain("<script", html); Assert.DoesNotContain("<img src=\"https://attacker", html);
        Assert.DoesNotContain("href=\"javascript:", html); Assert.DoesNotContain("href=\"data:", html);
        Assert.DoesNotContain(" onerror=", html.Replace("&lt;img src=x onerror=alert(1)&gt;", ""));
    }

    [Fact]
    public async Task Complete_Razor_document_renders_without_an_HTTP_request_and_encodes_metadata()
    {
        var services = new ServiceCollection().AddLogging().AddSingleton<SafeMarkdown>().BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var renderer = new StaticPageRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var html = await renderer.RenderAsync(new(new SiteSettings { Title = "My <home>", CanonicalBaseUrl = "https://example.test", Description = "\" onload=\"evil" }, Snapshot("## Heading\n\n**Bold** and [link](https://example.test)\n\n1. One\n\n```\n<script>\n```"), []));
        Assert.StartsWith("<!doctype html>", html); Assert.Contains("</html>", html);
        Assert.Contains("<h2>Heading</h2>", html); Assert.Contains("<strong>Bold</strong>", html);
        Assert.DoesNotContain("<home>", html); Assert.DoesNotContain("<script>", html);
        Assert.Contains("https://example.test/posts/story/", html);
    }
    private static ArticleSnapshot Snapshot(string markdown) => new(1, Guid.NewGuid(), "story", "A story", markdown, "A summary", DateTime.UtcNow, DateTime.UtcNow, 1, [], null, []);
}
