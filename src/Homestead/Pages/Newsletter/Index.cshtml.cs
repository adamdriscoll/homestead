using Homestead.Features.Newsletter;
using Homestead.Features.Publishing;
using Homestead.Features.Themes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Homestead.Pages.Newsletter;

[EnableRateLimiting("newsletter")]
[RequestSizeLimit(16 * 1024)]
public sealed class IndexModel(NewsletterService newsletters, ActiveSite active, IStaticPageRenderer renderer,
    ThemeService themes, IAntiforgery antiforgery) : PageModel
{
    [BindProperty] public SubscriptionInput Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }
    [BindProperty(SupportsGet = true)] public string? Manage { get; set; }
    public string Html { get; private set; } = "";

    private async Task<IActionResult> RenderAsync(NewsletterFormModel form)
    {
        if (active.Current is not { } site || !active.IsHealthy) return StatusCode(503);
        var token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
        Html = await renderer.RenderAsync(new(site.Settings, null, [], Path: "/newsletter/", Heading: "Newsletter",
            Branding: site.Branding, Theme: themes.Resolve(site.Settings.ThemeId))
        { NewsletterForm = form with { AntiforgeryToken = token } });
        return Page();
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (Manage is not null)
        {
            var subscriber = await newsletters.ResolveAsync(Manage, false, HttpContext.RequestAborted);
            return subscriber is null ? NotFound() : await RenderAsync(new("manage", SubscriptionInput.From(subscriber), Token: Manage,
                Message: subscriber.Subscribed ? null : "You are unsubscribed. Save preferences to subscribe again."));
        }
        if (Token is not null)
            return await newsletters.ResolveAsync(Token, true, HttpContext.RequestAborted) is null ? NotFound() : await RenderAsync(new("confirm", Input, Token: Token));
        return await newsletters.IsAvailableAsync(HttpContext.RequestAborted) ? await RenderAsync(new("subscribe", Input)) : NotFound();
    }

    private string Errors => string.Join(" ", ModelState.Values.SelectMany(value => value.Errors).Select(error => error.ErrorMessage.Length == 0 ? "Check the form values." : error.ErrorMessage));
    public async Task<IActionResult> OnPostAsync()
    {
        if (!Request.Form.ContainsKey("Input.PostTypes")) Input.PostTypes.Clear();
        if (Input.PostTypes.Count == 0) ModelState.AddModelError("Input.PostTypes", "Choose at least one post type.");
        if (ModelState.IsValid)
        {
            try
            {
                await newsletters.RequestSubscriptionAsync(Input, HttpContext.RequestAborted);
                return await RenderAsync(new("done", new(), Message: "If this address can receive email, a confirmation link is on its way. Check your inbox and spam folder."));
            }
            catch (InvalidOperationException ex) { ModelState.AddModelError("Input", ex.Message); }
        }
        return await RenderAsync(new("subscribe", Input, Error: Errors));
    }

    public async Task<IActionResult> OnPostConfirmAsync()
    {
        if (Token is null || await newsletters.ConfirmAsync(Token, HttpContext.RequestAborted) is not { } manage) return NotFound();
        var subscriber = await newsletters.ResolveAsync(manage, false, HttpContext.RequestAborted);
        return await RenderAsync(new("manage", SubscriptionInput.From(subscriber!), Token: manage,
            Message: "Subscription confirmed. You will receive new posts with your selected preferences."));
    }

    public async Task<IActionResult> OnPostPreferencesAsync()
    {
        var subscriber = await newsletters.ResolveAsync(Manage, false, HttpContext.RequestAborted);
        if (subscriber is null) return NotFound();
        Input.Email = subscriber.Email; ModelState.Remove("Input.Email");
        if (!Request.Form.ContainsKey("Input.PostTypes")) Input.PostTypes.Clear();
        if (Input.PostTypes.Count == 0) ModelState.AddModelError("Input.PostTypes", "Choose at least one post type.");
        if (!ModelState.IsValid) return await RenderAsync(new("manage", Input, Token: Manage, Error: Errors));
        if (!await newsletters.ManageAsync(Manage!, Input, HttpContext.RequestAborted)) return NotFound();
        return await RenderAsync(new("manage", Input, Token: Manage, Message: "Preferences saved."));
    }

    public async Task<IActionResult> OnPostUnsubscribeAsync()
    {
        if (Manage is null || !await newsletters.ManageAsync(Manage, null, HttpContext.RequestAborted)) return NotFound();
        return await RenderAsync(new("done", new(), Message: "You are unsubscribed. No further newsletters will be sent."));
    }
}
