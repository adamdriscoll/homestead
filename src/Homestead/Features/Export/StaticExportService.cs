using Homestead.Features.Publishing;
using Homestead.Infrastructure.Storage;

namespace Homestead.Features.Export;

public sealed class StaticExportService(StoragePaths paths, PublicationLock writer, ActiveSite active)
{
    public Task<FileStream> CreateAsync(CancellationToken ct = default) => ArchiveFiles.CreateAsync(paths, writer, async zip =>
    {
        var manifest = active.Current ?? throw new InvalidOperationException("Rebuild the site before exporting it.");
        foreach (var relative in manifest.Files)
            await ArchiveFiles.CopyAsync(zip, relative, ArchiveFiles.SafePath(active.Root(manifest), relative), ct);
        await ArchiveFiles.TextAsync(zip, "README.txt",
            $"Homestead static website\nGeneration: {manifest.GenerationId}\nCanonical URL: {manifest.Settings.CanonicalBaseUrl}\n" +
            "Serve this directory at the domain root with a static HTTP server. No Homestead runtime is required.\n" +
            "To change the canonical domain, update Homestead settings and rebuild before exporting.\n", ct);
    }, ct);
}
