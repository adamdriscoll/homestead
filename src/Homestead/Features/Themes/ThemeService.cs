using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Homestead.Data.Entities;
using Homestead.Features.Publishing;
using Homestead.Infrastructure.Storage;

namespace Homestead.Features.Themes;

public sealed class ThemeService(StoragePaths paths, IWebHostEnvironment environment, PublicationLock writer)
{
    public const int MaximumUploadBytes = 5 * 1024 * 1024;
    public const int MaximumExpandedBytes = 20 * 1024 * 1024;
    public static IReadOnlyList<string> BuiltInIds { get; } = ["porch", "journal", "ledger"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] PostTypes = ["microblog", "article", "review", "guide", "gallery"];

    public ThemePackage Resolve(string id)
    {
        if (BuiltInIds.Contains(id))
        {
            var root = Path.Combine(environment.ContentRootPath, "Themes", id);
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
                file => Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllBytes);
            return Validate(id, files);
        }
        if (string.IsNullOrEmpty(id) || !IsCustomId(id)) throw new InvalidDataException("Choose an installed public theme.");
        var path = Path.Combine(paths.ThemesPath, id + ".zip");
        if (!File.Exists(path)) throw new InvalidDataException("The selected public theme is missing. Choose an installed theme.");
        using var stream = File.OpenRead(path);
        return Read(id, stream);
    }

    public IReadOnlyList<ThemeSnapshot> Installed()
    {
        var result = BuiltInIds.Select(id => Resolve(id).Snapshot).ToList();
        foreach (var file in Directory.GetFiles(paths.ThemesPath, "custom-*.zip").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            try { result.Add(Resolve(id).Snapshot); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { /* Damaged packages cannot be selected. */ }
        }
        return result;
    }

    public async Task<ThemeSnapshot> InstallAsync(IFormFile upload, CancellationToken ct = default)
    {
        if (!upload.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || upload.Length is <= 0 or > MaximumUploadBytes)
            throw new InvalidDataException("Choose a theme ZIP up to 5 MB.");
        using var buffer = new MemoryStream();
        await using (var input = upload.OpenReadStream()) await CopyBoundedAsync(input, buffer, MaximumUploadBytes, ct);
        var id = "custom-" + Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
        buffer.Position = 0;
        var package = Read(id, buffer);
        await writer.Semaphore.WaitAsync(ct);
        var temporary = Path.Combine(paths.ThemesPath, $"upload-{Guid.NewGuid():N}.tmp");
        try
        {
            var destination = Path.Combine(paths.ThemesPath, id + ".zip");
            if (!File.Exists(destination))
            {
                await File.WriteAllBytesAsync(temporary, buffer.ToArray(), ct);
                File.Move(temporary, destination);
            }
            return package.Snapshot;
        }
        finally { File.Delete(temporary); writer.Semaphore.Release(); }
    }

    private static bool IsCustomId(string id) => id.Length == 71 && id.StartsWith("custom-", StringComparison.Ordinal) &&
        id[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void ValidateArchive(string file)
    {
        var id = Path.GetFileNameWithoutExtension(file);
        using var stream = File.OpenRead(file);
        if (!IsCustomId(id) || stream.Length > MaximumUploadBytes || id != "custom-" + Convert.ToHexStringLower(SHA256.HashData(stream)))
            throw new InvalidDataException("Invalid installed theme identity.");
        stream.Position = 0;
        _ = Read(id, stream);
    }

    private static ThemePackage Read(string id, Stream input)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count is < 2 or > 128 || zip.Entries.Sum(e => (decimal)e.Length) > MaximumExpandedBytes)
            throw new InvalidDataException("Theme ZIP exceeds the 128 file or 20 MB expanded limit.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var file = entry.FullName;
            if (!entries.Add(file)) throw new InvalidDataException("Theme ZIP contains duplicate paths.");
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("Theme ZIP cannot contain symbolic links.");
            if (file.EndsWith("/", StringComparison.Ordinal))
            {
                if (!ThemePackage.IsAssetPath(file[..^1]) || entry.Length != 0)
                    throw new InvalidDataException("Theme ZIP contains an unsafe directory.");
                continue;
            }
            if (!ThemePackage.IsAssetPath(file) ||
                file != "theme.json" && Path.GetExtension(file) is not (".css" or ".js" or ".html" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".woff" or ".woff2"))
                throw new InvalidDataException("Theme ZIP contains an unsafe path or unsupported file.");
            var limit = file == "theme.json" || file.EndsWith(".html", StringComparison.Ordinal) ? 64 * 1024 : MaximumExpandedBytes;
            if (entry.Length > limit || files.ContainsKey(file)) throw new InvalidDataException("Theme ZIP contains an oversized or duplicate file.");
            using var stream = entry.Open(); using var output = new MemoryStream();
            var bytes = new byte[81920]; int count;
            while ((count = stream.Read(bytes)) > 0)
            {
                if (output.Length + count > entry.Length || output.Length + count > limit) throw new InvalidDataException("Theme ZIP size mismatch.");
                output.Write(bytes, 0, count);
            }
            if (output.Length != entry.Length) throw new InvalidDataException("Theme ZIP file is incomplete.");
            files.Add(file, output.ToArray());
        }
        // Preserve exact path casing so URLs and exported files agree on Linux.
        return Validate(id, files.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
    }

    private static ThemePackage Validate(string id, Dictionary<string, byte[]> files)
    {
        if (!files.TryGetValue("theme.json", out var json)) throw new InvalidDataException("Theme ZIP requires theme.json at its root.");
        ThemeManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ThemeManifest>(json, JsonOptions) ?? throw new InvalidDataException("Empty theme manifest."); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid theme manifest JSON.", ex); }
        if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 80 ||
            string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 40 || manifest.Variants is not { Length: > 0 and <= 16 } ||
            manifest.Variants.Any(v => v is null) || manifest.Variants.Select(v => v.EngineVersion).Distinct().Count() != manifest.Variants.Length)
            throw new InvalidDataException("Theme manifest requires formatVersion 1, name, version, and distinct engine variants.");
        var variant = manifest.Variants.SingleOrDefault(v => v.EngineVersion == ThemePackage.EngineVersion)
            ?? throw new InvalidDataException($"Theme does not support engine version {ThemePackage.EngineVersion}.");
        if (variant.PostTypes is null || !PostTypes.All(type => variant.PostTypes.Contains(type, StringComparer.Ordinal)) ||
            variant.Styles is not { Length: > 0 and <= 16 } || variant.Scripts is not { Length: <= 16 })
            throw new InvalidDataException("Theme must declare styles, scripts, and support for all five post types.");
        foreach (var (items, extension) in new[] { (variant.Styles, ".css"), (variant.Scripts, ".js") })
            if (items.Any(file => file is null || !ThemePackage.IsAssetPath(file) || !file.EndsWith(extension, StringComparison.Ordinal) || !files.ContainsKey(file)) ||
                items.Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
                throw new InvalidDataException("Theme references a missing, duplicate, or invalid stylesheet or script.");
        if (variant.Layout is { } layout)
        {
            if (!ThemePackage.IsAssetPath(layout) || !layout.EndsWith(".html", StringComparison.Ordinal) || !files.TryGetValue(layout, out var contents))
                throw new InvalidDataException("Theme layout file is missing or invalid.");
            ThemePackage.ValidateLayout(Encoding.UTF8.GetString(contents));
        }
        return new(id, manifest, variant, files);
    }

    public static string ColorStyles(SiteSettings settings, ThemePackage theme)
    {
        if (!theme.Variant.SupportsColors) return "";
        var rules = new StringBuilder();
        foreach (var (value, variable) in new[] { (settings.ThemePrimaryColor, "primary"),
            (settings.ThemeBackgroundColor, "base-100"), (settings.ThemeTextColor, "base-content") })
        {
            if (!ThemePackage.IsColor(value)) throw new InvalidDataException("Invalid public theme color.");
            if (string.IsNullOrEmpty(value)) continue;
            rules.Append($"--color-{variable}:{value};");
            if (variable == "primary")
            {
                double Channel(int offset)
                {
                    var channel = Convert.ToInt32(value.Substring(offset, 2), 16) / 255d;
                    return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
                }
                var luminance = .2126 * Channel(1) + .7152 * Channel(3) + .0722 * Channel(5);
                rules.Append($"--color-primary-content:{(luminance > .179 ? "#000000" : "#ffffff")};");
            }
        }
        return $"html[data-public-theme]{{{rules}}}";
    }

    public static async Task CopyBoundedAsync(Stream input, Stream output, int maximum, CancellationToken ct)
    {
        var buffer = new byte[81920]; int count; long total = 0;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("Theme upload exceeds the size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }
}
