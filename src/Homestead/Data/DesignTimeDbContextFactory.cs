using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Homestead.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<HomesteadDbContext>
{
    public HomesteadDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<HomesteadDbContext>().UseSqlite("Data Source=homestead-design.db").Options);
}
