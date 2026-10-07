using Homestead.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Homestead.Pages.Account;

[Authorize]
public sealed class LogoutModel(SignInManager<ApplicationUser> signIn) : PageModel
{
    public IActionResult OnGet() => StatusCode(405);
    public async Task<IActionResult> OnPostAsync()
    {
        await signIn.SignOutAsync();
        return RedirectToPage("/Account/Login");
    }
}
