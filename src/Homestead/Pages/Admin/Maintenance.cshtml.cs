using Homestead.Features.Export;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;

namespace Homestead.Pages.Admin;

public sealed class MaintenanceModel(StaticExportService site, HugoExportService hugo, BackupService backup,
    PublishingService publishing, ILogger<MaintenanceModel> logger) : PageModel
{
    [TempData] public string? Notice { get; set; }
    public void OnGet() { }
    public Task<IActionResult> OnPostStaticAsync(CancellationToken ct) => Download(() => site.CreateAsync(ct), "static-site");
    public Task<IActionResult> OnPostHugoAsync(CancellationToken ct) => Download(() => hugo.CreateAsync(ct), "hugo");
    public Task<IActionResult> OnPostBackupAsync(CancellationToken ct) => Download(() => backup.CreateAsync(ct), "backup");
    public async Task<IActionResult> OnPostRebuildAsync()
    {
        Notice = (await publishing.RebuildAsync()).Message;
        return RedirectToPage();
    }
    private async Task<IActionResult> Download(Func<Task<FileStream>> create, string name)
    {
        try { return File(await create(), "application/zip", $"homestead-{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip"); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            logger.LogError(ex, "Unable to create {ArchiveKind} archive.", name);
            ModelState.AddModelError("", "The archive could not be completed. Check server logs, available disk space, and original media; then retry.");
            return Page();
        }
    }
}
