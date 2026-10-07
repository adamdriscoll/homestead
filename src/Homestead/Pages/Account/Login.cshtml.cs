using System.ComponentModel.DataAnnotations;
using Homestead.Data.Entities;
using Homestead.Features.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Homestead.Pages.Account;

[EnableRateLimiting("authentication")]
public sealed class LoginModel(SignInManager<ApplicationUser> signIn, SetupService setup) : PageModel
{
    [BindProperty] public LoginInput Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public async Task<IActionResult> OnGetAsync()
    {
        if (!await setup.IsCompleteAsync()) return RedirectToPage("/Setup/Index");
        return User.Identity?.IsAuthenticated == true ? LocalRedirect("/admin") : Page();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!await setup.IsCompleteAsync()) return RedirectToPage("/Setup/Index");
        if (!ModelState.IsValid) return Page();
        var result = await signIn.PasswordSignInAsync(Input.Email, Input.Password, isPersistent: true, lockoutOnFailure: true);
        if (result.Succeeded) return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/admin");
        ModelState.AddModelError("", "Unable to sign in. Check your credentials or try again later.");
        return Page();
    }
    public sealed class LoginInput
    {
        [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
        [Required, StringLength(128), DataType(DataType.Password)] public string Password { get; set; } = "";
    }
}
