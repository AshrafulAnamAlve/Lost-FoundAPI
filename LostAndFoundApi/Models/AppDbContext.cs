using Microsoft.EntityFrameworkCore;

namespace LostAndFoundApi.Models
{
    public class AppDbContext :DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }
        public DbSet<Register> Registers { get; set; }
        public DbSet<Lost> Losts { get; set; }
        public DbSet<Found> Founds { get; set; }
        public DbSet<Message> Messages { get; set; }
    }
}
