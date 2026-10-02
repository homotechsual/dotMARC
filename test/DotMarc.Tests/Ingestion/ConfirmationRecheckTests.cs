using DotMarc.Data;
using DotMarc.Ingestion;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class ConfirmationRecheckTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ConfirmationRecheckTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private static PollingService CreateService(DotMarcDbContext context) =>
        new(new FakeGraphMailboxClient(), context, NullLogger<PollingService>.Instance);

    private const string Mailbox = "rua.dmarc@mjco.uk";

    /// <summary>A domain whose checks all ran an hour ago, with a state row whose recheck became due a minute ago.</summary>
    private static async Task SeedDueRecheckAsync(DotMarcDbContext context, string item, DateTimeOffset? recheckDueUtc = null)
    {
        var anHourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        var domain = new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-30),
            DmarcCheckedUtc = anHourAgo, TlsrptCheckedUtc = anHourAgo, DmarcAuthorizationCheckedUtc = anHourAgo,
            SpfCheckedUtc = anHourAgo, MxCheckedUtc = anHourAgo, DkimCheckedUtc = anHourAgo, DnsProviderCheckedUtc = anHourAgo,
            DkimSelectors = ["selector1"],
        };
        domain.AlertStates.Add(new DomainAlertState
        {
            Item = item,
            HasPassed = true,
            PendingSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-16),
            RecheckDueUtc = recheckDueUtc ?? DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task TheDmarcCycle_RechecksADueDmarcCheck()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Dmarc);
        var checker = new FakeDmarcDnsChecker();

        await CreateService(context).RunDmarcCheckCycleAsync(context, checker, Mailbox, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.CheckedDomains);
    }

    [Fact]
    public async Task TheDmarcCycle_RechecksADuePolicyChange()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.DmarcPolicy);
        var checker = new FakeDmarcDnsChecker();

        await CreateService(context).RunDmarcCheckCycleAsync(context, checker, Mailbox, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.CheckedDomains);
    }

    [Fact]
    public async Task TheDmarcAuthorizationCycle_RechecksADueCheck()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.DmarcAuthorization);
        var checker = new FakeDmarcDnsChecker();

        await CreateService(context).RunDmarcAuthorizationCheckCycleAsync(context, checker, Mailbox, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.AuthorizationCheckedDomains);
    }

    [Fact]
    public async Task TheTlsrptCycle_RechecksADueCheck()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Tlsrpt);
        var checker = new FakeTlsrptDnsChecker();

        await CreateService(context).RunTlsrptCheckCycleAsync(context, checker, Mailbox, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.CheckedDomains);
    }

    [Fact]
    public async Task TheSpfCycle_RechecksADueCheck()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Spf);
        var checker = new FakeSpfDnsChecker();

        await CreateService(context).RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.CheckedDomains);
    }

    [Fact]
    public async Task TheMxCycle_RechecksADueCheck()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Mx);
        var checker = new FakeMxDnsChecker();

        await CreateService(context).RunMxCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.CheckedDomains);
    }

    [Fact]
    public async Task TheDkimCycle_RechecksADueCheck()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Dkim);
        var checker = new FakeDkimDnsChecker();

        await CreateService(context).RunDkimCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.CheckedDomains);
    }

    [Fact]
    public async Task TheDnsProviderCycle_RechecksADueNameserverChange()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Nameservers);
        var detector = new FakeDnsProviderDetector();

        await CreateService(context).RunDnsProviderCheckCycleAsync(context, detector, CancellationToken.None);

        Assert.Equal(["contoso.io"], detector.CheckedDomains);
    }

    [Fact]
    public async Task ARecheckThatIsntDueYet_IsLeftForLater()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Spf, recheckDueUtc: DateTimeOffset.UtcNow.AddMinutes(10));
        var checker = new FakeSpfDnsChecker();

        await CreateService(context).RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);
    }

    [Fact]
    public async Task ARecheckThatAlreadyRan_IsntRunAgain()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Spf);
        var domain = await context.Domains.SingleAsync();
        domain.SpfCheckedUtc = DateTimeOffset.UtcNow.AddSeconds(-30);
        await context.SaveChangesAsync();
        var checker = new FakeSpfDnsChecker();

        await CreateService(context).RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);
    }

    [Fact]
    public async Task ADueRecheckForAnotherCheck_DoesntTriggerThisOne()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Mx);
        var checker = new FakeSpfDnsChecker();

        await CreateService(context).RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);
    }
}
