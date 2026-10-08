using System.IO.Compression;
using System.Text;
using Homestead.Features.Publishing;
using Homestead.Infrastructure.Storage;

namespace Homestead.Features.Export;

internal static class ArchiveFiles
{
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.Any(char.IsControl) || relative.Split('/').Any(x => x.Length == 0 || x is "." or ".."))
            throw new InvalidDataException($"Unsafe archive path: {relative}");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Archive path escapes its directory: {relative}");
        return path;
    }

    public static async Task TextAsync(ZipArchive zip, string path, string text, CancellationToken ct)
    {
        await using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
    }

    public static async Task CopyAsync(ZipArchive zip, string path, string source, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        await input.CopyToAsync(output, ct);
    }

    // The lock prevents publication pruning and media mutations during archive capture.
    // Only a completed private file is returned; MVC disposes it after the download.
    public static async Task<FileStream> CreateAsync(StoragePaths paths, PublicationLock writer,
        Func<ZipArchive, Task> write, CancellationToken ct)
    {
        var path = Path.Combine(paths.DataRoot, "backups", $"download-{Guid.NewGuid():N}.zip");
        await writer.Semaphore.WaitAsync(ct);
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                await write(zip);
            }
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose);
        }
        catch { File.Delete(path); throw; }
        finally { writer.Semaphore.Release(); }
    }
}
