using System.ComponentModel.DataAnnotations;
using Homestead.Features.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Homestead.Pages.Setup;

[EnableRateLimiting("authentication")]
public sealed class IndexModel(SetupService setup) : PageModel
{
    [BindProperty] public SetupInput Input { get; set; } = new();
    public async Task<IActionResult> OnGetAsync() => await setup.IsCompleteAsync() ? NotFound() : Page();
    public async Task<IActionResult> OnPostAsync()
    {
        if (await setup.IsCompleteAsync()) return NotFound();
        if (!setup.IsValidToken(Input.Token)) return StatusCode(403);
        if (!ModelState.IsValid) return Page();
        var result = await setup.CreateAdminAsync(Input.Token, Input.Email, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
            return Page();
        }
        return RedirectToPage("/Account/Login");
    }
    public sealed class SetupInput
    {
        [Required, StringLength(1024), Display(Name = "Setup secret")] public string Token { get; set; } = "";
        [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
        [Required, StringLength(128, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = "";
        [Required, Compare(nameof(Password)), DataType(DataType.Password), Display(Name = "Confirm password")] public string ConfirmPassword { get; set; } = "";
    }
}
