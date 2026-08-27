# Permissions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace dotMARC's current "any authenticated user gets full access" authorization with a fine-grained, per-permission model that grants internal staff and external client contacts access the same way — by email, with an optional Group scope — while leaving sign-in itself (multi-tenant Entra ID) completely unchanged.

**Architecture:** A fixed `Permission` enum plus two new entities (`Role` — a named, admin-editable bundle of permissions; `UserAccess` — one row per granted person, keyed by email and bound to their Entra object ID on first sign-in) live in `DotMarc.Data`, following this project's existing entity/management-service conventions. An `IClaimsTransformation` enriches the signed-in user's `ClaimsPrincipal` with a claim per granted permission (and per accessible Group, when scoped) once per sign-in; ASP.NET Core's built-in policy-based authorization (`[Authorize(Policy = ...)]` / `AuthorizeView`) does the actual gating, page-by-page and control-by-control. A startup step, guarded by the same Postgres advisory-lock pattern already used for migrations, seeds the built-in `Admin`/`Viewer` roles and — only when no access grants exist yet — the initial Admin(s) from an environment variable.

**Tech Stack:** ASP.NET Core Blazor Server, MudBlazor 9.8.0, EF Core + Npgsql, Microsoft.Identity.Web (already a dependency, used for the existing OIDC sign-in), xUnit + Testcontainers.PostgreSql.

**Spec:** `docs/superpowers/specs/2026-08-27-permissions-design.md`

## Global Constraints

- Fourteen permissions, one enum, no more and no fewer: `DomainsView`,
  `DomainsAdd`, `DomainsEdit`, `DomainsReorder`, `DomainsDelete`,
  `GroupsView`, `GroupsAdd`, `GroupsRename`, `GroupsDelete`, `TagsView`,
  `TagsAdd`, `TagsEdit`, `TagsDelete`, `AccessManage`.
- Exactly two built-in roles, seeded at startup (not via EF Core `HasData`
  — see Task 3): `Admin` (`IsLocked = true`, `IsScopable = false`, every
  permission) and `Viewer` (`IsLocked = false`, `IsScopable = true`,
  `DomainsView`/`GroupsView`/`TagsView` only). `IsLocked` blocks renaming,
  permission-set changes, and deletion of a role through every code path,
  not just the UI. `IsScopable` is never exposed as an admin-editable
  option — every custom role is created with `IsScopable = false` — it
  exists purely so the "scope only applies to Viewer" rule survives a
  rename of the Viewer role itself, rather than being a fragile
  name-string check.
- A `UserAccess` grant's `ScopedGroups` is only ever meaningful when its
  Role has `IsScopable = true` — the service layer clears/ignores any
  supplied Group IDs for a non-scopable role, regardless of what the
  caller passes.
- A grant is made by `Email` and stays looked-up-by-email until the first
  time that email successfully signs in, at which point `EntraObjectId`
  is populated and becomes the lookup key from then on.
- Sign-in itself does not change in this plan — no new Entra app
  registration settings, no B2B, no new identity provider.
- `InitialAdmins:Emails` (comma-separated) is only ever consulted when the
  `UserAccess` table is completely empty; once any row exists, it's a
  no-op on every subsequent startup.
- The migration, the bootstrap seeding, and the tightened authorization
  fallback policy all land across this plan's tasks but are only
  meaningful once ALL of them are deployed together — Task 4 (the
  fallback-policy tightening) must not go out ahead of Task 3 (bootstrap
  seeding) in any real deployment, since that would lock out every user
  including the intended Admin. (Within this plan's own task sequence
  they're ordered correctly; this note is for whoever deploys the merged
  result.)
- No automated test exists for Blazor UI rendering in this codebase (no
  component-rendering test framework) — every UI-only task ends with a
  manual-verification step instead, consistent with every prior UI task
  this session.

---

### Task 1: `Permission` enum, `Role` and `UserAccess` entities, migration

**Files:**
- Create: `src/DotMarc/Data/Permission.cs`
- Create: `src/DotMarc/Data/Role.cs`
- Create: `src/DotMarc/Data/UserAccess.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Test: `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`
- (generated) `src/DotMarc/Migrations/`

**Interfaces:**
- Produces: `DotMarc.Data.Permission` enum (14 members, listed in Global
  Constraints), `DotMarc.Data.Role { int Id, string Name, bool IsLocked,
  bool IsScopable, List<Permission> Permissions }`, `DotMarc.Data.UserAccess
  { int Id, string Email, string? EntraObjectId, int RoleId, Role Role,
  List<Group> ScopedGroups }` — used by every later task in this plan.

- [ ] **Step 1: Write the failing tests**

Add to `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`, inside the
existing `DotMarcDbContextTests` class:

```csharp
    [Fact]
    public void CanInsertAndQuery_RoleWithPermissions()
    {
        using (var context = CreateContext())
        {
            context.Roles.Add(new Role
            {
                Name = "Domain Manager",
                IsLocked = false,
                IsScopable = false,
                Permissions = [Permission.DomainsView, Permission.DomainsAdd, Permission.DomainsEdit]
            });
            context.SaveChanges();
        }

        using var verify = CreateContext();
        var role = verify.Roles.Single();
        Assert.Equal("Domain Manager", role.Name);
        Assert.Equal(3, role.Permissions.Count);
        Assert.Contains(Permission.DomainsAdd, role.Permissions);
    }

    [Fact]
    public void Role_Name_MustBeUnique()
    {
        using var context = CreateContext();
        context.Roles.Add(new Role { Name = "Viewer", IsLocked = false, IsScopable = true, Permissions = [Permission.DomainsView] });
        context.SaveChanges();

        context.Roles.Add(new Role { Name = "Viewer", IsLocked = false, IsScopable = true, Permissions = [Permission.DomainsView] });
        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void CanInsertAndQuery_UserAccessWithScopedGroups()
    {
        using (var context = CreateContext())
        {
            var role = new Role { Name = "Viewer", IsLocked = false, IsScopable = true, Permissions = [Permission.DomainsView] };
            var group = new Group { Name = "Client A" };
            context.Roles.Add(role);
            context.Groups.Add(group);
            context.SaveChanges();

            context.UserAccesses.Add(new UserAccess
            {
                Email = "client@example.com",
                RoleId = role.Id,
                ScopedGroups = [group]
            });
            context.SaveChanges();
        }

        using var verify = CreateContext();
        var access = verify.UserAccesses.Include(u => u.Role).Include(u => u.ScopedGroups).Single();
        Assert.Equal("client@example.com", access.Email);
        Assert.Null(access.EntraObjectId);
        Assert.Equal("Viewer", access.Role.Name);
        Assert.Single(access.ScopedGroups);
    }

    [Fact]
    public void UserAccess_Email_MustBeUnique()
    {
        using var context = CreateContext();
        var role = new Role { Name = "Viewer", IsLocked = false, IsScopable = true, Permissions = [Permission.DomainsView] };
        context.Roles.Add(role);
        context.SaveChanges();

        context.UserAccesses.Add(new UserAccess { Email = "person@example.com", RoleId = role.Id });
        context.SaveChanges();

        context.UserAccesses.Add(new UserAccess { Email = "person@example.com", RoleId = role.Id });
        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "CanInsertAndQuery_RoleWithPermissions|Role_Name_MustBeUnique|CanInsertAndQuery_UserAccessWithScopedGroups|UserAccess_Email_MustBeUnique"`
Expected: FAIL to build — `Permission`, `Role`, `UserAccess` don't exist yet.

- [ ] **Step 3: Create the entities**

Create `src/DotMarc/Data/Permission.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>Every independently-grantable capability in the app. A fixed, closed set — adding a
/// new one is a code change (a new UI surface to gate), not something an admin can define, so this
/// is an enum rather than a database-driven list.</summary>
public enum Permission
{
    DomainsView,
    DomainsAdd,
    DomainsEdit,
    DomainsReorder,
    DomainsDelete,
    GroupsView,
    GroupsAdd,
    GroupsRename,
    GroupsDelete,
    TagsView,
    TagsAdd,
    TagsEdit,
    TagsDelete,
    AccessManage
}
```

Create `src/DotMarc/Data/Role.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>A named bundle of Permissions, grantable to any number of people via UserAccess.
/// IsLocked is true only for the built-in Admin role — enforced in RoleManagementService, not
/// just hidden in the UI, so Admin can never be renamed, have its permissions changed, or be
/// deleted through any code path, keeping it a reliable break-glass account. IsScopable is true
/// only for the built-in Viewer role and is never exposed as something an admin can set on a
/// custom role — it exists so "scope only applies to Viewer" survives a rename of Viewer itself,
/// rather than being a fragile string comparison against the name "Viewer".</summary>
public sealed class Role
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsLocked { get; set; }
    public bool IsScopable { get; set; }
    public List<Permission> Permissions { get; set; } = [];
}
```

Create `src/DotMarc/Data/UserAccess.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>One granted person — internal staff or an external client contact, granted the same
/// way. Email is what an admin types and is authoritative until EntraObjectId is populated on
/// that email's first successful sign-in, after which lookups use the object ID so a later
/// UPN/email rename on the Entra side can't orphan the grant. ScopedGroups only has any effect
/// when Role.IsScopable is true (see Role's doc comment) — UserAccessManagementService clears it
/// for any other role. An empty ScopedGroups list on a scopable role's grant means unrestricted
/// view access, not "access to nothing" — matching how the Dashboard's own Group filter already
/// treats "no filter selected".</summary>
public sealed class UserAccess
{
    public int Id { get; set; }
    public required string Email { get; set; }
    public string? EntraObjectId { get; set; }
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public List<Group> ScopedGroups { get; set; } = [];
}
```

- [ ] **Step 4: Register the DbSets and configure the model**

In `src/DotMarc/Data/DotMarcDbContext.cs`, add two DbSet properties
alongside the existing ones:

```csharp
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserAccess> UserAccesses => Set<UserAccess>();
```

In `OnModelCreating`, add:

```csharp
        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasIndex(r => r.Name).IsUnique();
            entity.Property(r => r.Permissions).HasConversion(
                permissions => permissions.Select(p => p.ToString()).ToArray(),
                stored => stored.Select(s => Enum.Parse<Permission>(s)).ToList());
        });

        modelBuilder.Entity<UserAccess>(entity =>
        {
            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasOne(u => u.Role)
                .WithMany()
                .HasForeignKey(u => u.RoleId)
                .OnDelete(DeleteBehavior.Restrict);
        });
```

`Role.Permissions` converts to/from a `string[]` — Npgsql maps a CLR
`string[]` property directly to a native Postgres `text[]` array column,
so this needs no separate join table. `DeleteBehavior.Restrict` on the
`UserAccess`→`Role` relationship means the database itself refuses to
delete a `Role` that any `UserAccess` row still references — a backstop
behind the service-level check Task 2 adds.

- [ ] **Step 5: Generate the migration**

Run: `dotnet ef migrations add AddPermissionsAndAccess --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

Review the generated migration carefully — this is the first place this
project has mapped a `List<TEnum>` property, so don't assume the
conversion worked as expected:

- Confirm `Roles.Permissions` is created as `text[]` (a genuine Postgres
  array column), not `jsonb` or a separate table. If it came out as
  something else, the `HasConversion` in Step 4 isn't being picked up the
  way this plan expects — stop and report rather than guessing at a fix.
- Confirm the join table for `UserAccess.ScopedGroups`↔`Group` (an
  implicit many-to-many, same mechanism as `Domain.Groups`/`Domain.Tags`
  from the domain-grouping feature) is created correctly.
- Confirm both new unique indexes (`Roles.Name`, `UserAccesses.Email`)
  are present.
- Confirm this is purely additive — new tables only, nothing altered or
  dropped on any existing table.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "CanInsertAndQuery_RoleWithPermissions|Role_Name_MustBeUnique|CanInsertAndQuery_UserAccessWithScopedGroups|UserAccess_Email_MustBeUnique"`
Expected: PASS (4 tests).

- [ ] **Step 7: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/DotMarc/Data/Permission.cs src/DotMarc/Data/Role.cs src/DotMarc/Data/UserAccess.cs src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Migrations/ test/DotMarc.Tests/Data/DotMarcDbContextTests.cs
git commit -m "Add Permission enum and Role/UserAccess entities"
```

---

### Task 2: `RoleManagementService` and `UserAccessManagementService`

**Files:**
- Create: `src/DotMarc/Data/RoleManagementService.cs`
- Create: `src/DotMarc/Data/UserAccessManagementService.cs`
- Test: `test/DotMarc.Tests/Data/RoleManagementServiceTests.cs`
- Test: `test/DotMarc.Tests/Data/UserAccessManagementServiceTests.cs`

**Interfaces:**
- Consumes: `Role`, `UserAccess`, `Permission`, `Group` (Task 1).
- Produces:
  - `RoleManagementService.AddRoleResult` (`Added`, `InvalidName`, `AlreadyExists`)
  - `RoleManagementService.AddRoleAsync(DotMarcDbContext, string rawName, List<Permission> permissions, CancellationToken) : Task<AddRoleResult>`
  - `RoleManagementService.UpdateRoleResult` (`Updated`, `InvalidName`, `AlreadyExists`, `Locked`)
  - `RoleManagementService.UpdateRoleAsync(DotMarcDbContext, int roleId, string rawName, List<Permission> permissions, CancellationToken) : Task<UpdateRoleResult>`
  - `RoleManagementService.RemoveRoleResult` (`Removed`, `Locked`, `InUse`)
  - `RoleManagementService.RemoveRoleAsync(DotMarcDbContext, int roleId, CancellationToken) : Task<RemoveRoleResult>`
  - `UserAccessManagementService.GrantAccessResult` (`Granted`, `InvalidEmail`, `AlreadyExists`, `RoleNotFound`)
  - `UserAccessManagementService.GrantAccessAsync(DotMarcDbContext, string rawEmail, int roleId, IReadOnlyList<int> groupIds, CancellationToken) : Task<GrantAccessResult>`
  - `UserAccessManagementService.UpdateAccessResult` (`Updated`, `RoleNotFound`)
  - `UserAccessManagementService.UpdateAccessAsync(DotMarcDbContext, int userAccessId, int roleId, IReadOnlyList<int> groupIds, CancellationToken) : Task<UpdateAccessResult>`
  - `UserAccessManagementService.RevokeAccessAsync(DotMarcDbContext, int userAccessId, CancellationToken) : Task`
  - `UserAccessManagementService.ResolveAsync(DotMarcDbContext, string? entraObjectId, string? email, CancellationToken) : Task<UserAccess?>` —
    the sign-in-time lookup/bind entry point Task 4's claims transformation calls.
  All `*Async` methods default `CancellationToken cancellationToken = default`, matching this
  project's existing management-service convention.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Data/RoleManagementServiceTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class RoleManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public RoleManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task AddRoleAsync_AddsARoleWithTheGivenPermissions()
    {
        using var context = CreateContext();

        var result = await RoleManagementService.AddRoleAsync(context, "Domain Manager", [Permission.DomainsView, Permission.DomainsAdd], CancellationToken.None);

        Assert.Equal(RoleManagementService.AddRoleResult.Added, result);
        using var verify = CreateContext();
        var role = verify.Roles.Single();
        Assert.Equal("Domain Manager", role.Name);
        Assert.False(role.IsLocked);
        Assert.False(role.IsScopable);
        Assert.Equal(2, role.Permissions.Count);
    }

    [Fact]
    public async Task AddRoleAsync_RejectsEmptyName()
    {
        using var context = CreateContext();

        var result = await RoleManagementService.AddRoleAsync(context, "   ", [Permission.DomainsView], CancellationToken.None);

        Assert.Equal(RoleManagementService.AddRoleResult.InvalidName, result);
        Assert.Empty(context.Roles);
    }

    [Fact]
    public async Task AddRoleAsync_RejectsCaseInsensitiveDuplicate()
    {
        using var context = CreateContext();
        await RoleManagementService.AddRoleAsync(context, "Domain Manager", [Permission.DomainsView], CancellationToken.None);

        var result = await RoleManagementService.AddRoleAsync(context, "domain manager", [Permission.DomainsView], CancellationToken.None);

        Assert.Equal(RoleManagementService.AddRoleResult.AlreadyExists, result);
    }

    [Fact]
    public async Task UpdateRoleAsync_UpdatesNameAndPermissions()
    {
        using var context = CreateContext();
        await RoleManagementService.AddRoleAsync(context, "Domain Manager", [Permission.DomainsView], CancellationToken.None);
        var roleId = context.Roles.Single().Id;

        var result = await RoleManagementService.UpdateRoleAsync(context, roleId, "Renamed", [Permission.DomainsView, Permission.DomainsDelete], CancellationToken.None);

        Assert.Equal(RoleManagementService.UpdateRoleResult.Updated, result);
        using var verify = CreateContext();
        var role = verify.Roles.Single();
        Assert.Equal("Renamed", role.Name);
        Assert.Equal(2, role.Permissions.Count);
    }

    [Fact]
    public async Task UpdateRoleAsync_RejectsChangesToALockedRole()
    {
        using var context = CreateContext();
        context.Roles.Add(new Role { Name = "Admin", IsLocked = true, IsScopable = false, Permissions = [Permission.AccessManage] });
        context.SaveChanges();
        var adminId = context.Roles.Single().Id;

        var result = await RoleManagementService.UpdateRoleAsync(context, adminId, "Not Admin", [Permission.DomainsView], CancellationToken.None);

        Assert.Equal(RoleManagementService.UpdateRoleResult.Locked, result);
        using var verify = CreateContext();
        Assert.Equal("Admin", verify.Roles.Single().Name);
    }

    [Fact]
    public async Task RemoveRoleAsync_RejectsRemovingALockedRole()
    {
        using var context = CreateContext();
        context.Roles.Add(new Role { Name = "Admin", IsLocked = true, IsScopable = false, Permissions = [Permission.AccessManage] });
        context.SaveChanges();
        var adminId = context.Roles.Single().Id;

        var result = await RoleManagementService.RemoveRoleAsync(context, adminId, CancellationToken.None);

        Assert.Equal(RoleManagementService.RemoveRoleResult.Locked, result);
    }

    [Fact]
    public async Task RemoveRoleAsync_RejectsRemovingARoleStillGrantedToSomeone()
    {
        using var context = CreateContext();
        await RoleManagementService.AddRoleAsync(context, "Domain Manager", [Permission.DomainsView], CancellationToken.None);
        var roleId = context.Roles.Single().Id;
        await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", roleId, [], CancellationToken.None);

        var result = await RoleManagementService.RemoveRoleAsync(context, roleId, CancellationToken.None);

        Assert.Equal(RoleManagementService.RemoveRoleResult.InUse, result);
        using var verify = CreateContext();
        Assert.NotEmpty(verify.Roles);
    }

    [Fact]
    public async Task RemoveRoleAsync_RemovesAnUnusedUnlockedRole()
    {
        using var context = CreateContext();
        await RoleManagementService.AddRoleAsync(context, "Domain Manager", [Permission.DomainsView], CancellationToken.None);
        var roleId = context.Roles.Single().Id;

        var result = await RoleManagementService.RemoveRoleAsync(context, roleId, CancellationToken.None);

        Assert.Equal(RoleManagementService.RemoveRoleResult.Removed, result);
        using var verify = CreateContext();
        Assert.Empty(verify.Roles);
    }
}
```

Create `test/DotMarc.Tests/Data/UserAccessManagementServiceTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class UserAccessManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public UserAccessManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private int SeedRole(DotMarcDbContext context, string name, bool isScopable, params Permission[] permissions)
    {
        var role = new Role { Name = name, IsLocked = false, IsScopable = isScopable, Permissions = [.. permissions] };
        context.Roles.Add(role);
        context.SaveChanges();
        return role.Id;
    }

    [Fact]
    public async Task GrantAccessAsync_GrantsAnUnscopedRoleWithNoGroups()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Domain Manager", isScopable: false, Permission.DomainsView);

        var result = await UserAccessManagementService.GrantAccessAsync(context, "  person@example.com  ", roleId, [999], CancellationToken.None);

        Assert.Equal(UserAccessManagementService.GrantAccessResult.Granted, result);
        using var verify = CreateContext();
        var access = verify.UserAccesses.Include(u => u.ScopedGroups).Single();
        Assert.Equal("person@example.com", access.Email);
        Assert.Empty(access.ScopedGroups); // non-scopable role: the group ID passed in is ignored.
    }

    [Fact]
    public async Task GrantAccessAsync_GrantsAScopableRoleWithTheGivenGroups()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        context.Groups.Add(new Group { Name = "Client A" });
        context.SaveChanges();
        var groupId = context.Groups.Single().Id;

        var result = await UserAccessManagementService.GrantAccessAsync(context, "client@example.com", roleId, [groupId], CancellationToken.None);

        Assert.Equal(UserAccessManagementService.GrantAccessResult.Granted, result);
        using var verify = CreateContext();
        var access = verify.UserAccesses.Include(u => u.ScopedGroups).Single();
        Assert.Single(access.ScopedGroups);
    }

    [Fact]
    public async Task GrantAccessAsync_RejectsADuplicateEmail()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", roleId, [], CancellationToken.None);

        var result = await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", roleId, [], CancellationToken.None);

        Assert.Equal(UserAccessManagementService.GrantAccessResult.AlreadyExists, result);
    }

    [Fact]
    public async Task GrantAccessAsync_RejectsAnUnknownRole()
    {
        using var context = CreateContext();

        var result = await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", 999, [], CancellationToken.None);

        Assert.Equal(UserAccessManagementService.GrantAccessResult.RoleNotFound, result);
    }

    [Fact]
    public async Task UpdateAccessAsync_ClearsScopedGroups_WhenNewRoleIsNotScopable()
    {
        using var context = CreateContext();
        var viewerRoleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        var managerRoleId = SeedRole(context, "Domain Manager", isScopable: false, Permission.DomainsEdit);
        context.Groups.Add(new Group { Name = "Client A" });
        context.SaveChanges();
        var groupId = context.Groups.Single().Id;
        await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", viewerRoleId, [groupId], CancellationToken.None);
        var accessId = context.UserAccesses.Single().Id;

        await UserAccessManagementService.UpdateAccessAsync(context, accessId, managerRoleId, [groupId], CancellationToken.None);

        using var verify = CreateContext();
        var access = verify.UserAccesses.Include(u => u.ScopedGroups).Single();
        Assert.Equal(managerRoleId, access.RoleId);
        Assert.Empty(access.ScopedGroups);
    }

    [Fact]
    public async Task RevokeAccessAsync_RemovesTheGrant()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", roleId, [], CancellationToken.None);
        var accessId = context.UserAccesses.Single().Id;

        await UserAccessManagementService.RevokeAccessAsync(context, accessId, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.UserAccesses);
    }

    [Fact]
    public async Task ResolveAsync_BindsAPendingGrant_ByEmailOnFirstSignIn()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", roleId, [], CancellationToken.None);

        var resolved = await UserAccessManagementService.ResolveAsync(context, "oid-123", "person@example.com", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("oid-123", resolved!.EntraObjectId);
        using var verify = CreateContext();
        Assert.Equal("oid-123", verify.UserAccesses.Single().EntraObjectId);
    }

    [Fact]
    public async Task ResolveAsync_FindsAnAlreadyBoundGrant_ByObjectIdRegardlessOfCurrentEmail()
    {
        using var context = CreateContext();
        var roleId = SeedRole(context, "Viewer", isScopable: true, Permission.DomainsView);
        await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", roleId, [], CancellationToken.None);
        await UserAccessManagementService.ResolveAsync(context, "oid-123", "person@example.com", CancellationToken.None);

        var resolved = await UserAccessManagementService.ResolveAsync(context, "oid-123", "person-renamed@example.com", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("person@example.com", resolved!.Email); // unchanged — lookup used the object ID, not the new email.
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenNeitherObjectIdNorEmailMatchesAnyGrant()
    {
        using var context = CreateContext();

        var resolved = await UserAccessManagementService.ResolveAsync(context, "oid-999", "nobody@example.com", CancellationToken.None);

        Assert.Null(resolved);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "RoleManagementServiceTests|UserAccessManagementServiceTests"`
Expected: FAIL to build — `RoleManagementService`/`UserAccessManagementService` don't exist yet.

- [ ] **Step 3: Write the implementation**

Create `src/DotMarc/Data/RoleManagementService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Add/update/remove operations for Role rows, created through the "Manage access"
/// page. Follows this project's DomainManagementService convention of a static class operating
/// directly on a caller-supplied DotMarcDbContext.</summary>
public static class RoleManagementService
{
    public enum AddRoleResult { Added, InvalidName, AlreadyExists }
    public enum UpdateRoleResult { Updated, InvalidName, AlreadyExists, Locked }
    public enum RemoveRoleResult { Removed, Locked, InUse }

    public static async Task<AddRoleResult> AddRoleAsync(DotMarcDbContext context, string rawName, List<Permission> permissions, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddRoleResult.InvalidName;
        }

        var exists = await context.Roles.AnyAsync(r => r.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddRoleResult.AlreadyExists;
        }

        context.Roles.Add(new Role { Name = name, IsLocked = false, IsScopable = false, Permissions = permissions });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return AddRoleResult.AlreadyExists;
        }

        return AddRoleResult.Added;
    }

    public static async Task<UpdateRoleResult> UpdateRoleAsync(DotMarcDbContext context, int roleId, string rawName, List<Permission> permissions, CancellationToken cancellationToken = default)
    {
        var role = await context.Roles.SingleAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role.IsLocked)
        {
            return UpdateRoleResult.Locked;
        }

        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return UpdateRoleResult.InvalidName;
        }

        var exists = await context.Roles.AnyAsync(r => r.Id != roleId && r.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return UpdateRoleResult.AlreadyExists;
        }

        role.Name = name;
        role.Permissions = permissions;

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return UpdateRoleResult.AlreadyExists;
        }

        return UpdateRoleResult.Updated;
    }

    /// <summary>Unlike Group/Tag deletion (which only ever removes membership rows and is always
    /// safe), deleting a Role that's still granted to someone would leave their UserAccess row
    /// pointing at nothing — an undefined-permissions state. This checks first and refuses rather
    /// than letting that happen; the database's own DeleteBehavior.Restrict foreign key is a
    /// backstop behind this check, not the primary guard.</summary>
    public static async Task<RemoveRoleResult> RemoveRoleAsync(DotMarcDbContext context, int roleId, CancellationToken cancellationToken = default)
    {
        var role = await context.Roles.SingleAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role.IsLocked)
        {
            return RemoveRoleResult.Locked;
        }

        var inUse = await context.UserAccesses.AnyAsync(u => u.RoleId == roleId, cancellationToken).ConfigureAwait(false);
        if (inUse)
        {
            return RemoveRoleResult.InUse;
        }

        context.Roles.Remove(role);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return RemoveRoleResult.Removed;
    }
}
```

Create `src/DotMarc/Data/UserAccessManagementService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Grant/update/revoke operations for UserAccess rows, plus the sign-in-time
/// lookup/bind entry point (ResolveAsync) that DotMarc.Security.UserAccessClaimsTransformation
/// calls. Follows this project's DomainManagementService convention of a static class operating
/// directly on a caller-supplied DotMarcDbContext.</summary>
public static class UserAccessManagementService
{
    public enum GrantAccessResult { Granted, InvalidEmail, AlreadyExists, RoleNotFound }
    public enum UpdateAccessResult { Updated, RoleNotFound }

    public static async Task<GrantAccessResult> GrantAccessAsync(DotMarcDbContext context, string rawEmail, int roleId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        var email = rawEmail.Trim();
        if (string.IsNullOrEmpty(email))
        {
            return GrantAccessResult.InvalidEmail;
        }

        var role = await context.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return GrantAccessResult.RoleNotFound;
        }

        var exists = await context.UserAccesses.AnyAsync(u => u.Email.ToLower() == email.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return GrantAccessResult.AlreadyExists;
        }

        var groups = role.IsScopable
            ? await context.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];

        context.UserAccesses.Add(new UserAccess { Email = email, RoleId = roleId, ScopedGroups = groups });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return GrantAccessResult.AlreadyExists;
        }

        return GrantAccessResult.Granted;
    }

    public static async Task<UpdateAccessResult> UpdateAccessAsync(DotMarcDbContext context, int userAccessId, int roleId, IReadOnlyList<int> groupIds, CancellationToken cancellationToken = default)
    {
        var role = await context.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return UpdateAccessResult.RoleNotFound;
        }

        var access = await context.UserAccesses.Include(u => u.ScopedGroups).SingleAsync(u => u.Id == userAccessId, cancellationToken).ConfigureAwait(false);
        access.RoleId = roleId;
        access.ScopedGroups = role.IsScopable
            ? await context.Groups.Where(g => groupIds.Contains(g.Id)).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return UpdateAccessResult.Updated;
    }

    public static async Task RevokeAccessAsync(DotMarcDbContext context, int userAccessId, CancellationToken cancellationToken = default)
    {
        var access = await context.UserAccesses.SingleAsync(u => u.Id == userAccessId, cancellationToken).ConfigureAwait(false);
        context.UserAccesses.Remove(access);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Looks up the caller's access grant by Entra object ID first (the stable,
    /// already-bound case). Falling back to a case-insensitive email match only when no
    /// object-ID match is found — binding that grant's EntraObjectId to the given value so every
    /// later sign-in resolves by object ID instead. Returns null when neither matches: the caller
    /// (the claims transformation) simply adds no permission claims for an unrecognized
    /// identity, and the tightened fallback authorization policy denies them.</summary>
    public static async Task<UserAccess?> ResolveAsync(DotMarcDbContext context, string? entraObjectId, string? email, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(entraObjectId))
        {
            var bound = await context.UserAccesses
                .Include(u => u.Role)
                .Include(u => u.ScopedGroups)
                .SingleOrDefaultAsync(u => u.EntraObjectId == entraObjectId, cancellationToken)
                .ConfigureAwait(false);
            if (bound is not null)
            {
                return bound;
            }
        }

        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        var pending = await context.UserAccesses
            .Include(u => u.Role)
            .Include(u => u.ScopedGroups)
            .SingleOrDefaultAsync(u => u.EntraObjectId == null && u.Email.ToLower() == email.ToLower(), cancellationToken)
            .ConfigureAwait(false);
        if (pending is null || string.IsNullOrEmpty(entraObjectId))
        {
            return pending;
        }

        pending.EntraObjectId = entraObjectId;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return pending;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter "RoleManagementServiceTests|UserAccessManagementServiceTests"`
Expected: PASS (7 + 9 = 16 tests).

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Data/RoleManagementService.cs src/DotMarc/Data/UserAccessManagementService.cs test/DotMarc.Tests/Data/RoleManagementServiceTests.cs test/DotMarc.Tests/Data/UserAccessManagementServiceTests.cs
git commit -m "Add RoleManagementService and UserAccessManagementService"
```

---

### Task 3: `AccessBootstrapper` — seed built-in roles and the initial Admin(s)

**Files:**
- Create: `src/DotMarc/Data/InitialAdminsOptions.cs`
- Create: `src/DotMarc/Data/AccessBootstrapper.cs`
- Modify: `src/DotMarc/Program.cs`
- Test: `test/DotMarc.Tests/Data/AccessBootstrapperTests.cs`

**Interfaces:**
- Consumes: `Role`, `UserAccess`, `Permission` (Task 1).
- Produces: `AccessBootstrapper.BootstrapWithLeaderLockAsync(DotMarcDbContext, IOptions<InitialAdminsOptions>, CancellationToken) : Task`,
  `InitialAdminsOptions { string Emails }`. No later task in this plan
  consumes these directly, but the deployment note in Global Constraints
  depends on this task's ordering relative to Task 4.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Data/AccessBootstrapperTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class AccessBootstrapperTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AccessBootstrapperTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static IOptions<InitialAdminsOptions> Options(string emails) =>
        Microsoft.Extensions.Options.Options.Create(new InitialAdminsOptions { Emails = emails });

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_SeedsAdminAndViewerRoles()
    {
        using var context = CreateContext();

        await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(""), CancellationToken.None);

        using var verify = CreateContext();
        var admin = verify.Roles.Single(r => r.Name == "Admin");
        Assert.True(admin.IsLocked);
        Assert.False(admin.IsScopable);
        Assert.Equal(Enum.GetValues<Permission>().Length, admin.Permissions.Count);

        var viewer = verify.Roles.Single(r => r.Name == "Viewer");
        Assert.False(viewer.IsLocked);
        Assert.True(viewer.IsScopable);
        Assert.Equal([Permission.DomainsView, Permission.GroupsView, Permission.TagsView], viewer.Permissions);
    }

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_GrantsAdminToEachConfiguredEmail_WhenNoAccessExistsYet()
    {
        using var context = CreateContext();

        await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(" first@example.com , second@example.com "), CancellationToken.None);

        using var verify = CreateContext();
        var adminRoleId = verify.Roles.Single(r => r.Name == "Admin").Id;
        var grants = verify.UserAccesses.ToList();
        Assert.Equal(2, grants.Count);
        Assert.All(grants, g => Assert.Equal(adminRoleId, g.RoleId));
        Assert.Contains(grants, g => g.Email == "first@example.com");
        Assert.Contains(grants, g => g.Email == "second@example.com");
    }

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_DoesNothing_WhenAnyAccessAlreadyExists()
    {
        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options("existing@example.com"), CancellationToken.None);
        }

        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options("someone-else@example.com"), CancellationToken.None);
        }

        using var verify = CreateContext();
        var grants = verify.UserAccesses.ToList();
        Assert.Single(grants);
        Assert.Equal("existing@example.com", grants[0].Email);
    }

    [Fact]
    public async Task BootstrapWithLeaderLockAsync_IsIdempotentOnRoles_AcrossMultipleCalls()
    {
        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(""), CancellationToken.None);
        }

        using (var context = CreateContext())
        {
            await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, Options(""), CancellationToken.None);
        }

        using var verify = CreateContext();
        Assert.Equal(2, verify.Roles.Count()); // still exactly Admin + Viewer, not duplicated.
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter AccessBootstrapperTests`
Expected: FAIL to build — `AccessBootstrapper`/`InitialAdminsOptions` don't exist yet.

- [ ] **Step 3: Write the implementation**

Create `src/DotMarc/Data/InitialAdminsOptions.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>Binds the InitialAdmins:Emails configuration section — a comma-separated list of
/// emails granted the Admin role the very first time the app starts with an empty UserAccess
/// table (see AccessBootstrapper). Deliberately not validated/required like GraphOptions: an
/// empty or absent value is a completely valid state on every startup after the first one.</summary>
public sealed class InitialAdminsOptions
{
    public const string SectionName = "InitialAdmins";
    public string Emails { get; set; } = "";
}
```

Create `src/DotMarc/Data/AccessBootstrapper.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Ensures the built-in Admin/Viewer roles exist and, only when the UserAccess table is
/// completely empty, grants Admin to the emails configured via InitialAdmins:Emails. Guarded by
/// the same Postgres advisory-lock pattern as DatabaseMigrator, so multiple replicas starting
/// concurrently don't race each other. Called once at startup, right after migrations run and
/// before the app serves any request — see Program.cs — so there's no window where the
/// authorization fallback policy (tightened in a later task) is live before this has run.
/// "Empty UserAccess table" covers both a genuinely fresh deployment and this app's own existing
/// live deployment picking up the permissions feature for the first time: from the database's
/// perspective the two look identical, since there's no way to retroactively know who's been
/// using the app so far.</summary>
public static class AccessBootstrapper
{
    internal const long BootstrapLeaderLockKey = 84_200_004;

    public static async Task BootstrapWithLeaderLockAsync(DotMarcDbContext context, IOptions<InitialAdminsOptions> options, CancellationToken cancellationToken = default)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", BootstrapLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var adminRoleId = await EnsureBuiltInRoleAsync(context, "Admin", isLocked: true, isScopable: false, [.. Enum.GetValues<Permission>()], cancellationToken).ConfigureAwait(false);
            await EnsureBuiltInRoleAsync(context, "Viewer", isLocked: false, isScopable: true, [Permission.DomainsView, Permission.GroupsView, Permission.TagsView], cancellationToken).ConfigureAwait(false);

            var anyAccessExists = await context.UserAccesses.AnyAsync(cancellationToken).ConfigureAwait(false);
            if (!anyAccessExists)
            {
                var emails = options.Value.Emails.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var email in emails)
                {
                    context.UserAccesses.Add(new UserAccess { Email = email, RoleId = adminRoleId });
                }
                if (emails.Length > 0)
                {
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await lockTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> EnsureBuiltInRoleAsync(DotMarcDbContext context, string name, bool isLocked, bool isScopable, List<Permission> permissions, CancellationToken cancellationToken)
    {
        var existing = await context.Roles.SingleOrDefaultAsync(r => r.Name == name, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Id;
        }

        var role = new Role { Name = name, IsLocked = isLocked, IsScopable = isScopable, Permissions = permissions };
        context.Roles.Add(role);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return role.Id;
    }
}
```

In `src/DotMarc/Program.cs`, add `using Microsoft.Extensions.Options;` to
the existing `using` block if not already present (check first — the
existing `GraphOptions`/`IOptions<GraphOptions>` usage a few lines down
means it's very likely already there).

Change:

```csharp
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await DatabaseMigrator.MigrateWithLeaderLockAsync(scope.ServiceProvider.GetRequiredService<DotMarcDbContext>());
}
```

to:

```csharp
builder.Services.Configure<InitialAdminsOptions>(builder.Configuration.GetSection(InitialAdminsOptions.SectionName));

builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<DotMarcDbContext>();
    await DatabaseMigrator.MigrateWithLeaderLockAsync(context);
    await AccessBootstrapper.BootstrapWithLeaderLockAsync(context, scope.ServiceProvider.GetRequiredService<IOptions<InitialAdminsOptions>>());
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter AccessBootstrapperTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 6: Build to confirm Program.cs wires up correctly**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Data/InitialAdminsOptions.cs src/DotMarc/Data/AccessBootstrapper.cs src/DotMarc/Program.cs test/DotMarc.Tests/Data/AccessBootstrapperTests.cs
git commit -m "Add AccessBootstrapper to seed built-in roles and the initial Admin(s)"
```

---

### Task 4: Claims transformation and authorization policies

**Files:**
- Create: `src/DotMarc/Security/UserAccessClaimsTransformation.cs`
- Modify: `src/DotMarc/Program.cs`
- Test: `test/DotMarc.Tests/Security/UserAccessClaimsTransformationTests.cs`

**Interfaces:**
- Consumes: `UserAccessManagementService.ResolveAsync` (Task 2).
- Produces: `DotMarc.Security.UserAccessClaimsTransformation` (implements
  `IClaimsTransformation`), `UserAccessClaimsTransformation.PermissionClaimType`
  (`const string`), `UserAccessClaimsTransformation.ScopedGroupClaimType`
  (`const string`) — later tasks (6, 7, 8) read these claim types directly
  from `AuthenticationState.User` to implement scope-locking on the
  Dashboard and Domain Detail. Also produces the named authorization
  policies `"DomainsWrite"` and `"GroupsOrTagsWrite"`, used by Tasks 7 and
  8 for page-level gating, plus one policy per `Permission` value (named
  after the enum member, e.g. `"DomainsAdd"`), used everywhere for
  control-level gating.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Security/UserAccessClaimsTransformationTests.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Security;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Security;

[Collection("Postgres")]
public sealed class UserAccessClaimsTransformationTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public UserAccessClaimsTransformationTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private FakeDbContextFactory CreateFactory() => new(_connectionString);

    private static ClaimsPrincipal PrincipalFor(string objectId, string email)
    {
        var identity = new ClaimsIdentity(
            [new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", objectId), new Claim("preferred_username", email)],
            authenticationType: "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task TransformAsync_AddsAPermissionClaimPerGrantedPermission()
    {
        using (var context = CreateContext())
        {
            var role = new Role { Name = "Domain Manager", IsLocked = false, IsScopable = false, Permissions = [Permission.DomainsView, Permission.DomainsAdd] };
            context.Roles.Add(role);
            context.SaveChanges();
            await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", role.Id, [], CancellationToken.None);
        }

        var transformation = new UserAccessClaimsTransformation(CreateFactory());
        var principal = await transformation.TransformAsync(PrincipalFor("oid-1", "person@example.com"));

        var permissionClaims = principal.FindAll(UserAccessClaimsTransformation.PermissionClaimType).Select(c => c.Value).ToList();
        Assert.Contains(nameof(Permission.DomainsView), permissionClaims);
        Assert.Contains(nameof(Permission.DomainsAdd), permissionClaims);
        Assert.DoesNotContain(nameof(Permission.DomainsDelete), permissionClaims);
    }

    [Fact]
    public async Task TransformAsync_AddsAScopedGroupClaimPerAccessibleGroup()
    {
        using (var context = CreateContext())
        {
            var role = new Role { Name = "Viewer", IsLocked = false, IsScopable = true, Permissions = [Permission.DomainsView] };
            var group = new Group { Name = "Client A" };
            context.Roles.Add(role);
            context.Groups.Add(group);
            context.SaveChanges();
            await UserAccessManagementService.GrantAccessAsync(context, "client@example.com", role.Id, [group.Id], CancellationToken.None);
        }

        var transformation = new UserAccessClaimsTransformation(CreateFactory());
        var principal = await transformation.TransformAsync(PrincipalFor("oid-2", "client@example.com"));

        var scopedGroupClaims = principal.FindAll(UserAccessClaimsTransformation.ScopedGroupClaimType).ToList();
        Assert.Single(scopedGroupClaims);
    }

    [Fact]
    public async Task TransformAsync_AddsNoClaims_ForAnUnrecognizedIdentity()
    {
        var transformation = new UserAccessClaimsTransformation(CreateFactory());

        var principal = await transformation.TransformAsync(PrincipalFor("oid-unknown", "nobody@example.com"));

        Assert.Empty(principal.FindAll(UserAccessClaimsTransformation.PermissionClaimType));
    }

    [Fact]
    public async Task TransformAsync_IsIdempotent_WhenCalledTwiceOnTheSamePrincipal()
    {
        using (var context = CreateContext())
        {
            var role = new Role { Name = "Domain Manager", IsLocked = false, IsScopable = false, Permissions = [Permission.DomainsView] };
            context.Roles.Add(role);
            context.SaveChanges();
            await UserAccessManagementService.GrantAccessAsync(context, "person@example.com", role.Id, [], CancellationToken.None);
        }

        var transformation = new UserAccessClaimsTransformation(CreateFactory());
        var principal = await transformation.TransformAsync(PrincipalFor("oid-3", "person@example.com"));
        principal = await transformation.TransformAsync(principal);

        Assert.Single(principal.FindAll(UserAccessClaimsTransformation.PermissionClaimType));
    }
}
```

This test file needs a tiny fake `IDbContextFactory<DotMarcDbContext>` —
`FakeGraphMailboxClient`-style, but for the DB factory rather than Graph.
Create `test/DotMarc.Tests/Internal/FakeDbContextFactory.cs`:

```csharp
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Tests.Internal;

/// <summary>A minimal IDbContextFactory<DotMarcDbContext> that always points at the same test
/// connection string — used where a real class under test (like
/// UserAccessClaimsTransformation) needs to create its own short-lived contexts rather than
/// being handed one directly.</summary>
internal sealed class FakeDbContextFactory(string connectionString) : IDbContextFactory<DotMarcDbContext>
{
    public DotMarcDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);

    public Task<DotMarcDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter UserAccessClaimsTransformationTests`
Expected: FAIL to build — `UserAccessClaimsTransformation`/`FakeDbContextFactory` don't exist yet.

- [ ] **Step 3: Write the implementation**

Create `src/DotMarc/Security/UserAccessClaimsTransformation.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

namespace DotMarc.Security;

/// <summary>Enriches the signed-in user's ClaimsPrincipal with dotMARC-specific authorization
/// data — one claim per granted Permission, plus one claim per accessible Group ID when the
/// grant is scoped — looked up via UserAccessManagementService.ResolveAsync. ASP.NET Core
/// invokes IClaimsTransformation as part of the authentication middleware, once per sign-in,
/// before the Blazor Server circuit starts — not on every render — so this doesn't add a
/// database round-trip to normal page navigation.</summary>
public sealed class UserAccessClaimsTransformation : IClaimsTransformation
{
    public const string PermissionClaimType = "dotmarc:permission";
    public const string ScopedGroupClaimType = "dotmarc:scoped-group";

    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;

    public UserAccessClaimsTransformation(IDbContextFactory<DotMarcDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == PermissionClaimType))
        {
            // ASP.NET Core can invoke IClaimsTransformation more than once per request; this
            // check makes re-invocation a no-op instead of duplicating claims.
            return principal;
        }

        // GetObjectId() is Microsoft.Identity.Web's own accessor for the Entra object ID claim —
        // preferred over reading a raw claim type string, since it's resilient to the exact
        // claim-type mapping in effect for a given token version/configuration.
        var objectId = principal.GetObjectId();
        var email = principal.FindFirst("preferred_username")?.Value;
        if (string.IsNullOrEmpty(objectId) && string.IsNullOrEmpty(email))
        {
            return principal;
        }

        await using var context = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var access = await UserAccessManagementService.ResolveAsync(context, objectId, email, CancellationToken.None).ConfigureAwait(false);
        if (access is null)
        {
            return principal;
        }

        var identity = (ClaimsIdentity)principal.Identity;
        foreach (var permission in access.Role.Permissions)
        {
            identity.AddClaim(new Claim(PermissionClaimType, permission.ToString()));
        }
        foreach (var group in access.ScopedGroups)
        {
            identity.AddClaim(new Claim(ScopedGroupClaimType, group.Id.ToString()));
        }

        return principal;
    }
}
```

**Verify against a real sign-in before trusting this fully**: the
`"preferred_username"` claim type is the standard OIDC claim Entra ID
puts the user's email/UPN in, and is what this task assumes — but claim
mapping can vary with configuration. If manual verification (Task 4's
Step 6) is possible in this environment, confirm by temporarily logging
`principal.Claims` and checking which claim actually carries the signed-in
email before relying on this in production. If manual verification isn't
possible here, say so clearly in the report and flag this specific
assumption for the person who does the branch's final live verification.

In `src/DotMarc/Program.cs`, change:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
```

to:

```csharp
builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, DotMarc.Security.UserAccessClaimsTransformation>();

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(DotMarc.Security.UserAccessClaimsTransformation.PermissionClaimType)
        .Build();

    foreach (var permission in Enum.GetValues<Permission>())
    {
        options.AddPolicy(permission.ToString(), policy =>
            policy.RequireClaim(DotMarc.Security.UserAccessClaimsTransformation.PermissionClaimType, permission.ToString()));
    }

    options.AddPolicy("DomainsWrite", policy => policy.RequireClaim(
        DotMarc.Security.UserAccessClaimsTransformation.PermissionClaimType,
        nameof(Permission.DomainsAdd), nameof(Permission.DomainsEdit), nameof(Permission.DomainsReorder), nameof(Permission.DomainsDelete)));

    options.AddPolicy("GroupsOrTagsWrite", policy => policy.RequireClaim(
        DotMarc.Security.UserAccessClaimsTransformation.PermissionClaimType,
        nameof(Permission.GroupsAdd), nameof(Permission.GroupsRename), nameof(Permission.GroupsDelete),
        nameof(Permission.TagsAdd), nameof(Permission.TagsEdit), nameof(Permission.TagsDelete)));
});
```

`RequireClaim(type, params string[] values)` is satisfied if the user has
*any* claim of that type matching *any* of the given values — this is
what gives `"DomainsWrite"`/`"GroupsOrTagsWrite"` their "at least one of
these permissions" (OR) semantics, with no custom
`IAuthorizationHandler` needed. The fallback policy's bare
`RequireClaim(PermissionClaimType)` (no specific value) means "has at
least one permission claim, whatever it is" — which is what actually
gates "authenticated AND recognized" now, replacing the old
`RequireAuthenticatedUser()`-only check.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter UserAccessClaimsTransformationTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 6: Build to confirm Program.cs wires up correctly**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Security/UserAccessClaimsTransformation.cs src/DotMarc/Program.cs test/DotMarc.Tests/Security/UserAccessClaimsTransformationTests.cs test/DotMarc.Tests/Internal/FakeDbContextFactory.cs
git commit -m "Add claims transformation and permission-based authorization policies"
```

---

### Task 5: "Manage Access" page

**Files:**
- Create: `src/DotMarc/Components/Pages/ManageAccess.razor`
- Modify: `src/DotMarc/Components/Layout/MainLayout.razor`

**Interfaces:**
- Consumes: `RoleManagementService`, `UserAccessManagementService` (Task 2),
  `Permission` (Task 1). Uses the `"AccessManage"` policy (Task 4).

No automated test for this task's Razor UI, consistent with this
project's established precedent — the service layer underneath is already
fully tested by Task 2.

- [ ] **Step 1: Create the Manage Access page**

Create `src/DotMarc/Components/Pages/ManageAccess.razor`:

```razor
@page "/access"
@attribute [Authorize(Policy = "AccessManage")]
@using DotMarc.Data
@using Microsoft.AspNetCore.Authorization
@using Microsoft.EntityFrameworkCore
@inject IDbContextFactory<DotMarcDbContext> DbFactory
@inject ISnackbar Snackbar

<PageTitle>dotMARC - Manage Access</PageTitle>
<MudButton Href="/dashboard" StartIcon="@Icons.Material.Filled.ArrowBack" Class="mb-2">Back</MudButton>
<MudText Typo="Typo.h4" Class="mb-4">Manage access</MudText>

<MudPaper Class="pa-4 mb-4" Elevation="1">
    <MudText Typo="Typo.h6" Class="mb-2">Roles</MudText>
    <MudGrid>
        <MudItem xs="9">
            <MudTextField @bind-Value="_newRoleName" Label="Role name" Placeholder="Domain Manager"
                          Error="@(_addRoleError is not null)" ErrorText="@_addRoleError" Immediate="true" />
        </MudItem>
        <MudItem xs="3" Class="d-flex align-center">
            <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Add"
                       OnClick="AddRoleAsync">Add role</MudButton>
        </MudItem>
    </MudGrid>

    @if (_roles is { Count: > 0 })
    {
        <MudTable Items="_roles" Hover="true" T="RoleRow" Class="mt-4">
            <HeaderContent>
                <MudTh>Name</MudTh>
                <MudTh>Permissions</MudTh>
                <MudTh></MudTh>
            </HeaderContent>
            <RowTemplate>
                <MudTd>
                    @if (context.IsLocked)
                    {
                        <MudText>@context.Name</MudText>
                    }
                    else
                    {
                        <MudTextField T="string" Value="context.Name" ValueChanged="@(v => RenameRoleAsync(context, v))" />
                    }
                </MudTd>
                <MudTd>
                    @if (context.IsLocked)
                    {
                        <MudText Typo="Typo.body2">All permissions</MudText>
                    }
                    else
                    {
                        <MudSelect T="Permission" MultiSelection="true" SelectedValues="context.Permissions"
                                   SelectedValuesChanged="@(p => SetRolePermissionsAsync(context, p))"
                                   MultiSelectionTextFunc="@(selected => selected.Count == 0 ? "None" : $"{selected.Count} permission(s)")"
                                   RelativeWidth="DropdownWidth.Adaptive">
                            @foreach (var permission in Enum.GetValues<Permission>())
                            {
                                <MudSelectItem T="Permission" Value="permission">@permission</MudSelectItem>
                            }
                        </MudSelect>
                    }
                </MudTd>
                <MudTd>
                    @if (!context.IsLocked)
                    {
                        <MudIconButton Icon="@Icons.Material.Filled.Delete" Color="Color.Error" OnClick="@(() => DeleteRoleAsync(context))" />
                    }
                </MudTd>
            </RowTemplate>
        </MudTable>
    }
</MudPaper>

<MudPaper Class="pa-4" Elevation="1">
    <MudText Typo="Typo.h6" Class="mb-2">Access grants</MudText>
    <MudGrid>
        <MudItem xs="5">
            <MudTextField @bind-Value="_newGrantEmail" Label="Email" Placeholder="person@example.com"
                          Error="@(_addGrantError is not null)" ErrorText="@_addGrantError" Immediate="true" />
        </MudItem>
        <MudItem xs="3">
            <MudSelect T="int" @bind-Value="_newGrantRoleId" Label="Role">
                @foreach (var role in _roles ?? [])
                {
                    <MudSelectItem T="int" Value="role.Id">@role.Name</MudSelectItem>
                }
            </MudSelect>
        </MudItem>
        @if (SelectedNewGrantRoleIsScopable)
        {
            <MudItem xs="4">
                <MudSelect T="int" MultiSelection="true" @bind-SelectedValues="_newGrantGroupIds" Label="Groups"
                           MultiSelectionTextFunc="@(selected => selected.Count == 0 ? "All (unrestricted)" : $"{selected.Count} group(s)")"
                           RelativeWidth="DropdownWidth.Adaptive">
                    @foreach (var group in _allGroups)
                    {
                        <MudSelectItem T="int" Value="group.Id">@group.Name</MudSelectItem>
                    }
                </MudSelect>
            </MudItem>
        }
        <MudItem xs="12" Class="d-flex justify-end">
            <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Add"
                       OnClick="GrantAccessAsync">Grant access</MudButton>
        </MudItem>
    </MudGrid>

    @if (_grants is { Count: > 0 })
    {
        <MudTable Items="_grants" Hover="true" T="GrantRow" Class="mt-4">
            <HeaderContent>
                <MudTh>Email</MudTh>
                <MudTh>Status</MudTh>
                <MudTh>Role</MudTh>
                <MudTh></MudTh>
            </HeaderContent>
            <RowTemplate>
                <MudTd>@context.Email</MudTd>
                <MudTd>@(context.IsBound ? "Active" : "Pending first sign-in")</MudTd>
                <MudTd>@context.RoleName</MudTd>
                <MudTd>
                    <MudIconButton Icon="@Icons.Material.Filled.Delete" Color="Color.Error" OnClick="@(() => RevokeAccessAsync(context))" />
                </MudTd>
            </RowTemplate>
        </MudTable>
    }
    else if (_grants is not null)
    {
        <MudText Typo="Typo.body2" Class="mt-4">No one has been granted access yet.</MudText>
    }
</MudPaper>

@code {
    private List<RoleRow>? _roles;
    private List<GrantRow>? _grants;
    private List<Group> _allGroups = [];
    private string _newRoleName = "";
    private string? _addRoleError;
    private string _newGrantEmail = "";
    private int _newGrantRoleId;
    private IEnumerable<int> _newGrantGroupIds = [];
    private string? _addGrantError;

    private bool SelectedNewGrantRoleIsScopable => _roles?.SingleOrDefault(r => r.Id == _newGrantRoleId)?.IsScopable ?? false;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _allGroups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        _roles = await db.Roles
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new RoleRow(r.Id, r.Name, r.IsLocked, r.IsScopable, r.Permissions))
            .ToListAsync();
        _grants = await db.UserAccesses
            .AsNoTracking()
            .OrderBy(u => u.Email)
            .Select(u => new GrantRow(u.Id, u.Email, u.EntraObjectId != null, u.Role.Name))
            .ToListAsync();

        if (_newGrantRoleId == 0 && _roles.Count > 0)
        {
            _newGrantRoleId = _roles[0].Id;
        }
    }

    private async Task AddRoleAsync()
    {
        _addRoleError = null;
        RoleManagementService.AddRoleResult result;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            result = await RoleManagementService.AddRoleAsync(db, _newRoleName, [], CancellationToken.None);
        }
        catch (Exception)
        {
            _addRoleError = "Something went wrong adding the role. Try again.";
            return;
        }

        _addRoleError = result switch
        {
            RoleManagementService.AddRoleResult.InvalidName => "Enter a role name.",
            RoleManagementService.AddRoleResult.AlreadyExists => "A role with that name already exists.",
            _ => null
        };

        if (result == RoleManagementService.AddRoleResult.Added)
        {
            _newRoleName = "";
            await LoadAsync();
        }
    }

    private async Task RenameRoleAsync(RoleRow row, string newName)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            var result = await RoleManagementService.UpdateRoleAsync(db, row.Id, newName, row.Permissions, CancellationToken.None);
            if (result != RoleManagementService.UpdateRoleResult.Updated)
            {
                Snackbar.Add("Failed to rename role — the name may be empty or already taken.", Severity.Error);
            }
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to rename {row.Name}. Try again.", Severity.Error);
        }
        await LoadAsync();
    }

    private async Task SetRolePermissionsAsync(RoleRow row, IEnumerable<Permission> permissions)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await RoleManagementService.UpdateRoleAsync(db, row.Id, row.Name, permissions.ToList(), CancellationToken.None);
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to update {row.Name}'s permissions. Try again.", Severity.Error);
        }
        await LoadAsync();
    }

    private async Task DeleteRoleAsync(RoleRow row)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            var result = await RoleManagementService.RemoveRoleAsync(db, row.Id, CancellationToken.None);
            if (result == RoleManagementService.RemoveRoleResult.InUse)
            {
                Snackbar.Add($"Can't remove {row.Name} — it's still granted to at least one person. Revoke or reassign their access first.", Severity.Error);
            }
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to remove {row.Name}. Try again.", Severity.Error);
        }
        await LoadAsync();
    }

    private async Task GrantAccessAsync()
    {
        _addGrantError = null;
        UserAccessManagementService.GrantAccessResult result;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            result = await UserAccessManagementService.GrantAccessAsync(db, _newGrantEmail, _newGrantRoleId, _newGrantGroupIds.ToList(), CancellationToken.None);
        }
        catch (Exception)
        {
            _addGrantError = "Something went wrong granting access. Try again.";
            return;
        }

        _addGrantError = result switch
        {
            UserAccessManagementService.GrantAccessResult.InvalidEmail => "Enter an email address.",
            UserAccessManagementService.GrantAccessResult.AlreadyExists => "That email already has access.",
            UserAccessManagementService.GrantAccessResult.RoleNotFound => "Choose a role.",
            _ => null
        };

        if (result == UserAccessManagementService.GrantAccessResult.Granted)
        {
            _newGrantEmail = "";
            _newGrantGroupIds = [];
            await LoadAsync();
        }
    }

    private async Task RevokeAccessAsync(GrantRow row)
    {
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await UserAccessManagementService.RevokeAccessAsync(db, row.Id, CancellationToken.None);
            await LoadAsync();
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to revoke access for {row.Email}. Try again.", Severity.Error);
        }
    }

    private sealed record RoleRow(int Id, string Name, bool IsLocked, bool IsScopable, List<Permission> Permissions);
    private sealed record GrantRow(int Id, string Email, bool IsBound, string RoleName);
}
```

- [ ] **Step 2: Add the nav link**

In `src/DotMarc/Components/Layout/MainLayout.razor`, change:

```razor
        <MudButton Href="/domains" Color="Color.Inherit">Manage domains</MudButton>
        <MudButton Href="/groups" Color="Color.Inherit">Manage groups</MudButton>
```

to:

```razor
        <AuthorizeView Policy="DomainsWrite">
            <MudButton Href="/domains" Color="Color.Inherit">Manage domains</MudButton>
        </AuthorizeView>
        <AuthorizeView Policy="GroupsOrTagsWrite">
            <MudButton Href="/groups" Color="Color.Inherit">Manage groups</MudButton>
        </AuthorizeView>
        <AuthorizeView Policy="AccessManage">
            <MudButton Href="/access" Color="Color.Inherit">Manage access</MudButton>
        </AuthorizeView>
```

`AuthorizeView`'s default rendering with no `<Authorized>`/`<NotAuthorized>`
child content is: render the child content when authorized, render
nothing when not — exactly "hide, don't disable," matching this
project's established convention from the domain-grouping feature. Add
`@using Microsoft.AspNetCore.Components.Authorization` to the top of
`MainLayout.razor` if it isn't already implicitly available (check the
file first — Blazor's default `_Imports.razor` likely already covers
this for every component in `Components/`, in which case no change is
needed here).

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 4: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 5: Manual verification**

If the environment allows running the app end-to-end with real Entra ID
credentials (see the README's Development section): sign in as the
bootstrapped Admin, navigate to `/access`, create a custom role with a
couple of permissions, grant access to a second email scoped to an
existing Group, and confirm the grant shows "Pending first sign-in" until
that email actually signs in. If this environment doesn't allow it
(consistent with every prior UI task this session), report clearly which
steps were skipped and why — not a blocker.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Components/Pages/ManageAccess.razor src/DotMarc/Components/Layout/MainLayout.razor
git commit -m "Add Manage Access page"
```

---

### Task 6: Gate Dashboard and Domain Detail

**Files:**
- Modify: `src/DotMarc/Components/Pages/Dashboard.razor`
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor`

**Interfaces:**
- Consumes: `UserAccessClaimsTransformation.ScopedGroupClaimType` (Task 4)
  — read from the current `AuthenticationState.User`'s claims.

No automated test for this task's Razor UI, consistent with this
project's established precedent.

- [ ] **Step 1: Gate the Dashboard page and lock its Group filter when scoped**

In `src/DotMarc/Components/Pages/Dashboard.razor`, add after the existing
`@page "/dashboard"` line:

```razor
@attribute [Authorize(Policy = "DomainsView")]
```

Add `@using Microsoft.AspNetCore.Authorization` and
`@using Microsoft.AspNetCore.Components.Authorization` to the file's
existing `@using` block if not already present. Add a cascading parameter
alongside the existing `@inject` lines:

```razor
@inject AuthenticationStateProvider AuthenticationStateProvider
```

In the `@code` block, add a field and populate it in `LoadAsync`:

```csharp
    private List<int>? _scopedGroupIds;
```

Change the start of `LoadAsync` from:

```csharp
    private async Task LoadAsync()
    {
        // A fresh, untracked context per render: the page is read-only and Blazor Server's DI
        // scope lives for the whole circuit, so injecting DotMarcDbContext directly would keep one
        // context (and its tracked entities) alive for as long as the tab stays open, showing
        // stale data as PollingService writes new reports behind it.
        await using var db = await DbFactory.CreateDbContextAsync();

        _allGroups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
```

to:

```csharp
    private async Task LoadAsync()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var scopedGroupClaims = authState.User.FindAll(DotMarc.Security.UserAccessClaimsTransformation.ScopedGroupClaimType).ToList();
        _scopedGroupIds = scopedGroupClaims.Count == 0 ? null : scopedGroupClaims.Select(c => int.Parse(c.Value)).ToList();
        if (_scopedGroupIds is not null && (_selectedGroupId is null || !_scopedGroupIds.Contains(_selectedGroupId.Value)))
        {
            _selectedGroupId = _scopedGroupIds[0];
        }

        // A fresh, untracked context per render: the page is read-only and Blazor Server's DI
        // scope lives for the whole circuit, so injecting DotMarcDbContext directly would keep one
        // context (and its tracked entities) alive for as long as the tab stays open, showing
        // stale data as PollingService writes new reports behind it.
        await using var db = await DbFactory.CreateDbContextAsync();

        _allGroups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        if (_scopedGroupIds is not null)
        {
            _allGroups = _allGroups.Where(g => _scopedGroupIds.Contains(g.Id)).ToList();
        }
```

This reuses the existing `_selectedGroupId` filter field exactly as-is —
a scoped user simply never has any Group in `_allGroups` outside their
allowed set, so the dropdown can't offer anything else, and this forces a
valid selection rather than leaving `_selectedGroupId` null (which would
mean "no filter" — unrestricted — exactly what a scoped user must not be
able to reach). The existing `if (_selectedGroupId is { } groupId) { domains = domains.Where(...) }` filtering logic later in `LoadAsync` needs no
changes — it already narrows by whatever `_selectedGroupId` holds.

Also lock the dropdown itself so a scoped user can't clear it back to
"no filter" via the UI's own `Clearable` control. Change:

```razor
            <MudSelect T="int?" Label="Group" Value="_selectedGroupId" ValueChanged="@(v => OnGroupFilterChangedAsync(v))" Clearable="true">
```

to:

```razor
            <MudSelect T="int?" Label="Group" Value="_selectedGroupId" ValueChanged="@(v => OnGroupFilterChangedAsync(v))" Clearable="@(_scopedGroupIds is null)" Disabled="@(_scopedGroupIds is not null && _scopedGroupIds.Count == 1)">
```

(a scoped user with exactly one accessible Group has nothing meaningful
to pick between, so the control is disabled rather than left as a
one-item picker; a scoped user with several accessible Groups can still
switch between them, just never clear back to "all").

- [ ] **Step 2: Gate Domain Detail and check scope on the specific domain**

In `src/DotMarc/Components/Pages/DomainDetail.razor`, add after the
existing `@page "/domains/{DomainName}"` line:

```razor
@attribute [Authorize(Policy = "DomainsView")]
```

Add the same two `@using` lines as Step 1 if not already present, plus
`@inject AuthenticationStateProvider AuthenticationStateProvider` and
`@inject NavigationManager Navigation` (check first — `NavigationManager`
may already be injected by this page; if so, don't duplicate it).

In `OnInitializedAsync` (or wherever `_domain` is currently loaded), after
the domain is fetched, add a scope check. Read the current file's
`OnInitializedAsync` first to place this correctly relative to the
existing load logic, then add:

```csharp
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var scopedGroupClaims = authState.User.FindAll(DotMarc.Security.UserAccessClaimsTransformation.ScopedGroupClaimType).ToList();
        if (scopedGroupClaims.Count > 0)
        {
            var scopedGroupIds = scopedGroupClaims.Select(c => int.Parse(c.Value)).ToHashSet();
            var domainGroupIds = _domain?.Groups.Select(g => g.Id).ToHashSet() ?? [];
            if (!domainGroupIds.Overlaps(scopedGroupIds))
            {
                Navigation.NavigateTo("/AccessDenied");
                return;
            }
        }
```

This must run after `_domain` is loaded (so `_domain.Groups` is
populated) — check the existing query that fetches `_domain` includes
`.Include(d => d.Groups)` already; if it doesn't (the DMARC DNS status
and domain-grouping features may not have needed it before now), add it.

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 4: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 5: Manual verification**

If the environment allows it (same setup as Task 5's manual step): sign
in as a Viewer scoped to one Group, confirm the Dashboard's Group filter
is locked to that Group and can't be cleared, confirm domains outside
that Group never appear, and confirm navigating directly to an
out-of-scope domain's `/domains/{name}` URL redirects to Access Denied
rather than rendering. If not possible in this environment, report which
steps were skipped and why.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Components/Pages/Dashboard.razor src/DotMarc/Components/Pages/DomainDetail.razor
git commit -m "Gate Dashboard and Domain Detail by DomainsView and Group scope"
```

---

### Task 7: Gate Manage Domains

**Files:**
- Modify: `src/DotMarc/Components/Pages/ManageDomains.razor`

**Interfaces:**
- Consumes: the `"DomainsWrite"` composite policy and the per-permission
  policies `"DomainsAdd"`, `"DomainsEdit"`, `"DomainsReorder"`,
  `"DomainsDelete"` (Task 4).

No automated test for this task's Razor UI, consistent with this
project's established precedent.

- [ ] **Step 1: Gate the page**

In `src/DotMarc/Components/Pages/ManageDomains.razor`, add after the
existing `@page "/domains"` line:

```razor
@attribute [Authorize(Policy = "DomainsWrite")]
```

Add `@using Microsoft.AspNetCore.Authorization` and
`@using Microsoft.AspNetCore.Components.Authorization` to the file's
`@using` block if not already present.

- [ ] **Step 2: Gate the add-domain form**

Wrap the existing add-domain `MudPaper` block in an `AuthorizeView` for
`DomainsAdd`. Change:

```razor
<MudPaper Class="pa-4 mb-4" Elevation="1">
    <MudGrid>
        <MudItem xs="9">
            <MudTextField @bind-Value="_newDomainName" Label="Domain name" Placeholder="contoso.com"
                          Error="@(_addError is not null)" ErrorText="@_addError" Immediate="true" />
        </MudItem>
        <MudItem xs="3" Class="d-flex align-center">
            <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Add"
                       OnClick="AddDomainAsync">Add domain</MudButton>
        </MudItem>
    </MudGrid>
</MudPaper>
```

to:

```razor
<AuthorizeView Policy="DomainsAdd">
    <MudPaper Class="pa-4 mb-4" Elevation="1">
        <MudGrid>
            <MudItem xs="9">
                <MudTextField @bind-Value="_newDomainName" Label="Domain name" Placeholder="contoso.com"
                              Error="@(_addError is not null)" ErrorText="@_addError" Immediate="true" />
            </MudItem>
            <MudItem xs="3" Class="d-flex align-center">
                <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Add"
                           OnClick="AddDomainAsync">Add domain</MudButton>
            </MudItem>
        </MudGrid>
    </MudPaper>
</AuthorizeView>
```

- [ ] **Step 3: Gate the drag-reorder handle, per row**

Change:

```razor
            <MudTd Style="width: 2.5rem;">
                @* Drag/drop attributes live on plain <div>s, not MudTd itself: MudTd is a
                   MudBlazor component, and whether its attribute splatting forwards
                   @ondragover:preventDefault correctly to the rendered <td> — with no paired
                   @ondragover handler — isn't a guarantee Blazor makes for components the way it
                   does for elements. A native element sidesteps the question entirely. *@
                <div draggable="true" @ondragstart="@(() => OnDragStart(context.Id))"
                     @ondragover:preventDefault="true" @ondrop="@(() => OnDropAsync(context.Id))"
                     style="cursor:grab; display:flex; align-items:center; width:100%; height:100%; margin:-16px; padding:16px;">
                    <MudIcon Icon="@Icons.Material.Filled.DragIndicator" Size="Size.Small" />
                </div>
            </MudTd>
```

to:

```razor
            <MudTd Style="width: 2.5rem;">
                <AuthorizeView Policy="DomainsReorder">
                    @* Drag/drop attributes live on plain <div>s, not MudTd itself: MudTd is a
                       MudBlazor component, and whether its attribute splatting forwards
                       @ondragover:preventDefault correctly to the rendered <td> — with no paired
                       @ondragover handler — isn't a guarantee Blazor makes for components the way it
                       does for elements. A native element sidesteps the question entirely. *@
                    <div draggable="true" @ondragstart="@(() => OnDragStart(context.Id))"
                         @ondragover:preventDefault="true" @ondrop="@(() => OnDropAsync(context.Id))"
                         style="cursor:grab; display:flex; align-items:center; width:100%; height:100%; margin:-16px; padding:16px;">
                        <MudIcon Icon="@Icons.Material.Filled.DragIndicator" Size="Size.Small" />
                    </div>
                </AuthorizeView>
            </MudTd>
```

- [ ] **Step 4: Gate the Monitored toggle and the Groups/Tags multi-selects**

For each of the three controls (the `MudSwitch` for Monitored, and the
two `MudSelect` multi-selects for Groups/Tags), wrap in an `AuthorizeView`
for `DomainsEdit`, with a plain read-only fallback in `<NotAuthorized>` so
someone who can only Add or Reorder domains (but not Edit them) still
sees the data, just not the controls. Change:

```razor
            <MudTd>
                <MudSelect T="int" MultiSelection="true" SelectedValues="context.GroupIds"
                           SelectedValuesChanged="@(ids => SetDomainGroupsAsync(context, ids))"
                           MultiSelectionTextFunc="@(selected => selected.Count == 0 ? "None" : $"{selected.Count} group(s)")"
                           RelativeWidth="DropdownWidth.Adaptive">
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
                           RelativeWidth="DropdownWidth.Adaptive">
                    @foreach (var tag in _allTags)
                    {
                        <MudSelectItem T="int" Value="tag.Id">@tag.Name</MudSelectItem>
                    }
                </MudSelect>
            </MudTd>
            <MudTd>
                <MudSwitch T="bool" Value="context.IsMonitored" ValueChanged="@(v => SetMonitoredAsync(context, v))" Color="Color.Primary" />
            </MudTd>
```

to:

```razor
            <MudTd>
                <AuthorizeView Policy="DomainsEdit">
                    <Authorized>
                        <MudSelect T="int" MultiSelection="true" SelectedValues="context.GroupIds"
                                   SelectedValuesChanged="@(ids => SetDomainGroupsAsync(context, ids))"
                                   MultiSelectionTextFunc="@(selected => selected.Count == 0 ? "None" : $"{selected.Count} group(s)")"
                                   RelativeWidth="DropdownWidth.Adaptive">
                            @foreach (var group in _allGroups)
                            {
                                <MudSelectItem T="int" Value="group.Id">@group.Name</MudSelectItem>
                            }
                        </MudSelect>
                    </Authorized>
                    <NotAuthorized>
                        @(_allGroups.Where(g => context.GroupIds.Contains(g.Id)).Select(g => g.Name).Any()
                            ? string.Join(", ", _allGroups.Where(g => context.GroupIds.Contains(g.Id)).Select(g => g.Name))
                            : "None")
                    </NotAuthorized>
                </AuthorizeView>
            </MudTd>
            <MudTd>
                <AuthorizeView Policy="DomainsEdit">
                    <Authorized>
                        <MudSelect T="int" MultiSelection="true" SelectedValues="context.TagIds"
                                   SelectedValuesChanged="@(ids => SetDomainTagsAsync(context, ids))"
                                   MultiSelectionTextFunc="@(selected => selected.Count == 0 ? "None" : $"{selected.Count} tag(s)")"
                                   RelativeWidth="DropdownWidth.Adaptive">
                            @foreach (var tag in _allTags)
                            {
                                <MudSelectItem T="int" Value="tag.Id">@tag.Name</MudSelectItem>
                            }
                        </MudSelect>
                    </Authorized>
                    <NotAuthorized>
                        @(_allTags.Where(t => context.TagIds.Contains(t.Id)).Select(t => t.Name).Any()
                            ? string.Join(", ", _allTags.Where(t => context.TagIds.Contains(t.Id)).Select(t => t.Name))
                            : "None")
                    </NotAuthorized>
                </AuthorizeView>
            </MudTd>
            <MudTd>
                <AuthorizeView Policy="DomainsEdit">
                    <Authorized>
                        <MudSwitch T="bool" Value="context.IsMonitored" ValueChanged="@(v => SetMonitoredAsync(context, v))" Color="Color.Primary" />
                    </Authorized>
                    <NotAuthorized>
                        @(context.IsMonitored ? "Yes" : "No")
                    </NotAuthorized>
                </AuthorizeView>
            </MudTd>
```

- [ ] **Step 5: Gate the delete button**

Change:

```razor
            <MudTd>
                <MudIconButton Icon="@Icons.Material.Filled.Delete" Color="Color.Error" OnClick="@(() => DeleteDomainAsync(context))" />
            </MudTd>
```

to:

```razor
            <MudTd>
                <AuthorizeView Policy="DomainsDelete">
                    <MudIconButton Icon="@Icons.Material.Filled.Delete" Color="Color.Error" OnClick="@(() => DeleteDomainAsync(context))" />
                </AuthorizeView>
            </MudTd>
```

- [ ] **Step 6: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 7: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 8: Manual verification**

If the environment allows it (same setup as Task 5's manual step): sign
in as someone with only `DomainsView` (a plain Viewer) and confirm Manage
Domains itself is unreachable (redirects to Access Denied). Sign in as
someone with `DomainsAdd` but not `DomainsEdit`/`DomainsDelete`/`DomainsReorder`
and confirm the add form shows, but the switch/multi-selects render as
plain text and the drag handle and delete button are both absent. If not
possible in this environment, report which steps were skipped and why.

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc/Components/Pages/ManageDomains.razor
git commit -m "Gate Manage Domains by DomainsWrite and per-control permissions"
```

---

### Task 8: Gate Manage Groups

**Files:**
- Modify: `src/DotMarc/Components/Pages/ManageGroups.razor`

**Interfaces:**
- Consumes: the `"GroupsOrTagsWrite"` composite policy and the
  per-permission policies `"GroupsAdd"`, `"GroupsRename"`, `"GroupsDelete"`,
  `"TagsAdd"`, `"TagsEdit"`, `"TagsDelete"` (Task 4).

No automated test for this task's Razor UI, consistent with this
project's established precedent.

- [ ] **Step 1: Gate the page**

In `src/DotMarc/Components/Pages/ManageGroups.razor`, add after the
existing `@page "/groups"` line:

```razor
@attribute [Authorize(Policy = "GroupsOrTagsWrite")]
```

Add `@using Microsoft.AspNetCore.Authorization` and
`@using Microsoft.AspNetCore.Components.Authorization` to the file's
`@using` block if not already present.

- [ ] **Step 2: Gate the Groups section's add form, rename field, and delete button**

Wrap the add-group `MudGrid` in `<AuthorizeView Policy="GroupsAdd">`. Wrap
the rename `MudTextField` in `<AuthorizeView Policy="GroupsRename">` with
a plain-text `<NotAuthorized>` fallback showing `context.Name`. Wrap the
delete `MudIconButton` in `<AuthorizeView Policy="GroupsDelete">`. Follow
the exact same wrapping pattern as Task 7 Steps 2 and 5 for the add form
and delete button; follow Task 7 Step 4's `Authorized`/`NotAuthorized`
pattern for the rename field, with the `NotAuthorized` branch simply
rendering `@context.Name` as plain text (no join/lookup needed here,
unlike the Groups/Tags multi-selects on Manage Domains).

- [ ] **Step 3: Gate the Tags section's add form, name/color fields, and delete button**

Same shape as Step 2, using `TagsAdd` for the add form, `TagsEdit` for
both the name `MudTextField` and the color `MudSelect` (both edit the
same `Tag` row, so both fall under the one `DomainsEdit`-equivalent
permission — `TagsEdit` — for Tags, rather than being split into two
separate permissions; the `Permission` enum from Task 1 deliberately has
one `TagsEdit`, not `TagsRename` + `TagsRecolor`), and `TagsDelete` for
the delete button. The `NotAuthorized` fallback for the name field shows
`@context.Name` as plain text; the `NotAuthorized` fallback for the color
field shows the existing read-only `MudChip` preview (the first column
already does this — reuse that same `<MudChip T="string" Color="context.Color">@context.Name</MudChip>`
markup as the fallback for the color cell too, rather than plain text,
since color genuinely needs a visual swatch to convey anything).

- [ ] **Step 4: Build to confirm it compiles**

Run: `dotnet build dotMARC.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test dotMARC.sln`
Expected: PASS.

- [ ] **Step 6: Manual verification**

If the environment allows it (same setup as Task 5's manual step): sign
in as someone with only `GroupsAdd` (no other Groups/Tags permissions)
and confirm the add-group form shows but existing rows render read-only,
with no delete buttons. If not possible in this environment, report which
steps were skipped and why.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Components/Pages/ManageGroups.razor
git commit -m "Gate Manage Groups by GroupsOrTagsWrite and per-control permissions"
```
