using DotMarc.Data;
using DotMarc.Ingestion;
using DotMarc.MtaSts;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class MtaStsCheckCycleTests : IAsyncLifetime
{
    private const string HostingHostname = "mta-sts.dotmarc.app";

    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public MtaStsCheckCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_MovesPendingDnsToPendingCertificate_WhenCnameResolves()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MtaStsEnabled = true,
            MtaStsStatus = MtaStsStatus.PendingDns
        });
        await context.SaveChangesAsync();

        var dnsVerifier = new FakeMtaStsDnsVerifier { Result = MtaStsDnsVerificationResult.Resolved };
        var servingVerifier = new FakeMtaStsServingVerifier();
        var provisioner = new FakeMtaStsHostProvisioner();
        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, dnsVerifier, servingVerifier, provisioner, HostingHostname, CancellationToken.None);

        Assert.Contains("contoso.io", dnsVerifier.VerifiedDomains);
        var domain = context.Domains.Single();
        Assert.Equal(MtaStsStatus.PendingCertificate, domain.MtaStsStatus);
        Assert.Null(domain.MtaStsCheckDetail);
        Assert.NotNull(domain.MtaStsCheckedUtc);
        // PendingDns never reaches the provisioner/serving-check path in the same pass it resolves.
        Assert.Empty(provisioner.ProvisionedDomains);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_StaysPendingDns_WhenCnameDoesNotResolveYet()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MtaStsEnabled = true,
            MtaStsStatus = MtaStsStatus.PendingDns
        });
        await context.SaveChangesAsync();

        var dnsVerifier = new FakeMtaStsDnsVerifier { Result = MtaStsDnsVerificationResult.NotFound };
        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, dnsVerifier, new FakeMtaStsServingVerifier(), new FakeMtaStsHostProvisioner(), HostingHostname, CancellationToken.None);

        var domain = context.Domains.Single();
        Assert.Equal(MtaStsStatus.PendingDns, domain.MtaStsStatus);
        Assert.Contains("Waiting for", domain.MtaStsCheckDetail);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_MovesPendingCertificateToActive_WhenServingCheckSucceeds()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MtaStsEnabled = true,
            MtaStsStatus = MtaStsStatus.PendingCertificate
        });
        await context.SaveChangesAsync();

        var provisioner = new FakeMtaStsHostProvisioner();
        var servingVerifier = new FakeMtaStsServingVerifier { IsServing = true };
        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, new FakeMtaStsDnsVerifier(), servingVerifier, provisioner, HostingHostname, CancellationToken.None);

        Assert.Contains("contoso.io", provisioner.ProvisionedDomains);
        Assert.Contains("contoso.io", servingVerifier.CheckedDomains);
        var domain = context.Domains.Single();
        Assert.Equal(MtaStsStatus.Active, domain.MtaStsStatus);
        Assert.Null(domain.MtaStsCheckDetail);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_MovesActiveToFailed_WhenServingCheckRegresses()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MtaStsEnabled = true,
            MtaStsStatus = MtaStsStatus.Active,
            MtaStsCheckedUtc = DateTimeOffset.UtcNow.AddHours(-25)
        });
        await context.SaveChangesAsync();

        var servingVerifier = new FakeMtaStsServingVerifier { IsServing = false };
        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, new FakeMtaStsDnsVerifier(), servingVerifier, new FakeMtaStsHostProvisioner(), HostingHostname, CancellationToken.None);

        var domain = context.Domains.Single();
        Assert.Equal(MtaStsStatus.Failed, domain.MtaStsStatus);
        Assert.Contains("stopped serving", domain.MtaStsCheckDetail);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_MovesToFailed_WhenProvisioningThrows()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MtaStsEnabled = true,
            MtaStsStatus = MtaStsStatus.PendingCertificate
        });
        await context.SaveChangesAsync();

        var provisioner = new FakeMtaStsHostProvisioner { ShouldThrowOnEnsure = true };
        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, new FakeMtaStsDnsVerifier(), new FakeMtaStsServingVerifier(), provisioner, HostingHostname, CancellationToken.None);

        var domain = context.Domains.Single();
        Assert.Equal(MtaStsStatus.Failed, domain.MtaStsStatus);
        Assert.Contains("Provisioning failed", domain.MtaStsCheckDetail);
    }

    private async Task<Domain> RunCheckAsync(
        MtaStsStatus status, FakeMtaStsDnsVerifier dnsVerifier, FakeMtaStsServingVerifier servingVerifier, FakeMtaStsHostProvisioner provisioner, string hostingHostname = HostingHostname)
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MtaStsEnabled = true,
            MtaStsStatus = status,
            MtaStsCheckedUtc = DateTimeOffset.UtcNow.AddHours(-25)
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, dnsVerifier, servingVerifier, provisioner, hostingHostname, CancellationToken.None);
        return context.Domains.Single();
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_SaysTheCnamePointsElsewhere_WhenAnActiveDomainStopsServing()
    {
        var dnsVerifier = new FakeMtaStsDnsVerifier { Result = MtaStsDnsVerificationResult.PointsElsewhere, CurrentTarget = "old.region.azurecontainerapps.io" };
        var domain = await RunCheckAsync(MtaStsStatus.Active, dnsVerifier, new FakeMtaStsServingVerifier { IsServing = false }, new FakeMtaStsHostProvisioner(), "new.region.azurecontainerapps.io");

        Assert.Equal(MtaStsStatus.Failed, domain.MtaStsStatus);
        Assert.Contains("points at old.region.azurecontainerapps.io, not at new.region.azurecontainerapps.io", domain.MtaStsCheckDetail);
        Assert.Contains("asuid", domain.MtaStsCheckDetail);
        Assert.DoesNotContain("stopped serving", domain.MtaStsCheckDetail);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_SaysTheCnameIsMissing_WhenAnActiveDomainStopsServingAndHasNoCname()
    {
        var dnsVerifier = new FakeMtaStsDnsVerifier { Result = MtaStsDnsVerificationResult.NotFound };
        var domain = await RunCheckAsync(MtaStsStatus.Active, dnsVerifier, new FakeMtaStsServingVerifier { IsServing = false }, new FakeMtaStsHostProvisioner());

        Assert.Equal(MtaStsStatus.Failed, domain.MtaStsStatus);
        Assert.Contains("has no CNAME record", domain.MtaStsCheckDetail);
        Assert.DoesNotContain("asuid", domain.MtaStsCheckDetail);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_ExplainsTheMovedCname_InsteadOfAProvisioningError()
    {
        var dnsVerifier = new FakeMtaStsDnsVerifier { Result = MtaStsDnsVerificationResult.PointsElsewhere, CurrentTarget = "old.example.net" };
        var domain = await RunCheckAsync(MtaStsStatus.Failed, dnsVerifier, new FakeMtaStsServingVerifier(), new FakeMtaStsHostProvisioner { ShouldThrowOnEnsure = true });

        Assert.Equal(MtaStsStatus.Failed, domain.MtaStsStatus);
        Assert.Contains("points at old.example.net", domain.MtaStsCheckDetail);
        Assert.DoesNotContain("Provisioning failed", domain.MtaStsCheckDetail);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_GoesBackToPendingDns_WhenTheCnameMovesWhileWaitingForACertificate()
    {
        var dnsVerifier = new FakeMtaStsDnsVerifier { Result = MtaStsDnsVerificationResult.PointsElsewhere, CurrentTarget = "old.example.net" };
        var domain = await RunCheckAsync(MtaStsStatus.PendingCertificate, dnsVerifier, new FakeMtaStsServingVerifier { IsServing = false }, new FakeMtaStsHostProvisioner());

        Assert.Equal(MtaStsStatus.PendingDns, domain.MtaStsStatus);
        Assert.Contains("points at old.example.net", domain.MtaStsCheckDetail);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_KeepsTheGenericMessage_WhenTheCnameLookupFails()
    {
        var dnsVerifier = new FakeMtaStsDnsVerifier { ShouldThrowOnVerify = true };
        var domain = await RunCheckAsync(MtaStsStatus.Active, dnsVerifier, new FakeMtaStsServingVerifier { IsServing = false }, new FakeMtaStsHostProvisioner());

        Assert.Equal(MtaStsStatus.Failed, domain.MtaStsStatus);
        Assert.Contains("stopped serving", domain.MtaStsCheckDetail);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_DoesNotLookUpTheCname_WhenAnActiveDomainIsServingFine()
    {
        var dnsVerifier = new FakeMtaStsDnsVerifier { Result = MtaStsDnsVerificationResult.PointsElsewhere };
        var domain = await RunCheckAsync(MtaStsStatus.Active, dnsVerifier, new FakeMtaStsServingVerifier { IsServing = true }, new FakeMtaStsHostProvisioner());

        Assert.Equal(MtaStsStatus.Active, domain.MtaStsStatus);
        Assert.Empty(dnsVerifier.VerifiedDomains);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_WaitsForTheCertificate_WhileAzureIsStillIssuingIt()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MtaStsEnabled = true,
            MtaStsStatus = MtaStsStatus.PendingCertificate
        });
        await context.SaveChangesAsync();

        var provisioner = new FakeMtaStsHostProvisioner { ExceptionOnEnsure = new MtaStsCertificatePendingException("mta-sts.contoso.io") };
        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, new FakeMtaStsDnsVerifier(), new FakeMtaStsServingVerifier(), provisioner, HostingHostname, CancellationToken.None);

        var domain = context.Domains.Single();
        Assert.Equal(MtaStsStatus.PendingCertificate, domain.MtaStsStatus);
        Assert.Contains("still issuing the certificate for mta-sts.contoso.io", domain.MtaStsCheckDetail);
        Assert.DoesNotContain("Provisioning failed", domain.MtaStsCheckDetail);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_ShowsAzuresReason_WithoutItsStatusHeadersAndBody_WhenProvisioningFails()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MtaStsEnabled = true,
            MtaStsStatus = MtaStsStatus.PendingCertificate
        });
        await context.SaveChangesAsync();

        var azureError = new Azure.RequestFailedException(409,
            "Cannot create managed certificate 'x' for subject name 'mta-sts.contoso.io'. Status: 409 (Conflict) ErrorCode: Other Content: {\"error\":{}} Headers: Cache-Control: no-cache");
        var provisioner = new FakeMtaStsHostProvisioner { ExceptionOnEnsure = azureError };
        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, new FakeMtaStsDnsVerifier(), new FakeMtaStsServingVerifier(), provisioner, HostingHostname, CancellationToken.None);

        var domain = context.Domains.Single();
        Assert.Equal(MtaStsStatus.Failed, domain.MtaStsStatus);
        Assert.StartsWith("Provisioning failed: Azure rejected the request: Cannot create managed certificate 'x' for subject name 'mta-sts.contoso.io'.", domain.MtaStsCheckDetail);
        Assert.DoesNotContain("Headers", domain.MtaStsCheckDetail);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_SkipsAnActiveDomainCheckedRecently_ButRechecksAPendingOneAtTheSameAge()
    {
        using var context = CreateContext();
        var oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        context.Domains.AddRange(
            new Domain
            {
                Name = "active.io",
                FirstSeenUtc = DateTimeOffset.UtcNow,
                MtaStsEnabled = true,
                MtaStsStatus = MtaStsStatus.Active,
                MtaStsCheckedUtc = oneHourAgo
            },
            new Domain
            {
                Name = "pending.io",
                FirstSeenUtc = DateTimeOffset.UtcNow,
                MtaStsEnabled = true,
                MtaStsStatus = MtaStsStatus.PendingCertificate,
                MtaStsCheckedUtc = oneHourAgo
            });
        await context.SaveChangesAsync();

        var servingVerifier = new FakeMtaStsServingVerifier { IsServing = true };
        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, new FakeMtaStsDnsVerifier(), servingVerifier, new FakeMtaStsHostProvisioner(), HostingHostname, CancellationToken.None);

        // active.io was checked 1 hour ago: within the 24h window for Active, so skipped.
        // pending.io was checked 1 hour ago too, but PendingCertificate uses the 15-minute window,
        // so it's stale and gets rechecked.
        Assert.DoesNotContain("active.io", servingVerifier.CheckedDomains);
        Assert.Contains("pending.io", servingVerifier.CheckedDomains);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_TearsDownAndResetsADisabledDomain_EvenThoughItNoLongerMatchesTheEnabledFilter()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MtaStsEnabled = false,
            MtaStsStatus = MtaStsStatus.Active,
            MtaStsCheckDetail = "stale detail from before it was disabled"
        });
        await context.SaveChangesAsync();

        var provisioner = new FakeMtaStsHostProvisioner();
        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, new FakeMtaStsDnsVerifier(), new FakeMtaStsServingVerifier(), provisioner, HostingHostname, CancellationToken.None);

        Assert.Contains("contoso.io", provisioner.TornDownDomains);
        var domain = context.Domains.Single();
        Assert.Equal(MtaStsStatus.NotConfigured, domain.MtaStsStatus);
        Assert.Null(domain.MtaStsCheckDetail);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_SkipsDomainsWhereMtaStsIsNotEnabled()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MtaStsEnabled = false,
            MtaStsStatus = MtaStsStatus.PendingDns
        });
        await context.SaveChangesAsync();

        var dnsVerifier = new FakeMtaStsDnsVerifier();
        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, dnsVerifier, new FakeMtaStsServingVerifier(), new FakeMtaStsHostProvisioner(), HostingHostname, CancellationToken.None);

        Assert.Empty(dnsVerifier.VerifiedDomains);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_NoOpsEntirely_WhenNoHostingHostnameIsConfigured()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MtaStsEnabled = true,
            MtaStsStatus = MtaStsStatus.PendingDns
        });
        await context.SaveChangesAsync();

        var dnsVerifier = new FakeMtaStsDnsVerifier();
        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, dnsVerifier, new FakeMtaStsServingVerifier(), new FakeMtaStsHostProvisioner(), hostingHostname: null, CancellationToken.None);

        Assert.Empty(dnsVerifier.VerifiedDomains);
    }

    [Fact]
    public async Task RunMtaStsCheckCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            MtaStsEnabled = true,
            MtaStsStatus = MtaStsStatus.PendingDns
        });
        await context.SaveChangesAsync();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.MtaStsCheckLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var dnsVerifier = new FakeMtaStsDnsVerifier();
        var service = CreateService(context);
        await service.RunMtaStsCheckCycleAsync(context, dnsVerifier, new FakeMtaStsServingVerifier(), new FakeMtaStsHostProvisioner(), HostingHostname, CancellationToken.None);

        Assert.Empty(dnsVerifier.VerifiedDomains);

        await lockTransaction.RollbackAsync();
    }
}
