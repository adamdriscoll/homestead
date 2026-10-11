using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Homestead.Data.Entities;
using Homestead.Features.Publishing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class DashboardTests
{
    [Theory]
    [InlineData(PostType.Microblog)]
    [InlineData(PostType.Article)]
    [InlineData(PostType.Review)]
    [InlineData(PostType.Guide)]
    [InlineData(PostType.Gallery)]
    [InlineData(PostType.Checkin)]
    [InlineData(PostType.Fitness)]
    public async Task Styled_navigation_opens_routed_editors_and_saves_each_type_privately_without_empty_drafts(PostType type)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path);
        using var client = app.Client(); _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var dashboard = await client.GetStringAsync("/admin");
        var link = Regex.Match(dashboard, $"<a[^>]*data-post-type-link=\"{type}\"[^>]*>").Value;
        var path = WebUtility.HtmlDecode(Regex.Match(link, "href=\"([^\"]+)\"").Groups[1].Value);
        var html = await client.GetStringAsync(path);
        Assert.Contains($"data-post-type=\"{type}\"", html);
        Assert.Contains($"aria-current=\"page\" data-post-type-link=\"{type}\"", html);
        Assert.Equal(7, Regex.Matches(html, "data-post-type-link=").Count);
        Assert.DoesNotContain("New article", html);
        await app.WithDatabase(async db => Assert.Empty(await db.Posts.ToArrayAsync()));
        var form = Regex.Match(html, "<form[^>]*id=\"article-editor\"[^>]*>").Value;
        var action = WebUtility.HtmlDecode(Regex.Match(form, "action=\"([^\"]+)\"").Groups[1].Value);
        var fields = new Dictionary<string, string> { ["Input.Version"] = "0", ["NewPostType"] = type.ToString() };
        var empty = await TestApplication.Submit(client, path, fields, action);
        Assert.Equal(HttpStatusCode.Redirect, empty.StatusCode);
        Assert.Contains($"newPostType={type}", empty.Headers.Location!.ToString());
        await app.WithDatabase(async db => Assert.Empty(await db.Posts.ToArrayAsync()));
        fields["Input.BodyMarkdown"] = "Private dashboard content";
        var saved = await TestApplication.Submit(client, path, fields, action);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var savedUrl = saved.Headers.Location!.ToString();
        Assert.StartsWith(type == PostType.Microblog ? "/admin?id=" : "/admin/posts/", savedUrl);
        Assert.Contains($"data-post-type=\"{type}\"", await client.GetStringAsync(savedUrl));
        await app.WithDatabase(async db =>
        {
            Assert.Equal(type, (await db.Posts.SingleAsync()).Type);
            Assert.Equal("Private dashboard content", (await db.PostDrafts.SingleAsync()).BodyMarkdown);
        });
        Assert.DoesNotContain("Private dashboard content", await client.GetStringAsync("/"));
        fields["Input.BodyMarkdown"] = "Stale dashboard content";
        Assert.Equal(HttpStatusCode.Conflict, (await TestApplication.Submit(client, savedUrl, fields, action)).StatusCode);
    }

    [Fact]
    public async Task Dashboard_hides_disabled_links_and_redirects_old_type_urls_to_routed_editors()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path);
        using var client = app.Client(); _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        await app.WithDatabase(async db =>
        {
            var settings = await db.SiteSettings.SingleAsync(); settings.EnableNotes = false;
            await db.SaveChangesAsync();
        });
        var html = await client.GetStringAsync("/admin");
        Assert.DoesNotContain("id=\"article-editor\"", html);
        Assert.DoesNotContain("data-post-type-link=\"Microblog\"", html);
        var redirected = await client.GetAsync("/admin?newPostType=Article");
        Assert.Equal(HttpStatusCode.Redirect, redirected.StatusCode);
        Assert.StartsWith("/admin/posts/", redirected.Headers.Location!.ToString());
        Assert.Contains("newPostType=Article", redirected.Headers.Location.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/admin?newPostType=999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/admin?newPostType=unknown")).StatusCode);
        await app.WithDatabase(async db => Assert.Empty(await db.Posts.ToArrayAsync()));
    }

    [Fact]
    public async Task Dashboard_notes_save_privately_reject_stale_edits_and_publish_without_a_title()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path);
        using var client = app.Client(); _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var html = await client.GetStringAsync("/admin");
        var form = Regex.Match(html, "<form[^>]*id=\"article-editor\"[^>]*>").Value;
        var action = WebUtility.HtmlDecode(Regex.Match(form, "action=\"([^\"]+)\"").Groups[1].Value);
        Assert.Contains("handler=Save", action);
        await app.WithDatabase(async db => Assert.Empty(await db.Posts.ToArrayAsync()));
        var fields = new Dictionary<string, string> { ["Input.Version"] = "0", ["Input.BodyMarkdown"] = "A quick private note", ["NewPostType"] = "Microblog" };
        using var request = new HttpRequestMessage(HttpMethod.Post, action);
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        request.Headers.Accept.ParseAdd("application/json"); request.Content = new FormUrlEncodedContent(fields);
        var saved = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var result = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        var editorUrl = result.RootElement.GetProperty("editorUrl").GetString()!;
        Assert.StartsWith("/admin?id=", editorUrl);
        Assert.DoesNotContain("A quick private note", await client.GetStringAsync("/"));
        Assert.Contains("A quick private note", await client.GetStringAsync(editorUrl));
        var stale = await TestApplication.Submit(client, editorUrl, new() { ["Input.Version"] = "0", ["Input.BodyMarkdown"] = "Stale overwrite" }, editorUrl + "&handler=Save");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await app.WithDatabase(async db => Assert.Equal("A quick private note", (await db.PostDrafts.SingleAsync()).BodyMarkdown));
        var published = await TestApplication.Submit(client, editorUrl, new() { ["Input.Version"] = "1", ["Input.BodyMarkdown"] = "A quick public note" }, editorUrl + "&handler=Publish");
        Assert.Equal(HttpStatusCode.Redirect, published.StatusCode); Assert.Equal("/admin", published.Headers.Location!.ToString());
        var live = Assert.Single(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
        Assert.Equal(PostType.Microblog, live.Type); Assert.True(string.IsNullOrEmpty(live.Title));
        Assert.Contains("A quick public note", await client.GetStringAsync($"/posts/{live.Slug}/"));
        var dashboard = await client.GetStringAsync("/admin");
        Assert.Contains("A quick public note", dashboard);
        Assert.DoesNotContain("Recent posts</h", dashboard);
        Assert.DoesNotContain("All posts</a", dashboard);
        Assert.DoesNotContain("A little space for everything", dashboard);
        Assert.DoesNotContain("Choose a post type, add content", dashboard);
        Assert.DoesNotContain("Create something", dashboard);
        Assert.Contains("Last successful build:", dashboard[(dashboard.LastIndexOf("<footer", StringComparison.Ordinal))..]);
        await app.WithDatabase(async db => Assert.Single(await db.Posts.ToArrayAsync()));
    }

    [Fact]
    public async Task Dashboard_writes_require_authentication_and_antiforgery()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path);
        using var client = app.Client(); _ = await TestApplication.Setup(client);
        var action = $"/admin?id={Guid.NewGuid()}&handler=Publish";
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(action, new FormUrlEncodedContent([]))).StatusCode);
        _ = await TestApplication.Login(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(action, new FormUrlEncodedContent(new Dictionary<string, string>
        { ["NewPostType"] = "Microblog", ["Input.Version"] = "0", ["Input.BodyMarkdown"] = "Missing token" }))).StatusCode);
        await app.WithDatabase(async db => Assert.Empty(await db.Posts.ToArrayAsync()));
    }
}
