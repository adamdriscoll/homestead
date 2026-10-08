using System.Net;
using Homestead.Features.Settings;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Tests;

public sealed class SettingsTests
{
    [Theory]
    [InlineData("general", "general")]
    [InlineData("theme", "theme")]
    [InlineData("about", "about")]
    [InlineData("unknown", "general")]
    public async Task Saving_all_settings_returns_to_the_selected_section(string section, string expected)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var fields = Fields(); fields["Section"] = section;
        var response = await TestApplication.Submit(client, "/admin/settings", fields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/admin/settings#{expected}", response.Headers.Location!.ToString());
        await app.WithDatabase(async db =>
        {
            var settings = await db.SiteSettings.SingleAsync();
            Assert.Equal("My home", settings.Title); Assert.Equal("Forest", settings.AccentColor); Assert.Equal("# About me", settings.AboutMarkdown);
        });
    }

    [Theory]
    [InlineData("Theme", "theme")]
    [InlineData("Upload", "images")]
    public async Task Upload_errors_return_to_the_upload_section(string handler, string section)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var response = await TestApplication.Submit(client, "/admin/settings", new(), $"/admin/settings?handler={handler}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/admin/settings#{section}", response.Headers.Location!.ToString());
        Assert.Contains(handler == "Theme" ? "Choose a theme ZIP first." : "Choose an image first.", await client.GetStringAsync(response.Headers.Location));
    }

    [Fact]
    public async Task Optional_settings_can_be_cleared_and_required_fields_report_validation_errors()
    {
        using var data = new TemporaryData();
        using var app = new TestApplication(data.Path, "Production");
        using var client = app.Client();
        _ = await TestApplication.Setup(client);
        _ = await TestApplication.Login(client);
        var fields = Fields();
        foreach (var key in new[] { "Input.Description", "Input.AuthorBio", "Input.AboutMarkdown", "Input.FooterText" }) fields[key] = "";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", fields)).StatusCode);
        fields["Input.CanonicalBaseUrl"] = "";
        Assert.Equal(HttpStatusCode.OK, (await TestApplication.Submit(client, "/admin/settings", fields)).StatusCode);
    }

    private static Dictionary<string, string> Fields(string url = "https://new.example.test") => new()
    {
        ["Input.Title"] = "My home",
        ["Input.CanonicalBaseUrl"] = url,
        ["Input.DisplayAuthor"] = "Ada",
        ["Input.Description"] = "Life and stories",
        ["Input.TimeZone"] = "America/Chicago",
        ["Input.AboutMarkdown"] = "# About me",
        ["Input.AuthorBio"] = "Author",
        ["Input.FooterText"] = "Welcome",
        ["Input.AccentColor"] = "Forest"
    };

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//attacker.test")]
    [InlineData("https://user:pass@example.test")]
    [InlineData("https://example.test/path")]
    [InlineData("https://example.test/?x=1")]
    [InlineData("https://example.test/#fragment")]
    public async Task Unsafe_or_unsupported_canonical_URLs_are_rejected(string url)
    {
        Assert.False(SettingsInput.IsCanonicalUrl(url));
        using var data = new TemporaryData();
        using var app = new TestApplication(data.Path);
        using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var response = await TestApplication.Submit(client, "/admin/settings", Fields(url));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Use an absolute HTTP(S)", await response.Content.ReadAsStringAsync());
        await app.WithDatabase(async db => Assert.Equal("https://example.test", (await db.SiteSettings.SingleAsync()).CanonicalBaseUrl));
    }

    [Theory]
    [InlineData("Input.AccentColor", "arbitrary-css")]
    [InlineData("Input.TimeZone", "Not/AZone")]
    public async Task Invalid_presets_and_time_zones_are_rejected(string key, string value)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var fields = Fields(); fields[key] = value;
        Assert.Equal(HttpStatusCode.OK, (await TestApplication.Submit(client, "/admin/settings", fields)).StatusCode);
        await app.WithDatabase(async db => Assert.Equal("Homestead", (await db.SiteSettings.SingleAsync()).Title));
    }

    [Fact]
    public async Task Settings_account_and_session_keys_survive_restart_without_the_setup_secret()
    {
        using var data = new TemporaryData();
        string authCookie;
        string[] keys;
        using (var app = new TestApplication(data.Path))
        using (var client = app.Client())
        {
            _ = await TestApplication.Setup(client);
            var login = await TestApplication.Login(client);
            authCookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("Homestead.Auth=")).Split(';')[0];
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/admin/settings", new FormUrlEncodedContent(Fields()))).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", Fields())).StatusCode);
            keys = Directory.GetFiles(System.IO.Path.Combine(data.Path, "keys")); Assert.NotEmpty(keys);
        }
        using var restarted = new TestApplication(data.Path, setupToken: null);
        using var newClient = restarted.Client(handleCookies: false);
        newClient.DefaultRequestHeaders.Add("Cookie", authCookie);
        Assert.Equal(HttpStatusCode.OK, (await newClient.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await newClient.GetAsync("/setup")).StatusCode);
        await restarted.WithDatabase(async db =>
        {
            Assert.Single(await db.Users.ToListAsync());
            var settings = await db.SiteSettings.SingleAsync();
            Assert.Equal("My home", settings.Title); Assert.Equal("https://new.example.test", settings.CanonicalBaseUrl);
            Assert.Equal("America/Chicago", settings.TimeZone); Assert.Equal("# About me", settings.AboutMarkdown);
            Assert.Equal("Forest", settings.AccentColor); Assert.Equal("Ada", settings.DisplayAuthor);
        });
        Assert.Equal(keys, Directory.GetFiles(System.IO.Path.Combine(data.Path, "keys")));
    }
}
