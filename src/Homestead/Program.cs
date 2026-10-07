using System.Net;
using System.Threading.RateLimiting;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Authentication;
using Homestead.Infrastructure.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
// Never enable the framework's blanket proxy trust. Only honor the explicit allowlist below.
builder.Configuration["ForwardedHeaders_Enabled"] = "false";
builder.Services.AddSingleton<StoragePaths>();
builder.Services.AddDbContext<HomesteadDbContext>((services, options) => options.UseSqlite(new SqliteConnectionStringBuilder
{
    DataSource = services.GetRequiredService<StoragePaths>().DatabasePath,
    ForeignKeys = true
}.ToString()));
builder.Services.AddDataProtection().SetApplicationName("Homestead");
builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
    .Configure<StoragePaths>((options, paths) => options.XmlRepository =
        new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(new DirectoryInfo(paths.KeysPath),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.User.RequireUniqueEmail = true; options.Password.RequiredLength = 12;
    options.Lockout.MaxFailedAccessAttempts = 5; options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
}).AddEntityFrameworkStores<HomesteadDbContext>().AddSignInManager();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/account/login"; options.AccessDeniedPath = "/account/login";
    options.Cookie.Name = "Homestead.Auth"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromDays(14); options.SlidingExpiration = true;
});
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy =
    builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always);
builder.Services.AddAuthorization();
builder.Services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/Admin"));
builder.Services.AddSingleton<SetupLock>();
builder.Services.AddScoped<SetupService>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto; options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear(); options.KnownProxies.Clear();
    foreach (var proxy in (builder.Configuration["HOMESTEAD_TRUSTED_PROXIES"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    // An empty allowlist must never mean trust every address.
    if (options.KnownProxies.Count == 0) options.KnownProxies.Add(IPAddress.Loopback);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("authentication", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
var paths = app.Services.GetRequiredService<StoragePaths>();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HomesteadDbContext>();
    app.Logger.LogInformation("Applying SQLite migrations for this single instance.");
    await db.Database.MigrateAsync();
    if (!await db.SiteSettings.AnyAsync())
    {
        db.SiteSettings.Add(new SiteSettings { CanonicalBaseUrl = paths.InitialBaseUrl.TrimEnd('/') });
        await db.SaveChangesAsync();
    }
    if (!await db.Users.AnyAsync())
    {
        if (app.Configuration["HOMESTEAD_SETUP_TOKEN"] is not { Length: >= 32 and <= 1024 })
            throw new InvalidOperationException("Initial setup requires HOMESTEAD_SETUP_TOKEN (32–1024 characters). Remove it after setup.");
        app.Logger.LogInformation("First-run setup is available at /setup; a bootstrap secret is required.");
    }
    else app.Logger.LogInformation("Administrator exists; setup is closed.");
}
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error"); app.UseHsts();
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps && !context.Request.Path.StartsWithSegments("/health"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("HTTPS is required. Configure the trusted reverse proxy."); return;
        }
        await next(context);
    });
}
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/admin") || context.Request.Path.StartsWithSegments("/account") || context.Request.Path.StartsWithSegments("/setup"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseStaticFiles(); // Only compiled admin assets in wwwroot; /data is never registered.
app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.MapRazorPages();
app.MapGet("/health/live", () => Results.Text("Healthy"));
app.MapGet("/health/ready", async (HomesteadDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Text("Ready") : Results.StatusCode(503));
app.MapGet("/admin/media/{**path}", () => Results.NotFound()).RequireAuthorization();
app.MapGet("/", () => Results.Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Homestead</title><link rel=\"stylesheet\" href=\"/css/site.css\"></head><body><main class=\"max-w-xl mx-auto p-8 space-y-6\"><h1 class=\"text-3xl font-semibold\">Welcome home.</h1><p>Your Homestead foundation is ready. Publishing arrives in Phase 2.</p><a class=\"btn\" href=\"/account/login\">Administrator sign in</a><a class=\"btn\" href=\"/setup\">First-run setup</a></main></body></html>", "text/html"));
await app.RunAsync();

public partial class Program;
