using System.Security.Cryptography;
using System.Text;
using Homestead.Data;
using Homestead.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Features.Authentication;

public sealed class SetupLock
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public sealed class SetupService(HomesteadDbContext db, UserManager<ApplicationUser> users, IConfiguration configuration, SetupLock setupLock)
{
    public Task<bool> IsCompleteAsync() => db.Users.AnyAsync();

    public bool IsValidToken(string? token)
    {
        var expected = configuration["HOMESTEAD_SETUP_TOKEN"];
        return expected is { Length: >= 32 } && token is { Length: <= 1024 } &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public async Task<IdentityResult> CreateAdminAsync(string token, string email, string password)
    {
        if (!IsValidToken(token)) return IdentityResult.Failed(new IdentityError { Description = "Invalid setup secret." });
        await setupLock.Semaphore.WaitAsync();
        try
        {
            if (await IsCompleteAsync()) return IdentityResult.Failed(new IdentityError { Description = "Setup is already complete." });
            return await users.CreateAsync(new ApplicationUser { UserName = email, Email = email }, password);
        }
        catch (DbUpdateException)
        {
            if (await db.Users.AsNoTracking().AnyAsync()) return IdentityResult.Failed(new IdentityError { Description = "Setup is already complete." });
            throw;
        }
        finally { setupLock.Semaphore.Release(); }
    }
}
