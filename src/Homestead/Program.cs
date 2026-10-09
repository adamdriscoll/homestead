using System.Net;
using System.Threading.RateLimiting;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Authentication;
using Homestead.Features.Export;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Homestead.Features.Themes;
using Homestead.Infrastructure.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

if (args.FirstOrDefault() == "restore")
{
    try
    {
        if (args.Length != 3) throw new InvalidOperationException("Usage: dotnet Homestead.dll restore backup.zip /empty/data/path");
        await BackupService.RestoreAsync(args[1], args[2]);
        Console.WriteLine("Backup restored and verified. Start the same application version to rebuild the public site.");
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException or FormatException)
    { Console.Error.WriteLine($"Restore failed: {ex.Message}"); Environment.ExitCode = 1; }
    return;
}
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
builder.Services.AddSingleton<PublicationLock>();
builder.Services.AddSingleton<ActiveSite>();
builder.Services.AddSingleton<IGenerationActivation, GenerationActivation>();
builder.Services.AddSingleton<SafeMarkdown>();
builder.Services.AddScoped<ArticleService>();
builder.Services.AddScoped<MediaService>();
builder.Services.AddScoped<PublishingService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddSingleton<RazorThemeCompiler>();
builder.Services.AddScoped<StaticExportService>();
builder.Services.AddScoped<HugoExportService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<IStaticPageRenderer, StaticPageRenderer>();
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
    var active = scope.ServiceProvider.GetRequiredService<ActiveSite>();
    active.Recover();
    foreach (var build in await db.SiteGenerations.Where(x => x.Status == GenerationStatus.Building).ToListAsync())
    {
        build.Status = build.Id == active.Current?.GenerationId ? GenerationStatus.Succeeded : GenerationStatus.Failed;
        build.ActivatedAtUtc = build.Id == active.Current?.GenerationId ? active.Current.ActivatedAtUtc : null;
        build.Error = build.Status == GenerationStatus.Failed ? "Build interrupted. Rebuild to apply pending changes." : null;
    }
    await db.SaveChangesAsync();
    if (await db.Users.AnyAsync() && !File.Exists(paths.CurrentPath))
        await scope.ServiceProvider.GetRequiredService<PublishingService>().RebuildAsync();
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
    var locationMap = context.Request.Path.Equals("/map", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.Equals("/map/", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.Equals("/map/index.html", StringComparison.OrdinalIgnoreCase);
    context.Response.Headers["Referrer-Policy"] = locationMap ? "strict-origin-when-cross-origin" : "same-origin";
    var preview = context.Request.Path.StartsWithSegments("/admin/posts") && context.Request.Path.Value!.EndsWith("/preview", StringComparison.OrdinalIgnoreCase);
    var frameAncestors = preview ? "'self'" : "'none'";
    var scripts = preview ? "'none'" : "'self'";
    var mapImages = locationMap ? " https://tile.openstreetmap.org" : "";
    context.Response.Headers["Content-Security-Policy"] = $"default-src 'self'; script-src {scripts}; style-src 'self'; img-src 'self' data:{mapImages}; object-src 'none'; base-uri 'self'; frame-ancestors {frameAncestors}; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/admin") || context.Request.Path.StartsWithSegments("/account") || context.Request.Path.StartsWithSegments("/setup"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseStaticFiles(); // Only compiled admin assets in wwwroot; /data is never registered.
app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.MapRazorPages();
app.MapGet("/admin/themes/colors.css", async (HomesteadDbContext db, ThemeService themes) =>
{
    var settings = await db.SiteSettings.AsNoTracking().SingleAsync();
    return Results.Text(ThemeService.ColorStyles(settings, themes.Resolve(settings.ThemeId)), "text/css");
}).RequireAuthorization();
app.MapGet("/admin/themes/{id}/{**file}", (string id, string file, ThemeService themes) =>
{
    if (!ThemePackage.IsAssetPath(file) || ThemePackage.IsSource(file) || file.EndsWith(".js", StringComparison.Ordinal))
        return Results.NotFound();
    try
    {
        var theme = themes.Resolve(id);
        if (!theme.Files.TryGetValue(file, out var bytes) || !new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider().TryGetContentType(file, out var type))
            return Results.NotFound();
        return Results.Bytes(bytes, type);
    }
    catch (InvalidDataException) { return Results.NotFound(); }
}).RequireAuthorization();
app.MapGet("/health/live", () => Results.Text("Healthy"));
app.MapGet("/health/ready", async (HomesteadDbContext db, ActiveSite active, CancellationToken ct) =>
    active.RecoveryError is null && await db.Database.CanConnectAsync(ct) && (active.IsHealthy || !await db.Users.AnyAsync(ct))
        ? Results.Text("Ready") : Results.StatusCode(503));
app.MapGet("/admin/media/{id:guid}", async (Guid id, HomesteadDbContext db, StoragePaths storage) =>
{
    var asset = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    if (asset is null) return Results.NotFound();
    var file = Path.Combine(storage.OriginalsPath, asset.Id.ToString("D"), asset.StoredFilename);
    return File.Exists(file) ? Results.File(file, asset.MimeType) : Results.NotFound();
}).RequireAuthorization();
app.MapGet("/admin/media/{**path}", () => Results.NotFound()).RequireAuthorization();
app.MapMethods("/{**path}", ["GET", "HEAD"], (HttpContext context, ActiveSite active) => active.ServeAsync(context));
await app.RunAsync();

public partial class Program;
