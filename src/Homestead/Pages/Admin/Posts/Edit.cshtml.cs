using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Homestead.Pages.Admin.Posts;

[RequestSizeLimit(MediaService.MaximumBytes + 1024 * 1024)]
public sealed class EditModel(ArticleService articles, MediaService media, PublishingService publishing, ActiveSite active) : PageModel
{
    [BindProperty] public ArticleInput Input { get; set; } = new();
    [BindProperty] public IFormFile? Upload { get; set; }
    [BindProperty] public string? UploadAlt { get; set; }
    [BindProperty] public bool ConfirmDelete { get; set; }
    [TempData] public string? StatusMessage { get; set; }
    public Post Post { get; private set; } = null!;
    public ArticleSnapshot? Live => active.Current?.Articles.SingleOrDefault(x => x.PostId == Post.Id);
    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var post = await articles.FindAsync(id); if (post is null) return NotFound();
        Post = post; Input = ArticleInput.From(post); return Page();
    }
    public async Task<IActionResult> OnPostSaveAsync(Guid id)
    {
        if (!ModelState.IsValid) return await InvalidAsync(id, "Check the post fields. Your changes have not been saved.", 400);
        try
        {
            Input.Version = await articles.SaveAsync(id, Input);
            if (WantsJson) return new JsonResult(new { version = Input.Version, message = "Saved" });
            StatusMessage = "Draft saved."; return RedirectToPage(new { id });
        }
        catch (DraftConflictException) { return await InvalidAsync(id, "Another tab saved this draft. Copy your changes before reloading; they have not been saved.", 409); }
        catch (InvalidOperationException ex) { return await InvalidAsync(id, ex.Message, 400); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
    public async Task<IActionResult> OnPostUploadAsync(Guid id)
    {
        try
        {
            if (Upload is null) return await InvalidAsync(id, "Choose an image first.", 400);
            var image = await media.UploadAsync(id, Upload, UploadAlt);
            if (WantsJson) return new JsonResult(new { image.Id, image.Url, image.PrivateUrl, image.AltText });
            StatusMessage = $"Image uploaded. Insert this Markdown in the body: ![{image.AltText.Replace("[", "").Replace("]", "")}]({image.Url})";
            return RedirectToPage(new { id });
        }
        catch (InvalidOperationException ex) { return await InvalidAsync(id, ex.Message, 400); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
    public Task<IActionResult> OnPostPublishAsync(Guid id) => Transition(id, "publish", save: true);
    public Task<IActionResult> OnPostUnpublishAsync(Guid id) => Transition(id, "unpublish", save: false);
    public Task<IActionResult> OnPostDeleteAsync(Guid id) => ConfirmDelete ? Transition(id, "delete", save: false) : InvalidAsync(id, "Confirm deletion first.", 400);
    private async Task<IActionResult> Transition(Guid id, string action, bool save)
    {
        if (!ModelState.IsValid) return await InvalidAsync(id, "Check the post fields.", 400);
        try
        {
            if (save) Input.Version = await articles.SaveAsync(id, Input);
            var result = await publishing.TransitionAsync(id, action, Input.Version);
            StatusMessage = result.Message;
            if (action == "delete") return RedirectToPage("Index");
            return RedirectToPage(new { id });
        }
        catch (DraftConflictException) { return await InvalidAsync(id, "Another tab saved this draft. Copy your changes before reloading.", 409); }
        catch (InvalidOperationException ex) { return await InvalidAsync(id, ex.Message, 400); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
    private bool WantsJson => Request.Headers.Accept.ToString().Contains("application/json", StringComparison.Ordinal);
    private async Task<IActionResult> InvalidAsync(Guid id, string error, int status)
    {
        Response.StatusCode = status;
        if (WantsJson) return new JsonResult(new { message = error }) { StatusCode = status };
        var post = await articles.FindAsync(id); if (post is null) return NotFound();
        Post = post; ModelState.AddModelError("", error); return Page();
    }
}
