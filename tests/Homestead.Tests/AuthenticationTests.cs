using System.Net;
using Homestead.Data.Entities;
using Homestead.Features.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task Authentication_routes_are_rate_limited()
    {
        using var data = new TemporaryData();
        using var app = new TestApplication(data.Path);
        using var client = app.Client();
        for (var attempt = 0; attempt < 20; attempt++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/setup")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/setup")).StatusCode);
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/settings")]
    [InlineData("/admin/media/private.jpg")]
    public async Task Anonymous_authoring_requests_require_login(string route)
    {
        using var data = new TemporaryData();
        using var app = new TestApplication(data.Path);
        using var client = app.Client();
        var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://localhost/account/login", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/homestead.db")]
    [InlineData("/data/homestead.db")]
    [InlineData("/media/originals/private.jpg")]
    [InlineData("/keys/private.xml")]
    [InlineData("/backups/private.zip")]
    [InlineData("/account/register")]
    [InlineData("/Identity/Account/Register")]
    [InlineData("/posts/nonexistent/")]
    public async Task Private_storage_and_registration_have_no_public_route(string route)
    {
        using var data = new TemporaryData();
        using var app = new TestApplication(data.Path);
        using var client = app.Client();
        _ = await client.GetAsync("/health/live");
        Directory.CreateDirectory(System.IO.Path.Combine(data.Path, "media", "originals"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(data.Path, "media", "originals", "private.jpg"), "PRIVATE");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-secret")]
    public async Task Setup_requires_the_server_secret(string? token)
    {
        using var data = new TemporaryData();
        using var app = new TestApplication(data.Path);
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.Forbidden, (await TestApplication.Setup(client, token)).StatusCode);
        await app.WithDatabase(async db => Assert.Empty(await db.Users.ToListAsync()));
    }

    [Fact]
    public async Task Setup_closes_and_logout_requires_post_and_antiforgery()
    {
        using var data = new TemporaryData();
        using var app = new TestApplication(data.Path);
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/setup", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Setup(client)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/setup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TestApplication.Submit(client, "/account/login", [], "/setup")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/account/login", new FormUrlEncodedContent([]))).StatusCode);
        var login = await TestApplication.Login(client, returnUrl: "https://attacker.test");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/admin", login.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/account/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/account/logout", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin", [], "/account/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_setup_creates_exactly_one_account()
    {
        using var data = new TemporaryData();
        using var app = new TestApplication(data.Path);
        async Task<bool> Create(string email)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<SetupService>()
                .CreateAdminAsync(TestApplication.SetupToken, email, TestApplication.Password)).Succeeded;
        }
        var results = await Task.WhenAll(Create("first@example.test"), Create("second@example.test"));
        Assert.Single(results, x => x);
        await app.WithDatabase(async db =>
        {
            Assert.Single(await db.Users.ToListAsync());
            db.Users.Add(new ApplicationUser { UserName = "third", AdminSlot = 1 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Identity_locks_out_after_repeated_bad_passwords()
    {
        using var data = new TemporaryData();
        using var app = new TestApplication(data.Path);
        using var client = app.Client();
        _ = await TestApplication.Setup(client);
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.OK, (await TestApplication.Login(client, "Incorrect-password-123!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TestApplication.Login(client)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);
        await app.WithDatabase(async db => Assert.True((await db.Users.SingleAsync()).LockoutEnd > DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Production_uses_secure_cookies_and_does_not_trust_arbitrary_forwarders()
    {
        using var data = new TemporaryData();
        using var app = new TestApplication(data.Path, "Production");
        using var client = app.Client();
        _ = await TestApplication.Setup(client);
        var response = await TestApplication.Login(client);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), x => x.StartsWith("Homestead.Auth="));
        Assert.Contains("secure", cookie); Assert.Contains("httponly", cookie); Assert.Contains("samesite=lax", cookie);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/admin");
        request.Headers.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("http://localhost/health/live")).StatusCode);
    }
}
