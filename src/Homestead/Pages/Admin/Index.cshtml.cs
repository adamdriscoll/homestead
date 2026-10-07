using Homestead.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Pages.Admin;

public sealed class IndexModel(HomesteadDbContext db) : PageModel
{
    public string SiteTitle { get; private set; } = "";
    public async Task OnGetAsync() => SiteTitle = await db.SiteSettings.Select(x => x.Title).SingleAsync();
}
