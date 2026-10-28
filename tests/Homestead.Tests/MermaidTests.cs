using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Publishing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class MermaidTests
{
    private const string Diagram = "```mermaid\nflowchart LR\nA[Published] --> B[Diagram]\n```";

    [Theory]
    [InlineData("porch")]
    [InlineData("journal")]
    [InlineData("ledger")]
    [InlineData("folio")]
    [InlineData("townsquare")]
    public async Task Diagrams_use_committed_content_in_every_theme_and_private_previews_and_exports(string theme)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path);
        using var owner = app.Client(); using var visitor = app.Client();
        _ = await TestApplication.Setup(owner); _ = await TestApplication.Login(owner);
        await app.WithDatabase(async db => { (await db.SiteSettings.SingleAsync()).ThemeId = theme; await db.SaveChangesAsync(); });
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var post = await articles.CreateAsync(PostType.Microblog);
        await articles.SaveAsync(post.Id, new() { Title = "Diagram note", BodyMarkdown = Diagram, Tags = "Charts" });
        Assert.True((await publishing.TransitionAsync(post.Id, "publish", 1)).Succeeded);
        foreach (var path in new[] { "/", "/notes/", "/tags/charts/", "/posts/diagram-note/" })
        {
            var html = await visitor.GetStringAsync(path);
            Assert.Contains("data-mermaid", html);
            Assert.Contains("/assets/mermaid/diagrams.mjs", html);
        }
        var active = app.Services.GetRequiredService<ActiveSite>().Current!;
        Assert.Contains("assets/mermaid/vendor/mermaid.esm.min.mjs", active.Files);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/assets/mermaid/vendor/mermaid.esm.min.mjs")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync("/admin/mermaid/diagrams.mjs")).StatusCode);
        var input = ArticleInput.From((await articles.FindAsync(post.Id))!);
        input.BodyMarkdown = Diagram.Replace("Published", "PRIVATE draft", StringComparison.Ordinal);
        await articles.SaveAsync(post.Id, input);
        var preview = await owner.GetAsync($"/admin/posts/{post.Id}/preview");
        var previewHtml = await preview.Content.ReadAsStringAsync();
        Assert.Contains("PRIVATE draft", previewHtml); Assert.Contains("/admin/mermaid/diagrams.mjs", previewHtml);
        Assert.Equal("no-store", preview.Headers.CacheControl!.ToString());
        Assert.Contains("script-src https://localhost/admin/mermaid/;", preview.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/admin/mermaid/vendor/mermaid.esm.min.mjs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/admin/mermaid/not-a-module.mjs")).StatusCode);
        Assert.DoesNotContain("PRIVATE draft", await visitor.GetStringAsync("/posts/diagram-note/"));
        await using (var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync())
        using (var zip = new ZipArchive(archive))
        {
            Assert.NotNull(zip.GetEntry("assets/mermaid/vendor/mermaid.esm.min.mjs"));
            using var reader = new StreamReader(zip.GetEntry("posts/diagram-note/index.html")!.Open());
            Assert.DoesNotContain("PRIVATE draft", await reader.ReadToEndAsync());
        }
        await using (var archive = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync())
        using (var zip = new ZipArchive(archive))
        {
            Assert.NotNull(zip.GetEntry("static/assets/mermaid/diagrams.mjs"));
            Assert.NotNull(zip.GetEntry("layouts/_markup/render-codeblock-mermaid.html"));
            if (theme == "porch")
            {
                using var output = new TemporaryData();
                zip.ExtractToDirectory(output.Path);
                using var process = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("HOMESTEAD_HUGO") ?? "hugo")
                {
                    WorkingDirectory = output.Path,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    ArgumentList = { "--panicOnWarning" }
                })!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, await stdout + await stderr);
                var hugoHtml = await File.ReadAllTextAsync(Path.Combine(output.Path, "public/posts/diagram-note/index.html"));
                Assert.Contains("<figure data-mermaid>", hugoHtml);
                Assert.Contains("A[Published]", hugoHtml); Assert.DoesNotContain("PRIVATE draft", hugoHtml);
            }
        }
        input = ArticleInput.From((await articles.FindAsync(post.Id))!); input.BodyMarkdown = "Plain note";
        var version = await articles.SaveAsync(post.Id, input);
        Assert.True((await publishing.TransitionAsync(post.Id, "publish", version)).Succeeded);
        Assert.DoesNotContain("assets/mermaid/diagrams.mjs", app.Services.GetRequiredService<ActiveSite>().Current!.Files);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/assets/mermaid/diagrams.mjs")).StatusCode);
    }

    [Fact]
    public async Task Guide_step_diagrams_load_assets_without_a_body_diagram()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var owner = app.Client();
        _ = await TestApplication.Setup(owner); _ = await TestApplication.Login(owner);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var post = await articles.CreateAsync(PostType.Guide);
        await articles.SaveAsync(post.Id, new() { Title = "Diagram guide", Steps = [new() { BodyMarkdown = Diagram }] });
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(post.Id, "publish", 1)).Succeeded);
        var html = await owner.GetStringAsync("/posts/diagram-guide/");
        Assert.Contains("data-mermaid", html); Assert.Contains("/assets/mermaid/diagrams.mjs", html);
    }
}
