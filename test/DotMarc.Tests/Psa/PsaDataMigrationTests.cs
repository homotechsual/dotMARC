using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DotMarc.Tests.Psa;

/// <summary>Halo client IDs on Groups and Domains, and Halo ticket IDs on alerts, move into the shared PSA tables
/// on upgrade, so an install keeps raising and closing Halo tickets exactly as before.</summary>
[Collection("Postgres")]
public sealed class PsaDataMigrationTests(PostgresContainerFixture fixture)
{
    private const string MigrationBeforePsaTables = "20261006110411_AddAlertChannelSwitches";

    private static DotMarcDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);

    [Fact]
    public async Task HaloClientIdsAndTickets_AreCopiedIntoTheSharedTables()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            int groupId, domainId, openAlertId, resolvedAlertId;
            await using (var context = CreateContext(connectionString))
            {
                await context.GetService<IMigrator>().MigrateAsync(MigrationBeforePsaTables);

                // Inserted through EF, which writes every column the current model has. Columns added after this point in the
                // history (SpfRangesJson, AlertRuleId) don't exist yet, so they are added here and dropped again before migrating
                // on. The old Halo columns are set in SQL, because they leave the model in a later change.
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" ADD COLUMN \"SpfRangesJson\" text");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Groups\" ADD COLUMN \"TargetDmarcPolicy\" text");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Groups\" ADD COLUMN \"TargetDmarcSubdomainPolicy\" text");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" ADD COLUMN \"TargetDmarcPolicy\" text");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" ADD COLUMN \"TargetDmarcSubdomainPolicy\" text");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" ADD COLUMN \"AdditionalRuaAddress\" text");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" ADD COLUMN \"OverrideAdditionalRuaAddress\" boolean NOT NULL DEFAULT false");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Groups\" ADD COLUMN \"AdditionalRuaAddress\" text");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Groups\" ADD COLUMN \"OverrideAdditionalRuaAddress\" boolean NOT NULL DEFAULT false");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"AlertEvents\" ADD COLUMN \"AlertRuleId\" integer");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"AlertEvents\" ADD COLUMN \"NotifiedUtc\" timestamp with time zone");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"AlertEvents\" ADD COLUMN \"EscalatedUtc\" timestamp with time zone");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" ADD COLUMN \"DmarcNonExistentSubdomainPolicy\" text");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" ADD COLUMN \"DmarcTesting\" boolean NOT NULL DEFAULT false");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" ADD COLUMN \"DmarcRecords\" text[] NOT NULL DEFAULT '{{}}'");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" ADD COLUMN \"DmarcProblems\" text[] NOT NULL DEFAULT '{{}}'");
                var group = new Group { Name = "Client A" };
                var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
                var openAlert = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "open", Message = "m" };
                var resolvedAlert = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "resolved", Message = "m", IsResolved = true };
                var alertWithoutTicket = new AlertEvent { DomainName = "contoso.io", AlertType = "SpfMissing", Severity = "Warning", Title = "none", Message = "m" };
                context.AddRange(group, domain, openAlert, resolvedAlert, alertWithoutTicket);
                await context.SaveChangesAsync();
                (groupId, domainId, openAlertId, resolvedAlertId) = (group.Id, domain.Id, openAlert.Id, resolvedAlert.Id);

                await context.Database.ExecuteSqlRawAsync("UPDATE \"Groups\" SET \"HaloClientId\" = 7 WHERE \"Id\" = {0}", groupId);
                await context.Database.ExecuteSqlRawAsync("UPDATE \"Domains\" SET \"HaloClientId\" = 9 WHERE \"Id\" = {0}", domainId);
                await context.Database.ExecuteSqlRawAsync("UPDATE \"AlertEvents\" SET \"ExternalTicketProvider\" = 'HaloPSA', \"ExternalTicketId\" = '100' WHERE \"Id\" = {0}", openAlertId);
                await context.Database.ExecuteSqlRawAsync("UPDATE \"AlertEvents\" SET \"ExternalTicketProvider\" = 'HaloPSA', \"ExternalTicketId\" = '101' WHERE \"Id\" = {0}", resolvedAlertId);

                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" DROP COLUMN \"SpfRangesJson\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Groups\" DROP COLUMN \"TargetDmarcPolicy\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Groups\" DROP COLUMN \"TargetDmarcSubdomainPolicy\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" DROP COLUMN \"TargetDmarcPolicy\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" DROP COLUMN \"TargetDmarcSubdomainPolicy\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" DROP COLUMN \"AdditionalRuaAddress\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" DROP COLUMN \"OverrideAdditionalRuaAddress\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Groups\" DROP COLUMN \"AdditionalRuaAddress\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Groups\" DROP COLUMN \"OverrideAdditionalRuaAddress\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"AlertEvents\" DROP COLUMN \"AlertRuleId\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"AlertEvents\" DROP COLUMN \"NotifiedUtc\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"AlertEvents\" DROP COLUMN \"EscalatedUtc\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" DROP COLUMN \"DmarcNonExistentSubdomainPolicy\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" DROP COLUMN \"DmarcTesting\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" DROP COLUMN \"DmarcRecords\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Domains\" DROP COLUMN \"DmarcProblems\"");
                await context.Database.MigrateAsync();
            }

            await using var verify = CreateContext(connectionString);
            var links = await verify.PsaCompanyLinks.OrderBy(link => link.CompanyId).ToListAsync();
            Assert.Collection(links,
                groupLink => Assert.Equal((PsaKind.HaloPsa, (int?)groupId, (int?)null, "7", "7"), (groupLink.Psa, groupLink.GroupId, groupLink.DomainId, groupLink.CompanyId, groupLink.CompanyName)),
                domainLink => Assert.Equal((PsaKind.HaloPsa, (int?)null, (int?)domainId, "9", "9"), (domainLink.Psa, domainLink.GroupId, domainLink.DomainId, domainLink.CompanyId, domainLink.CompanyName)));

            var tickets = await verify.AlertTickets.OrderBy(ticket => ticket.TicketId).ToListAsync();
            Assert.Collection(tickets,
                openTicket => Assert.Equal((openAlertId, PsaKind.HaloPsa, "100", true), (openTicket.AlertEventId, openTicket.Psa, openTicket.TicketId, openTicket.IsOpen)),
                closedTicket => Assert.Equal((resolvedAlertId, PsaKind.HaloPsa, "101", false), (closedTicket.AlertEventId, closedTicket.Psa, closedTicket.TicketId, closedTicket.IsOpen)));
        }
    }

    [Fact]
    public async Task RollingBack_PutsHalosLinksAndTicketsBackOnTheOldColumns()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using var context = CreateContext(connectionString);
            await context.Database.MigrateAsync();
            var group = new Group { Name = "Client A", PsaCompanyLinks = [new PsaCompanyLink { Psa = PsaKind.HaloPsa, CompanyId = "7", CompanyName = "Contoso" }, new PsaCompanyLink { Psa = PsaKind.ConnectWise, CompanyId = "250", CompanyName = "Contoso Ltd" }] };
            var alert = new AlertEvent
            {
                DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m",
                Tickets = [new AlertTicket { Psa = PsaKind.HaloPsa, TicketId = "100", IsOpen = true }],
            };
            context.AddRange(group, alert);
            await context.SaveChangesAsync();

            await context.GetService<IMigrator>().MigrateAsync("20261006145346_AddPsaCompanyLinksAndAlertTickets");

            var haloClientId = await context.Database.SqlQueryRaw<int?>("SELECT \"HaloClientId\" AS \"Value\" FROM \"Groups\" WHERE \"Id\" = {0}", group.Id).SingleAsync();
            var ticketId = await context.Database.SqlQueryRaw<string>("SELECT \"ExternalTicketId\" AS \"Value\" FROM \"AlertEvents\" WHERE \"Id\" = {0}", alert.Id).SingleAsync();
            Assert.Equal((7, "100"), (haloClientId, ticketId));
        }
    }

    [Fact]
    public async Task ALinkMustBelongToExactlyOneGroupOrDomain()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using var context = CreateContext(connectionString);
            await context.Database.MigrateAsync();
            context.PsaCompanyLinks.Add(new PsaCompanyLink { Psa = PsaKind.ConnectWise, CompanyId = "1", CompanyName = "Orphan" });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }
}
