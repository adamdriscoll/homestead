using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using Homestead.Features.Export;
using Homestead.Features.Publishing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class AboutImageTests
{
    [Fact]
    public async Task Featured_image_replaces_avatar_on_about_in_every_theme_and_is_portable()
    {
        using var data = new TemporaryData();
        using var app = new TestApplication(data.Path);
        using var author = app.Client();
        using var visitor = app.Client();
        _ = await TestApplication.Setup(author);
        _ = await TestApplication.Login(author);
        var active = app.Services.GetRequiredService<ActiveSite>();

        using var avatarUpload = await Upload(author, "Avatar");
        Assert.Equal(HttpStatusCode.Redirect, avatarUpload.StatusCode);
        var avatar = Assert.Single(active.Current!.Branding!);
        var originalAbout = await visitor.GetStringAsync("/about/");
        Assert.DoesNotContain(avatar.Url, MainContent(originalAbout));
        Assert.DoesNotContain("data-about-image", MainContent(originalAbout));

        using var upload = await Upload(author, "AboutImage", "Trees & hills");
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);
        Assert.EndsWith("#about", upload.Headers.Location?.OriginalString);
        var first = active.Current!.Branding!.Single(x => x.Id == active.Current.Settings.AboutImageMediaId);
        Assert.Contains(first.PrivateUrl, await author.GetStringAsync("/admin/settings"));
        using var privateResponse = await visitor.GetAsync(first.PrivateUrl);
        Assert.Equal(HttpStatusCode.Redirect, privateResponse.StatusCode);
        Assert.Equal(Photo(), await visitor.GetByteArrayAsync(first.Url));

        await using var scope = app.Services.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<PublishingService>();
        foreach (var theme in new[] { "porch", "journal", "ledger", "folio", "townsquare" })
        {
            await app.WithDatabase(async db =>
            {
                (await db.SiteSettings.SingleAsync()).ThemeId = theme;
                await db.SaveChangesAsync();
            });
            Assert.True((await publisher.RebuildAsync()).Succeeded);
            var about = await visitor.GetStringAsync("/about/");
            var main = MainContent(about);
            Assert.Matches($"""<img data-about-image src="{Regex.Escape(first.Url)}" alt="Trees &amp; hills"\s*/?>\s*<h1""", main);
            Assert.DoesNotContain(avatar.Url, main);
            Assert.Contains($"property=\"og:image\" content=\"https://example.test{first.Url}\"", about);
            var home = await visitor.GetStringAsync("/");
            Assert.DoesNotContain(first.Url, home);
            if (theme == "townsquare") Assert.Contains(avatar.Url, home);
        }

        await using (var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync())
        using (var zip = new ZipArchive(archive))
        {
            Assert.NotNull(zip.GetEntry(first.Url.TrimStart('/')));
            using var about = new StreamReader(zip.GetEntry("about/index.html")!.Open());
            Assert.Contains(first.Url, await about.ReadToEndAsync());
        }
        await using (var archive = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync())
        using (var zip = new ZipArchive(archive))
        {
            Assert.NotNull(zip.GetEntry("content/about/" + first.Filename));
            using var about = new StreamReader(zip.GetEntry("content/about/index.md")!.Open());
            Assert.Contains($"\"featured_image\":\"{first.Filename}\"", await about.ReadToEndAsync());
        }

        using var replacement = await Upload(author, "AboutImage");
        Assert.Equal(HttpStatusCode.Redirect, replacement.StatusCode);
        var second = active.Current!.Branding!.Single(x => x.Id == active.Current.Settings.AboutImageMediaId);
        Assert.NotEqual(first.Id, second.Id);
        using var oldImage = await visitor.GetAsync(first.Url);
        Assert.Equal(HttpStatusCode.NotFound, oldImage.StatusCode);

        using var removal = await TestApplication.Submit(author, "/admin/settings", new() { ["Slot"] = "AboutImage" },
            "/admin/settings?handler=Remove");
        Assert.Equal(HttpStatusCode.Redirect, removal.StatusCode);
        Assert.EndsWith("#about", removal.Headers.Location?.OriginalString);
        Assert.DoesNotContain("data-about-image", MainContent(await visitor.GetStringAsync("/about/")));
        using var removedImage = await visitor.GetAsync(second.Url);
        Assert.Equal(HttpStatusCode.NotFound, removedImage.StatusCode);
    }

    private static string MainContent(string html) => Regex.Match(html, "<main\\b.*?</main>", RegexOptions.Singleline).Value;

    private static byte[] Photo() => TestImages.Create(96, 48);

    private static async Task<HttpResponseMessage> Upload(HttpClient author, string slot, string alt = "")
    {
        var html = await author.GetStringAsync("/admin/settings");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(WebUtility.HtmlDecode(token)), "__RequestVerificationToken");
        form.Add(new StringContent(slot), "Slot");
        form.Add(new StringContent(alt), "AboutImageAltText");
        var photo = new ByteArrayContent(Photo());
        photo.Headers.ContentType = new("image/png");
        form.Add(photo, "Upload", "landscape.png");
        return await author.PostAsync("/admin/settings?handler=Upload", form);
    }
}
