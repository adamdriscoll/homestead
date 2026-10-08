using System.Net;
using System.Text.Json;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class LocationTests
{
    [Theory]
    [InlineData("91", "0")]
    [InlineData("0", "-181")]
    [InlineData("0", "")]
    [InlineData("", "0")]
    [InlineData("NaN", "0")]
    [InlineData("Infinity", "0")]
    public async Task Invalid_coordinates_are_rejected_without_saving(string latitude, string longitude)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var post = await articles.CreateAsync(); var path = $"/admin/posts/{post.Id}/edit";
        var response = await TestApplication.Submit(client, path, new()
        {
            ["Input.Version"] = "0",
            ["Input.LocationName"] = "Private place",
            ["Input.Latitude"] = latitude,
            ["Input.Longitude"] = longitude
        }, path + "?handler=Save");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        scope.ServiceProvider.GetRequiredService<Homestead.Data.HomesteadDbContext>().ChangeTracker.Clear();
        var saved = (await articles.FindAsync(post.Id))!.Draft!;
        Assert.Null(saved.LocationName); Assert.Equal(0, saved.Version);
    }

    [Fact]
    public async Task Place_only_checkins_get_distinct_stable_urls_and_safe_public_content()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var publisher = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var first = await articles.CreateAsync(PostType.Checkin); var second = await articles.CreateAsync(PostType.Checkin);
        foreach (var post in new[] { first, second })
        {
            await articles.SaveAsync(post.Id, new() { LocationName = " Café <script>place</script> ", LocationAddress = "1 & 2 Street", Latitude = 0, Longitude = 0 });
            Assert.True((await publisher.TransitionAsync(post.Id, "publish", 1)).Succeeded);
        }
        var active = app.Services.GetRequiredService<ActiveSite>();
        var live = active.Current!.Articles.Single(x => x.PostId == first.Id);
        Assert.StartsWith("checkin-cafe", live.Slug);
        Assert.Equal(2, active.Current.Articles.Select(x => x.Slug).Distinct().Count());
        Assert.Equal("Check-in at Café <script>place</script>", live.DisplayTitle);
        foreach (var path in new[] { $"/posts/{live.Slug}/", "/", "/checkins/", "/rss.xml" })
        {
            var html = await client.GetStringAsync(path);
            Assert.Contains("place", html); Assert.DoesNotContain("<script>", html);
        }
        var input = ArticleInput.From((await articles.FindAsync(first.Id))!);
        input.LocationName = "Updated place";
        input.Version = await articles.SaveAsync(first.Id, input);
        Assert.True((await publisher.TransitionAsync(first.Id, "publish", input.Version)).Succeeded);
        var updated = active.Current!.Articles.Single(x => x.PostId == first.Id);
        Assert.Equal(live.Slug, updated.Slug); Assert.Equal(live.PublishedAtUtc, updated.PublishedAtUtc);
    }

    [Fact]
    public async Task Location_only_edits_and_removal_remain_private_until_publication()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var publisher = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var post = await articles.CreateAsync(PostType.Review);
        var input = new ArticleInput { Subject = "Coffee", Rating = 4, BodyMarkdown = "Warm cup", LocationName = "Public shop" };
        input.Version = await articles.SaveAsync(post.Id, input);
        Assert.True((await publisher.TransitionAsync(post.Id, "publish", input.Version)).Succeeded);
        var active = app.Services.GetRequiredService<ActiveSite>(); var live = active.Current!.Articles.Single();
        input.LocationName = "PRIVATE shop";
        input.Version = await articles.SaveAsync(post.Id, input);
        Assert.True(ArticleService.HasDraftChanges((await articles.FindAsync(post.Id))!, live));
        Assert.Contains("PRIVATE shop", await client.GetStringAsync($"/admin/posts/{post.Id}/preview"));
        Assert.True((await publisher.RebuildAsync()).Succeeded);
        Assert.DoesNotContain("PRIVATE", await client.GetStringAsync($"/posts/{live.Slug}/"));
        await Assert.ThrowsAsync<DraftConflictException>(() => articles.SaveAsync(post.Id, new() { Version = 1, LocationName = "Stale shop" }));
        input.LocationName = " "; input.LocationAddress = " ";
        input.Version = await articles.SaveAsync(post.Id, input);
        Assert.True((await publisher.TransitionAsync(post.Id, "publish", input.Version)).Succeeded);
        Assert.Null(active.Current!.Articles.Single().Location);
        Assert.DoesNotContain("Location:", await client.GetStringAsync($"/posts/{live.Slug}/"));
        Assert.False(ArticleService.HasDraftChanges((await articles.FindAsync(post.Id))!, active.Current.Articles.Single()));
    }

    [Fact]
    public void Legacy_snapshots_default_to_no_location()
    {
        var snapshot = new ArticleSnapshot(1, Guid.NewGuid(), "old", "Old", "Body", null, DateTime.UtcNow, DateTime.UtcNow, 0, [], null, []);
        var json = JsonSerializer.SerializeToNode(snapshot)!;
        Assert.True(json.AsObject().Remove("Location"));
        Assert.Null(JsonSerializer.Deserialize<ArticleSnapshot>(json.ToJsonString())!.Location);
    }
}
