# Alert Ticket Rules Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let admins choose which alert types create HaloPSA tickets, globally and per group (client).

**Architecture:** A code registry (`AlertTypes`) lists the known alert types. An `AlertTicketRule` table stores global rows (no group) and per-group override rows. A pure function (`AlertTicketPolicy`) decides whether an alert creates a ticket, and `PsaTicketService` calls it before creating one. Two screens edit the rules: a global panel on Alert settings and a per-group dialog on Manage groups.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor 9.8, EF Core 10 with Npgsql, xUnit with Testcontainers Postgres.

**Spec:** `docs/superpowers/specs/2026-09-25-alert-ticket-rules-design.md`

## Global Constraints

* No rows in the rules table means every alert type creates tickets, so behaviour is unchanged until someone edits a rule. The migration seeds no rows.
* A rule only affects **creating** a ticket. The alert is still recorded and the Teams or webhook notification still goes out. Closing an existing ticket ignores the rules. Changing a rule is not retroactive.
* The deciding group is the one `HaloClientResolver` picks: lowest id among the domain's groups that have a Halo client. A domain with its own `HaloClientId` override has no deciding group and uses the global rules.
* Decision order: the deciding group's rule, then the global rule, then the registry default (true).
* The four alert type keys are exactly `MissedReport`, `SuspiciousRejectActivity`, `TlsrptFailure` and `UnexpectedActivityOnNullRoutedDomain`.
* MudBlazor 9.8: give generic components an explicit `T=` (for example `MudSelect T="int"`), use `Margin.Dense` and never `Dense` on `MudTextField`.
* Prose in code comments, docs and commit messages uses no em dashes. Use hyphens, commas or full stops.
* Use meaningful variable names everywhere, including in tests (no single-letter names such as `r` or `m`).
* Never run `git add -A` or `git add .`. The working tree intentionally holds two uncommitted release files, `Directory.Build.props` and `website/blog/2026-09-24-v0-7-0.mdx`. Stage only the files each task lists.
* Many files use CRLF line endings. Use the Edit tool for changes to existing files so endings are preserved.
* Baseline before starting: `dotnet test` from the repo root passes 687 tests.

## File Structure

* `src/DotMarc/Notifications/AlertTypes.cs` (create): the registry, `AlertTypeInfo` record and key constants.
* `src/DotMarc/Notifications/AlertTicketRule.cs` (create): the entity.
* `src/DotMarc/Notifications/AlertTicketRuleService.cs` (create): reads and writes rules.
* `src/DotMarc/Notifications/AlertTicketPolicy.cs` (create): the pure decision function.
* `src/DotMarc/Notifications/HaloClientResolver.cs` (modify): add `ResolveGroup`.
* `src/DotMarc/Notifications/PsaTicketService.cs` (modify): consult the policy.
* `src/DotMarc/Notifications/AlertingService.cs` (modify): use the registry keys.
* `src/DotMarc/Data/DotMarcDbContext.cs` (modify): `DbSet`, indexes, foreign key.
* `src/DotMarc/Migrations/*AddAlertTicketRules*` (generated).
* `src/DotMarc/Components/Pages/AlertsSettings.razor` (modify): the global panel.
* `src/DotMarc/Components/Dialogs/GroupTicketRulesDialog.razor` (create): the per-group dialog.
* `src/DotMarc/Components/Pages/ManageGroups.razor` (modify): button and badge.
* Tests in `test/DotMarc.Tests/Notifications/`: `AlertTypesTests.cs`, `AlertTicketPolicyTests.cs`, `AlertTicketRuleServiceTests.cs` (all new), and additions to `PsaTicketServiceTests.cs` and `HaloClientResolverTests.cs` if it exists (otherwise a new file of that name).
* Docs: `website/docs/psa-integration.mdx`, `website/data/faqs/ticket-rules.yaml` (create).

---

### Task 1: Alert type registry, used by AlertingService

**Files:**
- Create: `src/DotMarc/Notifications/AlertTypes.cs`
- Modify: `src/DotMarc/Notifications/AlertingService.cs` (lines 81, 93, 112, 116, 143, 149, 161, 183, and `EnsureAlertAsync` at 216)
- Test: `test/DotMarc.Tests/Notifications/AlertTypesTests.cs`

**Interfaces:**
- Produces: `AlertTypeInfo(string Key, string DisplayName, string Description, bool CreatesTicketByDefault = true)`; `AlertTypes.MissedReport`, `.SuspiciousRejectActivity`, `.TlsrptFailure`, `.UnexpectedActivityOnNullRoutedDomain` (string constants); `AlertTypes.All` (`IReadOnlyList<AlertTypeInfo>`, in that order); `AlertTypes.Find(string key)` returning `AlertTypeInfo?`.

- [ ] **Step 1: Write the failing test**

Create `test/DotMarc.Tests/Notifications/AlertTypesTests.cs`:

```csharp
// test/DotMarc.Tests/Notifications/AlertTypesTests.cs
using System.Reflection;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertTypesTests
{
    [Fact]
    public void All_ListsTheFourAlertTypesDotMarcRaises()
    {
        Assert.Equal(
            ["MissedReport", "SuspiciousRejectActivity", "TlsrptFailure", "UnexpectedActivityOnNullRoutedDomain"],
            AlertTypes.All.Select(alertType => alertType.Key));
    }

    [Fact]
    public void EveryKeyConstant_IsInTheRegistry_SoANewAlertTypeCannotBeMissingFromTheUi()
    {
        var constantValues = typeof(AlertTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(constantValues);
        Assert.Equal(constantValues.Order(), AlertTypes.All.Select(alertType => alertType.Key).Order());
    }

    [Fact]
    public void EveryAlertType_HasANameADescriptionAndCreatesTicketsByDefault()
    {
        foreach (var alertType in AlertTypes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(alertType.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(alertType.Description));
            Assert.True(alertType.CreatesTicketByDefault);
        }
    }

    [Fact]
    public void Find_ReturnsTheAlertType_OrNullForAnUnknownKey()
    {
        Assert.Equal("TlsrptFailure", AlertTypes.Find("TlsrptFailure")!.Key);
        Assert.Null(AlertTypes.Find("NotARealAlertType"));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertTypesTests"`
Expected: build error, `AlertTypes` does not exist.

- [ ] **Step 3: Create the registry**

Create `src/DotMarc/Notifications/AlertTypes.cs`:

```csharp
namespace DotMarc.Notifications;

/// <summary>One kind of alert dotMARC raises. <see cref="Key"/> is what is stored on an AlertEvent and in
/// ticket rules, so it must never change once shipped.</summary>
public sealed record AlertTypeInfo(string Key, string DisplayName, string Description, bool CreatesTicketByDefault = true);

/// <summary>Every alert type dotMARC raises. The ticket rule screens list this, so a new alert type added here
/// appears in them automatically, creating tickets until someone turns that off.</summary>
public static class AlertTypes
{
    public const string MissedReport = "MissedReport";
    public const string SuspiciousRejectActivity = "SuspiciousRejectActivity";
    public const string TlsrptFailure = "TlsrptFailure";
    public const string UnexpectedActivityOnNullRoutedDomain = "UnexpectedActivityOnNullRoutedDomain";

    public static IReadOnlyList<AlertTypeInfo> All { get; } =
    [
        new(MissedReport, "Missing DMARC report", "No aggregate report has arrived from a monitored domain for longer than the threshold."),
        new(SuspiciousRejectActivity, "Suspicious reject activity", "Rejected or quarantined mail looks like more than benign forwarding."),
        new(TlsrptFailure, "TLS delivery failures", "A TLSRPT report says other servers failed to deliver mail to the domain over TLS."),
        new(UnexpectedActivityOnNullRoutedDomain, "Mail activity on a null-routed domain", "A domain that should send no mail is appearing in DMARC reports."),
    ];

    public static AlertTypeInfo? Find(string key) => All.FirstOrDefault(alertType => alertType.Key == key);
}
```

- [ ] **Step 4: Use the keys in AlertingService**

In `src/DotMarc/Notifications/AlertingService.cs`, replace each literal alert type string with the constant, using the Edit tool:
`"UnexpectedActivityOnNullRoutedDomain"` becomes `AlertTypes.UnexpectedActivityOnNullRoutedDomain` (lines 81 and 183), `"MissedReport"` becomes `AlertTypes.MissedReport` (lines 93 and 143), `"SuspiciousRejectActivity"` becomes `AlertTypes.SuspiciousRejectActivity` (lines 112 and 116), `"TlsrptFailure"` becomes `AlertTypes.TlsrptFailure` (lines 149 and 161).

At the top of `EnsureAlertAsync` (line 216) add a guard, so an alert type missing from the registry fails loudly in tests instead of being invisible in the UI:

```csharp
        if (AlertTypes.Find(alertType) is null)
        {
            throw new InvalidOperationException($"Alert type '{alertType}' is not in AlertTypes.All, so it can't be controlled from the ticket rule screens. Add it there.");
        }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertTypes|FullyQualifiedName~AlertingService"`
Expected: PASS. The existing `AlertingService` tests raise every alert type, so they also prove the guard accepts all four.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Notifications/AlertTypes.cs src/DotMarc/Notifications/AlertingService.cs test/DotMarc.Tests/Notifications/AlertTypesTests.cs
git commit -m "Add a registry of alert types and use its keys in AlertingService"
```

---

### Task 2: Rules table, migration and service

**Files:**
- Create: `src/DotMarc/Notifications/AlertTicketRule.cs`, `src/DotMarc/Notifications/AlertTicketRuleService.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs` (add `DbSet` near line 34; add config after the `AlertEvent` block at line 262)
- Generate: migration `AddAlertTicketRules`
- Test: `test/DotMarc.Tests/Notifications/AlertTicketRuleServiceTests.cs`

**Interfaces:**
- Consumes: `AlertTypes.Find` from Task 1; `Group` and `DotMarcDbContext`.
- Produces: entity `AlertTicketRule { int Id; string AlertType; int? GroupId; bool CreateTicket }`; `DotMarcDbContext.AlertTicketRules`; static `AlertTicketRuleService` with
  * `Task<Dictionary<string, bool>> GetGlobalAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)`
  * `Task<Dictionary<string, bool>> GetForGroupAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default)`
  * `Task<Dictionary<int, int>> CountOverridesByGroupAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)` (group id to number of overrides)
  * `Task SetGlobalAsync(DotMarcDbContext context, string alertType, bool createTicket, CancellationToken cancellationToken = default)`
  * `Task SetForGroupAsync(DotMarcDbContext context, int groupId, string alertType, bool? createTicket, CancellationToken cancellationToken = default)` where `null` removes the override. Both setters throw `ArgumentException` for an alert type not in the registry.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Notifications/AlertTicketRuleServiceTests.cs`:

```csharp
// test/DotMarc.Tests/Notifications/AlertTicketRuleServiceTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class AlertTicketRuleServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertTicketRuleServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<int> AddGroupAsync(string name)
    {
        await using var context = CreateContext();
        var group = new Group { Name = name, HaloClientId = 7 };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        return group.Id;
    }

    [Fact]
    public async Task WithNoRules_NothingIsReturned_SoEveryTypeUsesItsDefault()
    {
        await using var context = CreateContext();

        Assert.Empty(await AlertTicketRuleService.GetGlobalAsync(context));
        Assert.Empty(await AlertTicketRuleService.CountOverridesByGroupAsync(context));
    }

    [Fact]
    public async Task SetGlobalAsync_CreatesTheRule_ThenUpdatesTheSameRow()
    {
        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, false);
        }

        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, true);
        }

        await using var verify = CreateContext();
        var global = await AlertTicketRuleService.GetGlobalAsync(verify);
        Assert.Equal(new Dictionary<string, bool> { [AlertTypes.MissedReport] = true }, global);
        Assert.Equal(1, await verify.AlertTicketRules.CountAsync());
    }

    [Fact]
    public async Task SetForGroupAsync_StoresAnOverrideForThatGroupOnly()
    {
        var groupA = await AddGroupAsync("Client A");
        var groupB = await AddGroupAsync("Client B");
        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetForGroupAsync(context, groupA, AlertTypes.TlsrptFailure, false);
        }

        await using var verify = CreateContext();
        Assert.Equal(new Dictionary<string, bool> { [AlertTypes.TlsrptFailure] = false }, await AlertTicketRuleService.GetForGroupAsync(verify, groupA));
        Assert.Empty(await AlertTicketRuleService.GetForGroupAsync(verify, groupB));
        Assert.Empty(await AlertTicketRuleService.GetGlobalAsync(verify));
    }

    [Fact]
    public async Task SetForGroupAsync_WithNull_RemovesTheOverride_SoNoNoOpRowsAccumulate()
    {
        var groupId = await AddGroupAsync("Client A");
        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetForGroupAsync(context, groupId, AlertTypes.MissedReport, true);
        }

        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetForGroupAsync(context, groupId, AlertTypes.MissedReport, null);
        }

        await using var verify = CreateContext();
        Assert.Equal(0, await verify.AlertTicketRules.CountAsync());
    }

    [Fact]
    public async Task CountOverridesByGroupAsync_CountsEachGroupsOverrides_AndIgnoresGlobalRows()
    {
        var groupA = await AddGroupAsync("Client A");
        var groupB = await AddGroupAsync("Client B");
        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, false);
            await AlertTicketRuleService.SetForGroupAsync(context, groupA, AlertTypes.MissedReport, true);
            await AlertTicketRuleService.SetForGroupAsync(context, groupA, AlertTypes.TlsrptFailure, false);
            await AlertTicketRuleService.SetForGroupAsync(context, groupB, AlertTypes.MissedReport, false);
        }

        await using var verify = CreateContext();
        var counts = await AlertTicketRuleService.CountOverridesByGroupAsync(verify);
        Assert.Equal(new Dictionary<int, int> { [groupA] = 2, [groupB] = 1 }, counts);
    }

    [Fact]
    public async Task DeletingAGroup_DeletesItsRules_ButNotTheGlobalOnes()
    {
        var groupId = await AddGroupAsync("Client A");
        await using (var context = CreateContext())
        {
            await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, false);
            await AlertTicketRuleService.SetForGroupAsync(context, groupId, AlertTypes.MissedReport, true);
        }

        await using (var context = CreateContext())
        {
            await GroupManagementService.RemoveGroupAsync(context, groupId);
        }

        await using var verify = CreateContext();
        var remaining = await verify.AlertTicketRules.ToListAsync();
        Assert.Single(remaining);
        Assert.Null(remaining[0].GroupId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Setters_RefuseAnAlertTypeThatIsNotInTheRegistry(bool forGroup)
    {
        var groupId = await AddGroupAsync("Client A");
        await using var context = CreateContext();

        var attempt = forGroup
            ? AlertTicketRuleService.SetForGroupAsync(context, groupId, "NotARealAlertType", true)
            : AlertTicketRuleService.SetGlobalAsync(context, "NotARealAlertType", true);

        await Assert.ThrowsAsync<ArgumentException>(() => attempt);
    }

    [Fact]
    public async Task TheDatabase_RefusesTwoGlobalRulesForTheSameType_AndTwoOverridesForTheSameGroupAndType()
    {
        var groupId = await AddGroupAsync("Client A");
        await using var context = CreateContext();
        context.AlertTicketRules.Add(new AlertTicketRule { AlertType = AlertTypes.MissedReport, GroupId = null, CreateTicket = true });
        context.AlertTicketRules.Add(new AlertTicketRule { AlertType = AlertTypes.MissedReport, GroupId = null, CreateTicket = false });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        await using var second = CreateContext();
        second.AlertTicketRules.Add(new AlertTicketRule { AlertType = AlertTypes.MissedReport, GroupId = groupId, CreateTicket = true });
        second.AlertTicketRules.Add(new AlertTicketRule { AlertType = AlertTypes.MissedReport, GroupId = groupId, CreateTicket = false });
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertTicketRuleServiceTests"`
Expected: build error, `AlertTicketRule` and `AlertTicketRuleService` do not exist.

- [ ] **Step 3: Create the entity**

Create `src/DotMarc/Notifications/AlertTicketRule.cs`:

```csharp
namespace DotMarc.Notifications;

/// <summary>Whether an alert type creates a HaloPSA ticket. A row with no <see cref="GroupId"/> is the global
/// setting for the type; a row with one is that group's override. No row means "use the next level down":
/// the group falls back to the global rule, which falls back to the registry default.</summary>
public sealed class AlertTicketRule
{
    public int Id { get; set; }
    public required string AlertType { get; set; }
    public int? GroupId { get; set; }
    public bool CreateTicket { get; set; }
}
```

- [ ] **Step 4: Register it with the DbContext**

In `src/DotMarc/Data/DotMarcDbContext.cs`, after the `AlertEvents` line (34's neighbour at line 32) add:

```csharp
    public DbSet<AlertTicketRule> AlertTicketRules => Set<AlertTicketRule>();
```

and after the `modelBuilder.Entity<AlertEvent>(...)` block (ends line 262) add:

```csharp
        modelBuilder.Entity<AlertTicketRule>(entity =>
        {
            entity.Property(rule => rule.AlertType).HasMaxLength(100);

            // Deleting a group deletes its overrides. The global rows have no group and are never touched.
            entity.HasOne<Group>().WithMany().HasForeignKey(rule => rule.GroupId).OnDelete(DeleteBehavior.Cascade);

            // In PostgreSQL nulls are distinct in a unique index, so "one row per type and group" needs two indexes:
            // one for the group overrides and one for the global rows.
            entity.HasIndex(rule => new { rule.AlertType, rule.GroupId })
                .IsUnique()
                .HasFilter("\"GroupId\" IS NOT NULL");
            entity.HasIndex(rule => rule.AlertType)
                .IsUnique()
                .HasDatabaseName("IX_AlertTicketRules_AlertType_Global")
                .HasFilter("\"GroupId\" IS NULL");
        });
```

Ensure `using DotMarc.Notifications;` is present at the top of the file (it already is, since `HaloPsaSettings` is used).

- [ ] **Step 5: Create the service**

Create `src/DotMarc/Notifications/AlertTicketRuleService.cs`:

```csharp
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

/// <summary>Reads and writes <see cref="AlertTicketRule"/> rows for the two screens that edit them.
/// The decision itself lives in <see cref="AlertTicketPolicy"/>.</summary>
public static class AlertTicketRuleService
{
    /// <summary>The global rules, by alert type. A type with no entry uses its registry default.</summary>
    public static async Task<Dictionary<string, bool>> GetGlobalAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await context.AlertTicketRules
            .AsNoTracking()
            .Where(rule => rule.GroupId == null)
            .ToDictionaryAsync(rule => rule.AlertType, rule => rule.CreateTicket, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>One group's overrides, by alert type. A type with no entry inherits the global setting.</summary>
    public static async Task<Dictionary<string, bool>> GetForGroupAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default) =>
        await context.AlertTicketRules
            .AsNoTracking()
            .Where(rule => rule.GroupId == groupId)
            .ToDictionaryAsync(rule => rule.AlertType, rule => rule.CreateTicket, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>How many overrides each group has, for the badge on Manage groups. Groups with none are absent.</summary>
    public static async Task<Dictionary<int, int>> CountOverridesByGroupAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await context.AlertTicketRules
            .AsNoTracking()
            .Where(rule => rule.GroupId != null)
            .GroupBy(rule => rule.GroupId!.Value)
            .Select(overrides => new { GroupId = overrides.Key, Count = overrides.Count() })
            .ToDictionaryAsync(overrides => overrides.GroupId, overrides => overrides.Count, cancellationToken)
            .ConfigureAwait(false);

    public static Task SetGlobalAsync(DotMarcDbContext context, string alertType, bool createTicket, CancellationToken cancellationToken = default) =>
        UpsertAsync(context, alertType, groupId: null, createTicket, cancellationToken);

    /// <summary>Sets a group's override, or removes it with <c>null</c> so the group inherits again. Removing
    /// the row, not storing "inherit", means overrides never pile up as no-ops.</summary>
    public static async Task SetForGroupAsync(DotMarcDbContext context, int groupId, string alertType, bool? createTicket, CancellationToken cancellationToken = default)
    {
        if (createTicket is null)
        {
            RequireKnownAlertType(alertType);
            await context.AlertTicketRules
                .Where(rule => rule.GroupId == groupId && rule.AlertType == alertType)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await UpsertAsync(context, alertType, groupId, createTicket.Value, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertAsync(DotMarcDbContext context, string alertType, int? groupId, bool createTicket, CancellationToken cancellationToken)
    {
        RequireKnownAlertType(alertType);

        var existing = await context.AlertTicketRules
            .SingleOrDefaultAsync(rule => rule.AlertType == alertType && rule.GroupId == groupId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            context.AlertTicketRules.Add(new AlertTicketRule { AlertType = alertType, GroupId = groupId, CreateTicket = createTicket });
        }
        else
        {
            existing.CreateTicket = createTicket;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RequireKnownAlertType(string alertType)
    {
        if (AlertTypes.Find(alertType) is null)
        {
            throw new ArgumentException($"'{alertType}' is not a known alert type.", nameof(alertType));
        }
    }
}
```

- [ ] **Step 6: Generate the migration**

Run: `dotnet ef migrations add AddAlertTicketRules --project src/DotMarc --startup-project src/DotMarc`
Expected: `Done.` and two new files under `src/DotMarc/Migrations/` plus a changed `DotMarcDbContextModelSnapshot.cs`. Open the new migration and confirm `Up` creates the `AlertTicketRules` table with the foreign key (cascade) and both filtered unique indexes, and seeds no data. The "may result in the loss of data" warning refers only to `Down` and is expected.

- [ ] **Step 7: Run the tests**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertTicketRuleServiceTests"`
Expected: PASS (all nine, counting the two theory cases).

- [ ] **Step 8: Commit**

```bash
git add src/DotMarc/Notifications/AlertTicketRule.cs src/DotMarc/Notifications/AlertTicketRuleService.cs src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Migrations test/DotMarc.Tests/Notifications/AlertTicketRuleServiceTests.cs
git commit -m "Add the alert ticket rules table and the service that edits it"
```

---

### Task 3: The decision function

**Files:**
- Modify: `src/DotMarc/Notifications/HaloClientResolver.cs`
- Create: `src/DotMarc/Notifications/AlertTicketPolicy.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertTicketPolicyTests.cs`

**Interfaces:**
- Consumes: `AlertTypes.Find`, `AlertTicketRule` (Tasks 1 and 2); `Domain` with `Groups` loaded; `Group.Id`, `Group.HaloClientId`.
- Produces: `HaloClientResolver.ResolveGroup(Domain domain)` returning `Group?` (null when the domain has its own `HaloClientId` override or none of its groups has a client); `AlertTicketPolicy.ShouldCreateTicket(string alertType, Domain domain, IReadOnlyCollection<AlertTicketRule> rules)` returning `bool`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Notifications/AlertTicketPolicyTests.cs`:

```csharp
// test/DotMarc.Tests/Notifications/AlertTicketPolicyTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertTicketPolicyTests
{
    private static Group MakeGroup(int id, int? haloClientId) => new() { Id = id, Name = $"Group {id}", HaloClientId = haloClientId };

    private static Domain DomainIn(int? clientOverride = null, params Group[] groups) =>
        new() { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, HaloClientId = clientOverride, Groups = [.. groups] };

    private static AlertTicketRule GlobalRule(string alertType, bool createTicket) =>
        new() { AlertType = alertType, GroupId = null, CreateTicket = createTicket };

    private static AlertTicketRule GroupRule(int groupId, string alertType, bool createTicket) =>
        new() { AlertType = alertType, GroupId = groupId, CreateTicket = createTicket };

    [Fact]
    public void WithNoRules_EveryAlertTypeCreatesATicket()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));

        foreach (var alertType in AlertTypes.All)
        {
            Assert.True(AlertTicketPolicy.ShouldCreateTicket(alertType.Key, domain, []));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGlobalRule_DecidesWhenTheGroupHasNoOverride(bool globalCreates)
    {
        var domain = DomainIn(null, MakeGroup(1, 7));

        var result = AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, [GlobalRule(AlertTypes.MissedReport, globalCreates)]);

        Assert.Equal(globalCreates, result);
    }

    [Fact]
    public void AGroupOverrideOfNever_BeatsAGlobalYes()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GlobalRule(AlertTypes.MissedReport, true), GroupRule(1, AlertTypes.MissedReport, false)];

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
    }

    [Fact]
    public void AGroupOverrideOfAlways_BeatsAGlobalNo()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GlobalRule(AlertTypes.MissedReport, false), GroupRule(1, AlertTypes.MissedReport, true)];

        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
    }

    [Fact]
    public void AGroupOverride_OnlyAffectsItsOwnAlertType()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GroupRule(1, AlertTypes.MissedReport, false)];

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.TlsrptFailure, domain, rules));
    }

    [Fact]
    public void ADomainWithItsOwnHaloClient_IgnoresEveryGroupOverride_AndUsesTheGlobalRule()
    {
        var domain = DomainIn(clientOverride: 99, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GlobalRule(AlertTypes.MissedReport, true), GroupRule(1, AlertTypes.MissedReport, false)];

        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
    }

    [Fact]
    public void AGroupWithNoHaloClient_IsNotTheDecidingGroup()
    {
        // Group 1 has no client, so group 2 decides where the ticket goes, and group 2's rules apply.
        var domain = DomainIn(null, MakeGroup(1, null), MakeGroup(2, 8));
        AlertTicketRule[] rules = [GroupRule(1, AlertTypes.MissedReport, false), GroupRule(2, AlertTypes.MissedReport, true), GlobalRule(AlertTypes.MissedReport, false)];

        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
    }

    [Fact]
    public void WithSeveralGroups_TheDecidingGroupsRuleWins_NotAnotherGroupsRule()
    {
        // Group 3 (the lowest id with a client is group 2) says Yes; group 2 says No. Group 2 decides.
        var domain = DomainIn(null, MakeGroup(3, 9), MakeGroup(2, 8));
        AlertTicketRule[] rules = [GroupRule(2, AlertTypes.MissedReport, false), GroupRule(3, AlertTypes.MissedReport, true)];

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
    }

    [Fact]
    public void ADomainWithNoGroups_UsesTheGlobalRule()
    {
        var domain = DomainIn();

        Assert.False(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, [GlobalRule(AlertTypes.MissedReport, false)]));
    }

    [Fact]
    public void ARuleForAnUnknownAlertType_IsIgnored()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));
        AlertTicketRule[] rules = [GlobalRule("RetiredAlertType", false), GroupRule(1, "RetiredAlertType", false)];

        Assert.True(AlertTicketPolicy.ShouldCreateTicket(AlertTypes.MissedReport, domain, rules));
    }

    [Fact]
    public void AnAlertTypeMissingFromTheRegistry_CreatesATicketUnlessARuleSaysOtherwise()
    {
        var domain = DomainIn(null, MakeGroup(1, 7));

        Assert.True(AlertTicketPolicy.ShouldCreateTicket("BrandNewType", domain, []));
        Assert.False(AlertTicketPolicy.ShouldCreateTicket("BrandNewType", domain, [GlobalRule("BrandNewType", false)]));
    }

    [Fact]
    public void ResolveGroup_PicksTheLowestIdGroupWithAClient_AndNullWhenTheDomainHasItsOwnClient()
    {
        var withGroups = DomainIn(null, MakeGroup(5, 9), MakeGroup(2, 8), MakeGroup(1, null));
        var withOverride = DomainIn(clientOverride: 99, MakeGroup(2, 8));

        Assert.Equal(2, HaloClientResolver.ResolveGroup(withGroups)!.Id);
        Assert.Null(HaloClientResolver.ResolveGroup(withOverride));
        Assert.Null(HaloClientResolver.ResolveGroup(DomainIn(null, MakeGroup(1, null))));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertTicketPolicyTests"`
Expected: build error, `AlertTicketPolicy` and `HaloClientResolver.ResolveGroup` do not exist.

- [ ] **Step 3: Add `ResolveGroup` to the resolver**

In `src/DotMarc/Notifications/HaloClientResolver.cs` add this method inside the class (leave `Resolve` as it is):

```csharp
    /// <summary>The Group whose Halo client a domain's ticket goes to, which is also the Group whose ticket rules
    /// apply. Null when the domain has its own Halo client override (the override isn't a Group, so the global
    /// rules apply) or when none of its Groups has a client.</summary>
    public static Group? ResolveGroup(Domain domain)
    {
        if (domain.HaloClientId is not null)
        {
            return null;
        }

        return domain.Groups
            .Where(group => group.HaloClientId is not null)
            .OrderBy(group => group.Id)
            .FirstOrDefault();
    }
```

- [ ] **Step 4: Create the policy**

Create `src/DotMarc/Notifications/AlertTicketPolicy.cs`:

```csharp
using DotMarc.Data;

namespace DotMarc.Notifications;

/// <summary>Decides whether an alert creates a HaloPSA ticket. Pure, so every case is a plain unit test.
/// Order: the deciding group's rule, then the global rule, then the alert type's registry default.</summary>
public static class AlertTicketPolicy
{
    /// <param name="domain">Must have its <c>Groups</c> loaded.</param>
    /// <param name="rules">The global rules and the deciding group's rules for this alert type. Rules for other
    /// groups or other alert types are ignored, so passing more than needed is harmless.</param>
    public static bool ShouldCreateTicket(string alertType, Domain domain, IReadOnlyCollection<AlertTicketRule> rules)
    {
        var decidingGroup = HaloClientResolver.ResolveGroup(domain);
        if (decidingGroup is not null)
        {
            var groupRule = rules.FirstOrDefault(rule => rule.AlertType == alertType && rule.GroupId == decidingGroup.Id);
            if (groupRule is not null)
            {
                return groupRule.CreateTicket;
            }
        }

        var globalRule = rules.FirstOrDefault(rule => rule.AlertType == alertType && rule.GroupId is null);
        if (globalRule is not null)
        {
            return globalRule.CreateTicket;
        }

        return AlertTypes.Find(alertType)?.CreatesTicketByDefault ?? true;
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertTicketPolicyTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Notifications/HaloClientResolver.cs src/DotMarc/Notifications/AlertTicketPolicy.cs test/DotMarc.Tests/Notifications/AlertTicketPolicyTests.cs
git commit -m "Add the pure decision for whether an alert creates a ticket"
```

---

### Task 4: PsaTicketService consults the policy

**Files:**
- Modify: `src/DotMarc/Notifications/PsaTicketService.cs` (`CreateTicketAsync`, between resolving the client at line 30 and the duplicate check at line 43)
- Test: `test/DotMarc.Tests/Notifications/PsaTicketServiceTests.cs` (add tests; the fixture, `CreateContext`, `FakeHaloPsaClient` and `EnableHaloAsync` already exist in that file)

**Interfaces:**
- Consumes: `AlertTicketPolicy.ShouldCreateTicket`, `HaloClientResolver.ResolveGroup`, `AlertTicketRuleService.SetGlobalAsync` / `SetForGroupAsync`, `DotMarcDbContext.AlertTicketRules`.
- Produces: no new public API. `CreateTicketAsync` returns without creating a ticket when the policy says no.

- [ ] **Step 1: Write the failing tests**

Add to the end of the `PsaTicketServiceTests` class:

```csharp
    private async Task<(Group Group, Domain Domain, AlertEvent Alert)> SeedMappedDomainAsync(DotMarcDbContext context, string alertType = "MissedReport")
    {
        var group = new Group { Name = "Client A", HaloClientId = 7 };
        context.Groups.Add(group);
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [group] };
        context.Domains.Add(domain);
        var alert = new AlertEvent { DomainName = "contoso.io", AlertType = alertType, Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();
        return (group, domain, alert);
    }

    [Fact]
    public async Task CreateTicketAsync_CreatesNoTicket_WhenTheGlobalRuleTurnsThatAlertTypeOff_ButTheAlertIsStillRecorded()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var (_, _, alert) = await SeedMappedDomainAsync(context);
        await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, false);

        var fakeClient = new FakeHaloPsaClient();
        await new PsaTicketService(fakeClient).CreateTicketAsync(context, alert);
        await context.SaveChangesAsync();

        Assert.Equal(0, fakeClient.CreateCallCount);
        var saved = await context.AlertEvents.SingleAsync();
        Assert.Null(saved.ExternalTicketId);
    }

    [Fact]
    public async Task CreateTicketAsync_StillCreatesTickets_ForOtherAlertTypes_WhenOneTypeIsTurnedOff()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var (_, _, alert) = await SeedMappedDomainAsync(context, AlertTypes.TlsrptFailure);
        await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, false);

        var fakeClient = new FakeHaloPsaClient();
        await new PsaTicketService(fakeClient).CreateTicketAsync(context, alert);

        Assert.Equal(1, fakeClient.CreateCallCount);
    }

    [Fact]
    public async Task CreateTicketAsync_HonoursAGroupOverrideOfNever_EvenWhenTheGlobalRuleSaysYes()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var (group, _, alert) = await SeedMappedDomainAsync(context);
        await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, true);
        await AlertTicketRuleService.SetForGroupAsync(context, group.Id, AlertTypes.MissedReport, false);

        var fakeClient = new FakeHaloPsaClient();
        await new PsaTicketService(fakeClient).CreateTicketAsync(context, alert);

        Assert.Equal(0, fakeClient.CreateCallCount);
    }

    [Fact]
    public async Task CreateTicketAsync_HonoursAGroupOverrideOfAlways_EvenWhenTheGlobalRuleSaysNo()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var (group, _, alert) = await SeedMappedDomainAsync(context);
        await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, false);
        await AlertTicketRuleService.SetForGroupAsync(context, group.Id, AlertTypes.MissedReport, true);

        var fakeClient = new FakeHaloPsaClient();
        await new PsaTicketService(fakeClient).CreateTicketAsync(context, alert);

        Assert.Equal(1, fakeClient.CreateCallCount);
    }

    [Fact]
    public async Task CreateTicketAsync_UsesTheGlobalRule_ForADomainWithItsOwnHaloClient_IgnoringTheGroupsOverride()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var (group, domain, alert) = await SeedMappedDomainAsync(context);
        domain.HaloClientId = 99;
        await context.SaveChangesAsync();
        await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, true);
        await AlertTicketRuleService.SetForGroupAsync(context, group.Id, AlertTypes.MissedReport, false);

        var fakeClient = new FakeHaloPsaClient();
        await new PsaTicketService(fakeClient).CreateTicketAsync(context, alert);

        Assert.Equal(1, fakeClient.CreateCallCount);
    }

    [Fact]
    public async Task CloseTicketAsync_StillClosesAnExistingTicket_EvenWhenTheRuleIsNowOff()
    {
        // Rules only decide whether a ticket is created. A ticket that already exists is closed when its alert resolves.
        await EnableHaloAsync();
        await using var context = CreateContext();
        var (group, _, alert) = await SeedMappedDomainAsync(context);
        alert.ExternalTicketProvider = "HaloPSA";
        alert.ExternalTicketId = "1000";
        await context.SaveChangesAsync();
        await AlertTicketRuleService.SetGlobalAsync(context, AlertTypes.MissedReport, false);
        await AlertTicketRuleService.SetForGroupAsync(context, group.Id, AlertTypes.MissedReport, false);

        var fakeClient = new FakeHaloPsaClient();
        await new PsaTicketService(fakeClient).CloseTicketAsync(context, alert);

        Assert.Equal(1, fakeClient.CloseCallCount);
    }
```

- [ ] **Step 2: Run to verify the new tests fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaTicketServiceTests"`
Expected: the "off" and "Never" tests FAIL (a ticket is still created); the "Always", "own client" and "close" tests pass already.

- [ ] **Step 3: Implement**

In `src/DotMarc/Notifications/PsaTicketService.cs`, after the block that returns when `haloClientId is null` (line 34) and before the `ticketAlreadyOpen` comment, insert:

```csharp
        // Only this alert type's rules matter, and of the group rules only the deciding group's (the one whose
        // Halo client the ticket would go to), so read just those plus the global ones.
        var decidingGroupId = HaloClientResolver.ResolveGroup(domain)?.Id;
        var rules = await context.AlertTicketRules
            .AsNoTracking()
            .Where(rule => rule.AlertType == alert.AlertType && (rule.GroupId == null || rule.GroupId == decidingGroupId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!AlertTicketPolicy.ShouldCreateTicket(alert.AlertType, domain, rules))
        {
            return;
        }

```

- [ ] **Step 4: Run the tests**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PsaTicketServiceTests|FullyQualifiedName~AlertingService"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Notifications/PsaTicketService.cs test/DotMarc.Tests/Notifications/PsaTicketServiceTests.cs
git commit -m "Skip HaloPSA ticket creation when the alert type's ticket rule says no"
```

---

### Task 5: Global rules panel on Alert settings

**Files:**
- Modify: `src/DotMarc/Components/Pages/AlertsSettings.razor` (new panel between the PSA panel and the "Test the integration" panel; new `@code` members)

**Interfaces:**
- Consumes: `AlertTypes.All`, `AlertTypeInfo`, `AlertTicketRuleService.GetGlobalAsync` and `SetGlobalAsync`; the page's existing `DbFactory`, `Snackbar`, `Logger`, `_haloSettings`.
- Produces: no new public API.

- [ ] **Step 1: Add the state and handlers**

In the `@code` block of `AlertsSettings.razor`, next to `_haloAgents`, add:

```csharp
    private Dictionary<string, bool> _globalTicketRules = [];

    private bool TicketRuleValue(AlertTypeInfo alertType) =>
        _globalTicketRules.TryGetValue(alertType.Key, out var createTicket) ? createTicket : alertType.CreatesTicketByDefault;

    private async Task SetTicketRuleAsync(AlertTypeInfo alertType, bool createTicket)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await AlertTicketRuleService.SetGlobalAsync(db, alertType.Key, createTicket);
            _globalTicketRules[alertType.Key] = createTicket;
            Snackbar.Add($"{alertType.DisplayName}: tickets {(createTicket ? "on" : "off")}.", Severity.Success);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Saving the ticket rule for {AlertType} failed", alertType.Key);
            Snackbar.Add("Couldn't save that ticket rule. Try again.", Severity.Error);
        }
    }
```

In `OnInitializedAsync`, after `_haloSettings = await HaloPsaSettingsService.GetAsync(db);` add:

```csharp
        _globalTicketRules = await AlertTicketRuleService.GetGlobalAsync(db);
```

- [ ] **Step 2: Add the panel**

Between the closing `</MudPaper>` of the PSA panel and `<MudPaper Class="pa-4 mt-4" Elevation="1">` that starts "Test the integration", insert:

```razor
    @if (_haloSettings.Enabled)
    {
        <MudPaper Class="pa-4 mt-4">
            <MudText Typo="Typo.h5" Class="mb-1">Which alerts create tickets</MudText>
            <MudText Typo="Typo.body2" Class="mud-text-secondary mb-3">
                Turn off an alert type to stop it opening HaloPSA tickets. The alert is still recorded and any Teams or webhook
                notification still goes out. A group can override this for its own client from Manage groups. A domain that has its own
                Halo client set on Manage domains uses these settings.
            </MudText>
            @foreach (var alertType in AlertTypes.All)
            {
                <div class="d-flex align-center justify-space-between py-2" style="gap: 1rem; border-top: 1px solid var(--mud-palette-lines-default);">
                    <div>
                        <MudText Typo="Typo.subtitle2">@alertType.DisplayName</MudText>
                        <MudText Typo="Typo.caption" Class="mud-text-secondary">@alertType.Description</MudText>
                    </div>
                    <MudSwitch T="bool" Value="@TicketRuleValue(alertType)" ValueChanged="@(createTicket => SetTicketRuleAsync(alertType, createTicket))"
                               Color="Color.Primary" Label="@(TicketRuleValue(alertType) ? "Creates tickets" : "No tickets")" />
                </div>
            }
        </MudPaper>
    }

```

- [ ] **Step 3: Build**

Run: `dotnet build src/DotMarc`
Expected: `Build succeeded` with no warnings.

- [ ] **Step 4: Commit**

```bash
git add src/DotMarc/Components/Pages/AlertsSettings.razor
git commit -m "Add a panel on Alert settings to choose which alert types create tickets"
```

(The browser check for this panel and the next screen is Task 7.)

---

### Task 6: Per-group dialog and badge on Manage groups

**Files:**
- Create: `src/DotMarc/Components/Dialogs/GroupTicketRulesDialog.razor`
- Modify: `src/DotMarc/Components/Pages/ManageGroups.razor` (a new column, a state field, a load step, an open handler)

**Interfaces:**
- Consumes: `AlertTypes.All`, `AlertTicketRuleService.GetGlobalAsync`, `GetForGroupAsync`, `SetForGroupAsync`, `CountOverridesByGroupAsync`; `IMudDialogInstance`.
- Produces: `GroupTicketRulesDialog` with `[Parameter] int GroupId` and `[Parameter] string GroupName`.

- [ ] **Step 1: Create the dialog**

Create `src/DotMarc/Components/Dialogs/GroupTicketRulesDialog.razor`:

```razor
@using DotMarc.Data
@using DotMarc.Notifications
@using Microsoft.EntityFrameworkCore
@inject IDbContextFactory<DotMarcDbContext> DbFactory
@inject ISnackbar Snackbar
@inject ILogger<GroupTicketRulesDialog> Logger

<MudDialog>
    <TitleContent>Ticket rules for @GroupName</TitleContent>
    <DialogContent>
        <MudText Typo="Typo.body2" Class="mud-text-secondary mb-3">
            Choose which alerts create HaloPSA tickets for this group's Halo client. <i>Use default</i> follows the setting on Alert settings.
        </MudText>
        @if (_loaded)
        {
            @foreach (var alertType in AlertTypes.All)
            {
                <MudSelect T="int" Label="@alertType.DisplayName" HelperText="@alertType.Description" Variant="Variant.Outlined" Class="mb-3"
                           Value="ChoiceFor(alertType)" ValueChanged="@(choice => SetChoiceAsync(alertType, choice))">
                    <MudSelectItem T="int" Value="Inherit">Use default (currently @(DefaultFor(alertType) ? "Yes" : "No"))</MudSelectItem>
                    <MudSelectItem T="int" Value="Always">Always create tickets</MudSelectItem>
                    <MudSelectItem T="int" Value="Never">Never create tickets</MudSelectItem>
                </MudSelect>
            }
        }
    </DialogContent>
    <DialogActions>
        <MudButton Color="Color.Primary" Variant="Variant.Filled" OnClick="Done">Done</MudButton>
    </DialogActions>
</MudDialog>

@code {
    private const int Inherit = 0;
    private const int Always = 1;
    private const int Never = 2;

    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public int GroupId { get; set; }
    [Parameter] public string GroupName { get; set; } = "";

    private Dictionary<string, bool> _globalRules = [];
    private Dictionary<string, bool> _groupRules = [];
    private bool _loaded;

    protected override async Task OnInitializedAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _globalRules = await AlertTicketRuleService.GetGlobalAsync(db);
        _groupRules = await AlertTicketRuleService.GetForGroupAsync(db, GroupId);
        _loaded = true;
    }

    private bool DefaultFor(AlertTypeInfo alertType) =>
        _globalRules.TryGetValue(alertType.Key, out var createTicket) ? createTicket : alertType.CreatesTicketByDefault;

    private int ChoiceFor(AlertTypeInfo alertType) =>
        _groupRules.TryGetValue(alertType.Key, out var createTicket) ? (createTicket ? Always : Never) : Inherit;

    private async Task SetChoiceAsync(AlertTypeInfo alertType, int choice)
    {
        bool? createTicket = choice switch
        {
            Always => true,
            Never => false,
            _ => null
        };

        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await AlertTicketRuleService.SetForGroupAsync(db, GroupId, alertType.Key, createTicket);
            if (createTicket is { } value)
            {
                _groupRules[alertType.Key] = value;
            }
            else
            {
                _groupRules.Remove(alertType.Key);
            }
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Saving the ticket rule for group {GroupId}, alert type {AlertType} failed", GroupId, alertType.Key);
            Snackbar.Add("Couldn't save that ticket rule. Try again.", Severity.Error);
        }
    }

    private void Done() => MudDialog.Close(DialogResult.Ok(true));
}
```

- [ ] **Step 2: Add the state and loading to ManageGroups**

In `src/DotMarc/Components/Pages/ManageGroups.razor`, in `@code` next to `_haloClients` add:

```csharp
    private Dictionary<int, int> _ticketRuleOverrideCounts = [];
```

In `LoadAsync`, after `_groups = ...ToListAsync();` add:

```csharp
        _ticketRuleOverrideCounts = await AlertTicketRuleService.CountOverridesByGroupAsync(db);
```

Add the handler beside `SetHaloClientIdAsync`. Before writing it, view the existing dialog calls at lines 276-282 of this file and mirror their `DialogParameters` and `ShowAsync` style:

```csharp
    private async Task OpenTicketRulesAsync(GroupRow row)
    {
        var parameters = new DialogParameters<GroupTicketRulesDialog>
        {
            { dialog => dialog.GroupId, row.Id },
            { dialog => dialog.GroupName, row.Name }
        };
        var dialogRef = await DialogService.ShowAsync<GroupTicketRulesDialog>("Ticket rules", parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        await dialogRef.Result;

        // The dialog saves each change itself, so refresh the badge counts when it closes.
        await using var db = await DbFactory.CreateDbContextAsync();
        _ticketRuleOverrideCounts = await AlertTicketRuleService.CountOverridesByGroupAsync(db);
    }
```

- [ ] **Step 3: Add the column**

In the groups `MudTable`, add a header after the `Halo Client` header (inside the same `@if (_haloConfigured)`):

```razor
                @if (_haloConfigured)
                {
                    <MudTh>Ticket rules</MudTh>
                }
```

and a cell after the Halo client cell:

```razor
                @if (_haloConfigured)
                {
                    <MudTd>
                        @if (context.HaloClientId.HasValue)
                        {
                            <MudButton Variant="Variant.Text" Size="Size.Small" StartIcon="@Icons.Material.Filled.Tune" OnClick="@(() => OpenTicketRulesAsync(context))">
                                Ticket rules
                            </MudButton>
                            @if (_ticketRuleOverrideCounts.TryGetValue(context.Id, out var overrideCount))
                            {
                                <MudChip T="string" Size="Size.Small" Color="Color.Info">@overrideCount @(overrideCount == 1 ? "override" : "overrides")</MudChip>
                            }
                        }
                        else
                        {
                            <MudText Typo="Typo.caption" Class="mud-text-secondary">Set a Halo client first</MudText>
                        }
                    </MudTd>
                }
```

- [ ] **Step 4: Build**

Run: `dotnet build src/DotMarc`
Expected: `Build succeeded` with no warnings.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Components/Dialogs/GroupTicketRulesDialog.razor src/DotMarc/Components/Pages/ManageGroups.razor
git commit -m "Add per-group ticket rules to Manage groups"
```

---

### Task 7: Docs, FAQ and browser verification

**Files:**
- Modify: `website/docs/psa-integration.mdx`
- Create: `website/data/faqs/ticket-rules.yaml`

**Interfaces:**
- Consumes: the finished feature.
- Produces: user-facing documentation.

- [ ] **Step 1: Document it**

In `website/docs/psa-integration.mdx`, before the `## Route tickets to the right client` heading, insert:

```markdown
## Choose which alerts create tickets

By default every alert type opens a ticket. To change that, open **Alert settings**. Once ticket sync is
enabled, a **Which alerts create tickets** panel lists each alert type with a switch. Turning a type off stops
it opening tickets. The alert is still recorded, and any Teams or webhook notification still goes out.

A group can override this for its own client. On **Manage groups**, a group that has a Halo client set has a
**Ticket rules** button. For each alert type choose *Use default* (follow Alert settings), *Always create
tickets* or *Never create tickets*. A group with overrides shows a badge such as "2 overrides".

Two details worth knowing:

* If a domain belongs to several groups, the rules of the group its ticket goes to apply: the one with the
  lowest id among the domain's groups that have a Halo client. Other groups' rules are ignored.
* A domain with its own Halo client set on **Manage domains** isn't covered by a group's rules, so it follows
  the settings on Alert settings.

Changing a rule affects alerts raised from then on. It doesn't create tickets for alerts that are already open,
and it doesn't stop an existing ticket from closing when its alert resolves.
```

Also update the "What syncs" first bullet in the same file: change `- Alert fires → ticket created, using` to `- Alert fires → ticket created (unless its alert type is turned off, see above), using`.

- [ ] **Step 2: Add the FAQ**

Create `website/data/faqs/ticket-rules.yaml`:

```yaml
question: Can I stop certain alerts, or certain clients, from creating HaloPSA tickets?
answer: |
  Yes. On **Alert settings**, the **Which alerts create tickets** panel has a switch for each alert type. Turn a
  type off and it stops opening tickets. The alert is still recorded and any Teams or webhook notification
  still goes out.

  For one client, open **Manage groups** and use **Ticket rules** on that client's group. Each alert type can
  follow the default, always create tickets, or never create them. The button appears once the group has a
  Halo client set.

  See [PSA integration: Choose which alerts create tickets](/docs/psa-integration#choose-which-alerts-create-tickets)
  for the details, including how domains in several groups are handled.
category: Alerts & PSA integration
order: 6
```

- [ ] **Step 3: Build the docs**

Run: `cd website && npm run build`
Expected: `Generated static files in "build"` with no errors. Also run `grep -c $'\xe2\x80\x94' website/docs/psa-integration.mdx website/data/faqs/ticket-rules.yaml` (that byte sequence is an em dash), expected `0` for both.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test` from the repo root.
Expected: PASS. Baseline was 687 tests; this plan adds roughly 40.

- [ ] **Step 5: Verify in a browser**

Start a throwaway stack, seed a mapped group, and check both screens:

```bash
docker run -d --name dotmarc-ui-check -e POSTGRES_USER=dotmarc -e POSTGRES_PASSWORD=dotmarc -e POSTGRES_DB=dotmarc -p 55432:5432 postgres:17
ConnectionStrings__DotMarc="Host=localhost;Port=55432;Database=dotmarc;Username=dotmarc;Password=dotmarc" Demo__Enabled=true ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5195 dotnet run --project src/DotMarc --no-launch-profile
```

Once the app is up (it migrates and seeds demo data), enable Halo and give one group a client directly in the database, since no Halo tenant is needed for these screens:

```bash
docker exec dotmarc-ui-check psql -U dotmarc -d dotmarc -c "update \"HaloPsaSettings\" set \"Enabled\" = true, \"ClientSecretConfigured\" = true;" -c "update \"Groups\" set \"HaloClientId\" = 7 where \"Id\" = (select min(\"Id\") from \"Groups\");"
```

With the Playwright Edge tools (`mcp__playwright-edge__*`; never install Chrome):

1. Open `http://localhost:5195/demo?ReturnUrl=%2Falerts%2Fsettings`, click **Continue as Demo Admin**, then open `/alerts/settings`. Confirm the **Which alerts create tickets** panel lists four rows, each on. Turn **Missing DMARC report** off. Confirm a snackbar, reload the page, and confirm it is still off.
2. Open `/groups`. Confirm the group with a client shows a **Ticket rules** button and the others show "Set a Halo client first". Open the dialog, and confirm the **Missing DMARC report** row says "Use default (currently No)". Set **TLS delivery failures** to *Never create tickets*, click **Done**, and confirm the badge reads "1 override".
3. Reopen the dialog, set that row back to *Use default*, click **Done**, and confirm the badge disappears.
4. Confirm in the database: `docker exec dotmarc-ui-check psql -U dotmarc -d dotmarc -c "select * from \"AlertTicketRules\";"` shows only the one global row (`MissedReport`, `CreateTicket` false).

Clean up: stop the app, `docker container remove --force dotmarc-ui-check`, close the browser, and delete any screenshot written into the repo root.

- [ ] **Step 6: Commit**

```bash
git add website/docs/psa-integration.mdx website/data/faqs/ticket-rules.yaml
git commit -m "Document which alerts create tickets and how to override it per group"
```

---

## Self-Review Notes

* **Spec coverage:** registry and AlertingService keys (Task 1); rules table, indexes, cascade, no seed (Task 2); decision function and `ResolveGroup` (Task 3); wiring into `PsaTicketService`, alert still recorded, closing unaffected (Task 4); global panel, shown only when ticket sync is enabled, immediate save (Task 5); per-group dialog, three choices with the current default shown, badge, button only for groups with a client (Task 6); docs, FAQ, browser check and the full suite (Task 7). The spec's errors paragraph relies on the existing try/catch around ticket creation in `AlertingService`, so it needs no code.
* **Guard test:** the spec's "every alert type raised is in the registry" is met by the `AlertTypesTests` reflection check plus the `EnsureAlertAsync` guard that the existing `AlertingService` tests exercise.
* **Type consistency:** `AlertTypeInfo`, `AlertTypes.All/Find`, `AlertTicketRule`, `AlertTicketRuleService` (six methods), `AlertTicketPolicy.ShouldCreateTicket` and `HaloClientResolver.ResolveGroup` keep the same names and signatures wherever they are used.
