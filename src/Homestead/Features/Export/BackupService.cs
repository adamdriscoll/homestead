using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Homestead.Data;
using Homestead.Features.Publishing;
using Homestead.Features.Themes;
using Homestead.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Features.Export;

public sealed record BackupFile(string Path, long Size, string Sha256);
public sealed record BackupManifest(int FormatVersion, string ApplicationVersion, DateTime CreatedAtUtc, BackupFile[] Files);

public sealed class BackupService(HomesteadDbContext db, StoragePaths paths, PublicationLock writer)
{
    public const long MaximumRestoreBytes = 20L * 1024 * 1024 * 1024;
    public static string ApplicationVersion => System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(BackupService).Assembly)!.InformationalVersion.Split('+')[0];

    public Task<FileStream> CreateAsync(CancellationToken ct = default) => CreateCoreAsync(false, ct);
    public Task<FileStream> CreateLockedAsync(CancellationToken ct = default) => CreateCoreAsync(true, ct);

    private Task<FileStream> CreateCoreAsync(bool lockHeld, CancellationToken ct) => ArchiveFiles.CreateAsync(paths, writer, async zip =>
    {
        var temporary = Path.Combine(paths.DataRoot, "backups", $"sqlite-{Guid.NewGuid():N}.db");
        try
        {
            await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
            {
                await source.OpenAsync(ct); await destination.OpenAsync(ct);
                // SQLite's backup API can return BUSY during a checkpoint, even with app writes serialized.
                for (var attempt = 0; ; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try { source.BackupDatabase(destination); break; }
                    catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < 7)
                    { await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct); }
                }
            }
            var files = new List<BackupFile>();
            await Add("homestead.db", temporary);
            foreach (var asset in await db.MediaAssets.AsNoTracking().ToArrayAsync(ct))
            {
                var relative = $"media/originals/{asset.Id:D}/{asset.StoredFilename}";
                await Add(relative, ArchiveFiles.SafePath(paths.DataRoot, relative));
                if (asset.RetainedOriginalFilename is { } retained)
                {
                    var retainedRelative = $"media/originals/{asset.Id:D}/{retained}";
                    await Add(retainedRelative, ArchiveFiles.SafePath(paths.DataRoot, retainedRelative));
                }
            }
            foreach (var key in Directory.GetFiles(paths.KeysPath, "*.xml"))
                await Add("keys/" + Path.GetFileName(key), key);
            foreach (var theme in Directory.GetFiles(paths.ThemesPath, "custom-*.zip"))
            {
                ThemeService.ValidateArchive(theme);
                await Add("themes/" + Path.GetFileName(theme), theme);
            }
            var manifest = JsonSerializer.Serialize(new BackupManifest(1, ApplicationVersion, DateTime.UtcNow, files.ToArray()),
                new JsonSerializerOptions { WriteIndented = true });
            if (System.Text.Encoding.UTF8.GetByteCount(manifest) > 4 * 1024 * 1024)
                throw new InvalidOperationException("Backup manifest exceeds the 4 MB restore limit.");
            await ArchiveFiles.TextAsync(zip, "manifest.json", manifest, ct);
            async Task Add(string relative, string source)
            {
                _ = ArchiveFiles.SafePath(paths.DataRoot, relative);
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                var size = input.Length;
                if (size + files.Sum(x => x.Size) > MaximumRestoreBytes)
                    throw new InvalidOperationException("Backup exceeds the 20 GB restore limit.");
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
                input.Position = 0;
                await using var output = zip.CreateEntry(relative, CompressionLevel.Optimal).Open();
                await input.CopyToAsync(output, ct);
                files.Add(new(relative, size, hash));
            }
        }
        finally { File.Delete(temporary); }
    }, ct, lockHeld);

    public static async Task RestoreAsync(string archivePath, string destination, CancellationToken ct = default)
    {
        var target = Path.GetFullPath(destination);
        if (File.Exists(target) || Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new InvalidOperationException("Restore requires an empty data directory. Stop Homestead and use a fresh volume.");
        var parent = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Choose a data directory below the filesystem root.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(target, $".homestead-restore-{Guid.NewGuid():N}");
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Backup manifest.json is missing.");
            if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("Backup manifest is too large.");
            BackupManifest manifest;
            try
            {
                await using var manifestStream = entry.Open();
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream, cancellationToken: ct)
                    ?? throw new InvalidDataException("Backup manifest is empty.");
            }
            catch (JsonException ex) { throw new InvalidDataException("Backup manifest is invalid JSON.", ex); }
            if (manifest.FormatVersion != 1 || manifest.ApplicationVersion != ApplicationVersion)
                throw new InvalidDataException($"Backup requires the same application version ({manifest.ApplicationVersion}); running {ApplicationVersion}. Format must be 1.");
            if (manifest.Files is null || !manifest.Files.Any(x => x?.Path == "homestead.db") || manifest.Files.Any(x => x is null || x.Path is null) ||
                manifest.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length ||
                zip.Entries.Count != manifest.Files.Length + 1 || zip.Entries.Select(x => x.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
                throw new InvalidDataException("Backup has missing, extra, or duplicate files.");
            if (manifest.Files.Any(x => x.Size < 0 || x.Size > MaximumRestoreBytes) || manifest.Files.Sum(x => (decimal)x.Size) > MaximumRestoreBytes)
                throw new InvalidDataException("Backup exceeds the 20 GB restore limit.");
            Directory.CreateDirectory(staging);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var file in manifest.Files)
            {
                var path = ArchiveFiles.SafePath(staging, file.Path);
                if (file.Path != "homestead.db" && !file.Path.StartsWith("media/originals/", StringComparison.Ordinal) && !file.Path.StartsWith("keys/", StringComparison.Ordinal) && !file.Path.StartsWith("themes/", StringComparison.Ordinal))
                    throw new InvalidDataException($"Unexpected backup file: {file.Path}");
                var source = zip.GetEntry(file.Path) ?? throw new InvalidDataException($"Backup file missing: {file.Path}");
                if (source.Length != file.Size) throw new InvalidDataException($"Backup size mismatch: {file.Path}");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var input = source.Open())
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920]; long written = 0; int count;
                    while ((count = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        written += count;
                        if (written > file.Size) throw new InvalidDataException($"Backup size mismatch: {file.Path}");
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    if (written != file.Size) throw new InvalidDataException($"Backup file incomplete: {file.Path}");
                }
                await using var check = File.OpenRead(path);
                if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(check, ct)), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Backup checksum mismatch: {file.Path}");
            }
            await ValidateDatabaseAsync(staging, manifest, ct);
            foreach (var theme in manifest.Files.Where(x => x.Path.StartsWith("themes/", StringComparison.Ordinal)))
            {
                if (theme.Path.Split('/').Length != 2 || !theme.Path.EndsWith(".zip", StringComparison.Ordinal)) throw new InvalidDataException("Invalid backup theme path.");
                ThemeService.ValidateArchive(ArchiveFiles.SafePath(staging, theme.Path));
            }
            // The archive is fully validated before any authoritative files are installed.
            // A pre-created empty Docker mount cannot itself be renamed, so move its children.
            Directory.CreateDirectory(target);
            if (Directory.EnumerateFileSystemEntries(target).Any(path => path != staging)) throw new InvalidOperationException("Restore destination is no longer empty.");
            var installed = new List<string>();
            try
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(staging))
                {
                    var output = Path.Combine(target, Path.GetFileName(child));
                    if (Directory.Exists(child)) Directory.Move(child, output); else File.Move(child, output);
                    installed.Add(output);
                }
                if (!OperatingSystem.IsWindows())
                {
                    foreach (var directory in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).Prepend(target))
                        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch
            {
                foreach (var output in installed)
                    if (Directory.Exists(output)) Directory.Delete(output, true); else File.Delete(output);
                throw;
            }
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static async Task ValidateDatabaseAsync(string staging, BackupManifest manifest, CancellationToken ct)
    {
        try
        {
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(staging, "homestead.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await db.OpenAsync(ct);
            using var command = db.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
            if ((string?)await command.ExecuteScalarAsync(ct) != "ok") throw new InvalidDataException("Backup SQLite integrity check failed.");
            command.CommandText = "PRAGMA foreign_key_check";
            await using (var violations = await command.ExecuteReaderAsync(ct))
                if (await violations.ReadAsync(ct)) throw new InvalidDataException("Backup SQLite foreign key check failed.");
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('MediaAssets') WHERE name = 'RetainedOriginalFilename'";
            var hasRetainedOriginals = (long)(await command.ExecuteScalarAsync(ct))! > 0;
            command.CommandText = hasRetainedOriginals
                ? "SELECT Id, StoredFilename, Size, RetainedOriginalFilename, RetainedOriginalSize FROM MediaAssets"
                : "SELECT Id, StoredFilename, Size, NULL, NULL FROM MediaAssets";
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var id = Guid.Parse(reader.GetString(0));
                    var relative = $"media/originals/{id:D}/{reader.GetString(1)}";
                    _ = ArchiveFiles.SafePath(staging, relative);
                    if (!manifest.Files.Any(x => x.Path == relative && x.Size == reader.GetInt64(2)))
                        throw new InvalidDataException($"Backup original media is missing or incomplete: {id}");
                    if (!reader.IsDBNull(3))
                    {
                        var retainedRelative = $"media/originals/{id:D}/{reader.GetString(3)}";
                        _ = ArchiveFiles.SafePath(staging, retainedRelative);
                        if (reader.IsDBNull(4) || !manifest.Files.Any(x => x.Path == retainedRelative && x.Size == reader.GetInt64(4)))
                            throw new InvalidDataException($"Backup retained original is missing or incomplete: {id}");
                    }
                }
            }
            // Backups from earlier versions have no theme column and default to Porch after migration.
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('SiteSettings') WHERE name = 'ThemeId'";
            if ((long)(await command.ExecuteScalarAsync(ct))! > 0)
            {
                command.CommandText = "SELECT ThemeId FROM SiteSettings";
                var themeId = (string?)await command.ExecuteScalarAsync(ct);
                if (themeId is not null && !ThemeService.BuiltInIds.Contains(themeId) && !manifest.Files.Any(x => x.Path == $"themes/{themeId}.zip"))
                    throw new InvalidDataException("Backup is missing the selected custom theme.");
            }
        }
        catch (SqliteException ex) { throw new InvalidDataException("Backup database is invalid or has an unsupported schema.", ex); }
    }
}
