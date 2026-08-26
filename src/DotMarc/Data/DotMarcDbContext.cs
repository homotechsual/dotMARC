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
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<PollCycle> PollCycles => Set<PollCycle>();
    public DbSet<PollCycleDailySummary> PollCycleDailySummaries => Set<PollCycleDailySummary>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Tag> Tags => Set<Tag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Domain>(entity =>
        {
            entity.HasIndex(d => d.Name).IsUnique();
            entity.Property(d => d.DmarcCheckStatus).HasConversion<string>();
        });

        modelBuilder.Entity<Report>(entity =>
        {
            entity.HasOne(r => r.Domain)
                .WithMany(d => d.Reports)
                .HasForeignKey(r => r.DomainId)
                .OnDelete(DeleteBehavior.Cascade);

            // One row per (domain, reporting org, report id): a report is re-ingested if the
            // mailbox message that produced it gets re-processed (e.g. it was stored successfully
            // but MarkAsReadAsync failed before the message could be marked read) — this index,
            // paired with PollingService's own pre-insert duplicate check, keeps that safe rather
            // than silently double-counting volume.
            entity.HasIndex(r => new { r.DomainId, r.ReportingOrg, r.ReportId }).IsUnique();
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

        modelBuilder.Entity<ParseFailure>(entity =>
        {
            entity.HasIndex(f => f.GraphMessageId).IsUnique();
        });

        modelBuilder.Entity<ProcessedMessage>(entity =>
        {
            entity.HasIndex(m => m.GraphMessageId).IsUnique();
        });

        modelBuilder.Entity<PollCycle>(entity =>
        {
            entity.HasIndex(p => p.PolledUtc);
        });

        modelBuilder.Entity<PollCycleDailySummary>(entity =>
        {
            entity.HasIndex(d => d.Date).IsUnique();
        });

        modelBuilder.Entity<Group>(entity =>
        {
            entity.HasIndex(g => g.Name).IsUnique();
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasIndex(t => t.Name).IsUnique();
            entity.Property(t => t.Color).HasConversion<string>();
        });
    }
}
