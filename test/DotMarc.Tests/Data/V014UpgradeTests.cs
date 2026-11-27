using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class V014UpgradeTests(PostgresContainerFixture fixture)
{
    private const string LastV013Migration = "20261119220653_AddQuietHoursAndEscalation";

    [Fact]
    public async Task UpgradingFromV013_PreservesExistingData_AndLeavesNewDomainTargetsUnset()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            var options = new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options;
            await using (var before = new DotMarcDbContext(options))
            {
                await before.GetService<IMigrator>().MigrateAsync(LastV013Migration);
                await before.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Domains" (
                        "Name", "IsMonitored", "SortOrder", "FirstSeenUtc", "DmarcCheckStatus",
                        "DmarcPolicy", "DmarcSubdomainPolicy", "DmarcPercent", "DkimCheckStatus", "DkimSelectors",
                        "DmarcAuthorizationCheckStatus", "DnsProvider", "DnsNameservers",
                        "MtaStsEnabled", "MtaStsMaxAgeSeconds", "MtaStsMode", "MtaStsMxHosts", "MtaStsStatus",
                        "MxCheckStatus", "SpfCheckStatus", "TlsrptCheckStatus")
                    VALUES (
                        'upgrade.example', true, 7, '2026-11-01T00:00:00Z', 'Ok',
                        'Reject', 'Quarantine', 100, 'Ok', ARRAY['selector1'],
                        'Ok', 'AzureDns', ARRAY['ns1.example'],
                        false, 604800, 'Testing', ARRAY[]::text[], 'NotConfigured',
                        'Ok', 'Ok', 'Ok');
                    UPDATE "DnsRecordSettings"
                    SET "KeepOtherRuaAddresses" = false, "AdditionalRuaAddress" = 'reports@upgrade.example'
                    WHERE "Id" = 1;
                    UPDATE "AzureDnsSettings"
                    SET "ClientId" = 'existing-azure-client', "ClientSecretConfigured" = true
                    WHERE "Id" = 1;
                    """);
                before.Roles.AddRange(
                    new Role { Name = "Admin", Permissions = [Permission.DomainsView] },
                    new Role { Name = "Custom viewer", Permissions = [Permission.DomainsView] });
                await before.SaveChangesAsync();
            }

            await using (var upgrade = new DotMarcDbContext(options))
            {
                await DatabaseMigrator.MigrateWithLeaderLockAsync(upgrade);
                await AccessBootstrapper.BootstrapWithLeaderLockAsync(upgrade,
                    Options.Create(new InitialAdminsOptions { Emails = "" }), NullLogger.Instance, CancellationToken.None);
            }

            await using var verify = new DotMarcDbContext(options);
            Assert.Empty(await verify.Database.GetPendingMigrationsAsync());
            var domain = await verify.Domains.SingleAsync();
            Assert.Equal("upgrade.example", domain.Name);
            Assert.True(domain.IsMonitored);
            Assert.Equal(7, domain.SortOrder);
            Assert.Equal(DmarcPolicyLevel.Reject, domain.DmarcPolicy);
            Assert.Equal(DmarcPolicyLevel.Quarantine, domain.DmarcSubdomainPolicy);
            Assert.Equal(100, domain.DmarcPercent);
            Assert.Equal(["selector1"], domain.DkimSelectors);
            Assert.Equal(DetectedDnsProvider.AzureDns, domain.DnsProvider);
            Assert.Null(domain.TargetDmarcPolicy);
            Assert.Null(domain.TargetDmarcSubdomainPolicy);
            Assert.False(domain.OverrideAdditionalRuaAddress);
            Assert.Null(domain.AdditionalRuaAddress);
            Assert.Empty(domain.DmarcRecords);
            Assert.Empty(domain.DmarcProblems);
            Assert.False(domain.DmarcTesting);
            Assert.Null(domain.DmarcNonExistentSubdomainPolicy);

            var settings = await verify.DnsRecordSettings.SingleAsync();
            Assert.False(settings.KeepOtherRuaAddresses);
            Assert.Equal("reports@upgrade.example", settings.AdditionalRuaAddress);
            Assert.Null(settings.TargetDmarcPolicy);
            Assert.Null(settings.TargetDmarcSubdomainPolicy);
            var azure = await verify.AzureDnsSettings.SingleAsync();
            Assert.Equal("existing-azure-client", azure.ClientId);
            Assert.True(azure.ClientSecretConfigured);
            Assert.Contains(Permission.BaselinesManage, (await verify.Roles.SingleAsync(role => role.Name == "Admin")).Permissions);
            Assert.Equal([Permission.DomainsView], (await verify.Roles.SingleAsync(role => role.Name == "Custom viewer")).Permissions);
            Assert.Empty(await verify.DnsPushBatches.ToListAsync());
            Assert.Empty(await verify.DnsPushBatchDomains.ToListAsync());
        }
    }
}
