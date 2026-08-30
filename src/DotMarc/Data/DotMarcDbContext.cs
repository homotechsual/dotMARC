using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserAccess> UserAccesses => Set<UserAccess>();
    public DbSet<IpInfo> IpInfos => Set<IpInfo>();
    public DbSet<IpRange> IpRanges => Set<IpRange>();

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

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasIndex(r => r.Name).IsUnique();

            // Without an explicit ValueComparer, EF Core's default comparer generation for a
            // List<TEnum> behind a value converter throws at runtime ("cannot be used as a
            // primitive collection") the first time an entity with this property is tracked —
            // it isn't just the cosmetic warning it looks like from the model-validation log.
            entity.Property(r => r.Permissions)
                .HasConversion(
                    permissions => permissions.Select(p => p.ToString()).ToArray(),
                    stored => stored.Select(s => Enum.Parse<Permission>(s)).ToList())
                .Metadata.SetValueComparer(new ValueComparer<List<Permission>>(
                    (a, b) => (a ?? new()).SequenceEqual(b ?? new()),
                    c => c.Aggregate(0, (hash, p) => HashCode.Combine(hash, p)),
                    c => c.ToList()));
        });

        modelBuilder.Entity<UserAccess>(entity =>
        {
            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasOne(u => u.Role)
                .WithMany()
                .HasForeignKey(u => u.RoleId)
                .OnDelete(DeleteBehavior.Restrict);

            // Group has no reciprocal navigation back to UserAccess (unlike Domain.Groups /
            // Group.Domains, which are bidirectional), so EF Core's implicit many-to-many
            // convention does not apply here — left unconfigured, EF instead infers a one-to-many
            // and adds a UserAccessId column directly onto the existing Groups table, which is
            // both the wrong cardinality (a Group must be scopable by more than one UserAccess)
            // and an unwanted change to an existing table. Configuring it explicitly with its own
            // join table avoids both problems.
            entity.HasMany(u => u.ScopedGroups)
                .WithMany()
                .UsingEntity("UserAccessScopedGroups");
        });

        modelBuilder.Entity<IpInfo>(entity =>
        {
            entity.HasKey(i => i.Ip);
            entity.Property(i => i.Ip).HasMaxLength(45); // enough for a full IPv6 address
            entity.Property(i => i.Status).HasConversion<string>();
        });

        modelBuilder.Entity<IpRange>(entity =>
        {
            entity.HasKey(r => new { r.RangeStart, r.RangeEnd });
            entity.Property(r => r.RangeStart).HasMaxLength(45); // enough for a full IPv6 address
            entity.Property(r => r.RangeEnd).HasMaxLength(45);
        });
    }
}
