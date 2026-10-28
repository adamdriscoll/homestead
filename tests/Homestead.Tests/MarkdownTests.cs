using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Homestead.Features.Themes;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class MarkdownTests
{
    [Theory]
    [InlineData(PostType.Microblog)]
    [InlineData(PostType.Article)]
    [InlineData(PostType.Review)]
    [InlineData(PostType.Guide)]
    [InlineData(PostType.Gallery)]
    [InlineData(PostType.Checkin)]
    [InlineData(PostType.Fitness)]
    public void Every_post_type_renders_Mermaid_from_its_body(PostType type)
    {
        var post = Snapshot("```mermaid\nflowchart LR\nA --> B\n```") with { Type = type };
        Assert.Contains("<figure data-mermaid>", new SafeMarkdown().RenderContent(post, false));
    }

    [Theory]
    [InlineData("mermaid")]
    [InlineData("Mermaid")]
    public void Mermaid_fences_are_encoded_with_readable_source_and_other_code_stays_ordinary(string language)
    {
        var markdown = $"```{language}\nflowchart LR\nA[\"<script>alert(1)</script>\"] --> B\n```\n\n```csharp\nvar x = 1;\n```";
        var html = new SafeMarkdown().Render(Snapshot(markdown), false);
        Assert.True(MermaidDiagrams.Contains(markdown));
        Assert.Contains("<figure data-mermaid><details open>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("<code class=\"language-csharp\">", html);
        Assert.DoesNotContain("<script", html);
        Assert.False(MermaidDiagrams.Contains("```text\nmermaid\n```"));
        Assert.False(MermaidDiagrams.Contains("`mermaid`"));
    }

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
        var theme = new ThemePackage("porch", new(1, "Porch", "1.0.0",
            ["microblog", "article", "review", "guide", "gallery", "checkin"], ["theme.css"], []), []);
        var html = await renderer.RenderAsync(new(new SiteSettings { Title = "My <home>", CanonicalBaseUrl = "https://example.test", Description = "\" onload=\"evil" }, Snapshot("## Heading\n\n**Bold** and [link](https://example.test)\n\n1. One\n\n```\n<script>\n```"), [], Theme: theme));
        Assert.StartsWith("<!doctype html>", html); Assert.Contains("</html>", html);
        Assert.Contains("<h2>Heading</h2>", html); Assert.Contains("<strong>Bold</strong>", html);
        Assert.DoesNotContain("<home>", html); Assert.DoesNotContain("<script>", html);
        Assert.Contains("https://example.test/posts/story/", html);
    }
    private static ArticleSnapshot Snapshot(string markdown) => new(1, Guid.NewGuid(), "story", "A story", markdown, "A summary", DateTime.UtcNow, DateTime.UtcNow, 1, [], null, []);
}
