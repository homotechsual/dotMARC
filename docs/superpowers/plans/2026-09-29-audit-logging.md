# Audit Logging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record who changed what, who signed in (or was refused), and which pages people opened, and let admins
browse, filter, export and set retention for that log.

**Architecture:** A new `DotMarc.Audit` namespace holds the `AuditEntry` table, small building blocks (`AuditActor`,
`AuditTarget`, `AuditChanges`, `AuditLog`) and the page support (`AuditFilter`, `AuditQuery`, `AuditCsv`). Every
mutating management and settings service takes an `AuditActor` straight after its `DbContext` and adds its entry to
the same save as the change. Sign-ins, page views and a few page actions are recorded best-effort through a
singleton `AuditRecorder` that uses its own context. A daily `AuditRetentionService` deletes expired entries.

**Tech Stack:** .NET 10, Blazor Server with MudBlazor 9.8, EF Core 10 on PostgreSQL (Npgsql), xUnit 2.9 with a
Testcontainers Postgres fixture (Docker must be running for the database tests).

**Spec:** `docs/superpowers/specs/2026-09-29-audit-logging-design.md`

## Global Constraints

- The actor is always the **second parameter, straight after the `DotMarcDbContext`**, on every audited method:
  `Method(DotMarcDbContext context, AuditActor actor, ...)`.
- Action codes come only from `AuditActions` constants. Never write an action string inline.
- Secrets are never stored. A changed secret is `AuditChanges.Secret(name, changed)`, with no values. Webhook URLs
  (Teams, generic) carry tokens, so they are treated as secrets too.
- Retention: each kind is 1 to 3650 days, or null for keep forever. Defaults: changes 365, sign-ins 365, page
  views 90.
- Only what actually changed is recorded: refused actions and no-op saves record nothing.
- Nothing edits or deletes entries except retention and the demo reset.
- User-facing text (UI copy, summaries, docs) uses no em dashes.
- Meaningful variable names everywhere, including tests and scripts: no `x`, `r`, `m`, `e` for anything but a
  one-token lambda over a clearly named collection.
- Follow the code around you: static services on a caller-supplied context, `ConfigureAwait(false)` in non-UI
  code, `/// <summary>` comments that explain why.
- Commit after each task with a plain sentence message, like the repository's history (no `feat:` prefixes).

Commands used throughout (run from the repository root):

- Build: `dotnet build src/DotMarc/DotMarc.csproj -nologo -v q`
- One test class: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ClassName" -nologo -v q`
- Everything: `dotnet test test/DotMarc.Tests -nologo -v q`

## Review Focus

1. **Search text containing `%`, `_` or `\`** (for example a summary search for "100%") must match those
   characters literally, not act as wildcards. Pinned in Task 10 (`AuditQueryTests`).
2. **Exported cells containing commas, quotes, line breaks, or starting with `=`, `+`, `-` or `@`** must survive
   as one cell and never run as a spreadsheet formula. Pinned in Task 10 (`AuditCsvTests`).
3. **A "To" date filter includes that whole day**, so an entry at 23:59 UTC on the To date is shown. Pinned in
   Task 10 (`AuditQueryTests`).
4. **A sign-in whose token carries no object id, name or email** is recorded as a refused "Unknown user" rather
   than throwing and breaking sign-in. Pinned in Task 2 (`AuditActorTests`) and Task 9 (`SignInAuditorTests`).
5. **A page URL with a query string or fragment** (DNS push state travels in the query string) is recorded as the
   bare path only. Pinned in Task 9 (`PageViewPathsTests`).

---

### Task 1: The audit tables

**Files:**
- Create: `src/DotMarc/Audit/AuditEntry.cs`
- Create: `src/DotMarc/Audit/AuditSettings.cs`
- Create: `src/DotMarc/Audit/AuditJson.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs` (DbSets, model configuration, seed row)
- Create: `src/DotMarc/Migrations/<timestamp>_AddAuditLog.cs` (generated)
- Test: `test/DotMarc.Tests/Audit/AuditEntryMappingTests.cs`

**Interfaces:**
- Produces: `AuditEntryKind { Change, SignIn, PageView }`, `AuditActorKind { User, System }`,
  `record AuditFieldChange(string Field, string? Old, string? New, bool Secret = false)`, class `AuditEntry`
  (properties below), class `AuditSettings`, `DotMarcDbContext.AuditEntries`, `DotMarcDbContext.AuditSettings`.

- [ ] **Step 1: Write the failing test**

Create `test/DotMarc.Tests/Audit/AuditEntryMappingTests.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Audit;

[Collection("Postgres")]
public sealed class AuditEntryMappingTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AuditEntryMappingTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task AnEntry_RoundTripsItsKindsAndFieldChanges()
    {
        await using (var context = CreateContext())
        {
            context.AuditEntries.Add(new AuditEntry
            {
                OccurredUtc = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero),
                Kind = AuditEntryKind.Change,
                ActorKind = AuditActorKind.User,
                ActorEmail = "admin@example.com",
                ActorName = "Test Admin",
                Action = "group.renamed",
                TargetType = "Group",
                TargetId = "7",
                TargetName = "Client B",
                Summary = "Renamed group Client A to Client B",
                Changes = [new AuditFieldChange("Name", "Client A", "Client B"), new AuditFieldChange("Client secret", null, null, Secret: true)]
            });
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditEntryKind.Change, entry.Kind);
        Assert.Equal(AuditActorKind.User, entry.ActorKind);
        Assert.Equal(
            [new AuditFieldChange("Name", "Client A", "Client B"), new AuditFieldChange("Client secret", null, null, true)],
            entry.Changes);
    }

    [Fact]
    public async Task KindsAreStoredAsText()
    {
        await using (var context = CreateContext())
        {
            context.AuditEntries.Add(new AuditEntry { OccurredUtc = DateTimeOffset.UtcNow, Kind = AuditEntryKind.SignIn, ActorName = "Someone", Action = "signin.succeeded", Summary = "Someone signed in" });
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        var storedKind = await verify.Database.SqlQueryRaw<string>("SELECT \"Kind\" AS \"Value\" FROM \"AuditEntries\"").SingleAsync();
        Assert.Equal("SignIn", storedKind);
    }

    [Fact]
    public async Task TheRetentionSettingsRowIsSeededWithTheDefaults()
    {
        await using var context = CreateContext();

        var settings = await context.AuditSettings.SingleAsync();

        Assert.Equal((365, 365, 90), (settings.ChangeRetentionDays, settings.SignInRetentionDays, settings.PageViewRetentionDays));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AuditEntryMappingTests" -nologo -v q`
Expected: build FAILS, `The type or namespace name 'Audit' does not exist`.

- [ ] **Step 3: Write the model**

Create `src/DotMarc/Audit/AuditEntry.cs`:

```csharp
namespace DotMarc.Audit;

public enum AuditEntryKind { Change, SignIn, PageView }

/// <summary>Who did something. <c>ApiKey</c> joins this when the public API is built.</summary>
public enum AuditActorKind { User, System }

/// <summary>One changed field in an audit entry. For a secret, <see cref="Old"/> and <see cref="New"/> are null and
/// <see cref="Secret"/> is true, so the value itself is never stored.</summary>
public sealed record AuditFieldChange(string Field, string? Old, string? New, bool Secret = false);

/// <summary>One thing that happened: a change someone made, a sign-in, or a page someone opened. Entries are never
/// edited, and leave only through retention (<see cref="AuditRetention"/>) or the demo reset.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset OccurredUtc { get; set; }
    public AuditEntryKind Kind { get; set; }
    public AuditActorKind ActorKind { get; set; }
    public string? ActorObjectId { get; set; }

    /// <summary>The email as it was at the time, so the entry stays readable after the grant is revoked.</summary>
    public string? ActorEmail { get; set; }

    public string ActorName { get; set; } = "";

    /// <summary>A stable code from <see cref="AuditActions"/>.</summary>
    public string Action { get; set; } = "";

    public string? TargetType { get; set; }
    public string? TargetId { get; set; }

    /// <summary>The target's display name at the time, for example the domain name.</summary>
    public string? TargetName { get; set; }

    public string Summary { get; set; } = "";
    public List<AuditFieldChange> Changes { get; set; } = [];
}
```

(`AuditRetention` and `AuditActions` arrive in later tasks; a `<see cref>` to a missing type is only a doc warning.
If warnings are treated as errors, drop the cref until Task 12.)

Create `src/DotMarc/Audit/AuditSettings.cs`:

```csharp
namespace DotMarc.Audit;

/// <summary>The single row of audit retention settings, seeded by DotMarcDbContext. A null period keeps that kind
/// of entry forever.</summary>
public sealed class AuditSettings
{
    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 3650;

    public int Id { get; set; }
    public int? ChangeRetentionDays { get; set; } = 365;
    public int? SignInRetentionDays { get; set; } = 365;
    public int? PageViewRetentionDays { get; set; } = 90;
}
```

Create `src/DotMarc/Audit/AuditJson.cs`:

```csharp
using System.Text.Json;

namespace DotMarc.Audit;

/// <summary>How <see cref="AuditEntry.Changes"/> is written to its jsonb column: camelCase, so the stored shape is
/// <c>{ "field", "old", "new", "secret" }</c> as the spec describes.</summary>
internal static class AuditJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
```

- [ ] **Step 4: Map it in the context**

In `src/DotMarc/Data/DotMarcDbContext.cs`, add `using System.Text.Json;` and `using DotMarc.Audit;` at the top (keep
existing usings). After the line `public DbSet<GoogleCloudDnsSettings> GoogleCloudDnsSettings => Set<GoogleCloudDnsSettings>();`
add:

```csharp
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<AuditSettings> AuditSettings => Set<AuditSettings>();
```

In `OnModelCreating`, directly after `modelBuilder.Entity<NotificationSettings>().HasData(new NotificationSettings { Id = 1 });`
add:

```csharp
        modelBuilder.Entity<AuditSettings>().HasData(new AuditSettings { Id = 1 });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.Property(auditEntry => auditEntry.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(auditEntry => auditEntry.ActorKind).HasConversion<string>().HasMaxLength(16);
            entity.Property(auditEntry => auditEntry.Action).HasMaxLength(64);
            entity.Property(auditEntry => auditEntry.TargetType).HasMaxLength(32);

            // Stored as jsonb through a converter, like Role.Permissions uses one for its list, so the column can be
            // queried in SQL later without a separate changes table.
            entity.Property(auditEntry => auditEntry.Changes)
                .HasColumnType("jsonb")
                .HasConversion(
                    changes => JsonSerializer.Serialize(changes, AuditJson.Options),
                    stored => JsonSerializer.Deserialize<List<AuditFieldChange>>(stored, AuditJson.Options) ?? new List<AuditFieldChange>())
                .Metadata.SetValueComparer(new ValueComparer<List<AuditFieldChange>>(
                    (left, right) => left!.SequenceEqual(right!),
                    changes => changes.Aggregate(0, (hash, change) => HashCode.Combine(hash, change.GetHashCode())),
                    changes => changes.ToList()));

            entity.HasIndex(auditEntry => auditEntry.OccurredUtc).IsDescending();
            entity.HasIndex(auditEntry => new { auditEntry.Kind, auditEntry.OccurredUtc });
            entity.HasIndex(auditEntry => auditEntry.ActorEmail);
            entity.HasIndex(auditEntry => new { auditEntry.TargetType, auditEntry.TargetId });
        });
```

(`ValueComparer` is already imported for `Role.Permissions`.)

- [ ] **Step 5: Generate the migration**

```powershell
dotnet tool restore
$env:ConnectionStrings__DotMarc = 'Host=localhost;Database=design_time;Username=design;Password=design'
dotnet dotnet-ef migrations add AddAuditLog --project src/DotMarc --startup-project src/DotMarc
Remove-Item Env:ConnectionStrings__DotMarc
```

Expected: `Done.` and new files `src/DotMarc/Migrations/*_AddAuditLog.cs`, `*_AddAuditLog.Designer.cs`, plus an
updated `DotMarcDbContextModelSnapshot.cs`. Open the migration and check it creates `AuditEntries` (with a `jsonb`
`Changes` column and the four indexes) and `AuditSettings` with an `InsertData` row of 365, 365, 90. Generating a
migration doesn't connect to the database, so the dummy connection string is only there to let the app's host start.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AuditEntryMappingTests" -nologo -v q`
Expected: PASS, 3 tests.

- [ ] **Step 7: Commit**

```powershell
git add src/DotMarc/Audit src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Migrations test/DotMarc.Tests/Audit
git commit -m "Add the audit entry and retention settings tables"
```

---

### Task 2: Audit building blocks

**Files:**
- Create: `src/DotMarc/Audit/AuditActor.cs`, `AuditTarget.cs`, `AuditActions.cs`, `AuditChanges.cs`, `AuditLog.cs`,
  `AuditActorAccessor.cs`
- Create: `src/DotMarc/Security/UserClaims.cs`
- Modify: `src/DotMarc/Security/UserAccessClaimsTransformation.cs` (use `UserClaims.GetEmail`)
- Modify: `src/DotMarc/Program.cs` (register `AuditActorAccessor`)
- Modify: `src/DotMarc/Components/_Imports.razor` (add `@using DotMarc.Audit`)
- Create: `test/DotMarc.Tests/Internal/TestActors.cs`
- Test: `test/DotMarc.Tests/Audit/AuditChangesTests.cs`, `AuditActorTests.cs`, `AuditLogTests.cs`

**Interfaces:**
- Consumes: Task 1's `AuditEntry`, `AuditEntryKind`, `AuditActorKind`, `AuditFieldChange`, `DotMarcDbContext.AuditEntries`.
- Produces:
  - `record AuditActor(AuditActorKind Kind, string Name, string? ObjectId = null, string? Email = null)` with
    `static AuditActor ForSystem(string name)`, `static AuditActor ForUser(string? objectId, string? email, string? displayName)`,
    `static AuditActor FromPrincipal(ClaimsPrincipal principal)`.
  - `record AuditTarget(string Type, string? Id, string? Name)` with `For(Domain)`, `For(Group)`, `For(Tag)`,
    `For(Role)`, `For(UserAccess)`, `Settings(string name)`, `DomainNamed(string domainName)`.
  - `static class AuditActions` (constants listed below, plus `IReadOnlyList<(string Action, string Label)> All`).
  - `sealed class AuditChanges` with `Field<T>(string name, T oldValue, T newValue)`,
    `Set(string name, IEnumerable<string> oldValues, IEnumerable<string> newValues)`, `Secret(string name, bool changed)`,
    `bool Any`, `IReadOnlyList<AuditFieldChange> Items`.
  - `static class AuditLog` with `AuditEntry Create(AuditActor actor, AuditEntryKind kind, string action, AuditTarget? target, string summary, IReadOnlyList<AuditFieldChange>? changes = null)`,
    `void Record(DotMarcDbContext context, AuditActor actor, string action, AuditTarget? target, string summary, AuditChanges? changes = null)`,
    `Task SaveAndRecordAsync(DotMarcDbContext context, Action recordEntries, CancellationToken cancellationToken)`.
  - `sealed class AuditActorAccessor` (scoped) with `Task<AuditActor> GetAsync()`.
  - `static class UserClaims` with `string? GetEmail(ClaimsPrincipal principal)`.
  - Test helper `TestActors.Admin`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Audit/AuditChangesTests.cs`:

```csharp
using DotMarc.Audit;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class AuditChangesTests
{
    [Fact]
    public void Field_RecordsOnlyValuesThatDiffer()
    {
        var changes = new AuditChanges()
            .Field("Name", "Client A", "Client B")
            .Field("Halo client", (int?)37, (int?)37);

        Assert.Equal([new AuditFieldChange("Name", "Client A", "Client B")], changes.Items);
        Assert.True(changes.Any);
    }

    [Fact]
    public void Field_FormatsBooleansAsYesAndNo_AndNullAsNull()
    {
        var changes = new AuditChanges()
            .Field("Monitored", true, false)
            .Field("Halo client", (int?)null, (int?)12);

        Assert.Equal(
            [new AuditFieldChange("Monitored", "Yes", "No"), new AuditFieldChange("Halo client", null, "12")],
            changes.Items);
    }

    [Fact]
    public void Set_IgnoresOrderAndRecordsTheValuesSorted()
    {
        var unchanged = new AuditChanges().Set("Groups", ["Beta", "Alpha"], ["Alpha", "Beta"]);
        var changed = new AuditChanges().Set("Groups", ["Beta"], ["gamma", "Beta", "alpha"]);

        Assert.False(unchanged.Any);
        Assert.Equal([new AuditFieldChange("Groups", "Beta", "alpha, Beta, gamma")], changed.Items);
    }

    [Fact]
    public void Set_RecordsNoneForAnEmptySet()
    {
        var changes = new AuditChanges().Set("Groups", [], ["Client A"]);

        Assert.Equal([new AuditFieldChange("Groups", "None", "Client A")], changes.Items);
    }

    [Fact]
    public void Secret_RecordsThatItChangedWithoutAnyValue()
    {
        var changes = new AuditChanges()
            .Secret("Client secret", changed: true)
            .Secret("Webhook secret", changed: false);

        Assert.Equal([new AuditFieldChange("Client secret", null, null, Secret: true)], changes.Items);
    }
}
```

Create `test/DotMarc.Tests/Audit/AuditActorTests.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Audit;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class AuditActorTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Test", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role));

    [Fact]
    public void FromPrincipal_UsesTheNameEmailAndObjectId()
    {
        var actor = AuditActor.FromPrincipal(Principal(
            new Claim("oid", "0f1e2d3c-0000-0000-0000-000000000001"),
            new Claim("preferred_username", "sam@contoso.com"),
            new Claim(ClaimTypes.Name, "Sam Jones")));

        Assert.Equal(new AuditActor(AuditActorKind.User, "Sam Jones", "0f1e2d3c-0000-0000-0000-000000000001", "sam@contoso.com"), actor);
    }

    [Fact]
    public void FromPrincipal_FallsBackToTheEmail_WhenThereIsNoName()
    {
        var actor = AuditActor.FromPrincipal(Principal(new Claim("preferred_username", "sam@contoso.com")));

        Assert.Equal("sam@contoso.com", actor.Name);
    }

    [Fact]
    public void FromPrincipal_WithNoIdentityClaims_IsAnUnknownUser()
    {
        var actor = AuditActor.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Equal(new AuditActor(AuditActorKind.User, "Unknown user"), actor);
    }

    [Fact]
    public void ForSystem_NamesTheSystemActor()
    {
        Assert.Equal(new AuditActor(AuditActorKind.System, "Startup"), AuditActor.ForSystem("Startup"));
    }
}
```

Create `test/DotMarc.Tests/Audit/AuditLogTests.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Audit;

[Collection("Postgres")]
public sealed class AuditLogTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AuditLogTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task Record_IsSavedByTheCallersOwnSave()
    {
        await using (var context = CreateContext())
        {
            AuditLog.Record(context, TestActors.Admin, AuditActions.DomainsReordered, null, "Reordered domains");
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal((AuditEntryKind.Change, "Test Admin", "admin@example.com"), (entry.Kind, entry.ActorName, entry.ActorEmail));
    }

    [Fact]
    public async Task SaveAndRecordAsync_GivesTheEntryTheNewRowsId()
    {
        await using (var context = CreateContext())
        {
            var group = new Group { Name = "Client A" };
            context.Groups.Add(group);
            await AuditLog.SaveAndRecordAsync(context, () => AuditLog.Record(context, TestActors.Admin, AuditActions.GroupAdded, AuditTarget.For(group), "Added group Client A"), CancellationToken.None);
        }

        await using var verify = CreateContext();
        var savedGroup = await verify.Groups.SingleAsync();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(savedGroup.Id.ToString(), entry.TargetId);
    }

    [Fact]
    public async Task SaveAndRecordAsync_KeepsNeither_WhenTheEntryCannotBeSaved()
    {
        await using (var context = CreateContext())
        {
            context.Groups.Add(new Group { Name = "Client A" });
            // Action is limited to 64 characters, so this entry fails to save after the group already has.
            var tooLongAction = new string('x', 65);
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                AuditLog.SaveAndRecordAsync(context, () => AuditLog.Record(context, TestActors.Admin, tooLongAction, null, "Broken"), CancellationToken.None));
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.Groups);
        Assert.Empty(verify.AuditEntries);
    }
}
```

Create `test/DotMarc.Tests/Internal/TestActors.cs`:

```csharp
using DotMarc.Audit;

namespace DotMarc.Tests.Internal;

/// <summary>The person tests act as when calling audited services.</summary>
internal static class TestActors
{
    public static readonly AuditActor Admin = AuditActor.ForUser("00000000-0000-0000-0000-00000000000a", "admin@example.com", "Test Admin");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Audit" -nologo -v q`
Expected: build FAILS, `AuditChanges`, `AuditActor`, `AuditLog` not found.

- [ ] **Step 3: Move the email claim lookup somewhere shared**

Create `src/DotMarc/Security/UserClaims.cs`:

```csharp
using System.Security.Claims;

namespace DotMarc.Security;

/// <summary>Reads identity details from a signed-in user's claims, shared by the claims transformation and the
/// audit log so both find the same email.</summary>
public static class UserClaims
{
    /// <summary>Which claim carries the email varies by tenant and token version, and getting it wrong would lock
    /// people out, so this tries preferred_username first (right for the v2.0 delegated flow dotMARC uses), then
    /// the UPN and Email claim types Microsoft.Identity.Web maps for some configurations, then a literal "email"
    /// claim some tenants send instead. An empty claim falls through to the next candidate.</summary>
    public static string? GetEmail(ClaimsPrincipal principal)
    {
        foreach (var claimType in new[] { "preferred_username", ClaimTypes.Upn, ClaimTypes.Email, "email" })
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }
}
```

In `src/DotMarc/Security/UserAccessClaimsTransformation.cs`, replace
`var email = FirstNonEmptyClaim(principal, "preferred_username", ClaimTypes.Upn, ClaimTypes.Email, "email");`
with `var email = UserClaims.GetEmail(principal);`, and delete the private `FirstNonEmptyClaim` method and its doc
comment at the bottom of the class. Leave the long comment above the `email` line in place; it still explains the
choice.

- [ ] **Step 4: Write the building blocks**

Create `src/DotMarc/Audit/AuditActor.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Security;
using Microsoft.Identity.Web;

namespace DotMarc.Audit;

/// <summary>Who an audit entry is about. Captured at the time, so an entry stays readable after the person's
/// grant is revoked or their name changes.</summary>
public sealed record AuditActor(AuditActorKind Kind, string Name, string? ObjectId = null, string? Email = null)
{
    /// <summary>Work nobody clicked, such as "Startup" seeding the initial admins.</summary>
    public static AuditActor ForSystem(string name) => new(AuditActorKind.System, name);

    public static AuditActor ForUser(string? objectId, string? email, string? displayName) =>
        new(AuditActorKind.User,
            FirstNonEmpty(displayName, email, objectId) ?? "Unknown user",
            string.IsNullOrEmpty(objectId) ? null : objectId,
            string.IsNullOrEmpty(email) ? null : email);

    public static AuditActor FromPrincipal(ClaimsPrincipal principal) =>
        ForUser(principal.GetObjectId(), UserClaims.GetEmail(principal), principal.Identity?.Name);

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate));
}
```

Create `src/DotMarc/Audit/AuditTarget.cs`:

```csharp
using System.Globalization;
using DotMarc.Data;

namespace DotMarc.Audit;

/// <summary>What an audit entry is about, with its name as it was at the time.</summary>
public sealed record AuditTarget(string Type, string? Id, string? Name)
{
    public static AuditTarget For(Domain domain) => new("Domain", IdText(domain.Id), domain.Name);
    public static AuditTarget For(Group group) => new("Group", IdText(group.Id), group.Name);
    public static AuditTarget For(Tag tag) => new("Tag", IdText(tag.Id), tag.Name);
    public static AuditTarget For(Role role) => new("Role", IdText(role.Id), role.Name);
    public static AuditTarget For(UserAccess access) => new("UserAccess", IdText(access.Id), access.Email);

    /// <summary>A settings screen, such as "HaloPSA" or "Notifications". There is only one of each, so no id.</summary>
    public static AuditTarget Settings(string name) => new("Settings", null, name);

    /// <summary>A domain known only by name, as on a page view of /domains/{name}.</summary>
    public static AuditTarget DomainNamed(string domainName) => new("Domain", null, domainName);

    private static string IdText(int id) => id.ToString(CultureInfo.InvariantCulture);
}
```

Create `src/DotMarc/Audit/AuditActions.cs`:

```csharp
namespace DotMarc.Audit;

/// <summary>Every action the audit log records. These codes are stored, so they must never change once shipped.</summary>
public static class AuditActions
{
    public const string DomainAdded = "domain.added";
    public const string DomainRemoved = "domain.removed";
    public const string DomainMonitoringChanged = "domain.monitoring_changed";
    public const string DomainHaloClientChanged = "domain.halo_client_changed";
    public const string DomainMtaStsChanged = "domain.mta_sts_changed";
    public const string DomainDkimSelectorsChanged = "domain.dkim_selectors_changed";
    public const string DomainsReordered = "domains.reordered";
    public const string DomainGroupsChanged = "domain.groups_changed";
    public const string DomainTagsChanged = "domain.tags_changed";
    public const string GroupAdded = "group.added";
    public const string GroupRenamed = "group.renamed";
    public const string GroupRemoved = "group.removed";
    public const string GroupHaloClientChanged = "group.halo_client_changed";
    public const string TagAdded = "tag.added";
    public const string TagUpdated = "tag.updated";
    public const string TagRemoved = "tag.removed";
    public const string RoleAdded = "role.added";
    public const string RoleUpdated = "role.updated";
    public const string RoleRemoved = "role.removed";
    public const string AccessGranted = "access.granted";
    public const string AccessUpdated = "access.updated";
    public const string AccessRevoked = "access.revoked";
    public const string TicketRuleGlobalChanged = "ticket_rule.global_changed";
    public const string TicketRuleGroupChanged = "ticket_rule.group_changed";
    public const string NotificationSettingsSaved = "settings.notifications.saved";
    public const string HaloSettingsSaved = "settings.halo.saved";
    public const string CloudflareDnsSettingsSaved = "settings.cloudflare_dns.saved";
    public const string AzureDnsSettingsSaved = "settings.azure_dns.saved";
    public const string GoogleCloudDnsSettingsSaved = "settings.google_cloud_dns.saved";
    public const string AuditSettingsSaved = "settings.audit.saved";
    public const string DnsPushed = "dns.pushed";
    public const string HaloIntegrationTested = "halo.integration_tested";
    public const string HaloSignInCleared = "halo.sign_in_cleared";
    public const string AuditExported = "audit.exported";
    public const string SignInSucceeded = "signin.succeeded";
    public const string SignInRefused = "signin.refused";
    public const string PageViewed = "page.viewed";

    /// <summary>Every action with a short label, for the Action filter on the Audit log page.</summary>
    public static IReadOnlyList<(string Action, string Label)> All { get; } =
    [
        (DomainAdded, "Domain added"),
        (DomainRemoved, "Domain removed"),
        (DomainMonitoringChanged, "Domain monitoring changed"),
        (DomainHaloClientChanged, "Domain Halo client changed"),
        (DomainMtaStsChanged, "Domain MTA-STS changed"),
        (DomainDkimSelectorsChanged, "Domain DKIM selectors changed"),
        (DomainsReordered, "Domains reordered"),
        (DomainGroupsChanged, "Domain groups changed"),
        (DomainTagsChanged, "Domain tags changed"),
        (GroupAdded, "Group added"),
        (GroupRenamed, "Group renamed"),
        (GroupRemoved, "Group removed"),
        (GroupHaloClientChanged, "Group Halo client changed"),
        (TagAdded, "Tag added"),
        (TagUpdated, "Tag updated"),
        (TagRemoved, "Tag removed"),
        (RoleAdded, "Role added"),
        (RoleUpdated, "Role updated"),
        (RoleRemoved, "Role removed"),
        (AccessGranted, "Access granted"),
        (AccessUpdated, "Access updated"),
        (AccessRevoked, "Access revoked"),
        (TicketRuleGlobalChanged, "Ticket rule changed"),
        (TicketRuleGroupChanged, "Group ticket rule changed"),
        (NotificationSettingsSaved, "Notification settings saved"),
        (HaloSettingsSaved, "HaloPSA settings saved"),
        (CloudflareDnsSettingsSaved, "Cloudflare DNS settings saved"),
        (AzureDnsSettingsSaved, "Azure DNS settings saved"),
        (GoogleCloudDnsSettingsSaved, "Google Cloud DNS settings saved"),
        (AuditSettingsSaved, "Audit retention changed"),
        (DnsPushed, "DNS records pushed"),
        (HaloIntegrationTested, "HaloPSA integration tested"),
        (HaloSignInCleared, "HaloPSA sign-in cleared"),
        (AuditExported, "Audit log exported"),
        (SignInSucceeded, "Signed in"),
        (SignInRefused, "Sign-in refused"),
        (PageViewed, "Page viewed"),
    ];
}
```

Create `src/DotMarc/Audit/AuditChanges.cs`:

```csharp
using System.Globalization;

namespace DotMarc.Audit;

/// <summary>Builds the field changes for an audit entry, keeping only fields whose value actually differs.</summary>
public sealed class AuditChanges
{
    private readonly List<AuditFieldChange> _changes = [];

    public bool Any => _changes.Count > 0;

    public IReadOnlyList<AuditFieldChange> Items => _changes;

    public AuditChanges Field<T>(string name, T oldValue, T newValue)
    {
        if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
        {
            _changes.Add(new AuditFieldChange(name, Format(oldValue), Format(newValue)));
        }

        return this;
    }

    /// <summary>For a set of names, such as a domain's groups or a role's permissions: order doesn't matter, and the
    /// values are recorded sorted and comma-separated, or "None" when empty.</summary>
    public AuditChanges Set(string name, IEnumerable<string> oldValues, IEnumerable<string> newValues)
    {
        var oldSet = oldValues.ToHashSet(StringComparer.Ordinal);
        var newSet = newValues.ToHashSet(StringComparer.Ordinal);
        if (!oldSet.SetEquals(newSet))
        {
            _changes.Add(new AuditFieldChange(name, Describe(oldSet), Describe(newSet)));
        }

        return this;
    }

    /// <summary>Records that a secret changed, never its value.</summary>
    public AuditChanges Secret(string name, bool changed)
    {
        if (changed)
        {
            _changes.Add(new AuditFieldChange(name, null, null, Secret: true));
        }

        return this;
    }

    private static string Describe(IEnumerable<string> values)
    {
        var sorted = values.Order(StringComparer.OrdinalIgnoreCase).ToList();
        return sorted.Count == 0 ? "None" : string.Join(", ", sorted);
    }

    private static string? Format<T>(T value) => value switch
    {
        null => null,
        bool flag => flag ? "Yes" : "No",
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
```

Create `src/DotMarc/Audit/AuditLog.cs`:

```csharp
using DotMarc.Data;

namespace DotMarc.Audit;

/// <summary>Creates and records audit entries. Services call <see cref="Record"/> before their own save, so a
/// change and its entry are saved together or not at all.</summary>
public static class AuditLog
{
    public static AuditEntry Create(AuditActor actor, AuditEntryKind kind, string action, AuditTarget? target, string summary, IReadOnlyList<AuditFieldChange>? changes = null) => new()
    {
        OccurredUtc = DateTimeOffset.UtcNow,
        Kind = kind,
        ActorKind = actor.Kind,
        ActorObjectId = actor.ObjectId,
        ActorEmail = actor.Email,
        ActorName = actor.Name,
        Action = action,
        TargetType = target?.Type,
        TargetId = target?.Id,
        TargetName = target?.Name,
        Summary = summary,
        Changes = changes?.ToList() ?? []
    };

    /// <summary>Adds a change entry to the context. The caller's own SaveChangesAsync saves it with the change.</summary>
    public static void Record(DotMarcDbContext context, AuditActor actor, string action, AuditTarget? target, string summary, AuditChanges? changes = null) =>
        context.AuditEntries.Add(Create(actor, AuditEntryKind.Change, action, target, summary, changes?.Items));

    /// <summary>For a change whose entry needs an id the database assigns, such as a newly added domain: saves the
    /// change, lets <paramref name="recordEntries"/> call <see cref="Record"/> now the id is known, and saves again,
    /// all in one transaction so neither is kept without the other. An exception from the first save (such as a
    /// unique-name race) propagates to the caller unchanged.</summary>
    public static async Task SaveAndRecordAsync(DotMarcDbContext context, Action recordEntries, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        recordEntries();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

Create `src/DotMarc/Audit/AuditActorAccessor.cs`:

```csharp
using Microsoft.AspNetCore.Components.Authorization;

namespace DotMarc.Audit;

/// <summary>Gives a page the actor for the person using it, from the circuit's authentication state.</summary>
public sealed class AuditActorAccessor(AuthenticationStateProvider authenticationStateProvider)
{
    public async Task<AuditActor> GetAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return AuditActor.FromPrincipal(state.User);
    }
}
```

In `src/DotMarc/Program.cs`, directly after
`builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, DotMarc.Security.UserAccessClaimsTransformation>();`
add:

```csharp
builder.Services.AddScoped<DotMarc.Audit.AuditActorAccessor>();
```

In `src/DotMarc/Components/_Imports.razor`, add the line `@using DotMarc.Audit` next to the other `@using` lines.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Audit|FullyQualifiedName~UserAccessClaimsTransformationTests" -nologo -v q`
Expected: PASS (the new audit tests, and the claims transformation tests still pass after the email refactor).

- [ ] **Step 6: Commit**

```powershell
git add src/DotMarc/Audit src/DotMarc/Security src/DotMarc/Program.cs src/DotMarc/Components/_Imports.razor test/DotMarc.Tests/Audit test/DotMarc.Tests/Internal/TestActors.cs
git commit -m "Add the audit actor, change builder and recording helpers"
```

---

### How call sites are updated (used by Tasks 3 to 6)

Each of Tasks 3 to 6 adds the actor parameter to some service methods, then updates every caller with this
script, passing that task's method pattern. It inserts the actor straight after the first argument (always the
context): `TestActors.Admin` in `.cs` test files, and `await AuditActorAccessor.GetAsync()` in `.razor` files.

```powershell
function Add-AuditActorArgument([string]$methodPattern, [string]$serviceFile) {
    $files = Get-ChildItem src, test -Recurse -Include *.cs, *.razor |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.Name -ne $serviceFile }
    foreach ($file in $files) {
        $text = [IO.File]::ReadAllText($file.FullName)
        $actorArgument = if ($file.Extension -eq '.razor') { 'await AuditActorAccessor.GetAsync()' } else { 'TestActors.Admin' }
        $updated = [regex]::Replace($text, "(?<call>(?:$methodPattern)\()(?<context>\s*\w+),", "`${call}`${context}, $actorArgument,")
        if ($updated -ne $text) {
            [IO.File]::WriteAllText($file.FullName, $updated)
            $file.FullName
        }
    }
}
```

After running it:
- Every `.razor` file it lists needs `@inject AuditActorAccessor AuditActorAccessor` added next to its other
  `@inject` lines, if it doesn't have one already.
- Every test file it lists needs `using DotMarc.Tests.Internal;` if it doesn't have one already.
- Build, and fix by hand any call the pattern couldn't match (a first argument that isn't a plain variable). The
  compiler lists them.

---

### Task 3: Record domain changes

**Files:**
- Modify: `src/DotMarc/Data/DomainManagementService.cs` (whole file below)
- Modify (by script): `Components/Pages/Dashboard.razor`, `Components/Pages/DomainDetail.razor`,
  `Components/Pages/ManageDomains.razor`, `Components/Shared/DomainMtaStsPanel.razor`,
  `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs`, `GroupManagementServiceTests.cs`,
  `TagManagementServiceTests.cs`, `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs`
- Test: `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs` (new tests)

**Interfaces:**
- Consumes: Task 2's `AuditActor`, `AuditTarget.For(Domain)`, `AuditActions`, `AuditChanges`, `AuditLog`, `TestActors.Admin`.
- Produces: every `DomainManagementService` mutating method takes `(DotMarcDbContext context, AuditActor actor, ...)`.

- [ ] **Step 1: Write the failing tests**

Add to `DomainManagementServiceTests` (add `using DotMarc.Audit;` at the top):

```csharp
    private static async Task<AuditEntry> LatestEntryAsync(DotMarcDbContext context) =>
        await context.AuditEntries.OrderByDescending(entry => entry.Id).FirstAsync();

    [Fact]
    public async Task AddDomainAsync_RecordsWhoAddedTheDomain()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "Contoso.com");
        }

        await using var verify = CreateContext();
        var domain = await verify.Domains.SingleAsync();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.DomainAdded, entry.Action);
        Assert.Equal(AuditEntryKind.Change, entry.Kind);
        Assert.Equal("admin@example.com", entry.ActorEmail);
        Assert.Equal(("Domain", domain.Id.ToString(), "contoso.com"), (entry.TargetType, entry.TargetId, entry.TargetName));
        Assert.Equal("Added domain contoso.com", entry.Summary);
    }

    [Fact]
    public async Task AddDomainAsync_RecordsNothing_WhenItRefusesTheDomain()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "not a domain");
        }

        await using var verify = CreateContext();
        Assert.Single(verify.AuditEntries);
    }

    [Fact]
    public async Task SetMonitoredAsync_RecordsTheOldAndNewValue()
    {
        int domainId;
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            domainId = (await context.Domains.SingleAsync()).Id;
            await DomainManagementService.SetMonitoredAsync(context, TestActors.Admin, domainId, isMonitored: false);
        }

        await using var verify = CreateContext();
        var entry = await LatestEntryAsync(verify);
        Assert.Equal(AuditActions.DomainMonitoringChanged, entry.Action);
        Assert.Equal("Stopped monitoring contoso.com", entry.Summary);
        Assert.Equal([new AuditFieldChange("Monitored", "Yes", "No")], entry.Changes);
    }

    [Fact]
    public async Task SetMonitoredAsync_RecordsNothing_WhenTheValueIsUnchanged()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            var domainId = (await context.Domains.SingleAsync()).Id;
            await DomainManagementService.SetMonitoredAsync(context, TestActors.Admin, domainId, isMonitored: true);
        }

        await using var verify = CreateContext();
        Assert.Single(verify.AuditEntries);
    }

    [Fact]
    public async Task SetMtaStsConfigAsync_RecordsOnlyTheFieldsThatChanged()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            var domainId = (await context.Domains.SingleAsync()).Id;
            await DomainManagementService.SetMtaStsConfigAsync(context, TestActors.Admin, domainId, enabled: true, MtaStsMode.Testing, ["mx1.contoso.com"], 86400);
            await DomainManagementService.SetMtaStsConfigAsync(context, TestActors.Admin, domainId, enabled: true, MtaStsMode.Enforce, ["mx1.contoso.com"], 86400);
        }

        await using var verify = CreateContext();
        var entry = await LatestEntryAsync(verify);
        Assert.Equal(AuditActions.DomainMtaStsChanged, entry.Action);
        Assert.Equal([new AuditFieldChange("Mode", "Testing", "Enforce")], entry.Changes);
    }

    [Fact]
    public async Task ReorderAsync_RecordsOneEntryWithTheOldAndNewOrder()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "alpha.com");
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "beta.com");
            var domainIdsByName = await context.Domains.ToDictionaryAsync(domain => domain.Name, domain => domain.Id);
            await DomainManagementService.ReorderAsync(context, TestActors.Admin, [domainIdsByName["beta.com"], domainIdsByName["alpha.com"]]);
        }

        await using var verify = CreateContext();
        var reorderEntry = await verify.AuditEntries.SingleAsync(entry => entry.Action == AuditActions.DomainsReordered);
        Assert.Equal([new AuditFieldChange("Order", "alpha.com, beta.com", "beta.com, alpha.com")], reorderEntry.Changes);
    }

    [Fact]
    public async Task RemoveDomainAsync_RecordsTheRemovedDomainsName()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            var domainId = (await context.Domains.SingleAsync()).Id;
            await DomainManagementService.RemoveDomainAsync(context, TestActors.Admin, domainId);
        }

        await using var verify = CreateContext();
        var entry = await LatestEntryAsync(verify);
        Assert.Equal((AuditActions.DomainRemoved, "contoso.com", "Removed domain contoso.com"), (entry.Action, entry.TargetName, entry.Summary));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainManagementServiceTests" -nologo -v q`
Expected: build FAILS, no overload of `AddDomainAsync` takes an `AuditActor`.

- [ ] **Step 3: Rewrite the service**

Replace the body of `src/DotMarc/Data/DomainManagementService.cs` with the following. The existing doc comments on
each method stay as they are; only the signatures and bodies change. The file's usings gain `DotMarc.Audit`.

```csharp
using DotMarc.Audit;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

/// (keep the existing class summary)
public static class DomainManagementService
{
    public enum AddDomainResult { Added, InvalidName, AlreadyMonitored }

    /// (keep the existing summary)
    public static async Task<AddDomainResult> AddDomainAsync(DotMarcDbContext context, AuditActor actor, string rawName, CancellationToken cancellationToken = default)
    {
        if (!DomainNameValidator.TryNormalize(rawName, out var normalized))
        {
            return AddDomainResult.InvalidName;
        }

        var exists = await context.Domains.AnyAsync(d => d.Name == normalized, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddDomainResult.AlreadyMonitored;
        }

        var nextSortOrder = (await context.Domains.MaxAsync(d => (int?)d.SortOrder, cancellationToken).ConfigureAwait(false) ?? -1) + 1;
        var domain = new Domain { Name = normalized, FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, SortOrder = nextSortOrder };
        context.Domains.Add(domain);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.DomainAdded, AuditTarget.For(domain), $"Added domain {domain.Name}"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // (keep the existing comment about the unique index race)
            return AddDomainResult.AlreadyMonitored;
        }

        return AddDomainResult.Added;
    }

    /// (keep the existing summary)
    public static async Task RemoveDomainAsync(DotMarcDbContext context, AuditActor actor, int domainId, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        AuditLog.Record(context, actor, AuditActions.DomainRemoved, AuditTarget.For(domain), $"Removed domain {domain.Name}");
        context.Domains.Remove(domain);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task SetMonitoredAsync(DotMarcDbContext context, AuditActor actor, int domainId, bool isMonitored, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Monitored", domain.IsMonitored, isMonitored);
        if (!changes.Any)
        {
            return;
        }

        domain.IsMonitored = isMonitored;
        AuditLog.Record(context, actor, AuditActions.DomainMonitoringChanged, AuditTarget.For(domain),
            $"{(isMonitored ? "Started" : "Stopped")} monitoring {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// (keep the existing summary)
    public static async Task SetHaloClientIdAsync(DotMarcDbContext context, AuditActor actor, int domainId, int? haloClientId, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Halo client", domain.HaloClientId, haloClientId);
        if (!changes.Any)
        {
            return;
        }

        domain.HaloClientId = haloClientId;
        AuditLog.Record(context, actor, AuditActions.DomainHaloClientChanged, AuditTarget.For(domain), $"Changed the Halo client for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// (keep the existing summary)
    public static async Task SetMtaStsConfigAsync(
        DotMarcDbContext context,
        AuditActor actor,
        int domainId,
        bool enabled,
        MtaStsMode mode,
        List<string> mxHosts,
        int maxAgeSeconds,
        CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Hosting enabled", domain.MtaStsEnabled, enabled)
            .Field("Mode", domain.MtaStsMode, mode)
            .Set("MX hosts", domain.MtaStsMxHosts, mxHosts)
            .Field("Max age (seconds)", domain.MtaStsMaxAgeSeconds, maxAgeSeconds);
        if (!changes.Any)
        {
            return;
        }

        if (enabled && !domain.MtaStsEnabled)
        {
            domain.MtaStsStatus = MtaStsStatus.PendingDns;
            domain.MtaStsCheckDetail = null;
            domain.MtaStsCheckedUtc = null;
        }

        domain.MtaStsEnabled = enabled;
        domain.MtaStsMode = mode;
        domain.MtaStsMxHosts = mxHosts;
        domain.MtaStsMaxAgeSeconds = maxAgeSeconds;

        AuditLog.Record(context, actor, AuditActions.DomainMtaStsChanged, AuditTarget.For(domain), $"Changed MTA-STS for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// (keep the existing summary)
    public static async Task SetDkimSelectorsAsync(DotMarcDbContext context, AuditActor actor, int domainId, List<string> selectors, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Set("DKIM selectors", domain.DkimSelectors, selectors);
        if (!changes.Any)
        {
            return;
        }

        domain.DkimSelectors = selectors;
        AuditLog.Record(context, actor, AuditActions.DomainDkimSelectorsChanged, AuditTarget.For(domain), $"Changed the DKIM selectors for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// (keep the existing summary)
    public static async Task ReorderAsync(DotMarcDbContext context, AuditActor actor, IReadOnlyList<int> orderedDomainIds, CancellationToken cancellationToken = default)
    {
        var domains = await context.Domains
            .Where(d => orderedDomainIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, cancellationToken)
            .ConfigureAwait(false);

        var orderBefore = domains.Values.OrderBy(domain => domain.SortOrder).ThenBy(domain => domain.Name).Select(domain => domain.Name).ToList();

        for (var index = 0; index < orderedDomainIds.Count; index++)
        {
            // (keep the existing comment about concurrently deleted domains)
            if (domains.TryGetValue(orderedDomainIds[index], out var domain))
            {
                domain.SortOrder = index;
            }
        }

        var orderAfter = orderedDomainIds.Where(domains.ContainsKey).Select(domainId => domains[domainId].Name).ToList();
        var changes = new AuditChanges().Field("Order", string.Join(", ", orderBefore), string.Join(", ", orderAfter));
        if (changes.Any)
        {
            AuditLog.Record(context, actor, AuditActions.DomainsReordered, null, "Reordered domains", changes);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

`MtaStsMxHosts` and `DkimSelectors` are `List<string>`, which `Set` accepts as `IEnumerable<string>`. The
MX host list is compared as a set on purpose: reordering hosts doesn't change the policy.

- [ ] **Step 4: Update the callers**

Define the `Add-AuditActorArgument` function from "How call sites are updated" above, then run:

```powershell
Add-AuditActorArgument 'DomainManagementService\.(?:AddDomainAsync|RemoveDomainAsync|SetMonitoredAsync|SetHaloClientIdAsync|SetMtaStsConfigAsync|SetDkimSelectorsAsync|ReorderAsync)' 'DomainManagementService.cs'
```

Expected output lists `Dashboard.razor`, `DomainDetail.razor`, `ManageDomains.razor`, `DomainMtaStsPanel.razor`,
`DomainManagementServiceTests.cs`, `GroupManagementServiceTests.cs`, `TagManagementServiceTests.cs` and
`PollingServiceTests.cs`. Add `@inject AuditActorAccessor AuditActorAccessor` to each of the four `.razor` files, then build:

Run: `dotnet build src/DotMarc/DotMarc.csproj -nologo -v q`
Expected: `Build succeeded.`

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainManagementServiceTests|FullyQualifiedName~PollingServiceTests|FullyQualifiedName~GroupManagementServiceTests|FullyQualifiedName~TagManagementServiceTests" -nologo -v q`
Expected: PASS, including the seven new tests.

- [ ] **Step 6: Commit**

```powershell
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Record domain changes in the audit log"
```

---

### Task 4: Record group and tag changes

**Files:**
- Modify: `src/DotMarc/Data/GroupManagementService.cs`, `src/DotMarc/Data/TagManagementService.cs`
- Modify (by script): `Components/Pages/ManageGroups.razor`, `Components/Pages/ManageDomains.razor`, and the tests that
  call these services (`GroupManagementServiceTests.cs`, `TagManagementServiceTests.cs`, `AlertTicketRuleServiceTests.cs`)
- Test: `test/DotMarc.Tests/Data/GroupManagementServiceTests.cs`, `TagManagementServiceTests.cs` (new tests)

**Interfaces:**
- Consumes: Task 2's building blocks.
- Produces: `GroupManagementService.AddGroupAsync(context, actor, rawName, cancellationToken, haloClientId)`,
  `RenameGroupAsync(context, actor, groupId, rawName, ...)`, `RemoveGroupAsync(context, actor, groupId, ...)`,
  `SetDomainGroupsAsync(context, actor, domainId, groupIds, ...)`, `SetHaloClientIdAsync(context, actor, groupId, haloClientId, ...)`;
  `TagManagementService.AddTagAsync(context, actor, rawName, color, ...)`, `UpdateTagAsync(context, actor, tagId, rawName, color, ...)`,
  `RemoveTagAsync(context, actor, tagId, ...)`, `SetDomainTagsAsync(context, actor, domainId, tagIds, ...)`.

- [ ] **Step 1: Write the failing tests**

Add to `GroupManagementServiceTests` (add `using DotMarc.Audit;`):

```csharp
    [Fact]
    public async Task AddGroupAsync_RecordsTheNewGroupAndItsHaloClient()
    {
        await using (var context = CreateContext())
        {
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None, haloClientId: 37);
        }

        await using var verify = CreateContext();
        var group = await verify.Groups.SingleAsync();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal((AuditActions.GroupAdded, group.Id.ToString(), "Added group Client A"), (entry.Action, entry.TargetId, entry.Summary));
        Assert.Equal([new AuditFieldChange("Halo client", null, "37")], entry.Changes);
    }

    [Fact]
    public async Task RenameGroupAsync_RecordsTheOldAndNewName()
    {
        await using (var context = CreateContext())
        {
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);
            var groupId = (await context.Groups.SingleAsync()).Id;
            await GroupManagementService.RenameGroupAsync(context, TestActors.Admin, groupId, "Client B");
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync(auditEntry => auditEntry.Action == AuditActions.GroupRenamed);
        Assert.Equal("Renamed group Client A to Client B", entry.Summary);
        Assert.Equal([new AuditFieldChange("Name", "Client A", "Client B")], entry.Changes);
    }

    [Fact]
    public async Task RenameGroupAsync_RecordsNothing_WhenTheNameIsTaken()
    {
        await using (var context = CreateContext())
        {
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client B", CancellationToken.None);
            var clientBId = (await context.Groups.SingleAsync(group => group.Name == "Client B")).Id;
            await GroupManagementService.RenameGroupAsync(context, TestActors.Admin, clientBId, "client a");
        }

        await using var verify = CreateContext();
        Assert.DoesNotContain(verify.AuditEntries, entry => entry.Action == AuditActions.GroupRenamed);
    }

    [Fact]
    public async Task SetDomainGroupsAsync_RecordsTheGroupsBeforeAndAfter()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client B", CancellationToken.None);
            var domainId = (await context.Domains.SingleAsync()).Id;
            var groupIdsByName = await context.Groups.ToDictionaryAsync(group => group.Name, group => group.Id);
            await GroupManagementService.SetDomainGroupsAsync(context, TestActors.Admin, domainId, [groupIdsByName["Client A"]]);
            await GroupManagementService.SetDomainGroupsAsync(context, TestActors.Admin, domainId, [groupIdsByName["Client A"], groupIdsByName["Client B"]]);
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.Where(auditEntry => auditEntry.Action == AuditActions.DomainGroupsChanged).OrderByDescending(auditEntry => auditEntry.Id).FirstAsync();
        Assert.Equal("Changed the groups for contoso.com", entry.Summary);
        Assert.Equal([new AuditFieldChange("Groups", "Client A", "Client A, Client B")], entry.Changes);
    }
```

Add to `TagManagementServiceTests` (add `using DotMarc.Audit;`):

```csharp
    [Fact]
    public async Task UpdateTagAsync_RecordsNameAndColorChanges()
    {
        await using (var context = CreateContext())
        {
            await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", Color.Primary);
            var tagId = (await context.Tags.SingleAsync()).Id;
            await TagManagementService.UpdateTagAsync(context, TestActors.Admin, tagId, "main", Color.Info);
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync(auditEntry => auditEntry.Action == AuditActions.TagUpdated);
        Assert.Equal([new AuditFieldChange("Name", "primary", "main"), new AuditFieldChange("Color", "Primary", "Info")], entry.Changes);
    }

    [Fact]
    public async Task SetDomainTagsAsync_RecordsNothing_WhenTheTagsAreUnchanged()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            var domainId = (await context.Domains.SingleAsync()).Id;
            await TagManagementService.SetDomainTagsAsync(context, TestActors.Admin, domainId, []);
        }

        await using var verify = CreateContext();
        Assert.DoesNotContain(verify.AuditEntries, entry => entry.Action == AuditActions.DomainTagsChanged);
    }

    [Fact]
    public async Task RemoveTagAsync_RecordsTheRemovedTag()
    {
        await using (var context = CreateContext())
        {
            await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", Color.Primary);
            var tagId = (await context.Tags.SingleAsync()).Id;
            await TagManagementService.RemoveTagAsync(context, TestActors.Admin, tagId);
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync(auditEntry => auditEntry.Action == AuditActions.TagRemoved);
        Assert.Equal(("primary", "Removed tag primary"), (entry.TargetName, entry.Summary));
    }
```

(`TagManagementServiceTests` already has `using MudBlazor;` for `Color`; add it if not.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~GroupManagementServiceTests|FullyQualifiedName~TagManagementServiceTests" -nologo -v q`
Expected: build FAILS on the new `AuditActor` arguments.

- [ ] **Step 3: Change the group service**

In `src/DotMarc/Data/GroupManagementService.cs`, add `using DotMarc.Audit;` and change the methods as follows
(keep every existing comment):

```csharp
    public static async Task<AddGroupResult> AddGroupAsync(DotMarcDbContext context, AuditActor actor, string rawName, CancellationToken cancellationToken = default, int? haloClientId = null)
    {
        // (validation and the exists check are unchanged)

        var group = new Group { Name = name, HaloClientId = haloClientId };
        context.Groups.Add(group);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.GroupAdded, AuditTarget.For(group), $"Added group {group.Name}",
                    new AuditChanges().Field("Halo client", (int?)null, haloClientId)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // (existing comment)
            return AddGroupResult.AlreadyExists;
        }

        return AddGroupResult.Added;
    }

    public static async Task<AddGroupResult> RenameGroupAsync(DotMarcDbContext context, AuditActor actor, int groupId, string rawName, CancellationToken cancellationToken = default)
    {
        // (validation and the exists check are unchanged)

        var group = await context.Groups.SingleAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Name", group.Name, name);
        if (!changes.Any)
        {
            return AddGroupResult.Added;
        }

        AuditLog.Record(context, actor, AuditActions.GroupRenamed, AuditTarget.For(group), $"Renamed group {group.Name} to {name}", changes);
        group.Name = name;

        // (the try/catch around SaveChangesAsync is unchanged)
    }

    public static async Task RemoveGroupAsync(DotMarcDbContext context, AuditActor actor, int groupId, CancellationToken cancellationToken = default)
    {
        var group = await context.Groups.SingleAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        AuditLog.Record(context, actor, AuditActions.GroupRemoved, AuditTarget.For(group), $"Removed group {group.Name}");
        context.Groups.Remove(group);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task SetDomainGroupsAsync(DotMarcDbContext context, AuditActor actor, int domainId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Include(d => d.Groups).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var groups = await context.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Set("Groups", domain.Groups.Select(group => group.Name), groups.Select(group => group.Name));
        if (!changes.Any)
        {
            return;
        }

        domain.Groups = groups;
        AuditLog.Record(context, actor, AuditActions.DomainGroupsChanged, AuditTarget.For(domain), $"Changed the groups for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task SetHaloClientIdAsync(DotMarcDbContext context, AuditActor actor, int groupId, int? haloClientId, CancellationToken cancellationToken = default)
    {
        var group = await context.Groups.SingleAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Halo client", group.HaloClientId, haloClientId);
        if (!changes.Any)
        {
            return;
        }

        group.HaloClientId = haloClientId;
        AuditLog.Record(context, actor, AuditActions.GroupHaloClientChanged, AuditTarget.For(group), $"Changed the Halo client for group {group.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Change the tag service**

In `src/DotMarc/Data/TagManagementService.cs`, add `using DotMarc.Audit;` and change:

```csharp
    public static async Task<AddTagResult> AddTagAsync(DotMarcDbContext context, AuditActor actor, string rawName, Color color, CancellationToken cancellationToken = default)
    {
        // (validation and the exists check are unchanged)

        var tag = new Tag { Name = name, Color = color };
        context.Tags.Add(tag);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.TagAdded, AuditTarget.For(tag), $"Added tag {tag.Name}",
                    new AuditChanges().Field("Color", (Color?)null, color)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return AddTagResult.AlreadyExists;
        }

        return AddTagResult.Added;
    }

    public static async Task<AddTagResult> UpdateTagAsync(DotMarcDbContext context, AuditActor actor, int tagId, string rawName, Color color, CancellationToken cancellationToken = default)
    {
        // (validation and the exists check are unchanged)

        var tag = await context.Tags.SingleAsync(t => t.Id == tagId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Name", tag.Name, name).Field("Color", tag.Color, color);
        if (!changes.Any)
        {
            return AddTagResult.Added;
        }

        AuditLog.Record(context, actor, AuditActions.TagUpdated, AuditTarget.For(tag), $"Updated tag {name}", changes);
        tag.Name = name;
        tag.Color = color;

        // (the try/catch around SaveChangesAsync is unchanged)
    }

    public static async Task RemoveTagAsync(DotMarcDbContext context, AuditActor actor, int tagId, CancellationToken cancellationToken = default)
    {
        var tag = await context.Tags.SingleAsync(t => t.Id == tagId, cancellationToken).ConfigureAwait(false);
        AuditLog.Record(context, actor, AuditActions.TagRemoved, AuditTarget.For(tag), $"Removed tag {tag.Name}");
        context.Tags.Remove(tag);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task SetDomainTagsAsync(DotMarcDbContext context, AuditActor actor, int domainId, IReadOnlyList<int> tagIds, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Include(d => d.Tags).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var tags = await context.Tags.Where(t => tagIds.Contains(t.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Set("Tags", domain.Tags.Select(tag => tag.Name), tags.Select(tag => tag.Name));
        if (!changes.Any)
        {
            return;
        }

        domain.Tags = tags;
        AuditLog.Record(context, actor, AuditActions.DomainTagsChanged, AuditTarget.For(domain), $"Changed the tags for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

The `Color` field formats with `ToString()`, giving `Primary`, `Info` and so on, which is what the tag picker shows.

- [ ] **Step 5: Update the callers**

```powershell
Add-AuditActorArgument 'GroupManagementService\.(?:AddGroupAsync|RenameGroupAsync|RemoveGroupAsync|SetDomainGroupsAsync|SetHaloClientIdAsync)' 'GroupManagementService.cs'
Add-AuditActorArgument 'TagManagementService\.(?:AddTagAsync|UpdateTagAsync|RemoveTagAsync|SetDomainTagsAsync)' 'TagManagementService.cs'
```

Add `@inject AuditActorAccessor AuditActorAccessor` to `ManageGroups.razor` (`ManageDomains.razor` already has it
from Task 3). Build: `dotnet build src/DotMarc/DotMarc.csproj -nologo -v q` → `Build succeeded.`

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~GroupManagementServiceTests|FullyQualifiedName~TagManagementServiceTests|FullyQualifiedName~AlertTicketRuleServiceTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Record group and tag changes in the audit log"
```

---

### Task 5: Record role and access changes

**Files:**
- Modify: `src/DotMarc/Data/RoleManagementService.cs`, `src/DotMarc/Data/UserAccessManagementService.cs`,
  `src/DotMarc/Data/AccessBootstrapper.cs`
- Modify (by script): `Components/Pages/ManageAccess.razor`, `RoleManagementServiceTests.cs`,
  `UserAccessManagementServiceTests.cs`, `UserAccessClaimsTransformationTests.cs`
- Test: `RoleManagementServiceTests.cs`, `UserAccessManagementServiceTests.cs`, `AccessBootstrapperTests.cs` (new tests)

**Interfaces:**
- Consumes: Task 2's building blocks.
- Produces: `RoleManagementService.AddRoleAsync(context, actor, rawName, permissions, ...)`,
  `UpdateRoleAsync(context, actor, roleId, rawName, permissions, ...)`, `RemoveRoleAsync(context, actor, roleId, ...)`;
  `UserAccessManagementService.GrantAccessAsync(context, actor, rawEmail, roleId, groupIds, ...)`,
  `UpdateAccessAsync(context, actor, userAccessId, roleId, groupIds, ...)`, `RevokeAccessAsync(context, actor, userAccessId, ...)`.
  `ResolveAsync` is unchanged (a read, exempt).

- [ ] **Step 1: Write the failing tests**

Add to `RoleManagementServiceTests` (add `using DotMarc.Audit;`):

```csharp
    [Fact]
    public async Task UpdateRoleAsync_RecordsThePermissionsBeforeAndAfter()
    {
        await using (var context = CreateContext())
        {
            await RoleManagementService.AddRoleAsync(context, TestActors.Admin, "Ops", [Permission.DomainsView]);
            var roleId = (await context.Roles.SingleAsync(role => role.Name == "Ops")).Id;
            await RoleManagementService.UpdateRoleAsync(context, TestActors.Admin, roleId, "Ops", [Permission.DomainsView, Permission.AlertsView]);
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync(auditEntry => auditEntry.Action == AuditActions.RoleUpdated);
        Assert.Equal([new AuditFieldChange("Permissions", "DomainsView", "AlertsView, DomainsView")], entry.Changes);
    }

    [Fact]
    public async Task RemoveRoleAsync_RecordsNothing_WhenTheRoleIsStillGranted()
    {
        await using (var context = CreateContext())
        {
            await RoleManagementService.AddRoleAsync(context, TestActors.Admin, "Ops", [Permission.DomainsView]);
            var roleId = (await context.Roles.SingleAsync(role => role.Name == "Ops")).Id;
            await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "sam@contoso.com", roleId, []);
            await RoleManagementService.RemoveRoleAsync(context, TestActors.Admin, roleId);
        }

        await using var verify = CreateContext();
        Assert.DoesNotContain(verify.AuditEntries, entry => entry.Action == AuditActions.RoleRemoved);
    }
```

Add to `UserAccessManagementServiceTests` (add `using DotMarc.Audit;`; this file already creates roles and
groups in its existing tests, so reuse its helpers for making a scopable role and a group if it has them,
otherwise create them as below):

```csharp
    [Fact]
    public async Task GrantAccessAsync_RecordsTheRoleAndGroups()
    {
        await using (var context = CreateContext())
        {
            context.Roles.Add(new Role { Name = "Client viewer", IsScopable = true, Permissions = [Permission.DomainsView] });
            context.Groups.Add(new Group { Name = "Client A" });
            await context.SaveChangesAsync();
            var roleId = (await context.Roles.SingleAsync(role => role.Name == "Client viewer")).Id;
            var groupId = (await context.Groups.SingleAsync()).Id;
            await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "sam@contoso.com", roleId, [groupId]);
        }

        await using var verify = CreateContext();
        var access = await verify.UserAccesses.SingleAsync(userAccess => userAccess.Email == "sam@contoso.com");
        var entry = await verify.AuditEntries.SingleAsync(auditEntry => auditEntry.Action == AuditActions.AccessGranted);
        Assert.Equal((access.Id.ToString(), "sam@contoso.com", "Granted sam@contoso.com the Client viewer role"), (entry.TargetId, entry.TargetName, entry.Summary));
        Assert.Equal([new AuditFieldChange("Role", null, "Client viewer"), new AuditFieldChange("Groups", "None", "Client A")], entry.Changes);
    }

    [Fact]
    public async Task RevokeAccessAsync_RecordsNothing_WhenItWouldRemoveTheLastAdmin()
    {
        await using (var context = CreateContext())
        {
            context.Roles.Add(new Role { Name = "Admin", IsLocked = true, Permissions = [Permission.AccessManage] });
            await context.SaveChangesAsync();
            var roleId = (await context.Roles.SingleAsync()).Id;
            await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "only-admin@contoso.com", roleId, []);
            var accessId = (await context.UserAccesses.SingleAsync()).Id;

            var result = await UserAccessManagementService.RevokeAccessAsync(context, TestActors.Admin, accessId);

            Assert.Equal(UserAccessManagementService.RevokeAccessResult.LastAdminGuard, result);
        }

        await using var verify = CreateContext();
        Assert.DoesNotContain(verify.AuditEntries, entry => entry.Action == AuditActions.AccessRevoked);
    }
```

Add to `AccessBootstrapperTests` (add `using DotMarc.Audit;`), calling the bootstrapper the same way that file's
existing tests do:

```csharp
    [Fact]
    public async Task SeedingInitialAdmins_RecordsEachGrantAsStartup()
    {
        await using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context,
                Options.Create(new InitialAdminsOptions { Emails = "first@contoso.com,second@contoso.com" }),
                NullLogger.Instance);
        }

        await using var verify = CreateContext();
        var entries = await verify.AuditEntries.Where(entry => entry.Action == AuditActions.AccessGranted).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal((AuditActorKind.System, "Startup"), (entry.ActorKind, entry.ActorName)));
        Assert.Equal(["first@contoso.com", "second@contoso.com"], entries.Select(entry => entry.TargetName).Order());
    }
```

(`Options` is `Microsoft.Extensions.Options.Options`, `NullLogger` is `Microsoft.Extensions.Logging.Abstractions.NullLogger`;
the file likely imports both already.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~RoleManagementServiceTests|FullyQualifiedName~UserAccessManagementServiceTests|FullyQualifiedName~AccessBootstrapperTests" -nologo -v q`
Expected: build FAILS on the new `AuditActor` arguments.

- [ ] **Step 3: Change the role service**

In `src/DotMarc/Data/RoleManagementService.cs`, add `using DotMarc.Audit;` and change (keep existing comments):

```csharp
    public static async Task<AddRoleResult> AddRoleAsync(DotMarcDbContext context, AuditActor actor, string rawName, List<Permission> permissions, CancellationToken cancellationToken = default)
    {
        // (validation and the exists check are unchanged)

        var role = new Role { Name = name, IsLocked = false, IsScopable = false, Permissions = permissions };
        context.Roles.Add(role);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.RoleAdded, AuditTarget.For(role), $"Added role {role.Name}",
                    new AuditChanges().Set("Permissions", [], PermissionNames(permissions))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return AddRoleResult.AlreadyExists;
        }

        return AddRoleResult.Added;
    }

    public static async Task<UpdateRoleResult> UpdateRoleAsync(DotMarcDbContext context, AuditActor actor, int roleId, string rawName, List<Permission> permissions, CancellationToken cancellationToken = default)
    {
        // (the locked, name and exists checks are unchanged)

        var changes = new AuditChanges()
            .Field("Name", role.Name, name)
            .Set("Permissions", PermissionNames(role.Permissions), PermissionNames(permissions));
        if (!changes.Any)
        {
            return UpdateRoleResult.Updated;
        }

        AuditLog.Record(context, actor, AuditActions.RoleUpdated, AuditTarget.For(role), $"Updated role {name}", changes);
        role.Name = name;
        role.Permissions = permissions;

        // (the try/catch around SaveChangesAsync is unchanged)
    }

    public static async Task<RemoveRoleResult> RemoveRoleAsync(DotMarcDbContext context, AuditActor actor, int roleId, CancellationToken cancellationToken = default)
    {
        // (the locked and in-use checks are unchanged)

        AuditLog.Record(context, actor, AuditActions.RoleRemoved, AuditTarget.For(role), $"Removed role {role.Name}");
        context.Roles.Remove(role);

        // (the try/catch around SaveChangesAsync is unchanged: if the delete is refused, the entry isn't saved
        // either, because it was part of the same save)
    }

    private static IEnumerable<string> PermissionNames(IEnumerable<Permission> permissions) =>
        permissions.Select(permission => permission.ToString());
```

- [ ] **Step 4: Change the access service**

In `src/DotMarc/Data/UserAccessManagementService.cs`, add `using DotMarc.Audit;` and change:

```csharp
    public static async Task<GrantAccessResult> GrantAccessAsync(DotMarcDbContext context, AuditActor actor, string rawEmail, int roleId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        // (validation, role lookup, exists check and groups query are unchanged)

        var access = new UserAccess { Email = email, RoleId = roleId, ScopedGroups = groups };
        context.UserAccesses.Add(access);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.AccessGranted, AuditTarget.For(access), $"Granted {email} the {role.Name} role",
                    new AuditChanges()
                        .Field("Role", (string?)null, role.Name)
                        .Set("Groups", [], groups.Select(group => group.Name))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return GrantAccessResult.AlreadyExists;
        }

        return GrantAccessResult.Granted;
    }
```

Note that `Set("Groups", [], [])` records nothing, so a grant with no groups has only the Role change, while a
grant with groups gets `("Groups", "None", "Client A")` as the test expects.

```csharp
    public static async Task<UpdateAccessResult> UpdateAccessAsync(DotMarcDbContext context, AuditActor actor, int userAccessId, int roleId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        var role = await context.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return UpdateAccessResult.RoleNotFound;
        }

        var access = await context.UserAccesses.Include(u => u.Role).Include(u => u.ScopedGroups).AsSplitQuery()
            .SingleAsync(u => u.Id == userAccessId, cancellationToken).ConfigureAwait(false);
        var groups = role.IsScopable
            ? await context.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];
        var changes = new AuditChanges()
            .Field("Role", access.Role.Name, role.Name)
            .Set("Groups", access.ScopedGroups.Select(group => group.Name), groups.Select(group => group.Name));

        access.RoleId = roleId;
        access.ScopedGroups = groups;
        if (changes.Any)
        {
            AuditLog.Record(context, actor, AuditActions.AccessUpdated, AuditTarget.For(access), $"Changed access for {access.Email}", changes);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return UpdateAccessResult.Updated;
    }
```

(`AsSplitQuery` because two collection-style includes trip the test factory's multiple-collection warning; `Role`
is a reference, but splitting keeps it safe if that changes.)

```csharp
    public static async Task<RevokeAccessResult> RevokeAccessAsync(DotMarcDbContext context, AuditActor actor, int userAccessId, CancellationToken cancellationToken = default)
    {
        // (the lookup and last-admin guard are unchanged)

        AuditLog.Record(context, actor, AuditActions.AccessRevoked, AuditTarget.For(access), $"Revoked access for {access.Email}");
        context.UserAccesses.Remove(access);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return RevokeAccessResult.Revoked;
    }
```

- [ ] **Step 5: Record the startup seeding**

In `src/DotMarc/Data/AccessBootstrapper.cs`, add `using DotMarc.Audit;` and replace the seeding block:

```csharp
                var seededGrants = emails.Select(email => new UserAccess { Email = email, RoleId = adminRoleId }).ToList();
                context.UserAccesses.AddRange(seededGrants);
                if (seededGrants.Count > 0)
                {
                    var startup = AuditActor.ForSystem("Startup");
                    await AuditLog.SaveAndRecordAsync(context, () =>
                    {
                        foreach (var grant in seededGrants)
                        {
                            AuditLog.Record(context, startup, AuditActions.AccessGranted, AuditTarget.For(grant),
                                $"Granted {grant.Email} the Admin role from InitialAdmins:Emails",
                                new AuditChanges().Field("Role", (string?)null, "Admin"));
                        }
                    }, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("Seeded {Count} initial admin grant(s) from InitialAdmins:Emails.", seededGrants.Count);
                }
                else
                {
                    // (existing warning comment and LogWarning, unchanged)
                }
```

(This replaces the `foreach (var email in emails) { context.UserAccesses.Add(...) }` loop and the
`if (emails.Length > 0)` save.)

- [ ] **Step 6: Update the callers**

```powershell
Add-AuditActorArgument 'RoleManagementService\.(?:AddRoleAsync|UpdateRoleAsync|RemoveRoleAsync)' 'RoleManagementService.cs'
Add-AuditActorArgument 'UserAccessManagementService\.(?:GrantAccessAsync|UpdateAccessAsync|RevokeAccessAsync)' 'UserAccessManagementService.cs'
```

Add `@inject AuditActorAccessor AuditActorAccessor` to `ManageAccess.razor`. Build → `Build succeeded.`

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~RoleManagementServiceTests|FullyQualifiedName~UserAccessManagementServiceTests|FullyQualifiedName~AccessBootstrapperTests|FullyQualifiedName~UserAccessClaimsTransformationTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 8: Commit**

```powershell
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Record role and access changes in the audit log"
```

---

### Task 6: Record settings and ticket rule changes

**Files:**
- Modify: `src/DotMarc/Notifications/NotificationSettingsService.cs`, `HaloPsaSettingsService.cs`,
  `CloudflareDnsSettingsService.cs`, `AzureDnsSettingsService.cs`, `GoogleCloudDnsSettingsService.cs`,
  `AlertTicketRuleService.cs`
- Modify (by script): `Components/Pages/AlertsSettings.razor`, `Components/Pages/DnsPushSettings.razor`,
  `Components/Dialogs/GroupTicketRulesDialog.razor`, and their tests
- Test: `NotificationSettingsServiceTests.cs`, `HaloPsaSettingsServiceTests.cs`, `CloudflareDnsSettingsServiceTests.cs`,
  `AlertTicketRuleServiceTests.cs` (new tests)

**Interfaces:**
- Consumes: Task 2's building blocks.
- Produces: `NotificationSettingsService.SaveAsync(context, actor, updated, ...)`;
  `HaloPsaSettingsService.SaveAsync(context, actor, secretStore, updated, newClientSecret, ...)`; the same shape for the
  three DNS settings services; `AlertTicketRuleService.SetGlobalAsync(context, actor, alertType, createTicket, ...)`
  and `SetForGroupAsync(context, actor, groupId, alertType, createTicket, ...)`.

Each settings service reads a **no-tracking snapshot** of the saved row before changing it. A page can hand the
service the same tracked instance it loaded from this context, and comparing that instance with itself would find no
changes; the snapshot always holds what is in the database.

- [ ] **Step 1: Write the failing tests**

Add to `NotificationSettingsServiceTests` (add `using DotMarc.Audit;`), loading the updated settings from a
separate context so it isn't the tracked instance:

```csharp
    [Fact]
    public async Task SaveAsync_RecordsChangedSettings_AndNeverAWebhookUrl()
    {
        NotificationSettings updated;
        await using (var loadContext = CreateContext())
        {
            updated = await loadContext.NotificationSettings.AsNoTracking().SingleAsync();
        }
        var originalCooldown = updated.CooldownMinutes;
        updated.CooldownMinutes = originalCooldown + 30;
        updated.TeamsWebhookUrl = "https://contoso.webhook.office.com/webhookb2/secret-token";

        await using (var context = CreateContext())
        {
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, updated);
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.NotificationSettingsSaved, entry.Action);
        Assert.Contains(new AuditFieldChange("Cooldown (minutes)", originalCooldown.ToString(), (originalCooldown + 30).ToString()), entry.Changes);
        Assert.Contains(new AuditFieldChange("Teams webhook URL", null, null, Secret: true), entry.Changes);
        Assert.DoesNotContain(entry.Changes, change => (change.Old ?? "").Contains("secret-token") || (change.New ?? "").Contains("secret-token"));
    }

    [Fact]
    public async Task SaveAsync_RecordsNothing_WhenNothingChanged()
    {
        await using (var context = CreateContext())
        {
            var unchanged = await context.NotificationSettings.SingleAsync();
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, unchanged);
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.AuditEntries);
    }
```

Add to `HaloPsaSettingsServiceTests` (use the fake `ISecretStore` that file already uses; called `secretStore` here):

```csharp
    [Fact]
    public async Task SaveAsync_RecordsSecretsAsChanged_WithoutTheirValues()
    {
        HaloPsaSettings updated;
        await using (var loadContext = CreateContext())
        {
            updated = await loadContext.HaloPsaSettings.AsNoTracking().SingleAsync();
        }
        updated.WebhookSecret = "webhook-secret-value";
        updated.AccountName = "contoso";

        await using (var context = CreateContext())
        {
            await HaloPsaSettingsService.SaveAsync(context, TestActors.Admin, secretStore, updated, "client-secret-value");
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync();
        var storedChanges = System.Text.Json.JsonSerializer.Serialize(entry.Changes);
        Assert.DoesNotContain("client-secret-value", storedChanges);
        Assert.DoesNotContain("webhook-secret-value", storedChanges);
        Assert.Contains(new AuditFieldChange("Client secret", null, null, Secret: true), entry.Changes);
        Assert.Contains(new AuditFieldChange("Webhook secret", null, null, Secret: true), entry.Changes);
        Assert.Contains(entry.Changes, change => change.Field == "Account name" && change.New == "contoso");
    }
```

Add to `CloudflareDnsSettingsServiceTests`:

```csharp
    [Fact]
    public async Task SaveAsync_RecordsANewClientSecret_WithoutItsValue()
    {
        CloudflareDnsSettings updated;
        await using (var loadContext = CreateContext())
        {
            updated = await loadContext.CloudflareDnsSettings.AsNoTracking().SingleAsync();
        }

        await using (var context = CreateContext())
        {
            await CloudflareDnsSettingsService.SaveAsync(context, TestActors.Admin, secretStore, updated, "cloudflare-secret-value");
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.CloudflareDnsSettingsSaved, entry.Action);
        Assert.Equal([new AuditFieldChange("Client secret", null, null, Secret: true)], entry.Changes);
    }
```

Add to `AlertTicketRuleServiceTests` (add `using DotMarc.Audit;`):

```csharp
    [Fact]
    public async Task SetForGroupAsync_RecordsTheGroupsOverride()
    {
        await using (var context = CreateContext())
        {
            context.Groups.Add(new Group { Name = "Client A" });
            await context.SaveChangesAsync();
            var groupId = (await context.Groups.SingleAsync()).Id;
            await AlertTicketRuleService.SetForGroupAsync(context, TestActors.Admin, groupId, AlertTypes.MissedReport, false);
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal((AuditActions.TicketRuleGroupChanged, "Client A"), (entry.Action, entry.TargetName));
        Assert.Equal([new AuditFieldChange("Missing DMARC report", "Use default", "Never create tickets")], entry.Changes);
    }

    [Fact]
    public async Task SetForGroupAsync_RemovingAnOverride_RecordsItAsUseDefault()
    {
        await using (var context = CreateContext())
        {
            context.Groups.Add(new Group { Name = "Client A" });
            await context.SaveChangesAsync();
            var groupId = (await context.Groups.SingleAsync()).Id;
            await AlertTicketRuleService.SetForGroupAsync(context, TestActors.Admin, groupId, AlertTypes.MissedReport, true);
            await AlertTicketRuleService.SetForGroupAsync(context, TestActors.Admin, groupId, AlertTypes.MissedReport, null);
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.AlertTicketRules);
        var entry = await verify.AuditEntries.OrderByDescending(auditEntry => auditEntry.Id).FirstAsync();
        Assert.Equal([new AuditFieldChange("Missing DMARC report", "Always create tickets", "Use default")], entry.Changes);
    }

    [Fact]
    public async Task SetGlobalAsync_RecordsNothing_WhenTheEffectiveSettingIsUnchanged()
    {
        await using (var context = CreateContext())
        {
            // Every alert type creates tickets by default, so turning one "on" changes nothing.
            await AlertTicketRuleService.SetGlobalAsync(context, TestActors.Admin, AlertTypes.MissedReport, true);
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.AuditEntries);
        Assert.Single(verify.AlertTicketRules);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SettingsServiceTests|FullyQualifiedName~AlertTicketRuleServiceTests" -nologo -v q`
Expected: build FAILS on the new `AuditActor` arguments.

- [ ] **Step 3: Change the notification settings service**

In `src/DotMarc/Notifications/NotificationSettingsService.cs`, add `using DotMarc.Audit;` and replace `SaveAsync`:

```csharp
    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, NotificationSettings updated, CancellationToken cancellationToken = default)
    {
        ValidateWebhookUrl(updated.TeamsWebhookUrl, "Teams webhook URL");
        ValidateWebhookUrl(updated.GenericWebhookUrl, "Generic webhook URL");

        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.NotificationSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Delivery mode", saved.DeliveryMode, updated.DeliveryMode)
            // Webhook URLs carry the token that lets anyone post to the channel, so they're recorded like secrets.
            .Secret("Teams webhook URL", saved.TeamsWebhookUrl != updated.TeamsWebhookUrl)
            .Secret("Generic webhook URL", saved.GenericWebhookUrl != updated.GenericWebhookUrl)
            .Field("Missing report threshold (days)", saved.MissingReportThresholdDays, updated.MissingReportThresholdDays)
            .Field("Cooldown (minutes)", saved.CooldownMinutes, updated.CooldownMinutes)
            .Field("Monitor interval (seconds)", saved.MonitorIntervalSeconds, updated.MonitorIntervalSeconds)
            .Field("Suspicious reject minimum volume", saved.SuspiciousRejectMinVolume, updated.SuspiciousRejectMinVolume)
            .Field("Suspicious reject non-benign %", saved.SuspiciousRejectNonBenignPercent, updated.SuspiciousRejectNonBenignPercent);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.NotificationSettings.SingleAsync(cancellationToken).ConfigureAwait(false);

        existing.Enabled = updated.Enabled;
        existing.DeliveryMode = updated.DeliveryMode;
        existing.TeamsWebhookUrl = updated.TeamsWebhookUrl;
        existing.GenericWebhookUrl = updated.GenericWebhookUrl;
        existing.MissingReportThresholdDays = updated.MissingReportThresholdDays;
        existing.CooldownMinutes = updated.CooldownMinutes;
        existing.MonitorIntervalSeconds = updated.MonitorIntervalSeconds;
        existing.SuspiciousRejectMinVolume = updated.SuspiciousRejectMinVolume;
        existing.SuspiciousRejectNonBenignPercent = updated.SuspiciousRejectNonBenignPercent;

        AuditLog.Record(context, actor, AuditActions.NotificationSettingsSaved, AuditTarget.Settings("Notifications"), "Saved notification settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Change the HaloPSA settings service**

In `src/DotMarc/Notifications/HaloPsaSettingsService.cs`, add `using DotMarc.Audit;` and replace `SaveAsync`:

```csharp
    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, HaloPsaSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        var saved = await context.HaloPsaSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewClientSecret = !string.IsNullOrWhiteSpace(newClientSecret);
        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Account name", saved.AccountName, updated.AccountName)
            .Field("Auth server URL", saved.AuthServerUrl, updated.AuthServerUrl)
            .Field("Resource server URL", saved.ResourceServerUrl, updated.ResourceServerUrl)
            .Field("Client ID", saved.ClientId, updated.ClientId)
            .Field("Ticket type", Describe(saved.TicketTypeId, saved.TicketTypeName), Describe(updated.TicketTypeId, updated.TicketTypeName))
            .Field("Default priority", Describe(saved.DefaultPriorityId, saved.DefaultPriorityName), Describe(updated.DefaultPriorityId, updated.DefaultPriorityName))
            .Field("Closed status", Describe(saved.ClosedStatusId, saved.ClosedStatusName), Describe(updated.ClosedStatusId, updated.ClosedStatusName))
            .Field("Assign new tickets to", Describe(saved.AssignedAgentId, saved.AssignedAgentName) ?? "Don't assign", Describe(updated.AssignedAgentId, updated.AssignedAgentName) ?? "Don't assign")
            .Secret("Webhook secret", saved.WebhookSecret != updated.WebhookSecret)
            .Secret("Client secret", hasNewClientSecret);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.HaloPsaSettings.SingleAsync(cancellationToken).ConfigureAwait(false);

        // (the existing field assignments, unchanged)

        if (hasNewClientSecret)
        {
            await secretStore.SetSecretAsync(HaloPsaSettings.SecretStoreKey, newClientSecret!, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.HaloSettingsSaved, AuditTarget.Settings("HaloPSA"), "Saved HaloPSA settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A chosen Halo option by its name, falling back to its id when the name wasn't saved.</summary>
    private static string? Describe(int? id, string? name) => id is null ? null : name ?? id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
```

- [ ] **Step 5: Change the three DNS settings services**

In each of `CloudflareDnsSettingsService.cs`, `AzureDnsSettingsService.cs` and `GoogleCloudDnsSettingsService.cs`,
add `using DotMarc.Audit;` and replace `SaveAsync` with this shape (shown for Cloudflare; Azure uses
`context.AzureDnsSettings`, `AzureDnsSettings.SecretStoreKey`, `AuditActions.AzureDnsSettingsSaved`,
`AuditTarget.Settings("Azure DNS")`, "Saved Azure DNS settings"; Google uses `GoogleCloudDnsSettings`,
`AuditActions.GoogleCloudDnsSettingsSaved`, `AuditTarget.Settings("Google Cloud DNS")`, "Saved Google Cloud DNS settings"):

```csharp
    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, CloudflareDnsSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        var saved = await context.CloudflareDnsSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewClientSecret = !string.IsNullOrWhiteSpace(newClientSecret);
        var changes = new AuditChanges()
            .Field("Client ID", saved.ClientId, updated.ClientId)
            .Secret("Client secret", hasNewClientSecret);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.CloudflareDnsSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.ClientId = updated.ClientId;

        if (hasNewClientSecret)
        {
            await secretStore.SetSecretAsync(CloudflareDnsSettings.SecretStoreKey, newClientSecret!, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.CloudflareDnsSettingsSaved, AuditTarget.Settings("Cloudflare DNS"), "Saved Cloudflare DNS settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 6: Change the ticket rule service**

In `src/DotMarc/Notifications/AlertTicketRuleService.cs`, add `using DotMarc.Audit;` and replace `SetGlobalAsync`,
`SetForGroupAsync`, `UpsertAsync` and `RequireKnownAlertType` with:

```csharp
    public static async Task SetGlobalAsync(DotMarcDbContext context, AuditActor actor, string alertType, bool createTicket, CancellationToken cancellationToken = default)
    {
        var alert = RequireKnownAlertType(alertType);
        var existing = await context.AlertTicketRules
            .SingleOrDefaultAsync(rule => rule.AlertType == alertType && rule.GroupId == null, cancellationToken)
            .ConfigureAwait(false);

        // Compared by effect: a missing rule means the alert type's default.
        var changes = new AuditChanges().Field("Creates tickets", existing?.CreateTicket ?? alert.CreatesTicketByDefault, createTicket);

        if (existing is null)
        {
            context.AlertTicketRules.Add(new AlertTicketRule { AlertType = alertType, GroupId = null, CreateTicket = createTicket });
        }
        else
        {
            existing.CreateTicket = createTicket;
        }

        if (changes.Any)
        {
            AuditLog.Record(context, actor, AuditActions.TicketRuleGlobalChanged, new AuditTarget("AlertType", alertType, alert.DisplayName),
                $"{alert.DisplayName} alerts {(createTicket ? "now create" : "no longer create")} tickets", changes);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets a group's override, or removes it with <c>null</c> so the group inherits again. Removing
    /// the row, not storing "inherit", means overrides never pile up as no-ops.</summary>
    public static async Task SetForGroupAsync(DotMarcDbContext context, AuditActor actor, int groupId, string alertType, bool? createTicket, CancellationToken cancellationToken = default)
    {
        var alert = RequireKnownAlertType(alertType);
        var existing = await context.AlertTicketRules
            .SingleOrDefaultAsync(rule => rule.GroupId == groupId && rule.AlertType == alertType, cancellationToken)
            .ConfigureAwait(false);
        var changes = new AuditChanges().Field(alert.DisplayName, OverrideText(existing?.CreateTicket), OverrideText(createTicket));
        if (!changes.Any)
        {
            return;
        }

        if (createTicket is null)
        {
            context.AlertTicketRules.Remove(existing!);
        }
        else if (existing is null)
        {
            context.AlertTicketRules.Add(new AlertTicketRule { AlertType = alertType, GroupId = groupId, CreateTicket = createTicket.Value });
        }
        else
        {
            existing.CreateTicket = createTicket.Value;
        }

        var group = await context.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        var target = group is null ? new AuditTarget("Group", groupId.ToString(System.Globalization.CultureInfo.InvariantCulture), null) : AuditTarget.For(group);
        AuditLog.Record(context, actor, AuditActions.TicketRuleGroupChanged, target,
            $"Set {alert.DisplayName} tickets for {group?.Name ?? $"group {groupId}"} to {OverrideText(createTicket).ToLowerInvariant()}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string OverrideText(bool? createTicket) => createTicket switch
    {
        null => "Use default",
        true => "Always create tickets",
        false => "Never create tickets"
    };

    private static AlertTypeInfo RequireKnownAlertType(string alertType) =>
        AlertTypes.Find(alertType) ?? throw new ArgumentException($"'{alertType}' is not a known alert type.", nameof(alertType));
```

`UpsertAsync` is no longer needed; delete it. The `Remove(existing!)` is safe: when `createTicket` is null and there
is no existing rule, both sides are "Use default", so the method already returned.

- [ ] **Step 7: Update the callers**

```powershell
Add-AuditActorArgument 'NotificationSettingsService\.SaveAsync' 'NotificationSettingsService.cs'
Add-AuditActorArgument 'HaloPsaSettingsService\.SaveAsync' 'HaloPsaSettingsService.cs'
Add-AuditActorArgument 'CloudflareDnsSettingsService\.SaveAsync' 'CloudflareDnsSettingsService.cs'
Add-AuditActorArgument 'AzureDnsSettingsService\.SaveAsync' 'AzureDnsSettingsService.cs'
Add-AuditActorArgument 'GoogleCloudDnsSettingsService\.SaveAsync' 'GoogleCloudDnsSettingsService.cs'
Add-AuditActorArgument 'AlertTicketRuleService\.(?:SetGlobalAsync|SetForGroupAsync)' 'AlertTicketRuleService.cs'
```

Add `@inject AuditActorAccessor AuditActorAccessor` to `AlertsSettings.razor`, `DnsPushSettings.razor` and
`GroupTicketRulesDialog.razor`. Build → `Build succeeded.`

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SettingsServiceTests|FullyQualifiedName~AlertTicketRuleServiceTests|FullyQualifiedName~PsaTicketServiceTests|FullyQualifiedName~AlertingServiceTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 9: Commit**

```powershell
git add -A src/DotMarc test/DotMarc.Tests
git commit -m "Record settings and ticket rule changes in the audit log"
```

---

### Task 7: Make sure no mutating service method skips the audit log

**Files:**
- Test: `test/DotMarc.Tests/Audit/AuditCoverageTests.cs`

**Interfaces:**
- Consumes: every service changed in Tasks 3 to 6.

- [ ] **Step 1: Write the test**

```csharp
using System.Reflection;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Audit;

/// <summary>A new mutating method on an audited service must take an actor, or its changes go unrecorded. This
/// fails the build's tests if one doesn't.</summary>
public sealed class AuditCoverageTests
{
    private static readonly Type[] AuditedServices =
    [
        typeof(DomainManagementService), typeof(GroupManagementService), typeof(TagManagementService),
        typeof(RoleManagementService), typeof(UserAccessManagementService), typeof(AlertTicketRuleService),
        typeof(NotificationSettingsService), typeof(HaloPsaSettingsService), typeof(CloudflareDnsSettingsService),
        typeof(AzureDnsSettingsService), typeof(GoogleCloudDnsSettingsService),
    ];

    private static readonly string[] ReadMethodPrefixes = ["Get", "List", "Count", "Resolve"];

    [Fact]
    public void EveryMutatingServiceMethod_TakesTheActorRightAfterTheContext()
    {
        var mutatingMethods = AuditedServices
            .SelectMany(service => service.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => !ReadMethodPrefixes.Any(prefix => method.Name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        // Guards against the list above silently matching nothing.
        Assert.True(mutatingMethods.Count >= 29, $"Expected at least 29 mutating methods, found {mutatingMethods.Count}.");

        var missingActor = mutatingMethods
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length < 2
                    || parameters[0].ParameterType != typeof(DotMarcDbContext)
                    || parameters[1].ParameterType != typeof(AuditActor);
            })
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToList();

        Assert.True(missingActor.Count == 0,
            "These change data without taking (DotMarcDbContext context, AuditActor actor, ...): " + string.Join(", ", missingActor));
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AuditCoverageTests" -nologo -v q`
Expected: PASS. (It passes straight away because Tasks 3 to 6 already did the work. To see it guard, temporarily
remove the `AuditActor` parameter from one method's signature, watch it fail naming that method, then restore it.)

- [ ] **Step 3: Commit**

```powershell
git add test/DotMarc.Tests/Audit/AuditCoverageTests.cs
git commit -m "Test that every mutating service method takes an audit actor"
```

---

### Task 8: Record DNS pushes and Halo actions

**Files:**
- Create: `src/DotMarc/Audit/AuditRecorder.cs`, `src/DotMarc/Audit/DnsPushAudit.cs`
- Modify: `src/DotMarc/Program.cs` (register `AuditRecorder`; record in the DNS push callback)
- Modify: `src/DotMarc/Components/Pages/AlertsSettings.razor` (record the Halo test and clearing the sign-in)
- Test: `test/DotMarc.Tests/Audit/AuditRecorderTests.cs`, `test/DotMarc.Tests/Audit/DnsPushAuditTests.cs`

**Interfaces:**
- Consumes: Task 2's `AuditLog.Create`, `AuditActor.FromPrincipal`, `AuditTarget`, `AuditActions`.
- Produces: `sealed class AuditRecorder` (singleton) with `Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)`
  that never throws; `static class DnsPushAudit` with
  `AuditEntry CreateEntry(AuditActor actor, Domain domain, string provider, IReadOnlyList<DnsRecordChange> changes)`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Audit/DnsPushAuditTests.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class DnsPushAuditTests
{
    [Fact]
    public void CreateEntry_RecordsEachRecordsOldAndNewValue()
    {
        var domain = new Domain { Id = 42, Name = "contoso.com" };
        IReadOnlyList<DnsRecordChange> changes =
        [
            new(DnsRecordChangeKind.Merge, "TXT", "_dmarc.contoso.com", "v=DMARC1; p=none; rua=mailto:reports@dotmarc.example", "v=DMARC1; p=none", "contoso.com"),
        ];

        var entry = DnsPushAudit.CreateEntry(TestActors.Admin, domain, "cloudflare", changes);

        Assert.Equal((AuditActions.DnsPushed, "42", "contoso.com"), (entry.Action, entry.TargetId, entry.TargetName));
        Assert.Equal("Pushed 1 DNS record for contoso.com to cloudflare", entry.Summary);
        Assert.Equal(
            [new AuditFieldChange("TXT _dmarc.contoso.com", "v=DMARC1; p=none", "v=DMARC1; p=none; rua=mailto:reports@dotmarc.example")],
            entry.Changes);
    }
}
```

Create `test/DotMarc.Tests/Audit/AuditRecorderTests.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Audit;

[Collection("Postgres")]
public sealed class AuditRecorderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AuditRecorderTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = new FakeDbContextFactory(_connectionString).CreateDbContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private static AuditEntry SampleEntry() =>
        AuditLog.Create(TestActors.Admin, AuditEntryKind.PageView, AuditActions.PageViewed, null, "Opened /dashboard");

    [Fact]
    public async Task RecordAsync_SavesTheEntry()
    {
        var recorder = new AuditRecorder(new FakeDbContextFactory(_connectionString), NullLogger<AuditRecorder>.Instance);

        await recorder.RecordAsync(SampleEntry());

        await using var verify = new FakeDbContextFactory(_connectionString).CreateDbContext();
        Assert.Equal("Opened /dashboard", (await verify.AuditEntries.SingleAsync()).Summary);
    }

    [Fact]
    public async Task RecordAsync_DoesNotThrow_WhenTheDatabaseIsUnreachable()
    {
        var unreachable = new FakeDbContextFactory("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");
        var recorder = new AuditRecorder(unreachable, NullLogger<AuditRecorder>.Instance);

        var recording = await Record.ExceptionAsync(() => recorder.RecordAsync(SampleEntry()));

        Assert.Null(recording);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsPushAuditTests|FullyQualifiedName~AuditRecorderTests" -nologo -v q`
Expected: build FAILS, `DnsPushAudit` and `AuditRecorder` not found.

- [ ] **Step 3: Write the recorder and the DNS push entry**

Create `src/DotMarc/Audit/AuditRecorder.cs`:

```csharp
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Audit;

/// <summary>Saves an audit entry on its own, for things that aren't a database change a service makes: sign-ins,
/// page views, DNS pushes and Halo actions. Best-effort by design: a failure is logged as a warning and never
/// reaches the person, so a database hiccup can't block a sign-in or a page.</summary>
public sealed class AuditRecorder(IDbContextFactory<DotMarcDbContext> dbContextFactory, ILogger<AuditRecorder> logger)
{
    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            context.AuditEntries.Add(entry);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Couldn't record the audit entry {Action} for {ActorName}", entry.Action, entry.ActorName);
        }
    }
}
```

Create `src/DotMarc/Audit/DnsPushAudit.cs`:

```csharp
using DotMarc.Data;
using DotMarc.DnsPush;

namespace DotMarc.Audit;

/// <summary>The audit entry for a successful DNS push: one field change per record, named by type and name.</summary>
public static class DnsPushAudit
{
    public static AuditEntry CreateEntry(AuditActor actor, Domain domain, string provider, IReadOnlyList<DnsRecordChange> changes) =>
        AuditLog.Create(actor, AuditEntryKind.Change, AuditActions.DnsPushed, AuditTarget.For(domain),
            $"Pushed {changes.Count} DNS {(changes.Count == 1 ? "record" : "records")} for {domain.Name} to {provider}",
            changes.Select(change => new AuditFieldChange($"{change.RecordType} {change.Name}", change.ExistingValue, change.DesiredValue)).ToList());
}
```

- [ ] **Step 4: Register the recorder and record DNS pushes**

In `src/DotMarc/Program.cs`, after the `AuditActorAccessor` registration from Task 2, add:

```csharp
builder.Services.AddSingleton<DotMarc.Audit.AuditRecorder>();
```

In the `app.MapGet("/dns-push/{provider}/callback", ...)` lambda, add a parameter `DotMarc.Audit.AuditRecorder auditRecorder`
after `IAuthorizationService authorizationService`. Directly after
`var result = await pushProvider.ExchangeAndPushAsync(code, decodedState.CodeVerifier, redirectUri, changes, CancellationToken.None);`
add:

```csharp
    // Recorded only when the push went through. A push that failed changed nothing, and is already logged below.
    if (result.Outcome == DnsPushOutcome.Pushed)
    {
        await auditRecorder.RecordAsync(DotMarc.Audit.DnsPushAudit.CreateEntry(DotMarc.Audit.AuditActor.FromPrincipal(httpContext.User), domain, provider, changes));
    }
```

(The records are already live in DNS at this point, so this entry can't share a transaction with the change; it is
recorded best-effort like sign-ins.)

- [ ] **Step 5: Record the Halo actions**

In `src/DotMarc/Components/Pages/AlertsSettings.razor`, add `@inject AuditRecorder AuditRecorder` next to the other
injects. In `ClearHaloSignInAsync`, directly after `var dropped = await HaloTokenCache.ClearAsync();`, add:

```csharp
        await AuditRecorder.RecordAsync(AuditLog.Create(await AuditActorAccessor.GetAsync(), AuditEntryKind.Change,
            AuditActions.HaloSignInCleared, AuditTarget.Settings("HaloPSA"), "Cleared the cached HaloPSA sign-in"));
```

In `RunHaloTestAsync`, directly after `_testRun = await HaloTester.RunAsync(_testHaloClientId, progress, null, CancellationToken.None);`, add:

```csharp
        await AuditRecorder.RecordAsync(AuditLog.Create(await AuditActorAccessor.GetAsync(), AuditEntryKind.Change,
            AuditActions.HaloIntegrationTested, AuditTarget.Settings("HaloPSA"), "Ran the HaloPSA integration test"));
```

(The other `HaloTokenCache.ClearAsync()` call, after saving PSA settings, is a side effect of that save, which is
already recorded, so it isn't recorded separately.)

- [ ] **Step 6: Run the tests and build**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsPushAuditTests|FullyQualifiedName~AuditRecorderTests|FullyQualifiedName~ProgramDiValidationTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/DotMarc/Audit src/DotMarc/Program.cs src/DotMarc/Components/Pages/AlertsSettings.razor test/DotMarc.Tests/Audit
git commit -m "Record DNS pushes and HaloPSA actions in the audit log"
```

---

### Task 9: Record sign-ins and page views

**Files:**
- Create: `src/DotMarc/Audit/SignInAuditor.cs`, `src/DotMarc/Audit/PageViewPaths.cs`
- Modify: `src/DotMarc/Program.cs` (register `SignInAuditor`; hook Entra `OnTokenValidated`; record demo sign-ins)
- Modify: `src/DotMarc/Components/Layout/MainLayout.razor` (record page views)
- Test: `test/DotMarc.Tests/Audit/SignInAuditorTests.cs`, `test/DotMarc.Tests/Audit/PageViewPathsTests.cs`

**Interfaces:**
- Consumes: Task 8's `AuditRecorder`; Task 2's `AuditActor.FromPrincipal`, `AuditLog.Create`, `AuditTarget.DomainNamed`;
  `UserAccessManagementService.ResolveAsync`.
- Produces: `sealed class SignInAuditor` (singleton) with `Task RecordAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)`;
  `static class PageViewPaths` with `string PathOf(string absoluteUri)` and `string? DomainOf(string path)`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Audit/PageViewPathsTests.cs`:

```csharp
using DotMarc.Audit;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class PageViewPathsTests
{
    [Theory]
    [InlineData("https://dotmarc.example/domains/contoso.com/sources", "/domains/contoso.com/sources")]
    [InlineData("https://dotmarc.example/dashboard?dnsPush=invalid#top", "/dashboard")]
    [InlineData("https://dotmarc.example/domains/xn--bcher-kva.example?state=abc", "/domains/xn--bcher-kva.example")]
    [InlineData("https://dotmarc.example/", "/")]
    public void PathOf_KeepsThePathOnly(string absoluteUri, string expectedPath)
    {
        Assert.Equal(expectedPath, PageViewPaths.PathOf(absoluteUri));
    }

    [Theory]
    [InlineData("/domains/contoso.com", "contoso.com")]
    [InlineData("/domains/contoso.com/sources", "contoso.com")]
    [InlineData("/domains", null)]
    [InlineData("/dashboard", null)]
    public void DomainOf_FindsTheDomainADomainPageIsAbout(string path, string? expectedDomain)
    {
        Assert.Equal(expectedDomain, PageViewPaths.DomainOf(path));
    }
}
```

Create `test/DotMarc.Tests/Audit/SignInAuditorTests.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Audit;

[Collection("Postgres")]
public sealed class SignInAuditorTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public SignInAuditorTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = new FakeDbContextFactory(_connectionString).CreateDbContext();
        await context.Database.MigrateAsync();
        context.Roles.Add(new Role { Name = "Reader", Permissions = [Permission.DomainsView] });
        await context.SaveChangesAsync();
        var roleId = (await context.Roles.SingleAsync()).Id;
        await UserAccessManagementService.GrantAccessAsync(context, TestActors.Admin, "sam@contoso.com", roleId, []);
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private SignInAuditor CreateAuditor()
    {
        var factory = new FakeDbContextFactory(_connectionString);
        return new SignInAuditor(factory, new AuditRecorder(factory, NullLogger<AuditRecorder>.Instance), NullLogger<SignInAuditor>.Instance);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Test", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role));

    private async Task<AuditEntry> SingleSignInEntryAsync()
    {
        await using var verify = new FakeDbContextFactory(_connectionString).CreateDbContext();
        return await verify.AuditEntries.SingleAsync(entry => entry.Kind == AuditEntryKind.SignIn);
    }

    [Fact]
    public async Task RecordAsync_RecordsASuccessfulSignIn_ForSomeoneWithAGrant()
    {
        await CreateAuditor().RecordAsync(Principal(new Claim("preferred_username", "sam@contoso.com"), new Claim(ClaimTypes.Name, "Sam Jones")));

        var entry = await SingleSignInEntryAsync();
        Assert.Equal((AuditActions.SignInSucceeded, "Sam Jones", "sam@contoso.com", "Sam Jones signed in"), (entry.Action, entry.ActorName, entry.ActorEmail, entry.Summary));
    }

    [Fact]
    public async Task RecordAsync_RecordsARefusal_WithTheEmailTheyTried()
    {
        await CreateAuditor().RecordAsync(Principal(new Claim("preferred_username", "stranger@fabrikam.com")));

        var entry = await SingleSignInEntryAsync();
        Assert.Equal((AuditActions.SignInRefused, "stranger@fabrikam.com"), (entry.Action, entry.ActorEmail));
    }

    [Fact]
    public async Task RecordAsync_RecordsAnUnknownUserAsRefused_WhenTheTokenHasNoIdentityClaims()
    {
        await CreateAuditor().RecordAsync(new ClaimsPrincipal(new ClaimsIdentity()));

        var entry = await SingleSignInEntryAsync();
        Assert.Equal((AuditActions.SignInRefused, "Unknown user"), (entry.Action, entry.ActorName));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PageViewPathsTests|FullyQualifiedName~SignInAuditorTests" -nologo -v q`
Expected: build FAILS, `PageViewPaths` and `SignInAuditor` not found.

- [ ] **Step 3: Write the sign-in auditor and path helpers**

Create `src/DotMarc/Audit/SignInAuditor.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Audit;

/// <summary>Records a sign-in once, where it happens (the Entra token-validated event, or the demo sign-in), not on
/// every request. Whether it counts as refused depends on the person having an access grant, the same lookup the
/// claims transformation makes.</summary>
public sealed class SignInAuditor(IDbContextFactory<DotMarcDbContext> dbContextFactory, AuditRecorder recorder, ILogger<SignInAuditor> logger)
{
    public async Task RecordAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var actor = AuditActor.FromPrincipal(principal);
        bool hasAccess;
        try
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            hasAccess = await UserAccessManagementService.ResolveAsync(context, actor.ObjectId, actor.Email, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception exception)
        {
            // Best-effort: recording a sign-in must never stop one.
            logger.LogWarning(exception, "Couldn't check {ActorName}'s access to record their sign-in", actor.Name);
            return;
        }

        var entry = hasAccess
            ? AuditLog.Create(actor, AuditEntryKind.SignIn, AuditActions.SignInSucceeded, null, $"{actor.Name} signed in")
            : AuditLog.Create(actor, AuditEntryKind.SignIn, AuditActions.SignInRefused, null, $"{actor.Name} tried to sign in without an access grant");
        await recorder.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
    }
}
```

Create `src/DotMarc/Audit/PageViewPaths.cs`:

```csharp
namespace DotMarc.Audit;

/// <summary>Turns a page URL into what a page view records.</summary>
public static class PageViewPaths
{
    /// <summary>The path alone. The query string and fragment are dropped because DNS push state travels in the
    /// query string, and they add nothing to "which page".</summary>
    public static string PathOf(string absoluteUri) => Uri.UnescapeDataString(new Uri(absoluteUri).AbsolutePath);

    /// <summary>The domain a /domains/{name} page (or one of its tabs) is about, or null for any other page,
    /// including the /domains list itself.</summary>
    public static string? DomainOf(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && segments[0] == "domains" ? segments[1] : null;
    }
}
```

- [ ] **Step 4: Hook up sign-ins**

In `src/DotMarc/Program.cs`, after `builder.Services.AddSingleton<DotMarc.Audit.AuditRecorder>();` add:

```csharp
builder.Services.AddSingleton<DotMarc.Audit.SignInAuditor>();
```

In the `else` branch that calls `.AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("EntraId"));`, after the
existing `Configure<CookieAuthenticationOptions>` call, add:

```csharp
    // Records each Entra sign-in once, as it completes, rather than in the claims transformation, which runs on
    // every request. Chains onto whatever handler Microsoft.Identity.Web has already set.
    builder.Services.Configure<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(
        Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme,
        options =>
        {
            var previousOnTokenValidated = options.Events.OnTokenValidated;
            options.Events.OnTokenValidated = async tokenValidatedContext =>
            {
                await previousOnTokenValidated(tokenValidatedContext);
                if (tokenValidatedContext.Principal is { } principal)
                {
                    var signInAuditor = tokenValidatedContext.HttpContext.RequestServices.GetRequiredService<DotMarc.Audit.SignInAuditor>();
                    await signInAuditor.RecordAsync(principal, tokenValidatedContext.HttpContext.RequestAborted);
                }
            };
        });
```

In the demo sign-in endpoint `app.MapPost("/demo/sign-in/{persona}", async (string persona, HttpContext httpContext) =>`,
add a parameter so it reads `async (string persona, HttpContext httpContext, DotMarc.Audit.SignInAuditor signInAuditor) =>`,
and replace the `SignInAsync` call with:

```csharp
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        await httpContext.SignInAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, principal);
        await signInAuditor.RecordAsync(principal, httpContext.RequestAborted);
```

- [ ] **Step 5: Hook up page views**

In `src/DotMarc/Components/Layout/MainLayout.razor`, add near the top:

```razor
@implements IDisposable
@inject NavigationManager Navigation
@inject AuthenticationStateProvider AuthenticationStateProvider
@inject AuditRecorder AuditRecorder
```

In the `@code` block, change `OnAfterRenderAsync` and add the new members:

```csharp
    private string? _lastRecordedPath;

    protected override void OnInitialized() => Navigation.LocationChanged += OnLocationChanged;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Read after the first render, not in OnInitializedAsync: JS interop isn't available
            // until the circuit's first render completes.
            var stored = await Js.InvokeAsync<string?>("localStorage.getItem", "dotmarc-dark-mode");
            _isDarkMode = stored == "true";
            StateHasChanged();

            // The first page of the circuit. Recorded here, not in OnInitialized, because this only runs once the
            // circuit is interactive, so a prerender doesn't count the same view twice.
            await RecordPageViewAsync(Navigation.Uri);
        }
    }

    private async void OnLocationChanged(object? sender, LocationChangedEventArgs args) => await RecordPageViewAsync(args.Location);

    /// <summary>Best-effort, like every page view: AuditRecorder never throws, and anything else here is caught,
    /// because this runs from an async void event handler where an exception would end the circuit.</summary>
    private async Task RecordPageViewAsync(string uri)
    {
        try
        {
            var path = PageViewPaths.PathOf(uri);
            if (path == _lastRecordedPath)
            {
                return;
            }

            _lastRecordedPath = path;
            var user = (await AuthenticationStateProvider.GetAuthenticationStateAsync()).User;
            if (user.Identity?.IsAuthenticated != true)
            {
                return;
            }

            var domainName = PageViewPaths.DomainOf(path);
            await AuditRecorder.RecordAsync(AuditLog.Create(AuditActor.FromPrincipal(user), AuditEntryKind.PageView, AuditActions.PageViewed,
                domainName is null ? null : AuditTarget.DomainNamed(domainName), $"Opened {path}"));
        }
        catch (Exception)
        {
            // Nothing to do: a missed page view must not break the page.
        }
    }

    public void Dispose() => Navigation.LocationChanged -= OnLocationChanged;
```

Add `@using Microsoft.AspNetCore.Components.Routing` at the top if `LocationChangedEventArgs` isn't already in scope.

- [ ] **Step 6: Run the tests and build**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PageViewPathsTests|FullyQualifiedName~SignInAuditorTests|FullyQualifiedName~ProgramDiValidationTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/DotMarc/Audit src/DotMarc/Program.cs src/DotMarc/Components/Layout/MainLayout.razor test/DotMarc.Tests/Audit
git commit -m "Record sign-ins and page views in the audit log"
```

---

### Task 10: Permissions, filtering and CSV

**Files:**
- Modify: `src/DotMarc/Data/Permission.cs` (append `AuditView`, `AuditManage`)
- Create: `src/DotMarc/Audit/AuditFilter.cs`, `src/DotMarc/Audit/AuditQuery.cs`, `src/DotMarc/Audit/AuditCsv.cs`
- Test: `test/DotMarc.Tests/Audit/AuditFilterTests.cs`, `AuditQueryTests.cs`, `AuditCsvTests.cs`

**Interfaces:**
- Produces:
  - `Permission.AuditView`, `Permission.AuditManage` (policies of the same names are registered by the existing loop
    in `Program.cs`, and the Admin role gains both at startup through `AccessBootstrapper`).
  - `sealed record AuditFilter` with init properties `DateOnly? From`, `DateOnly? To`, `AuditEntryKind? Kind`,
    `bool IncludePageViews`, `string? Who`, `string? Action`, `string? Target`, `string? Summary`, and methods
    `string ToQueryString()`, `static AuditFilter FromQuery(IQueryCollection query)`, `string Describe()`.
  - `static class AuditQuery` with `IQueryable<AuditEntry> Apply(IQueryable<AuditEntry> entries, AuditFilter filter)`.
  - `static class AuditCsv` with `Task WriteAsync(TextWriter writer, IAsyncEnumerable<AuditEntry> entries, CancellationToken cancellationToken)`
    and `string FormatChanges(IEnumerable<AuditFieldChange> changes)`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Audit/AuditFilterTests.cs`:

```csharp
using DotMarc.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class AuditFilterTests
{
    [Fact]
    public void AFilter_SurvivesTheExportLinksQueryString()
    {
        var filter = new AuditFilter
        {
            From = new DateOnly(2026, 9, 1),
            To = new DateOnly(2026, 9, 29),
            Kind = AuditEntryKind.Change,
            IncludePageViews = true,
            Who = "sam & co",
            Action = AuditActions.GroupRenamed,
            Target = "contoso.com",
            Summary = "100%"
        };

        var roundTripped = AuditFilter.FromQuery(new QueryCollection(QueryHelpers.ParseQuery(filter.ToQueryString())));

        Assert.Equal(filter, roundTripped);
    }

    [Fact]
    public void AnEmptyQuery_IsAnEmptyFilter()
    {
        Assert.Equal(new AuditFilter(), AuditFilter.FromQuery(new QueryCollection()));
        Assert.Equal("", new AuditFilter().ToQueryString());
    }

    [Fact]
    public void Describe_SummarisesTheFilterForTheExportEntry()
    {
        var filter = new AuditFilter { From = new DateOnly(2026, 9, 1), Who = "sam" };

        Assert.Equal("from 2026-09-01, who contains \"sam\"", filter.Describe());
        Assert.Equal("everything", new AuditFilter().Describe());
    }
}
```

Create `test/DotMarc.Tests/Audit/AuditQueryTests.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Audit;

[Collection("Postgres")]
public sealed class AuditQueryTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AuditQueryTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        context.AuditEntries.AddRange(
            Entry(1, AuditEntryKind.Change, "Sam Jones", AuditActions.GroupRenamed, "Client B", "Renamed group Client A to Client B", new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero)),
            Entry(2, AuditEntryKind.Change, "Alex Kim", AuditActions.NotificationSettingsSaved, "Notifications", "Set the non-benign threshold to 100%", new DateTimeOffset(2026, 9, 29, 23, 59, 0, TimeSpan.Zero)),
            Entry(3, AuditEntryKind.SignIn, "Sam Jones", AuditActions.SignInSucceeded, null, "Sam Jones signed in", new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero)),
            Entry(4, AuditEntryKind.PageView, "Sam Jones", AuditActions.PageViewed, "contoso.com", "Opened /domains/contoso.com", new DateTimeOffset(2026, 9, 15, 8, 1, 0, TimeSpan.Zero)),
            Entry(5, AuditEntryKind.Change, "Alex Kim", AuditActions.NotificationSettingsSaved, "Notifications", "Set the non-benign threshold to 1000", new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)));
        await context.SaveChangesAsync();
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

    private static AuditEntry Entry(long id, AuditEntryKind kind, string actorName, string action, string? targetName, string summary, DateTimeOffset occurredUtc) => new()
    {
        Id = id, Kind = kind, ActorName = actorName, ActorEmail = $"{actorName.Split(' ')[0].ToLowerInvariant()}@contoso.com",
        Action = action, TargetName = targetName, Summary = summary, OccurredUtc = occurredUtc
    };

    private async Task<long[]> MatchingIdsAsync(AuditFilter filter)
    {
        await using var context = CreateContext();
        return await AuditQuery.Apply(context.AuditEntries, filter).OrderBy(entry => entry.Id).Select(entry => entry.Id).ToArrayAsync();
    }

    [Fact]
    public async Task PageViewsAreHidden_UnlessAskedFor()
    {
        Assert.Equal([1, 2, 3, 5], await MatchingIdsAsync(new AuditFilter()));
        Assert.Equal([1, 2, 3, 4, 5], await MatchingIdsAsync(new AuditFilter { IncludePageViews = true }));
        Assert.Equal([4], await MatchingIdsAsync(new AuditFilter { Kind = AuditEntryKind.PageView }));
    }

    [Fact]
    public async Task TheToDate_IncludesTheWholeDay()
    {
        Assert.Equal([1, 2, 3], await MatchingIdsAsync(new AuditFilter { From = new DateOnly(2026, 9, 10), To = new DateOnly(2026, 9, 29) }));
    }

    [Fact]
    public async Task Who_MatchesTheNameOrEmail_IgnoringCase()
    {
        Assert.Equal([1, 3], await MatchingIdsAsync(new AuditFilter { Who = "SAM" }));
        Assert.Equal([2, 5], await MatchingIdsAsync(new AuditFilter { Who = "alex@contoso" }));
    }

    [Fact]
    public async Task SearchText_TreatsPercentAndUnderscoreLiterally()
    {
        Assert.Equal([2], await MatchingIdsAsync(new AuditFilter { Summary = "100%" }));
        Assert.Empty(await MatchingIdsAsync(new AuditFilter { Summary = "threshold_to" }));
    }

    [Fact]
    public async Task ActionAndTarget_Narrow()
    {
        Assert.Equal([1], await MatchingIdsAsync(new AuditFilter { Action = AuditActions.GroupRenamed }));
        Assert.Equal([2, 5], await MatchingIdsAsync(new AuditFilter { Target = "notif" }));
    }
}
```

Create `test/DotMarc.Tests/Audit/AuditCsvTests.cs`:

```csharp
using DotMarc.Audit;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class AuditCsvTests
{
    private static async Task<string[]> WriteAsync(params AuditEntry[] entries)
    {
        await using var writer = new StringWriter();
        await AuditCsv.WriteAsync(writer, entries.ToAsyncEnumerable(), CancellationToken.None);
        return writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static AuditEntry Entry(string summary, params AuditFieldChange[] changes) => new()
    {
        OccurredUtc = new DateTimeOffset(2026, 9, 29, 10, 30, 0, TimeSpan.Zero),
        Kind = AuditEntryKind.Change, ActorName = "Sam Jones", ActorEmail = "sam@contoso.com",
        Action = AuditActions.GroupRenamed, TargetType = "Group", TargetName = "Client B", Summary = summary, Changes = [.. changes]
    };

    [Fact]
    public async Task TheFirstLine_IsTheHeader()
    {
        var lines = await WriteAsync();

        Assert.Equal("Time (UTC),Kind,Who,Email,Action,Target type,Target,Summary,Changes", lines.Single());
    }

    [Fact]
    public async Task ARow_HasEveryColumn_WithChangesFlattened()
    {
        var lines = await WriteAsync(Entry("Renamed group", new AuditFieldChange("Name", "Client A", "Client B"), new AuditFieldChange("Client secret", null, null, true)));

        Assert.Equal("2026-09-29 10:30:00,Change,Sam Jones,sam@contoso.com,group.renamed,Group,Client B,Renamed group,Name: Client A -> Client B; Client secret: changed (value not recorded)", lines[1]);
    }

    [Fact]
    public async Task CommasQuotesAndLineBreaks_StayInOneCell()
    {
        await using var writer = new StringWriter();
        await AuditCsv.WriteAsync(writer, new[] { Entry("Said \"hi\", then\nleft") }.ToAsyncEnumerable(), CancellationToken.None);

        Assert.Contains("\"Said \"\"hi\"\", then\nleft\"", writer.ToString());
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")")]
    [InlineData("+1+1")]
    [InlineData("-2")]
    [InlineData("@SUM(A1)")]
    public async Task ACellStartingLikeAFormula_IsEscaped(string summary)
    {
        var lines = await WriteAsync(Entry(summary));

        // The apostrophe goes first in the cell. A formula containing quotes is also wrapped in quotes, so the
        // cell may start with a quote before the apostrophe.
        Assert.Contains("'" + summary.Replace("\"", "\"\""), lines[1]);
    }
}
```

(`ToAsyncEnumerable` comes from `System.Linq.AsyncEnumerable` in .NET 10's base library. If the build says it's
missing, add a two-line local helper: `static async IAsyncEnumerable<AuditEntry> AsAsync(IEnumerable<AuditEntry> entries) { foreach (var entry in entries) { yield return entry; } await Task.CompletedTask; }`.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AuditFilterTests|FullyQualifiedName~AuditQueryTests|FullyQualifiedName~AuditCsvTests" -nologo -v q`
Expected: build FAILS, `AuditFilter`, `AuditQuery`, `AuditCsv` not found.

- [ ] **Step 3: Add the permissions**

In `src/DotMarc/Data/Permission.cs`, change the last line of the enum from `LogsView` to:

```csharp
    LogsView,
    AuditView,
    AuditManage
```

(Permissions are stored by name, so appending is safe for existing roles.)

- [ ] **Step 4: Write the filter, query and CSV writer**

Create `src/DotMarc/Audit/AuditFilter.cs`:

```csharp
using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace DotMarc.Audit;

/// <summary>What the Audit log page is showing. Dates are UTC days, and <see cref="To"/> includes the whole day.
/// It travels in the export link's query string, so the export matches the page exactly.</summary>
public sealed record AuditFilter
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public AuditEntryKind? Kind { get; init; }
    public bool IncludePageViews { get; init; }
    public string? Who { get; init; }
    public string? Action { get; init; }
    public string? Target { get; init; }
    public string? Summary { get; init; }

    private const string DateFormat = "yyyy-MM-dd";

    public string ToQueryString()
    {
        var pairs = new List<KeyValuePair<string, string?>>();
        if (From is { } from) pairs.Add(new("from", from.ToString(DateFormat, CultureInfo.InvariantCulture)));
        if (To is { } to) pairs.Add(new("to", to.ToString(DateFormat, CultureInfo.InvariantCulture)));
        if (Kind is { } kind) pairs.Add(new("kind", kind.ToString()));
        if (IncludePageViews) pairs.Add(new("pageViews", "true"));
        if (!string.IsNullOrWhiteSpace(Who)) pairs.Add(new("who", Who));
        if (!string.IsNullOrWhiteSpace(Action)) pairs.Add(new("action", Action));
        if (!string.IsNullOrWhiteSpace(Target)) pairs.Add(new("target", Target));
        if (!string.IsNullOrWhiteSpace(Summary)) pairs.Add(new("summary", Summary));
        return QueryString.Create(pairs).ToUriComponent().TrimStart('?');
    }

    public static AuditFilter FromQuery(IQueryCollection query) => new()
    {
        From = ParseDate(query["from"]),
        To = ParseDate(query["to"]),
        Kind = Enum.TryParse<AuditEntryKind>(query["kind"].ToString(), ignoreCase: true, out var kind) ? kind : null,
        IncludePageViews = query["pageViews"] == "true",
        Who = NullIfBlank(query["who"]),
        Action = NullIfBlank(query["action"]),
        Target = NullIfBlank(query["target"]),
        Summary = NullIfBlank(query["summary"]),
    };

    /// <summary>A short description for the audit.exported entry, such as <c>from 2026-09-01, who contains "sam"</c>.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (From is { } from) parts.Add($"from {from.ToString(DateFormat, CultureInfo.InvariantCulture)}");
        if (To is { } to) parts.Add($"to {to.ToString(DateFormat, CultureInfo.InvariantCulture)}");
        if (Kind is { } kind) parts.Add($"kind {kind}");
        if (IncludePageViews) parts.Add("including page views");
        if (!string.IsNullOrWhiteSpace(Who)) parts.Add($"who contains \"{Who}\"");
        if (!string.IsNullOrWhiteSpace(Action)) parts.Add($"action {Action}");
        if (!string.IsNullOrWhiteSpace(Target)) parts.Add($"target contains \"{Target}\"");
        if (!string.IsNullOrWhiteSpace(Summary)) parts.Add($"summary contains \"{Summary}\"");
        return parts.Count == 0 ? "everything" : string.Join(", ", parts);
    }

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
```

Create `src/DotMarc/Audit/AuditQuery.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Audit;

/// <summary>Applies an <see cref="AuditFilter"/> in the database, for the page's paging and for export.</summary>
public static class AuditQuery
{
    private const string LikeEscape = "\\";

    public static IQueryable<AuditEntry> Apply(IQueryable<AuditEntry> entries, AuditFilter filter)
    {
        if (filter.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            entries = entries.Where(entry => entry.OccurredUtc >= start);
        }

        if (filter.To is { } to)
        {
            // The whole To day: everything before midnight at its end.
            var endExclusive = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            entries = entries.Where(entry => entry.OccurredUtc < endExclusive);
        }

        if (filter.Kind is { } kind)
        {
            entries = entries.Where(entry => entry.Kind == kind);
        }
        else if (!filter.IncludePageViews)
        {
            entries = entries.Where(entry => entry.Kind != AuditEntryKind.PageView);
        }

        if (ContainsPattern(filter.Who) is { } whoPattern)
        {
            entries = entries.Where(entry => EF.Functions.ILike(entry.ActorName, whoPattern, LikeEscape)
                || (entry.ActorEmail != null && EF.Functions.ILike(entry.ActorEmail, whoPattern, LikeEscape)));
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            entries = entries.Where(entry => entry.Action == filter.Action);
        }

        if (ContainsPattern(filter.Target) is { } targetPattern)
        {
            entries = entries.Where(entry => entry.TargetName != null && EF.Functions.ILike(entry.TargetName, targetPattern, LikeEscape));
        }

        if (ContainsPattern(filter.Summary) is { } summaryPattern)
        {
            entries = entries.Where(entry => EF.Functions.ILike(entry.Summary, summaryPattern, LikeEscape));
        }

        return entries;
    }

    /// <summary>A case-insensitive "contains" pattern, with LIKE's own wildcards escaped so "100%" means the text
    /// "100%", not "100 then anything".</summary>
    private static string? ContainsPattern(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
```

Create `src/DotMarc/Audit/AuditCsv.cs`:

```csharp
using System.Globalization;

namespace DotMarc.Audit;

/// <summary>Writes audit entries as CSV, one row at a time, so a large export never sits in memory.</summary>
public static class AuditCsv
{
    private static readonly string[] Header = ["Time (UTC)", "Kind", "Who", "Email", "Action", "Target type", "Target", "Summary", "Changes"];

    public static async Task WriteAsync(TextWriter writer, IAsyncEnumerable<AuditEntry> entries, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(string.Join(',', Header.Select(Cell))).ConfigureAwait(false);
        await foreach (var entry in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var row = new[]
            {
                entry.OccurredUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                entry.Kind.ToString(),
                entry.ActorName,
                entry.ActorEmail,
                entry.Action,
                entry.TargetType,
                entry.TargetName,
                entry.Summary,
                FormatChanges(entry.Changes),
            };
            await writer.WriteLineAsync(string.Join(',', row.Select(Cell))).ConfigureAwait(false);
        }
    }

    public static string FormatChanges(IEnumerable<AuditFieldChange> changes) =>
        string.Join("; ", changes.Select(change => change.Secret
            ? $"{change.Field}: changed (value not recorded)"
            : $"{change.Field}: {change.Old ?? "(none)"} -> {change.New ?? "(none)"}"));

    /// <summary>One CSV cell. A value a spreadsheet would run as a formula gets a leading apostrophe, and a value
    /// with a comma, quote or line break is quoted with its quotes doubled.</summary>
    private static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0]))
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AuditFilterTests|FullyQualifiedName~AuditQueryTests|FullyQualifiedName~AuditCsvTests|FullyQualifiedName~AccessBootstrapperTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/DotMarc/Audit src/DotMarc/Data/Permission.cs test/DotMarc.Tests/Audit
git commit -m "Add audit permissions, filtering and CSV output"
```

---

### Task 11: The Audit log page and export

**Files:**
- Create: `src/DotMarc/Components/Pages/AuditLogPage.razor` (named `...Page` because a component class called
  `AuditLog` would clash with `DotMarc.Audit.AuditLog`)
- Modify: `src/DotMarc/Program.cs` (the `/audit/export` endpoint)
- Modify: `src/DotMarc/Components/Layout/MainLayout.razor` (menu item)

**Interfaces:**
- Consumes: Task 10's `AuditFilter`, `AuditQuery`, `AuditCsv`, `Permission.AuditView`; Task 8's `AuditRecorder`;
  the shared `SearchableSelect` and `SelectOption` components (`DotMarc.Components.Shared`); Task 12's
  `AuditRetentionPanel` is added to this page in Task 12.

- [ ] **Step 1: Add the export endpoint**

In `src/DotMarc/Program.cs`, after the `app.MapGet("/.well-known/mta-sts.txt", ...)` endpoint, add:

```csharp
// Streams the rows the Audit log page's filters match. The filter travels in the query string, and the export is
// itself recorded, so who took a copy of the log is part of the log.
app.MapGet("/audit/export", async (HttpContext httpContext, IDbContextFactory<DotMarcDbContext> dbContextFactory, DotMarc.Audit.AuditRecorder auditRecorder) =>
{
    var filter = DotMarc.Audit.AuditFilter.FromQuery(httpContext.Request.Query);
    await auditRecorder.RecordAsync(DotMarc.Audit.AuditLog.Create(DotMarc.Audit.AuditActor.FromPrincipal(httpContext.User),
        DotMarc.Audit.AuditEntryKind.Change, DotMarc.Audit.AuditActions.AuditExported, null, $"Exported the audit log ({filter.Describe()})"));

    httpContext.Response.ContentType = "text/csv; charset=utf-8";
    httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"dotmarc-audit-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv\"";

    await using var context = await dbContextFactory.CreateDbContextAsync(httpContext.RequestAborted);
    var entries = DotMarc.Audit.AuditQuery.Apply(context.AuditEntries.AsNoTracking(), filter)
        .OrderByDescending(entry => entry.OccurredUtc)
        .ThenByDescending(entry => entry.Id)
        .AsAsyncEnumerable();

    // A byte order mark so Excel reads the UTF-8 correctly.
    await using var writer = new StreamWriter(httpContext.Response.Body, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    await DotMarc.Audit.AuditCsv.WriteAsync(writer, entries, httpContext.RequestAborted);
}).RequireAuthorization(nameof(Permission.AuditView));
```

- [ ] **Step 2: Add the page**

Create `src/DotMarc/Components/Pages/AuditLogPage.razor`:

```razor
@page "/audit"
@attribute [Authorize(Policy = "AuditView")]
@using DotMarc.Components.Shared
@using DotMarc.Data
@using Microsoft.AspNetCore.Authorization
@using Microsoft.EntityFrameworkCore
@inject IDbContextFactory<DotMarcDbContext> DbFactory

<PageTitle>dotMARC - Audit log</PageTitle>
<div class="d-flex align-center mb-1">
    <MudText Typo="Typo.h4">Audit log</MudText>
    <DocsLink Href="https://dotmarc.app/docs/audit-log" Text="What the audit log records" Class="ml-2" />
</div>
<MudText Typo="Typo.body2" Class="mb-4 mud-text-secondary">
    Who changed what, who signed in, and which pages they opened. Entries can't be edited or deleted. They leave
    only once they pass the retention period.
</MudText>

<MudGrid Class="mb-2">
    <MudItem xs="12" sm="4">
        <MudDatePicker Label="From (UTC)" Date="@ToDateTime(_filter.From)" DateChanged="@(date => UpdateFilterAsync(_filter with { From = ToDateOnly(date) }))" Clearable="true" />
    </MudItem>
    <MudItem xs="12" sm="4">
        <MudDatePicker Label="To (UTC)" Date="@ToDateTime(_filter.To)" DateChanged="@(date => UpdateFilterAsync(_filter with { To = ToDateOnly(date) }))" Clearable="true" />
    </MudItem>
    <MudItem xs="12" sm="4" Class="d-flex align-center">
        <MudSwitch T="bool" Value="_filter.IncludePageViews" ValueChanged="@(include => UpdateFilterAsync(_filter with { IncludePageViews = include }))"
                   Label="Show page views" Color="Color.Primary" />
    </MudItem>
</MudGrid>

<MudTable @ref="_table" T="AuditEntry" ServerData="LoadPageAsync" Hover="true" Dense="true" CustomHeader="true" RowsPerPage="50"
          OnRowClick="@((TableRowClickEventArgs<AuditEntry> args) => ToggleExpanded(args.Item))">
    <ToolBarContent>
        <MudSpacer />
        <MudButton Variant="Variant.Outlined" Color="Color.Primary" Size="Size.Small" StartIcon="@Icons.Material.Filled.Download" Class="mr-2"
                   Href="@($"/audit/export?{_filter.ToQueryString()}")">Export CSV</MudButton>
        <MudButton Variant="@(_showFilters ? Variant.Filled : Variant.Outlined)" Color="Color.Primary" Size="Size.Small" StartIcon="@Icons.Material.Filled.FilterList"
                   OnClick="@(() => _showFilters = !_showFilters)">@(_showFilters ? "Hide filters" : "Filters")</MudButton>
    </ToolBarContent>
    <HeaderContent>
        <tr>
            <MudTh>Time (UTC)</MudTh>
            <MudTh>Kind</MudTh>
            <MudTh>Who</MudTh>
            <MudTh>Action</MudTh>
            <MudTh>Target</MudTh>
            <MudTh>Summary</MudTh>
        </tr>
        @if (_showFilters)
        {
        <tr>
            <MudTh></MudTh>
            <MudTh>
                <MudSelect T="AuditEntryKind?" Value="_filter.Kind" ValueChanged="@(kind => UpdateFilterAsync(_filter with { Kind = kind }))" Clearable="true" Placeholder="Any" Margin="Margin.Dense">
                    <MudSelectItem T="AuditEntryKind?" Value="AuditEntryKind.Change">Changes</MudSelectItem>
                    <MudSelectItem T="AuditEntryKind?" Value="AuditEntryKind.SignIn">Sign-ins</MudSelectItem>
                    <MudSelectItem T="AuditEntryKind?" Value="AuditEntryKind.PageView">Page views</MudSelectItem>
                </MudSelect>
            </MudTh>
            <MudTh><MudTextField T="string" Value="_filter.Who" ValueChanged="@(text => UpdateFilterAsync(_filter with { Who = text }))" Placeholder="Search" DebounceInterval="400" Adornment="Adornment.Start" AdornmentIcon="@Icons.Material.Filled.Search" Margin="Margin.Dense" /></MudTh>
            <MudTh><SearchableSelect TValue="string" Options="ActionOptions" Value="_filter.Action" ValueChanged="@(action => UpdateFilterAsync(_filter with { Action = action }))" Clearable="true" Placeholder="Any" Margin="Margin.Dense" /></MudTh>
            <MudTh><MudTextField T="string" Value="_filter.Target" ValueChanged="@(text => UpdateFilterAsync(_filter with { Target = text }))" Placeholder="Search" DebounceInterval="400" Margin="Margin.Dense" /></MudTh>
            <MudTh><MudTextField T="string" Value="_filter.Summary" ValueChanged="@(text => UpdateFilterAsync(_filter with { Summary = text }))" Placeholder="Search" DebounceInterval="400" Margin="Margin.Dense" /></MudTh>
        </tr>
        }
    </HeaderContent>
    <RowTemplate>
        <MudTd Style="white-space:nowrap">@context.OccurredUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss")</MudTd>
        <MudTd><MudChip T="string" Size="Size.Small" Color="@KindColor(context.Kind)">@KindLabel(context.Kind)</MudChip></MudTd>
        <MudTd>@context.ActorName</MudTd>
        <MudTd><MudText Typo="Typo.caption">@context.Action</MudText></MudTd>
        <MudTd>@context.TargetName</MudTd>
        <MudTd Style="cursor:@(context.Changes.Count > 0 ? "pointer" : "default")">
            @context.Summary
            @if (context.Changes.Count > 0)
            {
                <MudIcon Icon="@(_expandedEntryId == context.Id ? Icons.Material.Filled.ExpandLess : Icons.Material.Filled.ExpandMore)" Size="Size.Small" Class="ml-1" Style="vertical-align:middle" />
            }
        </MudTd>
    </RowTemplate>
    <ChildRowContent>
        @if (_expandedEntryId == context.Id && context.Changes.Count > 0)
        {
            <MudTr>
                <td colspan="6" class="px-6 pb-4">
                    <MudSimpleTable Dense="true" Elevation="0">
                        <thead><tr><th>Field</th><th>Before</th><th>After</th></tr></thead>
                        <tbody>
                            @foreach (var change in context.Changes)
                            {
                                <tr>
                                    <td>@change.Field</td>
                                    @if (change.Secret)
                                    {
                                        <td colspan="2" class="mud-text-secondary">Changed (the value isn't recorded)</td>
                                    }
                                    else
                                    {
                                        <td>@(change.Old ?? "(none)")</td>
                                        <td>@(change.New ?? "(none)")</td>
                                    }
                                </tr>
                            }
                        </tbody>
                    </MudSimpleTable>
                </td>
            </MudTr>
        }
    </ChildRowContent>
    <NoRecordsContent>
        <MudText>No entries match these filters.</MudText>
    </NoRecordsContent>
    <PagerContent>
        <MudTablePager PageSizeOptions="@(new[] { 25, 50, 100 })" />
    </PagerContent>
</MudTable>

@code {
    private static readonly IReadOnlyList<SelectOption<string>> ActionOptions =
        AuditActions.All.Select(action => new SelectOption<string>(action.Action, action.Label)).ToList();

    private MudTable<AuditEntry>? _table;
    private AuditFilter _filter = new() { From = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)) };
    private bool _showFilters;
    private long? _expandedEntryId;

    private async Task<TableData<AuditEntry>> LoadPageAsync(TableState state, CancellationToken cancellationToken)
    {
        await using var context = await DbFactory.CreateDbContextAsync(cancellationToken);
        var query = AuditQuery.Apply(context.AuditEntries.AsNoTracking(), _filter);
        var total = await query.CountAsync(cancellationToken);
        var entries = await query
            .OrderByDescending(entry => entry.OccurredUtc)
            .ThenByDescending(entry => entry.Id)
            .Skip(state.Page * state.PageSize)
            .Take(state.PageSize)
            .ToListAsync(cancellationToken);
        return new TableData<AuditEntry> { TotalItems = total, Items = entries };
    }

    private async Task UpdateFilterAsync(AuditFilter filter)
    {
        _filter = filter;
        _expandedEntryId = null;
        if (_table is not null)
        {
            await _table.ReloadServerData();
        }
    }

    private void ToggleExpanded(AuditEntry entry) => _expandedEntryId = _expandedEntryId == entry.Id ? null : entry.Id;

    private static DateTime? ToDateTime(DateOnly? date) => date?.ToDateTime(TimeOnly.MinValue);

    private static DateOnly? ToDateOnly(DateTime? date) => date is { } value ? DateOnly.FromDateTime(value) : null;

    private static string KindLabel(AuditEntryKind kind) => kind switch
    {
        AuditEntryKind.Change => "Change",
        AuditEntryKind.SignIn => "Sign-in",
        _ => "Page view"
    };

    private static Color KindColor(AuditEntryKind kind) => kind switch
    {
        AuditEntryKind.Change => Color.Primary,
        AuditEntryKind.SignIn => Color.Info,
        _ => Color.Default
    };
}
```

If the build reports a different `ServerData` signature for MudBlazor 9.8, match the one it names (the
`TableState` plus `CancellationToken` form is current); the body is unchanged.

- [ ] **Step 3: Add the menu item**

In `src/DotMarc/Components/Layout/MainLayout.razor`, after the Server logs `AuthorizeView` in the Manage menu, add:

```razor
            <AuthorizeView Policy="AuditView">
                <MudMenuItem Href="/audit" Icon="@Icons.Material.Filled.History">Audit log</MudMenuItem>
            </AuthorizeView>
```

- [ ] **Step 4: Build and try it on the demo**

Run: `dotnet build src/DotMarc/DotMarc.csproj -nologo -v q` → `Build succeeded.`

Start the demo (`$env:Demo__Enabled='true'; $env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/DotMarc --no-build --no-launch-profile --urls http://localhost:5195`),
sign in as Demo Admin at `/demo`, rename a group on Manage groups, then open **Manage > Audit log** and check:
- The sign-in and the rename are listed, newest first; page views are not.
- Clicking the rename row shows Name, Client A, Client B.
- **Show page views** adds the pages you opened.
- Filters: Who "admin", and Action "Group renamed", each narrow the list.
- **Export CSV** downloads a file with the same rows, and a new `audit.exported` entry appears.

Stop the app when done.

- [ ] **Step 5: Commit**

```powershell
git add src/DotMarc/Program.cs src/DotMarc/Components/Pages/AuditLogPage.razor src/DotMarc/Components/Layout/MainLayout.razor
git commit -m "Add the Audit log page and CSV export"
```

---

### Task 12: Retention

**Files:**
- Create: `src/DotMarc/Audit/AuditSettingsService.cs`, `src/DotMarc/Audit/AuditRetention.cs`, `src/DotMarc/Audit/AuditRetentionService.cs`
- Create: `src/DotMarc/Components/Shared/AuditRetentionPanel.razor`
- Modify: `src/DotMarc/Components/Pages/AuditLogPage.razor` (show the panel), `src/DotMarc/Program.cs` (register the
  background service), `test/DotMarc.Tests/Audit/AuditCoverageTests.cs` (add `AuditSettingsService`)
- Test: `test/DotMarc.Tests/Audit/AuditSettingsServiceTests.cs`, `test/DotMarc.Tests/Audit/AuditRetentionTests.cs`

**Interfaces:**
- Consumes: Task 1's `AuditSettings`; Task 2's building blocks.
- Produces: `AuditSettingsService.GetAsync(context, cancellationToken)`, `AuditSettingsService.SaveAsync(context, actor, updated, cancellationToken)`
  (throws `ArgumentOutOfRangeException` outside 1 to 3650);
  `AuditRetention.PurgeAsync(context, nowUtc, cancellationToken)` returning the number deleted.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Audit/AuditSettingsServiceTests.cs` with the same Postgres boilerplate as
`AuditLogTests` (class name `AuditSettingsServiceTests`), and these tests:

```csharp
    [Fact]
    public async Task SaveAsync_RecordsTheOldAndNewPeriods()
    {
        await using (var context = CreateContext())
        {
            await AuditSettingsService.SaveAsync(context, TestActors.Admin, new AuditSettings { ChangeRetentionDays = null, SignInRetentionDays = 365, PageViewRetentionDays = 30 });
        }

        await using var verify = CreateContext();
        var settings = await verify.AuditSettings.SingleAsync();
        Assert.Equal((null, 365, 30), (settings.ChangeRetentionDays, settings.SignInRetentionDays, settings.PageViewRetentionDays));
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.AuditSettingsSaved, entry.Action);
        Assert.Equal(
            [new AuditFieldChange("Keep changes for", "365 days", "Keep forever"), new AuditFieldChange("Keep page views for", "90 days", "30 days")],
            entry.Changes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task SaveAsync_RefusesAPeriodOutOfRange_AndChangesNothing(int days)
    {
        await using (var context = CreateContext())
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                AuditSettingsService.SaveAsync(context, TestActors.Admin, new AuditSettings { ChangeRetentionDays = days, SignInRetentionDays = 365, PageViewRetentionDays = 90 }));
        }

        await using var verify = CreateContext();
        Assert.Equal(365, (await verify.AuditSettings.SingleAsync()).ChangeRetentionDays);
        Assert.Empty(verify.AuditEntries);
    }
```

Create `test/DotMarc.Tests/Audit/AuditRetentionTests.cs` with the same boilerplate (class name `AuditRetentionTests`):

```csharp
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static AuditEntry EntryAged(AuditEntryKind kind, int daysOld) => new()
    {
        Kind = kind, ActorName = "Someone", Action = "test.action", Summary = $"{kind} {daysOld} days old", OccurredUtc = Now.AddDays(-daysOld)
    };

    [Fact]
    public async Task PurgeAsync_RemovesEachKindAfterItsOwnPeriod()
    {
        await using (var context = CreateContext())
        {
            context.AuditEntries.AddRange(
                EntryAged(AuditEntryKind.PageView, 89), EntryAged(AuditEntryKind.PageView, 91),
                EntryAged(AuditEntryKind.SignIn, 364), EntryAged(AuditEntryKind.SignIn, 366),
                EntryAged(AuditEntryKind.Change, 366));
            await context.SaveChangesAsync();

            var deleted = await AuditRetention.PurgeAsync(context, Now);

            Assert.Equal(3, deleted);
        }

        await using var verify = CreateContext();
        Assert.Equal(["PageView 89 days old", "SignIn 364 days old"], await verify.AuditEntries.OrderBy(entry => entry.Summary).Select(entry => entry.Summary).ToListAsync());
    }

    [Fact]
    public async Task PurgeAsync_KeepsEverything_ForAKindKeptForever()
    {
        await using (var context = CreateContext())
        {
            var settings = await context.AuditSettings.SingleAsync();
            settings.ChangeRetentionDays = null;
            context.AuditEntries.Add(EntryAged(AuditEntryKind.Change, 5000));
            await context.SaveChangesAsync();

            await AuditRetention.PurgeAsync(context, Now);
        }

        await using var verify = CreateContext();
        Assert.Single(verify.AuditEntries);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AuditSettingsServiceTests|FullyQualifiedName~AuditRetentionTests" -nologo -v q`
Expected: build FAILS, `AuditSettingsService` and `AuditRetention` not found.

- [ ] **Step 3: Write the settings service and the purge**

Create `src/DotMarc/Audit/AuditSettingsService.cs`:

```csharp
using System.Globalization;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Audit;

/// <summary>Reads and saves the audit retention periods. Follows NotificationSettingsService's pattern.</summary>
public static class AuditSettingsService
{
    public static Task<AuditSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.AuditSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, AuditSettings updated, CancellationToken cancellationToken = default)
    {
        Validate(updated.ChangeRetentionDays, "Changes");
        Validate(updated.SignInRetentionDays, "Sign-ins");
        Validate(updated.PageViewRetentionDays, "Page views");

        var saved = await context.AuditSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Keep changes for", Describe(saved.ChangeRetentionDays), Describe(updated.ChangeRetentionDays))
            .Field("Keep sign-ins for", Describe(saved.SignInRetentionDays), Describe(updated.SignInRetentionDays))
            .Field("Keep page views for", Describe(saved.PageViewRetentionDays), Describe(updated.PageViewRetentionDays));
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.AuditSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.ChangeRetentionDays = updated.ChangeRetentionDays;
        existing.SignInRetentionDays = updated.SignInRetentionDays;
        existing.PageViewRetentionDays = updated.PageViewRetentionDays;

        AuditLog.Record(context, actor, AuditActions.AuditSettingsSaved, AuditTarget.Settings("Audit log retention"), "Changed how long audit entries are kept", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(int? days) => days is { } count
        ? $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "day" : "days")}"
        : "Keep forever";

    private static void Validate(int? days, string kindName)
    {
        if (days is { } count && (count < AuditSettings.MinimumRetentionDays || count > AuditSettings.MaximumRetentionDays))
        {
            throw new ArgumentOutOfRangeException(nameof(days), count,
                $"{kindName} must be kept for between {AuditSettings.MinimumRetentionDays} and {AuditSettings.MaximumRetentionDays} days, or forever.");
        }
    }
}
```

Create `src/DotMarc/Audit/AuditRetention.cs`:

```csharp
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Audit;

/// <summary>Deletes entries past their kind's retention period.</summary>
public static class AuditRetention
{
    /// <summary>Deleted a batch at a time so the first cleanup of a large log doesn't hold one long transaction.</summary>
    public const int BatchSize = 5000;

    public static async Task<int> PurgeAsync(DotMarcDbContext context, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var settings = await context.AuditSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        deleted += await PurgeKindAsync(context, AuditEntryKind.Change, settings.ChangeRetentionDays, nowUtc, cancellationToken).ConfigureAwait(false);
        deleted += await PurgeKindAsync(context, AuditEntryKind.SignIn, settings.SignInRetentionDays, nowUtc, cancellationToken).ConfigureAwait(false);
        deleted += await PurgeKindAsync(context, AuditEntryKind.PageView, settings.PageViewRetentionDays, nowUtc, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private static async Task<int> PurgeKindAsync(DotMarcDbContext context, AuditEntryKind kind, int? retentionDays, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (retentionDays is not { } days)
        {
            return 0;
        }

        var cutoff = nowUtc.AddDays(-days);
        var deleted = 0;
        while (true)
        {
            var expiredIds = await context.AuditEntries
                .Where(entry => entry.Kind == kind && entry.OccurredUtc < cutoff)
                .OrderBy(entry => entry.Id)
                .Select(entry => entry.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (expiredIds.Count == 0)
            {
                return deleted;
            }

            deleted += await context.AuditEntries.Where(entry => expiredIds.Contains(entry.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AuditSettingsServiceTests|FullyQualifiedName~AuditRetentionTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 5: Run the purge daily**

Create `src/DotMarc/Audit/AuditRetentionService.cs`:

```csharp
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotMarc.Audit;

/// <summary>Runs <see cref="AuditRetention.PurgeAsync"/> a few minutes after startup and then once a day. A failed
/// run is logged and tried again the next day. Safe with several instances running: they delete the same rows.</summary>
public sealed class AuditRetentionService(IDbContextFactory<DotMarcDbContext> dbContextFactory, ILogger<AuditRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await PurgeOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task PurgeOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);
            var deleted = await AuditRetention.PurgeAsync(context, DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            if (deleted > 0)
            {
                logger.LogInformation("Removed {Count} audit entries past their retention period", deleted);
            }
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Audit retention cleanup failed; it will try again tomorrow");
        }
    }
}
```

In `src/DotMarc/Program.cs`, directly after `builder.Services.AddHostedService<PinnedDomainHealthMonitor>();` add:

```csharp
builder.Services.AddHostedService<DotMarc.Audit.AuditRetentionService>();
```

- [ ] **Step 6: Add the retention panel**

Create `src/DotMarc/Components/Shared/AuditRetentionPanel.razor`:

```razor
@using DotMarc.Data
@using Microsoft.EntityFrameworkCore
@inject IDbContextFactory<DotMarcDbContext> DbFactory
@inject AuditActorAccessor AuditActorAccessor
@inject ISnackbar Snackbar

<MudPaper Class="pa-4 mb-4" Elevation="1">
    <MudText Typo="Typo.h6">Retention</MudText>
    <MudText Typo="Typo.body2" Class="mb-3 mud-text-secondary">
        How long each kind of entry is kept, in days. Leave a box empty to keep that kind forever. Entries older than
        this are deleted once a day.
    </MudText>
    @if (_settings is null)
    {
        <MudProgressCircular Indeterminate="true" Size="Size.Small" />
    }
    else
    {
        <MudGrid>
            <MudItem xs="12" sm="4">
                <MudNumericField T="int?" @bind-Value="_settings.ChangeRetentionDays" Label="Changes" Placeholder="Keep forever" Variant="Variant.Outlined"
                                 Min="AuditSettings.MinimumRetentionDays" Max="AuditSettings.MaximumRetentionDays" />
            </MudItem>
            <MudItem xs="12" sm="4">
                <MudNumericField T="int?" @bind-Value="_settings.SignInRetentionDays" Label="Sign-ins" Placeholder="Keep forever" Variant="Variant.Outlined"
                                 Min="AuditSettings.MinimumRetentionDays" Max="AuditSettings.MaximumRetentionDays" />
            </MudItem>
            <MudItem xs="12" sm="4">
                <MudNumericField T="int?" @bind-Value="_settings.PageViewRetentionDays" Label="Page views" Placeholder="Keep forever" Variant="Variant.Outlined"
                                 Min="AuditSettings.MinimumRetentionDays" Max="AuditSettings.MaximumRetentionDays" />
            </MudItem>
        </MudGrid>
        <div class="d-flex justify-end mt-3">
            <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Save" OnClick="SaveAsync">Save retention</MudButton>
        </div>
    }
</MudPaper>

@code {
    private AuditSettings? _settings;

    protected override async Task OnInitializedAsync()
    {
        await using var context = await DbFactory.CreateDbContextAsync();
        _settings = await context.AuditSettings.AsNoTracking().SingleAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            await using var context = await DbFactory.CreateDbContextAsync();
            await AuditSettingsService.SaveAsync(context, await AuditActorAccessor.GetAsync(), _settings!);
            Snackbar.Add("Retention saved.", Severity.Success);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            Snackbar.Add(exception.Message.Split(" (Parameter")[0], Severity.Error);
        }
        catch (Exception)
        {
            Snackbar.Add("Couldn't save retention. Try again.", Severity.Error);
        }
    }
}
```

In `src/DotMarc/Components/Pages/AuditLogPage.razor`, directly after the intro `MudText`, add:

```razor
<AuthorizeView Policy="AuditManage">
    <AuditRetentionPanel />
</AuthorizeView>
```

- [ ] **Step 7: Cover the new service in the coverage test**

In `test/DotMarc.Tests/Audit/AuditCoverageTests.cs`, add `typeof(AuditSettingsService)` to `AuditedServices` and raise
the minimum from 29 to 30.

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Audit" -nologo -v q`
Expected: PASS.

- [ ] **Step 8: Commit**

```powershell
git add src/DotMarc/Audit src/DotMarc/Program.cs src/DotMarc/Components test/DotMarc.Tests/Audit
git commit -m "Add audit retention settings and daily cleanup"
```

---

### Task 13: Demo reset, docs, roadmap and final check

**Files:**
- Modify: `src/DotMarc/Demo/DemoDataSeeder.cs` (truncate `AuditEntries`)
- Create: `website/docs/audit-log.mdx`
- Modify: `website/docs/permissions-and-access.mdx`, `website/sidebars.ts`
- Modify: `website/scripts/canny-roadmap.json` (mark the idea complete)

- [ ] **Step 1: Clear the log on the demo reset**

In `src/DotMarc/Demo/DemoDataSeeder.cs`, add `"AuditEntries"` to the table list in `TruncateAllTablesAsync`, after
`"TlsrptFailureDetails"`. Leave `AuditSettings` out: its seeded row must survive, like `NotificationSettings`.

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DemoDataSeederTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 2: Write the docs page**

Create `website/docs/audit-log.mdx`:

```mdx
---
description: What dotMARC's audit log records, who can see it, how to filter and export it, and how long entries are kept.
---

# Audit log

The audit log answers "who changed this?", "who has been signing in?" and "who looked at what?". Open it from
**Manage > Audit log**.

## What it records

* **Changes.** Domains, groups and tags added, changed or removed; roles and access grants; alert, HaloPSA and DNS
  provider settings; ticket rules; DNS records pushed to your provider; running the HaloPSA integration test; and
  exporting the log itself. Each entry says who did it, when, what it was about, and the old and new value of
  every field that changed.
* **Sign-ins.** Every successful sign-in, and every sign-in refused because the person has no access grant, with
  the email they tried.
* **Page views.** Which pages each person opened, such as a domain's Sources tab. These are hidden unless you
  switch on **Show page views**.

Secrets are never recorded. When a client secret, webhook secret or webhook URL changes, the entry says it changed,
without the value.

Attempts that dotMARC refused, such as adding a domain that already exists, aren't recorded. Reports arriving and
alerts being raised aren't recorded either: they're the data dotMARC monitors, and have their own pages.

The log starts when you upgrade to the version that added it. Nothing from before then can be reconstructed.

## Who can see it

Reading and exporting the log needs the `AuditView` permission. Changing how long entries are kept needs
`AuditManage`, so someone can review the log without being able to shorten it. Both are part of the built-in Admin
role, and can be added to a custom role from **Manage access**. See [Permissions and access](/docs/permissions-and-access).

Nothing in dotMARC edits or deletes an entry. Entries leave only when they pass their retention period.

## Filter and export

The date pickers choose the range, in UTC, and default to the last 30 days. **Filters** opens a row for kind, who
(name or email), action, target and summary. Click an entry to see each field it changed.

**Export CSV** downloads exactly what the filters match. The export is itself recorded.

## Retention

Changes and sign-ins are kept for a year, and page views for 90 days, by default. Change these on the Audit log
page: each is between 1 and 3,650 days, or empty to keep forever. Expired entries are deleted once a day.
```

- [ ] **Step 3: Update the permissions page and sidebar**

In `website/docs/permissions-and-access.mdx`, find the sentence listing independently grantable permissions (it
mentions `LogsView` on line 28) and add `AuditView` and `AuditManage` to it, matching its wording, with a link:
"(`AuditView` and `AuditManage`, see [Audit log](/docs/audit-log))".

In `website/sidebars.ts`, change `items: ['permissions-and-access', 'updating', 'server-logs'],` to
`items: ['permissions-and-access', 'updating', 'server-logs', 'audit-log'],`.

- [ ] **Step 4: Mark the roadmap idea complete**

In `website/scripts/canny-roadmap.json`, on the entry titled
`"Audit logging for all changes / configuration alterations and access."`, change `"status": "planned"` to
`"status": "complete"`. (It stays "Target release" on Canny until v0.8.0 is released; the release job then marks it
complete and links it. See `website/docs/releasing.mdx`.)

- [ ] **Step 5: Run everything**

Run: `dotnet test test/DotMarc.Tests -nologo -v q`
Expected: PASS, with no failures.

Run: `cd website; yarn build; cd ..`
Expected: the site builds with no broken links.

- [ ] **Step 6: Check it end to end on the demo**

Start the demo as in Task 11 Step 4, then:
- Sign in as Demo Admin, rename a group, grant access to someone, and save alert settings with a new Teams webhook URL.
- On **Manage > Audit log**, each shows with the right who, target and field changes, and the webhook URL shows as
  "Changed (the value isn't recorded)".
- Switch persona to Demo Viewer: **Manage > Audit log** isn't in the menu, and `/audit` is refused.
- Switch back to Demo Admin: the Viewer's sign-in is listed.
- Set page views to 1 day and save: an `Audit retention changed` entry appears.

Stop the app.

- [ ] **Step 7: Commit**

```powershell
git add src/DotMarc/Demo/DemoDataSeeder.cs website/docs/audit-log.mdx website/docs/permissions-and-access.mdx website/sidebars.ts website/scripts/canny-roadmap.json
git commit -m "Document the audit log and clear it on the demo reset"
```
