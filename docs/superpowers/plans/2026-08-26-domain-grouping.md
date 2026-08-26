# Domain Grouping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a domain belong to any number of user-defined Groups and any number of curated, colored Tags, with assignment on Manage Domains and two independent filter dropdowns on the Dashboard.

**Architecture:** Two new entities (`Group`, `Tag`) in an EF Core implicit many-to-many with `Domain` (no hand-written join entities). Two small static management services follow this project's `DomainManagementService` convention. A new `/groups` page owns creating/renaming/deleting the curated lists; `ManageDomains.razor` gets multi-select columns for assignment (pure configuration, no status); `Dashboard.razor` gets two filter dropdowns that narrow the already-loaded domain list before `DashboardSummary.Build` runs, so summary tiles reflect the filtered set too.

**Tech Stack:** ASP.NET Core Blazor Server, MudBlazor 9.8.0, EF Core + Npgsql, xUnit + Testcontainers.PostgreSql (existing stack — no new dependency).

**Spec:** `docs/superpowers/specs/2026-08-26-domain-grouping-design.md`

## Global Constraints

- A domain can belong to any number of Groups and any number of Tags
  (many-to-many both ways) — not exactly one.
- Groups and Tags are each a curated list (created/renamed/deleted on the
  new `/groups` page), never free text typed inline on Manage Domains.
- Tag color is the `MudBlazor.Color` enum, stored via `HasConversion<string>()`
  (same pattern as `Domain.DmarcCheckStatus`). Only these five values are
  offered: `Primary`, `Secondary`, `Tertiary`, `Info`, `Dark` — deliberately
  excluding `Success`/`Warning`/`Error`, which already carry pass/fail/status
  meaning on the Dashboard's Report Status and DNS Status chips.
- `Group.Name` and `Tag.Name` uniqueness is case-insensitive at the
  application level (an `AnyAsync` pre-check comparing `.ToLower()`), backed
  by a plain (case-sensitive) unique DB index as a race guard. This does not
  fully guarantee a same-instant "Client A" vs "client a" race is caught —
  an accepted gap, since group/tag creation is a low-frequency manual action,
  not the high-concurrency path Domain auto-discovery is.
- Manage Domains shows Groups/Tags assignment only — no status information
  of any kind, per this project's standing rule that Manage Domains is
  configuration-only.
- Deleting a Group or Tag removes only its membership rows (EF's default
  many-to-many cascade) — the Domain and its Reports are never touched. The
  confirm dialog states how many domains are currently members.
- The Dashboard's Group and Tag filters are independent and combine with
  AND when both are set. Filtering happens on the loaded `List<Domain>`
  before `DashboardSummary.Build` runs — `Build`'s signature does not
  change, and its own tests are untouched by this plan.
- Non-goals for this plan (do not build): tag-derived "smart groups" with
  computed membership; any permissions/access-control; bulk group/tag
  assignment across multiple domains at once; a color on `Group`; any
  linkage to Entra ID groups.

---

### Task 1: `Group` and `Tag` entities

**Files:**
- Create: `src/DotMarc/Data/Group.cs`
- Create: `src/DotMarc/Data/Tag.cs`
- Modify: `src/DotMarc/Data/Domain.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Test: `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`
- (generated) `src/DotMarc/Migrations/`

**Interfaces:**
- Produces: `DotMarc.Data.Group { int Id, string Name, List<Domain> Domains }`,
  `DotMarc.Data.Tag { int Id, string Name, Color Color, List<Domain> Domains }`,
  and `Domain.Groups`/`Domain.Tags` (`List<Group>`/`List<Tag>`) — used by
  every later task in this plan.

- [ ] **Step 1: Write the failing tests**

Add to `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`, inside the
existing `DotMarcDbContextTests` class (add `using MudBlazor;` to the file's
existing `using` block if not already present):

```csharp
    [Fact]
    public void CanInsertAndQuery_GroupWithMemberDomain()
    {
        using (var context = CreateContext())
        {
            var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
            var group = new Group { Name = "Client A" };
            group.Domains.Add(domain);
            context.Groups.Add(group);
            context.SaveChanges();
        }

        using var verify = CreateContext();
        var savedGroup = verify.Groups.Include(g => g.Domains).Single();
        Assert.Equal("Client A", savedGroup.Name);
        Assert.Single(savedGroup.Domains);
        Assert.Equal("contoso.io", savedGroup.Domains[0].Name);
    }

    [Fact]
    public void Group_Name_MustBeUnique()
    {
        using var context = CreateContext();
        context.Groups.Add(new Group { Name = "Client A" });
        context.SaveChanges();

        context.Groups.Add(new Group { Name = "Client A" });
        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void CanInsertAndQuery_TagWithColorAndMemberDomain()
    {
        using (var context = CreateContext())
        {
            var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
            var tag = new Tag { Name = "primary", Color = Color.Info };
            tag.Domains.Add(domain);
            context.Tags.Add(tag);
            context.SaveChanges();
        }

        using var verify = CreateContext();
        var savedTag = verify.Tags.Include(t => t.Domains).Single();
        Assert.Equal("primary", savedTag.Name);
        Assert.Equal(Color.Info, savedTag.Color);
        Assert.Single(savedTag.Domains);
    }

    [Fact]
    public void Tag_Name_MustBeUnique()
    {
        using var context = CreateContext();
        context.Tags.Add(new Tag { Name = "primary", Color = Color.Primary });
        context.SaveChanges();

        context.Tags.Add(new Tag { Name = "primary", Color = Color.Secondary });
        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void Domain_CanBelongToMultipleGroupsAndTags()
    {
        using (var context = CreateContext())
        {
            var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
            domain.Groups.Add(new Group { Name = "Client A" });
            domain.Groups.Add(new Group { Name = "Project X" });
            domain.Tags.Add(new Tag { Name = "primary", Color = Color.Primary });
            context.Domains.Add(domain);
            context.SaveChanges();
        }

        using var verify = CreateContext();
        var savedDomain = verify.Domains.Include(d => d.Groups).Include(d => d.Tags).Single();
        Assert.Equal(2, savedDomain.Groups.Count);
        Assert.Single(savedDomain.Tags);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "CanInsertAndQuery_GroupWithMemberDomain|Group_Name_MustBeUnique|CanInsertAndQuery_TagWithColorAndMemberDomain|Tag_Name_MustBeUnique|Domain_CanBelongToMultipleGroupsAndTags"`
Expected: FAIL to build — `Group`, `Tag`, `Domain.Groups`, `Domain.Tags` don't exist yet.

- [ ] **Step 3: Create the entities**

Create `src/DotMarc/Data/Group.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>A user-defined container a domain can belong to — typically a client/owner in the
/// MSP use case this app is designed for, though a domain can belong to more than one Group at
/// once. Carries no access-control meaning on its own; that's the subject of a later design
/// cycle, not this one.</summary>
public sealed class Group
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<Domain> Domains { get; set; } = [];
}
```

Create `src/DotMarc/Data/Tag.cs`:

```csharp
using MudBlazor;

namespace DotMarc.Data;

/// <summary>A curated, colored label a domain can carry (e.g. "primary") — many-to-many, used
/// for filtering on the Dashboard rather than ownership. Unlike Group, a Tag never implies
/// access to anything.</summary>
public sealed class Tag
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required Color Color { get; set; }
    public List<Domain> Domains { get; set; } = [];
}
```

- [ ] **Step 4: Add navigation properties to `Domain`**

In `src/DotMarc/Data/Domain.cs`, add two properties alongside the existing
`Reports` property:

```csharp
    public List<Group> Groups { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
```

- [ ] **Step 5: Register the DbSets and configure the model**

In `src/DotMarc/Data/DotMarcDbContext.cs`, add two DbSet properties
alongside the existing ones:

```csharp
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Tag> Tags => Set<Tag>();
```

In `OnModelCreating`, add (implicit many-to-many skip navigations need no
explicit join-entity configuration — EF Core infers the join tables
`GroupDomain`/`DomainTag` from `Domain.Groups`/`Group.Domains` and
`Domain.Tags`/`Tag.Domains` automatically):

```csharp
        modelBuilder.Entity<Group>(entity =>
        {
            entity.HasIndex(g => g.Name).IsUnique();
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasIndex(t => t.Name).IsUnique();
            entity.Property(t => t.Color).HasConversion<string>();
        });
```

- [ ] **Step 6: Generate the migration**

Run: `dotnet ef migrations add AddGroupsAndTags --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

Review the generated migration: confirm it creates four new tables total —
`Groups`, `Tags`, and the two implicit join tables EF names for the
Domain↔Group and Domain↔Tag many-to-many relationships — a unique index on
`Groups.Name` and
`Tags.Name`, and that `Tags.Color` is a `text`/`character varying` column
(not `integer`) — confirming the string conversion took effect. This is a
brand-new set of tables (nothing existing is renamed or altered), so there
is no data-loss risk to review for, unlike a column rename.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "CanInsertAndQuery_GroupWithMemberDomain|Group_Name_MustBeUnique|CanInsertAndQuery_TagWithColorAndMemberDomain|Tag_Name_MustBeUnique|Domain_CanBelongToMultipleGroupsAndTags"`
Expected: PASS (5 tests).

- [ ] **Step 8: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc/Data/Group.cs src/DotMarc/Data/Tag.cs src/DotMarc/Data/Domain.cs src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Migrations/ test/DotMarc.Tests/Data/DotMarcDbContextTests.cs
git commit -m "Add Group and Tag entities with many-to-many Domain navigation"
```

---

### Task 2: `GroupManagementService` and `TagManagementService`

**Files:**
- Create: `src/DotMarc/Data/GroupManagementService.cs`
- Create: `src/DotMarc/Data/TagManagementService.cs`
- Test: `test/DotMarc.Tests/Data/GroupManagementServiceTests.cs`
- Test: `test/DotMarc.Tests/Data/TagManagementServiceTests.cs`

**Interfaces:**
- Consumes: `Group`, `Tag`, `Domain.Groups`, `Domain.Tags` (Task 1).
- Produces:
  - `GroupManagementService.AddGroupResult` enum (`Added`, `InvalidName`, `AlreadyExists`)
  - `GroupManagementService.AddGroupAsync(DotMarcDbContext, string rawName, CancellationToken) : Task<AddGroupResult>`
  - `GroupManagementService.RenameGroupAsync(DotMarcDbContext, int groupId, string rawName, CancellationToken) : Task<AddGroupResult>`
  - `GroupManagementService.RemoveGroupAsync(DotMarcDbContext, int groupId, CancellationToken) : Task`
  - `GroupManagementService.SetDomainGroupsAsync(DotMarcDbContext, int domainId, IReadOnlyList<int> groupIds, CancellationToken) : Task`
  - `TagManagementService.AddTagResult` enum (`Added`, `InvalidName`, `AlreadyExists`)
  - `TagManagementService.AddTagAsync(DotMarcDbContext, string rawName, Color color, CancellationToken) : Task<AddTagResult>`
  - `TagManagementService.UpdateTagAsync(DotMarcDbContext, int tagId, string rawName, Color color, CancellationToken) : Task<AddTagResult>`
  - `TagManagementService.RemoveTagAsync(DotMarcDbContext, int tagId, CancellationToken) : Task`
  - `TagManagementService.SetDomainTagsAsync(DotMarcDbContext, int domainId, IReadOnlyList<int> tagIds, CancellationToken) : Task`
  All four `*Async` CRUD-shaped methods default `CancellationToken cancellationToken = default`, matching `DomainManagementService`'s existing convention. Later tasks (3, 4) call these directly.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Data/GroupManagementServiceTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class GroupManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public GroupManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task AddGroupAsync_AddsATrimmedGroup()
    {
        using var context = CreateContext();

        var result = await GroupManagementService.AddGroupAsync(context, "  Client A  ", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.Added, result);
        using var verify = CreateContext();
        Assert.Equal("Client A", verify.Groups.Single().Name);
    }

    [Fact]
    public async Task AddGroupAsync_RejectsEmptyName()
    {
        using var context = CreateContext();

        var result = await GroupManagementService.AddGroupAsync(context, "   ", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.InvalidName, result);
        Assert.Empty(context.Groups);
    }

    [Fact]
    public async Task AddGroupAsync_RejectsCaseInsensitiveDuplicate()
    {
        using var context = CreateContext();
        await GroupManagementService.AddGroupAsync(context, "Client A", CancellationToken.None);

        var result = await GroupManagementService.AddGroupAsync(context, "client a", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.AlreadyExists, result);
    }

    [Fact]
    public async Task RenameGroupAsync_RenamesTheGroup()
    {
        using var context = CreateContext();
        await GroupManagementService.AddGroupAsync(context, "Client A", CancellationToken.None);
        var groupId = context.Groups.Single().Id;

        var result = await GroupManagementService.RenameGroupAsync(context, groupId, "Client A Renamed", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.Added, result);
        using var verify = CreateContext();
        Assert.Equal("Client A Renamed", verify.Groups.Single().Name);
    }

    [Fact]
    public async Task RenameGroupAsync_RejectsRenamingToAnExistingName()
    {
        using var context = CreateContext();
        await GroupManagementService.AddGroupAsync(context, "Client A", CancellationToken.None);
        await GroupManagementService.AddGroupAsync(context, "Client B", CancellationToken.None);
        var clientBId = context.Groups.Single(g => g.Name == "Client B").Id;

        var result = await GroupManagementService.RenameGroupAsync(context, clientBId, "Client A", CancellationToken.None);

        Assert.Equal(GroupManagementService.AddGroupResult.AlreadyExists, result);
    }

    [Fact]
    public async Task RemoveGroupAsync_RemovesTheGroup_ButNotItsMemberDomain()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.io", CancellationToken.None);
        await GroupManagementService.AddGroupAsync(context, "Client A", CancellationToken.None);
        var domainId = context.Domains.Single().Id;
        var groupId = context.Groups.Single().Id;
        await GroupManagementService.SetDomainGroupsAsync(context, domainId, [groupId], CancellationToken.None);

        await GroupManagementService.RemoveGroupAsync(context, groupId, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.Groups);
        Assert.NotNull(verify.Domains.SingleOrDefault(d => d.Id == domainId));
    }

    [Fact]
    public async Task SetDomainGroupsAsync_ReplacesTheFullMembershipSet()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.io", CancellationToken.None);
        await GroupManagementService.AddGroupAsync(context, "Client A", CancellationToken.None);
        await GroupManagementService.AddGroupAsync(context, "Client B", CancellationToken.None);
        var domainId = context.Domains.Single().Id;
        var groupAId = context.Groups.Single(g => g.Name == "Client A").Id;
        var groupBId = context.Groups.Single(g => g.Name == "Client B").Id;

        await GroupManagementService.SetDomainGroupsAsync(context, domainId, [groupAId, groupBId], CancellationToken.None);
        using (var verify1 = CreateContext())
        {
            var domain = verify1.Domains.Include(d => d.Groups).Single();
            Assert.Equal(2, domain.Groups.Count);
        }

        await GroupManagementService.SetDomainGroupsAsync(context, domainId, [groupAId], CancellationToken.None);

        using var verify2 = CreateContext();
        var updated = verify2.Domains.Include(d => d.Groups).Single();
        Assert.Single(updated.Groups);
        Assert.Equal("Client A", updated.Groups[0].Name);
    }
}
```

Create `test/DotMarc.Tests/Data/TagManagementServiceTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class TagManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public TagManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task AddTagAsync_AddsATrimmedTagWithColor()
    {
        using var context = CreateContext();

        var result = await TagManagementService.AddTagAsync(context, "  primary  ", Color.Info, CancellationToken.None);

        Assert.Equal(TagManagementService.AddTagResult.Added, result);
        using var verify = CreateContext();
        var tag = verify.Tags.Single();
        Assert.Equal("primary", tag.Name);
        Assert.Equal(Color.Info, tag.Color);
    }

    [Fact]
    public async Task AddTagAsync_RejectsEmptyName()
    {
        using var context = CreateContext();

        var result = await TagManagementService.AddTagAsync(context, "   ", Color.Primary, CancellationToken.None);

        Assert.Equal(TagManagementService.AddTagResult.InvalidName, result);
        Assert.Empty(context.Tags);
    }

    [Fact]
    public async Task AddTagAsync_RejectsCaseInsensitiveDuplicate()
    {
        using var context = CreateContext();
        await TagManagementService.AddTagAsync(context, "primary", Color.Primary, CancellationToken.None);

        var result = await TagManagementService.AddTagAsync(context, "PRIMARY", Color.Info, CancellationToken.None);

        Assert.Equal(TagManagementService.AddTagResult.AlreadyExists, result);
    }

    [Fact]
    public async Task UpdateTagAsync_UpdatesNameAndColorTogether()
    {
        using var context = CreateContext();
        await TagManagementService.AddTagAsync(context, "primary", Color.Primary, CancellationToken.None);
        var tagId = context.Tags.Single().Id;

        var result = await TagManagementService.UpdateTagAsync(context, tagId, "renamed", Color.Dark, CancellationToken.None);

        Assert.Equal(TagManagementService.AddTagResult.Added, result);
        using var verify = CreateContext();
        var tag = verify.Tags.Single();
        Assert.Equal("renamed", tag.Name);
        Assert.Equal(Color.Dark, tag.Color);
    }

    [Fact]
    public async Task RemoveTagAsync_RemovesTheTag_ButNotItsMemberDomain()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.io", CancellationToken.None);
        await TagManagementService.AddTagAsync(context, "primary", Color.Primary, CancellationToken.None);
        var domainId = context.Domains.Single().Id;
        var tagId = context.Tags.Single().Id;
        await TagManagementService.SetDomainTagsAsync(context, domainId, [tagId], CancellationToken.None);

        await TagManagementService.RemoveTagAsync(context, tagId, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.Tags);
        Assert.NotNull(verify.Domains.SingleOrDefault(d => d.Id == domainId));
    }

    [Fact]
    public async Task SetDomainTagsAsync_ReplacesTheFullMembershipSet()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, "contoso.io", CancellationToken.None);
        await TagManagementService.AddTagAsync(context, "primary", Color.Primary, CancellationToken.None);
        await TagManagementService.AddTagAsync(context, "secondary", Color.Secondary, CancellationToken.None);
        var domainId = context.Domains.Single().Id;
        var primaryId = context.Tags.Single(t => t.Name == "primary").Id;
        var secondaryId = context.Tags.Single(t => t.Name == "secondary").Id;

        await TagManagementService.SetDomainTagsAsync(context, domainId, [primaryId, secondaryId], CancellationToken.None);
        using (var verify1 = CreateContext())
        {
            Assert.Equal(2, verify1.Domains.Include(d => d.Tags).Single().Tags.Count);
        }

        await TagManagementService.SetDomainTagsAsync(context, domainId, [primaryId], CancellationToken.None);

        using var verify2 = CreateContext();
        var updated = verify2.Domains.Include(d => d.Tags).Single();
        Assert.Single(updated.Tags);
        Assert.Equal("primary", updated.Tags[0].Name);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "GroupManagementServiceTests|TagManagementServiceTests"`
Expected: FAIL to build — `GroupManagementService`/`TagManagementService` don't exist yet.

- [ ] **Step 3: Write the implementation**

Create `src/DotMarc/Data/GroupManagementService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Add/rename/remove operations for Group rows, created through the "Manage groups"
/// page, plus setting a domain's full group membership from Manage Domains. Follows this
/// project's DomainManagementService convention of a static class operating directly on a
/// caller-supplied DotMarcDbContext.</summary>
public static class GroupManagementService
{
    public enum AddGroupResult { Added, InvalidName, AlreadyExists }

    public static async Task<AddGroupResult> AddGroupAsync(DotMarcDbContext context, string rawName, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddGroupResult.InvalidName;
        }

        var exists = await context.Groups.AnyAsync(g => g.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddGroupResult.AlreadyExists;
        }

        context.Groups.Add(new Group { Name = name });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // The unique index on Group.Name caught a same-cased race. A concurrent
            // different-cased duplicate (e.g. "Client A" vs "client a") is not caught by the
            // plain index — an accepted gap given group creation is a low-frequency manual
            // action, not the high-concurrency path Domain auto-discovery is.
            return AddGroupResult.AlreadyExists;
        }

        return AddGroupResult.Added;
    }

    public static async Task<AddGroupResult> RenameGroupAsync(DotMarcDbContext context, int groupId, string rawName, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddGroupResult.InvalidName;
        }

        var exists = await context.Groups.AnyAsync(g => g.Id != groupId && g.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddGroupResult.AlreadyExists;
        }

        var group = await context.Groups.SingleAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        group.Name = name;

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return AddGroupResult.AlreadyExists;
        }

        return AddGroupResult.Added;
    }

    /// <summary>Permanently deletes a Group row. DotMarcDbContext.cs's implicit many-to-many
    /// skip navigation between Domain and Group means EF removes the join rows via the join
    /// table's own cascade-delete foreign key — no Domain or Report data is touched.</summary>
    public static async Task RemoveGroupAsync(DotMarcDbContext context, int groupId, CancellationToken cancellationToken = default)
    {
        var group = await context.Groups.SingleAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        context.Groups.Remove(group);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces a domain's full set of group memberships with exactly the given group
    /// IDs — the multi-select on Manage Domains always submits the complete desired set, not an
    /// incremental add/remove.</summary>
    public static async Task SetDomainGroupsAsync(DotMarcDbContext context, int domainId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Include(d => d.Groups).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var groups = await context.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        domain.Groups = groups;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

Create `src/DotMarc/Data/TagManagementService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Add/update/remove operations for Tag rows, created through the "Manage groups"
/// page, plus setting a domain's full tag membership from Manage Domains. Follows this
/// project's DomainManagementService convention of a static class operating directly on a
/// caller-supplied DotMarcDbContext.</summary>
public static class TagManagementService
{
    public enum AddTagResult { Added, InvalidName, AlreadyExists }

    public static async Task<AddTagResult> AddTagAsync(DotMarcDbContext context, string rawName, Color color, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddTagResult.InvalidName;
        }

        var exists = await context.Tags.AnyAsync(t => t.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddTagResult.AlreadyExists;
        }

        context.Tags.Add(new Tag { Name = name, Color = color });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return AddTagResult.AlreadyExists;
        }

        return AddTagResult.Added;
    }

    public static async Task<AddTagResult> UpdateTagAsync(DotMarcDbContext context, int tagId, string rawName, Color color, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddTagResult.InvalidName;
        }

        var exists = await context.Tags.AnyAsync(t => t.Id != tagId && t.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddTagResult.AlreadyExists;
        }

        var tag = await context.Tags.SingleAsync(t => t.Id == tagId, cancellationToken).ConfigureAwait(false);
        tag.Name = name;
        tag.Color = color;

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return AddTagResult.AlreadyExists;
        }

        return AddTagResult.Added;
    }

    /// <summary>Permanently deletes a Tag row. See GroupManagementService.RemoveGroupAsync's doc
    /// comment — the same implicit many-to-many cascade behavior applies here.</summary>
    public static async Task RemoveTagAsync(DotMarcDbContext context, int tagId, CancellationToken cancellationToken = default)
    {
        var tag = await context.Tags.SingleAsync(t => t.Id == tagId, cancellationToken).ConfigureAwait(false);
        context.Tags.Remove(tag);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces a domain's full set of tag memberships with exactly the given tag IDs —
    /// see GroupManagementService.SetDomainGroupsAsync's doc comment for why this replaces
    /// rather than incrementally adds/removes.</summary>
    public static async Task SetDomainTagsAsync(DotMarcDbContext context, int domainId, IReadOnlyList<int> tagIds, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Include(d => d.Tags).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var tags = await context.Tags.Where(t => tagIds.Contains(t.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        domain.Tags = tags;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "GroupManagementServiceTests|TagManagementServiceTests"`
Expected: PASS (7 + 6 = 13 tests).

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Data/GroupManagementService.cs src/DotMarc/Data/TagManagementService.cs test/DotMarc.Tests/Data/GroupManagementServiceTests.cs test/DotMarc.Tests/Data/TagManagementServiceTests.cs
git commit -m "Add GroupManagementService and TagManagementService"
```

---

### Task 3: "Manage Groups" page

**Files:**
- Create: `src/DotMarc/Components/Pages/ManageGroups.razor`
- Create: `src/DotMarc/Components/Dialogs/ConfirmDeleteLabelDialog.razor`
- Modify: `src/DotMarc/Components/Layout/MainLayout.razor`

**Interfaces:**
- Consumes: `GroupManagementService`/`TagManagementService` (Task 2),
  `Group`/`Tag` (Task 1).
- Produces: the `/groups` route. No new types other later tasks depend on.

No automated test for this task's Razor UI, consistent with this project's
established precedent (no Blazor component-rendering test framework) — the
service layer underneath is already fully tested by Task 2.

- [ ] **Step 1: Create the shared confirm-delete dialog**

Create `src/DotMarc/Components/Dialogs/ConfirmDeleteLabelDialog.razor`:

```razor
<MudDialog>
    <TitleContent>Remove @Kind</TitleContent>
    <DialogContent>
        @if (DomainCount > 0)
        {
            <MudText Color="Color.Error">
                This will remove <b>@LabelName</b> and unassign it from @DomainCount domain(s).
                Domain data itself is not affected. This cannot be undone.
            </MudText>
        }
        else
        {
            <MudText>Remove <b>@LabelName</b>? It isn't assigned to any domains.</MudText>
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Color="Color.Error" Variant="Variant.Filled" OnClick="Confirm">Remove</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public string Kind { get; set; } = "";
    [Parameter] public string LabelName { get; set; } = "";
    [Parameter] public int DomainCount { get; set; }

    private void Confirm() => MudDialog.Close(DialogResult.Ok(true));
    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 2: Create the Manage Groups page**

Create `src/DotMarc/Components/Pages/ManageGroups.razor`:

```razor
@page "/groups"
@using DotMarc.Data
@using DotMarc.Components.Dialogs
@using Microsoft.EntityFrameworkCore
@inject IDbContextFactory<DotMarcDbContext> DbFactory
@inject IDialogService DialogService
@inject ISnackbar Snackbar

<PageTitle>dotMARC - Manage Groups</PageTitle>
<MudButton Href="/dashboard" StartIcon="@Icons.Material.Filled.ArrowBack" Class="mb-2">Back</MudButton>
<MudText Typo="Typo.h4" Class="mb-4">Manage groups</MudText>

<MudPaper Class="pa-4 mb-4" Elevation="1">
    <MudText Typo="Typo.h6" Class="mb-2">Groups</MudText>
    <MudGrid>
        <MudItem xs="9">
            <MudTextField @bind-Value="_newGroupName" Label="Group name" Placeholder="Client A"
                          Error="@(_addGroupError is not null)" ErrorText="@_addGroupError" Immediate="true" />
        </MudItem>
        <MudItem xs="3" Class="d-flex align-center">
            <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Add"
                       OnClick="AddGroupAsync">Add group</MudButton>
        </MudItem>
    </MudGrid>

    @if (_groups is { Count: > 0 })
    {
        <MudTable Items="_groups" Hover="true" T="GroupRow" Class="mt-4">
            <HeaderContent>
                <MudTh>Name</MudTh>
                <MudTh>Domains</MudTh>
                <MudTh></MudTh>
            </HeaderContent>
            <RowTemplate>
                <MudTd>
                    <MudTextField T="string" Value="context.Name" ValueChanged="@(v => RenameGroupAsync(context, v))" />
                </MudTd>
                <MudTd>@context.DomainCount</MudTd>
                <MudTd>
                    <MudIconButton Icon="@Icons.Material.Filled.Delete" Color="Color.Error" OnClick="@(() => DeleteGroupAsync(context))" />
                </MudTd>
            </RowTemplate>
        </MudTable>
    }
</MudPaper>

<MudPaper Class="pa-4" Elevation="1">
    <MudText Typo="Typo.h6" Class="mb-2">Tags</MudText>
    <MudGrid>
        <MudItem xs="6">
            <MudTextField @bind-Value="_newTagName" Label="Tag name" Placeholder="primary"
                          Error="@(_addTagError is not null)" ErrorText="@_addTagError" Immediate="true" />
        </MudItem>
        <MudItem xs="3">
            <MudSelect T="Color" @bind-Value="_newTagColor" Label="Color">
                @foreach (var color in TagColors)
                {
                    <MudSelectItem T="Color" Value="color">@color</MudSelectItem>
                }
            </MudSelect>
        </MudItem>
        <MudItem xs="3" Class="d-flex align-center">
            <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Add"
                       OnClick="AddTagAsync">Add tag</MudButton>
        </MudItem>
    </MudGrid>

    @if (_tags is { Count: > 0 })
    {
        <MudTable Items="_tags" Hover="true" T="TagRow" Class="mt-4">
            <HeaderContent>
                <MudTh>Preview</MudTh>
                <MudTh>Name</MudTh>
                <MudTh>Color</MudTh>
                <MudTh>Domains</MudTh>
                <MudTh></MudTh>
            </HeaderContent>
            <RowTemplate>
                <MudTd><MudChip T="string" Color="context.Color">@context.Name</MudChip></MudTd>
                <MudTd>
                    <MudTextField T="string" Value="context.Name" ValueChanged="@(v => UpdateTagAsync(context, v, context.Color))" />
                </MudTd>
                <MudTd>
                    <MudSelect T="Color" Value="context.Color" ValueChanged="@(v => UpdateTagAsync(context, context.Name, v))">
                        @foreach (var color in TagColors)
                        {
                            <MudSelectItem T="Color" Value="color">@color</MudSelectItem>
                        }
                    </MudSelect>
                </MudTd>
                <MudTd>@context.DomainCount</MudTd>
                <MudTd>
                    <MudIconButton Icon="@Icons.Material.Filled.Delete" Color="Color.Error" OnClick="@(() => DeleteTagAsync(context))" />
                </MudTd>
            </RowTemplate>
        </MudTable>
    }
</MudPaper>

@code {
    private static readonly Color[] TagColors = [Color.Primary, Color.Secondary, Color.Tertiary, Color.Info, Color.Dark];

    private List<GroupRow>? _groups;
    private List<TagRow>? _tags;
    private string _newGroupName = "";
    private string? _addGroupError;
    private string _newTagName = "";
    private Color _newTagColor = Color.Primary;
    private string? _addTagError;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _groups = await db.Groups
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new GroupRow(g.Id, g.Name, g.Domains.Count))
            .ToListAsync();
        _tags = await db.Tags
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new TagRow(t.Id, t.Name, t.Color, t.Domains.Count))
            .ToListAsync();
    }

    private async Task AddGroupAsync()
    {
        _addGroupError = null;
        GroupManagementService.AddGroupResult result;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            result = await GroupManagementService.AddGroupAsync(db, _newGroupName, CancellationToken.None);
        }
        catch (Exception)
        {
            _addGroupError = "Something went wrong adding the group. Try again.";
            return;
        }

        _addGroupError = result switch
        {
            GroupManagementService.AddGroupResult.InvalidName => "Enter a group name.",
            GroupManagementService.AddGroupResult.AlreadyExists => "A group with that name already exists.",
            _ => null
        };

        if (result == GroupManagementService.AddGroupResult.Added)
        {
            _newGroupName = "";
            await LoadAsync();
        }
    }

    private async Task RenameGroupAsync(GroupRow row, string newName)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            var result = await GroupManagementService.RenameGroupAsync(db, row.Id, newName, CancellationToken.None);
            if (result != GroupManagementService.AddGroupResult.Added)
            {
                Snackbar.Add("Failed to rename group — a group with that name may already exist.", Severity.Error);
            }
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to rename {row.Name}. Try again.", Severity.Error);
        }
        await LoadAsync();
    }

    private async Task DeleteGroupAsync(GroupRow row)
    {
        var parameters = new DialogParameters<ConfirmDeleteLabelDialog>
        {
            { x => x.Kind, "group" },
            { x => x.LabelName, row.Name },
            { x => x.DomainCount, row.DomainCount }
        };
        var dialogRef = await DialogService.ShowAsync<ConfirmDeleteLabelDialog>("Remove group", parameters);
        var result = await dialogRef.Result;

        if (result is { Canceled: false })
        {
            try
            {
                await using var db = await DbFactory.CreateDbContextAsync();
                await GroupManagementService.RemoveGroupAsync(db, row.Id, CancellationToken.None);
                await LoadAsync();
            }
            catch (Exception)
            {
                Snackbar.Add($"Failed to remove {row.Name}. Try again.", Severity.Error);
            }
        }
    }

    private async Task AddTagAsync()
    {
        _addTagError = null;
        TagManagementService.AddTagResult result;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            result = await TagManagementService.AddTagAsync(db, _newTagName, _newTagColor, CancellationToken.None);
        }
        catch (Exception)
        {
            _addTagError = "Something went wrong adding the tag. Try again.";
            return;
        }

        _addTagError = result switch
        {
            TagManagementService.AddTagResult.InvalidName => "Enter a tag name.",
            TagManagementService.AddTagResult.AlreadyExists => "A tag with that name already exists.",
            _ => null
        };

        if (result == TagManagementService.AddTagResult.Added)
        {
            _newTagName = "";
            _newTagColor = Color.Primary;
            await LoadAsync();
        }
    }

    private async Task UpdateTagAsync(TagRow row, string newName, Color newColor)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            var result = await TagManagementService.UpdateTagAsync(db, row.Id, newName, newColor, CancellationToken.None);
            if (result != TagManagementService.AddTagResult.Added)
            {
                Snackbar.Add("Failed to update tag — a tag with that name may already exist.", Severity.Error);
            }
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to update {row.Name}. Try again.", Severity.Error);
        }
        await LoadAsync();
    }

    private async Task DeleteTagAsync(TagRow row)
    {
        var parameters = new DialogParameters<ConfirmDeleteLabelDialog>
        {
            { x => x.Kind, "tag" },
            { x => x.LabelName, row.Name },
            { x => x.DomainCount, row.DomainCount }
        };
        var dialogRef = await DialogService.ShowAsync<ConfirmDeleteLabelDialog>("Remove tag", parameters);
        var result = await dialogRef.Result;

        if (result is { Canceled: false })
        {
            try
            {
                await using var db = await DbFactory.CreateDbContextAsync();
                await TagManagementService.RemoveTagAsync(db, row.Id, CancellationToken.None);
                await LoadAsync();
            }
            catch (Exception)
            {
                Snackbar.Add($"Failed to remove {row.Name}. Try again.", Severity.Error);
            }
        }
    }

    private sealed record GroupRow(int Id, string Name, int DomainCount);
    private sealed record TagRow(int Id, string Name, Color Color, int DomainCount);
}
```

- [ ] **Step 3: Add the nav link**

In `src/DotMarc/Components/Layout/MainLayout.razor`, change:

```razor
        <MudButton Href="/domains" Color="Color.Inherit">Manage domains</MudButton>
```

to:

```razor
        <MudButton Href="/domains" Color="Color.Inherit">Manage domains</MudButton>
        <MudButton Href="/groups" Color="Color.Inherit">Manage groups</MudButton>
```

- [ ] **Step 4: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 6: Manual verification**

If the environment allows running the app (see the README's Development
section for the required env vars and `docker compose up postgres`): sign
in, navigate to `/groups`, add a group and a tag (with a color), confirm
they appear in their respective lists with the right color, rename one
inline, delete one and confirm the confirmation dialog's domain count is
accurate (0 for a freshly-created group/tag). If the environment doesn't
allow this, report which steps were skipped and why — expected, not a
blocker, consistent with this project's precedent for UI-only tasks.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Components/Pages/ManageGroups.razor src/DotMarc/Components/Dialogs/ConfirmDeleteLabelDialog.razor src/DotMarc/Components/Layout/MainLayout.razor
git commit -m "Add Manage Groups page"
```

---

### Task 4: Manage Domains — Groups/Tags assignment

**Files:**
- Modify: `src/DotMarc/Components/Pages/ManageDomains.razor`

**Interfaces:**
- Consumes: `GroupManagementService.SetDomainGroupsAsync`,
  `TagManagementService.SetDomainTagsAsync` (Task 2).

No automated test for this task's Razor UI, consistent with this project's
established precedent — the service layer underneath (`SetDomainGroupsAsync`/
`SetDomainTagsAsync`) is already fully tested by Task 2.

- [ ] **Step 1: Add the two new columns**

In `src/DotMarc/Components/Pages/ManageDomains.razor`, change the header
row from:

```razor
        <HeaderContent>
            <MudTh></MudTh>
            <MudTh>Domain</MudTh>
            <MudTh>Monitored</MudTh>
            <MudTh>Reports</MudTh>
            <MudTh>Last report</MudTh>
            <MudTh></MudTh>
        </HeaderContent>
```

to:

```razor
        <HeaderContent>
            <MudTh></MudTh>
            <MudTh>Domain</MudTh>
            <MudTh>Groups</MudTh>
            <MudTh>Tags</MudTh>
            <MudTh>Monitored</MudTh>
            <MudTh>Reports</MudTh>
            <MudTh>Last report</MudTh>
            <MudTh></MudTh>
        </HeaderContent>
```

Change the `RowTemplate` — insert two new `<MudTd>` cells immediately after
the existing domain-name cell (the one with the drag/drop `<div>` inside)
and before the existing `Monitored` switch cell:

```razor
            <MudTd>
                <MudSelect T="int" MultiSelection="true" SelectedValues="context.GroupIds"
                           SelectedValuesChanged="@(ids => SetDomainGroupsAsync(context, ids))"
                           MultiSelectionTextFunc="@(selected => selected.Count == 0 ? "None" : $"{selected.Count} group(s)")"
                           Placeholder="None">
                    @foreach (var group in _allGroups)
                    {
                        <MudSelectItem T="int" Value="group.Id">@group.Name</MudSelectItem>
                    }
                </MudSelect>
            </MudTd>
            <MudTd>
                <MudSelect T="int" MultiSelection="true" SelectedValues="context.TagIds"
                           SelectedValuesChanged="@(ids => SetDomainTagsAsync(context, ids))"
                           MultiSelectionTextFunc="@(selected => selected.Count == 0 ? "None" : $"{selected.Count} tag(s)")"
                           Placeholder="None">
                    @foreach (var tag in _allTags)
                    {
                        <MudSelectItem T="int" Value="tag.Id">@tag.Name</MudSelectItem>
                    }
                </MudSelect>
            </MudTd>
```

If `_allGroups`/`_allTags` is empty, the picker renders with no items and
`Placeholder="None"` shows in its place — no separate empty-state markup
needed.

- [ ] **Step 2: Extend `DomainRow` and load the group/tag data**

Change:

```csharp
    private sealed record DomainRow(int Id, string Name, bool IsMonitored, int ReportCount, DateTimeOffset? LastReportReceivedUtc);
```

to:

```csharp
    private sealed record DomainRow(int Id, string Name, bool IsMonitored, int ReportCount, DateTimeOffset? LastReportReceivedUtc, List<int> GroupIds, List<int> TagIds);
```

Add two new fields alongside the existing `_domains` field:

```csharp
    private List<Group> _allGroups = [];
    private List<Tag> _allTags = [];
```

Change `LoadAsync` from:

```csharp
    private async Task LoadAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _domains = await db.Domains
            .AsNoTracking()
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .Select(d => new DomainRow(d.Id, d.Name, d.IsMonitored, d.Reports.Count, d.LastReportReceivedUtc))
            .ToListAsync();
    }
```

to:

```csharp
    private async Task LoadAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _allGroups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        _allTags = await db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        _domains = await db.Domains
            .AsNoTracking()
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .Select(d => new DomainRow(d.Id, d.Name, d.IsMonitored, d.Reports.Count, d.LastReportReceivedUtc,
                d.Groups.Select(g => g.Id).ToList(), d.Tags.Select(t => t.Id).ToList()))
            .ToListAsync();
    }
```

- [ ] **Step 3: Add the two new handler methods**

Add alongside the existing `SetMonitoredAsync` method:

```csharp
    private async Task SetDomainGroupsAsync(DomainRow row, IEnumerable<int> groupIds)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await GroupManagementService.SetDomainGroupsAsync(db, row.Id, groupIds.ToList(), CancellationToken.None);
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to update {row.Name}'s groups. Try again.", Severity.Error);
        }
        await LoadAsync();
    }

    private async Task SetDomainTagsAsync(DomainRow row, IEnumerable<int> tagIds)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await TagManagementService.SetDomainTagsAsync(db, row.Id, tagIds.ToList(), CancellationToken.None);
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to update {row.Name}'s tags. Try again.", Severity.Error);
        }
        await LoadAsync();
    }
```

- [ ] **Step 4: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 6: Manual verification**

If the environment allows it (same setup as Task 3's manual step): on
`/domains`, assign a group and a tag created in Task 3 to a domain via the
new multi-selects, confirm the selection persists after a page reload
(reflecting `SetDomainGroupsAsync`/`SetDomainTagsAsync` actually saved), and
confirm no status information (report counts, DMARC status, etc. beyond
what already existed) leaked into this page. If not possible in this
environment, report which steps were skipped and why.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Components/Pages/ManageDomains.razor
git commit -m "Add Groups/Tags assignment to Manage Domains"
```

---

### Task 5: Dashboard — Group/Tag filtering

**Files:**
- Modify: `src/DotMarc/Components/Pages/Dashboard.razor`

**Interfaces:**
- Consumes: `Group`, `Tag` (Task 1). Does NOT change
  `DashboardSummary.Build`'s signature (Task 5 of the earlier DMARC DNS
  status plan) — filtering happens on the domain list before `Build` is
  called.

No automated test for this task's Razor UI, consistent with this project's
established precedent — `DashboardSummary.Build` (unchanged by this task)
already has its own full test coverage, and this task's only new logic is a
LINQ `.Where` over an already-loaded list, exercised the same way `Build`
itself already is.

- [ ] **Step 1: Add the filter dropdowns**

In `src/DotMarc/Components/Pages/Dashboard.razor`, insert a new `MudGrid`
immediately after the opening `else` block's `{` and before the existing
summary-tiles `MudGrid`:

```razor
    <MudGrid Class="mb-4">
        <MudItem xs="12" sm="6">
            <MudSelect T="int?" Label="Group" Value="_selectedGroupId" ValueChanged="@(v => OnGroupFilterChangedAsync(v))" Clearable="true">
                @foreach (var group in _allGroups)
                {
                    <MudSelectItem T="int?" Value="group.Id">@group.Name</MudSelectItem>
                }
            </MudSelect>
        </MudItem>
        <MudItem xs="12" sm="6">
            <MudSelect T="int?" Label="Tag" Value="_selectedTagId" ValueChanged="@(v => OnTagFilterChangedAsync(v))" Clearable="true">
                @foreach (var tag in _allTags)
                {
                    <MudSelectItem T="int?" Value="tag.Id">@tag.Name</MudSelectItem>
                }
            </MudSelect>
        </MudItem>
    </MudGrid>
```

This goes above the existing `<MudGrid Class="mb-4">` that holds the six
summary tiles — both grids render, filter dropdowns first.

- [ ] **Step 2: Add the filter state and loading**

Add three new fields alongside the existing `_summary`/`_rows`/`_lastPoll`
fields:

```csharp
    private List<Group> _allGroups = [];
    private List<Tag> _allTags = [];
    private int? _selectedGroupId;
    private int? _selectedTagId;
```

Change `LoadAsync` from:

```csharp
    private async Task LoadAsync()
    {
        // A fresh, untracked context per render: the page is read-only and Blazor Server's DI
        // scope lives for the whole circuit, so injecting DotMarcDbContext directly would keep one
        // context (and its tracked entities) alive for as long as the tab stays open, showing
        // stale data as PollingService writes new reports behind it.
        await using var db = await DbFactory.CreateDbContextAsync();

        var cutoff = DomainStatistics.GetWindowCutoffUtc();
        var domains = await db.Domains
            .AsNoTracking()
            .Include(d => d.Reports.Where(r => r.ReceivedUtc >= cutoff))
            .ThenInclude(r => r.Records)
            .ToListAsync();

        var parseFailureCount = await db.ParseFailures.CountAsync();

        (_summary, _rows) = DashboardSummary.Build(domains, parseFailureCount);
```

to:

```csharp
    private async Task LoadAsync()
    {
        // A fresh, untracked context per render: the page is read-only and Blazor Server's DI
        // scope lives for the whole circuit, so injecting DotMarcDbContext directly would keep one
        // context (and its tracked entities) alive for as long as the tab stays open, showing
        // stale data as PollingService writes new reports behind it.
        await using var db = await DbFactory.CreateDbContextAsync();

        _allGroups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        _allTags = await db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync();

        var cutoff = DomainStatistics.GetWindowCutoffUtc();
        var domains = await db.Domains
            .AsNoTracking()
            .Include(d => d.Reports.Where(r => r.ReceivedUtc >= cutoff))
            .ThenInclude(r => r.Records)
            .Include(d => d.Groups)
            .Include(d => d.Tags)
            .ToListAsync();

        if (_selectedGroupId is { } groupId)
        {
            domains = domains.Where(d => d.Groups.Any(g => g.Id == groupId)).ToList();
        }
        if (_selectedTagId is { } tagId)
        {
            domains = domains.Where(d => d.Tags.Any(t => t.Id == tagId)).ToList();
        }

        var parseFailureCount = await db.ParseFailures.CountAsync();

        (_summary, _rows) = DashboardSummary.Build(domains, parseFailureCount);
```

(the rest of `LoadAsync`, loading `_lastPoll`, stays unchanged below this
point — only the section above `_lastPoll`'s load changes).

- [ ] **Step 3: Add the two filter-change handlers**

Add alongside the existing `ToggleMonitoredAsync` method:

```csharp
    private async Task OnGroupFilterChangedAsync(int? groupId)
    {
        _selectedGroupId = groupId;
        await LoadAsync();
    }

    private async Task OnTagFilterChangedAsync(int? tagId)
    {
        _selectedTagId = tagId;
        await LoadAsync();
    }
```

- [ ] **Step 4: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS — critically, all existing `DashboardSummaryTests` still
pass unmodified, confirming this task didn't touch `DashboardSummary.Build`.

- [ ] **Step 6: Manual verification**

If the environment allows it (same setup as Task 3's manual step): on
`/dashboard`, select a group assigned to at least one domain in Task 4's
manual test and confirm the table narrows to just that domain, and the
summary tiles (domain count, pass rate, etc.) update to reflect only the
filtered domain. Repeat for the Tag filter, and confirm selecting both
narrows further (AND, not OR). Clear both and confirm the full list
returns. If not possible in this environment, report which steps were
skipped and why.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Components/Pages/Dashboard.razor
git commit -m "Add Group/Tag filtering to Dashboard"
```
