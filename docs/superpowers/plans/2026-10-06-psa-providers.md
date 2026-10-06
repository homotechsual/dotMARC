# PSA Providers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Open, close and close-back alert tickets in HaloPSA, ConnectWise Manage and Autotask, side by side, on one shared PSA layer.

**Architecture:** A new `DotMarc.Psa` namespace holds the shared pieces: `PsaKind`, the `PsaCompanyLink` and `AlertTicket` entities, the `IPsaProvider` contract, the multi-PSA `PsaTicketService`, `PsaTicketClosure` (shared by the Halo webhook and the poller), the `PsaTicketPoller` background service and `PsaDirectory` (loads each ready PSA's companies for pages). HaloPSA keeps its client and settings in `DotMarc.Notifications` and gains a `HaloPsaProvider` adapter. ConnectWise and Autotask each get a settings row, settings service, HTTP client and provider under `DotMarc.Psa.ConnectWise` and `DotMarc.Psa.Autotask`.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor 9.8, EF Core 10 with Npgsql, xUnit with the Testcontainers Postgres fixture (`[Collection("Postgres")]`, `PostgresContainerFixture.CreateDatabaseAsync()`), `FakeHttpMessageHandler` for HTTP client tests.

**Spec:** `docs/superpowers/specs/2026-10-06-psa-providers-design.md`

## Global Constraints

- Several PSAs at once: each Group and Domain may link to one company per PSA; one alert may hold one ticket per PSA.
- ConnectWise and Autotask close-back is by polling. The Halo webhook stays.
- Vendor identifiers: `ConnectWiseSettings.DefaultClientId` and `AutotaskSettings.DefaultIntegrationCode` are constants, empty until registered. The effective value is the override if set, otherwise the default. Saving with Enabled on and no effective value fails validation.
- Every public static method on a service listed in `test/DotMarc.Tests/Audit/AuditCoverageTests.cs` that changes data takes `(DotMarcDbContext context, AuditActor actor, ...)` first, unless its name starts with Get, List, Count or Resolve.
- No em dashes in any user-facing text, docs or comments.
- Meaningful variable names everywhere, tests included (no `r`, `m`, `x`).
- One PSA failing never stops another PSA, an alert being recorded, or the Teams, Slack and webhook channels.
- Secrets (Halo client secret, ConnectWise private key, Autotask secret) live only in `ISecretStore`; rows hold a `...Configured` flag. Audit records them with `.Secret(...)`.
- Poll interval: `Psa:PollIntervalMinutes`, default 5. Backoff doubles per consecutive failure for one PSA, capped at 1 hour, and resets on success.
- The test suite stays green at the end of every task: `dotnet test test/DotMarc.Tests`.

## Review Focus

1. **Headerless import input.** `ImportTable` maps headerless columns by `Enum.GetValues<ImportColumn>()` order, so new `ImportColumn` members must be appended after `MtaStsMaxAge`, or every existing headerless paste shifts columns. Pinned by a test in Task 10.
2. **Upgrading an install with Halo data.** Existing Group and Domain Halo client IDs and open Halo tickets must survive the upgrade, and the Halo webhook must still resolve an alert whose ticket was created before the upgrade. Pinned by `PsaDataMigrationTests` (Task 1) and the webhook test in Task 4.
3. **An alert re-raised after cooldown.** The new copy must not open a second ticket in a PSA where an earlier unresolved copy already has an open ticket, but must still open one in a PSA where it has none. Pinned in Task 3.
4. **A PSA that is down.** Creating, closing and polling continue for the other PSAs, and a failed close is retried by the poller once the alert is resolved. Pinned in Tasks 3 and 4.
5. **A ticket deleted in the PSA.** The poller must mark it closed (Missing) without resolving the alert, and stop asking. Pinned in Task 4.

## Rulings already made against the spec

- **Domains page:** the spec put the per-PSA company override in a domain edit dialog. The page has no such dialog; Halo's override is an inline column. The plan keeps inline columns, one per ready PSA.
- **Autotask close note:** Autotask `TicketNotes` need tenant-specific `noteType` and `publish` picklist values. Closing writes the note into the ticket's `resolution` field instead, in the same PATCH that sets the status.
- **Autotask due date:** Autotask requires `dueDateTime` on create unless the ticket category sets one. Create sends now plus 24 hours.
- **ConnectWise type:** the spec's `TypeId` is kept; ConnectWise also needs a `StatusId` for new tickets, which the spec lists.
- **Readiness:** a PSA is ready when it is Enabled and every setting needed for ticketing is saved (`PsaReadiness`). Halo's webhook secret is not needed for ticketing (the poller backs it up); the Halo integration test still checks it.
- **Data migration timing:** Task 1's migration creates the new tables and copies Halo data in; Task 8's migration drops the old columns. A real upgrade runs both back to back.

---

## Phase 1: Groundwork and HaloPSA

### Task 1: PSA identity, company links and alert tickets (tables and data copy)

**Files:**
- Create: `src/DotMarc/Psa/PsaKind.cs`
- Create: `src/DotMarc/Psa/PsaModels.cs`
- Create: `src/DotMarc/Psa/PsaCompanyLink.cs`
- Create: `src/DotMarc/Psa/AlertTicket.cs`
- Modify: `src/DotMarc/Data/Group.cs`, `src/DotMarc/Data/Domain.cs`, `src/DotMarc/Notifications/AlertEvent.cs` (add navigations)
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Create: migration `AddPsaCompanyLinksAndAlertTickets` (generated, then hand-edited)
- Test: `test/DotMarc.Tests/Psa/PsaDataMigrationTests.cs`

**Interfaces:**
- Produces:
  - `enum PsaKind { HaloPsa, ConnectWise, Autotask }` and extension methods `DisplayName()` ("HaloPSA", "ConnectWise", "Autotask") and `CompanyLabel()` ("Halo client", "ConnectWise company", "Autotask company").
  - `record PsaCompany(string Id, string Name)`, `record PsaOption(int Id, string Name)`, `record PsaTicketRequest(string CompanyId, string DomainName, string AlertType, string Title, string Message)`, `enum PsaTicketState { Open, Closed, Missing }`, `record PsaReadiness(bool Enabled, IReadOnlyList<string> Missing) { bool IsReady }`, `record PsaCloseResult(int Closed, int Failed)` with `static PsaCloseResult None`.
  - `PsaCompanyLink { int Id; PsaKind Psa; int? GroupId; int? DomainId; string CompanyId; string CompanyName; }`
  - `AlertTicket { int Id; int AlertEventId; PsaKind Psa; string TicketId; bool IsOpen; DateTimeOffset CreatedUtc; DateTimeOffset? LastCheckedUtc; }`
  - `Group.PsaCompanyLinks`, `Domain.PsaCompanyLinks` (`List<PsaCompanyLink>`), `AlertEvent.Tickets` (`List<AlertTicket>`).
  - `DbSet<PsaCompanyLink> PsaCompanyLinks`, `DbSet<AlertTicket> AlertTickets`.

- [ ] **Step 1: Write the failing migration test**

```csharp
// test/DotMarc.Tests/Psa/PsaDataMigrationTests.cs
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

                // Inserted through EF (only the old tables' columns are written), then the old Halo columns set in SQL,
                // because they leave the model in a later task.
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
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaDataMigrationTests"`
Expected: build FAIL, `The type or namespace name 'Psa' does not exist in the namespace 'DotMarc'`.

- [ ] **Step 3: Add the types**

```csharp
// src/DotMarc/Psa/PsaKind.cs
namespace DotMarc.Psa;

/// <summary>A PSA dotMARC can raise tickets in. Stored by name, so the order here can change freely.</summary>
public enum PsaKind { HaloPsa, ConnectWise, Autotask }

public static class PsaKindNames
{
    public static string DisplayName(this PsaKind psa) => psa switch
    {
        PsaKind.HaloPsa => "HaloPSA",
        PsaKind.ConnectWise => "ConnectWise",
        PsaKind.Autotask => "Autotask",
        _ => psa.ToString()
    };

    /// <summary>What the PSA calls the customer a ticket is raised against, as a column or field label.</summary>
    public static string CompanyLabel(this PsaKind psa) => psa switch
    {
        PsaKind.HaloPsa => "Halo client",
        PsaKind.ConnectWise => "ConnectWise company",
        PsaKind.Autotask => "Autotask company",
        _ => $"{psa} company"
    };
}
```

```csharp
// src/DotMarc/Psa/PsaModels.cs
namespace DotMarc.Psa;

/// <summary>A customer in a PSA. The id is text because Autotask, ConnectWise and Halo number them differently.</summary>
public sealed record PsaCompany(string Id, string Name);

/// <summary>One choice in a PSA pick-list (board, queue, status, priority and so on).</summary>
public sealed record PsaOption(int Id, string Name);

public sealed record PsaTicketRequest(string CompanyId, string DomainName, string AlertType, string Title, string Message)
{
    /// <summary>The ticket body: the alert's message, then where it came from.</summary>
    public string Body => $"{Message}\n\nDomain: {DomainName}\nAlert type: {AlertType}\nRaised automatically by dotMARC.";
}

public enum PsaTicketState { Open, Closed, Missing }

/// <summary>Whether a PSA can take tickets: switched on, with every setting ticketing needs saved.</summary>
public sealed record PsaReadiness(bool Enabled, IReadOnlyList<string> Missing)
{
    public bool IsReady => Enabled && Missing.Count == 0;
}

public sealed record PsaCloseResult(int Closed, int Failed)
{
    public static PsaCloseResult None { get; } = new(0, 0);

    public PsaCloseResult Add(PsaCloseResult other) => new(Closed + other.Closed, Failed + other.Failed);
}
```

```csharp
// src/DotMarc/Psa/PsaCompanyLink.cs
namespace DotMarc.Psa;

/// <summary>Which company in one PSA a Group's or Domain's tickets go to. Exactly one of <see cref="GroupId"/> and
/// <see cref="DomainId"/> is set (a check constraint enforces it). A Domain's link overrides its Groups' for that PSA.</summary>
public sealed class PsaCompanyLink
{
    public int Id { get; set; }
    public PsaKind Psa { get; set; }
    public int? GroupId { get; set; }
    public int? DomainId { get; set; }
    public required string CompanyId { get; set; }

    /// <summary>For display only, refreshed whenever a page loads the PSA's company list. Nothing sent to the PSA uses it.</summary>
    public required string CompanyName { get; set; }
}
```

```csharp
// src/DotMarc/Psa/AlertTicket.cs
namespace DotMarc.Psa;

/// <summary>A ticket raised in one PSA for an alert. <see cref="IsOpen"/> stays true until dotMARC closes it or sees it
/// closed (or deleted) in the PSA, so a close that failed is retried by the poller.</summary>
public sealed class AlertTicket
{
    public int Id { get; set; }
    public int AlertEventId { get; set; }
    public PsaKind Psa { get; set; }
    public required string TicketId { get; set; }
    public bool IsOpen { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedUtc { get; set; }
}
```

Add navigations (keep the old Halo properties for now; Task 8 removes them):

```csharp
// src/DotMarc/Data/Group.cs, inside the class
public List<DotMarc.Psa.PsaCompanyLink> PsaCompanyLinks { get; set; } = [];

// src/DotMarc/Data/Domain.cs, inside the class
public List<DotMarc.Psa.PsaCompanyLink> PsaCompanyLinks { get; set; } = [];

// src/DotMarc/Notifications/AlertEvent.cs, inside the class
public List<DotMarc.Psa.AlertTicket> Tickets { get; set; } = [];
```

In `DotMarcDbContext`, add the sets beside `AlertTicketRules`, and this configuration after the `AlertTicketRule` block:

```csharp
public DbSet<DotMarc.Psa.PsaCompanyLink> PsaCompanyLinks => Set<DotMarc.Psa.PsaCompanyLink>();
public DbSet<DotMarc.Psa.AlertTicket> AlertTickets => Set<DotMarc.Psa.AlertTicket>();
```

```csharp
modelBuilder.Entity<DotMarc.Psa.PsaCompanyLink>(entity =>
{
    entity.Property(link => link.Psa).HasConversion<string>().HasMaxLength(20);
    entity.Property(link => link.CompanyId).HasMaxLength(64);
    entity.Property(link => link.CompanyName).HasMaxLength(256);
    entity.HasOne<Group>().WithMany(group => group.PsaCompanyLinks).HasForeignKey(link => link.GroupId).OnDelete(DeleteBehavior.Cascade);
    entity.HasOne<Domain>().WithMany(domain => domain.PsaCompanyLinks).HasForeignKey(link => link.DomainId).OnDelete(DeleteBehavior.Cascade);

    // Nulls are distinct in a PostgreSQL unique index, so these allow any number of domain links per PSA in the
    // first and group links in the second, while keeping one link per PSA per owner.
    entity.HasIndex(link => new { link.Psa, link.GroupId }).IsUnique();
    entity.HasIndex(link => new { link.Psa, link.DomainId }).IsUnique();
    entity.ToTable(table => table.HasCheckConstraint("CK_PsaCompanyLinks_OneOwner", "(\"GroupId\" IS NULL) <> (\"DomainId\" IS NULL)"));
});

modelBuilder.Entity<DotMarc.Psa.AlertTicket>(entity =>
{
    entity.Property(ticket => ticket.Psa).HasConversion<string>().HasMaxLength(20);
    entity.Property(ticket => ticket.TicketId).HasMaxLength(64);
    entity.HasOne<AlertEvent>().WithMany(alert => alert.Tickets).HasForeignKey(ticket => ticket.AlertEventId).OnDelete(DeleteBehavior.Cascade);
    entity.HasIndex(ticket => new { ticket.AlertEventId, ticket.Psa }).IsUnique();
    entity.HasIndex(ticket => new { ticket.Psa, ticket.TicketId });
});
```

- [ ] **Step 4: Generate the migration, then add the data copy**

Run: `dotnet ef migrations add AddPsaCompanyLinksAndAlertTickets --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

At the end of the generated `Up`, after both `CreateTable` and `CreateIndex` calls, add:

```csharp
// Halo was the only PSA, and stored its client and ticket ids on the old columns. Copy them in so an upgrade keeps
// its mappings and open tickets. The client name isn't known here; the id stands in until a page loads Halo's list.
migrationBuilder.Sql(@"
    INSERT INTO ""PsaCompanyLinks"" (""Psa"", ""GroupId"", ""DomainId"", ""CompanyId"", ""CompanyName"")
    SELECT 'HaloPsa', ""Id"", NULL, ""HaloClientId""::text, ""HaloClientId""::text FROM ""Groups"" WHERE ""HaloClientId"" IS NOT NULL;
    INSERT INTO ""PsaCompanyLinks"" (""Psa"", ""GroupId"", ""DomainId"", ""CompanyId"", ""CompanyName"")
    SELECT 'HaloPsa', NULL, ""Id"", ""HaloClientId""::text, ""HaloClientId""::text FROM ""Domains"" WHERE ""HaloClientId"" IS NOT NULL;
    INSERT INTO ""AlertTickets"" (""AlertEventId"", ""Psa"", ""TicketId"", ""IsOpen"", ""CreatedUtc"")
    SELECT ""Id"", 'HaloPsa', ""ExternalTicketId"", NOT ""IsResolved"", ""CreatedUtc"" FROM ""AlertEvents""
    WHERE ""ExternalTicketProvider"" = 'HaloPSA' AND ""ExternalTicketId"" IS NOT NULL;");
```

Add the class doc comment: `/// <summary>Adds the shared PSA tables and copies HaloPSA's client and ticket ids into them. Hand-edited: the copy is added after the generated tables.</summary>`. `Down` stays as generated (drops both tables).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaDataMigrationTests"`
Expected: PASS, 2 tests.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test test/DotMarc.Tests`
Expected: all pass.

```bash
git add src/DotMarc/Psa src/DotMarc/Data src/DotMarc/Notifications/AlertEvent.cs src/DotMarc/Migrations test/DotMarc.Tests/Psa
git commit -m "Add the shared PSA tables and copy HaloPSA's client and ticket ids into them"
```

---

### Task 2: Company resolution, ticket policy and suggestions per PSA

**Files:**
- Create: `src/DotMarc/Psa/PsaCompanyResolver.cs` (replaces `src/DotMarc/Notifications/HaloClientResolver.cs`, deleted)
- Modify: `src/DotMarc/Notifications/AlertTicketPolicy.cs`, `src/DotMarc/Notifications/AlertTicketRule.cs` (doc comment)
- Rename: `src/DotMarc/Notifications/HaloGroupSuggestions.cs` to `src/DotMarc/Psa/PsaCompanySuggestions.cs`
- Modify: `src/DotMarc/DomainImport/NameMatcher.cs` (uses `LooseKey`)
- Rename tests: `HaloClientResolverTests.cs` to `test/DotMarc.Tests/Psa/PsaCompanyResolverTests.cs`, `HaloGroupSuggestionsTests.cs` to `test/DotMarc.Tests/Psa/PsaCompanySuggestionsTests.cs`; update `AlertTicketPolicyTests.cs`

**Interfaces:**
- Consumes: `PsaCompanyLink`, `Domain.PsaCompanyLinks`, `Group.PsaCompanyLinks`.
- Produces:
  - `PsaCompanyResolver.Resolve(Domain domain, PsaKind psa) : PsaCompanyLink?`
  - `PsaCompanyResolver.ResolveGroup(Domain domain, PsaKind psa) : Group?`
  - `PsaCompanyResolver.IncludeLinks(IQueryable<Domain>) : IQueryable<Domain>` (includes the domain's links, its Groups and their links)
  - `AlertTicketPolicy.ShouldCreateTicket(string alertType, Domain domain, PsaKind psa, IReadOnlyCollection<AlertTicketRule> rules) : bool`
  - `PsaCompanySuggestions.SuggestCompaniesForGroup(string groupName, IEnumerable<PsaCompany> companies) : IEnumerable<PsaCompany>`, `PsaCompanySuggestions.CompaniesWithoutGroup(PsaKind psa, IEnumerable<PsaCompany> companies, IEnumerable<GroupSummary> groups) : IReadOnlyList<PsaCompany>`, `PsaCompanySuggestions.LooseKey(string) : string`, `record GroupSummary(string Name, string? CompanyId)`.

- [ ] **Step 1: Write the failing resolver tests**

Replace the content of the renamed `PsaCompanyResolverTests.cs` with tests built on links (the old file's cases carry over, rewritten for links, plus the per-PSA ones):

```csharp
// test/DotMarc.Tests/Psa/PsaCompanyResolverTests.cs
using DotMarc.Data;
using DotMarc.Psa;
using Xunit;

namespace DotMarc.Tests.Psa;

public sealed class PsaCompanyResolverTests
{
    private static PsaCompanyLink Link(PsaKind psa, string companyId) => new() { Psa = psa, CompanyId = companyId, CompanyName = $"Company {companyId}" };

    private static Group GroupWith(int id, params PsaCompanyLink[] links) => new() { Id = id, Name = $"Group {id}", PsaCompanyLinks = [.. links] };

    private static Domain DomainWith(IEnumerable<Group> groups, params PsaCompanyLink[] links) =>
        new() { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [.. groups], PsaCompanyLinks = [.. links] };

    [Fact]
    public void TheDomainsOwnLink_WinsOverItsGroups_AndNoGroupDecides()
    {
        var domain = DomainWith([GroupWith(1, Link(PsaKind.HaloPsa, "7"))], Link(PsaKind.HaloPsa, "9"));

        Assert.Equal("9", PsaCompanyResolver.Resolve(domain, PsaKind.HaloPsa)?.CompanyId);
        Assert.Null(PsaCompanyResolver.ResolveGroup(domain, PsaKind.HaloPsa));
    }

    [Fact]
    public void WithoutItsOwnLink_TheLowestIdGroupLinkedInThatPsaDecides()
    {
        var domain = DomainWith([GroupWith(5, Link(PsaKind.HaloPsa, "50")), GroupWith(2, Link(PsaKind.HaloPsa, "20")), GroupWith(1)]);

        Assert.Equal("20", PsaCompanyResolver.Resolve(domain, PsaKind.HaloPsa)?.CompanyId);
        Assert.Equal(2, PsaCompanyResolver.ResolveGroup(domain, PsaKind.HaloPsa)?.Id);
    }

    [Fact]
    public void EachPsa_IsResolvedOnItsOwn_SoDifferentGroupsCanDecide()
    {
        var haloGroup = GroupWith(1, Link(PsaKind.HaloPsa, "7"));
        var connectWiseGroup = GroupWith(2, Link(PsaKind.ConnectWise, "250"));
        var domain = DomainWith([haloGroup, connectWiseGroup], Link(PsaKind.Autotask, "3001"));

        Assert.Equal(1, PsaCompanyResolver.ResolveGroup(domain, PsaKind.HaloPsa)?.Id);
        Assert.Equal(2, PsaCompanyResolver.ResolveGroup(domain, PsaKind.ConnectWise)?.Id);
        Assert.Null(PsaCompanyResolver.ResolveGroup(domain, PsaKind.Autotask));
        Assert.Equal("3001", PsaCompanyResolver.Resolve(domain, PsaKind.Autotask)?.CompanyId);
    }

    [Fact]
    public void NoLinkAnywhere_ResolvesToNothing()
    {
        var domain = DomainWith([GroupWith(1, Link(PsaKind.HaloPsa, "7"))]);

        Assert.Null(PsaCompanyResolver.Resolve(domain, PsaKind.ConnectWise));
        Assert.Null(PsaCompanyResolver.ResolveGroup(domain, PsaKind.ConnectWise));
    }
}
```

In `AlertTicketPolicyTests.cs`, change every `new Group { ..., HaloClientId = n }` to `new Group { ..., PsaCompanyLinks = [new PsaCompanyLink { Psa = PsaKind.HaloPsa, CompanyId = "n", CompanyName = "n" }] }`, every `Domain.HaloClientId = n` likewise on the domain, and every call to `ShouldCreateTicket(type, domain, rules)` to `ShouldCreateTicket(type, domain, PsaKind.HaloPsa, rules)`. Add:

```csharp
[Fact]
public void AGroupRule_OnlyAppliesToThePsaThatGroupDecides()
{
    var haloGroup = new Group { Id = 1, Name = "Halo group", PsaCompanyLinks = [new PsaCompanyLink { Psa = PsaKind.HaloPsa, CompanyId = "7", CompanyName = "A" }] };
    var connectWiseGroup = new Group { Id = 2, Name = "CW group", PsaCompanyLinks = [new PsaCompanyLink { Psa = PsaKind.ConnectWise, CompanyId = "250", CompanyName = "B" }] };
    var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [haloGroup, connectWiseGroup] };
    var rules = new[] { new AlertTicketRule { AlertType = AlertTypes.MissedReport, GroupId = 1, CreateTicket = false } };

    Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.HaloPsa, rules));
    Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, PsaKind.ConnectWise, rules));
}
```

(Use the alert type constant the existing tests use; check `AlertTypes` for the exact member name of the missed-report type and use it.)

In the renamed `PsaCompanySuggestionsTests.cs`, change `HaloClient(id, name)` to `PsaCompany("id", name)`, `HaloGroupSuggestions.` to `PsaCompanySuggestions.`, `ClientsWithoutGroup(clients, groups)` to `CompaniesWithoutGroup(PsaKind.HaloPsa, clients, groups)`, `SuggestClientsForGroup` to `SuggestCompaniesForGroup`, and `GroupSummary(name, int?)` to `GroupSummary(name, string?)`. Add:

```csharp
[Fact]
public void HalosUnknownClient_IsOnlySkippedForHalo()
{
    var companies = new[] { new PsaCompany("1", "Unknown"), new PsaCompany("2", "Fabrikam") };

    Assert.Equal(["Fabrikam"], PsaCompanySuggestions.CompaniesWithoutGroup(PsaKind.HaloPsa, companies, []).Select(company => company.Name));
    Assert.Equal(["Fabrikam", "Unknown"], PsaCompanySuggestions.CompaniesWithoutGroup(PsaKind.ConnectWise, companies, []).Select(company => company.Name));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaCompanyResolverTests|FullyQualifiedName~AlertTicketPolicyTests|FullyQualifiedName~PsaCompanySuggestionsTests"`
Expected: build FAIL, `PsaCompanyResolver` and `PsaCompanySuggestions` not found.

- [ ] **Step 3: Implement**

```csharp
// src/DotMarc/Psa/PsaCompanyResolver.cs
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa;

/// <summary>Works out, for one PSA, which company a domain's ticket goes to and which Group decides its ticket rules.
/// Domain and Group is an implicit many-to-many with no order, so the lowest Group.Id (oldest) breaks ties. Each PSA is
/// resolved on its own, so a domain can take its Halo client from one Group and its ConnectWise company from another.</summary>
public static class PsaCompanyResolver
{
    /// <summary>Loads what <see cref="Resolve"/> and <see cref="ResolveGroup"/> read.</summary>
    public static IQueryable<Domain> IncludeLinks(IQueryable<Domain> domains) =>
        domains.Include(domain => domain.PsaCompanyLinks).Include(domain => domain.Groups).ThenInclude(group => group.PsaCompanyLinks);

    public static PsaCompanyLink? Resolve(Domain domain, PsaKind psa) =>
        domain.PsaCompanyLinks.FirstOrDefault(link => link.Psa == psa)
        ?? ResolveGroup(domain, psa)?.PsaCompanyLinks.First(link => link.Psa == psa);

    /// <summary>The Group whose company the ticket goes to, which is also the Group whose ticket rules apply. Null when
    /// the domain has its own link for this PSA (the global rules apply) or when none of its Groups is linked in it.</summary>
    public static Group? ResolveGroup(Domain domain, PsaKind psa)
    {
        if (domain.PsaCompanyLinks.Any(link => link.Psa == psa))
        {
            return null;
        }

        return domain.Groups
            .Where(group => group.PsaCompanyLinks.Any(link => link.Psa == psa))
            .OrderBy(group => group.Id)
            .FirstOrDefault();
    }
}
```

`AlertTicketPolicy.ShouldCreateTicket` gains the `PsaKind psa` parameter after `domain`, and its first line becomes `var decidingGroup = PsaCompanyResolver.ResolveGroup(domain, psa);`. Its summary becomes "Decides whether an alert creates a ticket in one PSA." `AlertTicketRule`'s summary becomes "Whether an alert type creates PSA tickets (in every PSA the domain maps to)."

Move `HaloGroupSuggestions.cs` to `src/DotMarc/Psa/PsaCompanySuggestions.cs`, namespace `DotMarc.Psa`, class `PsaCompanySuggestions`. Change `HaloClient` to `PsaCompany`, `int` ids to `string`, `GroupSummary(string Name, int? HaloClientId)` to `GroupSummary(string Name, string? CompanyId)`, rename the two public methods as in Interfaces, and make the Unknown-client exclusion `!(psa == PsaKind.HaloPsa && company.Id == HaloUnknownClientId)` with `public const string HaloUnknownClientId = "1";`. `LooseKey` and the name-matching helpers stay as they are. In `NameMatcher.cs`, replace `HaloGroupSuggestions.LooseKey` with `PsaCompanySuggestions.LooseKey` and add `using DotMarc.Psa;`.

Delete `HaloClientResolver.cs`. Every remaining caller (`PsaTicketService`, `HaloIntegrationTestService`, `ManageGroups.razor`) is rewritten in Tasks 3, 5 and 7; to keep this task compiling, change them now to call `PsaCompanyResolver` with `PsaKind.HaloPsa`, and where they need an `int?` client id use `int.TryParse(PsaCompanyResolver.Resolve(domain, PsaKind.HaloPsa)?.CompanyId, out var id) ? id : null`. Their queries that load domains use `PsaCompanyResolver.IncludeLinks(context.Domains)` instead of `context.Domains.Include(d => d.Groups)`.

`ManageGroups.razor` uses `HaloGroupSuggestions`; map its `HaloClient` list with `.Select(client => new PsaCompany(client.Id.ToString(CultureInfo.InvariantCulture), client.Name))` before calling the renamed methods. (Task 5 rewrites this page.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaCompanyResolverTests|FullyQualifiedName~AlertTicketPolicyTests|FullyQualifiedName~PsaCompanySuggestionsTests|FullyQualifiedName~NameMatcher"`
Expected: PASS.

- [ ] **Step 5: Full suite and commit**

Run: `dotnet test test/DotMarc.Tests`. Expected: all pass. (Existing tests that set `HaloClientId` still pass until Task 3, because the old columns remain; any that fail because resolution now reads links are updated to add a `PsaCompanyLink` instead, in this task.)

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Resolve the ticket company and deciding Group per PSA"
```

---

### Task 3: The provider contract, HaloPSA provider and multi-PSA ticket service

**Files:**
- Create: `src/DotMarc/Psa/IPsaProvider.cs`
- Create: `src/DotMarc/Notifications/HaloPsaProvider.cs`
- Modify: `src/DotMarc/Notifications/IPsaTicketService.cs`, `src/DotMarc/Notifications/PsaTicketService.cs`
- Modify: `src/DotMarc/Notifications/AlertingService.cs` (call sites), `src/DotMarc/Notifications/AlertAcknowledgement.cs`
- Modify: `src/DotMarc/Api/AlertEndpoints.cs`, `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Components/Pages/Alerts.razor`
- Modify: `src/DotMarc/Program.cs`
- Modify: `website/data/openapi.json` via `node scripts/update-openapi.mjs` (whatever the committed OpenAPI copy's path is; the script knows it)
- Test: `test/DotMarc.Tests/Notifications/PsaTicketServiceTests.cs` (rewritten), `test/DotMarc.Tests/Internal/FakePsaProvider.cs` (new), `test/DotMarc.Tests/Notifications/HaloPsaProviderTests.cs` (new), `AlertAcknowledgementTests.cs`, API acknowledgement tests

**Interfaces:**
- Consumes: Task 1 types; `PsaCompanyResolver`, `AlertTicketPolicy.ShouldCreateTicket(type, domain, psa, rules)`.
- Produces:

```csharp
public interface IPsaProvider
{
    PsaKind Kind { get; }
    Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default);
    Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default);
    Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default);
    Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default);
    /// <summary>A link to a ticket with "{0}" where its id goes, or null when the PSA's web address isn't known.</summary>
    Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default);
}

public interface IPsaTicketService
{
    Task CreateTicketsAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default);
    Task<PsaCloseResult> CloseTicketsAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default);
}

public sealed record AcknowledgeResult(AcknowledgeOutcome Outcome, PsaCloseResult Tickets);
// AlertAcknowledgement.AcknowledgeAsync(context, actor, alertId, psaTicketService, ct) : Task<AcknowledgeResult>
// AcknowledgeOutcome keeps Acknowledged, AcknowledgedButTicketNotClosed, NotAcknowledgeable
public sealed record ApiAcknowledgement(bool TicketClosed, int TicketsClosed, int TicketsFailed);
```

`PsaTicketService` constructor: `(IEnumerable<IPsaProvider> providers, ILogger<PsaTicketService> logger)`. `CloseTicketsAsync` reads `context.AlertTickets` for the alert (it does not rely on `alert.Tickets` being loaded) and sets `IsOpen = false` on success; callers save.

- [ ] **Step 1: Write a fake provider for tests**

```csharp
// test/DotMarc.Tests/Internal/FakePsaProvider.cs
using DotMarc.Data;
using DotMarc.Psa;

namespace DotMarc.Tests.Internal;

/// <summary>An in-memory PSA. Tickets are numbered from <see cref="NextTicketNumber"/>; set <see cref="FailWith"/> to make
/// every call throw, and <see cref="States"/> to choose what a ticket reads as.</summary>
internal sealed class FakePsaProvider(PsaKind kind) : IPsaProvider
{
    public PsaKind Kind { get; } = kind;
    public bool Ready { get; set; } = true;
    public Exception? FailWith { get; set; }
    public int NextTicketNumber { get; set; } = 1000;
    public List<PsaTicketRequest> Created { get; } = [];
    public List<string> Closed { get; } = [];
    public Dictionary<string, PsaTicketState> States { get; } = [];
    public List<PsaCompany> Companies { get; } = [];

    public Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PsaReadiness(Ready, []));

    public Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return Task.FromResult<IReadOnlyList<PsaCompany>>(Companies);
    }

    public Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        Created.Add(request);
        var ticketId = (NextTicketNumber++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        States[ticketId] = PsaTicketState.Open;
        return Task.FromResult(ticketId);
    }

    public Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return Task.FromResult(States.TryGetValue(ticketId, out var state) ? state : PsaTicketState.Missing);
    }

    public Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        Closed.Add(ticketId);
        States[ticketId] = PsaTicketState.Closed;
        return Task.CompletedTask;
    }

    public Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>($"https://{Kind}.example/ticket/{{0}}");

    private void ThrowIfFailing()
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }
    }
}
```

- [ ] **Step 2: Write the failing ticket service tests**

Rewrite `PsaTicketServiceTests.cs`. Keep the existing class scaffolding (Postgres fixture, `InitializeAsync`, `CreateContext`). Remove the private `FakeHaloPsaClient` and `EnableHaloAsync`. Add helpers and tests:

```csharp
private static PsaCompanyLink Link(PsaKind psa, string companyId) => new() { Psa = psa, CompanyId = companyId, CompanyName = $"Company {companyId}" };

private PsaTicketService CreateService(params FakePsaProvider[] providers) =>
    new(providers, Microsoft.Extensions.Logging.Abstractions.NullLogger<PsaTicketService>.Instance);

/// <summary>A domain in one Group linked in the given PSAs, and one new alert for it.</summary>
private async Task<AlertEvent> SeedAlertAsync(DotMarcDbContext context, params PsaCompanyLink[] groupLinks)
{
    var group = new Group { Name = "Client A", PsaCompanyLinks = [.. groupLinks] };
    context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [group] });
    var alert = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m" };
    context.AlertEvents.Add(alert);
    await context.SaveChangesAsync();
    return alert;
}

[Fact]
public async Task CreateTicketsAsync_RaisesATicketInEveryReadyPsaTheDomainMapsTo()
{
    await using var context = CreateContext();
    var alert = await SeedAlertAsync(context, Link(PsaKind.HaloPsa, "7"), Link(PsaKind.ConnectWise, "250"));
    var halo = new FakePsaProvider(PsaKind.HaloPsa);
    var connectWise = new FakePsaProvider(PsaKind.ConnectWise);
    var autotask = new FakePsaProvider(PsaKind.Autotask);

    await CreateService(halo, connectWise, autotask).CreateTicketsAsync(context, alert);
    await context.SaveChangesAsync();

    Assert.Equal("7", Assert.Single(halo.Created).CompanyId);
    Assert.Equal("250", Assert.Single(connectWise.Created).CompanyId);
    Assert.Empty(autotask.Created);
    var tickets = await context.AlertTickets.OrderBy(ticket => ticket.Psa).ToListAsync();
    Assert.All(tickets, ticket => Assert.True(ticket.IsOpen));
    Assert.Equal([PsaKind.HaloPsa, PsaKind.ConnectWise], tickets.Select(ticket => ticket.Psa).OrderBy(psa => psa));
}

[Fact]
public async Task CreateTicketsAsync_SkipsAPsaThatIsntReady()
{
    await using var context = CreateContext();
    var alert = await SeedAlertAsync(context, Link(PsaKind.HaloPsa, "7"));
    var halo = new FakePsaProvider(PsaKind.HaloPsa) { Ready = false };

    await CreateService(halo).CreateTicketsAsync(context, alert);
    await context.SaveChangesAsync();

    Assert.Empty(halo.Created);
    Assert.Empty(await context.AlertTickets.ToListAsync());
}

[Fact]
public async Task CreateTicketsAsync_OnePsaFailing_DoesntStopTheOthers()
{
    await using var context = CreateContext();
    var alert = await SeedAlertAsync(context, Link(PsaKind.HaloPsa, "7"), Link(PsaKind.ConnectWise, "250"));
    var halo = new FakePsaProvider(PsaKind.HaloPsa) { FailWith = new HttpRequestException("Halo is down") };
    var connectWise = new FakePsaProvider(PsaKind.ConnectWise);

    await CreateService(halo, connectWise).CreateTicketsAsync(context, alert);
    await context.SaveChangesAsync();

    Assert.Single(connectWise.Created);
    Assert.Equal(PsaKind.ConnectWise, (await context.AlertTickets.SingleAsync()).Psa);
}

[Fact]
public async Task CreateTicketsAsync_AReRaisedAlert_OnlyGetsTicketsInPsasWithoutAnOpenOne()
{
    await using var context = CreateContext();
    var earlier = await SeedAlertAsync(context, Link(PsaKind.HaloPsa, "7"), Link(PsaKind.ConnectWise, "250"));
    context.AlertTickets.Add(new AlertTicket { AlertEventId = earlier.Id, Psa = PsaKind.HaloPsa, TicketId = "900", IsOpen = true });
    var reRaised = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m" };
    context.AlertEvents.Add(reRaised);
    await context.SaveChangesAsync();
    var halo = new FakePsaProvider(PsaKind.HaloPsa);
    var connectWise = new FakePsaProvider(PsaKind.ConnectWise);

    await CreateService(halo, connectWise).CreateTicketsAsync(context, reRaised);
    await context.SaveChangesAsync();

    Assert.Empty(halo.Created);
    Assert.Single(connectWise.Created);
}

[Fact]
public async Task CreateTicketsAsync_FollowsTheTicketRules_ForEachPsasDecidingGroup()
{
    await using var context = CreateContext();
    var alert = await SeedAlertAsync(context, Link(PsaKind.HaloPsa, "7"));
    var groupId = (await context.Groups.SingleAsync()).Id;
    context.AlertTicketRules.Add(new AlertTicketRule { AlertType = "MissedReport", GroupId = groupId, CreateTicket = false });
    await context.SaveChangesAsync();
    var halo = new FakePsaProvider(PsaKind.HaloPsa);

    await CreateService(halo).CreateTicketsAsync(context, alert);

    Assert.Empty(halo.Created);
}

[Fact]
public async Task CloseTicketsAsync_ClosesEachOpenTicket_AndKeepsAFailedOneOpenForTheRetry()
{
    await using var context = CreateContext();
    var alert = await SeedAlertAsync(context);
    context.AlertTickets.AddRange(
        new AlertTicket { AlertEventId = alert.Id, Psa = PsaKind.HaloPsa, TicketId = "1", IsOpen = true },
        new AlertTicket { AlertEventId = alert.Id, Psa = PsaKind.ConnectWise, TicketId = "2", IsOpen = true });
    await context.SaveChangesAsync();
    var halo = new FakePsaProvider(PsaKind.HaloPsa);
    var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { FailWith = new HttpRequestException("ConnectWise is down") };

    var result = await CreateService(halo, connectWise).CloseTicketsAsync(context, alert);
    await context.SaveChangesAsync();

    Assert.Equal(new PsaCloseResult(1, 1), result);
    Assert.Equal(["1"], halo.Closed);
    var stillOpen = await context.AlertTickets.SingleAsync(ticket => ticket.IsOpen);
    Assert.Equal(PsaKind.ConnectWise, stillOpen.Psa);
}

[Fact]
public async Task CloseTicketsAsync_ATicketInAPsaThatIsNoLongerRegistered_CountsAsFailed()
{
    await using var context = CreateContext();
    var alert = await SeedAlertAsync(context);
    context.AlertTickets.Add(new AlertTicket { AlertEventId = alert.Id, Psa = PsaKind.Autotask, TicketId = "5", IsOpen = true });
    await context.SaveChangesAsync();

    var result = await CreateService(new FakePsaProvider(PsaKind.HaloPsa)).CloseTicketsAsync(context, alert);

    Assert.Equal(new PsaCloseResult(0, 1), result);
}
```

Update `AlertAcknowledgementTests.cs`: assertions on `AcknowledgeOutcome` become assertions on `result.Outcome`, and add one that two open tickets with one failing give `AcknowledgedButTicketNotClosed` and `Tickets == new PsaCloseResult(1, 1)`, using a fake `IPsaTicketService` that returns a fixed `PsaCloseResult`.

- [ ] **Step 3: Write the failing Halo provider tests**

```csharp
// test/DotMarc.Tests/Notifications/HaloPsaProviderTests.cs
using System.Net;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class HaloPsaProviderTests : IAsyncLifetime
{
    // Same scaffolding as PsaTicketServiceTests: fixture, InitializeAsync migrating, DisposeAsync, CreateContext.

    private sealed class StatusHaloClient(Func<int, int> statusFor) : IHaloPsaClient
    {
        // Every member delegates to NoOpHaloPsaClient except GetTicketStatusAsync, which returns statusFor(ticketId).
    }

    private async Task<HaloPsaSettings> SaveSettingsAsync(Action<HaloPsaSettings> change)
    {
        await using var context = CreateContext();
        var settings = await context.HaloPsaSettings.SingleAsync();
        change(settings);
        await context.SaveChangesAsync();
        return settings;
    }

    [Fact]
    public async Task Readiness_ListsWhatTicketingStillNeeds()
    {
        await SaveSettingsAsync(settings => { settings.Enabled = true; settings.AuthServerUrl = "https://halo.example/auth"; });
        await using var context = CreateContext();

        var readiness = await new HaloPsaProvider(new NoOpHaloPsaClient()).GetReadinessAsync(context);

        Assert.False(readiness.IsReady);
        Assert.Equal(["resource server URL", "client ID", "client secret", "ticket type", "default priority", "closed status"], readiness.Missing);
    }

    [Theory]
    [InlineData(9, PsaTicketState.Closed)]
    [InlineData(2, PsaTicketState.Open)]
    public async Task TicketState_ComparesTheStatusWithTheClosedStatus(int statusId, PsaTicketState expected)
    {
        await SaveSettingsAsync(settings => settings.ClosedStatusId = 9);
        await using var context = CreateContext();

        var state = await new HaloPsaProvider(new StatusHaloClient(_ => statusId)).GetTicketStateAsync(context, "100");

        Assert.Equal(expected, state);
    }

    [Fact]
    public async Task TicketState_ATicketHaloNoLongerHas_IsMissing()
    {
        await SaveSettingsAsync(settings => settings.ClosedStatusId = 9);
        await using var context = CreateContext();
        var provider = new HaloPsaProvider(new StatusHaloClient(_ => throw new HttpRequestException("gone", null, HttpStatusCode.NotFound)));

        Assert.Equal(PsaTicketState.Missing, await provider.GetTicketStateAsync(context, "100"));
    }

    [Fact]
    public async Task TicketUrl_IsTheWebAddressBesideTheApi()
    {
        await SaveSettingsAsync(settings => settings.ResourceServerUrl = "https://contoso.halopsa.com/api/");
        await using var context = CreateContext();

        Assert.Equal("https://contoso.halopsa.com/ticket?id={0}", await new HaloPsaProvider(new NoOpHaloPsaClient()).GetTicketUrlTemplateAsync(context));
    }
}
```

Write out `StatusHaloClient` in full in the file: each `IHaloPsaClient` member returns the same value `NoOpHaloPsaClient` returns, and `GetTicketStatusAsync(settings, ticketId, ct) => Task.FromResult(statusFor(ticketId))`.

- [ ] **Step 4: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaTicketServiceTests|FullyQualifiedName~HaloPsaProviderTests|FullyQualifiedName~AlertAcknowledgementTests"`
Expected: build FAIL (`IPsaProvider`, `HaloPsaProvider`, `CreateTicketsAsync` missing).

- [ ] **Step 5: Implement the contract and Halo provider**

```csharp
// src/DotMarc/Psa/IPsaProvider.cs
using DotMarc.Data;

namespace DotMarc.Psa;

/// <summary>One PSA, as the shared ticket code sees it. Each implementation reads its own settings row from the
/// context it's given. Pick-lists for the settings page (boards, queues, statuses) stay on each PSA's own client,
/// because they differ per PSA and only its settings tab uses them.</summary>
public interface IPsaProvider
{
    PsaKind Kind { get; }
    Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default);

    /// <summary>Raises the ticket and returns its id in the PSA.</summary>
    Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default);

    /// <summary>Missing means the PSA has no such ticket any more (it was deleted or merged).</summary>
    Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default);
    Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default);

    /// <summary>A link to a ticket with "{0}" where its id goes, or null when the PSA's web address isn't known.</summary>
    Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default);
}
```

```csharp
// src/DotMarc/Notifications/HaloPsaProvider.cs
using System.Globalization;
using System.Net;
using DotMarc.Data;
using DotMarc.Psa;

namespace DotMarc.Notifications;

/// <summary>HaloPSA behind the shared PSA contract. The Halo client and settings are unchanged; this adapts them.</summary>
public sealed class HaloPsaProvider(IHaloPsaClient haloClient) : IPsaProvider
{
    public PsaKind Kind => PsaKind.HaloPsa;

    public async Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return new PsaReadiness(settings.Enabled, MissingSettings(settings));
    }

    /// <summary>What ticketing needs. The webhook secret isn't here: tickets still close back through the poller without
    /// it. The integration test checks it separately.</summary>
    public static IReadOnlyList<string> MissingSettings(HaloPsaSettings settings)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.AuthServerUrl)) missing.Add("auth server URL");
        if (string.IsNullOrWhiteSpace(settings.ResourceServerUrl)) missing.Add("resource server URL");
        if (string.IsNullOrWhiteSpace(settings.ClientId)) missing.Add("client ID");
        if (!settings.ClientSecretConfigured) missing.Add("client secret");
        if (settings.TicketTypeId is null) missing.Add("ticket type");
        if (settings.DefaultPriorityId is null) missing.Add("default priority");
        if (settings.ClosedStatusId is null) missing.Add("closed status");
        return missing;
    }

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var clients = await haloClient.ListClientsAsync(settings, cancellationToken).ConfigureAwait(false);
        return clients.Select(client => new PsaCompany(client.Id.ToString(CultureInfo.InvariantCulture), client.Name)).ToList();
    }

    public async Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var clientId = int.Parse(request.CompanyId, CultureInfo.InvariantCulture);
        return await haloClient.CreateTicketAsync(settings, clientId, request.DomainName, request.AlertType, request.Title, request.Message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            var statusId = await haloClient.GetTicketStatusAsync(settings, int.Parse(ticketId, CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            return statusId == settings.ClosedStatusId ? PsaTicketState.Closed : PsaTicketState.Open;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return PsaTicketState.Missing;
        }
    }

    public async Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        await haloClient.CloseTicketAsync(settings, ticketId, note, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.ResourceServerUrl))
        {
            return null;
        }

        // The resource server is the API, at /api beside the web app.
        var webRoot = settings.ResourceServerUrl.TrimEnd('/');
        if (webRoot.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            webRoot = webRoot[..^"/api".Length];
        }

        return webRoot + "/ticket?id={0}";
    }
}
```

- [ ] **Step 6: Implement the ticket service**

```csharp
// src/DotMarc/Notifications/IPsaTicketService.cs
using DotMarc.Data;
using DotMarc.Psa;

namespace DotMarc.Notifications;

public interface IPsaTicketService
{
    /// <summary>Raises a ticket in each ready PSA the alert's domain maps to, recording each as an AlertTicket. The
    /// caller saves.</summary>
    Task CreateTicketsAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default);

    /// <summary>Closes each of the alert's open tickets. A ticket that fails stays open, so the poller retries it.
    /// The caller saves.</summary>
    Task<PsaCloseResult> CloseTicketsAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default);
}
```

```csharp
// src/DotMarc/Notifications/PsaTicketService.cs
using DotMarc.Data;
using DotMarc.Psa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Notifications;

/// <summary>Raises and closes an alert's tickets across every PSA. Each PSA is handled on its own, so one that is down
/// or misconfigured never stops the others.</summary>
public sealed class PsaTicketService(IEnumerable<IPsaProvider> providers, ILogger<PsaTicketService> logger) : IPsaTicketService
{
    public const string ResolvedNote = "Resolved automatically by dotMARC.";

    private readonly IReadOnlyList<IPsaProvider> _providers = providers.ToList();

    public async Task CreateTicketsAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default)
    {
        var domain = await PsaCompanyResolver.IncludeLinks(context.Domains)
            .AsNoTracking()
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Name == alert.DomainName, cancellationToken)
            .ConfigureAwait(false);
        if (domain is null)
        {
            return;
        }

        foreach (var provider in _providers)
        {
            try
            {
                await CreateTicketAsync(context, provider, domain, alert, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Failed to raise a {Psa} ticket for {DomainName} alert {AlertType}.", provider.Kind.DisplayName(), alert.DomainName, alert.AlertType);
            }
        }
    }

    private static async Task CreateTicketAsync(DotMarcDbContext context, IPsaProvider provider, Domain domain, AlertEvent alert, CancellationToken cancellationToken)
    {
        if (!(await provider.GetReadinessAsync(context, cancellationToken).ConfigureAwait(false)).IsReady)
        {
            return;
        }

        if (PsaCompanyResolver.Resolve(domain, provider.Kind) is not { } company)
        {
            return;
        }

        // Only this alert type's rules matter, and of the group rules only the deciding group's for this PSA.
        var decidingGroupId = PsaCompanyResolver.ResolveGroup(domain, provider.Kind)?.Id;
        var rules = await context.AlertTicketRules
            .AsNoTracking()
            .Where(rule => rule.AlertType == alert.AlertType && (rule.GroupId == null || rule.GroupId == decidingGroupId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!AlertTicketPolicy.ShouldCreateTicket(alert.AlertType, domain, provider.Kind, rules))
        {
            return;
        }

        // AlertingService raises a new AlertEvent each cooldown window while a condition stays unhealthy, without
        // resolving the earlier one. If an earlier unresolved copy already has an open ticket in this PSA, don't raise
        // another there; the alert row itself is still recorded.
        var ticketAlreadyOpen = await context.AlertTickets.AnyAsync(ticket =>
                ticket.Psa == provider.Kind && ticket.IsOpen && ticket.AlertEventId != alert.Id
                && context.AlertEvents.Any(other => other.Id == ticket.AlertEventId && other.DomainName == alert.DomainName && other.AlertType == alert.AlertType && !other.IsResolved),
                cancellationToken)
            .ConfigureAwait(false);
        if (ticketAlreadyOpen)
        {
            return;
        }

        var ticketId = await provider.CreateTicketAsync(context, new PsaTicketRequest(company.CompanyId, alert.DomainName, alert.AlertType, alert.Title, alert.Message), cancellationToken).ConfigureAwait(false);
        context.AlertTickets.Add(new AlertTicket { AlertEventId = alert.Id, Psa = provider.Kind, TicketId = ticketId, IsOpen = true });
    }

    public async Task<PsaCloseResult> CloseTicketsAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default)
    {
        var tickets = await context.AlertTickets
            .Where(ticket => ticket.AlertEventId == alert.Id && ticket.IsOpen)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Filtered again in memory: a ticket this context has just marked closed (PsaTicketClosure does, before calling
        // this) is still open in the database, and the query returns the tracked instance.
        var result = PsaCloseResult.None;
        foreach (var ticket in tickets.Where(ticket => ticket.IsOpen))
        {
            result = result.Add(await CloseTicketAsync(context, ticket, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>Closes one ticket, marking it closed on success. Shared with the poller's retry.</summary>
    public async Task<PsaCloseResult> CloseTicketAsync(DotMarcDbContext context, AlertTicket ticket, CancellationToken cancellationToken = default)
    {
        var provider = _providers.FirstOrDefault(candidate => candidate.Kind == ticket.Psa);
        if (provider is null)
        {
            return new PsaCloseResult(0, 1);
        }

        try
        {
            await provider.CloseTicketAsync(context, ticket.TicketId, ResolvedNote, cancellationToken).ConfigureAwait(false);
            ticket.IsOpen = false;
            return new PsaCloseResult(1, 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to close {Psa} ticket {TicketId}. dotMARC will try again.", ticket.Psa.DisplayName(), ticket.TicketId);
            return new PsaCloseResult(0, 1);
        }
    }
}
```

Call sites:

- `AlertingService.cs` line ~369: `CreateTicketAsync` becomes `CreateTicketsAsync`. Lines ~279 and ~310: `await _psaTicketService.CloseTicketAsync(db, copy, ...)` becomes `await _psaTicketService.CloseTicketsAsync(db, copy, ...)`; keep the surrounding try/catch. The `SaveChangesAsync` after each loop already persists `IsOpen`.
- `AlertAcknowledgement.cs`: return `Task<AcknowledgeResult>`; replace the `ticketsClosed` loop with

```csharp
var tickets = PsaCloseResult.None;
foreach (var copy in openCopies)
{
    tickets = tickets.Add(await psaTicketService.CloseTicketsAsync(context, copy, cancellationToken).ConfigureAwait(false));
}

await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
return new AcknowledgeResult(tickets.Failed == 0 ? AcknowledgeOutcome.Acknowledged : AcknowledgeOutcome.AcknowledgedButTicketNotClosed, tickets);
```

  and the not-acknowledgeable return becomes `new AcknowledgeResult(AcknowledgeOutcome.NotAcknowledgeable, PsaCloseResult.None)`. Add `public sealed record AcknowledgeResult(AcknowledgeOutcome Outcome, PsaCloseResult Tickets);` to the file and update the enum comment to "its PSA tickets couldn't all be closed and need closing by hand".
- `ApiModels.cs`: `public sealed record ApiAcknowledgement(bool TicketClosed, int TicketsClosed, int TicketsFailed);` with an XML summary: "TicketClosed is true when no ticket failed to close (also when the alert had none)."
- `AlertEndpoints.cs`: switch on `result.Outcome`; both success cases return `TypedResults.Ok(new ApiAcknowledgement(result.Tickets.Failed == 0, result.Tickets.Closed, result.Tickets.Failed))`.
- `Alerts.razor` `AcknowledgeAsync`: the dialog text "Its Halo ticket is closed too, if it has one." becomes "Its PSA tickets are closed too, if it has any." and the outcome switch becomes:

```csharp
switch (result.Outcome)
{
    case AcknowledgeOutcome.Acknowledged:
        Snackbar.Add(result.Tickets.Closed == 0 ? "Alert acknowledged." : $"Alert acknowledged. Closed {result.Tickets.Closed} ticket{(result.Tickets.Closed == 1 ? "" : "s")}.", Severity.Success);
        break;
    case AcknowledgeOutcome.AcknowledgedButTicketNotClosed:
        Snackbar.Add($"Alert acknowledged, but {result.Tickets.Failed} ticket{(result.Tickets.Failed == 1 ? "" : "s")} couldn't be closed. dotMARC will keep trying, or close {(result.Tickets.Failed == 1 ? "it" : "them")} in the PSA.", Severity.Warning);
        break;
    // NotAcknowledgeable stays as it is
}
```

- `Program.cs`: beside the Halo registrations add `builder.Services.AddSingleton<IPsaProvider, HaloPsaProvider>();` (with `using DotMarc.Psa;`). `PsaTicketService` stays a singleton; register it as itself too so the poller can reach `CloseTicketAsync`: replace `builder.Services.AddSingleton<IPsaTicketService, PsaTicketService>();` with

```csharp
builder.Services.AddSingleton<PsaTicketService>();
builder.Services.AddSingleton<IPsaTicketService>(services => services.GetRequiredService<PsaTicketService>());
```

- Stop writing the old columns: nothing sets `ExternalTicketProvider` or `ExternalTicketId` any more. The Halo webhook still reads them until Task 4.
- Refresh the OpenAPI copy: `node scripts/update-openapi.mjs`.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaTicketServiceTests|FullyQualifiedName~HaloPsaProviderTests|FullyQualifiedName~AlertAcknowledgementTests|FullyQualifiedName~Api"`
Expected: PASS. Then `dotnet test test/DotMarc.Tests`; fix any `AlertingServiceTests`, `DnsHealthAlertingTests` or `ProgramDiValidationTests` that constructed `PsaTicketService(IHaloPsaClient)` by passing `[new HaloPsaProvider(new NoOpHaloPsaClient())]` and a `NullLogger`.

- [ ] **Step 8: Commit**

```bash
git add -A src/DotMarc test/DotMarc.Tests website
git commit -m "Raise and close alert tickets in every ready PSA, each on its own"
```

---

### Task 4: Shared ticket closure, the Halo webhook on AlertTicket, and the poller

**Files:**
- Create: `src/DotMarc/Psa/PsaTicketClosure.cs`
- Create: `src/DotMarc/Psa/PsaTicketPoller.cs`
- Modify: `src/DotMarc/Program.cs` (webhook lookup, poller registration)
- Modify: `src/DotMarc/Audit/AuditActions.cs` (new action)
- Test: `test/DotMarc.Tests/Psa/PsaTicketPollerTests.cs`, `test/DotMarc.Tests/Notifications/HaloWebhookEndpointTests.cs`

**Interfaces:**
- Consumes: `PsaTicketService.CloseTicketAsync(context, AlertTicket, ct)`, `IPsaTicketService.CloseTicketsAsync`, `IPsaProvider`.
- Produces:
  - `AuditActions.AlertResolvedByTicket = "alert.resolved_by_ticket"`, label "Alert resolved by a closed PSA ticket".
  - `PsaTicketClosure.ResolveFromTicketAsync(DotMarcDbContext context, IPsaTicketService ticketService, PsaKind psa, string ticketId, CancellationToken ct) : Task<bool>` (true when an unresolved alert was resolved; the caller saves).
  - `PsaTicketPoller(IDbContextFactory<DotMarcDbContext> dbFactory, IEnumerable<IPsaProvider> providers, PsaTicketService ticketService, IConfiguration configuration, TimeProvider timeProvider, ILogger<PsaTicketPoller> logger) : BackgroundService`, with `public Task PollOnceAsync(CancellationToken ct)`.

- [ ] **Step 1: Write the failing poller tests**

```csharp
// test/DotMarc.Tests/Psa/PsaTicketPollerTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Psa;

[Collection("Postgres")]
public sealed class PsaTicketPollerTests : IAsyncLifetime
{
    // Same fixture scaffolding as PsaTicketServiceTests (connection string, migrate in InitializeAsync, CreateContext).

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

    private PsaTicketPoller CreatePoller(params FakePsaProvider[] providers) =>
        new(new FakeDbContextFactory(CreateContext), providers,
            new PsaTicketService(providers, NullLogger<PsaTicketService>.Instance),
            new ConfigurationBuilder().Build(), _clock, NullLogger<PsaTicketPoller>.Instance);

    private async Task<(int AlertId, int TicketRowId)> SeedTicketAsync(PsaKind psa, string ticketId, bool alertResolved)
    {
        await using var context = CreateContext();
        var alert = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m", IsResolved = alertResolved };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();
        var ticket = new AlertTicket { AlertEventId = alert.Id, Psa = psa, TicketId = ticketId, IsOpen = true };
        context.AlertTickets.Add(ticket);
        await context.SaveChangesAsync();
        return (alert.Id, ticket.Id);
    }

    [Fact]
    public async Task ATicketClosedInThePsa_ResolvesItsAlert_AndIsAudited()
    {
        var (alertId, _) = await SeedTicketAsync(PsaKind.ConnectWise, "250", alertResolved: false);
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { States = { ["250"] = PsaTicketState.Closed } };

        await CreatePoller(connectWise).PollOnceAsync(CancellationToken.None);

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync(alert => alert.Id == alertId)).IsResolved);
        Assert.False((await verify.AlertTickets.SingleAsync()).IsOpen);
        Assert.Equal(DotMarc.Audit.AuditActions.AlertResolvedByTicket, (await verify.AuditEntries.SingleAsync()).Action);
    }

    [Fact]
    public async Task ATicketDeletedInThePsa_StopsBeingChecked_ButLeavesTheAlertOpen()
    {
        var (alertId, _) = await SeedTicketAsync(PsaKind.Autotask, "3001", alertResolved: false);

        await CreatePoller(new FakePsaProvider(PsaKind.Autotask)).PollOnceAsync(CancellationToken.None);

        await using var verify = CreateContext();
        Assert.False((await verify.AlertEvents.SingleAsync(alert => alert.Id == alertId)).IsResolved);
        Assert.False((await verify.AlertTickets.SingleAsync()).IsOpen);
    }

    [Fact]
    public async Task AStillOpenTicket_IsStampedAsChecked()
    {
        await SeedTicketAsync(PsaKind.HaloPsa, "100", alertResolved: false);
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { States = { ["100"] = PsaTicketState.Open } };

        await CreatePoller(halo).PollOnceAsync(CancellationToken.None);

        await using var verify = CreateContext();
        var ticket = await verify.AlertTickets.SingleAsync();
        Assert.True(ticket.IsOpen);
        Assert.Equal(_clock.GetUtcNow(), ticket.LastCheckedUtc);
    }

    [Fact]
    public async Task AResolvedAlertsOpenTicket_IsClosedAgain()
    {
        await SeedTicketAsync(PsaKind.ConnectWise, "250", alertResolved: true);
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { States = { ["250"] = PsaTicketState.Open } };

        await CreatePoller(connectWise).PollOnceAsync(CancellationToken.None);

        Assert.Equal(["250"], connectWise.Closed);
        await using var verify = CreateContext();
        Assert.False((await verify.AlertTickets.SingleAsync()).IsOpen);
    }

    [Fact]
    public async Task OnePsaFailing_DoesntStopTheOthers()
    {
        await SeedTicketAsync(PsaKind.HaloPsa, "100", alertResolved: false);
        var (connectWiseAlertId, _) = await SeedTicketAsync(PsaKind.ConnectWise, "250", alertResolved: false);
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { FailWith = new HttpRequestException("Halo is down") };
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { States = { ["250"] = PsaTicketState.Closed } };

        await CreatePoller(halo, connectWise).PollOnceAsync(CancellationToken.None);

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync(alert => alert.Id == connectWiseAlertId)).IsResolved);
    }

    [Fact]
    public async Task AFailingPsa_BacksOff_AndIsAskedAgainOnceTheWaitIsOver()
    {
        await SeedTicketAsync(PsaKind.HaloPsa, "100", alertResolved: false);
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { FailWith = new HttpRequestException("Halo is down") };
        var poller = CreatePoller(halo);

        await poller.PollOnceAsync(CancellationToken.None);   // fails: wait one interval (5 minutes) before the next try
        halo.FailWith = null;
        halo.States["100"] = PsaTicketState.Closed;
        await poller.PollOnceAsync(CancellationToken.None);   // still inside the wait: not asked

        await using (var verify = CreateContext())
        {
            Assert.False((await verify.AlertEvents.SingleAsync()).IsResolved);
        }

        _clock.Advance(TimeSpan.FromMinutes(5));
        await poller.PollOnceAsync(CancellationToken.None);

        await using var verifyAfter = CreateContext();
        Assert.True((await verifyAfter.AlertEvents.SingleAsync()).IsResolved);
    }

    [Fact]
    public async Task APsaThatIsntReady_IsLeftAlone()
    {
        await SeedTicketAsync(PsaKind.HaloPsa, "100", alertResolved: false);
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { Ready = false, States = { ["100"] = PsaTicketState.Closed } };

        await CreatePoller(halo).PollOnceAsync(CancellationToken.None);

        await using var verify = CreateContext();
        Assert.True((await verify.AlertTickets.SingleAsync()).IsOpen);
    }
}
```

Check `FixedTimeProvider` and `FakeDbContextFactory` in `test/DotMarc.Tests/Internal` for their exact constructors and whether `FixedTimeProvider` has `Advance`; if it has no `Advance`, add `public void Advance(TimeSpan by) => _now += by;` (or the equivalent for its field). Check the audit entity's DbSet name in `DotMarcDbContext` (shown here as `AuditEntries`) and use the real one.

In `HaloWebhookEndpointTests.cs`, change every seeded alert that set `ExternalTicketProvider = "HaloPSA", ExternalTicketId = "n"` to add `new AlertTicket { AlertEventId = alert.Id, Psa = PsaKind.HaloPsa, TicketId = "n", IsOpen = true }` after saving the alert, and add a poller test that pins the double-close bug this guards against:

```csharp
[Fact]
public async Task ResolvingFromAClosedTicket_DoesntTryToCloseThatSameTicketAgain()
{
    await SeedTicketAsync(PsaKind.ConnectWise, "250", alertResolved: false);
    var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { States = { ["250"] = PsaTicketState.Closed } };

    await CreatePoller(connectWise).PollOnceAsync(CancellationToken.None);

    Assert.Empty(connectWise.Closed);
}
```

and in the webhook tests add:

```csharp
[Fact]
public async Task AClosedStatus_ResolvesTheAlert_ClosesItsOtherTickets_AndMarksTheHaloTicketClosed()
```

which seeds a Halo ticket "100" and a ConnectWise ticket "250" on the same open alert, posts the closed status for 100, and asserts the alert is resolved and the Halo row has `IsOpen == false`. (The ConnectWise ticket closes through the registered providers; in the test host, ConnectWise isn't registered until Task 9, so assert only that the Halo row is closed and the alert resolved.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaTicketPollerTests|FullyQualifiedName~HaloWebhookEndpointTests"`
Expected: build FAIL (`PsaTicketPoller`, `AlertResolvedByTicket` missing).

- [ ] **Step 3: Implement closure and the poller**

Add to `AuditActions`: `public const string AlertResolvedByTicket = "alert.resolved_by_ticket";` and the label row `(AlertResolvedByTicket, "Alert resolved by a closed PSA ticket"),`.

```csharp
// src/DotMarc/Psa/PsaTicketClosure.cs
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa;

/// <summary>What happens when a tech closes an alert's ticket in a PSA, however dotMARC hears about it (the Halo
/// webhook or the poller): the alert resolves, a policy or nameserver change is accepted as Acknowledge does, the
/// alert's other open tickets are closed, and the audit log says which PSA closed it.</summary>
public static class PsaTicketClosure
{
    public static readonly AuditActor Actor = AuditActor.ForSystem("PSA ticket sync");

    /// <summary>True when an unresolved alert was resolved. The caller saves.</summary>
    public static async Task<bool> ResolveFromTicketAsync(DotMarcDbContext context, IPsaTicketService ticketService, PsaKind psa, string ticketId, CancellationToken cancellationToken)
    {
        var ticket = await context.AlertTickets.FirstOrDefaultAsync(candidate => candidate.Psa == psa && candidate.TicketId == ticketId && candidate.IsOpen, cancellationToken).ConfigureAwait(false);
        if (ticket is null)
        {
            return false;
        }

        ticket.IsOpen = false;
        var alert = await context.AlertEvents.SingleAsync(candidate => candidate.Id == ticket.AlertEventId, cancellationToken).ConfigureAwait(false);
        if (alert.IsResolved)
        {
            return false;
        }

        alert.IsResolved = true;
        alert.ResolvedUtc = DateTimeOffset.UtcNow;
        await DnsHealthBaselines.AcceptCurrentAsync(context, alert, cancellationToken).ConfigureAwait(false);
        AuditLog.Record(context, Actor, AuditActions.AlertResolvedByTicket, AuditTarget.For(alert), $"Resolved \"{alert.Title}\" for {alert.DomainName}: ticket {ticketId} was closed in {psa.DisplayName()}");
        await ticketService.CloseTicketsAsync(context, alert, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
```

```csharp
// src/DotMarc/Psa/PsaTicketPoller.cs
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotMarc.Psa;

/// <summary>Checks dotMARC's open tickets in each ready PSA: a ticket closed there resolves its alert, a deleted one
/// stops being checked, and a resolved alert's ticket that failed to close is closed again. ConnectWise and Autotask
/// rely on this; for HaloPSA it backs up the webhook. A PSA that keeps failing is asked less often (doubling up to an
/// hour) and logged once per step rather than every cycle.</summary>
public sealed class PsaTicketPoller(
    IDbContextFactory<DotMarcDbContext> dbFactory,
    IEnumerable<IPsaProvider> providers,
    PsaTicketService ticketService,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<PsaTicketPoller> logger) : BackgroundService
{
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(1);

    private readonly IReadOnlyList<IPsaProvider> _providers = providers.ToList();
    private readonly Dictionary<PsaKind, (int Failures, DateTimeOffset NextAttemptUtc)> _backoff = [];

    private TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Psa:PollIntervalMinutes", 5)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Checking PSA tickets failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            var now = timeProvider.GetUtcNow();
            if (_backoff.TryGetValue(provider.Kind, out var waiting) && now < waiting.NextAttemptUtc)
            {
                continue;
            }

            try
            {
                await PollProviderAsync(provider, cancellationToken).ConfigureAwait(false);
                _backoff.Remove(provider.Kind);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failures = _backoff.TryGetValue(provider.Kind, out var previous) ? previous.Failures + 1 : 1;
                var wait = TimeSpan.FromTicks(Math.Min(MaximumBackoff.Ticks, Interval.Ticks * (1L << Math.Min(failures - 1, 10))));
                _backoff[provider.Kind] = (failures, now + wait);
                logger.LogWarning(exception, "Checking {Psa} tickets failed ({Failures} in a row). Trying again in {Minutes} minutes.", provider.Kind.DisplayName(), failures, wait.TotalMinutes);
            }
        }
    }

    private async Task PollProviderAsync(IPsaProvider provider, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!(await provider.GetReadinessAsync(context, cancellationToken).ConfigureAwait(false)).IsReady)
        {
            return;
        }

        var openTickets = await context.AlertTickets
            .Where(ticket => ticket.Psa == provider.Kind && ticket.IsOpen)
            .Join(context.AlertEvents, ticket => ticket.AlertEventId, alert => alert.Id, (ticket, alert) => new { Ticket = ticket, alert.IsResolved })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var open in openTickets)
        {
            if (open.IsResolved)
            {
                var retried = await ticketService.CloseTicketAsync(context, open.Ticket, cancellationToken).ConfigureAwait(false);
                if (retried.Failed > 0)
                {
                    throw new HttpRequestException($"Closing {provider.Kind.DisplayName()} ticket {open.Ticket.TicketId} failed again.");
                }

                continue;
            }

            switch (await provider.GetTicketStateAsync(context, open.Ticket.TicketId, cancellationToken).ConfigureAwait(false))
            {
                case PsaTicketState.Closed:
                    await PsaTicketClosure.ResolveFromTicketAsync(context, ticketService, provider.Kind, open.Ticket.TicketId, cancellationToken).ConfigureAwait(false);
                    break;
                case PsaTicketState.Missing:
                    open.Ticket.IsOpen = false;
                    logger.LogInformation("{Psa} ticket {TicketId} no longer exists, so dotMARC stopped checking it.", provider.Kind.DisplayName(), open.Ticket.TicketId);
                    break;
                default:
                    open.Ticket.LastCheckedUtc = timeProvider.GetUtcNow();
                    break;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

In `Program.cs`:
- Register the poller next to the other hosted services: `builder.Services.AddHostedService<PsaTicketPoller>();` and, if `TimeProvider` isn't registered yet, `builder.Services.TryAddSingleton(TimeProvider.System);`.
- In the Halo webhook, add `IPsaTicketService ticketService` to the handler's parameters and replace the block from `var ticketId = payload.TicketId.ToString();` through the `if (alert is not null) { ... }` with:

```csharp
var resolvedAnAlert = await PsaTicketClosure.ResolveFromTicketAsync(context, ticketService, PsaKind.HaloPsa, payload.TicketId.ToString(CultureInfo.InvariantCulture), request.HttpContext.RequestAborted);
await context.SaveChangesAsync();
```

  and pass `resolvedAnAlert: resolvedAnAlert` to `webhookActivity.Record`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaTicketPollerTests|FullyQualifiedName~HaloWebhookEndpointTests"`
Expected: PASS. Then `dotnet test test/DotMarc.Tests`: all pass. `ProgramDiValidationTests` covers the new registrations.

- [ ] **Step 5: Commit**

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Poll open PSA tickets, and resolve alerts from closed tickets through one shared path"
```

---

### Task 5: Company links on Groups and Domains

**Files:**
- Create: `src/DotMarc/Psa/PsaDirectory.cs`
- Create: `src/DotMarc/Components/Shared/PsaCompanyPicker.razor`
- Modify: `src/DotMarc/Data/GroupManagementService.cs`, `src/DotMarc/Data/DomainManagementService.cs`
- Modify: `src/DotMarc/Audit/AuditActions.cs`
- Modify: `src/DotMarc/Components/Pages/ManageGroups.razor`, `src/DotMarc/Components/Pages/ManageDomains.razor`
- Modify: `src/DotMarc/Program.cs`
- Test: `test/DotMarc.Tests/Data/GroupManagementServiceTests.cs`, `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs`, `test/DotMarc.Tests/Psa/PsaDirectoryTests.cs`

**Interfaces:**
- Consumes: `IPsaProvider`, `PsaCompanySuggestions`, `PsaCompanyLink`.
- Produces:
  - `GroupManagementService.SetPsaCompanyAsync(DotMarcDbContext context, AuditActor actor, int groupId, PsaKind psa, PsaCompany? company, CancellationToken ct = default)`
  - `DomainManagementService.SetPsaCompanyAsync(DotMarcDbContext context, AuditActor actor, int domainId, PsaKind psa, PsaCompany? company, CancellationToken ct = default)`
  - `AuditActions.GroupPsaCompanyChanged = "group.psa_company_changed"` ("Group PSA company changed"), `AuditActions.DomainPsaCompanyChanged = "domain.psa_company_changed"` ("Domain PSA company changed"). The Halo constants and labels stay for old entries.
  - `PsaDirectory(IEnumerable<IPsaProvider> providers, ILogger<PsaDirectory> logger)`, scoped, with `Task<IReadOnlyList<PsaCompanyList>> LoadAsync(DotMarcDbContext context, CancellationToken ct = default)` and `record PsaCompanyList(PsaKind Psa, IReadOnlyList<PsaCompany>? Companies, string? FailureReason)`, one entry per ready PSA in `PsaKind` order. It also refreshes stored link names from the loaded lists.

- [ ] **Step 1: Write the failing service tests**

In `GroupManagementServiceTests.cs`, replace the `SetHaloClientIdAsync` tests with:

```csharp
[Fact]
public async Task SetPsaCompanyAsync_LinksAndRelinksAGroupInOnePsa_AndAuditsEachChange()
{
    await using var context = CreateContext();
    var group = new Group { Name = "Client A" };
    context.Groups.Add(group);
    await context.SaveChangesAsync();

    await GroupManagementService.SetPsaCompanyAsync(context, TestActors.Admin, group.Id, PsaKind.ConnectWise, new PsaCompany("250", "Contoso Ltd"));
    await GroupManagementService.SetPsaCompanyAsync(context, TestActors.Admin, group.Id, PsaKind.ConnectWise, new PsaCompany("251", "Contoso Group"));
    await GroupManagementService.SetPsaCompanyAsync(context, TestActors.Admin, group.Id, PsaKind.HaloPsa, new PsaCompany("7", "Contoso"));

    var links = await context.PsaCompanyLinks.Where(link => link.GroupId == group.Id).OrderBy(link => link.Psa).ToListAsync();
    Assert.Equal([(PsaKind.HaloPsa, "7", "Contoso"), (PsaKind.ConnectWise, "251", "Contoso Group")], links.Select(link => (link.Psa, link.CompanyId, link.CompanyName)));
    var entries = await context.AuditEntries.Where(entry => entry.Action == AuditActions.GroupPsaCompanyChanged).ToListAsync();
    Assert.Equal(3, entries.Count);
}

[Fact]
public async Task SetPsaCompanyAsync_WithNoCompany_RemovesThatPsasLinkOnly()
{
    await using var context = CreateContext();
    var group = new Group { Name = "Client A", PsaCompanyLinks = [new PsaCompanyLink { Psa = PsaKind.HaloPsa, CompanyId = "7", CompanyName = "Contoso" }, new PsaCompanyLink { Psa = PsaKind.ConnectWise, CompanyId = "250", CompanyName = "Contoso Ltd" }] };
    context.Groups.Add(group);
    await context.SaveChangesAsync();

    await GroupManagementService.SetPsaCompanyAsync(context, TestActors.Admin, group.Id, PsaKind.HaloPsa, null);

    Assert.Equal(PsaKind.ConnectWise, (await context.PsaCompanyLinks.SingleAsync()).Psa);
}

[Fact]
public async Task SetPsaCompanyAsync_TheSameCompanyAgain_ChangesAndAuditsNothing()
{
    await using var context = CreateContext();
    var group = new Group { Name = "Client A", PsaCompanyLinks = [new PsaCompanyLink { Psa = PsaKind.HaloPsa, CompanyId = "7", CompanyName = "Contoso" }] };
    context.Groups.Add(group);
    await context.SaveChangesAsync();

    await GroupManagementService.SetPsaCompanyAsync(context, TestActors.Admin, group.Id, PsaKind.HaloPsa, new PsaCompany("7", "Contoso"));

    Assert.Empty(await context.AuditEntries.ToListAsync());
}
```

Add the same three tests to `DomainManagementServiceTests.cs` against `DomainManagementService.SetPsaCompanyAsync`, `DomainId`, and `AuditActions.DomainPsaCompanyChanged`. (Use the actor helper and audit DbSet names the existing tests in those files use.)

```csharp
// test/DotMarc.Tests/Psa/PsaDirectoryTests.cs
[Collection("Postgres")]
public sealed class PsaDirectoryTests : IAsyncLifetime
{
    // Fixture scaffolding as in PsaTicketServiceTests.

    [Fact]
    public async Task LoadsEachReadyPsa_ReportsAFailureByName_AndSkipsOnesNotReady()
    {
        await using var context = CreateContext();
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { Companies = { new PsaCompany("7", "Contoso") } };
        var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { FailWith = new HttpRequestException("refused") };
        var autotask = new FakePsaProvider(PsaKind.Autotask) { Ready = false };

        var lists = await new PsaDirectory([halo, connectWise, autotask], NullLogger<PsaDirectory>.Instance).LoadAsync(context);

        Assert.Equal([PsaKind.HaloPsa, PsaKind.ConnectWise], lists.Select(list => list.Psa));
        Assert.Equal("Contoso", Assert.Single(lists[0].Companies!).Name);
        Assert.Null(lists[1].Companies);
        Assert.Equal("ConnectWise companies couldn't be loaded: refused", lists[1].FailureReason);
    }

    [Fact]
    public async Task RefreshesTheStoredNamesOfLinkedCompanies()
    {
        await using var context = CreateContext();
        context.Groups.Add(new Group { Name = "Client A", PsaCompanyLinks = [new PsaCompanyLink { Psa = PsaKind.HaloPsa, CompanyId = "7", CompanyName = "7" }] });
        await context.SaveChangesAsync();
        var halo = new FakePsaProvider(PsaKind.HaloPsa) { Companies = { new PsaCompany("7", "Contoso") } };

        await new PsaDirectory([halo], NullLogger<PsaDirectory>.Instance).LoadAsync(context);

        Assert.Equal("Contoso", (await context.PsaCompanyLinks.SingleAsync()).CompanyName);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~GroupManagementServiceTests|FullyQualifiedName~DomainManagementServiceTests|FullyQualifiedName~PsaDirectoryTests"`
Expected: build FAIL (`SetPsaCompanyAsync`, `PsaDirectory` missing).

- [ ] **Step 3: Implement the services and directory**

```csharp
// GroupManagementService.cs, replacing SetHaloClientIdAsync
/// <summary>Links (or, with null, unlinks) a Group to a company in one PSA, from that PSA's column on Manage groups.</summary>
public static async Task SetPsaCompanyAsync(DotMarcDbContext context, AuditActor actor, int groupId, PsaKind psa, PsaCompany? company, CancellationToken cancellationToken = default)
{
    var group = await context.Groups.Include(candidate => candidate.PsaCompanyLinks).SingleAsync(candidate => candidate.Id == groupId, cancellationToken).ConfigureAwait(false);
    var existing = group.PsaCompanyLinks.FirstOrDefault(link => link.Psa == psa);
    var changes = new AuditChanges().Field(psa.CompanyLabel(), existing?.CompanyName, company?.Name);
    if (existing?.CompanyId == company?.Id)
    {
        return;
    }

    PsaCompanyLinks.Apply(group.PsaCompanyLinks, existing, psa, company);
    AuditLog.Record(context, actor, AuditActions.GroupPsaCompanyChanged, AuditTarget.For(group), $"Changed the {psa.CompanyLabel()} for group {group.Name}", changes);
    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
}
```

`DomainManagementService.SetPsaCompanyAsync` is the same, over `context.Domains`, with `AuditActions.DomainPsaCompanyChanged` and summary `$"Changed the {psa.CompanyLabel()} for {domain.Name}"`. Both replace their `SetHaloClientIdAsync`.

```csharp
// src/DotMarc/Psa/PsaCompanyLinks.cs
namespace DotMarc.Psa;

public static class PsaCompanyLinks
{
    /// <summary>Points an owner's link for one PSA at a company, adding or removing the link as needed.</summary>
    public static void Apply(List<PsaCompanyLink> links, PsaCompanyLink? existing, PsaKind psa, PsaCompany? company)
    {
        if (company is null)
        {
            if (existing is not null)
            {
                links.Remove(existing);
            }

            return;
        }

        if (existing is null)
        {
            links.Add(new PsaCompanyLink { Psa = psa, CompanyId = company.Id, CompanyName = company.Name });
            return;
        }

        existing.CompanyId = company.Id;
        existing.CompanyName = company.Name;
    }
}
```

Removing from the navigation list deletes the row because the FK is required-by-constraint; if EF instead tries to null the FK, call `context.PsaCompanyLinks.Remove(existing)` in the services as well (the `SetPsaCompanyAsync_WithNoCompany` test catches it).

```csharp
// src/DotMarc/Psa/PsaDirectory.cs
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Psa;

public sealed record PsaCompanyList(PsaKind Psa, IReadOnlyList<PsaCompany>? Companies, string? FailureReason);

/// <summary>Loads the company list of every ready PSA for the pages that link Groups and Domains to companies. A PSA
/// that fails to load is reported by name and the others still load. Loading also refreshes the names stored on links,
/// which is how links copied from the old Halo columns get their real names.</summary>
public sealed class PsaDirectory(IEnumerable<IPsaProvider> providers, ILogger<PsaDirectory> logger)
{
    public async Task<IReadOnlyList<PsaCompanyList>> LoadAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var lists = new List<PsaCompanyList>();
        foreach (var provider in providers.OrderBy(candidate => candidate.Kind))
        {
            if (!(await provider.GetReadinessAsync(context, cancellationToken).ConfigureAwait(false)).IsReady)
            {
                continue;
            }

            try
            {
                var companies = await provider.ListCompaniesAsync(context, cancellationToken).ConfigureAwait(false);
                lists.Add(new PsaCompanyList(provider.Kind, companies, null));
                await RefreshNamesAsync(context, provider.Kind, companies, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Loading {Psa} companies failed", provider.Kind.DisplayName());
                lists.Add(new PsaCompanyList(provider.Kind, null, $"{provider.Kind.DisplayName()} companies couldn't be loaded: {exception.Message}"));
            }
        }

        return lists;
    }

    private static async Task RefreshNamesAsync(DotMarcDbContext context, PsaKind psa, IReadOnlyList<PsaCompany> companies, CancellationToken cancellationToken)
    {
        var namesById = companies.GroupBy(company => company.Id).ToDictionary(sameId => sameId.Key, sameId => sameId.First().Name);
        var links = await context.PsaCompanyLinks.Where(link => link.Psa == psa).ToListAsync(cancellationToken).ConfigureAwait(false);
        var renamed = false;
        foreach (var link in links)
        {
            if (namesById.TryGetValue(link.CompanyId, out var name) && name != link.CompanyName)
            {
                link.CompanyName = name;
                renamed = true;
            }
        }

        if (renamed)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
```

Register in `Program.cs`: `builder.Services.AddScoped<PsaDirectory>();`.

Audit actions: add the two constants and label rows, keeping the Halo ones.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~GroupManagementServiceTests|FullyQualifiedName~DomainManagementServiceTests|FullyQualifiedName~PsaDirectoryTests|FullyQualifiedName~AuditCoverageTests"`
Expected: PASS.

- [ ] **Step 5: Rewrite the Groups and Domains pages around PsaDirectory**

Create a small shared picker so both pages render a company cell the same way:

```razor
@* src/DotMarc/Components/Shared/PsaCompanyPicker.razor *@
@using DotMarc.Psa
@* One PSA's company for a Group or Domain. With the PSA's list loaded it's a searchable picker; when the list failed
   to load it shows the saved name read-only, so a PSA that's down never blanks or changes a link. *@
@if (Companies is null)
{
    <MudText Typo="Typo.body2">@(Current?.CompanyName ?? "None")</MudText>
}
else
{
    <SearchableSelect TValue="string" Options="_options" Value="Current?.CompanyId" ValueChanged="OnChangedAsync"
                      Clearable="true" Placeholder="@Placeholder" />
}

@code {
    [Parameter, EditorRequired] public PsaKind Psa { get; set; }
    [Parameter] public IReadOnlyList<PsaCompany>? Companies { get; set; }
    [Parameter] public PsaCompanyLink? Current { get; set; }
    [Parameter] public string Placeholder { get; set; } = "None";
    [Parameter] public EventCallback<PsaCompany?> CompanyChanged { get; set; }

    private List<SelectOption<string>> _options = [];

    protected override void OnParametersSet() =>
        _options = Companies?.Select(company => new SelectOption<string>(company.Id, company.Name)).ToList() ?? [];

    private Task OnChangedAsync(string? companyId) =>
        CompanyChanged.InvokeAsync(companyId is null ? null : Companies?.FirstOrDefault(company => company.Id == companyId));
}
```

(Check `SearchableSelect`'s parameter names and `SelectOption<T>` in `Components/Shared/SearchableSelect.razor` and adjust if they differ; `SearchableSelect` with `TValue="string"` must accept a null value for "none".)

`ManageGroups.razor`:
- Replace `@inject IHaloPsaClient HaloPsaClient` with `@inject PsaDirectory PsaDirectory`.
- Replace the Halo fields (`_haloClients`, `_haloClientOptions`, `_clientsWithoutGroup`, `_haloLoadFailed`, `_creatingFromHalo`, `_haloConfigured`, `_groupHasHaloClientFilter`) with `List<PsaCompanyList> _psaLists`, `PsaKind? _withoutGroupPsa` (the PSA picked for "Companies without a group", defaulting to the first loaded list), `List<PsaCompany> _companiesWithoutGroup`, `bool _creatingFromPsa`, and `Dictionary<PsaKind, bool?> _hasCompanyFilters`.
- `GroupRow` becomes `GroupRow(int Id, string Name, int DomainCount, IReadOnlyList<PsaCompanyLink> Links)`, loaded with `.Include(group => group.PsaCompanyLinks)`.
- Load: `_psaLists = (await PsaDirectory.LoadAsync(db)).ToList();`.
- For each list with a `FailureReason`, show a `MudAlert Severity="Severity.Warning"` with that reason (replacing the single Halo warning).
- Header and filter cells: one per `_psaLists` entry, labelled `list.Psa.CompanyLabel()`, with the existing "has a client" filter generalised to `_hasCompanyFilters[list.Psa]`.
- Row cell per list: `<PsaCompanyPicker Psa="list.Psa" Companies="list.Companies" Current="@context.Links.FirstOrDefault(link => link.Psa == list.Psa)" CompanyChanged="@(company => SetPsaCompanyAsync(context, list.Psa, company))" />`, and when the row has no link and the list loaded, the existing "Use it" suggestions from `PsaCompanySuggestions.SuggestCompaniesForGroup(context.Name, list.Companies).Take(3)`.
- "Halo clients without a group" becomes "Companies without a group" with a `MudSelect` over the loaded lists' PSAs; `_companiesWithoutGroup = PsaCompanySuggestions.CompaniesWithoutGroup(psa, companies, rows.Select(row => new GroupSummary(row.Name, row.Links.FirstOrDefault(link => link.Psa == psa)?.CompanyId)))`.
- `CreateFromHaloAsync(HaloClient)` becomes `CreateFromCompanyAsync(PsaKind psa, PsaCompany company)`: create the group as today, then `GroupManagementService.SetPsaCompanyAsync(db, actor, newGroupId, psa, company)`, then for each other loaded list whose companies contain one where `PsaCompanySuggestions.LooseKey(other.Name) == PsaCompanySuggestions.LooseKey(company.Name)`, add a snackbar action "Also link to {name} in {PSA}" that calls `SetPsaCompanyAsync` for that PSA. `CreateAllFromHaloAsync` becomes `CreateAllFromCompaniesAsync` over the picked PSA; its dialog text says "{n} groups, one for each {PSA} company listed".
- `SetHaloClientIdAsync(GroupRow, int?)` becomes `SetPsaCompanyAsync(GroupRow row, PsaKind psa, PsaCompany? company)` calling the new service method; its error snackbar says `$"Failed to update {row.Name}'s {psa.CompanyLabel()}. Try again."`.
- The ticket-rules button's "Set a Halo client first" becomes "Link a PSA company first", shown when the row has no links.

`ManageDomains.razor`: the same pattern. Replace the injection and Halo fields, load `_psaLists`, render one column per list headed `$"{list.Psa.CompanyLabel()} override"` with `PsaCompanyPicker` (placeholder "Use Group's"), generalise the client filter to one per PSA, `DomainRow` carries `IReadOnlyList<PsaCompanyLink> Links` (loaded with `.Include(d => d.PsaCompanyLinks)`), and `SetHaloClientIdAsync` becomes `SetPsaCompanyAsync(DomainRow row, PsaKind psa, PsaCompany? company)`.

Remove `DomainManagementService.SetHaloClientIdAsync` and `GroupManagementService.SetHaloClientIdAsync`. `DomainImportService` still calls the domain one: change it to `DomainManagementService.SetPsaCompanyAsync(context, actor, domainId, PsaKind.HaloPsa, target.HaloClientId is { } id ? new PsaCompany(id.ToString(CultureInfo.InvariantCulture), target.HaloClientName ?? id.ToString(CultureInfo.InvariantCulture)) : null, cancellationToken)` for now. If `DomainTarget` has no name, pass the id as the name; Task 6 replaces this.

- [ ] **Step 6: Build, run the full suite and commit**

Run: `dotnet build src/DotMarc` (expect 0 errors) and `dotnet test test/DotMarc.Tests` (expect all pass).

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Link Groups and Domains to a company in each ready PSA"
```

---

### Task 6: Import with a company column per PSA

**Files:**
- Create: `src/DotMarc/DomainImport/PsaImportColumns.cs`
- Modify: `src/DotMarc/DomainImport/ImportTable.cs`, `ImportPlan.cs`, `ImportSnapshot.cs`, `DomainImportPlanner.cs`, `DomainImportService.cs`, `DomainImportSamples.cs`
- Modify: `src/DotMarc/Components/Pages/ImportDomains.razor`
- Test: `test/DotMarc.Tests/DomainImport/DomainImportPlannerTests.cs`, `ImportTableTests.cs` (or wherever header parsing is tested; search for `"haloclient"`)

**Interfaces:**
- Consumes: `PsaDirectory.LoadAsync`, `PsaCompanyList`, `DomainManagementService.SetPsaCompanyAsync`.
- Produces:
  - `PsaImportColumns.All : IReadOnlyList<PsaImportColumn>` with `record PsaImportColumn(PsaKind Psa, ImportColumn Column, ImportNameKind NameKind)`; in this task it holds only `(HaloPsa, ImportColumn.HaloClient, ImportNameKind.HaloClient)`. Tasks 10 and 12 add one entry each.
  - `ImportTableRow.PsaCompanies : IReadOnlyDictionary<PsaKind, string>` replaces `string? HaloClient` (only PSAs with a non-blank cell).
  - `ImportSnapshot.PsaCompanies : IReadOnlyDictionary<PsaKind, PsaCompanyList>` replaces `HaloClients` and `HaloUnavailableReason`; `ImportSnapshotLoader.LoadAsync(context, table, IReadOnlyList<PsaCompanyList> psaCompanies, mxHostsLookup, ct)`.
  - `ExistingDomain.PsaCompanyIds : IReadOnlyDictionary<PsaKind, string>` replaces `int? HaloClientId`.
  - `DomainTarget.PsaCompanies : IReadOnlyDictionary<PsaKind, PsaCompany>` replaces `SetHaloClient` and `HaloClientId`.

- [ ] **Step 1: Write the failing planner tests**

Update every existing planner test that builds a snapshot with Halo clients to pass `PsaCompanies = { [PsaKind.HaloPsa] = new PsaCompanyList(PsaKind.HaloPsa, [new PsaCompany("7", "Contoso")], null) }` (use the test file's existing snapshot helper and change its Halo parameters), and every assertion on `target.HaloClientId` to `target.PsaCompanies[PsaKind.HaloPsa].Id`. Add:

```csharp
[Fact]
public void APsaThatFailedToLoad_HasItsColumnIgnored_WithItsReason()
{
    var table = TableFrom("domain,halo client", "contoso.io,Contoso");
    var snapshot = SnapshotWith(psaCompanies: [new PsaCompanyList(PsaKind.HaloPsa, null, "HaloPSA companies couldn't be loaded: refused")]);

    var plan = Plan(table, snapshot);

    Assert.Contains("HaloPSA companies couldn't be loaded: refused", plan.Notices);
    Assert.Empty(plan.Rows.Single().Target?.PsaCompanies ?? new Dictionary<PsaKind, PsaCompany>());
}

[Fact]
public void APsaThatIsntConnected_HasItsColumnIgnored()
{
    var plan = Plan(TableFrom("domain,halo client", "contoso.io,Contoso"), SnapshotWith(psaCompanies: []));

    Assert.Contains("HaloPSA isn't connected, so the Halo client column was ignored.", plan.Notices);
}
```

Use the helpers the file already has for building a table, a snapshot and a plan; extend the snapshot helper with a `psaCompanies` parameter. Keep the existing tests for the unknown Halo client choices (`LeaveOut`, `MapTo`), now asserting on `PsaCompanies`.

Add to the header-parsing tests:

```csharp
[Fact]
public void HeaderlessInput_KeepsItsColumnOrder()
{
    // Positional columns follow ImportColumn's order. New PSA columns are appended at the end, so a paste without a
    // header that worked before still lands in the same columns.
    var table = ImportTable.FromRows([Row("contoso.io", "Client A", "red", "Contoso", "yes")]);

    var row = table.Rows.Single();
    Assert.Equal("Contoso", row.PsaCompanies[PsaKind.HaloPsa]);
    Assert.True(row.Monitored);
}
```

(Use the row-building helper the file already has.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainImport"`
Expected: build FAIL (`PsaCompanies` missing).

- [ ] **Step 3: Implement**

```csharp
// src/DotMarc/DomainImport/PsaImportColumns.cs
using DotMarc.Psa;

namespace DotMarc.DomainImport;

public sealed record PsaImportColumn(PsaKind Psa, ImportColumn Column, ImportNameKind NameKind);

/// <summary>The import column for each PSA's company. Their ImportColumn members sit at the end of the enum, because
/// headerless input is read in enum order.</summary>
public static class PsaImportColumns
{
    public static IReadOnlyList<PsaImportColumn> All { get; } =
    [
        new(PsaKind.HaloPsa, ImportColumn.HaloClient, ImportNameKind.HaloClient),
    ];

    public static PsaImportColumn? ForNameKind(ImportNameKind kind) => All.FirstOrDefault(column => column.NameKind == kind);
}
```

- `ImportTable`: build `PsaCompanies` with `PsaImportColumns.All.Select(column => (column.Psa, Value: ImportValueParser.Text(Cell(column.Column)))).Where(entry => entry.Value is not null).ToDictionary(entry => entry.Psa, entry => entry.Value!)`.
- `ImportPlan`: `DomainTarget(NameSetChange? Groups, NameSetChange? Tags, IReadOnlyDictionary<PsaKind, PsaCompany> PsaCompanies, bool? Monitored, IReadOnlyList<string>? DkimSelectors, MtaStsTarget? MtaSts)`. `UnknownName`'s comment says "group, tag or PSA company name".
- `ImportSnapshot`: as in Interfaces. `ToExisting` builds `PsaCompanyIds` from `domain.PsaCompanyLinks` (add `.Include(domain => domain.PsaCompanyLinks)` to the loader's query).
- `DomainImportPlanner`:
  - `UsableColumns` holds `IReadOnlySet<PsaKind> PsaCompanies` instead of `bool HaloClient`. The edit-columns label list adds each present PSA column labelled `psa.CompanyLabel()`. For each `PsaImportColumns.All` entry present in `columns` (and `permissions.CanEditDomains`): if `snapshot.PsaCompanies` has no entry for it, add the notice `$"{psa.DisplayName()} isn't connected, so the {psa.CompanyLabel()} column was ignored."`; if its entry has `Companies == null`, add its `FailureReason`; otherwise it's usable.
  - `MergedRow` keeps `Dictionary<PsaKind, string> PsaCompanies`, merged so a later non-blank cell wins per PSA.
  - `NameResolver.Existing(kind)`: for a PSA name kind, `snapshot.PsaCompanies.TryGetValue(column.Psa, out var list) ? list.Companies?.Select(company => company.Name).ToList() ?? [] : []`.
  - The planner's `names.Consider(...)` loop over usable PSA columns replaces the single Halo one.
  - `PlanHaloClient` becomes `PlanPsaCompanies()` returning the dictionary: for each usable PSA with a requested name, resolve it; a left-out name adds the note `$"{psa.CompanyLabel()} \"{requestedName}\" was left out."`; a resolved one finds the `PsaCompany` by name (ordinal ignore case) and records `_changes.Field(psa.CompanyLabel(), CurrentName(psa), company.Name)` where `CurrentName` looks the existing id up in the snapshot list, falling back to `$"{psa.CompanyLabel()} {id}"`.
- `DomainImportService`: `foreach (var (psa, company) in target.PsaCompanies) await DomainManagementService.SetPsaCompanyAsync(context, actor, domainId, psa, company, cancellationToken);`.
- `DomainImportSamples`: unchanged in this task (the `halo client` column stays).
- `ImportDomains.razor`: replace `IHaloPsaClient` with `PsaDirectory`; `LoadHaloClientsAsync` becomes `var psaCompanies = PsaImportColumns.All.Any(column => _table!.Columns.Contains(column.Column)) ? await PsaDirectory.LoadAsync(context) : [];` passed to the loader. The name-kind label switch uses `PsaImportColumns.ForNameKind(kind)?.Psa.CompanyLabel()`; the existing-names switch reads `_snapshot!.PsaCompanies`. The intro text "Halo client" becomes "PSA company".

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainImport"`
Expected: PASS. Then `dotnet test test/DotMarc.Tests`: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Import a company per PSA, with each PSA's column handled on its own"
```

---

### Task 7: The PSA settings page and the shared integration test

**Files:**
- Create: `src/DotMarc/Psa/PsaIntegrationTestService.cs` (replaces `src/DotMarc/Notifications/HaloIntegrationTestService.cs`, deleted)
- Create: `src/DotMarc/Components/Pages/PsaSettings.razor`
- Create: `src/DotMarc/Components/Psa/HaloPsaSettingsTab.razor`
- Create: `src/DotMarc/Components/Psa/PsaIntegrationTestPanel.razor`
- Modify: `src/DotMarc/Components/Pages/AlertsSettings.razor`, `src/DotMarc/Components/Layout/MainLayout.razor`, `src/DotMarc/Program.cs`, `src/DotMarc/Audit/AuditActions.cs` (no new action for Halo; the existing `HaloIntegrationTested` stays)
- Test: rename `test/DotMarc.Tests/Notifications/HaloIntegrationTestServiceTests.cs` to `test/DotMarc.Tests/Psa/PsaIntegrationTestServiceTests.cs`

**Interfaces:**
- Consumes: `IPsaProvider`, `PsaCompanyResolver`, `HaloWebhookActivity`, `HaloPsaProvider.MissingSettings`.
- Produces:
  - `enum PsaTestOutcome { Running, Passed, Warning, Failed }`, `record PsaTestStep(string Name, PsaTestOutcome Outcome, string Detail)`, `record PsaTestRun(PsaKind Psa, IReadOnlyList<PsaTestStep> Steps, bool NeedsCompanyChoice) { bool Succeeded }`.
  - `PsaIntegrationTestService(IDbContextFactory<DotMarcDbContext> dbFactory, IEnumerable<IPsaProvider> providers, HaloWebhookActivity webhooks, ILogger<PsaIntegrationTestService> logger)` with `Task<PsaTestRun> RunAsync(PsaKind psa, string? chosenCompanyId, IProgress<PsaTestStep>? progress, TimeSpan? webhookTimeout, CancellationToken ct)` and `static IReadOnlyList<string> StepNames(PsaKind psa)`.
  - Step names: `SettingsStep = "Check settings"`, `AlertStep = "Pick an alert and company"`, `CreateStep = "Create a ticket"`, `OpenStep = "Read the new ticket"`, `CloseStep = "Close the ticket"`, `ClosedStep = "Read the closed ticket"`, `WebhookStep = "Wait for Halo's webhook"` (HaloPSA only).
  - Route `/psa/settings`, policy `AlertsManage`. `<PsaIntegrationTestPanel Psa="..." />` renders the run for any PSA.

- [ ] **Step 1: Rewrite the integration test service tests**

Rename the file and class; construct the service with `[provider]` lists of `FakePsaProvider` (and the Halo cases with a `FakePsaProvider(PsaKind.HaloPsa)` plus a real `HaloWebhookActivity`). Keep every existing case, translated:
- missing settings fail the first step (for a fake, make `GetReadinessAsync` return `Missing = ["closed status"]`: add an optional `Missing` list property to `FakePsaProvider` used by `GetReadinessAsync`);
- no alert and no chosen company sets `NeedsCompanyChoice`;
- a create failure stops at Create;
- a close failure stops at Close;
- the Halo webhook cases (`ClosedStatus` passes, timeout warns, `OtherStatus` fails, `WrongSecret` fails), recording receipts on the `HaloWebhookActivity` from a background task as today.

Add:

```csharp
[Fact]
public async Task ConnectWise_PassesWhenTheTicketReadsOpenThenClosed_WithNoWebhookStep()
{
    var connectWise = new FakePsaProvider(PsaKind.ConnectWise);

    var run = await CreateService(connectWise).RunAsync(PsaKind.ConnectWise, chosenCompanyId: "250", progress: null, webhookTimeout: null, CancellationToken.None);

    Assert.True(run.Succeeded);
    Assert.Equal(PsaIntegrationTestService.StepNames(PsaKind.ConnectWise), run.Steps.Select(step => step.Name));
    Assert.DoesNotContain(run.Steps, step => step.Name == PsaIntegrationTestService.WebhookStep);
}

[Fact]
public async Task ATicketThatStillReadsOpenAfterClosing_FailsTheLastStep()
{
    var connectWise = new FakePsaProvider(PsaKind.ConnectWise) { IgnoreCloses = true };

    var run = await CreateService(connectWise).RunAsync(PsaKind.ConnectWise, "250", null, null, CancellationToken.None);

    Assert.False(run.Succeeded);
    Assert.Equal(PsaTestOutcome.Failed, run.Steps.Single(step => step.Name == PsaIntegrationTestService.ClosedStep).Outcome);
}

[Fact]
public async Task Halo_WithoutAWebhookSecret_FailsTheSettingsStep()
{
    // The webhook secret isn't needed for ticketing, but the Halo test waits for the webhook, so it needs one.
}
```

Write the third test against a real `HaloPsaSettings` row with every ticketing field set and `WebhookSecret = null`, using a `FakePsaProvider(PsaKind.HaloPsa)` that reports ready; assert the settings step fails with a detail containing "webhook secret". Add `public bool IgnoreCloses { get; set; }` to `FakePsaProvider`; when true, `CloseTicketAsync` records the call but leaves the state Open.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaIntegrationTestServiceTests"`
Expected: build FAIL.

- [ ] **Step 3: Implement the service**

Port `HaloIntegrationTestService` to `src/DotMarc/Psa/PsaIntegrationTestService.cs`, keeping its structure and its `Report`/`Finish` helpers, with these changes:

```csharp
public sealed record PsaTestRun(PsaKind Psa, IReadOnlyList<PsaTestStep> Steps, bool NeedsCompanyChoice)
{
    /// <summary>True only when every step ran and none failed. For HaloPSA the webhook step must have passed: a timeout
    /// is only a warning there and must never read as success.</summary>
    public bool Succeeded =>
        Steps.Count == PsaIntegrationTestService.StepNames(Psa).Count
        && Steps.All(step => step.Outcome is PsaTestOutcome.Passed or PsaTestOutcome.Warning)
        && (Psa != PsaKind.HaloPsa || Steps.Single(step => step.Name == PsaIntegrationTestService.WebhookStep).Outcome == PsaTestOutcome.Passed);
}

public static IReadOnlyList<string> StepNames(PsaKind psa) => psa == PsaKind.HaloPsa
    ? [SettingsStep, AlertStep, CreateStep, OpenStep, CloseStep, ClosedStep, WebhookStep]
    : [SettingsStep, AlertStep, CreateStep, OpenStep, CloseStep, ClosedStep];
```

1. **Settings:** `provider.GetReadinessAsync`; for HaloPSA also add "webhook secret" to the missing list when `HaloPsaSettings.WebhookSecret` is blank. Missing fails with "Not saved yet: ... Save {PSA} settings first, since this test uses the saved settings."; not Enabled warns "...{PSA} tickets are switched off, so real alerts won't create tickets until you turn them on."
2. **Alert and company:** the latest alert's domain, loaded with `PsaCompanyResolver.IncludeLinks`, resolved with `PsaCompanyResolver.Resolve(domain, psa)?.CompanyId`; the chosen company wins; messages say "{psa.CompanyLabel()}" where they said "Halo client", and "#{id}" stays.
3. **Create:** `provider.CreateTicketAsync(db, new PsaTicketRequest(companyId, domainName, alertType, $"[dotMARC test] {title}", $"{message}\n\nThis ticket was created by dotMARC's integration test and is closed automatically straight afterwards."))`.
4. **Read the new ticket:** `GetTicketStateAsync`; Open passes; Closed or Missing fails with "Ticket #{id} was created but reads as {state}. Check the {PSA} closed status setting isn't the status new tickets start in."
5. **Close:** `CloseTicketAsync(db, ticketId, "Closed by dotMARC's integration test.")`; on failure keep Halo's assignment advice when `psa == PsaKind.HaloPsa` and the message mentions "assign", otherwise "Check the closed status, and close the ticket by hand in {PSA}."
6. **Read the closed ticket:** Closed passes ("dotMARC sees ticket #{id} as closed, so a tech closing a real alert's ticket resolves the alert within the poll interval."); Open fails ("...still reads as open after closing. Check the closed status matches the one {PSA} uses for closed tickets."); Missing warns.
7. **Webhook (HaloPSA only):** exactly today's step 5, unchanged.

Audit stays where it is today (the page records `HaloIntegrationTested` after a Halo run; Tasks 10 and 12 add the other PSAs' actions).

Register: replace `builder.Services.AddTransient<HaloIntegrationTestService>();` with `builder.Services.AddTransient<PsaIntegrationTestService>();`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaIntegrationTestServiceTests"`
Expected: PASS.

- [ ] **Step 5: Move the Halo settings onto the PSA settings page**

`PsaSettings.razor`:

```razor
@page "/psa/settings"
@attribute [Authorize(Policy = "AlertsManage")]
@using DotMarc.Psa

<PageTitle>PSA settings - dotMARC</PageTitle>

<MudContainer MaxWidth="MaxWidth.Large" Class="mt-4">
    <MudText Typo="Typo.h4">PSA settings</MudText>
    <MudText Typo="Typo.body2" Class="mb-4">
        Turn on any PSA dotMARC should raise tickets in. You can use more than one: each Group or Domain links to a
        company in each, and an alert raises a ticket in every PSA its domain is linked in. Which alert types raise
        tickets is set under <MudLink Href="/alerts/settings">Alert settings</MudLink>.
    </MudText>
    <MudTabs Elevation="1" Rounded="true" PanelClass="pa-4" @bind-ActivePanelIndex="_activeTab">
        <MudTabPanel Text="HaloPSA" Icon="@TabIcon(_haloEnabled)"><HaloPsaSettingsTab EnabledChanged="@(enabled => _haloEnabled = enabled)" /></MudTabPanel>
    </MudTabs>
</MudContainer>

@code {
    private int _activeTab;
    private bool _haloEnabled;

    /// <summary>A tick on the tab of each PSA that is switched on.</summary>
    private static string? TabIcon(bool enabled) => enabled ? Icons.Material.Filled.CheckCircle : null;
}
```

`HaloPsaSettingsTab.razor`: move these parts of `AlertsSettings.razor` into it unchanged, apart from the renames below:
- the markup from `<MudText Typo="Typo.h5">PSA integration (HaloPSA)</MudText>` (line 144) up to, but not including, the "Which alerts create tickets" heading (line 259);
- the "Test the integration" section from line 281 to the end of that section, replaced by `<PsaIntegrationTestPanel Psa="PsaKind.HaloPsa" />` (the panel holds the run logic, see below);
- the `@code` members for Halo: `_haloSettings`, `_newHaloClientSecret`, the option lists, `LoadHaloOptionsAsync`, `ClearHaloSignInAsync`, `LoadHaloAgentsAsync`, `ShowHaloError`, `DescribeWebhookCall`, `CopyWebhookUrlAsync`, `SaveHaloSettingsAsync`, `_haloSyncSaved`, and the injections they use (`IHaloPsaClient`, `ISecretStore`, `HaloPsaTokenCache`, `HaloWebhookActivity`, `NavigationManager`, `IJSRuntime`, `ISnackbar`, the db factory and `AuditActorAccessor`).

It loads the settings in `OnInitializedAsync` (the Halo half of `AlertsSettings`'s current load) and raises `[Parameter] public EventCallback<bool> EnabledChanged` after loading and after saving. The heading becomes "Connection". The "Save PSA settings" button text becomes "Save HaloPSA settings".

`PsaIntegrationTestPanel.razor`: the run UI taken from `AlertsSettings` (`RunHaloTestAsync`, `LoadTestClientsAsync`, `StepIcon`, `StepColor`, the step list and the company choice), generalised:
- `[Parameter, EditorRequired] public PsaKind Psa`;
- injects `PsaIntegrationTestService`, `IEnumerable<IPsaProvider>`, the db factory and `AuditRecorder`;
- the company choice loads `provider.ListCompaniesAsync` for `Psa` into `List<PsaCompany>` and binds `string? _testCompanyId`;
- calls `RunAsync(Psa, _testCompanyId, progress, null, ct)`;
- after a run, records the audit action for the PSA: `PsaKind.HaloPsa => AuditActions.HaloIntegrationTested` (Tasks 10 and 12 add the others), using the existing recorder call the Halo test uses today, with the summary `$"Tested the {Psa.DisplayName()} integration: {(run.Succeeded ? "passed" : "did not pass")}"`.

`AlertsSettings.razor`: remove what moved. Keep "Which alerts create tickets"; its intro text becomes "These apply to every PSA. A Group can override them for its own domains." Add above it: `<MudAlert Severity="Severity.Info" Dense="true">PSA connections have moved to <MudLink Href="/psa/settings">PSA settings</MudLink>.</MudAlert>`. Remove the now-unused injections and `@using`s.

`MainLayout.razor`: after the "Alert settings" menu item add `<MudMenuItem Href="/psa/settings" Icon="@Icons.Material.Filled.ConfirmationNumber">PSA settings</MudMenuItem>` inside the same `AlertsManage` authorization block.

Bookmarks: `/alerts/settings#halo` lands on Alert settings, which shows the "moved" notice with the link, so no redirect code is needed. (Ruling: a fragment never reaches the server, so a server redirect can't see it; the notice does the job.)

The webhook URL shown on the Halo tab is built the same way as before; check the copy still points at `/integrations/halopsa/webhook/{secret}`.

- [ ] **Step 6: Build, run the suite, check in the browser, commit**

Run: `dotnet build src/DotMarc` (0 errors), `dotnet test test/DotMarc.Tests` (all pass).

Browser check with playwright-edge against a demo run (`dotnet run --project src/DotMarc` with `Demo:Enabled=true`, port from the launch output): open `/psa/settings`, confirm the HaloPSA tab shows the connection fields, ticket defaults and the test panel; open `/alerts/settings`, confirm the moved notice and the ticket rules. Stop only the process you started, by its process ID.

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Move HaloPSA onto a PSA settings page, with an integration test any PSA can run"
```

---

### Task 8: Drop the old Halo columns, show ticket links on alerts, demo PSA data

**Files:**
- Modify: `src/DotMarc/Data/Group.cs`, `src/DotMarc/Data/Domain.cs`, `src/DotMarc/Notifications/AlertEvent.cs` (remove old properties)
- Create: migration `RemoveHaloColumns` (generated, then checked)
- Modify: `src/DotMarc/Components/Pages/Alerts.razor` (ticket chips)
- Create: `src/DotMarc/Demo/DemoPsaProvider.cs`
- Modify: `src/DotMarc/Program.cs` (demo registration)
- Test: `test/DotMarc.Tests/Psa/PsaDataMigrationTests.cs` (still passes), `test/DotMarc.Tests/Demo/DemoPsaProviderTests.cs`

**Interfaces:**
- Consumes: `IPsaProvider.GetTicketUrlTemplateAsync`, `AlertEvent.Tickets`.
- Produces: `DemoPsaProvider(PsaKind kind, TimeProvider timeProvider) : IPsaProvider`, always ready, with five sample companies per PSA, tickets numbered from 5000, and a ticket reading Closed three minutes after it was created.

- [ ] **Step 1: Write the failing demo provider test**

```csharp
// test/DotMarc.Tests/Demo/DemoPsaProviderTests.cs
using DotMarc.Demo;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Demo;

public sealed class DemoPsaProviderTests
{
    [Fact]
    public async Task ADemoTicket_ReadsOpen_ThenClosedAfterThreeMinutes()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var provider = new DemoPsaProvider(PsaKind.ConnectWise, clock);

        var ticketId = await provider.CreateTicketAsync(null!, new PsaTicketRequest("1", "contoso.io", "MissedReport", "t", "m"));
        Assert.Equal(PsaTicketState.Open, await provider.GetTicketStateAsync(null!, ticketId));

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(PsaTicketState.Closed, await provider.GetTicketStateAsync(null!, ticketId));
        Assert.Equal(PsaTicketState.Missing, await provider.GetTicketStateAsync(null!, "nope"));
        Assert.Equal(5, (await provider.ListCompaniesAsync(null!)).Count);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DemoPsaProviderTests"`
Expected: build FAIL (`DemoPsaProvider` missing).

- [ ] **Step 3: Implement the demo provider**

```csharp
// src/DotMarc/Demo/DemoPsaProvider.cs
using System.Collections.Concurrent;
using System.Globalization;
using DotMarc.Data;
using DotMarc.Psa;

namespace DotMarc.Demo;

/// <summary>A pretend PSA for the demo instance: always connected, a few sample companies, and tickets that "a tech"
/// closes three minutes after they're raised, so visitors see the poller resolve an alert. Nothing leaves the process.</summary>
public sealed class DemoPsaProvider(PsaKind kind, TimeProvider timeProvider) : IPsaProvider
{
    private static readonly TimeSpan TimeToClose = TimeSpan.FromMinutes(3);
    private static readonly string[] CompanyNames = ["Contoso Ltd", "Fabrikam Inc", "Northwind Traders", "Tailspin Toys", "Woodgrove Bank"];

    private readonly ConcurrentDictionary<string, DateTimeOffset> _createdUtc = new();
    private readonly ConcurrentDictionary<string, bool> _closed = new();
    private int _nextTicketNumber = 5000;

    public PsaKind Kind { get; } = kind;

    public Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PsaReadiness(true, []));

    public Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PsaCompany>>(CompanyNames.Select((name, index) => new PsaCompany((index + 1).ToString(CultureInfo.InvariantCulture), name)).ToList());

    public Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        var ticketId = Interlocked.Increment(ref _nextTicketNumber).ToString(CultureInfo.InvariantCulture);
        _createdUtc[ticketId] = timeProvider.GetUtcNow();
        return Task.FromResult(ticketId);
    }

    public Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default)
    {
        if (!_createdUtc.TryGetValue(ticketId, out var createdUtc))
        {
            return Task.FromResult(PsaTicketState.Missing);
        }

        var closed = _closed.ContainsKey(ticketId) || timeProvider.GetUtcNow() - createdUtc >= TimeToClose;
        return Task.FromResult(closed ? PsaTicketState.Closed : PsaTicketState.Open);
    }

    public Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        _closed[ticketId] = true;
        return Task.CompletedTask;
    }

    public Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
```

(`Interlocked.Increment` returns the incremented value, so the first ticket is 5001; change the test's expectation if it asserts a number. It doesn't.)

In `Program.cs`, register providers by mode:

```csharp
if (demoOptions.Enabled)
{
    builder.Services.AddSingleton<IPsaProvider>(services => new DotMarc.Demo.DemoPsaProvider(PsaKind.HaloPsa, services.GetRequiredService<TimeProvider>()));
}
else
{
    builder.Services.AddSingleton<IPsaProvider, HaloPsaProvider>();
}
```

(replacing the unconditional Halo registration from Task 3). In demo mode the Halo settings tab still edits the real (demo database) settings row, which is harmless. Tasks 10 and 12 add the other PSAs to both branches.

- [ ] **Step 4: Remove the old columns**

Delete `Group.HaloClientId`, `Domain.HaloClientId` (and its comment), `AlertEvent.ExternalTicketProvider` and `AlertEvent.ExternalTicketId`. Fix any remaining references the compiler reports (there should be none outside migrations). Then:

Run: `dotnet ef migrations add RemoveHaloColumns --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

Check the generated `Up` only drops the four columns. Replace the generated `Down` (which re-adds empty columns) with one that re-adds them and copies HaloPSA's data back:

```csharp
// Re-adds the old columns and copies HaloPSA's links and tickets back. ConnectWise and Autotask data has nowhere to go
// in the old shape, so it stays only in the shared tables (which the previous migration's Down drops).
migrationBuilder.Sql(@"
    UPDATE ""Groups"" SET ""HaloClientId"" = link.""CompanyId""::int FROM ""PsaCompanyLinks"" link
    WHERE link.""GroupId"" = ""Groups"".""Id"" AND link.""Psa"" = 'HaloPsa';
    UPDATE ""Domains"" SET ""HaloClientId"" = link.""CompanyId""::int FROM ""PsaCompanyLinks"" link
    WHERE link.""DomainId"" = ""Domains"".""Id"" AND link.""Psa"" = 'HaloPsa';
    UPDATE ""AlertEvents"" SET ""ExternalTicketProvider"" = 'HaloPSA', ""ExternalTicketId"" = ticket.""TicketId"" FROM ""AlertTickets"" ticket
    WHERE ticket.""AlertEventId"" = ""AlertEvents"".""Id"" AND ticket.""Psa"" = 'HaloPsa';");
```

placed after the generated `AddColumn` calls. Add the class summary: `/// <summary>Drops HaloPSA's old client and ticket columns, now that the shared PSA tables hold them. Hand-edited Down copies Halo's data back.</summary>`.

- [ ] **Step 5: Ticket chips on the Alerts page**

In `Alerts.razor`:
- Load alerts with `.Include(alert => alert.Tickets)`.
- Inject `IEnumerable<IPsaProvider> PsaProviders`; on load, build `Dictionary<PsaKind, string?> _ticketUrlTemplates` from each provider's `GetTicketUrlTemplateAsync`.
- Add a "Tickets" column after the existing status column:

```razor
<MudTd DataLabel="Tickets">
    @foreach (var ticket in context.Tickets.OrderBy(ticket => ticket.Psa))
    {
        var label = $"{ticket.Psa.DisplayName()} #{ticket.TicketId}";
        @if (_ticketUrlTemplates.GetValueOrDefault(ticket.Psa) is { } template)
        {
            <MudChip T="string" Size="Size.Small" Variant="@(ticket.IsOpen ? Variant.Filled : Variant.Outlined)"
                     Href="@string.Format(CultureInfo.InvariantCulture, template, Uri.EscapeDataString(ticket.TicketId))" Target="_blank">@label</MudChip>
        }
        else
        {
            <MudChip T="string" Size="Size.Small" Variant="@(ticket.IsOpen ? Variant.Filled : Variant.Outlined)">@label</MudChip>
        }
    }
</MudTd>
```

  and the matching `<MudTh>Tickets</MudTh>`. A filled chip is an open ticket, an outlined chip a closed one; add `title` text saying so (`title="@(ticket.IsOpen ? "Open" : "Closed")"`).

- [ ] **Step 6: Run everything and commit**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DemoPsaProviderTests|FullyQualifiedName~PsaDataMigrationTests"` (PASS), then `dotnet test test/DotMarc.Tests` (all pass).

Browser check (playwright-edge, demo): Groups page shows a "Halo client" column fed by the five demo companies; link a Group; trigger or find an alert for a domain in it and confirm a "HaloPSA #500n" chip; after three minutes plus a poll, the alert resolves and the chip turns outlined. Stop only your own process, by its ID.

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Drop HaloPSA's old columns, show PSA tickets on alerts, and give the demo a pretend PSA"
```

---

## Phase 2: ConnectWise

### Task 9: ConnectWise settings, client and provider

**Files:**
- Create: `src/DotMarc/Psa/ConnectWise/ConnectWiseSettings.cs`, `ConnectWiseSettingsService.cs`, `IConnectWiseClient.cs`, `ConnectWiseClient.cs`, `ConnectWiseProvider.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs` (DbSet, `HasData` seed), `src/DotMarc/Audit/AuditActions.cs`, `src/DotMarc/Program.cs`, `src/DotMarc/ServerLogs/LogRedactor.cs`
- Create: migration `AddConnectWiseSettings`
- Modify: `test/DotMarc.Tests/Audit/AuditCoverageTests.cs` (add the service)
- Test: `test/DotMarc.Tests/Psa/ConnectWise/ConnectWiseClientTests.cs`, `ConnectWiseSettingsServiceTests.cs`, `ConnectWiseProviderTests.cs`, `test/DotMarc.Tests/ServerLogs/LogRedactorTests.cs` (or the existing redactor test file)

**Interfaces:**
- Consumes: `IPsaProvider`, `PsaCompany`, `PsaOption`, `PsaTicketRequest`, `PsaTicketState`, `PsaReadiness`, `ISecretStore`.
- Produces:

```csharp
public sealed class ConnectWiseSettings
{
    public const string PrivateKeySecretKey = "ConnectWise.PrivateKey";
    /// <summary>dotMARC's registered ConnectWise developer clientId. Empty until registered.</summary>
    public const string DefaultClientId = "";
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string? SiteUrl { get; set; }          // host only, for example api-eu.myconnectwise.net
    public string? CompanyId { get; set; }        // the login company
    public string? PublicKey { get; set; }
    public bool PrivateKeyConfigured { get; set; }
    public string? ClientIdOverride { get; set; }
    public int? BoardId { get; set; }  public string? BoardName { get; set; }
    public int? StatusId { get; set; } public string? StatusName { get; set; }      // new tickets start in this status
    public int? TypeId { get; set; }   public string? TypeName { get; set; }        // optional
    public int? PriorityId { get; set; } public string? PriorityName { get; set; }
    public int? ClosedStatusId { get; set; } public string? ClosedStatusName { get; set; }
    public string? EffectiveClientId => string.IsNullOrWhiteSpace(ClientIdOverride) ? (DefaultClientId.Length > 0 ? DefaultClientId : null) : ClientIdOverride.Trim();
}

public interface IConnectWiseClient
{
    Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(ConnectWiseSettings settings, CancellationToken ct = default);
    Task<IReadOnlyList<PsaOption>> ListBoardsAsync(ConnectWiseSettings settings, CancellationToken ct = default);
    Task<IReadOnlyList<PsaOption>> ListBoardStatusesAsync(ConnectWiseSettings settings, int boardId, CancellationToken ct = default);
    Task<IReadOnlyList<PsaOption>> ListBoardTypesAsync(ConnectWiseSettings settings, int boardId, CancellationToken ct = default);
    Task<IReadOnlyList<PsaOption>> ListPrioritiesAsync(ConnectWiseSettings settings, CancellationToken ct = default);
    Task<string> CreateTicketAsync(ConnectWiseSettings settings, PsaTicketRequest request, CancellationToken ct = default);
    /// <summary>Null when ConnectWise has no such ticket.</summary>
    Task<ConnectWiseTicket?> GetTicketAsync(ConnectWiseSettings settings, string ticketId, CancellationToken ct = default);
    Task CloseTicketAsync(ConnectWiseSettings settings, string ticketId, string note, CancellationToken ct = default);
}

public sealed record ConnectWiseTicket(bool ClosedFlag, int? StatusId);

// ConnectWiseSettingsService.GetAsync(context, ct)
// ConnectWiseSettingsService.SaveAsync(context, actor, secretStore, ConnectWiseSettings updated, string? newPrivateKey, ct)
// ConnectWiseSettingsService.NormalizeSiteUrl(string?) : string?
// ConnectWiseProvider(IConnectWiseClient client) : IPsaProvider; static MissingSettings(ConnectWiseSettings) : IReadOnlyList<string>
// AuditActions.ConnectWiseSettingsSaved = "settings.connectwise.saved" ("ConnectWise settings saved")
// AuditActions.ConnectWiseIntegrationTested = "connectwise.integration_tested" ("ConnectWise integration tested")
```

- [ ] **Step 1: Write the failing client tests**

```csharp
// test/DotMarc.Tests/Psa/ConnectWise/ConnectWiseClientTests.cs
using System.Net;
using System.Text;
using DotMarc.Psa;
using DotMarc.Psa.ConnectWise;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Psa.ConnectWise;

public sealed class ConnectWiseClientTests
{
    private static readonly ConnectWiseSettings Settings = new()
    {
        SiteUrl = "api-eu.myconnectwise.net", CompanyId = "contoso", PublicKey = "public", PrivateKeyConfigured = true,
        ClientIdOverride = "client-123", BoardId = 1, StatusId = 16, TypeId = 4, PriorityId = 8, ClosedStatusId = 20,
    };

    private static (ConnectWiseClient Client, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        var secrets = new FakeSecretStore { Secrets = { [ConnectWiseSettings.PrivateKeySecretKey] = "private" } };
        return (new ConnectWiseClient(new HttpClient(handler), secrets), handler);
    }

    [Fact]
    public async Task EveryRequest_SignsInWithTheCompanyAndKeys_AndSendsTheClientId()
    {
        var (client, handler) = Create();
        handler.ResponseBody = "[]";

        await client.ListPrioritiesAsync(Settings);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api-eu.myconnectwise.net/v4_6_release/apis/3.0/service/priorities?pageSize=1000", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("contoso+public:private", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
        Assert.Equal("client-123", Assert.Single(request.Headers.GetValues("clientId")));
    }

    [Fact]
    public async Task WithNoClientId_ItRefusesBeforeSendingAnything()
    {
        var (client, handler) = Create();
        var withoutClientId = new ConnectWiseSettings { SiteUrl = "api-eu.myconnectwise.net", CompanyId = "contoso", PublicKey = "public" };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListPrioritiesAsync(withoutClientId));

        Assert.Contains("client ID", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Companies_ArePagedUntilAShortPage_AndSortedByName()
    {
        var (client, handler) = Create();
        var fullPage = "[" + string.Join(",", Enumerable.Range(1, 1000).Select(number => $"{{\"id\":{number},\"name\":\"Company {number:0000}\"}}")) + "]";
        handler.ResponseBodies.Enqueue(fullPage);
        handler.ResponseBodies.Enqueue("[{\"id\":1001,\"name\":\"Aardvark Ltd\"}]");

        var companies = await client.ListCompaniesAsync(Settings);

        Assert.Equal(1001, companies.Count);
        Assert.Equal(new PsaCompany("1001", "Aardvark Ltd"), companies[0]);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("conditions=deletedFlag%3Dfalse", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("page=2", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task CreateTicket_PostsTheTicketWithTheSavedDefaults()
    {
        var (client, handler) = Create();
        handler.ResponseBody = "{\"id\":4321}";

        var ticketId = await client.CreateTicketAsync(Settings, new PsaTicketRequest("250", "contoso.io", "MissedReport", new string('x', 150), "Reports stopped"));

        Assert.Equal("4321", ticketId);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.EndsWith("/service/tickets", handler.Requests[0].RequestUri!.AbsolutePath);
        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]);
        var root = body.RootElement;
        Assert.Equal(100, root.GetProperty("summary").GetString()!.Length);
        Assert.Contains("Domain: contoso.io", root.GetProperty("initialDescription").GetString());
        Assert.Equal(250, root.GetProperty("company").GetProperty("id").GetInt32());
        Assert.Equal(1, root.GetProperty("board").GetProperty("id").GetInt32());
        Assert.Equal(16, root.GetProperty("status").GetProperty("id").GetInt32());
        Assert.Equal(4, root.GetProperty("type").GetProperty("id").GetInt32());
        Assert.Equal(8, root.GetProperty("priority").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task CreateTicket_WithNoType_LeavesTypeOut()
    {
        var (client, handler) = Create();
        handler.ResponseBody = "{\"id\":1}";
        var withoutType = new ConnectWiseSettings
        {
            SiteUrl = Settings.SiteUrl, CompanyId = Settings.CompanyId, PublicKey = Settings.PublicKey, ClientIdOverride = Settings.ClientIdOverride,
            BoardId = 1, StatusId = 16, PriorityId = 8, ClosedStatusId = 20,
        };

        await client.CreateTicketAsync(withoutType, new PsaTicketRequest("250", "contoso.io", "MissedReport", "t", "m"));

        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.False(body.RootElement.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task GetTicket_ReadsTheClosedFlagAndStatus_AndAMissingTicketIsNull()
    {
        var (client, handler) = Create();
        handler.ResponseBodies.Enqueue("{\"id\":4321,\"closedFlag\":true,\"status\":{\"id\":20,\"name\":\">Closed\"}}");
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.NotFound);

        Assert.Equal(new ConnectWiseTicket(true, 20), await client.GetTicketAsync(Settings, "4321"));
        Assert.Null(await client.GetTicketAsync(Settings, "9999"));
    }

    [Fact]
    public async Task CloseTicket_AddsAnInternalNote_ThenPatchesTheStatus()
    {
        var (client, handler) = Create();
        handler.ResponseBody = "{}";

        await client.CloseTicketAsync(Settings, "4321", "Resolved automatically by dotMARC.");

        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/service/tickets/4321/notes", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"internalAnalysisFlag\":true", handler.RequestBodies[0]);
        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.Equal("[{\"op\":\"replace\",\"path\":\"status\",\"value\":{\"id\":20}}]", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task ARefusedSignIn_SaysWhatToCheck()
    {
        var (client, handler) = Create();
        handler.StatusCode = HttpStatusCode.Unauthorized;
        handler.ResponseBody = "{\"message\":\"Invalid credentials\"}";

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListPrioritiesAsync(Settings));

        Assert.StartsWith("ConnectWise refused the sign-in: check the company ID, public key and private key.", exception.Message);
        Assert.DoesNotContain("private", exception.Message.Replace("private key", ""));
    }
}
```

Board lists get their own small tests in the same file: `ListBoardsAsync` requests `service/boards?conditions=inactiveFlag%3Dfalse&pageSize=1000` and maps `[{"id":1,"name":"Help Desk"}]` to `PsaOption(1, "Help Desk")`; `ListBoardStatusesAsync(settings, 1)` requests `service/boards/1/statuses?pageSize=1000`; `ListBoardTypesAsync(settings, 1)` requests `service/boards/1/types?pageSize=1000`.

- [ ] **Step 2: Write the failing settings service and provider tests**

```csharp
// test/DotMarc.Tests/Psa/ConnectWise/ConnectWiseSettingsServiceTests.cs
[Collection("Postgres")]
public sealed class ConnectWiseSettingsServiceTests : IAsyncLifetime
{
    // Fixture scaffolding as in HaloPsaSettingsServiceTests.

    private static ConnectWiseSettings Complete(Action<ConnectWiseSettings>? change = null)
    {
        var settings = new ConnectWiseSettings
        {
            Enabled = true, SiteUrl = "api-eu.myconnectwise.net", CompanyId = "contoso", PublicKey = "public",
            ClientIdOverride = "client-123", BoardId = 1, StatusId = 16, PriorityId = 8, ClosedStatusId = 20,
        };
        change?.Invoke(settings);
        return settings;
    }

    [Fact]
    public async Task Save_StoresThePrivateKeyInTheSecretStore_AndAuditsItAsASecret()
    {
        await using var context = CreateContext();
        var secrets = new FakeSecretStore();

        await ConnectWiseSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(), "private");

        Assert.Equal("private", secrets.Secrets[ConnectWiseSettings.PrivateKeySecretKey]);
        Assert.True((await ConnectWiseSettingsService.GetAsync(context)).PrivateKeyConfigured);
        var entry = await context.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.ConnectWiseSettingsSaved, entry.Action);
        Assert.DoesNotContain("private", entry.ChangesJson ?? "");
    }

    [Theory]
    [InlineData("https://api-eu.myconnectwise.net/", "api-eu.myconnectwise.net")]
    [InlineData("  api-na.myconnectwise.net  ", "api-na.myconnectwise.net")]
    [InlineData("http://cw.contoso.com/v4_6_release/apis/3.0", "cw.contoso.com")]
    public async Task Save_KeepsOnlyTheSiteHost(string entered, string saved)
    {
        await using var context = CreateContext();

        await ConnectWiseSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), Complete(settings => settings.SiteUrl = entered), "private");

        Assert.Equal(saved, (await ConnectWiseSettingsService.GetAsync(context)).SiteUrl);
    }

    [Fact]
    public async Task Save_TurnedOnWithNoClientId_IsRefused()
    {
        await using var context = CreateContext();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            ConnectWiseSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), Complete(settings => settings.ClientIdOverride = null), "private"));

        Assert.Contains("client ID", exception.Message);
    }
}
```

(Use the audit entity's real property name for its changes column; check `AuditEntry` in `src/DotMarc/Audit`.)

```csharp
// test/DotMarc.Tests/Psa/ConnectWise/ConnectWiseProviderTests.cs
// Postgres-backed like HaloPsaProviderTests. A FakeConnectWiseClient returns a fixed ConnectWiseTicket? from GetTicketAsync.
[Theory]
[InlineData(true, 5, PsaTicketState.Closed)]    // closedFlag wins whatever the status
[InlineData(false, 20, PsaTicketState.Closed)]  // the configured closed status
[InlineData(false, 16, PsaTicketState.Open)]
public async Task TicketState_UsesTheClosedFlagOrTheClosedStatus(bool closedFlag, int statusId, PsaTicketState expected)

[Fact] public async Task TicketState_AMissingTicket_IsMissing()
[Fact] public async Task Readiness_ListsWhatIsMissing()   // expect ["site URL", "company ID", "public key", "private key", "client ID", "board", "new ticket status", "priority", "closed status"] for an enabled empty row
[Fact] public async Task TicketUrl_UsesTheWebHost()      // SiteUrl "api-eu.myconnectwise.net" gives "https://eu.myconnectwise.net/v4_6_release/ConnectWise.aspx?routeTo=ServiceFV&recid={0}"
```

Write each test body in full in the file, following `HaloPsaProviderTests`: save a `ConnectWiseSettings` row (Task 9's migration seeds it with Id 1), build the provider over the fake client, and assert.

Redactor test (in the existing `LogRedactor` tests):

```csharp
[Theory]
[InlineData("Authorization: Basic Y29udG9zbytwdWJsaWM6cHJpdmF0ZQ==", "Authorization: Basic [redacted]")]
[InlineData("clientId: 0f0e-1234", "clientId: [redacted]")]
[InlineData("ApiIntegrationCode: ABCDEF", "ApiIntegrationCode: [redacted]")]
[InlineData("Secret: hunter2", "Secret: [redacted]")]
public void RedactsPsaCredentials(string text, string expected) => Assert.Equal(expected, LogRedactor.Redact(text));
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ConnectWise|FullyQualifiedName~LogRedactor"`
Expected: build FAIL.

- [ ] **Step 4: Implement settings, service and migration**

Write `ConnectWiseSettings` as in Interfaces (with a class summary like `HaloPsaSettings`'s). Add `public DbSet<ConnectWiseSettings> ConnectWiseSettings => Set<ConnectWiseSettings>();` and `modelBuilder.Entity<ConnectWiseSettings>().HasData(new ConnectWiseSettings { Id = 1 });` beside the Halo seed.

```csharp
// src/DotMarc/Psa/ConnectWise/ConnectWiseSettingsService.cs
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa.ConnectWise;

/// <summary>Reads and saves the single ConnectWise settings row. The private key goes to the secret store and is never
/// on the row; <see cref="ConnectWiseSettings.PrivateKeyConfigured"/> says whether one is saved.</summary>
public static class ConnectWiseSettingsService
{
    public static Task<ConnectWiseSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.ConnectWiseSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, ConnectWiseSettings updated, string? newPrivateKey, CancellationToken cancellationToken = default)
    {
        updated.SiteUrl = NormalizeSiteUrl(updated.SiteUrl);
        if (updated.Enabled && updated.EffectiveClientId is null)
        {
            throw new ArgumentException("ConnectWise needs a client ID. Enter the one from developer.connectwise.com under Client ID.", nameof(updated));
        }

        var saved = await context.ConnectWiseSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewPrivateKey = !string.IsNullOrWhiteSpace(newPrivateKey);
        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Site", saved.SiteUrl, updated.SiteUrl)
            .Field("Company ID", saved.CompanyId, updated.CompanyId)
            .Field("Public key", saved.PublicKey, updated.PublicKey)
            .Field("Client ID override", saved.ClientIdOverride, updated.ClientIdOverride)
            .Field("Board", Describe(saved.BoardId, saved.BoardName), Describe(updated.BoardId, updated.BoardName))
            .Field("New ticket status", Describe(saved.StatusId, saved.StatusName), Describe(updated.StatusId, updated.StatusName))
            .Field("Type", Describe(saved.TypeId, saved.TypeName), Describe(updated.TypeId, updated.TypeName))
            .Field("Priority", Describe(saved.PriorityId, saved.PriorityName), Describe(updated.PriorityId, updated.PriorityName))
            .Field("Closed status", Describe(saved.ClosedStatusId, saved.ClosedStatusName), Describe(updated.ClosedStatusId, updated.ClosedStatusName))
            .Secret("Private key", hasNewPrivateKey);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.ConnectWiseSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.Enabled = updated.Enabled;
        existing.SiteUrl = updated.SiteUrl;
        existing.CompanyId = updated.CompanyId?.Trim();
        existing.PublicKey = updated.PublicKey?.Trim();
        existing.ClientIdOverride = string.IsNullOrWhiteSpace(updated.ClientIdOverride) ? null : updated.ClientIdOverride.Trim();
        (existing.BoardId, existing.BoardName) = (updated.BoardId, updated.BoardName);
        (existing.StatusId, existing.StatusName) = (updated.StatusId, updated.StatusName);
        (existing.TypeId, existing.TypeName) = (updated.TypeId, updated.TypeName);
        (existing.PriorityId, existing.PriorityName) = (updated.PriorityId, updated.PriorityName);
        (existing.ClosedStatusId, existing.ClosedStatusName) = (updated.ClosedStatusId, updated.ClosedStatusName);

        if (hasNewPrivateKey)
        {
            await secretStore.SetSecretAsync(ConnectWiseSettings.PrivateKeySecretKey, newPrivateKey!.Trim(), cancellationToken).ConfigureAwait(false);
            existing.PrivateKeyConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.ConnectWiseSettingsSaved, AuditTarget.Settings("ConnectWise"), "Saved ConnectWise settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Just the host: people paste the full URL from their browser or the API docs.</summary>
    public static string? NormalizeSiteUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return Uri.TryCreate(trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed, UriKind.Absolute, out var uri)
            ? uri.Host
            : trimmed;
    }

    private static string? Describe(int? id, string? name) => id is null ? null : name ?? id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
```

Add the two audit actions with labels. Add `typeof(ConnectWiseSettingsService)` to `AuditCoverageTests`'s service list.

Run: `dotnet ef migrations add AddConnectWiseSettings --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj` and check it creates the table and inserts row 1.

- [ ] **Step 5: Implement the client**

```csharp
// src/DotMarc/Psa/ConnectWise/ConnectWiseClient.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Notifications;

namespace DotMarc.Psa.ConnectWise;

/// <summary>ConnectWise PSA (Manage) REST API, version 3.0. Every call signs in with Basic auth as
/// "company+publicKey:privateKey" and carries the developer clientId header ConnectWise requires.</summary>
public sealed class ConnectWiseClient(HttpClient httpClient, ISecretStore secretStore) : IConnectWiseClient
{
    private const int PageSize = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(ConnectWiseSettings settings, CancellationToken cancellationToken = default)
    {
        var companies = new List<PsaCompany>();
        for (var page = 1; ; page++)
        {
            var entries = await GetListAsync<IdName>(settings, $"company/companies?conditions={Uri.EscapeDataString("deletedFlag=false")}&fields=id,name&pageSize={PageSize}&page={page}", cancellationToken).ConfigureAwait(false);
            companies.AddRange(entries.Select(entry => new PsaCompany(entry.Id.ToString(CultureInfo.InvariantCulture), entry.Name)));
            if (entries.Count < PageSize)
            {
                break;
            }
        }

        return companies.OrderBy(company => company.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<IReadOnlyList<PsaOption>> ListBoardsAsync(ConnectWiseSettings settings, CancellationToken cancellationToken = default) =>
        ListOptionsAsync(settings, $"service/boards?conditions={Uri.EscapeDataString("inactiveFlag=false")}&pageSize={PageSize}", cancellationToken);

    public Task<IReadOnlyList<PsaOption>> ListBoardStatusesAsync(ConnectWiseSettings settings, int boardId, CancellationToken cancellationToken = default) =>
        ListOptionsAsync(settings, $"service/boards/{boardId}/statuses?pageSize={PageSize}", cancellationToken);

    public Task<IReadOnlyList<PsaOption>> ListBoardTypesAsync(ConnectWiseSettings settings, int boardId, CancellationToken cancellationToken = default) =>
        ListOptionsAsync(settings, $"service/boards/{boardId}/types?pageSize={PageSize}", cancellationToken);

    public Task<IReadOnlyList<PsaOption>> ListPrioritiesAsync(ConnectWiseSettings settings, CancellationToken cancellationToken = default) =>
        ListOptionsAsync(settings, $"service/priorities?pageSize={PageSize}", cancellationToken);

    public async Task<string> CreateTicketAsync(ConnectWiseSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        var ticket = new CreateTicketBody(
            Truncate(request.Title, 100),
            request.Body,
            new Reference(int.Parse(request.CompanyId, CultureInfo.InvariantCulture)),
            new Reference(settings.BoardId!.Value),
            new Reference(settings.StatusId!.Value),
            settings.TypeId is { } typeId ? new Reference(typeId) : null,
            new Reference(settings.PriorityId!.Value));
        using var response = await SendAsync(settings, HttpMethod.Post, "service/tickets", JsonContent.Create(ticket, options: JsonOptions), cancellationToken).ConfigureAwait(false);
        var created = await response.Content.ReadFromJsonAsync<IdOnly>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("ConnectWise created a ticket but didn't say its id.");
        return created.Id.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<ConnectWiseTicket?> GetTicketAsync(ConnectWiseSettings settings, string ticketId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(settings, HttpMethod.Get, $"service/tickets/{Uri.EscapeDataString(ticketId)}?fields=id,closedFlag,status", null, cancellationToken).ConfigureAwait(false);
            var ticket = await response.Content.ReadFromJsonAsync<TicketState>(JsonOptions, cancellationToken).ConfigureAwait(false);
            return ticket is null ? null : new ConnectWiseTicket(ticket.ClosedFlag, ticket.Status?.Id);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task CloseTicketAsync(ConnectWiseSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        var escapedId = Uri.EscapeDataString(ticketId);
        using (await SendAsync(settings, HttpMethod.Post, $"service/tickets/{escapedId}/notes", JsonContent.Create(new NoteBody(note, true), options: JsonOptions), cancellationToken).ConfigureAwait(false))
        {
        }

        var patch = new[] { new PatchOperation("replace", "status", new Reference(settings.ClosedStatusId!.Value)) };
        using (await SendAsync(settings, HttpMethod.Patch, $"service/tickets/{escapedId}", JsonContent.Create(patch, options: JsonOptions), cancellationToken).ConfigureAwait(false))
        {
        }
    }

    private async Task<IReadOnlyList<PsaOption>> ListOptionsAsync(ConnectWiseSettings settings, string path, CancellationToken cancellationToken)
    {
        var entries = await GetListAsync<IdName>(settings, path, cancellationToken).ConfigureAwait(false);
        return entries.Select(entry => new PsaOption(entry.Id, entry.Name)).OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<T>> GetListAsync<T>(ConnectWiseSettings settings, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(settings, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
    }

    private async Task<HttpResponseMessage> SendAsync(ConnectWiseSettings settings, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var clientId = settings.EffectiveClientId ?? throw new InvalidOperationException("ConnectWise needs a client ID before dotMARC can call it.");
        var privateKey = await secretStore.GetSecretAsync(ConnectWiseSettings.PrivateKeySecretKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The ConnectWise private key isn't saved.");

        using var request = new HttpRequestMessage(method, $"https://{settings.SiteUrl}/v4_6_release/apis/3.0/{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.CompanyId}+{settings.PublicKey}:{privateKey}")));
        request.Headers.Add("clientId", clientId);
        var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = response.StatusCode;
        response.Dispose();
        var advice = status switch
        {
            HttpStatusCode.Unauthorized => "ConnectWise refused the sign-in: check the company ID, public key and private key.",
            HttpStatusCode.Forbidden => "ConnectWise refused the request: check the API member's security role allows it.",
            _ => $"ConnectWise returned {(int)status} for {method} {path.Split('?')[0]}."
        };
        throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? advice : $"{advice} ConnectWise said: {Truncate(body, 300)}", null, status);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private sealed record IdName(int Id, string Name);
    private sealed record IdOnly(int Id);
    private sealed record Reference(int Id);
    private sealed record TicketState(int Id, bool ClosedFlag, Reference? Status);
    private sealed record NoteBody(string Text, bool InternalAnalysisFlag);
    private sealed record PatchOperation(string Op, string Path, object Value);
    private sealed record CreateTicketBody(
        string Summary,
        string InitialDescription,
        Reference Company,
        Reference Board,
        Reference Status,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Reference? Type,
        Reference Priority);
}
```

(Check the "says nothing secret" assertion in `ARefusedSignIn_SaysWhatToCheck`: the message never includes the key itself. `PatchOperation.Value` is `object` so the serializer writes the `Reference` shape; if it serializes as `{}`, change the field type to `Reference`.)

Register: `builder.Services.AddHttpClient<IConnectWiseClient, ConnectWiseClient>();`.

- [ ] **Step 6: Implement the provider**

```csharp
// src/DotMarc/Psa/ConnectWise/ConnectWiseProvider.cs
using DotMarc.Data;

namespace DotMarc.Psa.ConnectWise;

public sealed class ConnectWiseProvider(IConnectWiseClient client) : IPsaProvider
{
    public PsaKind Kind => PsaKind.ConnectWise;

    public async Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await ConnectWiseSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return new PsaReadiness(settings.Enabled, MissingSettings(settings));
    }

    public static IReadOnlyList<string> MissingSettings(ConnectWiseSettings settings)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.SiteUrl)) missing.Add("site URL");
        if (string.IsNullOrWhiteSpace(settings.CompanyId)) missing.Add("company ID");
        if (string.IsNullOrWhiteSpace(settings.PublicKey)) missing.Add("public key");
        if (!settings.PrivateKeyConfigured) missing.Add("private key");
        if (settings.EffectiveClientId is null) missing.Add("client ID");
        if (settings.BoardId is null) missing.Add("board");
        if (settings.StatusId is null) missing.Add("new ticket status");
        if (settings.PriorityId is null) missing.Add("priority");
        if (settings.ClosedStatusId is null) missing.Add("closed status");
        return missing;
    }

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await client.ListCompaniesAsync(await ConnectWiseSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default) =>
        await client.CreateTicketAsync(await ConnectWiseSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), request, cancellationToken).ConfigureAwait(false);

    public async Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default)
    {
        var settings = await ConnectWiseSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var ticket = await client.GetTicketAsync(settings, ticketId, cancellationToken).ConfigureAwait(false);
        if (ticket is null)
        {
            return PsaTicketState.Missing;
        }

        return ticket.ClosedFlag || ticket.StatusId == settings.ClosedStatusId ? PsaTicketState.Closed : PsaTicketState.Open;
    }

    public async Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default) =>
        await client.CloseTicketAsync(await ConnectWiseSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), ticketId, note, cancellationToken).ConfigureAwait(false);

    /// <summary>The web app is on the API host without its "api-" prefix (api-eu.myconnectwise.net serves
    /// eu.myconnectwise.net). Not confirmed against a live tenant.</summary>
    public async Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await ConnectWiseSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.SiteUrl))
        {
            return null;
        }

        var webHost = settings.SiteUrl.StartsWith("api-", StringComparison.OrdinalIgnoreCase) ? settings.SiteUrl["api-".Length..] : settings.SiteUrl;
        return $"https://{webHost}/v4_6_release/ConnectWise.aspx?routeTo=ServiceFV&recid={{0}}";
    }
}
```

Register in the non-demo branch: `builder.Services.AddSingleton<IPsaProvider, ConnectWiseProvider>();`.

`LogRedactor`: add

```csharp
[GeneratedRegex(@"Basic\s+[A-Za-z0-9+/]+=*", RegexOptions.IgnoreCase)]
private static partial Regex BasicCredentials();

[GeneratedRegex(@"\b(clientId|ApiIntegrationCode|Secret|UserName)(\s*:\s*)[^\s,;""']+", RegexOptions.IgnoreCase)]
private static partial Regex PsaCredentialHeader();
```

applied in `Redact` as `text = BasicCredentials().Replace(text, "Basic " + Mask);` and `text = PsaCredentialHeader().Replace(text, "$1$2" + Mask);`. Update the class summary to mention PSA credential headers.

- [ ] **Step 7: Run the tests to verify they pass, then commit**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ConnectWise|FullyQualifiedName~LogRedactor|FullyQualifiedName~AuditCoverageTests"` (PASS), then `dotnet test test/DotMarc.Tests` (all pass).

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Add ConnectWise ticketing: settings, API client and provider"
```

---

### Task 10: ConnectWise settings tab, import column, demo and docs

**Files:**
- Create: `src/DotMarc/Components/Psa/ConnectWiseSettingsTab.razor`
- Modify: `src/DotMarc/Components/Pages/PsaSettings.razor`, `src/DotMarc/Components/Psa/PsaIntegrationTestPanel.razor` (audit action)
- Modify: `src/DotMarc/DomainImport/ImportTable.cs` (enum and header names), `ImportPlan.cs` (`ImportNameKind`), `PsaImportColumns.cs`, `DomainImportSamples.cs`
- Modify: `src/DotMarc/Program.cs` (demo branch)
- Create: `website/docs/psa/connectwise.mdx`
- Test: `test/DotMarc.Tests/DomainImport/` header and planner tests

**Interfaces:**
- Consumes: Task 9's settings service, client and audit actions; Task 6's `PsaImportColumns`.
- Produces: `ImportColumn.ConnectWiseCompany` (appended after `MtaStsMaxAge`), `ImportNameKind.ConnectWiseCompany`, header names `connectwisecompany` and `connectwise`.

- [ ] **Step 1: Write the failing import tests**

```csharp
[Fact]
public void TheConnectWiseCompanyColumn_IsReadByEitherHeaderName()
{
    foreach (var header in new[] { "connectwise company", "ConnectWise" })
    {
        var table = TableFrom($"domain,{header}", "contoso.io,Contoso Ltd");

        Assert.Equal("Contoso Ltd", table.Rows.Single().PsaCompanies[PsaKind.ConnectWise]);
    }
}

[Fact]
public void HeaderlessInput_StillReadsTheOriginalColumnsInOrder_WithTheConnectWiseColumnLast()
{
    var table = ImportTable.FromRows([Row("contoso.io", "Client A", "red", "Contoso", "yes", "s1", "testing", "mx.contoso.io", "86400", "Contoso Ltd")]);

    var row = table.Rows.Single();
    Assert.Equal("Contoso", row.PsaCompanies[PsaKind.HaloPsa]);
    Assert.Equal("Contoso Ltd", row.PsaCompanies[PsaKind.ConnectWise]);
}

[Fact]
public void AConnectWiseCompany_IsMatchedAgainstConnectWisesList_NotHalos()
{
    var snapshot = SnapshotWith(psaCompanies:
    [
        new PsaCompanyList(PsaKind.HaloPsa, [new PsaCompany("7", "Contoso")], null),
        new PsaCompanyList(PsaKind.ConnectWise, [new PsaCompany("250", "Contoso Ltd")], null),
    ]);

    var plan = Plan(TableFrom("domain,halo client,connectwise company", "contoso.io,Contoso,Contoso Ltd"), snapshot);

    var target = plan.Rows.Single().Target!;
    Assert.Equal("7", target.PsaCompanies[PsaKind.HaloPsa].Id);
    Assert.Equal("250", target.PsaCompanies[PsaKind.ConnectWise].Id);
}
```

(The headerless row's values must match the parsers for each original column; use values the existing headerless tests already use.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainImport"`
Expected: FAIL (`PsaKind.ConnectWise` key not found).

- [ ] **Step 3: Implement the import column**

- `ImportColumn`: append `ConnectWiseCompany` after `MtaStsMaxAge`, with a comment on the enum: `// Headerless input is read in this order, so new columns go at the end.`
- `ImportTable.HeaderNames`: `["connectwisecompany"] = ImportColumn.ConnectWiseCompany, ["connectwise"] = ImportColumn.ConnectWiseCompany,`.
- `ImportNameKind`: add `ConnectWiseCompany`.
- `PsaImportColumns.All`: add `new(PsaKind.ConnectWise, ImportColumn.ConnectWiseCompany, ImportNameKind.ConnectWiseCompany),`.
- `DomainImportSamples`: add `"connectwise company"` to the header row after the last column, and a value in each sample row (use "Contoso Ltd" style names matching the Halo sample's).

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainImport"`. Expected: PASS.

- [ ] **Step 4: The ConnectWise tab**

`ConnectWiseSettingsTab.razor`, laid out like `HaloPsaSettingsTab` with `FieldWithHelp` on every field:
- **Connection:** Enabled switch; Site (`SiteUrl`, help: "Your ConnectWise API host, such as api-eu.myconnectwise.net or api-na.myconnectwise.net. Pasting the full URL is fine; only the host is kept."); Company ID (help: "The company you sign in to ConnectWise with."); Public key; Private key (password field bound to `_newPrivateKey`, placeholder "Saved" when `PrivateKeyConfigured`, help: "From the API member's API Keys tab. dotMARC keeps it in its secret store and never shows it again."); Client ID override (help: "dotMARC sends its own ConnectWise client ID. Enter yours from developer.connectwise.com only if you want to use your own." When `ConnectWiseSettings.DefaultClientId` is empty, the help instead says "Required. Create a client ID at developer.connectwise.com and enter it here." and the field is marked required).
- **Ticket defaults:** a "Load from ConnectWise" button (enabled once the connection fields and key are saved) that calls `ListBoardsAsync` and `ListPrioritiesAsync`; Board select; on choosing a board, load `ListBoardStatusesAsync` and `ListBoardTypesAsync`; New ticket status, Type (optional, with a "None" choice), Priority, Closed status. Each select stores its id and name (like Halo's `HaloOptionNames`). Load failures show in a `MudAlert` with the exception message.
- **Save:** calls `ConnectWiseSettingsService.SaveAsync`; an `ArgumentException` shows its message as an error snackbar.
- **Test:** `<PsaIntegrationTestPanel Psa="PsaKind.ConnectWise" />`.
- Raises `EnabledChanged` like the Halo tab.

`PsaSettings.razor`: add `<MudTabPanel Text="ConnectWise" Icon="@TabIcon(_connectWiseEnabled)"><ConnectWiseSettingsTab EnabledChanged="@(enabled => _connectWiseEnabled = enabled)" /></MudTabPanel>`.

`PsaIntegrationTestPanel.razor`: the action switch gains `PsaKind.ConnectWise => AuditActions.ConnectWiseIntegrationTested`.

`Program.cs` demo branch: add `builder.Services.AddSingleton<IPsaProvider>(services => new DotMarc.Demo.DemoPsaProvider(PsaKind.ConnectWise, services.GetRequiredService<TimeProvider>()));`.

- [ ] **Step 5: Docs**

Create `website/docs/psa/connectwise.mdx` (front matter `title: ConnectWise`, `sidebar_position: 2`), covering, in plain prose without em dashes:
1. **Create an API member:** System > Members > API Members, with a security role that can read companies and service boards and add and update service tickets and notes; generate an API key pair on its API Keys tab.
2. **Client ID:** dotMARC sends its own; to use yours, register at developer.connectwise.com and enter it under Client ID override. (Until dotMARC's own is registered, say it's required.)
3. **Connect:** PSA settings > ConnectWise: site, company ID, public key, private key; save; Load from ConnectWise; pick board, new ticket status, type, priority, closed status; turn it on; save.
4. **Link companies:** Manage groups has a ConnectWise company column, and Companies without a group can create Groups from ConnectWise companies. Domains can override.
5. **How closing works:** dotMARC checks open tickets every few minutes (`Psa:PollIntervalMinutes`, default 5); a ticket closed in ConnectWise (closed flag, or your closed status) resolves its alert; when the alert resolves first, dotMARC adds an internal note and sets your closed status.
6. **Test integration:** what each step proves.

Task 13 turns `psa-integration.mdx` into the overview that links here.

- [ ] **Step 6: Build, test, browser check, commit**

Run: `dotnet build src/DotMarc`, `dotnet test test/DotMarc.Tests` (all pass). Browser check (playwright-edge, demo): the ConnectWise tab renders; Groups shows a ConnectWise company column next to the Halo client column; "Companies without a group" switches between the two. Stop only your own process.

```bash
git add -A src/DotMarc test/DotMarc.Tests website/docs
git commit -m "Add the ConnectWise settings tab, import column, demo data and docs"
```

---

## Phase 3: Autotask

### Task 11: Autotask settings, client and provider

**Files:**
- Create: `src/DotMarc/Psa/Autotask/AutotaskSettings.cs`, `AutotaskSettingsService.cs`, `AutotaskZoneCache.cs`, `IAutotaskClient.cs`, `AutotaskClient.cs`, `AutotaskProvider.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`, `src/DotMarc/Audit/AuditActions.cs`, `src/DotMarc/Program.cs`
- Create: migration `AddAutotaskSettings`
- Modify: `test/DotMarc.Tests/Audit/AuditCoverageTests.cs`
- Test: `test/DotMarc.Tests/Psa/Autotask/AutotaskClientTests.cs`, `AutotaskSettingsServiceTests.cs`, `AutotaskProviderTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class AutotaskSettings
{
    public const string SecretStoreKey = "Autotask.Secret";
    public const int CompleteStatus = 5;
    public const int NewStatus = 1;
    /// <summary>dotMARC's registered Autotask API tracking identifier. Empty until registered.</summary>
    public const string DefaultIntegrationCode = "";
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string? Username { get; set; }
    public bool SecretConfigured { get; set; }
    public string? IntegrationCodeOverride { get; set; }
    public int? QueueId { get; set; } public string? QueueName { get; set; }
    public int? TicketTypeId { get; set; } public string? TicketTypeName { get; set; }
    public int? IssueTypeId { get; set; } public string? IssueTypeName { get; set; }   // optional
    public int? PriorityId { get; set; } public string? PriorityName { get; set; }
    public int? ClosedStatusId { get; set; } = CompleteStatus; public string? ClosedStatusName { get; set; } = "Complete";
    public string? EffectiveIntegrationCode => string.IsNullOrWhiteSpace(IntegrationCodeOverride) ? (DefaultIntegrationCode.Length > 0 ? DefaultIntegrationCode : null) : IntegrationCodeOverride.Trim();
}

public sealed record AutotaskZone(string ApiUrl, string WebUrl);   // ApiUrl ends ".../ATServicesRest/V1.0/", WebUrl ends "/"
public sealed class AutotaskZoneCache   // singleton
{
    public bool TryGet(string username, out AutotaskZone zone);
    public void Set(string username, AutotaskZone zone);
    public void Clear();
}

public sealed record AutotaskTicketPicklists(IReadOnlyList<PsaOption> Queues, IReadOnlyList<PsaOption> TicketTypes, IReadOnlyList<PsaOption> IssueTypes, IReadOnlyList<PsaOption> Priorities, IReadOnlyList<PsaOption> Statuses);

public interface IAutotaskClient
{
    Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(AutotaskSettings settings, CancellationToken ct = default);
    Task<AutotaskTicketPicklists> GetTicketPicklistsAsync(AutotaskSettings settings, CancellationToken ct = default);
    Task<string> CreateTicketAsync(AutotaskSettings settings, PsaTicketRequest request, CancellationToken ct = default);
    /// <summary>Null when Autotask has no such ticket.</summary>
    Task<int?> GetTicketStatusAsync(AutotaskSettings settings, string ticketId, CancellationToken ct = default);
    Task CloseTicketAsync(AutotaskSettings settings, string ticketId, string note, CancellationToken ct = default);
}

// AutotaskClient(HttpClient httpClient, ISecretStore secretStore, AutotaskZoneCache zones, TimeProvider timeProvider)
// AutotaskSettingsService.GetAsync / SaveAsync(context, actor, secretStore, AutotaskSettings updated, string? newSecret, ct)
// AutotaskProvider(IAutotaskClient client, AutotaskZoneCache zones) : IPsaProvider; static MissingSettings(AutotaskSettings)
// AuditActions.AutotaskSettingsSaved = "settings.autotask.saved" ("Autotask settings saved")
// AuditActions.AutotaskIntegrationTested = "autotask.integration_tested" ("Autotask integration tested")
// AuditActions.AutotaskZoneCleared = "autotask.zone_cleared" ("Autotask zone cleared")
```

- [ ] **Step 1: Write the failing client tests**

```csharp
// test/DotMarc.Tests/Psa/Autotask/AutotaskClientTests.cs
using System.Net;
using System.Text.Json;
using DotMarc.Psa;
using DotMarc.Psa.Autotask;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Psa.Autotask;

public sealed class AutotaskClientTests
{
    private const string ZoneResponse = "{\"zoneName\":\"Pre-Release\",\"url\":\"https://webservices2.autotask.net/ATServicesRest/\",\"webUrl\":\"https://ww2.autotask.net/\",\"ci\":0}";

    private static readonly AutotaskSettings Settings = new()
    {
        Username = "api@contoso.com", SecretConfigured = true, IntegrationCodeOverride = "TRACKING", QueueId = 29683354,
        TicketTypeId = 1, IssueTypeId = 12, PriorityId = 2, ClosedStatusId = AutotaskSettings.CompleteStatus,
    };

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

    private (AutotaskClient Client, FakeHttpMessageHandler Handler, AutotaskZoneCache Zones) Create()
    {
        var handler = new FakeHttpMessageHandler();
        var secrets = new FakeSecretStore { Secrets = { [AutotaskSettings.SecretStoreKey] = "s3cret" } };
        var zones = new AutotaskZoneCache();
        return (new AutotaskClient(new HttpClient(handler), secrets, zones, _clock), handler, zones);
    }

    [Fact]
    public async Task TheFirstCall_LooksUpTheZone_ThenUsesItWithTheThreeHeaders()
    {
        var (client, handler, zones) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("{\"item\":{\"id\":7,\"status\":1}}");

        await client.GetTicketStatusAsync(Settings, "7");

        Assert.Equal("https://webservices.autotask.net/atservicesrest/v1.0/zoneInformation?user=api%40contoso.com", handler.Requests[0].RequestUri!.ToString());
        var ticketRequest = handler.Requests[1];
        Assert.Equal("https://webservices2.autotask.net/ATServicesRest/V1.0/Tickets/7", ticketRequest.RequestUri!.ToString());
        Assert.Equal("TRACKING", Assert.Single(ticketRequest.Headers.GetValues("ApiIntegrationCode")));
        Assert.Equal("api@contoso.com", Assert.Single(ticketRequest.Headers.GetValues("UserName")));
        Assert.Equal("s3cret", Assert.Single(ticketRequest.Headers.GetValues("Secret")));
        Assert.True(zones.TryGet("api@contoso.com", out var zone));
        Assert.Equal("https://ww2.autotask.net/", zone.WebUrl);
    }

    [Fact]
    public async Task TheZone_IsLookedUpOnlyOnce()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("{\"item\":{\"id\":7,\"status\":1}}");
        handler.ResponseBodies.Enqueue("{\"item\":{\"id\":8,\"status\":5}}");

        await client.GetTicketStatusAsync(Settings, "7");
        Assert.Equal(5, await client.GetTicketStatusAsync(Settings, "8"));

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ARefusedSignIn_ForgetsTheZone_SoTheNextCallLooksItUpAgain()
    {
        var (client, handler, zones) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.StatusCodes.Enqueue(HttpStatusCode.OK);
        handler.StatusCodes.Enqueue(HttpStatusCode.Unauthorized);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetTicketStatusAsync(Settings, "7"));

        Assert.StartsWith("Autotask refused the sign-in: check the username, secret and API tracking identifier.", exception.Message);
        Assert.False(zones.TryGet("api@contoso.com", out _));
    }

    [Fact]
    public async Task AnUnknownUsername_SaysSo()
    {
        var (client, handler, _) = Create();
        handler.StatusCode = HttpStatusCode.NotFound;

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetTicketStatusAsync(Settings, "7"));

        Assert.Equal("Autotask couldn't find a zone for that username. Check it's the API user's username.", exception.Message);
    }

    [Fact]
    public async Task AMissingTicket_IsNull()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("{\"item\":null}");

        Assert.Null(await client.GetTicketStatusAsync(Settings, "7"));
    }

    [Fact]
    public async Task Companies_FollowNextPageUrl_AndAreSortedByName()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("{\"items\":[{\"id\":30,\"companyName\":\"Woodgrove Bank\"}],\"pageDetails\":{\"nextPageUrl\":\"https://webservices2.autotask.net/ATServicesRest/V1.0/Companies/query/next?paging=abc\"}}");
        handler.ResponseBodies.Enqueue("{\"items\":[{\"id\":31,\"companyName\":\"Contoso Ltd\"}],\"pageDetails\":{\"nextPageUrl\":null}}");

        var companies = await client.ListCompaniesAsync(Settings);

        Assert.Equal([new PsaCompany("31", "Contoso Ltd"), new PsaCompany("30", "Woodgrove Bank")], companies);
        Assert.Contains("Companies/query?search=", handler.Requests[1].RequestUri!.ToString());
        Assert.Contains("isActive", Uri.UnescapeDataString(handler.Requests[1].RequestUri!.Query));
        Assert.Equal("https://webservices2.autotask.net/ATServicesRest/V1.0/Companies/query/next?paging=abc", handler.Requests[2].RequestUri!.ToString());
    }

    [Fact]
    public async Task CreateTicket_PostsTheTicket_WithADueDateADayAway()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("{\"itemId\":9001}");

        var ticketId = await client.CreateTicketAsync(Settings, new PsaTicketRequest("31", "contoso.io", "MissedReport", "Reports stopped", "No reports for 3 days"));

        Assert.Equal("9001", ticketId);
        using var body = JsonDocument.Parse(handler.RequestBodies[1]);
        var root = body.RootElement;
        Assert.Equal(31, root.GetProperty("companyID").GetInt32());
        Assert.Equal("Reports stopped", root.GetProperty("title").GetString());
        Assert.Contains("Domain: contoso.io", root.GetProperty("description").GetString());
        Assert.Equal(29683354, root.GetProperty("queueID").GetInt32());
        Assert.Equal(1, root.GetProperty("ticketType").GetInt32());
        Assert.Equal(12, root.GetProperty("issueType").GetInt32());
        Assert.Equal(2, root.GetProperty("priority").GetInt32());
        Assert.Equal(AutotaskSettings.NewStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(_clock.GetUtcNow().AddDays(1), root.GetProperty("dueDateTime").GetDateTimeOffset());
    }

    [Fact]
    public async Task CloseTicket_PatchesTheStatusAndResolution()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("{\"itemId\":9001}");

        await client.CloseTicketAsync(Settings, "9001", "Resolved automatically by dotMARC.");

        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.EndsWith("/V1.0/Tickets", handler.Requests[1].RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal(9001, body.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(5, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Resolved automatically by dotMARC.", body.RootElement.GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task Picklists_AreReadFromTheTicketFieldInfo_ActiveValuesOnly()
    {
        var (client, handler, _) = Create();
        handler.ResponseBodies.Enqueue(ZoneResponse);
        handler.ResponseBodies.Enqueue("""
            {"fields":[
              {"name":"queueID","isPickList":true,"picklistValues":[{"value":"29683354","label":"Monitoring","isActive":true},{"value":"1","label":"Old","isActive":false}]},
              {"name":"ticketType","isPickList":true,"picklistValues":[{"value":"1","label":"Service Request","isActive":true}]},
              {"name":"issueType","isPickList":true,"picklistValues":[{"value":"12","label":"Email","isActive":true}]},
              {"name":"priority","isPickList":true,"picklistValues":[{"value":"2","label":"Medium","isActive":true}]},
              {"name":"status","isPickList":true,"picklistValues":[{"value":"1","label":"New","isActive":true},{"value":"5","label":"Complete","isActive":true}]},
              {"name":"title","isPickList":false,"picklistValues":null}
            ]}
            """);

        var picklists = await client.GetTicketPicklistsAsync(Settings);

        Assert.Equal([new PsaOption(29683354, "Monitoring")], picklists.Queues);
        Assert.Equal([new PsaOption(5, "Complete"), new PsaOption(1, "New")], picklists.Statuses);
        Assert.EndsWith("/V1.0/Tickets/entityInformation/fields", handler.Requests[1].RequestUri!.AbsolutePath);
    }
}
```

(`FakeHttpMessageHandler` dequeues bodies and status codes independently; in `ARefusedSignIn...` the zone call gets `OK` with the zone body and the ticket call gets `Unauthorized` with the default body.)

- [ ] **Step 2: Write the failing settings and provider tests**

`AutotaskSettingsServiceTests` (Postgres), mirroring the ConnectWise ones:
- `Save_StoresTheSecretInTheSecretStore_AndAuditsItAsASecret` (key `AutotaskSettings.SecretStoreKey`, action `AuditActions.AutotaskSettingsSaved`, secret absent from the changes).
- `Save_TurnedOnWithNoTrackingIdentifier_IsRefused` (message contains "API tracking identifier").
- `Save_TrimsTheUsername`.

`AutotaskProviderTests` (Postgres, over a fake `IAutotaskClient`):
- `TicketState_TheClosedStatus_IsClosed` (status 5 with `ClosedStatusId = 5` gives Closed; status 1 gives Open; null gives Missing), as a `[Theory]`.
- `Readiness_ListsWhatIsMissing`: an enabled empty row (apart from the default closed status) gives `["username", "secret", "API tracking identifier", "queue", "ticket type", "priority"]`.
- `TicketUrl_IsKnownOnlyOnceTheZoneIs`: with an empty `AutotaskZoneCache`, null; after `zones.Set("api@contoso.com", new AutotaskZone("https://webservices2.autotask.net/ATServicesRest/V1.0/", "https://ww2.autotask.net/"))`, `"https://ww2.autotask.net/Autotask/AutotaskExtend/ExecuteCommand.aspx?Code=OpenTicketDetail&TicketID={0}"`.

Write each in full, following the ConnectWise test files.

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~Autotask"`
Expected: build FAIL.

- [ ] **Step 4: Implement settings, service, zone cache and migration**

`AutotaskSettings` as in Interfaces, seeded with `HasData(new AutotaskSettings { Id = 1 })` (the defaults give Closed status 5, "Complete"). `AutotaskSettingsService` follows `ConnectWiseSettingsService` exactly: refuse `Enabled` with no `EffectiveIntegrationCode` ("Autotask needs an API tracking identifier. Choose one when you create the API user, then enter it under API tracking identifier."); trim `Username`; audit fields "Enabled", "Username", "API tracking identifier override", "Queue", "Ticket type", "Issue type", "Priority", "Closed status", and `.Secret("Secret", hasNewSecret)`; record `AuditActions.AutotaskSettingsSaved` against `AuditTarget.Settings("Autotask")`. Add the three audit actions and `typeof(AutotaskSettingsService)` to `AuditCoverageTests`.

```csharp
// src/DotMarc/Psa/Autotask/AutotaskZoneCache.cs
using System.Collections.Concurrent;

namespace DotMarc.Psa.Autotask;

public sealed record AutotaskZone(string ApiUrl, string WebUrl);

/// <summary>Which Autotask zone (datacentre) each API username lives in. Looked up once per username and kept for the
/// life of the process; a refused sign-in or Clear zone forgets it so the next call looks it up again.</summary>
public sealed class AutotaskZoneCache
{
    private readonly ConcurrentDictionary<string, AutotaskZone> _zones = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string username, out AutotaskZone zone) => _zones.TryGetValue(username, out zone!);
    public void Set(string username, AutotaskZone zone) => _zones[username] = zone;
    public void Forget(string username) => _zones.TryRemove(username, out _);
    public void Clear() => _zones.Clear();
}
```

Run: `dotnet ef migrations add AddAutotaskSettings --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj` and check the seed row.

- [ ] **Step 5: Implement the client**

```csharp
// src/DotMarc/Psa/Autotask/AutotaskClient.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Notifications;

namespace DotMarc.Psa.Autotask;

/// <summary>Autotask REST API v1.0. The API lives in a zone per tenant, found from the username. Every call sends the
/// ApiIntegrationCode (tracking identifier), UserName and Secret headers.</summary>
public sealed class AutotaskClient(HttpClient httpClient, ISecretStore secretStore, AutotaskZoneCache zones, TimeProvider timeProvider) : IAutotaskClient
{
    private const string ZoneLookupUrl = "https://webservices.autotask.net/atservicesrest/v1.0/zoneInformation?user=";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(AutotaskSettings settings, CancellationToken cancellationToken = default)
    {
        var search = JsonSerializer.Serialize(new
        {
            filter = new[] { new { op = "eq", field = "isActive", value = (object)true } },
            IncludeFields = new[] { "id", "companyName" },
        });
        var companies = new List<PsaCompany>();
        string? next = "Companies/query?search=" + Uri.EscapeDataString(search);
        while (next is not null)
        {
            using var response = await SendAsync(settings, HttpMethod.Get, next, null, cancellationToken).ConfigureAwait(false);
            var page = await response.Content.ReadFromJsonAsync<CompanyPage>(JsonOptions, cancellationToken).ConfigureAwait(false);
            companies.AddRange(page?.Items.Select(item => new PsaCompany(item.Id.ToString(CultureInfo.InvariantCulture), item.CompanyName)) ?? []);
            next = page?.PageDetails?.NextPageUrl;
        }

        return companies.OrderBy(company => company.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<AutotaskTicketPicklists> GetTicketPicklistsAsync(AutotaskSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(settings, HttpMethod.Get, "Tickets/entityInformation/fields", null, cancellationToken).ConfigureAwait(false);
        var info = await response.Content.ReadFromJsonAsync<FieldInfo>(JsonOptions, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<PsaOption> Options(string fieldName) =>
            info?.Fields.FirstOrDefault(field => string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))?.PicklistValues?
                .Where(value => value.IsActive && int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .Select(value => new PsaOption(int.Parse(value.Value, CultureInfo.InvariantCulture), value.Label))
                .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];

        return new AutotaskTicketPicklists(Options("queueID"), Options("ticketType"), Options("issueType"), Options("priority"), Options("status"));
    }

    public async Task<string> CreateTicketAsync(AutotaskSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        var ticket = new CreateTicketBody(
            int.Parse(request.CompanyId, CultureInfo.InvariantCulture),
            Truncate(request.Title, 255),
            Truncate(request.Body, 8000),
            settings.QueueId!.Value,
            settings.TicketTypeId!.Value,
            settings.IssueTypeId,
            settings.PriorityId!.Value,
            AutotaskSettings.NewStatus,
            // Autotask requires a due date unless the ticket category sets one.
            timeProvider.GetUtcNow().AddDays(1));
        using var response = await SendAsync(settings, HttpMethod.Post, "Tickets", JsonContent.Create(ticket, options: JsonOptions), cancellationToken).ConfigureAwait(false);
        var created = await response.Content.ReadFromJsonAsync<ItemId>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Autotask created a ticket but didn't say its id.");
        return created.ItemIdValue.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<int?> GetTicketStatusAsync(AutotaskSettings settings, string ticketId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(settings, HttpMethod.Get, $"Tickets/{Uri.EscapeDataString(ticketId)}", null, cancellationToken).ConfigureAwait(false);
            var ticket = await response.Content.ReadFromJsonAsync<TicketItem>(JsonOptions, cancellationToken).ConfigureAwait(false);
            return ticket?.Item?.Status;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound && exception.Message.StartsWith("Autotask returned", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task CloseTicketAsync(AutotaskSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        var update = new CloseTicketBody(long.Parse(ticketId, CultureInfo.InvariantCulture), settings.ClosedStatusId ?? AutotaskSettings.CompleteStatus, note);
        using var response = await SendAsync(settings, HttpMethod.Patch, "Tickets", JsonContent.Create(update, options: JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    private async Task<AutotaskZone> GetZoneAsync(AutotaskSettings settings, CancellationToken cancellationToken)
    {
        var username = settings.Username ?? throw new InvalidOperationException("The Autotask username isn't saved.");
        if (zones.TryGet(username, out var cached))
        {
            return cached;
        }

        using var response = await httpClient.GetAsync(ZoneLookupUrl + Uri.EscapeDataString(username), cancellationToken).ConfigureAwait(false);
        var zone = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ZoneInformation>(JsonOptions, cancellationToken).ConfigureAwait(false) : null;
        if (zone?.Url is null)
        {
            throw new HttpRequestException("Autotask couldn't find a zone for that username. Check it's the API user's username.", null, response.StatusCode);
        }

        var found = new AutotaskZone(zone.Url.TrimEnd('/') + "/V1.0/", zone.WebUrl ?? "");
        zones.Set(username, found);
        return found;
    }

    /// <param name="path">Relative to the zone's API root, or an absolute nextPageUrl Autotask returned.</param>
    private async Task<HttpResponseMessage> SendAsync(AutotaskSettings settings, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var integrationCode = settings.EffectiveIntegrationCode ?? throw new InvalidOperationException("Autotask needs an API tracking identifier before dotMARC can call it.");
        var secret = await secretStore.GetSecretAsync(AutotaskSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Autotask secret isn't saved.");
        var zone = await GetZoneAsync(settings, cancellationToken).ConfigureAwait(false);

        var url = path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? path : zone.ApiUrl + path;
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("ApiIntegrationCode", integrationCode);
        request.Headers.Add("UserName", settings.Username);
        request.Headers.Add("Secret", secret);
        var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = response.StatusCode;
        response.Dispose();
        if (status == HttpStatusCode.Unauthorized)
        {
            zones.Forget(settings.Username!);
        }

        var advice = status switch
        {
            HttpStatusCode.Unauthorized => "Autotask refused the sign-in: check the username, secret and API tracking identifier.",
            HttpStatusCode.Forbidden => "Autotask refused the request: check the API user's security level allows it.",
            _ => $"Autotask returned {(int)status} for {method} {path.Split('?')[0]}."
        };
        throw new HttpRequestException(string.IsNullOrWhiteSpace(body) || body == "{}" ? advice : $"{advice} Autotask said: {Truncate(body, 300)}", null, status);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private sealed record ZoneInformation(string? Url, string? WebUrl);
    private sealed record CompanyItem(long Id, string CompanyName);
    private sealed record PageDetails(string? NextPageUrl);
    private sealed record CompanyPage(List<CompanyItem> Items, PageDetails? PageDetails);
    private sealed record PicklistValue(string Value, string Label, bool IsActive);
    private sealed record Field(string Name, List<PicklistValue>? PicklistValues);
    private sealed record FieldInfo(List<Field> Fields);
    private sealed record ItemId([property: JsonPropertyName("itemId")] long ItemIdValue);
    private sealed record TicketState(long Id, int Status);
    private sealed record TicketItem(TicketState? Item);
    private sealed record CloseTicketBody(long Id, int Status, string Resolution);
    private sealed record CreateTicketBody(
        [property: JsonPropertyName("companyID")] int CompanyId,
        string Title,
        string Description,
        [property: JsonPropertyName("queueID")] int QueueId,
        int TicketType,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? IssueType,
        int Priority,
        int Status,
        DateTimeOffset DueDateTime);
}
```

(The zone lookup's own failure has its own message and doesn't start with "Autotask returned", so a missing zone isn't mistaken for a missing ticket in `GetTicketStatusAsync`.)

Register: `builder.Services.AddSingleton<AutotaskZoneCache>();` and `builder.Services.AddHttpClient<IAutotaskClient, AutotaskClient>();`.

- [ ] **Step 6: Implement the provider**

`AutotaskProvider(IAutotaskClient client, AutotaskZoneCache zones)`, like `ConnectWiseProvider`:
- `MissingSettings`: "username", "secret" (`!SecretConfigured`), "API tracking identifier" (`EffectiveIntegrationCode is null`), "queue", "ticket type", "priority", "closed status".
- `GetTicketStateAsync`: `status is null ? Missing : status == settings.ClosedStatusId ? Closed : Open`.
- `GetTicketUrlTemplateAsync`: `settings.Username is { } username && zones.TryGet(username, out var zone) && zone.WebUrl.Length > 0 ? zone.WebUrl.TrimEnd('/') + "/Autotask/AutotaskExtend/ExecuteCommand.aspx?Code=OpenTicketDetail&TicketID={0}" : null`, with a summary saying the link is known once dotMARC has called Autotask since starting, and that it isn't confirmed against a live tenant.

Register in the non-demo branch: `builder.Services.AddSingleton<IPsaProvider, AutotaskProvider>();`.

- [ ] **Step 7: Run the tests, then commit**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~Autotask|FullyQualifiedName~AuditCoverageTests"` (PASS), then `dotnet test test/DotMarc.Tests` (all pass).

```bash
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Add Autotask ticketing: settings, zone lookup, API client and provider"
```

---

### Task 12: Autotask settings tab, import column, demo and docs

**Files:**
- Create: `src/DotMarc/Components/Psa/AutotaskSettingsTab.razor`
- Modify: `src/DotMarc/Components/Pages/PsaSettings.razor`, `src/DotMarc/Components/Psa/PsaIntegrationTestPanel.razor`
- Modify: `src/DotMarc/DomainImport/ImportTable.cs`, `ImportPlan.cs`, `PsaImportColumns.cs`, `DomainImportSamples.cs`
- Modify: `src/DotMarc/Program.cs` (demo branch)
- Create: `website/docs/psa/autotask.mdx`
- Test: `test/DotMarc.Tests/DomainImport/` header tests

**Interfaces:**
- Produces: `ImportColumn.AutotaskCompany` (appended after `ConnectWiseCompany`), `ImportNameKind.AutotaskCompany`, header names `autotaskcompany` and `autotask`.

- [ ] **Step 1: Write the failing import tests**

```csharp
[Fact]
public void TheAutotaskCompanyColumn_IsReadByEitherHeaderName()
{
    foreach (var header in new[] { "autotask company", "Autotask" })
    {
        Assert.Equal("Contoso Ltd", TableFrom($"domain,{header}", "contoso.io,Contoso Ltd").Rows.Single().PsaCompanies[PsaKind.Autotask]);
    }
}

[Fact]
public void HeaderlessInput_PutsTheAutotaskColumnAfterConnectWise()
{
    var table = ImportTable.FromRows([Row("contoso.io", "Client A", "red", "Contoso", "yes", "s1", "testing", "mx.contoso.io", "86400", "Contoso Ltd", "Contoso Limited")]);

    Assert.Equal("Contoso Limited", table.Rows.Single().PsaCompanies[PsaKind.Autotask]);
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainImport"`
Expected: FAIL.

- [ ] **Step 3: Implement the column**

Append `AutotaskCompany` to `ImportColumn`; add `["autotaskcompany"]` and `["autotask"]` header names; add `ImportNameKind.AutotaskCompany`; add `new(PsaKind.Autotask, ImportColumn.AutotaskCompany, ImportNameKind.AutotaskCompany)` to `PsaImportColumns.All`; add `"autotask company"` to the samples.

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainImport"`. Expected: PASS.

- [ ] **Step 4: The Autotask tab**

`AutotaskSettingsTab.razor`, laid out like the ConnectWise tab:
- **Connection:** Enabled; Username (help: "The API user's username, an email address. dotMARC finds your Autotask zone from it."); Secret (password, placeholder "Saved" when configured); API tracking identifier override (help as for ConnectWise's client ID: dotMARC's own unless you enter one; while `AutotaskSettings.DefaultIntegrationCode` is empty, "Required. When you create the API user, choose Integration vendor or Custom (internal integration) as its tracking identifier, and enter that identifier here."); a "Clear cached zone" button that calls `AutotaskZoneCache.Clear()` and records `AuditActions.AutotaskZoneCleared` through the same recorder the Halo "clear sign-in" button uses, with summary "Cleared the cached Autotask zone".
- **Ticket defaults:** "Load from Autotask" calls `GetTicketPicklistsAsync`; selects for Queue, Ticket type, Issue type (optional, with "None"), Priority, Closed status (defaulting to Complete), each storing id and name.
- **Save** via `AutotaskSettingsService.SaveAsync`; **Test** `<PsaIntegrationTestPanel Psa="PsaKind.Autotask" />`; raises `EnabledChanged`.

`PsaSettings.razor`: add the Autotask tab after ConnectWise. `PsaIntegrationTestPanel`: `PsaKind.Autotask => AuditActions.AutotaskIntegrationTested`. `Program.cs` demo branch: add a `DemoPsaProvider(PsaKind.Autotask, ...)`.

- [ ] **Step 5: Docs**

Create `website/docs/psa/autotask.mdx` (`title: Autotask`, `sidebar_position: 3`), structured like the ConnectWise page:
1. **Create an API user:** Admin > Resources (Users) > New API User; security level "API User (system)" or one that can read companies and add and edit tickets; choose the tracking identifier; generate the secret.
2. **Tracking identifier:** as above.
3. **Connect:** PSA settings > Autotask: username, secret, identifier; save; Load from Autotask; pick queue, ticket type, issue type, priority, closed status; turn on; save.
4. **Due dates:** dotMARC sets each new ticket due a day after it's raised, because Autotask requires one; your SLA or workflow rules may change it.
5. **Link companies**, **How closing works** (polling; dotMARC writes "Resolved automatically by dotMARC." into the ticket's Resolution and sets your closed status), **Test integration**, **Clear cached zone** (after moving the API user or if Autotask moves your tenant).

- [ ] **Step 6: Build, test, browser check, commit**

Run: `dotnet build src/DotMarc`, `dotnet test test/DotMarc.Tests` (all pass). Browser check (playwright-edge, demo): three tabs, three company columns on Groups and Domains. Stop only your own process.

```bash
git add -A src/DotMarc test/DotMarc.Tests website/docs
git commit -m "Add the Autotask settings tab, import column, demo data and docs"
```

---

### Task 13: PSA docs overview, roadmap and final checks

**Files:**
- Modify: `website/docs/psa-integration.mdx` (becomes the overview), move its Halo content to `website/docs/psa/halopsa.mdx`
- Create: `website/docs/psa/_category_.json` if the sidebar is autogenerated (check `website/sidebars.*` first)
- Modify: `website/docs/alerts.mdx` (where it mentions HaloPSA settings living under Alert settings), `website/docs/import-domains.mdx` (the new columns)
- Modify: `website/scripts/canny-roadmap.json`

- [ ] **Step 1: Docs**

- `website/docs/psa/halopsa.mdx` (`title: HaloPSA`, `sidebar_position: 1`): the Halo setup content from `psa-integration.mdx`, with paths updated to PSA settings > HaloPSA, and a note that dotMARC now also checks open Halo tickets every few minutes, so a missed webhook only delays resolution.
- `psa-integration.mdx` becomes the overview: what PSA integration does; that HaloPSA, ConnectWise and Autotask can run side by side; linking Groups and Domains to companies (one per PSA, a Domain overrides its Groups, lowest Group wins ties); the ticket rules on Alert settings applying to every PSA, decided per PSA by that PSA's deciding Group; one ticket per PSA per alert, and no second ticket while an earlier copy's is open; closing in both directions; the Tickets column on Alerts; links to the three pages. Keep its slug so existing links work.
- `alerts.mdx`: point PSA set-up at PSA settings.
- `import-domains.mdx`: document `connectwise company` and `autotask company` alongside `halo client`, and that each is matched against that PSA's company list and ignored with a notice when the PSA isn't connected.

Build the site to check links: `cd website && npm run build`. Expected: success with no broken-link errors.

- [ ] **Step 2: Roadmap**

In `website/scripts/canny-roadmap.json`, mark complete: the Public API entry (release v0.8.0), Slack alert delivery (v0.9.0), "Add ConnectWise PSA support" (v0.9.0) and "Add Autotask PSA support" (v0.9.0), following the shape of entries already marked complete in that file. Update the two PSA entries' text so it describes what shipped (polling close-back, several PSAs at once).

- [ ] **Step 3: Final checks**

Run: `dotnet build src/DotMarc -warnaserror` if the project builds clean with it today (check first with a plain build; don't introduce the flag if the existing build has warnings), then `dotnet test test/DotMarc.Tests`. Expected: all pass.

Run: `node scripts/update-openapi.mjs` and check `git diff` shows only the acknowledgement counts from Task 3 (or nothing).

Browser check (playwright-edge, demo), end to end: PSA settings shows three tabs with ticks for the ones on; Groups and Domains show three company columns; an alert for a linked domain shows a chip per PSA; after three minutes and a poll the alert resolves and the chips turn outlined; the audit log shows "Alert resolved by a closed PSA ticket". Stop only your own process, by its ID.

- [ ] **Step 4: Commit**

```bash
git add -A website
git commit -m "Document PSA integration across HaloPSA, ConnectWise and Autotask, and update the roadmap"
```
