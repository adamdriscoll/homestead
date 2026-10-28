using System.Net;
using System.Text.RegularExpressions;
using Homestead.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Homestead.Tests;

public sealed class TemporaryData : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "homestead-tests", Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        // Other test classes may still be opening their own databases in parallel.
        // Release only this fixture's pools before deleting its temporary files.
        foreach (var file in Directory.EnumerateFiles(Path, "*.db", SearchOption.AllDirectories))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file }.ToString());
            SqliteConnection.ClearPool(connection);
            using var foreignKeys = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, ForeignKeys = true }.ToString());
            SqliteConnection.ClearPool(foreignKeys);
        }
        Directory.Delete(Path, recursive: true);
    }
}

public sealed class TestApplication(string dataPath, string environment = "Development", string? setupToken = TestApplication.SetupToken, Action<IServiceCollection>? configureServices = null, bool httpsAtEdge = false)
    : WebApplicationFactory<Program>
{
    public const string SetupToken = "test-only-bootstrap-secret-32-characters";
    public const string Password = "Test-only-password-123!";
    public const string Email = "owner@example.test";

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Apply before WebApplication.CreateBuilder loads JSON providers. Hundreds of
        // isolated test hosts otherwise exhaust Linux's per-user inotify limit.
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["hostBuilder:reloadConfigOnChange"] = "false"
        }));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, TestConnectionFilter>(); configureServices?.Invoke(services);
        });
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HOMESTEAD_DATA_PATH"] = dataPath,
            ["HOMESTEAD_BASE_URL"] = "https://example.test",
            ["HOMESTEAD_SETUP_TOKEN"] = setupToken,
            ["HOMESTEAD_TRUSTED_PROXIES"] = "192.0.2.10",
            ["HOMESTEAD_HTTPS_AT_EDGE"] = httpsAtEdge.ToString()
        }));
    }

    public HttpClient Client(bool handleCookies = true) => CreateClient(new WebApplicationFactoryClientOptions
    { AllowAutoRedirect = false, HandleCookies = handleCookies, BaseAddress = new Uri("https://localhost") });

    public async Task WithDatabase(Func<HomesteadDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<HomesteadDbContext>());
    }

    public static async Task<HttpResponseMessage> Submit(HttpClient client, string path, Dictionary<string, string> fields, string? postPath = null)
    {
        using var page = await client.GetAsync(path);
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "The form must include an antiforgery token.");
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value);
        return await client.PostAsync(postPath ?? path, new FormUrlEncodedContent(fields));
    }

    public static Task<HttpResponseMessage> Setup(HttpClient client, string? token = SetupToken) => Submit(client, "/setup", new()
    {
        ["Input.Token"] = token ?? "",
        ["Input.Email"] = Email,
        ["Input.Password"] = Password,
        ["Input.ConfirmPassword"] = Password
    });

    public static Task<HttpResponseMessage> Login(HttpClient client, string password = Password, string returnUrl = "/admin") => Submit(client, "/account/login", new()
    { ["Input.Email"] = Email, ["Input.Password"] = password, ["ReturnUrl"] = returnUrl });
}

public sealed class TestConnectionFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            // TestServer has no socket address by default. Supply an untrusted peer like real Kestrel.
            context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.40");
            await continuation(context);
        });
        next(app);
    };
}
