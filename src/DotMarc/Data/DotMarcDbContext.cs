using Microsoft.EntityFrameworkCore;

namespace DotMarc.Data;

public sealed class DotMarcDbContext : DbContext
{
    public DotMarcDbContext(DbContextOptions<DotMarcDbContext> options) : base(options)
    {
    }

    public DbSet<Domain> Domains => Set<Domain>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ReportRecord> ReportRecords => Set<ReportRecord>();
    public DbSet<ParseFailure> ParseFailures => Set<ParseFailure>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Domain>(entity =>
        {
            entity.HasIndex(d => d.Name).IsUnique();
        });

        modelBuilder.Entity<Report>(entity =>
        {
            entity.HasOne(r => r.Domain)
                .WithMany(d => d.Reports)
                .HasForeignKey(r => r.DomainId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReportRecord>(entity =>
        {
            entity.HasOne(r => r.Report)
                .WithMany(r => r.Records)
                .HasForeignKey(r => r.ReportId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(r => r.Disposition).HasConversion<string>();
            entity.Property(r => r.SpfResult).HasConversion<string>();
            entity.Property(r => r.DkimResult).HasConversion<string>();
        });
    }
}
