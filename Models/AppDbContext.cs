using Microsoft.EntityFrameworkCore;

namespace AdsFinder.Models
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<CheckResult> CheckResults { get; set; }
    }
}