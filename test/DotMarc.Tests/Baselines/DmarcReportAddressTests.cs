using DotMarc.Audit;
using DotMarc.Baselines;
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Baselines;

public sealed class DmarcReportAddressResolverTests
{
    [Fact]
    public void InheritsSystemWhenNoGroupOverrides() =>
        Assert.Equal(new("system@example.com", "System default"), DmarcReportAddress.Resolve(
            new Domain { Name = "contoso.example", Groups = [new Group { Name = "Inherited", AdditionalRuaAddress = "ignored@example.com" }] },
            new() { AdditionalRuaAddress = "system@example.com" }));

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("one@example.com", "ONE@example.com", false)]
    [InlineData("one@example.com", "two@example.com", true)]
    [InlineData("one@example.com", null, true)]
    public void GroupsMustAgreeIncludingPrimaryOnly(string? first, string? second, bool conflict)
    {
        var domain = new Domain
        {
            Name = "contoso.example",
            Groups = [
                new Group { Name = "One", OverrideAdditionalRuaAddress = true, AdditionalRuaAddress = first },
                new Group { Name = "Two", OverrideAdditionalRuaAddress = true, AdditionalRuaAddress = second }]
        };
        var result = DmarcReportAddress.Resolve(domain, new() { AdditionalRuaAddress = "system@example.com" });
        Assert.Equal(conflict, result.Conflict);
        Assert.Equal(conflict ? null : first, result.Address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("domain@example.com")]
    public void DomainOverrideResolvesGroupConflictsAndSupportsPrimaryOnly(string? address)
    {
        var domain = new Domain
        {
            Name = "contoso.example", OverrideAdditionalRuaAddress = true, AdditionalRuaAddress = address,
            Groups = [
                new Group { Name = "One", OverrideAdditionalRuaAddress = true, AdditionalRuaAddress = "one@example.com" },
                new Group { Name = "Two", OverrideAdditionalRuaAddress = true }]
        };
        Assert.Equal(new(address, "Domain override"), DmarcReportAddress.Resolve(domain, new()));
    }
}

[Collection("Postgres")]
public sealed class DmarcReportAddressServiceTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private string _connection = "";
    private IAsyncDisposable? _cleanup;
    private DotMarcDbContext Context() => new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connection).Options);

    public async Task InitializeAsync()
    {
        (_connection, _cleanup) = await fixture.CreateDatabaseAsync();
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null) await _cleanup.DisposeAsync();
    }

    [Fact]
    public async Task OverridesPersistAcrossContextsAndEffectiveReaderUsesThem()
    {
        int domainId, groupId;
        await using (var db = Context())
        {
            var group = new Group { Name = "Contoso" };
            var domain = new Domain { Name = "contoso.example", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [group] };
            db.Domains.Add(domain);
            await db.SaveChangesAsync();
            (domainId, groupId) = (domain.Id, group.Id);
            await DmarcReportAddress.SaveAsync(db, TestActors.Admin, null, null, true, " system@example.com ");
            await DmarcReportAddress.SaveAsync(db, TestActors.Admin, null, groupId, true, " group@example.com ");
        }
        await using (var db = Context())
        {
            var domain = await db.Domains.Include(domain => domain.Groups).SingleAsync();
            Assert.Equal(new("group@example.com", "Group override"), DmarcReportAddress.Resolve(domain, await db.DnsRecordSettings.SingleAsync()));
            await DmarcReportAddress.SaveAsync(db, TestActors.Admin, domainId, null, true, " ");
            await DmarcReportAddress.SaveAsync(db, TestActors.Admin, domainId, null, true, null);
            Assert.Equal(3, await db.AuditEntries.CountAsync());
        }
        await using (var db = Context())
        {
            var domain = await db.Domains.Include(domain => domain.Groups).SingleAsync();
            Assert.True(domain.OverrideAdditionalRuaAddress);
            Assert.Null(domain.AdditionalRuaAddress);
            Assert.Equal(new(null, "Domain override"), DmarcReportAddress.Resolve(domain, await db.DnsRecordSettings.SingleAsync()));
            await DmarcReportAddress.SaveAsync(db, TestActors.Admin, domainId, null, false, "ignored@example.com");
        }
        await using (var db = Context())
        {
            var domain = await db.Domains.Include(domain => domain.Groups).SingleAsync();
            Assert.False(domain.OverrideAdditionalRuaAddress);
            Assert.Null(domain.AdditionalRuaAddress);
            Assert.Equal("group@example.com", DmarcReportAddress.Resolve(domain, await db.DnsRecordSettings.SingleAsync()).Address);
            var changes = (await db.AuditEntries.ToListAsync()).SelectMany(entry => entry.Changes).ToList();
            Assert.Contains(changes, change => change.Field == "Secondary DMARC report address");
        }
        var reader = new DnsRecordSettingsReader(new FakeDbContextFactory(_connection));
        Assert.Equal(new("group@example.com", "Group override"), await reader.GetReportAddressAsync(domainId, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidAddressesAreRejectedWithoutChanges()
    {
        await using var db = Context();
        await Assert.ThrowsAsync<ArgumentException>(() => DmarcReportAddress.SaveAsync(db, TestActors.Admin, null, null, true, "mailto:one@example.com"));
        Assert.Empty(await db.AuditEntries.ToListAsync());
        Assert.Null((await db.DnsRecordSettings.SingleAsync()).AdditionalRuaAddress);
    }
}
