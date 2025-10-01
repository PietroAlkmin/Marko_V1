// Marko.Data/MarkoDbContext.cs
using Marko.Domain;
using Microsoft.EntityFrameworkCore;

namespace Marko.Data
{
    public class MarkoDbContext : DbContext
    {
        public MarkoDbContext(DbContextOptions<MarkoDbContext> opt) : base(opt) { }

        public DbSet<Price> Prices => Set<Price>();
        public DbSet<Membership> Memberships => Set<Membership>();

        protected override void OnModelCreating(ModelBuilder mb)
        {
            // ----- prices -----
            mb.Entity<Price>(e =>
            {
                e.ToTable("prices", "public");
                e.HasKey(p => new { p.Symbol, p.Date });

                e.Property(p => p.Symbol).HasColumnName("symbol");
                e.Property(p => p.Date).HasColumnName("date").HasColumnType("date");
                e.Property(p => p.PriceAdj).HasColumnName("price_adj").HasColumnType("numeric");

                e.HasIndex(p => p.Symbol);
                e.HasIndex(p => p.Date);
            });

            // ----- membership (nome na base: sp500_membership) -----
            mb.Entity<Membership>(e =>
            {
                e.ToTable("sp500_membership", "public");   // <— aqui muda
                e.HasKey(m => new { m.Symbol, m.StartDate });

                e.Property(m => m.Symbol).HasColumnName("symbol");
                e.Property(m => m.StartDate).HasColumnName("start_date").HasColumnType("date");
                e.Property(m => m.EndDate).HasColumnName("end_date").HasColumnType("date");

                e.HasIndex(m => m.Symbol);
                e.HasIndex(m => m.StartDate);
            });
        }
    }
}
