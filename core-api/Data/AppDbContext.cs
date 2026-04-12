using core_api.Models;
using Microsoft.EntityFrameworkCore;

namespace core_api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TunnelMapping> TunnelMappings => Set<TunnelMapping>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var e = modelBuilder.Entity<TunnelMapping>();
        e.ToTable("tunnel_mappings");
        e.HasKey(x => x.Id);
        e.Property(x => x.Subdomain).HasMaxLength(64).IsRequired();
        e.HasIndex(x => x.Subdomain).IsUnique();
        e.Property(x => x.LocalPort).IsRequired();
        e.Property(x => x.CreatedAtUtc).IsRequired();
    }
}
