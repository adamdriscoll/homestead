using Homestead.Features.Settings;

namespace Homestead.Infrastructure.Storage;

public sealed class StoragePaths
{
    public string DataRoot { get; }
    public string DatabasePath => Path.Combine(DataRoot, "homestead.db");
    public string KeysPath => Path.Combine(DataRoot, "keys");
    public string InitialBaseUrl { get; }

    public StoragePaths(IConfiguration configuration, IWebHostEnvironment environment)
    {
        InitialBaseUrl = configuration["HOMESTEAD_BASE_URL"] ?? "";
        if (!SettingsInput.IsCanonicalUrl(InitialBaseUrl))
            throw new InvalidOperationException("HOMESTEAD_BASE_URL must be an absolute HTTP(S) URL at the site root without credentials, query, or fragment.");
        if (!environment.IsDevelopment() && !InitialBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HOMESTEAD_BASE_URL must use HTTPS in production.");
        DataRoot = Path.GetFullPath(configuration["HOMESTEAD_DATA_PATH"] ?? "/data");
        var webRoot = Path.GetFullPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
        if (DataRoot.Equals(webRoot, StringComparison.OrdinalIgnoreCase) || DataRoot.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The data directory must be outside wwwroot.");
        foreach (var path in new[] { DataRoot, KeysPath, Path.Combine(DataRoot, "media", "originals"), Path.Combine(DataRoot, "site", "builds"), Path.Combine(DataRoot, "backups") })
        {
            Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
