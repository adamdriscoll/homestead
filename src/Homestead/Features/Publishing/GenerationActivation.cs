using System.Text.Json;
using Homestead.Infrastructure.Storage;

namespace Homestead.Features.Publishing;

public interface IGenerationActivation
{
    Task ActivateAsync(GenerationManifest manifest);
}

// Keep the filesystem commit in one place: the pointer changes only after the complete manifest is durable.
public sealed class GenerationActivation(StoragePaths paths, ActiveSite active) : IGenerationActivation
{
    public async Task ActivateAsync(GenerationManifest manifest)
    {
        var temporary = Path.Combine(paths.SitePath, $"current-{manifest.GenerationId:N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, manifest);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, paths.CurrentPath, overwrite: true);
            active.Activate(manifest);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
