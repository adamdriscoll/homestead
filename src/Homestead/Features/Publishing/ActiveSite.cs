using System.Text.Json;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Themes;
using Homestead.Infrastructure.Storage;
using Microsoft.AspNetCore.StaticFiles;

namespace Homestead.Features.Publishing;

public sealed record GenerationManifest(Guid GenerationId, DateTime ActivatedAtUtc, SiteSettings Settings,
    ArticleSnapshot[] Articles, string[] Files, ArticleMedia[]? Branding = null, ThemeSnapshot? Theme = null);

public sealed class ActiveSite(StoragePaths paths, ILogger<ActiveSite> logger)
{
    private GenerationManifest? current;
    private readonly object fileGate = new();
    public GenerationManifest? Current => Volatile.Read(ref current);
    public bool IsHealthy => Current is { } manifest && manifest.Files.All(x => File.Exists(Path.Combine(Root(manifest), x)));
    public string? RecoveryError { get; private set; }

    public void Recover()
    {
        if (!File.Exists(paths.CurrentPath)) return;
        try
        {
            var manifest = JsonSerializer.Deserialize<GenerationManifest>(File.ReadAllText(paths.CurrentPath))
                ?? throw new IOException("Empty active generation manifest.");
            if (manifest.GenerationId == Guid.Empty || manifest.Settings is null || manifest.Articles is null || manifest.Files is null ||
                manifest.Articles.Any(x => x is null) || !manifest.Files.Contains("index.html") ||
                manifest.Files.Any(x => x is null || !SafeRelativePath(x) || !File.Exists(Path.Combine(Root(manifest), x))))
                throw new IOException("The active generation is missing required files.");
            Activate(manifest);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { RecoveryError = "The active site is missing or damaged. Rebuild the site."; logger.LogError(ex, "Active generation recovery failed."); }
    }

    public string Root(GenerationManifest manifest) => Path.Combine(paths.BuildsPath, manifest.GenerationId.ToString("N"));
    public void Activate(GenerationManifest manifest)
    { lock (fileGate) { Volatile.Write(ref current, manifest); RecoveryError = null; } }
    public void RemoveBuild(Guid id)
    {
        lock (fileGate)
        {
            if (id == Current?.GenerationId) return;
            var root = Path.GetFullPath(Path.Combine(paths.BuildsPath, id.ToString("N")));
            if (Path.GetDirectoryName(root) != Path.GetFullPath(paths.BuildsPath)) throw new IOException("Invalid generation directory.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    private static bool SafeRelativePath(string path) => !Path.IsPathRooted(path) && !path.Contains('\\') &&
        path.Split('/').All(x => x.Length > 0 && x is not ("." or ".."));

    public async Task ServeAsync(HttpContext context)
    {
        var request = context.Request.Path.Value ?? "/";
        var relative = request.TrimStart('/');
        if (request.EndsWith('/')) relative += "index.html";
        else if (!Path.HasExtension(relative)) relative += "/index.html";
        try
        {
            FileStream stream; string? type;
            lock (fileGate)
            {
                var manifest = Current;
                if (manifest is null || !SafeRelativePath(relative) || !manifest.Files.Contains(relative, StringComparer.Ordinal))
                { context.Response.StatusCode = 404; return; }
                if (!request.EndsWith('/') && relative.EndsWith("/index.html") && !request.EndsWith("/index.html"))
                { context.Response.Redirect(request + "/", permanent: true); return; }
                var file = Path.Combine(Root(manifest), relative);
                if (!new FileExtensionContentTypeProvider().TryGetContentType(file, out type)) { context.Response.StatusCode = 404; return; }
                // Open under the same gate as cleanup; FileShare.Delete lets an in-flight response finish.
                stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            }
            await using var opened = stream;
            context.Response.ContentType = type; context.Response.ContentLength = stream.Length;
            if (relative == "rss.xml") context.Response.ContentType = "application/rss+xml; charset=utf-8";
            else if (relative == "sitemap.xml") context.Response.ContentType = "application/xml; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            if (!HttpMethods.IsHead(context.Request.Method)) await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (FileNotFoundException) { context.Response.StatusCode = 503; }
        catch (DirectoryNotFoundException) { context.Response.StatusCode = 503; }
    }
}
