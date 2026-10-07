using Microsoft.AspNetCore.Identity;

namespace Homestead.Data.Entities;

public sealed class ApplicationUser : IdentityUser
{
    // The unique, checked constant enforces one account even during concurrent setup.
    public int AdminSlot { get; set; } = 1;
}
