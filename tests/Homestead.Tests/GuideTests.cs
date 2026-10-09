using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class GuideTests
{
    [Fact]
    public async Task Guide_supplies_and_duration_round_trip_stay_private_and_update_atomically()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var author = app.Client();
        _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var id = (await articles.CreateAsync(PostType.Guide)).Id;
        var step = Guid.NewGuid();
        var fields = new Dictionary<string, string>
        {
            ["Input.Version"] = "0",
            ["Input.Title"] = "Bread recipe",
            ["Input.BodyMarkdown"] = "Introduction",
            ["Input.Tools[0].Name"] = "Mixing bowl & spoon",
            ["Input.Tools[0].Url"] = "https://example.test/bowl?x=1&y=2",
            ["Input.Materials[0].Name"] = "2 cups flour <script>",
            ["Input.Materials[0].Quantity"] = "10 boxes",
            ["Input.Steps[0].Id"] = step.ToString(),
            ["Input.Steps[0].BodyMarkdown"] = "Mix **well**.\n\n- Rest\n- Bake",
            ["Input.Steps[0].Duration"] = "01:30:00"
        };
        var editor = $"/admin/posts/{id}/edit";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(author, editor, fields, editor + "?handler=Save")).StatusCode);
        var post = (await articles.FindAsync(id))!;
        var input = ArticleInput.From(post);
        Assert.Equal("Mixing bowl & spoon", input.Tools.Single().Name);
        Assert.Equal("10 boxes", input.Materials.Single().Quantity);
        Assert.Equal(5400, input.Steps.Single().DurationSeconds);
        var preview = await author.GetStringAsync($"/admin/posts/{id}/preview");
        Assert.Contains("1 hour, 30 minutes", preview); Assert.Contains("<strong>well</strong>", preview);
        Assert.Contains("2 cups flour &lt;script&gt;", preview); Assert.DoesNotContain("<script>", preview);
        Assert.True((await publishing.TransitionAsync(id, "publish", 1)).Succeeded);
        var active = app.Services.GetRequiredService<ActiveSite>();
        var live = active.Current!.Articles.Single();
        var publicPath = $"/posts/{live.Slug}/";
        var html = await author.GetStringAsync(publicPath);
        Assert.Contains("href=\"https://example.test/bowl?x=1&amp;y=2\"", html);
        Assert.Contains("datetime=\"PT5400S\"", html);
        Assert.False(ArticleService.HasDraftChanges((await articles.FindAsync(id))!, live));

        input = ArticleInput.From((await articles.FindAsync(id))!);
        input.Materials.Single().Quantity = "5 switches <script>";
        await articles.SaveAsync(id, input);
        Assert.True(ArticleService.HasDraftChanges((await articles.FindAsync(id))!, live));
        Assert.Equal("10 boxes", active.Current!.Articles.Single().Materials.Single().Quantity);
        Assert.Equal(html, await author.GetStringAsync(publicPath));
        var quantityPreview = await author.GetStringAsync($"/admin/posts/{id}/preview");
        Assert.Contains("5 switches &lt;script&gt;", quantityPreview); Assert.DoesNotContain("5 switches <script>", quantityPreview);

        input = ArticleInput.From((await articles.FindAsync(id))!);
        input.Tools.Single().Name = "PRIVATE TOOL";
        var version = await articles.SaveAsync(id, input);
        Assert.True(ArticleService.HasDraftChanges((await articles.FindAsync(id))!, live));
        Assert.Equal(html, await author.GetStringAsync(publicPath));
        Assert.True((await publishing.RebuildAsync()).Succeeded);
        Assert.Equal(html, await author.GetStringAsync(publicPath));
        input.Version = version; input.Tools.Single().Name = live.Tools.Single().Name;
        input.Materials.Clear(); input.Steps.Single().Duration = null;
        version = await articles.SaveAsync(id, input);
        Assert.True(ArticleService.HasDraftChanges((await articles.FindAsync(id))!, live));
        Assert.Contains("1 hour, 30 minutes", await author.GetStringAsync(publicPath));
        Assert.True((await publishing.TransitionAsync(id, "publish", version)).Succeeded);
        var updated = active.Current!.Articles.Single();
        Assert.Empty(updated.Materials); Assert.Null(updated.Steps.Single().DurationSeconds);
        Assert.Equal(live.Slug, updated.Slug); Assert.Equal(live.PublishedAtUtc, updated.PublishedAtUtc);
        Assert.DoesNotContain("datetime=\"PT", await author.GetStringAsync(publicPath));
        Assert.False(ArticleService.HasDraftChanges((await articles.FindAsync(id))!, updated));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("//example.test")]
    [InlineData("https://user:pass@example.test")]
    [InlineData("https://example.test/\nnext")]
    public async Task Unsafe_supply_links_are_rejected_without_changing_draft(string url)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var id = (await articles.CreateAsync(PostType.Guide)).Id;
        foreach (var tool in new[] { true, false })
        {
            var input = new ArticleInput { Title = "Guide" };
            (tool ? input.Tools : input.Materials).Add(new() { Name = "Supply", Url = url });
            await Assert.ThrowsAsync<InvalidOperationException>(() => articles.SaveAsync(id, input));
            var post = (await articles.FindAsync(id))!;
            Assert.Equal(0, post.Draft!.Version); Assert.Empty(post.Supplies);
        }
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("1.00:00:00")]
    [InlineData("00:00:01.5")]
    public async Task Invalid_duration_is_rejected_without_changing_draft(string duration)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var id = (await articles.CreateAsync(PostType.Guide)).Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => articles.SaveAsync(id, new()
        { Steps = [new() { BodyMarkdown = "Wait", Duration = TimeSpan.Parse(duration, System.Globalization.CultureInfo.InvariantCulture) }] }));
        Assert.Equal(0, (await articles.FindAsync(id))!.Draft!.Version);
    }

    [Fact]
    public async Task Supply_only_drafts_persist_and_incomplete_supplies_block_publication()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var id = Guid.NewGuid();
        var input = new ArticleInput { Tools = [new() { Name = "Spoon" }] };
        Assert.Equal(1, await articles.SaveAsync(id, input, PostType.Guide));
        input = ArticleInput.From((await articles.FindAsync(id))!);
        input.Title = "Guide"; input.Steps.Add(new() { BodyMarkdown = "Stir" });
        input.Materials.Add(new() { Url = "https://example.test/flour" });
        Assert.Equal(2, await articles.SaveAsync(id, input));
        Assert.False((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(id, "publish", 2)).Succeeded);
        Assert.Empty(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(5400)]
    [InlineData(86399)]
    [InlineData(86400)]
    [InlineData(90061)]
    [InlineData(31536000)]
    public void Existing_durations_round_trip_as_TimeSpan_without_losing_seconds(int? seconds)
    {
        var input = GuideStepInput.From(new() { DurationSeconds = seconds });
        Assert.Equal((long?)seconds, input.DurationSeconds);
        Assert.Equal(seconds is { } value ? TimeSpan.FromSeconds(value) : (TimeSpan?)null, input.Duration);
    }

    [Theory]
    [InlineData(30, "30 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(5400, "1 hour, 30 minutes")]
    [InlineData(90061, "25 hours, 1 minute, 1 second")]
    public void Step_duration_is_humanized_without_losing_precision(int seconds, string expected)
        => Assert.Equal(expected, new GuideStepSnapshot(Guid.NewGuid(), "Wait") { DurationSeconds = seconds }.DurationText);

    [Fact]
    public async Task Existing_long_durations_survive_form_saves_and_can_be_replaced_or_cleared()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var id = (await articles.CreateAsync(PostType.Guide)).Id;
        var step = Guid.NewGuid();
        await articles.SaveAsync(id, new() { Title = "Old duration", Steps = [new() { Id = step, BodyMarkdown = "Wait." }] });
        await app.WithDatabase(async db => { (await db.GuideSteps.SingleAsync()).DurationSeconds = 90061; await db.SaveChangesAsync(); });
        var editor = $"/admin/posts/{id}/edit";
        var html = await client.GetStringAsync(editor);
        Assert.Contains("25 hours, 1 minute, 1 second", html); Assert.DoesNotContain("DurationDays", html);
        var fields = new Dictionary<string, string>
        {
            ["Input.Version"] = "1",
            ["Input.Title"] = "Updated title",
            ["Input.Steps[0].Id"] = step.ToString(),
            ["Input.Steps[0].BodyMarkdown"] = "Wait.",
            ["Input.Steps[0].Duration"] = "1.01:01:01"
        };
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, editor, fields, editor + "?handler=Save")).StatusCode);
        await app.WithDatabase(async db => Assert.Equal(90061, (await db.GuideSteps.SingleAsync()).DurationSeconds));
        fields["Input.Version"] = "2"; fields["Input.Steps[0].ReplacementDuration"] = "01:30:00";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, editor, fields, editor + "?handler=Save")).StatusCode);
        await app.WithDatabase(async db => Assert.Equal(5400, (await db.GuideSteps.SingleAsync()).DurationSeconds));
        await app.WithDatabase(async db => { (await db.GuideSteps.SingleAsync()).DurationSeconds = 90061; await db.SaveChangesAsync(); });
        fields["Input.Version"] = "3"; fields["Input.Steps[0].ReplacementDuration"] = "00:00:00";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, editor, fields, editor + "?handler=Save")).StatusCode);
        await app.WithDatabase(async db => Assert.Null((await db.GuideSteps.SingleAsync()).DurationSeconds));
    }

    [Fact]
    public void Legacy_guide_snapshots_have_empty_supplies_and_no_duration()
    {
        var snapshot = JsonSerializer.Deserialize<ArticleSnapshot>("""
            { "Type": 3, "Steps": [{ "Id": "00000000-0000-0000-0000-000000000001", "BodyMarkdown": "Mix" }] }
            """);
        Assert.Empty(snapshot!.Tools); Assert.Empty(snapshot.Materials);
        Assert.Null(snapshot.Steps.Single().DurationSeconds); Assert.Null(snapshot.Steps.Single().DurationText);
        Assert.Null(snapshot.Steps.Single().Title);
        Assert.Null(JsonSerializer.Deserialize<GuideSupplySnapshot>("""{ "Name": "Flour", "Url": null }""")!.Quantity);
    }

    [Fact]
    public async Task Quantity_only_drafts_persist_and_overlong_quantities_are_rejected()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var id = Guid.NewGuid(); var input = new ArticleInput { Materials = [new() { Quantity = "5 switches" }] };
        Assert.Equal(1, await articles.SaveAsync(id, input, PostType.Guide));
        input = ArticleInput.From((await articles.FindAsync(id))!);
        Assert.Equal("5 switches", input.Materials.Single().Quantity);
        input.Materials.Single().Quantity = new string('x', 301);
        await Assert.ThrowsAsync<InvalidOperationException>(() => articles.SaveAsync(id, input));
        var unchanged = (await articles.FindAsync(id))!;
        Assert.Equal(1, unchanged.Draft!.Version); Assert.Equal("5 switches", unchanged.Supplies.Single().Quantity);
    }

    [Fact]
    public async Task Quantity_migration_preserves_existing_materials_and_defaults_quantity_to_null()
    {
        using var data = new TemporaryData(); Directory.CreateDirectory(data.Path);
        await using var db = new HomesteadDbContext(new DbContextOptionsBuilder<HomesteadDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data.Path, "upgrade.db"), ForeignKeys = true }.ToString()).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261009131212_GuideSuppliesAndDurations");
        var post = new Post { Type = PostType.Guide, Draft = new() { Title = "Existing guide" } };
        db.Posts.Add(post); await db.SaveChangesAsync();
        var supplyId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO GuideSupplies (Id, PostId, Kind, \"Order\", Name, Url) VALUES ({supplyId}, {post.Id}, 1, 0, 'Switches', 'https://example.test/switches')");
        db.ChangeTracker.Clear(); await db.Database.MigrateAsync();
        var material = await db.GuideSupplies.SingleAsync();
        Assert.Equal(supplyId, material.Id); Assert.Equal("Switches", material.Name);
        Assert.Equal("https://example.test/switches", material.Url); Assert.Null(material.Quantity);
        material.Quantity = "5 switches"; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal("5 switches", (await db.GuideSupplies.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task Upgrade_preserves_steps_photo_assignments_and_locked_publication()
    {
        using var data = new TemporaryData(); Directory.CreateDirectory(data.Path);
        await using var db = new HomesteadDbContext(new DbContextOptionsBuilder<HomesteadDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data.Path, "upgrade.db"), ForeignKeys = true }.ToString()).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261008214025_LocationMap");
        var post = new Post { Type = PostType.Guide, Slug = "old-guide", FirstPublishedAtUtc = DateTime.UtcNow, Draft = new() { Title = "Old guide" } };
        var media = new MediaAsset { StoredFilename = "old.png" };
        db.Posts.Add(post); db.MediaAssets.Add(media); await db.SaveChangesAsync();
        var stepId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO GuideSteps (Id, PostId, \"Order\", BodyMarkdown) VALUES ({stepId}, {post.Id}, 0, 'Existing instructions')");
        db.PostMedia.Add(new() { PostId = post.Id, MediaAssetId = media.Id, GuideStepId = stepId }); await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); await db.Database.MigrateAsync();
        var step = await db.GuideSteps.SingleAsync();
        Assert.Equal(stepId, step.Id); Assert.Equal("Existing instructions", step.BodyMarkdown); Assert.Null(step.DurationSeconds); Assert.Null(step.Title);
        Assert.Equal(stepId, (await db.PostMedia.SingleAsync()).GuideStepId); Assert.Empty(await db.GuideSupplies.ToArrayAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE GuideSteps SET DurationSeconds = 0 WHERE Id = {stepId}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Posts SET Slug = 'changed' WHERE Id = {post.Id}"));
    }

    [Fact]
    public async Task Step_titles_create_drafts_and_stay_private_until_publication()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var author = app.Client();
        _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var id = Guid.NewGuid();
        Assert.Equal(1, await articles.SaveAsync(id, new() { Steps = [new() { Title = "Mix ingredients" }] }, PostType.Guide));
        var input = ArticleInput.From((await articles.FindAsync(id))!);
        Assert.Equal("Mix ingredients", input.Steps.Single().Title);
        input.Title = "Titled guide"; input.Steps.Single().BodyMarkdown = "Stir well.";
        Assert.Equal(2, await articles.SaveAsync(id, input));
        Assert.True((await publishing.TransitionAsync(id, "publish", 2)).Succeeded);
        var active = app.Services.GetRequiredService<ActiveSite>(); var live = active.Current!.Articles.Single();
        var publicPath = $"/posts/{live.Slug}/"; var publicHtml = await author.GetStringAsync(publicPath);
        Assert.Contains("<h2>Mix ingredients</h2>", publicHtml);
        input = ArticleInput.From((await articles.FindAsync(id))!); input.Steps.Single().Title = "Bake <script>";
        Assert.Equal(3, await articles.SaveAsync(id, input));
        Assert.True(ArticleService.HasDraftChanges((await articles.FindAsync(id))!, live));
        Assert.Equal("Mix ingredients", active.Current!.Articles.Single().Steps.Single().Title);
        Assert.Contains("<h2>Bake &lt;script&gt;</h2>", await author.GetStringAsync($"/admin/posts/{id}/preview"));
        Assert.True((await publishing.RebuildAsync()).Succeeded);
        Assert.Equal(publicHtml, await author.GetStringAsync(publicPath));
        Assert.True((await publishing.TransitionAsync(id, "publish", 3)).Succeeded);
        Assert.Contains("<h2>Bake &lt;script&gt;</h2>", await author.GetStringAsync(publicPath));
        input = ArticleInput.From((await articles.FindAsync(id))!); input.Steps.Single().Title = new string('x', 301);
        await Assert.ThrowsAsync<InvalidOperationException>(() => articles.SaveAsync(id, input));
        Assert.Equal(3, (await articles.FindAsync(id))!.Draft!.Version);
        input.Steps.Single().Title = " ";
        Assert.Equal(4, await articles.SaveAsync(id, input));
        Assert.Null((await articles.FindAsync(id))!.Steps.Single().Title);
        Assert.True((await publishing.TransitionAsync(id, "publish", 4)).Succeeded);
        Assert.DoesNotContain("<h2>Bake", await author.GetStringAsync(publicPath));
    }

    [Fact]
    public async Task Hugo_build_keeps_inline_step_photos_local_and_renders_them_once()
    {
        using var data = new TemporaryData(); using var output = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var id = (await articles.CreateAsync(PostType.Guide)).Id;
        await using var bytes = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Homestead/wwwroot/icon-192.png"));
        var photo = await scope.ServiceProvider.GetRequiredService<MediaService>().UploadAsync(id,
            new FormFile(bytes, 0, bytes.Length, "Upload", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" }, "Step photo");
        var step = Guid.NewGuid();
        await articles.SaveAsync(id, new() { Title = "Inline guide", Steps = [new() { Id = step, BodyMarkdown = $"Mix\n\n![Photo]({photo.Url})" }], StepImages = new() { [photo.Id] = step } });
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(id, "publish", 1)).Succeeded);
        await using (var archive = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync())
        {
            using var zip = new ZipArchive(archive); Directory.CreateDirectory(output.Path); zip.ExtractToDirectory(output.Path);
        }
        using var process = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("HOMESTEAD_HUGO") ?? "hugo")
        { WorkingDirectory = output.Path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, ArgumentList = { "--panicOnWarning" } })!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await stdout + await stderr);
        var html = File.ReadAllText(Path.Combine(output.Path, "public", "posts", "inline-guide", "index.html"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<img\\b"));
        Assert.Contains(photo.Filename, html); Assert.DoesNotContain("/admin/media/", html); Assert.DoesNotContain(photo.Url, html);
        Assert.True(File.Exists(Path.Combine(output.Path, "public", "posts", "inline-guide", photo.Filename)));
    }
}
