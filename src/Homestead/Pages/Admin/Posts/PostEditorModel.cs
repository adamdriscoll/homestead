using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin.Posts;

[RequestSizeLimit(5 * MediaService.MaximumBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 5 * MediaService.MaximumBytes + 1024 * 1024)]
public abstract class PostEditorModel(ArticleService articles, MediaService media, PublishingService publishing, ActiveSite active, HomesteadDbContext db) : PageModel
{
    [BindProperty] public ArticleInput Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? EditorTab { get; set; } = "write";
    [BindProperty(SupportsGet = true)] public PostType? NewPostType { get; set; }
    [BindProperty] public IFormFile? Upload { get; set; }
    [BindProperty] public IFormFile? GpxUpload { get; set; }
    [BindProperty] public List<IFormFile> Uploads { get; set; } = [];
    [BindProperty] public string? UploadAlt { get; set; }
    [BindProperty] public bool InlineUpload { get; set; }
    [BindProperty] public bool ConfirmDelete { get; set; }
    [BindProperty] public DateTime? ScheduleLocal { get; set; }
    [BindProperty] public string? ScheduleOffset { get; set; }
    public string ScheduleTimeZone { get; private set; } = "UTC";
    [TempData] public string? StatusMessage { get; set; }
    public Post Post { get; private set; } = null!;
    public ActiveSite Site => active;
    protected HomesteadDbContext Database => db;
    public bool IsNew { get; private set; }
    public bool IsTypeEnabled { get; private set; }
    public ArticleSnapshot? Live => active.Current?.Articles.SingleOrDefault(x => x.PostId == Post.Id);
    public string TypeLabel => Post.Type switch { PostType.Microblog => "note", PostType.Checkin => "check-in", PostType.Fitness => "fitness activity", _ => Post.Type.ToString().ToLowerInvariant() };
    public FitnessSnapshot? Activity => Post.Activity is { } activity ? System.Text.Json.JsonSerializer.Deserialize<FitnessSnapshot>(activity.TrackJson) : null;
    public FitnessUnitSystem FitnessUnits { get; private set; }

    public async Task<IActionResult> OnGetGpxAsync(Guid id)
    {
        var post = await articles.FindAsync(id);
        if (post?.Activity is not { } activity) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return File(activity.OriginalGpx, "application/gpx+xml", "activity.gpx");
    }

    public async Task<IActionResult> OnPostGpxAsync(Guid id, bool remove = false)
    {
        if (!ModelState.IsValid) return await InvalidUploadAsync(id, "Check the GPX upload fields.");
        try
        {
            var version = await articles.ImportGpxAsync(id, Input.Version, GpxUpload, NewPostType, remove, HttpContext.RequestAborted);
            if (WantsJson) return new JsonResult(new { version, editorUrl = Url.Page(EditorPage, new { id }) });
            StatusMessage = remove ? "GPX track removed from this draft." : "GPX track imported privately. Review the route and measurements before publishing.";
            return RedirectToPage(new { id });
        }
        catch (DraftConflictException) { return await InvalidAsync(id, "Another tab saved this draft. Reload before replacing its GPX track.", 409); }
        catch (InvalidOperationException ex) { return await InvalidUploadAsync(id, ex.Message); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
    public virtual async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!ModelState.IsValid || NewPostType is { } type && !Enum.IsDefined(type)) return BadRequest();
        if (!await LoadEditorAsync(id)) return NotFound();
        Input = ArticleInput.From(Post);
        if (Post.Schedule is { } schedule)
        {
            var local = TimeZoneInfo.ConvertTime(new DateTimeOffset(schedule.PublishAtUtc), TimeZoneInfo.FindSystemTimeZoneById(ScheduleTimeZone));
            ScheduleLocal = local.DateTime;
            ScheduleOffset = local.ToString("zzz", System.Globalization.CultureInfo.InvariantCulture);
        }
        return Page();
    }
    public async Task<IActionResult> OnPostSaveAsync(Guid id)
    {
        if (!ModelState.IsValid) return await InvalidAsync(id, "Check the post fields. Your changes have not been saved.", 400);
        try
        {
            Input.Version = await articles.SaveAsync(id, Input, NewPostType);
            if (!await LoadEditorAsync(id)) return NotFound();
            if (WantsJson) return new JsonResult(new { version = Input.Version, persisted = !IsNew, editorUrl = Url.Page(EditorPage, new { id }), message = IsNew ? "Add content to save a draft." : "Saved" });
            if (IsNew) { StatusMessage = "Add content to save a draft."; return RedirectToPage(new { id, newPostType = NewPostType }); }
            StatusMessage = "Draft saved."; return RedirectToPage(new { id });
        }
        catch (DraftConflictException) { return await InvalidAsync(id, "Another tab saved this draft. Copy your changes before reloading; they have not been saved.", 409); }
        catch (InvalidOperationException ex) { return await InvalidAsync(id, ex.Message, 400); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
    public async Task<IActionResult> OnPostUploadAsync(Guid id)
    {
        var completed = 0;
        try
        {
            var files = Upload is null ? Uploads : [Upload];
            if (files.Count == 0 || files.Count > 5) return await InvalidUploadAsync(id, "Choose between one and five photos per upload.");
            ArticleMedia? image = null;
            foreach (var file in files) { image = await media.UploadAsync(id, file, UploadAlt, InlineUpload, NewPostType); completed++; }
            if (WantsJson) return new JsonResult(new { image!.Id, image.Url, image.PrivateUrl, image.AltText, editorUrl = Url.Page(EditorPage, new { id }) });
            StatusMessage = $"{files.Count} photo(s) uploaded. Review descriptions and photo placement before publishing.";
            return RedirectToPage(new { id });
        }
        catch (InvalidOperationException ex) { return await InvalidUploadAsync(id, ex.Message + (completed > 0 ? $" {completed} earlier photo(s) were uploaded privately. Review them before retrying." : "")); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
    private async Task<IActionResult> InvalidUploadAsync(Guid id, string error)
    {
        if (!WantsJson)
        {
            if (!await LoadEditorAsync(id)) return NotFound();
            Input = ArticleInput.From(Post); ModelState.Clear();
        }
        return await InvalidAsync(id, error, 400);
    }
    public async Task<IActionResult> OnPostArrangeAsync(Guid id, string command)
    {
        if (!ModelState.IsValid) return await InvalidAsync(id, "Check the post fields before arranging them.", 400);
        try
        {
            var post = await articles.FindOrNewAsync(id, NewPostType) ?? throw new KeyNotFoundException();
            var parts = command.Split(':');
            if (post.Type == PostType.Guide && parts[0] is "add-tool" or "add-material")
                (parts[0] == "add-tool" ? Input.Tools : Input.Materials).Add(new());
            else if (post.Type == PostType.Guide && parts.Length == 2 && int.TryParse(parts[1], out var supplyIndex) &&
                parts[0] is "tool-up" or "tool-down" or "remove-tool" or "material-up" or "material-down" or "remove-material")
            {
                var supplies = parts[0].Contains("material", StringComparison.Ordinal) ? Input.Materials : Input.Tools;
                if (supplyIndex < 0 || supplyIndex >= supplies.Count) throw new InvalidOperationException("Choose a valid tool or material.");
                if (parts[0].StartsWith("remove-", StringComparison.Ordinal)) supplies.RemoveAt(supplyIndex);
                else
                {
                    var target = parts[0].EndsWith("-up", StringComparison.Ordinal) ? supplyIndex - 1 : supplyIndex + 1;
                    if (target >= 0 && target < supplies.Count) (supplies[supplyIndex], supplies[target]) = (supplies[target], supplies[supplyIndex]);
                }
            }
            else if (parts[0] == "add-step" && post.Type == PostType.Guide) Input.Steps.Add(new());
            else if (parts.Length == 2 && int.TryParse(parts[1], out var index) && parts[0] is "step-up" or "step-down" or "remove-step" && post.Type == PostType.Guide && index >= 0 && index < Input.Steps.Count)
            {
                if (parts[0] == "remove-step")
                {
                    var removed = Input.Steps[index].Id; Input.Steps.RemoveAt(index);
                    foreach (var image in Input.StepImages.Keys.ToArray()) if (Input.StepImages[image] == removed) Input.StepImages[image] = null;
                }
                else
                {
                    var target = parts[0] == "step-up" ? index - 1 : index + 1;
                    if (target >= 0 && target < Input.Steps.Count) (Input.Steps[index], Input.Steps[target]) = (Input.Steps[target], Input.Steps[index]);
                }
            }
            else if (parts.Length == 2 && Guid.TryParse(parts[1], out var imageId) && post.Media.Any(x => x.MediaAssetId == imageId))
            {
                if (Input.MediaOrder.Count == 0) Input.MediaOrder = post.Media.OrderBy(x => x.SortOrder).Select(x => x.MediaAssetId).ToList();
                var imageIndex = Input.MediaOrder.IndexOf(imageId);
                if (parts[0] == "remove-photo")
                {
                    Input.RemovedMediaIds.Add(imageId); Input.MediaOrder.Remove(imageId); Input.StepImages.Remove(imageId);
                }
                else if (parts[0] is "photo-up" or "photo-down" && imageIndex >= 0)
                {
                    var target = parts[0] == "photo-up" ? imageIndex - 1 : imageIndex + 1;
                    if (target >= 0 && target < Input.MediaOrder.Count) (Input.MediaOrder[imageIndex], Input.MediaOrder[target]) = (Input.MediaOrder[target], Input.MediaOrder[imageIndex]);
                }
                else throw new InvalidOperationException("Choose a valid photo action.");
            }
            else throw new InvalidOperationException("Choose a valid step or photo action.");
            Input.Version = await articles.SaveAsync(id, Input, NewPostType);
            if (!await LoadEditorAsync(id)) return NotFound();
            if (IsNew) { ModelState.Clear(); return Page(); }
            StatusMessage = "Draft order saved.";
            return RedirectToPage(new { id, editorTab = EditorTab is "tools" or "materials" or "images" ? EditorTab : "write" });
        }
        catch (DraftConflictException) { return await InvalidAsync(id, "Another tab saved this draft. Copy your changes before reloading.", 409); }
        catch (InvalidOperationException ex) { return await InvalidAsync(id, ex.Message, 400); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
    public Task<IActionResult> OnPostPublishAsync(Guid id) => Transition(id, "publish", save: true);
    public Task<IActionResult> OnPostScheduleAsync(Guid id) => ScheduleLocal is null
        ? InvalidAsync(id, "Choose a publication date and time.", 400) : Transition(id, "schedule", save: true);
    public Task<IActionResult> OnPostCancelScheduleAsync(Guid id) => Transition(id, "cancel-schedule", save: false);
    public Task<IActionResult> OnPostUnpublishAsync(Guid id) => Transition(id, "unpublish", save: false);
    public Task<IActionResult> OnPostDeleteAsync(Guid id) => ConfirmDelete ? Transition(id, "delete", save: false) : InvalidAsync(id, "Confirm deletion first.", 400);
    private async Task<IActionResult> Transition(Guid id, string action, bool save)
    {
        if (!ModelState.IsValid) return await InvalidAsync(id, "Check the post fields.", 400);
        try
        {
            if (save)
            {
                Input.Version = await articles.SaveAsync(id, Input, NewPostType);
                if (await articles.FindAsync(id) is null)
                {
                    if (!await LoadEditorAsync(id)) return NotFound();
                    StatusMessage = PostValidation.PublicationError(Post) ?? "Add content before publishing this post.";
                    return RedirectToPage(new { id, newPostType = NewPostType });
                }
            }
            else if (action == "delete" && await articles.FindAsync(id) is null)
            {
                if (!await LoadEditorAsync(id)) return NotFound();
                return RedirectToPage("Index");
            }
            var result = action switch
            {
                "schedule" => await publishing.ScheduleAsync(id, Input.Version, ScheduleLocal!.Value, ScheduleOffset),
                "cancel-schedule" => await publishing.CancelScheduleAsync(id, Input.Version),
                _ => await publishing.TransitionAsync(id, action, Input.Version)
            };
            StatusMessage = result.Message;
            if (action == "delete") return RedirectToPage("Index");
            return AfterTransition(id, action, result);
        }
        catch (DraftConflictException) { return await InvalidAsync(id, "Another tab saved this draft. Copy your changes before reloading.", 409); }
        catch (InvalidOperationException ex) { return await InvalidAsync(id, ex.Message, 400); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
    public virtual string EditorPage => "/Admin/Posts/Edit";
    protected virtual IActionResult AfterTransition(Guid id, string action, PublicationResult result) => RedirectToPage(new { id });
    private bool WantsJson => Request.Headers.Accept.ToString().Contains("application/json", StringComparison.Ordinal);
    protected virtual async Task<bool> LoadEditorAsync(Guid id)
    {
        var post = await articles.FindAsync(id);
        IsNew = post is null;
        post ??= await articles.FindOrNewAsync(id, NewPostType);
        if (post is null) return false;
        Post = post;
        var settings = await db.SiteSettings.AsNoTracking().SingleAsync();
        IsTypeEnabled = settings.IsPostTypeEnabled(post.Type);
        ScheduleTimeZone = settings.TimeZone;
        FitnessUnits = settings.FitnessUnits;
        return true;
    }
    private async Task<IActionResult> InvalidAsync(Guid id, string error, int status)
    {
        Response.StatusCode = status;
        if (WantsJson) return new JsonResult(new { message = error }) { StatusCode = status };
        if (!await LoadEditorAsync(id)) return NotFound();
        ModelState.AddModelError("", error); return Page();
    }
}
