# Public API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A key-authenticated JSON API under `/api/v1` for reading domains, health, report summaries, groups, tags and alerts, and for adding/importing domains, setting groups, tags and monitoring, and acknowledging alerts, with a committed OpenAPI copy for the website.

**Architecture:** A new `ApiKey` authentication scheme turns a `dmk_` bearer key into the same `dotmarc:permission` and `dotmarc:scoped-group` claims a signed-in user gets, so per-permission policies and group scoping work unchanged. The `/api/v1` route group accepts only that scheme. Endpoints live in `src/DotMarc/Api/`, one static class per area, and writes go through the existing audited static services with an `ApiKey` audit actor.

**Tech Stack:** .NET 10 minimal APIs, EF Core 10 with Npgsql, ASP.NET built-in rate limiter, `Microsoft.AspNetCore.OpenApi` 10, MudBlazor 9.8, xUnit with the Testcontainers Postgres fixture and `WebApplicationFactory<Program>`.

**Spec:** `docs/superpowers/specs/2026-10-05-public-api-design.md`

## Global Constraints

- All endpoints under `/api/v1`, JSON, camelCase; enums serialized as strings.
- Key format: `dmk_` + 32 random bytes base64url. Stored as SHA-256 hex. Prefix shown in lists is the first 12 characters.
- Key lifetimes: exactly 30, 90, 180 or 365 days (default 90).
- A key never holds `AccessManage`: refused at creation, and dropped from claims at authentication.
- A scoped key (scopable role with groups) sees only domains in its groups; out-of-scope domain or alert is 404; add and import are 403.
- Health is read from stored check results; the API never runs DNS checks.
- Errors are RFC 7807 problem+json: 400 (with field errors), 401, 403, 404, 409, 429. Never a redirect.
- Rate limit: 120 requests per minute per key (config `Api:RequestsPerMinute`), fixed window, 429 with `Retry-After`.
- OpenAPI document at `/api/v1/openapi.json`, anonymous. Committed copy at `website/data/openapi/dotmarc-api.json`, sorted keys, 2-space indent, `info.version` = `VersionPrefix`.
- Expiry warning: 14 days, alert type `ApiKeyExpiring`, no ticket by default.
- New audit actions `api_key.created`, `api_key.revoked`; actor kind `ApiKey`, name `API key '<name>' (created by <creator>)`.
- UI and docs copy: no em dashes. Variable names meaningful, even in tests.
- Migrations: `dotnet dotnet-ef migrations add <Name> --project src/DotMarc --startup-project src/DotMarc`. Stop any running demo first (it locks `DotMarc.exe`).

## Review Focus

1. A key whose role later gains `AccessManage`: it must not get that permission (pinned by `AccessClaimsTests.AnApiKey_NeverGetsAccessManage` in Task 2).
2. Unknown paths under `/api/` with no key: must be a problem+json 404/401, not an Entra or demo redirect (pinned by `ApiAuthenticationTests.AnUnknownApiPath_IsAProblemNotARedirect` in Task 2).
3. Match-mode import where no row sends tags: existing tags must survive (pinned by `ImportEndpointTests.MatchMode_LeavesTagsAlone_WhenNoRowSendsTags` in Task 5).
4. A scoped key setting a domain's groups must not remove groups outside its scope (pinned by `DomainWriteEndpointTests.AScopedKey_SettingGroups_KeepsGroupsOutsideItsScope` in Task 4).
5. Malformed JSON bodies: 400 problem+json, not an empty 400 or a 500 (pinned by `DomainWriteEndpointTests.MalformedJson_IsAProblem` in Task 4).

---

## File Structure

**Create**
- `src/DotMarc/Data/ApiKey.cs`: the entity.
- `src/DotMarc/Data/ApiKeyManagementService.cs`: create, revoke, list (audited static service).
- `src/DotMarc/Security/ApiKeySecrets.cs`: generate, hash, display prefix.
- `src/DotMarc/Security/ApiKeyClaims.cs`: claim type constants for key principals.
- `src/DotMarc/Security/AccessClaims.cs`: builds permission and scoped-group claims for a role and groups (shared by users and keys).
- `src/DotMarc/Security/ApiKeyAuthenticationHandler.cs`: the `ApiKey` scheme.
- `src/DotMarc/Api/ApiOptions.cs`, `ApiPolicies.cs`, `ApiPermissionMetadata.cs`, `ApiProblems.cs`, `ApiScope.cs`, `ApiServices.cs`, `ApiEndpoints.cs`, `ApiModels.cs`: plumbing and DTOs.
- `src/DotMarc/Api/GroupAndTagEndpoints.cs`, `DomainReadEndpoints.cs`, `DomainWriteEndpoints.cs`, `ImportEndpoints.cs`, `AlertEndpoints.cs`: endpoints.
- `src/DotMarc/Api/ApiDocument.cs`: OpenAPI registration, transformers, examples.
- `src/DotMarc/Components/Shared/ApiKeysSection.razor`: the API keys tab's content.
- `scripts/update-openapi.mjs`, `website/data/openapi/dotmarc-api.json`, `website/docs/api.mdx`.
- Tests under `test/DotMarc.Tests/Api/` and `test/DotMarc.Tests/Data/ApiKeyManagementServiceTests.cs`, `test/DotMarc.Tests/Security/AccessClaimsTests.cs`.

**Modify**
- `src/DotMarc/Data/DotMarcDbContext.cs`, `Data/RoleManagementService.cs`, `Demo/DemoDataSeeder.cs`
- `src/DotMarc/Audit/AuditEntry.cs`, `AuditActor.cs`, `AuditActions.cs`, `AuditTarget.cs`
- `src/DotMarc/Security/UserAccessClaimsTransformation.cs`
- `src/DotMarc/Notifications/AlertTypes.cs`, `AlertingService.cs`
- `src/DotMarc/Components/Pages/ManageAccess.razor`
- `src/DotMarc/Program.cs`, `src/DotMarc/DotMarc.csproj`, `scripts/release.mjs`
- `test/DotMarc.Tests/Audit/AuditCoverageTests.cs`, `test/DotMarc.Tests/Data/RoleManagementServiceTests.cs`, `test/DotMarc.Tests/Audit/AuditActorTests.cs`

---

### Task 1: API keys: entity, secrets, management service, audit actor

**Files:**
- Create: `src/DotMarc/Data/ApiKey.cs`, `src/DotMarc/Data/ApiKeyManagementService.cs`, `src/DotMarc/Security/ApiKeySecrets.cs`, `src/DotMarc/Security/ApiKeyClaims.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`, `src/DotMarc/Data/RoleManagementService.cs`, `src/DotMarc/Demo/DemoDataSeeder.cs`, `src/DotMarc/Audit/AuditEntry.cs`, `src/DotMarc/Audit/AuditActor.cs`, `src/DotMarc/Audit/AuditActions.cs`, `src/DotMarc/Audit/AuditTarget.cs`, `src/DotMarc/Components/Pages/ManageAccess.razor`
- Migration: `src/DotMarc/Migrations/<timestamp>_AddApiKeys.cs`
- Test: `test/DotMarc.Tests/Data/ApiKeyManagementServiceTests.cs`, `test/DotMarc.Tests/Data/RoleManagementServiceTests.cs`, `test/DotMarc.Tests/Audit/AuditActorTests.cs`, `test/DotMarc.Tests/Audit/AuditCoverageTests.cs`

**Interfaces:**
- Produces:
  - `DotMarc.Data.ApiKey` with `Id, Name, Prefix, Hash, RoleId (int?), Role (Role?), ScopedGroups (List<Group>), CreatedBy, CreatedUtc, ExpiresUtc, LastUsedUtc, RevokedUtc, RevokedBy`, `bool IsActive(DateTimeOffset nowUtc)`.
  - `DbSet<ApiKey> ApiKeys`.
  - `ApiKeySecrets.Prefix` (`"dmk_"`), `string Generate()`, `string Hash(string secret)`, `string DisplayPrefix(string secret)`.
  - `ApiKeyClaims.IdClaimType` (`"dotmarc:api-key-id"`), `ApiKeyClaims.CreatedByClaimType` (`"dotmarc:api-key-created-by"`).
  - `ApiKeyManagementService.LifetimeDays` (`int[] {30, 90, 180, 365}`), `MaximumNameLength` (100), `CreateAsync(DotMarcDbContext, AuditActor, string rawName, int roleId, IReadOnlyList<int> groupIds, int lifetimeDays, CancellationToken) -> Task<CreateApiKeyResult>`, `RevokeAsync(DotMarcDbContext, AuditActor, int keyId, CancellationToken) -> Task<RevokeApiKeyResult>`, `ListAsync(DotMarcDbContext, CancellationToken) -> Task<List<ApiKey>>`.
  - `CreateApiKeyResult(ApiKey? Key, string? Secret, CreateApiKeyError? Error)`; `enum CreateApiKeyError { InvalidName, NameInUse, RoleNotFound, RoleCanManageAccess, InvalidLifetime }`; `enum RevokeApiKeyResult { Revoked, NotFound, AlreadyRevoked }`.
  - `AuditActorKind.ApiKey`; `AuditActor.ForApiKey(int keyId, string keyName, string createdBy)`; `AuditActor.FromPrincipal` returns the key actor for a principal carrying `ApiKeyClaims.IdClaimType`.
  - `AuditActions.ApiKeyCreated = "api_key.created"`, `AuditActions.ApiKeyRevoked = "api_key.revoked"`; `AuditTarget.For(ApiKey)`.

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Data/ApiKeyManagementServiceTests.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Security;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class ApiKeyManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ApiKeyManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<int> SeedRoleAsync(string name, bool isScopable, params Permission[] permissions)
    {
        await using var context = CreateContext();
        var role = new Role { Name = name, IsScopable = isScopable, Permissions = [.. permissions] };
        context.Roles.Add(role);
        await context.SaveChangesAsync();
        return role.Id;
    }

    private async Task<int> SeedGroupAsync(string name)
    {
        await using var context = CreateContext();
        var group = new Group { Name = name };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        return group.Id;
    }

    [Fact]
    public async Task Create_ReturnsTheSecretOnce_AndStoresOnlyItsHash()
    {
        var roleId = await SeedRoleAsync("Reader", isScopable: false, Permission.DomainsView);

        CreateApiKeyResult result;
        await using (var context = CreateContext())
        {
            result = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "  Halo sync  ", roleId, [], 90);
        }

        Assert.Null(result.Error);
        Assert.StartsWith("dmk_", result.Secret);
        await using var verify = CreateContext();
        var stored = await verify.ApiKeys.SingleAsync();
        Assert.Equal("Halo sync", stored.Name);
        Assert.Equal(ApiKeySecrets.Hash(result.Secret!), stored.Hash);
        Assert.Equal(result.Secret![..12], stored.Prefix);
        Assert.DoesNotContain(result.Secret, stored.Hash);
        Assert.Equal("Test Admin", stored.CreatedBy);
        Assert.Equal(90, (int)Math.Round((stored.ExpiresUtc - stored.CreatedUtc).TotalDays));
    }

    [Fact]
    public async Task Create_RefusesARoleThatCanManageAccess()
    {
        var roleId = await SeedRoleAsync("Admins", isScopable: false, Permission.DomainsView, Permission.AccessManage);
        await using var context = CreateContext();

        var result = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Too powerful", roleId, [], 90);

        Assert.Equal(CreateApiKeyError.RoleCanManageAccess, result.Error);
        Assert.False(await context.ApiKeys.AnyAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(366)]
    public async Task Create_RefusesALifetimeThatIsntOffered(int lifetimeDays)
    {
        var roleId = await SeedRoleAsync("Reader", isScopable: false, Permission.DomainsView);
        await using var context = CreateContext();

        var result = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Odd lifetime", roleId, [], lifetimeDays);

        Assert.Equal(CreateApiKeyError.InvalidLifetime, result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_RefusesABlankName(string name)
    {
        var roleId = await SeedRoleAsync("Reader", isScopable: false, Permission.DomainsView);
        await using var context = CreateContext();

        var result = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, name, roleId, [], 90);

        Assert.Equal(CreateApiKeyError.InvalidName, result.Error);
    }

    [Fact]
    public async Task Create_RefusesAnActiveNameInAnyCase_ButAllowsReusingARevokedName()
    {
        var roleId = await SeedRoleAsync("Reader", isScopable: false, Permission.DomainsView);
        await using var context = CreateContext();
        var first = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Reporting", roleId, [], 90);

        var duplicate = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "REPORTING", roleId, [], 90);
        await ApiKeyManagementService.RevokeAsync(context, TestActors.Admin, first.Key!.Id);
        var reused = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Reporting", roleId, [], 90);

        Assert.Equal(CreateApiKeyError.NameInUse, duplicate.Error);
        Assert.Null(reused.Error);
    }

    [Fact]
    public async Task Create_KeepsGroupsOnlyForAScopableRole()
    {
        var scopableRoleId = await SeedRoleAsync("Client viewer", isScopable: true, Permission.DomainsView);
        var plainRoleId = await SeedRoleAsync("Reader", isScopable: false, Permission.DomainsView);
        var groupId = await SeedGroupAsync("Contoso");
        await using var context = CreateContext();

        var scoped = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Scoped", scopableRoleId, [groupId], 90);
        var unscoped = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Unscoped", plainRoleId, [groupId], 90);

        await using var verify = CreateContext();
        var keys = await verify.ApiKeys.Include(key => key.ScopedGroups).ToDictionaryAsync(key => key.Id);
        Assert.Equal([groupId], keys[scoped.Key!.Id].ScopedGroups.Select(group => group.Id));
        Assert.Empty(keys[unscoped.Key!.Id].ScopedGroups);
    }

    [Fact]
    public async Task CreateAndRevoke_AreAudited()
    {
        var roleId = await SeedRoleAsync("Reader", isScopable: false, Permission.DomainsView);
        await using var context = CreateContext();
        var created = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Halo sync", roleId, [], 30);

        var revoked = await ApiKeyManagementService.RevokeAsync(context, TestActors.Admin, created.Key!.Id);
        var revokedAgain = await ApiKeyManagementService.RevokeAsync(context, TestActors.Admin, created.Key.Id);

        Assert.Equal(RevokeApiKeyResult.Revoked, revoked);
        Assert.Equal(RevokeApiKeyResult.AlreadyRevoked, revokedAgain);
        await using var verify = CreateContext();
        var key = await verify.ApiKeys.SingleAsync();
        Assert.NotNull(key.RevokedUtc);
        Assert.Equal("Test Admin", key.RevokedBy);
        var actions = await verify.AuditEntries.OrderBy(entry => entry.Id).Select(entry => entry.Action).ToListAsync();
        Assert.Equal([AuditActions.ApiKeyCreated, AuditActions.ApiKeyRevoked], actions);
        Assert.All(await verify.AuditEntries.ToListAsync(), entry => Assert.Equal("ApiKey", entry.TargetType));
    }

    [Fact]
    public async Task Revoke_AnUnknownKey_IsNotFound()
    {
        await using var context = CreateContext();

        Assert.Equal(RevokeApiKeyResult.NotFound, await ApiKeyManagementService.RevokeAsync(context, TestActors.Admin, 999));
    }
}
```

Append to `test/DotMarc.Tests/Data/RoleManagementServiceTests.cs` (inside the existing class; it already has a `CreateContext()` helper and the Postgres lifecycle; if a helper name differs, use the class's own):

```csharp
    [Fact]
    public async Task RemoveRole_IsRefused_WhileAnUnrevokedApiKeyUsesIt_AndAllowedOnceRevoked()
    {
        int roleId;
        int keyId;
        await using (var context = CreateContext())
        {
            var role = new Role { Name = "Integration", Permissions = [Permission.DomainsView] };
            context.Roles.Add(role);
            await context.SaveChangesAsync();
            roleId = role.Id;
            keyId = (await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Integration key", roleId, [], 90)).Key!.Id;
        }

        await using (var context = CreateContext())
        {
            Assert.Equal(RoleManagementService.RemoveRoleResult.InUse, await RoleManagementService.RemoveRoleAsync(context, TestActors.Admin, roleId));
            await ApiKeyManagementService.RevokeAsync(context, TestActors.Admin, keyId);
        }

        await using (var context = CreateContext())
        {
            Assert.Equal(RoleManagementService.RemoveRoleResult.Removed, await RoleManagementService.RemoveRoleAsync(context, TestActors.Admin, roleId));
        }

        await using var verify = CreateContext();
        Assert.Null((await verify.ApiKeys.SingleAsync()).RoleId);
    }
```

Append to `test/DotMarc.Tests/Audit/AuditActorTests.cs` (inside the existing class; add `using System.Security.Claims;` and `using DotMarc.Security;` if missing):

```csharp
    [Fact]
    public void FromPrincipal_ForAnApiKey_NamesTheKeyAndItsCreator()
    {
        var identity = new ClaimsIdentity("ApiKey", ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, "Halo sync"));
        identity.AddClaim(new Claim(ApiKeyClaims.IdClaimType, "7"));
        identity.AddClaim(new Claim(ApiKeyClaims.CreatedByClaimType, "Jo Smith"));

        var actor = AuditActor.FromPrincipal(new ClaimsPrincipal(identity));

        Assert.Equal(AuditActorKind.ApiKey, actor.Kind);
        Assert.Equal("API key 'Halo sync' (created by Jo Smith)", actor.Name);
        Assert.Equal("api-key:7", actor.ObjectId);
        Assert.Null(actor.Email);
    }
```

In `test/DotMarc.Tests/Audit/AuditCoverageTests.cs`, add `typeof(ApiKeyManagementService)` to `AuditedServices` (after `typeof(UserAccessManagementService)`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ApiKeyManagementServiceTests|FullyQualifiedName~RoleManagementServiceTests|FullyQualifiedName~AuditActorTests|FullyQualifiedName~AuditCoverageTests"`
Expected: build FAIL with errors naming `ApiKeyManagementService`, `ApiKeySecrets`, `ApiKeyClaims`, `AuditActorKind.ApiKey`.

- [ ] **Step 3: Write the entity, secrets and claim types**

`src/DotMarc/Data/ApiKey.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>A key that lets a tool call the public API with a role's permissions, optionally limited to some groups
/// when the role is scopable. Only a hash of the secret is stored; the secret itself is shown once, when the key is
/// created. A key is never edited: changing what it can do means creating a new key and revoking this one, so its
/// power never silently grows. RoleId becomes null only when a role is deleted after every key using it was revoked.</summary>
public sealed class ApiKey
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>The first 12 characters of the secret ("dmk_" and 8 more), shown in lists to tell keys apart.</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the full secret, as lowercase hex.</summary>
    public required string Hash { get; set; }

    public int? RoleId { get; set; }
    public Role? Role { get; set; }
    public List<Group> ScopedGroups { get; set; } = [];

    /// <summary>The creator's display name at the time, so the key stays attributable after their grant goes.</summary>
    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }
    public DateTimeOffset? RevokedUtc { get; set; }
    public string? RevokedBy { get; set; }

    public bool IsActive(DateTimeOffset nowUtc) => RevokedUtc is null && ExpiresUtc > nowUtc;
}
```

`src/DotMarc/Security/ApiKeySecrets.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace DotMarc.Security;

/// <summary>Makes and hashes API key secrets. A secret carries 256 random bits, so a plain SHA-256 is enough to store
/// it: there is nothing to brute-force that a salt or a slow hash would protect.</summary>
public static class ApiKeySecrets
{
    public const string Prefix = "dmk_";
    private const int DisplayPrefixLength = 12;

    public static string Generate() => Prefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static string DisplayPrefix(string secret) => secret[..Math.Min(DisplayPrefixLength, secret.Length)];

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

`src/DotMarc/Security/ApiKeyClaims.cs`:

```csharp
namespace DotMarc.Security;

/// <summary>Claims that mark a principal as an API key rather than a person. The key's name is the principal's name
/// claim.</summary>
public static class ApiKeyClaims
{
    public const string IdClaimType = "dotmarc:api-key-id";
    public const string CreatedByClaimType = "dotmarc:api-key-created-by";
}
```

- [ ] **Step 4: Configure the model and add the migration**

In `DotMarcDbContext.cs` add the set after `UserAccesses`:

```csharp
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
```

and in `OnModelCreating`, after the `UserAccess` block:

```csharp
        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.Property(key => key.Name).HasMaxLength(ApiKeyManagementService.MaximumNameLength);
            entity.Property(key => key.Prefix).HasMaxLength(16);
            entity.Property(key => key.Hash).HasMaxLength(64);
            entity.Property(key => key.CreatedBy).HasMaxLength(256);
            entity.Property(key => key.RevokedBy).HasMaxLength(256);
            entity.HasIndex(key => key.Hash).IsUnique();
            // Unique among keys still in use; a revoked key's name can be reused.
            entity.HasIndex(key => key.Name).IsUnique().HasFilter("\"RevokedUtc\" IS NULL");
            // Keys still in use block deleting their role (RoleManagementService checks first); revoked keys keep their
            // row for the record and lose the role.
            entity.HasOne(key => key.Role).WithMany().HasForeignKey(key => key.RoleId).OnDelete(DeleteBehavior.SetNull);
            // Group has no navigation back to ApiKey, so the join table is configured explicitly, as for UserAccess.
            entity.HasMany(key => key.ScopedGroups).WithMany().UsingEntity("ApiKeyScopedGroups");
        });
```

In `Demo/DemoDataSeeder.cs`, add `"ApiKeys", "ApiKeyScopedGroups",` to the `TRUNCATE TABLE` list (after `"UserAccessScopedGroups",`).

Run: `dotnet dotnet-ef migrations add AddApiKeys --project src/DotMarc --startup-project src/DotMarc`
Expected: a new migration creating `ApiKeys` (with `RoleId` nullable, FK `ON DELETE SET NULL`, unique `Hash`, filtered unique `Name`) and `ApiKeyScopedGroups`. Read the generated file and confirm nothing else changed.

- [ ] **Step 5: Add the audit pieces**

`Audit/AuditEntry.cs`: replace the `AuditActorKind` doc comment and enum with:

```csharp
/// <summary>Who did something: a person, dotMARC itself, or an API key.</summary>
public enum AuditActorKind { User, System, ApiKey }
```

`Audit/AuditActor.cs`: add `ForApiKey` and change `FromPrincipal`:

```csharp
    /// <summary>A change made through the public API. The creator is named so the entry says whose integration it was.</summary>
    public static AuditActor ForApiKey(int keyId, string keyName, string createdBy) =>
        new(AuditActorKind.ApiKey, $"API key '{keyName}' (created by {createdBy})", $"api-key:{keyId.ToString(CultureInfo.InvariantCulture)}");

    public static AuditActor FromPrincipal(ClaimsPrincipal principal) =>
        principal.FindFirst(ApiKeyClaims.IdClaimType) is { } keyIdClaim
            ? ForApiKey(int.Parse(keyIdClaim.Value, CultureInfo.InvariantCulture), principal.Identity?.Name ?? "",
                principal.FindFirst(ApiKeyClaims.CreatedByClaimType)?.Value ?? "unknown")
            : ForUser(principal.GetObjectId(), UserClaims.GetEmail(principal), principal.Identity?.Name);
```

(add `using System.Globalization;`).

`Audit/AuditActions.cs`: add constants after `AccessRevoked`:

```csharp
    public const string ApiKeyCreated = "api_key.created";
    public const string ApiKeyRevoked = "api_key.revoked";
```

and entries in `All` after `(AccessRevoked, "Access revoked"),`:

```csharp
        (ApiKeyCreated, "API key created"),
        (ApiKeyRevoked, "API key revoked"),
```

`Audit/AuditTarget.cs`: after `For(UserAccess access)`:

```csharp
    public static AuditTarget For(ApiKey key) => new("ApiKey", IdText(key.Id), key.Name);
```

- [ ] **Step 6: Write the management service**

`src/DotMarc/Data/ApiKeyManagementService.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

public enum CreateApiKeyError { InvalidName, NameInUse, RoleNotFound, RoleCanManageAccess, InvalidLifetime }

public enum RevokeApiKeyResult { Revoked, NotFound, AlreadyRevoked }

/// <summary>The new key and its secret, which is never available again; or why it wasn't created.</summary>
public sealed record CreateApiKeyResult(ApiKey? Key, string? Secret, CreateApiKeyError? Error)
{
    public static CreateApiKeyResult Refused(CreateApiKeyError error) => new(null, null, error);
}

/// <summary>Creates, revokes and lists API keys from the Access page. Follows the project's convention of a static
/// class working on a caller-supplied context, with every change audited.</summary>
public static class ApiKeyManagementService
{
    public const int MaximumNameLength = 100;
    public static readonly int[] LifetimeDays = [30, 90, 180, 365];

    public static async Task<CreateApiKeyResult> CreateAsync(DotMarcDbContext context, AuditActor actor, string rawName, int roleId,
        IReadOnlyList<int> groupIds, int lifetimeDays, CancellationToken cancellationToken = default)
    {
        var name = rawName.Trim();
        if (name.Length is 0 or > MaximumNameLength)
        {
            return CreateApiKeyResult.Refused(CreateApiKeyError.InvalidName);
        }

        if (!LifetimeDays.Contains(lifetimeDays))
        {
            return CreateApiKeyResult.Refused(CreateApiKeyError.InvalidLifetime);
        }

        var role = await context.Roles.SingleOrDefaultAsync(candidate => candidate.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return CreateApiKeyResult.Refused(CreateApiKeyError.RoleNotFound);
        }

        // Keys can't mint keys or change who has access.
        if (role.Permissions.Contains(Permission.AccessManage))
        {
            return CreateApiKeyResult.Refused(CreateApiKeyError.RoleCanManageAccess);
        }

        var loweredName = name.ToLower();
        if (await context.ApiKeys.AnyAsync(key => key.RevokedUtc == null && key.Name.ToLower() == loweredName, cancellationToken).ConfigureAwait(false))
        {
            return CreateApiKeyResult.Refused(CreateApiKeyError.NameInUse);
        }

        var groups = role.IsScopable
            ? await context.Groups.Where(group => groupIds.Contains(group.Id)).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];
        var secret = ApiKeySecrets.Generate();
        var nowUtc = DateTimeOffset.UtcNow;
        var apiKey = new ApiKey
        {
            Name = name,
            Prefix = ApiKeySecrets.DisplayPrefix(secret),
            Hash = ApiKeySecrets.Hash(secret),
            RoleId = role.Id,
            ScopedGroups = groups,
            CreatedBy = actor.Name,
            CreatedUtc = nowUtc,
            ExpiresUtc = nowUtc.AddDays(lifetimeDays),
        };
        context.ApiKeys.Add(apiKey);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.ApiKeyCreated, AuditTarget.For(apiKey), $"Created API key {name} with the {role.Name} role",
                    new AuditChanges()
                        .Field("Role", (string?)null, role.Name)
                        .Set("Groups", [], groups.Select(group => group.Name))
                        .Field("Expires", (string?)null, apiKey.ExpiresUtc.ToString("yyyy-MM-dd"))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            // Another key with this name was created between the check above and this save.
            return CreateApiKeyResult.Refused(CreateApiKeyError.NameInUse);
        }

        return new CreateApiKeyResult(apiKey, secret, null);
    }

    public static async Task<RevokeApiKeyResult> RevokeAsync(DotMarcDbContext context, AuditActor actor, int keyId, CancellationToken cancellationToken = default)
    {
        var apiKey = await context.ApiKeys.SingleOrDefaultAsync(key => key.Id == keyId, cancellationToken).ConfigureAwait(false);
        if (apiKey is null)
        {
            return RevokeApiKeyResult.NotFound;
        }

        if (apiKey.RevokedUtc is not null)
        {
            return RevokeApiKeyResult.AlreadyRevoked;
        }

        apiKey.RevokedUtc = DateTimeOffset.UtcNow;
        apiKey.RevokedBy = actor.Name;
        AuditLog.Record(context, actor, AuditActions.ApiKeyRevoked, AuditTarget.For(apiKey), $"Revoked API key {apiKey.Name}");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return RevokeApiKeyResult.Revoked;
    }

    /// <summary>Keys in use first, then revoked ones, each by name.</summary>
    public static Task<List<ApiKey>> ListAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.ApiKeys
            .AsNoTracking()
            .Include(key => key.Role)
            .Include(key => key.ScopedGroups)
            .OrderBy(key => key.RevokedUtc != null)
            .ThenBy(key => key.Name)
            .ToListAsync(cancellationToken);
}
```

If `AuditChanges.Set` doesn't accept a collection expression `[]` for its old values, pass `Array.Empty<string>()` (as `UserAccessManagementService.GrantAccessAsync` does).

- [ ] **Step 7: Block deleting a role that active keys use**

In `RoleManagementService.RemoveRoleAsync`, replace the `inUse` line with:

```csharp
        var inUse = await context.UserAccesses.AnyAsync(u => u.RoleId == roleId, cancellationToken).ConfigureAwait(false)
            || await context.ApiKeys.AnyAsync(key => key.RoleId == roleId && key.RevokedUtc == null, cancellationToken).ConfigureAwait(false);
```

In `ManageAccess.razor`, change the InUse snackbar text to:

```razor
                Snackbar.Add($"Can't remove {row.Name}. It's still granted to at least one person or API key. Revoke or reassign those first.", Severity.Error);
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ApiKeyManagementServiceTests|FullyQualifiedName~RoleManagementServiceTests|FullyQualifiedName~AuditActorTests|FullyQualifiedName~AuditCoverageTests|FullyQualifiedName~Demo"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc test/DotMarc.Tests
git commit -m "Add API keys: storage, creation, revocation and an API key audit actor"
```

---

### Task 2: API key authentication and the API's plumbing, with group and tag endpoints

**Files:**
- Create: `src/DotMarc/Security/AccessClaims.cs`, `src/DotMarc/Security/ApiKeyAuthenticationHandler.cs`, `src/DotMarc/Api/ApiOptions.cs`, `src/DotMarc/Api/ApiPolicies.cs`, `src/DotMarc/Api/ApiPermissionMetadata.cs`, `src/DotMarc/Api/ApiProblems.cs`, `src/DotMarc/Api/ApiScope.cs`, `src/DotMarc/Api/ApiServices.cs`, `src/DotMarc/Api/ApiEndpoints.cs`, `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Api/GroupAndTagEndpoints.cs`
- Modify: `src/DotMarc/Security/UserAccessClaimsTransformation.cs`, `src/DotMarc/Program.cs`
- Test: `test/DotMarc.Tests/Security/AccessClaimsTests.cs`, `test/DotMarc.Tests/Api/ApiTestHost.cs`, `test/DotMarc.Tests/Api/ApiAuthenticationTests.cs`, `test/DotMarc.Tests/Api/GroupAndTagEndpointTests.cs`

**Interfaces:**
- Consumes: Task 1's `ApiKey`, `ApiKeySecrets`, `ApiKeyClaims`, `AuditActor.FromPrincipal`.
- Produces:
  - `AccessClaims.For(Role role, IEnumerable<int> scopedGroupIds, bool forApiKey = false) -> IEnumerable<Claim>`.
  - `ApiKeyAuthenticationHandler.SchemeName` (`"ApiKey"`).
  - `ApiPolicies.For(Permission) -> string`, `ApiPolicies.AnyKey`, `ApiPolicies.Add(AuthorizationOptions)`.
  - `RouteHandlerBuilder.RequirePermission(Permission)` (extension in `ApiEndpointConventions`), `ApiPermissionMetadata(Permission Permission)`.
  - `ApiProblems.NotFound(string what)`, `ApiProblems.Forbidden(string detail)`, `ApiProblems.Conflict(string detail)`, `ApiProblems.Validation(string field, string message)`, `ApiProblems.WriteAsync(HttpContext, int status, string title, string detail)`.
  - `ApiScope.From(ClaimsPrincipal)`, `.IsScoped`, `.Includes(int groupId)`, `.Domains(IQueryable<Domain>)`, `.Groups(IQueryable<Group>)`.
  - `ApiEndpoints.MapDotMarcApi(this WebApplication) -> RouteGroupBuilder`, `ApiEndpoints.RateLimiterPolicy`.
  - DTOs: `ApiNamedRef(int Id, string Name)`, `ApiPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)`, `ApiGroup(int Id, string Name, int DomainCount)`, `ApiTag(int Id, string Name, string Color, int DomainCount)`.
  - Test helper `ApiTestHost` (see code).

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Security/AccessClaimsTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Security;
using Xunit;

namespace DotMarc.Tests.Security;

public sealed class AccessClaimsTests
{
    [Fact]
    public void AUser_GetsEveryPermissionAndTheirGroups()
    {
        var role = new Role { Name = "Viewer", IsScopable = true, Permissions = [Permission.DomainsView, Permission.AccessManage] };

        var claims = AccessClaims.For(role, [4, 9]).ToList();

        Assert.Equal(["DomainsView", "AccessManage"],
            claims.Where(claim => claim.Type == UserAccessClaimsTransformation.PermissionClaimType).Select(claim => claim.Value));
        Assert.Equal(["4", "9"],
            claims.Where(claim => claim.Type == UserAccessClaimsTransformation.ScopedGroupClaimType).Select(claim => claim.Value));
    }

    [Fact]
    public void AnApiKey_NeverGetsAccessManage()
    {
        var role = new Role { Name = "Grew too much", Permissions = [Permission.DomainsView, Permission.AccessManage] };

        var permissions = AccessClaims.For(role, [], forApiKey: true)
            .Where(claim => claim.Type == UserAccessClaimsTransformation.PermissionClaimType)
            .Select(claim => claim.Value);

        Assert.Equal(["DomainsView"], permissions);
    }

    [Fact]
    public void GroupsAreIgnored_ForARoleThatIsntScopable()
    {
        var role = new Role { Name = "Editor", IsScopable = false, Permissions = [Permission.DomainsEdit] };

        var claims = AccessClaims.For(role, [4]);

        Assert.DoesNotContain(claims, claim => claim.Type == UserAccessClaimsTransformation.ScopedGroupClaimType);
    }
}
```

`test/DotMarc.Tests/Api/ApiTestHost.cs`:

```csharp
using System.Net.Http.Headers;
using DotMarc.Data;
using DotMarc.Security;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using MudBlazor;

namespace DotMarc.Tests.Api;

/// <summary>Boots the app against its own database for API tests. Demo mode stands in for Entra (as in the Halo
/// webhook tests); the host is booted before anything is seeded because demo start-up resets the data. Names seeded
/// here are unique so the demo dataset never collides with them.</summary>
internal sealed class ApiTestHost : IAsyncDisposable
{
    private readonly IAsyncDisposable _databaseCleanup;

    private ApiTestHost(string connectionString, IAsyncDisposable databaseCleanup, WebApplicationFactory<Program> factory)
    {
        ConnectionString = connectionString;
        _databaseCleanup = databaseCleanup;
        Factory = factory;
    }

    public string ConnectionString { get; }
    public WebApplicationFactory<Program> Factory { get; }

    public static async Task<ApiTestHost> StartAsync(PostgresContainerFixture fixture, IReadOnlyDictionary<string, string>? settings = null)
    {
        var (connectionString, databaseCleanup) = await fixture.CreateDatabaseAsync();
        await using (var context = CreateContext(connectionString))
        {
            await context.Database.MigrateAsync();
        }

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", connectionString);
            builder.UseSetting("Demo:Enabled", "true");
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }
        });
        factory.CreateClient().Dispose();
        return new ApiTestHost(connectionString, databaseCleanup, factory);
    }

    public DotMarcDbContext CreateContext() => CreateContext(ConnectionString);

    private static DotMarcDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);

    /// <summary>Inserts a key directly (not through the service) so tests can make expired, revoked and AccessManage
    /// keys. With <paramref name="scopedGroupIds"/> the key's role is scopable and limited to those groups.</summary>
    public async Task<(int KeyId, string Secret)> CreateKeyAsync(IReadOnlyList<Permission> permissions, IReadOnlyList<int>? scopedGroupIds = null,
        DateTimeOffset? expiresUtc = null, bool revoked = false, string name = "test key")
    {
        var secret = ApiKeySecrets.Generate();
        var nowUtc = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        var groups = scopedGroupIds is null ? [] : await context.Groups.Where(group => scopedGroupIds.Contains(group.Id)).ToListAsync();
        var apiKey = new ApiKey
        {
            Name = $"{name} {Guid.NewGuid():N}",
            Prefix = ApiKeySecrets.DisplayPrefix(secret),
            Hash = ApiKeySecrets.Hash(secret),
            Role = new Role { Name = $"role {Guid.NewGuid():N}", IsScopable = scopedGroupIds is not null, Permissions = [.. permissions] },
            ScopedGroups = groups,
            CreatedBy = "Test Admin",
            CreatedUtc = nowUtc,
            ExpiresUtc = expiresUtc ?? nowUtc.AddDays(90),
            RevokedUtc = revoked ? nowUtc : null,
        };
        context.ApiKeys.Add(apiKey);
        await context.SaveChangesAsync();
        return (apiKey.Id, secret);
    }

    public HttpClient ClientFor(string? secret)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (secret is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        return client;
    }

    public async Task<int> SeedGroupAsync(string name)
    {
        await using var context = CreateContext();
        var group = new Group { Name = name };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        return group.Id;
    }

    public async Task<int> SeedTagAsync(string name)
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = name, Color = Color.Primary };
        context.Tags.Add(tag);
        await context.SaveChangesAsync();
        return tag.Id;
    }

    public async Task<int> SeedDomainAsync(string name, IReadOnlyList<int>? groupIds = null, IReadOnlyList<int>? tagIds = null,
        bool monitored = true, Action<Domain>? configure = null)
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = name, IsMonitored = monitored, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-10) };
        domain.Groups = groupIds is null ? [] : await context.Groups.Where(group => groupIds.Contains(group.Id)).ToListAsync();
        domain.Tags = tagIds is null ? [] : await context.Tags.Where(tag => tagIds.Contains(tag.Id)).ToListAsync();
        configure?.Invoke(domain);
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return domain.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _databaseCleanup.DisposeAsync();
    }
}
```

If `Tag.Color` isn't MudBlazor's `Color`, use whatever type `Tag.Color` has and its first value.

`test/DotMarc.Tests/Api/ApiAuthenticationTests.cs`:

```csharp
using System.Net;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class ApiAuthenticationTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ApiAuthenticationTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static void AssertProblem(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task AValidKey_CanCallTheApi()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/groups");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NoKey_IsAProblem401_NotARedirect()
    {
        using var client = _host.ClientFor(null);

        var response = await client.GetAsync("/api/v1/groups");

        AssertProblem(response, HttpStatusCode.Unauthorized);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task AnUnknownKey_Is401()
    {
        using var client = _host.ClientFor("dmk_thisisnotarealkeythisisnotarealkeyxx");

        AssertProblem(await client.GetAsync("/api/v1/groups"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnExpiredKey_Is401()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView], expiresUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        using var client = _host.ClientFor(secret);

        AssertProblem(await client.GetAsync("/api/v1/groups"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARevokedKey_Is401()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView], revoked: true);
        using var client = _host.ClientFor(secret);

        AssertProblem(await client.GetAsync("/api/v1/groups"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AKeyWithoutThePermission_Is403()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.TagsView]);
        using var client = _host.ClientFor(secret);

        AssertProblem(await client.GetAsync("/api/v1/groups"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ASignedInBrowserCookie_DoesNotAuthenticateTheApi()
    {
        using var client = _host.ClientFor(null);
        await client.PostAsync("/demo/sign-in/admin", content: null);

        AssertProblem(await client.GetAsync("/api/v1/groups"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnUnknownApiPath_IsAProblemNotARedirect()
    {
        using var client = _host.ClientFor(null);

        AssertProblem(await client.GetAsync("/api/v1/nothing-here"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LastUsed_IsRecorded_AndNotRewrittenWithinAMinute()
    {
        var (keyId, secret) = await _host.CreateKeyAsync([Permission.GroupsView]);
        using var client = _host.ClientFor(secret);

        await client.GetAsync("/api/v1/groups");
        DateTimeOffset? firstUse;
        await using (var context = _host.CreateContext())
        {
            firstUse = (await context.ApiKeys.SingleAsync(key => key.Id == keyId)).LastUsedUtc;
        }

        await client.GetAsync("/api/v1/groups");
        await using var verify = _host.CreateContext();

        Assert.NotNull(firstUse);
        Assert.Equal(firstUse, (await verify.ApiKeys.SingleAsync(key => key.Id == keyId)).LastUsedUtc);
    }
}

[Collection("Postgres")]
public sealed class ApiRateLimitTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ApiRateLimitTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() =>
        _host = await ApiTestHost.StartAsync(_fixture, new Dictionary<string, string> { ["Api:RequestsPerMinute"] = "3" });

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task OverTheLimit_Is429WithRetryAfter_AndOtherKeysAreUnaffected()
    {
        var (_, busySecret) = await _host.CreateKeyAsync([Permission.GroupsView]);
        var (_, quietSecret) = await _host.CreateKeyAsync([Permission.GroupsView]);
        using var busyClient = _host.ClientFor(busySecret);
        using var quietClient = _host.ClientFor(quietSecret);

        for (var request = 0; request < 3; request++)
        {
            Assert.Equal(HttpStatusCode.OK, (await busyClient.GetAsync("/api/v1/groups")).StatusCode);
        }

        var limited = await busyClient.GetAsync("/api/v1/groups");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await quietClient.GetAsync("/api/v1/groups")).StatusCode);
    }
}
```

`test/DotMarc.Tests/Api/GroupAndTagEndpointTests.cs`:

```csharp
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class GroupAndTagEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public GroupAndTagEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Groups_ListsEveryGroupWithItsDomainCount()
    {
        var groupId = await _host.SeedGroupAsync("api-groups-a");
        await _host.SeedDomainAsync("api-groups-a1.example", groupIds: [groupId]);
        await _host.SeedDomainAsync("api-groups-a2.example", groupIds: [groupId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView]);
        using var client = _host.ClientFor(secret);

        var groups = await client.GetFromJsonAsync<List<ApiGroup>>("/api/v1/groups");

        Assert.Contains(new ApiGroup(groupId, "api-groups-a", 2), groups!);
    }

    [Fact]
    public async Task AScopedKey_SeesOnlyItsGroups()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-groups-own");
        await _host.SeedGroupAsync("api-groups-other");
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView], scopedGroupIds: [ownGroupId]);
        using var client = _host.ClientFor(secret);

        var groups = await client.GetFromJsonAsync<List<ApiGroup>>("/api/v1/groups");

        Assert.Equal([ownGroupId], groups!.Select(group => group.Id));
    }

    [Fact]
    public async Task Tags_CountOnlyDomainsInAScopedKeysGroups()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-tags-own");
        var otherGroupId = await _host.SeedGroupAsync("api-tags-other");
        var tagId = await _host.SeedTagAsync("api-tags-shared");
        await _host.SeedDomainAsync("api-tags-own.example", groupIds: [ownGroupId], tagIds: [tagId]);
        await _host.SeedDomainAsync("api-tags-other.example", groupIds: [otherGroupId], tagIds: [tagId]);
        var (_, scopedSecret) = await _host.CreateKeyAsync([Permission.TagsView], scopedGroupIds: [ownGroupId]);
        var (_, unscopedSecret) = await _host.CreateKeyAsync([Permission.TagsView]);
        using var scopedClient = _host.ClientFor(scopedSecret);
        using var unscopedClient = _host.ClientFor(unscopedSecret);

        var scopedTag = (await scopedClient.GetFromJsonAsync<List<ApiTag>>("/api/v1/tags"))!.Single(tag => tag.Id == tagId);
        var unscopedTag = (await unscopedClient.GetFromJsonAsync<List<ApiTag>>("/api/v1/tags"))!.Single(tag => tag.Id == tagId);

        Assert.Equal(1, scopedTag.DomainCount);
        Assert.Equal(2, unscopedTag.DomainCount);
        Assert.Equal("Primary", unscopedTag.Color);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AccessClaimsTests|FullyQualifiedName~DotMarc.Tests.Api"`
Expected: build FAIL naming `AccessClaims`, `ApiGroup`, `ApiTag`.

- [ ] **Step 3: Shared claim building**

`src/DotMarc/Security/AccessClaims.cs`:

```csharp
using System.Globalization;
using System.Security.Claims;
using DotMarc.Data;

namespace DotMarc.Security;

/// <summary>The permission and scoped-group claims for a role and its groups, built the same way for a signed-in
/// person and an API key so the two can never be authorised differently. Groups count only on a scopable role. An API
/// key never gets AccessManage, even if its role gains it after the key was made.</summary>
public static class AccessClaims
{
    public static IEnumerable<Claim> For(Role role, IEnumerable<int> scopedGroupIds, bool forApiKey = false)
    {
        foreach (var permission in role.Permissions)
        {
            if (forApiKey && permission == Permission.AccessManage)
            {
                continue;
            }

            yield return new Claim(UserAccessClaimsTransformation.PermissionClaimType, permission.ToString());
        }

        if (!role.IsScopable)
        {
            yield break;
        }

        foreach (var groupId in scopedGroupIds)
        {
            yield return new Claim(UserAccessClaimsTransformation.ScopedGroupClaimType, groupId.ToString(CultureInfo.InvariantCulture));
        }
    }
}
```

In `UserAccessClaimsTransformation.TransformAsync`, replace the two `foreach` loops with:

```csharp
        identity.AddClaims(AccessClaims.For(access.Role, access.ScopedGroups.Select(group => group.Id)));
```

- [ ] **Step 4: The authentication handler**

`src/DotMarc/Security/ApiKeyAuthenticationHandler.cs`:

```csharp
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DotMarc.Api;
using DotMarc.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotMarc.Security;

/// <summary>Authenticates "Authorization: Bearer dmk_..." for the public API, giving the key the same permission and
/// scoped-group claims a person with its role and groups would get. Challenges and denials are problem+json, never a
/// redirect. Only the /api/v1 policies name this scheme, so a browser's cookie never reaches the API and a key never
/// reaches the UI.</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    private const string BearerPrefix = "Bearer ";
    private static readonly TimeSpan LastUsedPrecision = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var secret = header[BearerPrefix.Length..].Trim();
        if (!secret.StartsWith(ApiKeySecrets.Prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = ApiKeySecrets.Hash(secret);
        await using var context = await dbFactory.CreateDbContextAsync(Context.RequestAborted).ConfigureAwait(false);
        var apiKey = await context.ApiKeys
            .AsNoTracking()
            .Include(key => key.Role)
            .Include(key => key.ScopedGroups)
            .SingleOrDefaultAsync(key => key.Hash == hash, Context.RequestAborted)
            .ConfigureAwait(false);
        var nowUtc = timeProvider.GetUtcNow();
        if (apiKey?.Role is null || !apiKey.IsActive(nowUtc))
        {
            return AuthenticateResult.Fail("The API key is unknown, expired or revoked.");
        }

        // At most one write a minute per key, rather than one per request.
        if (apiKey.LastUsedUtc is null || nowUtc - apiKey.LastUsedUtc >= LastUsedPrecision)
        {
            await context.ApiKeys
                .Where(key => key.Id == apiKey.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(key => key.LastUsedUtc, nowUtc), Context.RequestAborted)
                .ConfigureAwait(false);
        }

        var identity = new ClaimsIdentity(SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, apiKey.Name));
        identity.AddClaim(new Claim(ApiKeyClaims.IdClaimType, apiKey.Id.ToString(CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim(ApiKeyClaims.CreatedByClaimType, apiKey.CreatedBy));
        identity.AddClaims(AccessClaims.For(apiKey.Role, apiKey.ScopedGroups.Select(group => group.Id), forApiKey: true));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return ApiProblems.WriteAsync(Context, StatusCodes.Status401Unauthorized, "API key required",
            "Send a valid, unexpired API key as 'Authorization: Bearer dmk_...'. Keys are created on dotMARC's Access page.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiProblems.WriteAsync(Context, StatusCodes.Status403Forbidden, "Not allowed", "This API key's role doesn't include the permission this needs.");
}
```

- [ ] **Step 5: API plumbing**

`src/DotMarc/Api/ApiOptions.cs`:

```csharp
namespace DotMarc.Api;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>Requests each key may make per minute.</summary>
    public int RequestsPerMinute { get; set; } = 120;
}
```

`src/DotMarc/Api/ApiPolicies.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Security;
using Microsoft.AspNetCore.Authorization;

namespace DotMarc.Api;

/// <summary>The API's own copy of the permission policies, naming only the ApiKey scheme. The UI's policies use the
/// default (cookie) scheme, so each side accepts only its own kind of caller.</summary>
public static class ApiPolicies
{
    public const string AnyKey = "Api.AnyKey";

    public static string For(Permission permission) => $"Api.{permission}";

    public static void Add(AuthorizationOptions options)
    {
        options.AddPolicy(AnyKey, policy => policy
            .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser());

        foreach (var permission in Enum.GetValues<Permission>())
        {
            options.AddPolicy(For(permission), policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireClaim(UserAccessClaimsTransformation.PermissionClaimType, permission.ToString()));
        }
    }
}
```

`src/DotMarc/Api/ApiPermissionMetadata.cs`:

```csharp
using DotMarc.Data;

namespace DotMarc.Api;

/// <summary>Which permission an endpoint needs, for the OpenAPI document.</summary>
public sealed record ApiPermissionMetadata(Permission Permission);

public static class ApiEndpointConventions
{
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, Permission permission) =>
        builder.RequireAuthorization(ApiPolicies.For(permission)).WithMetadata(new ApiPermissionMetadata(permission));
}
```

`src/DotMarc/Api/ApiProblems.cs`:

```csharp
using Microsoft.AspNetCore.Http.HttpResults;

namespace DotMarc.Api;

/// <summary>The API's error responses, all RFC 7807 problem+json.</summary>
public static class ApiProblems
{
    public static ProblemHttpResult NotFound(string what) =>
        TypedResults.Problem($"There's no {what}, or this key can't see it.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    public static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status403Forbidden, title: "Not allowed");

    public static ProblemHttpResult Conflict(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict");

    public static ValidationProblem Validation(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    public static Task WriteAsync(HttpContext httpContext, int statusCode, string title, string detail) =>
        TypedResults.Problem(detail, statusCode: statusCode, title: title).ExecuteAsync(httpContext);
}
```

`src/DotMarc/Api/ApiScope.cs`:

```csharp
using System.Globalization;
using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Security;

namespace DotMarc.Api;

/// <summary>Which groups a key is limited to. No scoped-group claims means unrestricted, as for a person. A scoped key
/// sees a domain when the domain is in at least one of its groups, as DomainDetail does for a scoped person.</summary>
public sealed class ApiScope
{
    private readonly int[]? _groupIds;

    private ApiScope(int[]? groupIds) => _groupIds = groupIds;

    public bool IsScoped => _groupIds is not null;

    public static ApiScope From(ClaimsPrincipal principal)
    {
        var groupIds = principal.FindAll(UserAccessClaimsTransformation.ScopedGroupClaimType)
            .Select(claim => int.Parse(claim.Value, CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();
        return new ApiScope(groupIds.Length == 0 ? null : groupIds);
    }

    public bool Includes(int groupId) => _groupIds is null || _groupIds.Contains(groupId);

    public IQueryable<Domain> Domains(IQueryable<Domain> domains)
    {
        var groupIds = _groupIds;
        return groupIds is null ? domains : domains.Where(domain => domain.Groups.Any(group => groupIds.Contains(group.Id)));
    }

    public IQueryable<Group> Groups(IQueryable<Group> groups)
    {
        var groupIds = _groupIds;
        return groupIds is null ? groups : groups.Where(group => groupIds.Contains(group.Id));
    }
}
```

`src/DotMarc/Api/ApiModels.cs` (later tasks append to this file):

```csharp
namespace DotMarc.Api;

public sealed record ApiNamedRef(int Id, string Name);

public sealed record ApiPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record ApiGroup(int Id, string Name, int DomainCount);

public sealed record ApiTag(int Id, string Name, string Color, int DomainCount);
```

`src/DotMarc/Api/ApiServices.cs`:

```csharp
using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using DotMarc.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DotMarc.Api;

public static class ApiServices
{
    public static IServiceCollection AddDotMarcApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ApiOptions>(configuration.GetSection(ApiOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, configureOptions: null);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (rejection, cancellationToken) =>
            {
                if (rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    rejection.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await ApiProblems.WriteAsync(rejection.HttpContext, StatusCodes.Status429TooManyRequests, "Too many requests",
                    "This API key has made too many requests this minute. Wait for the time in Retry-After, then try again.");
            };

            // Runs after authorization, which has already replaced the user with the key's principal.
            options.AddPolicy(ApiEndpoints.RateLimiterPolicy, httpContext =>
            {
                var keyId = httpContext.User.FindFirst(ApiKeyClaims.IdClaimType)?.Value ?? "none";
                var requestsPerMinute = httpContext.RequestServices.GetRequiredService<IOptions<ApiOptions>>().Value.RequestsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(keyId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = requestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });

        return services;
    }
}
```

`src/DotMarc/Api/ApiEndpoints.cs`:

```csharp
namespace DotMarc.Api;

public static class ApiEndpoints
{
    public const string RateLimiterPolicy = "api";

    public static RouteGroupBuilder MapDotMarcApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1")
            .RequireAuthorization(ApiPolicies.AnyKey)
            .RequireRateLimiting(RateLimiterPolicy);

        GroupAndTagEndpoints.Map(api);

        // Without this, a path under /api that isn't an endpoint falls to the UI's fallback policy and redirects to
        // sign-in.
        app.MapFallback("/api/{**path}", () => ApiProblems.NotFound("such API endpoint"))
            .AllowAnonymous()
            .ExcludeFromDescription();

        return api;
    }
}
```

`src/DotMarc/Api/GroupAndTagEndpoints.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class GroupAndTagEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/groups", ListGroupsAsync)
            .RequirePermission(Permission.GroupsView)
            .WithName("ListGroups")
            .WithSummary("List groups")
            .WithDescription("Every group this key can see, with how many domains are in each. A key limited to certain groups sees only those.");

        api.MapGet("/tags", ListTagsAsync)
            .RequirePermission(Permission.TagsView)
            .WithName("ListTags")
            .WithSummary("List tags")
            .WithDescription("Every tag, with its colour and how many domains this key can see carry it.");
    }

    private static async Task<Ok<List<ApiGroup>>> ListGroupsAsync(ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var groups = await ApiScope.From(user).Groups(context.Groups.AsNoTracking())
            .OrderBy(group => group.Name)
            .Select(group => new ApiGroup(group.Id, group.Name, group.Domains.Count))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(groups);
    }

    private static async Task<Ok<List<ApiTag>>> ListTagsAsync(ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tags = await context.Tags.AsNoTracking()
            .OrderBy(tag => tag.Name)
            .Select(tag => new { tag.Id, tag.Name, tag.Color, DomainIds = tag.Domains.Select(domain => domain.Id).ToList() })
            .ToListAsync(cancellationToken);
        var visibleDomainIds = (await scope.Domains(context.Domains.AsNoTracking()).Select(domain => domain.Id).ToListAsync(cancellationToken)).ToHashSet();
        return TypedResults.Ok(tags
            .Select(tag => new ApiTag(tag.Id, tag.Name, tag.Color.ToString(), tag.DomainIds.Count(visibleDomainIds.Contains)))
            .ToList());
    }
}
```

- [ ] **Step 6: Wire it into Program.cs**

1. After the `else { ... AddMicrosoftIdentityWebApp ... }` authentication block (before `builder.Services.AddScoped<...IClaimsTransformation...>`), add:

```csharp
builder.Services.AddDotMarcApi(builder.Configuration);
```

2. In `AddAuthorization(options => { ... })`, after the `GroupsOrTagsWrite` policy, add:

```csharp
    DotMarc.Api.ApiPolicies.Add(options);
```

3. Replace

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
```

with

```csharp
// Empty error responses from the API (a malformed JSON body, for one) become problem+json; the UI keeps its own pages.
app.UseWhen(httpContext => httpContext.Request.Path.StartsWithSegments("/api"), api => api.UseStatusCodePages());

app.UseAuthentication();
app.UseAuthorization();
// After authorization, so the API's limiter sees the key, not the browser's cookie user.
app.UseRateLimiter();
app.UseAntiforgery();
```

4. After the last existing `app.Map...` endpoint registration (before `app.MapRazorComponents` if that comes later, otherwise anywhere among the maps), add:

```csharp
app.MapDotMarcApi();
```

Add `using DotMarc.Api;` at the top of Program.cs if it has usings, otherwise qualify as `DotMarc.Api.ApiServices.AddDotMarcApi(builder.Services, builder.Configuration)` and `DotMarc.Api.ApiEndpoints.MapDotMarcApi(app)`.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AccessClaimsTests|FullyQualifiedName~DotMarc.Tests.Api|FullyQualifiedName~UserAccessClaimsTransformation"`
Expected: PASS. If `AnUnknownApiPath_IsAProblemNotARedirect` fails because the fallback ranks below the UI's own fallback route, add `.WithOrder(-1)` to the `MapFallback` call and record the ruling.

- [ ] **Step 8: Run the full suite** (the JSON enum converter and status code pages are app-wide registrations)

Run: `dotnet test test/DotMarc.Tests > .superpowers/sdd/2026-10-05-public-api/task2-suite.txt 2>&1; tail -5 .superpowers/sdd/2026-10-05-public-api/task2-suite.txt`
Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc test/DotMarc.Tests
git commit -m "Authenticate the public API with API keys and serve groups and tags"
```

---

### Task 3: Domain read endpoints

**Files:**
- Create: `src/DotMarc/Api/DomainReadEndpoints.cs`
- Modify: `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Api/ApiEndpoints.cs`
- Test: `test/DotMarc.Tests/Api/DomainReadEndpointTests.cs`

**Interfaces:**
- Consumes: Task 2's `ApiScope`, `ApiProblems`, `RequirePermission`, `ApiPage<T>`, `ApiNamedRef`, `ApiTestHost`.
- Produces:
  - `ApiDomain(int Id, string Name, bool Monitored, IReadOnlyList<ApiNamedRef> Groups, IReadOnlyList<ApiNamedRef> Tags, DateTimeOffset? LastReportReceivedUtc, double? PassRate)` with `static ApiDomain From(Domain domain, ApiScope scope)` (needs `Groups`, `Tags`, and in-window `Reports.Records` loaded; groups outside the scope are left out).
  - `ApiCheck`, `ApiDomainHealth`, `ApiDmarcPolicy`, `ApiDnsProvider`, `ApiDomainDetail`, `ApiReasonBreakdown`, `ApiSource`, `ApiReportSummary`.
  - `DomainReadEndpoints.MaximumPageSize = 200`, `DomainReadEndpoints.LoadForApiAsync(DotMarcDbContext, ApiScope, int domainId, CancellationToken) -> Task<Domain?>` (used by Task 4 to return a domain).

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Api/DomainReadEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class DomainReadEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public DomainReadEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static Report ReportWith(DateTimeOffset receivedUtc, params (string SourceIp, int MessageCount, AuthResult Spf, AuthResult Dkim)[] records)
    {
        var report = new Report
        {
            ReportingOrg = "google.com",
            ReportId = Guid.NewGuid().ToString(),
            DateRangeBeginUtc = receivedUtc.AddDays(-1),
            DateRangeEndUtc = receivedUtc,
            RawXml = "<feedback/>",
            ReceivedUtc = receivedUtc,
            AuthDetailBackfilledUtc = receivedUtc,
        };
        foreach (var (sourceIp, messageCount, spf, dkim) in records)
        {
            report.Records.Add(new ReportRecord { SourceIp = sourceIp, MessageCount = messageCount, Disposition = DispositionResult.None, SpfResult = spf, DkimResult = dkim, HeaderFrom = "example" });
        }

        return report;
    }

    [Fact]
    public async Task ListDomains_ReturnsGroupsTagsAndThe30DayPassRate()
    {
        var groupId = await _host.SeedGroupAsync("api-read-list");
        var tagId = await _host.SeedTagAsync("api-read-tag");
        var domainId = await _host.SeedDomainAsync("api-read-list.example", groupIds: [groupId], tagIds: [tagId], configure: domain =>
        {
            domain.LastReportReceivedUtc = DateTimeOffset.UtcNow.AddHours(-2);
            domain.Reports.Add(ReportWith(DateTimeOffset.UtcNow.AddHours(-2), ("203.0.113.1", 30, AuthResult.Pass, AuthResult.Fail), ("203.0.113.2", 10, AuthResult.Fail, AuthResult.Fail)));
            domain.Reports.Add(ReportWith(DateTimeOffset.UtcNow.AddDays(-40), ("203.0.113.3", 500, AuthResult.Fail, AuthResult.Fail)));
        });
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiDomain>>($"/api/v1/domains?group={groupId}");

        var domain = Assert.Single(page!.Items);
        Assert.Equal(domainId, domain.Id);
        Assert.Equal("api-read-list.example", domain.Name);
        Assert.True(domain.Monitored);
        Assert.Equal([new ApiNamedRef(groupId, "api-read-list")], domain.Groups);
        Assert.Equal([new ApiNamedRef(tagId, "api-read-tag")], domain.Tags);
        Assert.Equal(0.75, domain.PassRate!.Value, precision: 6);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ListDomains_PagesAndFiltersByTagAndMonitoring()
    {
        var tagId = await _host.SeedTagAsync("api-read-paging");
        await _host.SeedDomainAsync("api-page-a.example", tagIds: [tagId]);
        await _host.SeedDomainAsync("api-page-b.example", tagIds: [tagId]);
        await _host.SeedDomainAsync("api-page-c.example", tagIds: [tagId], monitored: false);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var firstPage = await client.GetFromJsonAsync<ApiPage<ApiDomain>>($"/api/v1/domains?tag={tagId}&monitored=true&pageSize=1");
        var secondPage = await client.GetFromJsonAsync<ApiPage<ApiDomain>>($"/api/v1/domains?tag={tagId}&monitored=true&pageSize=1&page=2");

        Assert.Equal(2, firstPage!.TotalCount);
        Assert.Equal(["api-page-a.example", "api-page-b.example"], firstPage.Items.Concat(secondPage!.Items).Select(domain => domain.Name).Order());
    }

    [Theory]
    [InlineData("pageSize=201", "pageSize")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("page=0", "page")]
    public async Task ListDomains_RefusesABadPage(string query, string field)
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync($"/api/v1/domains?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task AScopedKey_SeesOnlyItsDomains_AndOnlyItsGroupsOnThem()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-scope-own");
        var otherGroupId = await _host.SeedGroupAsync("api-scope-other");
        var sharedDomainId = await _host.SeedDomainAsync("api-scope-shared.example", groupIds: [ownGroupId, otherGroupId]);
        var hiddenDomainId = await _host.SeedDomainAsync("api-scope-hidden.example", groupIds: [otherGroupId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView], scopedGroupIds: [ownGroupId]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiDomain>>("/api/v1/domains");
        var hidden = await client.GetAsync($"/api/v1/domains/{hiddenDomainId}");

        var visible = Assert.Single(page!.Items);
        Assert.Equal(sharedDomainId, visible.Id);
        Assert.Equal([ownGroupId], visible.Groups.Select(group => group.Id));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task GetDomain_ReturnsTheStoredHealth()
    {
        var checkedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        var domainId = await _host.SeedDomainAsync("api-health.example", configure: domain =>
        {
            domain.DmarcCheckStatus = DmarcCheckStatus.Ok;
            domain.DmarcCheckedUtc = checkedUtc;
            domain.SpfCheckStatus = SpfCheckStatus.Ok;
            domain.SpfCheckDetail = "v=spf1 -all";
            domain.DmarcPolicy = DmarcPolicyLevel.Reject;
            domain.DmarcPercent = 100;
            domain.DnsZone = "api-health.example";
        });
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var detail = await client.GetFromJsonAsync<ApiDomainDetail>($"/api/v1/domains/{domainId}");

        Assert.Equal("Ok", detail!.Health.Dmarc.Status);
        Assert.Equal(checkedUtc.ToUnixTimeSeconds(), detail.Health.Dmarc.CheckedUtc!.Value.ToUnixTimeSeconds());
        Assert.Equal("v=spf1 -all", detail.Health.Spf.Detail);
        Assert.Equal("Reject", detail.DmarcPolicy.Policy);
        Assert.Equal(100, detail.DmarcPolicy.Percent);
        Assert.Equal("api-health.example", detail.DnsProvider.Zone);
    }

    [Fact]
    public async Task GetDomain_Unknown_Is404()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/domains/987654");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ReportSummary_TotalsTheWindow_AndRanksSources()
    {
        var domainId = await _host.SeedDomainAsync("api-summary.example", configure: domain =>
        {
            domain.Reports.Add(ReportWith(DateTimeOffset.UtcNow.AddDays(-2), ("203.0.113.10", 5, AuthResult.Pass, AuthResult.Pass), ("203.0.113.11", 20, AuthResult.Fail, AuthResult.Fail)));
            domain.Reports.Add(ReportWith(DateTimeOffset.UtcNow.AddDays(-10), ("203.0.113.12", 100, AuthResult.Pass, AuthResult.Pass)));
        });
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var summary = await client.GetFromJsonAsync<ApiReportSummary>($"/api/v1/domains/{domainId}/reports/summary?days=7");

        Assert.Equal(7, summary!.Days);
        Assert.Equal(25, summary.TotalVolume);
        Assert.Equal(0.2, summary.PassRate!.Value, precision: 6);
        Assert.Equal(["203.0.113.11", "203.0.113.10"], summary.TopSources.Select(source => source.SourceIp));
        Assert.Equal("Fail", summary.TopSources[0].Spf);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task ReportSummary_RefusesAWindowOutside1To30Days(int days)
    {
        var domainId = await _host.SeedDomainAsync($"api-summary-{days}.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync($"/api/v1/domains/{domainId}/reports/summary?days={days}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

The `JsonStringEnumConverter` is server-side only; these DTOs use strings for statuses, so `GetFromJsonAsync` needs no converter.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainReadEndpointTests"`
Expected: build FAIL naming `ApiDomain`, `ApiDomainDetail`, `ApiReportSummary`.

- [ ] **Step 3: Add the DTOs**

Append to `src/DotMarc/Api/ApiModels.cs` (add `using DotMarc.Data;` and `using DotMarc.Reporting;` at the top):

```csharp
public sealed record ApiDomain(
    int Id, string Name, bool Monitored, IReadOnlyList<ApiNamedRef> Groups, IReadOnlyList<ApiNamedRef> Tags,
    DateTimeOffset? LastReportReceivedUtc, double? PassRate)
{
    /// <summary>Needs Groups, Tags and the 30-day window's Reports with their Records loaded.</summary>
    public static ApiDomain From(Domain domain, ApiScope scope) => new(
        domain.Id,
        domain.Name,
        domain.IsMonitored,
        domain.Groups.Where(group => scope.Includes(group.Id)).OrderBy(group => group.Name).Select(group => new ApiNamedRef(group.Id, group.Name)).ToList(),
        domain.Tags.OrderBy(tag => tag.Name).Select(tag => new ApiNamedRef(tag.Id, tag.Name)).ToList(),
        domain.LastReportReceivedUtc,
        DomainStatistics.GetPassRate(domain.Reports));
}

public sealed record ApiCheck(string Status, DateTimeOffset? CheckedUtc, string? Detail);

public sealed record ApiDomainHealth(ApiCheck Dmarc, ApiCheck Spf, ApiCheck Dkim, ApiCheck Mx, ApiCheck Tlsrpt, ApiCheck MtaSts, ApiCheck DmarcAuthorization);

public sealed record ApiDmarcPolicy(string? Policy, string? SubdomainPolicy, int? Percent);

public sealed record ApiDnsProvider(string Provider, string? Zone);

public sealed record ApiDomainDetail(
    int Id, string Name, bool Monitored, IReadOnlyList<ApiNamedRef> Groups, IReadOnlyList<ApiNamedRef> Tags,
    DateTimeOffset? LastReportReceivedUtc, double? PassRate, ApiDomainHealth Health, ApiDmarcPolicy DmarcPolicy, ApiDnsProvider DnsProvider)
{
    public static ApiDomainDetail From(Domain domain, ApiScope scope)
    {
        var summary = ApiDomain.From(domain, scope);
        return new ApiDomainDetail(
            summary.Id, summary.Name, summary.Monitored, summary.Groups, summary.Tags, summary.LastReportReceivedUtc, summary.PassRate,
            new ApiDomainHealth(
                new ApiCheck(domain.DmarcCheckStatus.ToString(), domain.DmarcCheckedUtc, domain.DmarcCheckDetail),
                new ApiCheck(domain.SpfCheckStatus.ToString(), domain.SpfCheckedUtc, domain.SpfCheckDetail),
                new ApiCheck(domain.DkimCheckStatus.ToString(), domain.DkimCheckedUtc, domain.DkimCheckDetail),
                new ApiCheck(domain.MxCheckStatus.ToString(), domain.MxCheckedUtc, domain.MxCheckDetail),
                new ApiCheck(domain.TlsrptCheckStatus.ToString(), domain.TlsrptCheckedUtc, domain.TlsrptCheckDetail),
                new ApiCheck(domain.MtaStsStatus.ToString(), domain.MtaStsCheckedUtc, domain.MtaStsCheckDetail),
                new ApiCheck(domain.DmarcAuthorizationCheckStatus.ToString(), domain.DmarcAuthorizationCheckedUtc, domain.DmarcAuthorizationCheckDetail)),
            new ApiDmarcPolicy(domain.DmarcPolicy?.ToString(), domain.DmarcSubdomainPolicy?.ToString(), domain.DmarcPercent),
            new ApiDnsProvider(domain.DnsProvider.ToString(), domain.DnsZone));
    }
}

public sealed record ApiReasonBreakdown(int BenignOverride, int LocalPolicy, int Other, int NoReasonGiven, int InferredSpfFailure, int InferredDkimFailure, int InferredBothFailure);

public sealed record ApiSource(string SourceIp, int Volume, string Spf, string Dkim, string Disposition);

public sealed record ApiReportSummary(int DomainId, string DomainName, int Days, int TotalVolume, double? PassRate, ApiReasonBreakdown ReasonBreakdown, IReadOnlyList<ApiSource> TopSources);
```

- [ ] **Step 4: Write the endpoints**

`src/DotMarc/Api/DomainReadEndpoints.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Reporting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class DomainReadEndpoints
{
    public const int MaximumPageSize = 200;
    private const int DefaultPageSize = 50;
    private const int TopSourceCount = 20;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/domains", ListDomainsAsync)
            .RequirePermission(Permission.DomainsView)
            .WithName("ListDomains")
            .WithSummary("List domains")
            .WithDescription($"Domains this key can see, in dashboard order, {DefaultPageSize} a page by default and at most {MaximumPageSize}. Filter by group id, tag id or monitoring. The pass rate covers the last 30 days and is null with no reports.");

        api.MapGet("/domains/{id:int}", GetDomainAsync)
            .RequirePermission(Permission.DomainsView)
            .WithName("GetDomain")
            .WithSummary("Get a domain and its health")
            .WithDescription("The domain with the results of dotMARC's last DNS checks. The API never runs checks itself; checkedUtc says when each last ran.");

        api.MapGet("/domains/{id:int}/reports/summary", GetReportSummaryAsync)
            .RequirePermission(Permission.DomainsView)
            .WithName("GetReportSummary")
            .WithSummary("Summarise a domain's DMARC reports")
            .WithDescription("Volume, pass rate, why failing mail was let through or rejected, and the 20 busiest sending IPs, over the last 1 to 30 days (default 30).");
    }

    /// <summary>One domain the scope can see, with what ApiDomain.From needs, or null.</summary>
    public static Task<Domain?> LoadForApiAsync(DotMarcDbContext context, ApiScope scope, int domainId, CancellationToken cancellationToken)
    {
        var cutoffUtc = DomainStatistics.GetWindowCutoffUtc();
        return scope.Domains(context.Domains.AsNoTracking())
            .Where(domain => domain.Id == domainId)
            .Include(domain => domain.Groups)
            .Include(domain => domain.Tags)
            .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoffUtc))
            .ThenInclude(report => report.Records)
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static async Task<Results<Ok<ApiPage<ApiDomain>>, ValidationProblem>> ListDomainsAsync(
        ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, int? page, int? pageSize, int? group, int? tag, bool? monitored,
        CancellationToken cancellationToken)
    {
        var pageNumber = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (pageNumber < 1)
        {
            return ApiProblems.Validation("page", "Must be 1 or more.");
        }

        if (size is < 1 or > MaximumPageSize)
        {
            return ApiProblems.Validation("pageSize", $"Must be between 1 and {MaximumPageSize}.");
        }

        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = scope.Domains(context.Domains.AsNoTracking());
        if (group is { } groupId)
        {
            query = query.Where(domain => domain.Groups.Any(candidate => candidate.Id == groupId));
        }

        if (tag is { } tagId)
        {
            query = query.Where(domain => domain.Tags.Any(candidate => candidate.Id == tagId));
        }

        if (monitored is { } isMonitored)
        {
            query = query.Where(domain => domain.IsMonitored == isMonitored);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var cutoffUtc = DomainStatistics.GetWindowCutoffUtc();
        var domains = await query
            .OrderBy(domain => domain.SortOrder)
            .ThenBy(domain => domain.Name)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .Include(domain => domain.Groups)
            .Include(domain => domain.Tags)
            .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoffUtc))
            .ThenInclude(report => report.Records)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new ApiPage<ApiDomain>(domains.Select(domain => ApiDomain.From(domain, scope)).ToList(), pageNumber, size, totalCount));
    }

    private static async Task<Results<Ok<ApiDomainDetail>, ProblemHttpResult>> GetDomainAsync(
        int id, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await LoadForApiAsync(context, scope, id, cancellationToken);
        return domain is null ? ApiProblems.NotFound($"domain {id}") : TypedResults.Ok(ApiDomainDetail.From(domain, scope));
    }

    private static async Task<Results<Ok<ApiReportSummary>, ValidationProblem, ProblemHttpResult>> GetReportSummaryAsync(
        int id, int? days, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var windowDays = days ?? (int)DomainStatistics.ReportWindow.TotalDays;
        if (windowDays < 1 || windowDays > DomainStatistics.ReportWindow.TotalDays)
        {
            return ApiProblems.Validation("days", $"Must be between 1 and {(int)DomainStatistics.ReportWindow.TotalDays}.");
        }

        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-windowDays);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await ApiScope.From(user).Domains(context.Domains.AsNoTracking())
            .Where(candidate => candidate.Id == id)
            .Include(candidate => candidate.Reports.Where(report => report.ReceivedUtc >= cutoffUtc))
            .ThenInclude(report => report.Records)
            .ThenInclude(record => record.OverrideReasons)
            .Include(candidate => candidate.Reports.Where(report => report.ReceivedUtc >= cutoffUtc))
            .ThenInclude(report => report.Records)
            .ThenInclude(record => record.AuthDetails)
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken);
        if (domain is null)
        {
            return ApiProblems.NotFound($"domain {id}");
        }

        var breakdown = DomainStatistics.GetReasonBreakdown(domain.Reports);
        var topSources = DomainStatistics.GetSourceAggregates(domain.Reports)
            .OrderByDescending(source => source.Volume)
            .ThenBy(source => source.SourceIp)
            .Take(TopSourceCount)
            .Select(source => new ApiSource(source.SourceIp, source.Volume, source.SpfResult.ToString(), source.DkimResult.ToString(), source.Disposition.ToString()))
            .ToList();
        return TypedResults.Ok(new ApiReportSummary(
            domain.Id, domain.Name, windowDays, DomainStatistics.GetTotalVolume(domain.Reports), DomainStatistics.GetPassRate(domain.Reports),
            new ApiReasonBreakdown(breakdown.BenignOverride, breakdown.LocalPolicy, breakdown.Other, breakdown.NoReasonGiven,
                breakdown.InferredSpfFailure, breakdown.InferredDkimFailure, breakdown.InferredBothFailure),
            topSources));
    }
}
```

In `ApiEndpoints.MapDotMarcApi`, after `GroupAndTagEndpoints.Map(api);` add `DomainReadEndpoints.Map(api);`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainReadEndpointTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc test/DotMarc.Tests
git commit -m "Serve domains, their health and report summaries over the API"
```

---

### Task 4: Domain write endpoints

**Files:**
- Create: `src/DotMarc/Api/DomainWriteEndpoints.cs`
- Modify: `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Api/ApiEndpoints.cs`
- Test: `test/DotMarc.Tests/Api/DomainWriteEndpointTests.cs`

**Interfaces:**
- Consumes: `DomainReadEndpoints.LoadForApiAsync`, `ApiDomain.From`, `ApiScope`, `ApiProblems`, `AuditActor.FromPrincipal`, `DomainManagementService.AddDomainAsync/SetMonitoredAsync`, `GroupManagementService.SetDomainGroupsAsync`, `TagManagementService.SetDomainTagsAsync`.
- Produces: `ApiAddDomainRequest(string? Name)`, `ApiSetGroupsRequest(IReadOnlyList<int>? GroupIds)`, `ApiSetTagsRequest(IReadOnlyList<int>? TagIds)`, `ApiSetMonitoringRequest(bool? Monitored)`; `ApiProblems.ScopedKeyCantAdd` (string constant used by Task 5).

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Api/DomainWriteEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using DotMarc.Api;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class DomainWriteEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public DomainWriteEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task AddDomain_CreatesItAsTheKey()
    {
        var (keyId, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd], name: "Provisioning");
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains", new { name = "API-Added.example" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var added = await response.Content.ReadFromJsonAsync<ApiDomain>();
        Assert.Equal("api-added.example", added!.Name);
        Assert.Equal($"/api/v1/domains/{added.Id}", response.Headers.Location!.ToString());
        await using var context = _host.CreateContext();
        var entry = await context.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.DomainAdded && candidate.TargetName == "api-added.example");
        Assert.Equal(AuditActorKind.ApiKey, entry.ActorKind);
        Assert.StartsWith("API key 'Provisioning", entry.ActorName);
        Assert.Equal($"api-key:{keyId}", entry.ActorObjectId);
    }

    [Fact]
    public async Task AddDomain_AnExistingDomain_Is409()
    {
        await _host.SeedDomainAsync("api-existing.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains", new { name = "api-existing.example" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AddDomain_AnInvalidName_Is400()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains", new { name = "not a domain" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddDomain_ByAScopedKey_Is403()
    {
        var groupId = await _host.SeedGroupAsync("api-write-scoped-add");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd], scopedGroupIds: [groupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains", new { name = "api-scoped-add.example" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AddDomain_WithoutDomainsAdd_Is403()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains", new { name = "api-no-permission.example" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJson_IsAProblem()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync("/api/v1/domains", new StringContent("{\"name\": ", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SetGroups_ReplacesTheDomainsGroups()
    {
        var oldGroupId = await _host.SeedGroupAsync("api-set-groups-old");
        var newGroupId = await _host.SeedGroupAsync("api-set-groups-new");
        var domainId = await _host.SeedDomainAsync("api-set-groups.example", groupIds: [oldGroupId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/groups", new { groupIds = new[] { newGroupId } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var context = _host.CreateContext();
        var domain = await context.Domains.Include(candidate => candidate.Groups).SingleAsync(candidate => candidate.Id == domainId);
        Assert.Equal([newGroupId], domain.Groups.Select(group => group.Id));
    }

    [Fact]
    public async Task SetGroups_AnUnknownGroup_Is400()
    {
        var domainId = await _host.SeedDomainAsync("api-set-groups-unknown.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/groups", new { groupIds = new[] { 987654 } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AScopedKey_SettingGroups_KeepsGroupsOutsideItsScope()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-scoped-set-own");
        var secondOwnGroupId = await _host.SeedGroupAsync("api-scoped-set-own-2");
        var otherGroupId = await _host.SeedGroupAsync("api-scoped-set-other");
        var domainId = await _host.SeedDomainAsync("api-scoped-set.example", groupIds: [ownGroupId, otherGroupId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit], scopedGroupIds: [ownGroupId, secondOwnGroupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/groups", new { groupIds = new[] { secondOwnGroupId } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var context = _host.CreateContext();
        var domain = await context.Domains.Include(candidate => candidate.Groups).SingleAsync(candidate => candidate.Id == domainId);
        Assert.Equal(new[] { secondOwnGroupId, otherGroupId }.Order(), domain.Groups.Select(group => group.Id).Order());
    }

    [Fact]
    public async Task AScopedKey_NamingAGroupOutsideItsScope_Is403()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-scoped-name-own");
        var otherGroupId = await _host.SeedGroupAsync("api-scoped-name-other");
        var domainId = await _host.SeedDomainAsync("api-scoped-name.example", groupIds: [ownGroupId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit], scopedGroupIds: [ownGroupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/groups", new { groupIds = new[] { ownGroupId, otherGroupId } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AScopedKey_WritingToADomainItCantSee_Is404()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-scoped-hidden-own");
        var domainId = await _host.SeedDomainAsync("api-scoped-hidden.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit], scopedGroupIds: [ownGroupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/monitoring", new { monitored = false });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SetTags_AndSetMonitoring_ChangeTheDomain()
    {
        var tagId = await _host.SeedTagAsync("api-set-tags");
        var domainId = await _host.SeedDomainAsync("api-set-tags.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var tagsResponse = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/tags", new { tagIds = new[] { tagId } });
        var monitoringResponse = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/monitoring", new { monitored = false });

        Assert.Equal(HttpStatusCode.NoContent, tagsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, monitoringResponse.StatusCode);
        await using var context = _host.CreateContext();
        var domain = await context.Domains.Include(candidate => candidate.Tags).SingleAsync(candidate => candidate.Id == domainId);
        Assert.Equal([tagId], domain.Tags.Select(tag => tag.Id));
        Assert.False(domain.IsMonitored);
    }

    [Fact]
    public async Task SetMonitoring_WithoutAValue_Is400()
    {
        var domainId = await _host.SeedDomainAsync("api-monitoring-missing.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/monitoring", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainWriteEndpointTests"`
Expected: FAIL (405 or 404 responses: the endpoints don't exist yet).

- [ ] **Step 3: Add the request DTOs**

Append to `src/DotMarc/Api/ApiModels.cs`:

```csharp
public sealed record ApiAddDomainRequest(string? Name);

public sealed record ApiSetGroupsRequest(IReadOnlyList<int>? GroupIds);

public sealed record ApiSetTagsRequest(IReadOnlyList<int>? TagIds);

public sealed record ApiSetMonitoringRequest(bool? Monitored);
```

Add to `ApiProblems`:

```csharp
    public const string ScopedKeyCantAdd =
        "A key limited to certain groups can't add domains, because a new domain isn't in any group yet. Use a key that isn't limited to groups.";
```

- [ ] **Step 4: Write the endpoints**

`src/DotMarc/Api/DomainWriteEndpoints.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class DomainWriteEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/domains", AddDomainAsync)
            .RequirePermission(Permission.DomainsAdd)
            .WithName("AddDomain")
            .WithSummary("Add a domain")
            .WithDescription("Adds a domain for dotMARC to monitor. It shows as missing reports until its first DMARC report arrives.");

        api.MapPut("/domains/{id:int}/groups", SetGroupsAsync)
            .RequirePermission(Permission.DomainsEdit)
            .WithName("SetDomainGroups")
            .WithSummary("Set a domain's groups")
            .WithDescription("Replaces the domain's groups with exactly these. A key limited to certain groups can only name its own groups, and the domain keeps any groups outside them.");

        api.MapPut("/domains/{id:int}/tags", SetTagsAsync)
            .RequirePermission(Permission.DomainsEdit)
            .WithName("SetDomainTags")
            .WithSummary("Set a domain's tags")
            .WithDescription("Replaces the domain's tags with exactly these.");

        api.MapPut("/domains/{id:int}/monitoring", SetMonitoringAsync)
            .RequirePermission(Permission.DomainsEdit)
            .WithName("SetDomainMonitoring")
            .WithSummary("Turn monitoring on or off")
            .WithDescription("Whether dotMARC alerts on the domain's missing reports and DNS health.");
    }

    private static async Task<Results<Created<ApiDomain>, ValidationProblem, ProblemHttpResult>> AddDomainAsync(
        ApiAddDomainRequest request, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var scope = ApiScope.From(user);
        if (scope.IsScoped)
        {
            return ApiProblems.Forbidden(ApiProblems.ScopedKeyCantAdd);
        }

        if (!DomainNameValidator.TryNormalize(request.Name ?? "", out var normalizedName))
        {
            return ApiProblems.Validation("name", "That isn't a valid domain name.");
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var result = await DomainManagementService.AddDomainAsync(context, AuditActor.FromPrincipal(user), normalizedName, cancellationToken);
        if (result == DomainManagementService.AddDomainResult.AlreadyMonitored)
        {
            return ApiProblems.Conflict($"{normalizedName} is already in dotMARC.");
        }

        if (result == DomainManagementService.AddDomainResult.InvalidName)
        {
            return ApiProblems.Validation("name", "That isn't a valid domain name.");
        }

        var domainId = await context.Domains.Where(domain => domain.Name == normalizedName).Select(domain => domain.Id).SingleAsync(cancellationToken);
        var added = await DomainReadEndpoints.LoadForApiAsync(context, scope, domainId, cancellationToken);
        return TypedResults.Created($"/api/v1/domains/{domainId}", ApiDomain.From(added!, scope));
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetGroupsAsync(
        int id, ApiSetGroupsRequest request, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        if (request.GroupIds is not { } requestedIds)
        {
            return ApiProblems.Validation("groupIds", "Send groupIds, an array of group ids (empty to remove every group).");
        }

        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await scope.Domains(context.Domains.AsNoTracking()).Include(candidate => candidate.Groups).SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (domain is null)
        {
            return ApiProblems.NotFound($"domain {id}");
        }

        var distinctIds = requestedIds.Distinct().ToList();
        var existingIds = await context.Groups.Where(group => distinctIds.Contains(group.Id)).Select(group => group.Id).ToListAsync(cancellationToken);
        var missingIds = distinctIds.Except(existingIds).ToList();
        if (missingIds.Count > 0)
        {
            return ApiProblems.Validation("groupIds", $"There's no group {missingIds[0]}.");
        }

        if (distinctIds.Any(groupId => !scope.Includes(groupId)))
        {
            return ApiProblems.Forbidden("This key can only put domains in its own groups.");
        }

        // A scoped key can't see the domain's other groups, so it can't remove them either.
        var keptOutsideScope = domain.Groups.Where(group => !scope.Includes(group.Id)).Select(group => group.Id);
        await GroupManagementService.SetDomainGroupsAsync(context, AuditActor.FromPrincipal(user), id, distinctIds.Concat(keptOutsideScope).Distinct().ToList(), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetTagsAsync(
        int id, ApiSetTagsRequest request, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        if (request.TagIds is not { } requestedIds)
        {
            return ApiProblems.Validation("tagIds", "Send tagIds, an array of tag ids (empty to remove every tag).");
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await ApiScope.From(user).Domains(context.Domains).AnyAsync(domain => domain.Id == id, cancellationToken))
        {
            return ApiProblems.NotFound($"domain {id}");
        }

        var distinctIds = requestedIds.Distinct().ToList();
        var existingIds = await context.Tags.Where(tag => distinctIds.Contains(tag.Id)).Select(tag => tag.Id).ToListAsync(cancellationToken);
        var missingIds = distinctIds.Except(existingIds).ToList();
        if (missingIds.Count > 0)
        {
            return ApiProblems.Validation("tagIds", $"There's no tag {missingIds[0]}.");
        }

        await TagManagementService.SetDomainTagsAsync(context, AuditActor.FromPrincipal(user), id, distinctIds, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetMonitoringAsync(
        int id, ApiSetMonitoringRequest request, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        if (request.Monitored is not { } monitored)
        {
            return ApiProblems.Validation("monitored", "Send monitored, true or false.");
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await ApiScope.From(user).Domains(context.Domains).AnyAsync(domain => domain.Id == id, cancellationToken))
        {
            return ApiProblems.NotFound($"domain {id}");
        }

        await DomainManagementService.SetMonitoredAsync(context, AuditActor.FromPrincipal(user), id, monitored, cancellationToken);
        return TypedResults.NoContent();
    }
}
```

In `ApiEndpoints.MapDotMarcApi`, add `DomainWriteEndpoints.Map(api);` after `DomainReadEndpoints.Map(api);`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainWriteEndpointTests"`
Expected: PASS. `MalformedJson_IsAProblem` relies on Task 2's `UseStatusCodePages` branch; if the body is empty, check that branch runs before `UseAuthentication`.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc test/DotMarc.Tests
git commit -m "Add domains and set their groups, tags and monitoring over the API"
```

---

### Task 5: Import endpoint

**Files:**
- Create: `src/DotMarc/Api/ImportEndpoints.cs`
- Modify: `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Api/ApiEndpoints.cs`
- Test: `test/DotMarc.Tests/Api/ImportEndpointTests.cs`

**Interfaces:**
- Consumes: `ImportTable.FromRows`, `ImportRow`, `ImportInputException`, `ImportSnapshotLoader.LoadAsync`, `DomainImportPlanner.Plan`, `DomainImportService.ApplyAsync`, `ImportPermissions`, `NameResolution`, `NameChoice`, `ExistingDomainMode`, `IMxHostsLookup`; `ApiProblems.ScopedKeyCantAdd`.
- Produces: `ApiImportRequest`, `ApiImportDomain`, `ApiImportResponse`, `ApiImportRow`, `ApiImportUnknownName`; `ImportEndpoints.MaximumRows = 500`.

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Api/ImportEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class ImportEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ImportEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Import_AddsDomainsWithGroupsAndTags()
    {
        await _host.SeedGroupAsync("api-import-group");
        await _host.SeedTagAsync("api-import-tag");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd, Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains/import", new
        {
            domains = new object[]
            {
                new { name = "api-import-1.example", groups = new[] { "api-import-group" }, tags = new[] { "api-import-tag" } },
                new { name = "api-import-2.example", monitored = false },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiImportResponse>();
        Assert.False(result!.DryRun);
        Assert.Equal(2, result.Added);
        await using var context = _host.CreateContext();
        var first = await context.Domains.Include(domain => domain.Groups).Include(domain => domain.Tags).SingleAsync(domain => domain.Name == "api-import-1.example");
        Assert.Equal(["api-import-group"], first.Groups.Select(group => group.Name));
        Assert.Equal(["api-import-tag"], first.Tags.Select(tag => tag.Name));
        Assert.False((await context.Domains.SingleAsync(domain => domain.Name == "api-import-2.example")).IsMonitored);
    }

    [Fact]
    public async Task Import_DryRun_ChangesNothing()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains/import?dryRun=true", new { domains = new[] { new { name = "api-dry-run.example" } } });

        var result = await response.Content.ReadFromJsonAsync<ApiImportResponse>();
        Assert.True(result!.DryRun);
        Assert.Equal(1, result.Added);
        Assert.Equal("add", Assert.Single(result.Rows).Outcome);
        await using var context = _host.CreateContext();
        Assert.False(await context.Domains.AnyAsync(domain => domain.Name == "api-dry-run.example"));
    }

    [Fact]
    public async Task Import_UnknownNamesSkipped_ByDefault()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd, Permission.DomainsEdit, Permission.GroupsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains/import", new { domains = new[] { new { name = "api-unknown-skip.example", groups = new[] { "api-never-made" } } } });

        var result = await response.Content.ReadFromJsonAsync<ApiImportResponse>();
        Assert.Equal("api-never-made", Assert.Single(result!.UnknownNames).Name);
        await using var context = _host.CreateContext();
        Assert.False(await context.Groups.AnyAsync(group => group.Name == "api-never-made"));
        Assert.True(await context.Domains.AnyAsync(domain => domain.Name == "api-unknown-skip.example"));
    }

    [Fact]
    public async Task Import_UnknownNamesCreated_WhenAskedAndAllowed()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd, Permission.DomainsEdit, Permission.GroupsAdd]);
        using var client = _host.ClientFor(secret);

        await client.PostAsJsonAsync("/api/v1/domains/import", new { unknownNames = "create", domains = new[] { new { name = "api-unknown-create.example", groups = new[] { "api-made-by-import" } } } });

        await using var context = _host.CreateContext();
        Assert.True(await context.Groups.AnyAsync(group => group.Name == "api-made-by-import"));
    }

    [Fact]
    public async Task Import_UnknownNamesLeftOut_WhenCreateIsAskedButNotAllowed()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd, Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        await client.PostAsJsonAsync("/api/v1/domains/import", new { unknownNames = "create", domains = new[] { new { name = "api-unknown-denied.example", groups = new[] { "api-denied-group" } } } });

        await using var context = _host.CreateContext();
        Assert.False(await context.Groups.AnyAsync(group => group.Name == "api-denied-group"));
    }

    [Fact]
    public async Task MatchMode_LeavesTagsAlone_WhenNoRowSendsTags()
    {
        var oldGroupId = await _host.SeedGroupAsync("api-match-old");
        await _host.SeedGroupAsync("api-match-new");
        var tagId = await _host.SeedTagAsync("api-match-tag");
        await _host.SeedDomainAsync("api-match.example", groupIds: [oldGroupId], tagIds: [tagId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd, Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        await client.PostAsJsonAsync("/api/v1/domains/import", new { existingDomains = "match", domains = new[] { new { name = "api-match.example", groups = new[] { "api-match-new" } } } });

        await using var context = _host.CreateContext();
        var domain = await context.Domains.Include(candidate => candidate.Groups).Include(candidate => candidate.Tags).SingleAsync(candidate => candidate.Name == "api-match.example");
        Assert.Equal(["api-match-new"], domain.Groups.Select(group => group.Name));
        Assert.Equal([tagId], domain.Tags.Select(tag => tag.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Import_RefusesNoneOrTooManyRows(int rowCount)
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains/import",
            new { domains = Enumerable.Range(0, rowCount).Select(index => new { name = $"api-bulk-{index}.example" }).ToArray() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"existingDomains\":\"replace\",\"domains\":[{\"name\":\"a.example\"}]}", "existingDomains")]
    [InlineData("{\"unknownNames\":\"guess\",\"domains\":[{\"name\":\"a.example\"}]}", "unknownNames")]
    [InlineData("{\"domains\":[{\"name\":\"a.example\",\"groups\":[\"semi;colon\"]}]}", "domains")]
    public async Task Import_RefusesBadOptions(string body, string field)
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync("/api/v1/domains/import", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Import_ByAScopedKey_Is403()
    {
        var groupId = await _host.SeedGroupAsync("api-import-scoped");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd], scopedGroupIds: [groupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains/import", new { domains = new[] { new { name = "api-import-scoped.example" } } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ImportEndpointTests"`
Expected: build FAIL naming `ApiImportResponse`.

- [ ] **Step 3: Add the DTOs**

Append to `src/DotMarc/Api/ApiModels.cs`:

```csharp
public sealed record ApiImportDomain(string? Name, IReadOnlyList<string>? Groups, IReadOnlyList<string>? Tags, bool? Monitored);

public sealed record ApiImportRequest(string? ExistingDomains, string? UnknownNames, IReadOnlyList<ApiImportDomain>? Domains);

/// <summary>Outcome is add, update, unchanged, skip, duplicate or invalid in a dry run; after applying, the import's
/// own outcome text.</summary>
public sealed record ApiImportRow(int Line, string Domain, string Outcome, IReadOnlyList<string> Notes);

public sealed record ApiImportUnknownName(string Kind, string Name, string Resolution);

public sealed record ApiImportResponse(
    bool DryRun, int Added, int Updated, int Unchanged, int SkippedExisting, int Invalid, int Duplicates,
    IReadOnlyList<ApiImportRow> Rows, IReadOnlyList<ApiImportUnknownName> UnknownNames, IReadOnlyList<string> Notices);
```

- [ ] **Step 4: Write the endpoint**

`src/DotMarc/Api/ImportEndpoints.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DomainImport;
using DotMarc.MtaSts;
using DotMarc.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

/// <summary>The bulk import page's planner and service behind a JSON body: rows become the same table a CSV with
/// domain, groups, tags and monitored columns would, so validation, permissions and auditing match the UI exactly.</summary>
public static class ImportEndpoints
{
    public const int MaximumRows = 500;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/domains/import", ImportAsync)
            .RequirePermission(Permission.DomainsAdd)
            .WithName("ImportDomains")
            .WithSummary("Import domains in bulk")
            .WithDescription($"Adds up to {MaximumRows} domains, with groups, tags and monitoring, exactly as the Import domains page does. existingDomains: skip (default), add (add groups and tags; a name written as -Name removes one) or match (make groups and tags match). unknownNames: skip (default) or create (needs GroupsAdd or TagsAdd). Changing existing domains needs DomainsEdit. dryRun=true returns the plan without changing anything.");
    }

    private static async Task<Results<Ok<ApiImportResponse>, ValidationProblem, ProblemHttpResult>> ImportAsync(
        ApiImportRequest request, bool? dryRun, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, IMxHostsLookup mxHostsLookup,
        CancellationToken cancellationToken)
    {
        if (ApiScope.From(user).IsScoped)
        {
            return ApiProblems.Forbidden(ApiProblems.ScopedKeyCantAdd);
        }

        ExistingDomainMode mode;
        switch ((request.ExistingDomains ?? "skip").ToLowerInvariant())
        {
            case "skip": mode = ExistingDomainMode.Skip; break;
            case "add": mode = ExistingDomainMode.Add; break;
            case "match": mode = ExistingDomainMode.Match; break;
            default: return ApiProblems.Validation("existingDomains", "Use skip, add or match.");
        }

        var unknownNames = (request.UnknownNames ?? "skip").ToLowerInvariant();
        if (unknownNames is not ("skip" or "create"))
        {
            return ApiProblems.Validation("unknownNames", "Use skip or create.");
        }

        var domains = request.Domains ?? [];
        if (domains.Count is 0 or > MaximumRows)
        {
            return ApiProblems.Validation("domains", $"Send between 1 and {MaximumRows} domains.");
        }

        if (domains.Any(domain => (domain.Groups ?? []).Concat(domain.Tags ?? []).Any(name => name.Contains(';'))))
        {
            return ApiProblems.Validation("domains", "Group and tag names can't contain ';'.");
        }

        ImportTable table;
        try
        {
            table = ImportTable.FromRows(ToRows(domains));
        }
        catch (ImportInputException exception)
        {
            return ApiProblems.Validation("domains", exception.Message);
        }

        var permissions = new ImportPermissions(
            CanEditDomains: Has(user, Permission.DomainsEdit),
            CanManageMtaSts: false,
            CanAddGroups: Has(user, Permission.GroupsAdd),
            CanAddTags: Has(user, Permission.TagsAdd));
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await ImportSnapshotLoader.LoadAsync(context, table, null, null, mxHostsLookup, cancellationToken);
        var plan = DomainImportPlanner.Plan(table, snapshot, mode, permissions);
        if (unknownNames == "skip" && plan.UnknownNames.Count > 0)
        {
            var leaveOut = plan.UnknownNames.ToDictionary(name => name.Key, _ => new NameResolution(NameChoice.LeaveOut));
            plan = DomainImportPlanner.Plan(table, snapshot, mode, permissions, leaveOut);
        }

        var notesByLine = plan.Rows.ToDictionary(row => row.LineNumber, row => row.Notes);
        var unknown = plan.UnknownNames
            .Select(name => new ApiImportUnknownName(name.Kind.ToString(), name.Name, name.Resolution.Choice == NameChoice.Create ? "create" : "leave out"))
            .ToList();
        if (dryRun == true)
        {
            var plannedRows = plan.Rows.Select(row => new ApiImportRow(row.LineNumber, row.Domain ?? row.RawDomain, DryRunOutcome(plan, row), row.Notes)).ToList();
            return TypedResults.Ok(new ApiImportResponse(true, plan.NewCount, plan.UpdateCount, plan.UnchangedCount, plan.SkippedExistingCount,
                plan.InvalidCount, plan.DuplicateCount, plannedRows, unknown, plan.Notices));
        }

        var result = await DomainImportService.ApplyAsync(context, AuditActor.FromPrincipal(user), plan, cancellationToken);
        var appliedRows = result.Rows
            .Select(row => new ApiImportRow(row.LineNumber, row.Domain, row.Outcome, notesByLine.GetValueOrDefault(row.LineNumber) ?? []))
            .ToList();
        return TypedResults.Ok(new ApiImportResponse(false, result.Added, result.Updated, result.Unchanged, result.SkippedExisting,
            result.Invalid, result.Duplicates, appliedRows, unknown, plan.Notices));
    }

    /// <summary>A header row naming only the columns some domain actually sent, so match mode never reads a column
    /// nobody sent as "clear it".</summary>
    private static List<ImportRow> ToRows(IReadOnlyList<ApiImportDomain> domains)
    {
        var sendsGroups = domains.Any(domain => domain.Groups is not null);
        var sendsTags = domains.Any(domain => domain.Tags is not null);
        var sendsMonitored = domains.Any(domain => domain.Monitored is not null);
        var header = new List<string> { "domain" };
        if (sendsGroups) header.Add("groups");
        if (sendsTags) header.Add("tags");
        if (sendsMonitored) header.Add("monitored");

        var rows = new List<ImportRow> { new(0, header) };
        for (var index = 0; index < domains.Count; index++)
        {
            var domain = domains[index];
            var cells = new List<string> { (domain.Name ?? "").Trim() };
            if (sendsGroups) cells.Add(string.Join(';', domain.Groups ?? []));
            if (sendsTags) cells.Add(string.Join(';', domain.Tags ?? []));
            if (sendsMonitored) cells.Add(domain.Monitored switch { true => "yes", false => "no", null => "" });
            rows.Add(new ImportRow(index + 1, cells));
        }

        return rows;
    }

    private static string DryRunOutcome(ImportPlan plan, PlannedRow row) => row.Status switch
    {
        ImportRowStatus.New => "add",
        ImportRowStatus.Duplicate => "duplicate",
        ImportRowStatus.Invalid => "invalid",
        _ when plan.Mode == ExistingDomainMode.Skip => "skip",
        _ when row.Target is null => "unchanged",
        _ => "update",
    };

    private static bool Has(ClaimsPrincipal user, Permission permission) =>
        user.HasClaim(UserAccessClaimsTransformation.PermissionClaimType, permission.ToString());
}
```

`ImportTable.FromRows` throws for rows over its own maximum (1000), which the 500 check above makes unreachable. Each API row is line `index + 1`, the header is line 0. If the planner shows a header-related warning for the header row, it lands in `table.Warnings`, which is not returned; the planner's notices are.

In `ApiEndpoints.MapDotMarcApi`, add `ImportEndpoints.Map(api);` after `DomainWriteEndpoints.Map(api);`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ImportEndpointTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc test/DotMarc.Tests
git commit -m "Import domains in bulk over the API"
```

---

### Task 6: Alert endpoints

**Files:**
- Create: `src/DotMarc/Api/AlertEndpoints.cs`
- Modify: `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Api/ApiEndpoints.cs`, `docs/superpowers/specs/2026-10-05-public-api-design.md` (alert fields and acknowledge response, see Step 4)
- Test: `test/DotMarc.Tests/Api/AlertEndpointTests.cs`

**Interfaces:**
- Consumes: `AlertEvent`, `AlertTypes.Find`, `AlertAcknowledgement.IsAcknowledgeable/AcknowledgeAsync`, `IPsaTicketService`, `ApiScope`, `ApiPage<T>`.
- Produces: `ApiAlert(int Id, string Type, string TypeName, string Subject, ApiNamedRef? Domain, string Severity, string Title, string Message, DateTimeOffset RaisedUtc, bool Resolved, DateTimeOffset? ResolvedUtc, bool Acknowledgeable)`, `ApiAcknowledgement(bool TicketClosed)`.

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Api/AlertEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class AlertEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public AlertEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<int> SeedAlertAsync(string subject, string alertType, bool resolved = false, DateTimeOffset? createdUtc = null)
    {
        await using var context = _host.CreateContext();
        var alert = new AlertEvent
        {
            DomainName = subject, AlertType = alertType, Severity = "Warning", Title = $"{alertType} title", Message = "message",
            IsResolved = resolved, ResolvedUtc = resolved ? DateTimeOffset.UtcNow : null, CreatedUtc = createdUtc ?? DateTimeOffset.UtcNow,
        };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();
        return alert.Id;
    }

    [Fact]
    public async Task ListAlerts_ShowsOpenAlertsNewestFirst_ForAScopedKeysDomains()
    {
        var groupId = await _host.SeedGroupAsync("api-alerts-own");
        var domainId = await _host.SeedDomainAsync("api-alerts.example", groupIds: [groupId]);
        var olderId = await SeedAlertAsync("api-alerts.example", AlertTypes.MissedReport, createdUtc: DateTimeOffset.UtcNow.AddHours(-3));
        var newerId = await SeedAlertAsync("api-alerts.example", AlertTypes.SpfRecordBroken);
        var resolvedId = await SeedAlertAsync("api-alerts.example", AlertTypes.MxRecordBroken, resolved: true);
        await SeedAlertAsync("api-alerts-elsewhere.example", AlertTypes.MissedReport);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView], scopedGroupIds: [groupId]);
        using var client = _host.ClientFor(secret);

        var open = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts");
        var all = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?status=all");

        Assert.Equal([newerId, olderId], open!.Items.Select(alert => alert.Id));
        Assert.Equal(new ApiNamedRef(domainId, "api-alerts.example"), open.Items[0].Domain);
        Assert.Equal("SPF record broken", open.Items[0].TypeName);
        Assert.Contains(resolvedId, all!.Items.Select(alert => alert.Id));
        Assert.Equal(3, all.TotalCount);
    }

    [Fact]
    public async Task AnUnscopedKey_SeesAlertsThatArentAboutADomain()
    {
        var alertId = await SeedAlertAsync("API key Old sync (dmk_abcdefgh)", AlertTypes.ApiKeyExpiring);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?pageSize=200");

        var alert = Assert.Single(page!.Items, candidate => candidate.Id == alertId);
        Assert.Null(alert.Domain);
        Assert.Equal("API key Old sync (dmk_abcdefgh)", alert.Subject);
    }

    [Fact]
    public async Task Acknowledge_ClosesAPolicyAlert_AsTheKey()
    {
        await _host.SeedDomainAsync("api-ack.example", configure: domain =>
        {
            domain.DmarcPolicy = DmarcPolicyLevel.None;
            domain.DmarcSubdomainPolicy = DmarcPolicyLevel.None;
            domain.DmarcPercent = 100;
            domain.AlertStates.Add(new DomainAlertState { Item = DnsHealthItems.DmarcPolicy, Baseline = "p=reject; sp=reject; pct=100" });
        });
        var alertId = await SeedAlertAsync("api-ack.example", AlertTypes.DmarcPolicyWeakened);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsManage]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync($"/api/v1/alerts/{alertId}/acknowledge", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<ApiAcknowledgement>())!.TicketClosed);
        await using var context = _host.CreateContext();
        Assert.True((await context.AlertEvents.SingleAsync(alert => alert.Id == alertId)).IsResolved);
        var entry = await context.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.AlertAcknowledged && candidate.TargetName == "api-ack.example");
        Assert.Equal(AuditActorKind.ApiKey, entry.ActorKind);
    }

    [Fact]
    public async Task Acknowledge_ACheckAlert_Is409()
    {
        await _host.SeedDomainAsync("api-ack-check.example");
        var alertId = await SeedAlertAsync("api-ack-check.example", AlertTypes.SpfRecordBroken);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsManage]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync($"/api/v1/alerts/{alertId}/acknowledge", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Acknowledge_AnAlertOutsideAScopedKeysGroups_Is404()
    {
        var groupId = await _host.SeedGroupAsync("api-ack-scope");
        await _host.SeedDomainAsync("api-ack-hidden.example");
        var alertId = await SeedAlertAsync("api-ack-hidden.example", AlertTypes.NameserversChanged);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsManage], scopedGroupIds: [groupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync($"/api/v1/alerts/{alertId}/acknowledge", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Acknowledge_WithoutAlertsManage_Is403()
    {
        var alertId = await SeedAlertAsync("api-ack-denied.example", AlertTypes.NameserversChanged);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync($"/api/v1/alerts/{alertId}/acknowledge", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListAlerts_RefusesAnUnknownStatus()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/alerts?status=closed");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

`AlertTypes.ApiKeyExpiring` is added in Task 7. For this task, add the constant and its `All` entry now (Step 3 below) so these tests compile; Task 7 adds the monitoring.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertEndpointTests"`
Expected: build FAIL naming `ApiAlert`, `ApiAcknowledgement`, `AlertTypes.ApiKeyExpiring`.

- [ ] **Step 3: Add the alert type and DTOs**

In `Notifications/AlertTypes.cs`, add after `NameserversChanged`:

```csharp
    public const string ApiKeyExpiring = "ApiKeyExpiring";
```

and in `All`, after the `NameserversChanged` entry:

```csharp
        new(ApiKeyExpiring, "API key expiring", "An API key expires within 14 days. Make a replacement before whatever uses it stops working.", CreatesTicketByDefault: false),
```

Append to `src/DotMarc/Api/ApiModels.cs`:

```csharp
/// <summary>Subject is what the alert is about: a domain name, or for alerts not about a domain (an expiring API key),
/// a description. Domain is set when the subject is a domain this key can see.</summary>
public sealed record ApiAlert(
    int Id, string Type, string TypeName, string Subject, ApiNamedRef? Domain, string Severity, string Title, string Message,
    DateTimeOffset RaisedUtc, bool Resolved, DateTimeOffset? ResolvedUtc, bool Acknowledgeable);

public sealed record ApiAcknowledgement(bool TicketClosed);
```

- [ ] **Step 4: Write the endpoints**

`src/DotMarc/Api/AlertEndpoints.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class AlertEndpoints
{
    private const int DefaultPageSize = 50;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/alerts", ListAlertsAsync)
            .RequirePermission(Permission.AlertsView)
            .WithName("ListAlerts")
            .WithSummary("List alerts")
            .WithDescription($"Alerts newest first, open ones by default (status=all includes resolved), {DefaultPageSize} a page by default and at most {DomainReadEndpoints.MaximumPageSize}. A key limited to certain groups sees only alerts about its domains.");

        api.MapPost("/alerts/{id:int}/acknowledge", AcknowledgeAsync)
            .RequirePermission(Permission.AlertsManage)
            .WithName("AcknowledgeAlert")
            .WithSummary("Acknowledge an alert")
            .WithDescription("Closes a DMARC policy weakened or nameservers changed alert and accepts the current value as normal, closing its PSA ticket too. Other alerts close themselves once fixed, so acknowledging them is a conflict. ticketClosed is false when the ticket couldn't be closed and needs closing by hand.");
    }

    private static async Task<Results<Ok<ApiPage<ApiAlert>>, ValidationProblem>> ListAlertsAsync(
        ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, string? status, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var includeResolved = (status ?? "open").ToLowerInvariant() switch
        {
            "open" => (bool?)false,
            "all" => true,
            _ => null,
        };
        if (includeResolved is null)
        {
            return ApiProblems.Validation("status", "Use open or all.");
        }

        var pageNumber = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (pageNumber < 1)
        {
            return ApiProblems.Validation("page", "Must be 1 or more.");
        }

        if (size is < 1 or > DomainReadEndpoints.MaximumPageSize)
        {
            return ApiProblems.Validation("pageSize", $"Must be between 1 and {DomainReadEndpoints.MaximumPageSize}.");
        }

        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = context.AlertEvents.AsNoTracking();
        if (includeResolved == false)
        {
            query = query.Where(alert => !alert.IsResolved);
        }

        if (scope.IsScoped)
        {
            var visibleNames = scope.Domains(context.Domains).Select(domain => domain.Name);
            query = query.Where(alert => visibleNames.Contains(alert.DomainName));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var alerts = await query
            .OrderByDescending(alert => alert.CreatedUtc)
            .ThenByDescending(alert => alert.Id)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);
        var subjects = alerts.Select(alert => alert.DomainName).Distinct().ToList();
        var domainIds = await scope.Domains(context.Domains.AsNoTracking())
            .Where(domain => subjects.Contains(domain.Name))
            .ToDictionaryAsync(domain => domain.Name, domain => domain.Id, cancellationToken);
        var items = alerts.Select(alert => new ApiAlert(
                alert.Id,
                alert.AlertType,
                AlertTypes.Find(alert.AlertType)?.DisplayName ?? alert.AlertType,
                alert.DomainName,
                domainIds.TryGetValue(alert.DomainName, out var domainId) ? new ApiNamedRef(domainId, alert.DomainName) : null,
                alert.Severity,
                alert.Title,
                alert.Message,
                alert.CreatedUtc,
                alert.IsResolved,
                alert.ResolvedUtc,
                !alert.IsResolved && AlertAcknowledgement.IsAcknowledgeable(alert.AlertType)))
            .ToList();
        return TypedResults.Ok(new ApiPage<ApiAlert>(items, pageNumber, size, totalCount));
    }

    private static async Task<Results<Ok<ApiAcknowledgement>, ProblemHttpResult>> AcknowledgeAsync(
        int id, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, IPsaTicketService psaTicketService, CancellationToken cancellationToken)
    {
        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var alert = await context.AlertEvents.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (alert is null || (scope.IsScoped && !await scope.Domains(context.Domains).AnyAsync(domain => domain.Name == alert.DomainName, cancellationToken)))
        {
            return ApiProblems.NotFound($"alert {id}");
        }

        if (alert.IsResolved || !AlertAcknowledgement.IsAcknowledgeable(alert.AlertType))
        {
            return ApiProblems.Conflict(alert.IsResolved
                ? "This alert is already closed."
                : "Only DMARC policy weakened and nameservers changed alerts can be acknowledged. Other alerts close themselves once the problem is fixed.");
        }

        var outcome = await AlertAcknowledgement.AcknowledgeAsync(context, AuditActor.FromPrincipal(user), id, psaTicketService, cancellationToken);
        return outcome switch
        {
            AcknowledgeOutcome.Acknowledged => TypedResults.Ok(new ApiAcknowledgement(true)),
            AcknowledgeOutcome.AcknowledgedButTicketNotClosed => TypedResults.Ok(new ApiAcknowledgement(false)),
            _ => ApiProblems.Conflict("This alert can't be acknowledged."),
        };
    }
}
```

In `ApiEndpoints.MapDotMarcApi`, add `AlertEndpoints.Map(api);` after `ImportEndpoints.Map(api);`.

Update the spec's `/alerts` and acknowledge rows (dotMARC records no acknowledged-by; acknowledging resolves the alert):
- `/alerts` items: "id, type, typeName, subject, domain (id, name; null when the subject isn't a domain the key can see), severity, title, message, raisedUtc, resolved, resolvedUtc, acknowledgeable."
- acknowledge: "200 with `{ ticketClosed }`, or 409 when the alert is already closed or isn't a policy or nameserver alert."

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertEndpointTests|FullyQualifiedName~AlertTypesTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc test/DotMarc.Tests docs/superpowers/specs/2026-10-05-public-api-design.md
git commit -m "List and acknowledge alerts over the API"
```

---

### Task 7: Expiring key alerts

**Files:**
- Modify: `src/DotMarc/Notifications/AlertingService.cs`
- Test: `test/DotMarc.Tests/Notifications/ApiKeyExpiryAlertingTests.cs`

**Interfaces:**
- Consumes: `AlertTypes.ApiKeyExpiring` (Task 6), `ApiKey` (Task 1), `AlertingService.EnsureAlertAsync/ResolveAllCopiesAsync` (existing, private).
- Produces: `AlertingService.ApiKeyExpiryWarning` (`TimeSpan.FromDays(14)`), `AlertingService.ApiKeyAlertSubject(ApiKey key) -> string` (`$"API key {key.Name} ({key.Prefix})"`).

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Notifications/ApiKeyExpiryAlertingTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class ApiKeyExpiryAlertingTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ApiKeyExpiryAlertingTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
        settings.Enabled = true;
        settings.TeamsWebhookUrl = "https://example.test/webhook";
        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
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

    private AlertingService CreateService(FakeAlertWebhookClient notifier) =>
        new(new FakeDbContextFactory(_connectionString), notifier, new PsaTicketService(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);

    private async Task<ApiKey> SeedKeyAsync(string name, DateTimeOffset expiresUtc)
    {
        await using var context = CreateContext();
        var apiKey = new ApiKey
        {
            Name = name, Prefix = "dmk_" + name[..Math.Min(8, name.Length)], Hash = Guid.NewGuid().ToString("N"),
            Role = new Role { Name = $"role for {name}", Permissions = [Permission.DomainsView] },
            CreatedBy = "Test Admin", CreatedUtc = DateTimeOffset.UtcNow.AddDays(-80), ExpiresUtc = expiresUtc,
        };
        context.ApiKeys.Add(apiKey);
        await context.SaveChangesAsync();
        return apiKey;
    }

    [Fact]
    public async Task AKeyExpiringWithin14Days_RaisesOneAlert_AndALaterOneDoesnt()
    {
        var soon = await SeedKeyAsync("soon", DateTimeOffset.UtcNow.AddDays(10));
        await SeedKeyAsync("later", DateTimeOffset.UtcNow.AddDays(30));
        var notifier = new FakeAlertWebhookClient();

        await CreateService(notifier).CheckPinnedDomainsAsync();

        await using var context = CreateContext();
        var alert = await context.AlertEvents.SingleAsync(candidate => candidate.AlertType == AlertTypes.ApiKeyExpiring);
        Assert.Equal(AlertingService.ApiKeyAlertSubject(soon), alert.DomainName);
        Assert.False(alert.IsResolved);
        Assert.Contains((AlertingService.ApiKeyAlertSubject(soon), AlertTypes.ApiKeyExpiring), notifier.Sent);
    }

    [Fact]
    public async Task RevokingTheKey_ResolvesItsAlert()
    {
        var soon = await SeedKeyAsync("revoked", DateTimeOffset.UtcNow.AddDays(5));
        var service = CreateService(new FakeAlertWebhookClient());
        await service.CheckPinnedDomainsAsync();
        await using (var context = CreateContext())
        {
            await ApiKeyManagementService.RevokeAsync(context, TestActors.Admin, soon.Id);
        }

        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync(candidate => candidate.AlertType == AlertTypes.ApiKeyExpiring)).IsResolved);
    }

    [Fact]
    public async Task OnceTheKeyHasExpired_ItsAlertResolves()
    {
        var expiring = await SeedKeyAsync("lapsed", DateTimeOffset.UtcNow.AddDays(3));
        var service = CreateService(new FakeAlertWebhookClient());
        await service.CheckPinnedDomainsAsync();
        await using (var context = CreateContext())
        {
            await context.ApiKeys.Where(key => key.Id == expiring.Id).ExecuteUpdateAsync(setters => setters.SetProperty(key => key.ExpiresUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync(candidate => candidate.AlertType == AlertTypes.ApiKeyExpiring)).IsResolved);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ApiKeyExpiryAlertingTests"`
Expected: build FAIL naming `AlertingService.ApiKeyAlertSubject`.

- [ ] **Step 3: Implement**

In `AlertingService.cs`, add to the class:

```csharp
    public static readonly TimeSpan ApiKeyExpiryWarning = TimeSpan.FromDays(14);

    /// <summary>What an expiring key's alert is about. Stored where a domain alert stores its domain name; a key's name
    /// and prefix never change, so the same key always has the same subject.</summary>
    public static string ApiKeyAlertSubject(ApiKey key) => $"API key {key.Name} ({key.Prefix})";

    /// <summary>Warns before an API key expires so whatever uses it doesn't break unannounced. The alert closes when the
    /// key is revoked or finally expires.</summary>
    private async Task CheckApiKeyExpiryAsync(DotMarcDbContext db, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var openSubjects = (await db.AlertEvents
            .Where(alert => alert.AlertType == AlertTypes.ApiKeyExpiring && !alert.IsResolved)
            .Select(alert => alert.DomainName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet();
        var keys = await db.ApiKeys.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var apiKey in keys)
        {
            var subject = ApiKeyAlertSubject(apiKey);
            var expiresSoon = apiKey.IsActive(nowUtc) && apiKey.ExpiresUtc - nowUtc <= ApiKeyExpiryWarning;
            if (expiresSoon)
            {
                await EnsureAlertAsync(db, settings, subject, AlertTypes.ApiKeyExpiring, "Warning", "API key expiring soon",
                    $"The API key '{apiKey.Name}' ({apiKey.Prefix}...) expires on {apiKey.ExpiresUtc:yyyy-MM-dd}. Create a replacement on the Access page, switch whatever uses this key over to it, then revoke this one.",
                    cancellationToken).ConfigureAwait(false);
            }
            else if (openSubjects.Contains(subject))
            {
                await ResolveAllCopiesAsync(subject, AlertTypes.ApiKeyExpiring, cancellationToken).ConfigureAwait(false);
            }
        }
    }
```

In `CheckPinnedDomainsAsync`, after `await CheckDnsHealthAsync(db, settings, domains, cancellationToken).ConfigureAwait(false);` add:

```csharp
        await CheckApiKeyExpiryAsync(db, settings, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ApiKeyExpiryAlertingTests|FullyQualifiedName~AlertingServiceTests|FullyQualifiedName~DnsHealthAlertingTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc test/DotMarc.Tests
git commit -m "Warn two weeks before an API key expires"
```

---

### Task 8: Access page tabs, with API keys

**Files:**
- Create: `src/DotMarc/Components/Shared/ApiKeysSection.razor`
- Modify: `src/DotMarc/Components/Pages/ManageAccess.razor` (split into People, Roles and API keys tabs)

**Interfaces:**
- Consumes: `ApiKeyManagementService.CreateAsync/RevokeAsync/ListAsync/LifetimeDays`, `AlertingService.ApiKeyExpiryWarning`, `AuditActorAccessor`, `CreateApiKeyError`.
- Produces: routes `/access` (People), `/access/people`, `/access/roles`, `/access/api-keys`.

There is no component test framework in the test project; this task is verified in the browser (Step 3). The service behaviour it calls is covered by Task 1.

- [ ] **Step 1: Write the section**

`src/DotMarc/Components/Shared/ApiKeysSection.razor`:

```razor
@using DotMarc.Audit
@using DotMarc.Data
@using DotMarc.Notifications
@using Microsoft.EntityFrameworkCore
@inject AuditActorAccessor AuditActorAccessor
@inject IDbContextFactory<DotMarcDbContext> DbFactory
@inject ISnackbar Snackbar
@inject IDialogService DialogService
@inject IJSRuntime JS

<MudPaper Class="pa-4 mb-4" Elevation="1">
    <div class="d-flex align-center mb-1">
        <MudText Typo="Typo.h6">API keys</MudText>
        <DocsLink Href="https://dotmarc.app/docs/api" Text="Using the API" Class="ml-2" />
    </div>
    <MudText Typo="Typo.body2" Class="mb-4 mud-text-secondary">
        Keys let scripts and other tools use dotMARC's API with a role's permissions. A key for a Viewer can be limited to
        certain groups. Roles that can manage access aren't offered. To change what a key can do, create a new one and
        revoke the old. The API reference is at <MudLink Href="/api/v1/openapi.json" Target="_blank">/api/v1/openapi.json</MudLink>.
    </MudText>

    @if (_newSecret is not null)
    {
        <MudAlert Severity="Severity.Success" Class="mb-4" ShowCloseIcon="true" CloseIconClicked="@(() => _newSecret = null)">
            <MudText Typo="Typo.body2" Class="mb-2">Copy this key now. dotMARC only stores a hash of it, so it can't be shown again.</MudText>
            <div class="d-flex align-center">
                <MudText Typo="Typo.body2" Style="font-family: monospace; word-break: break-all;">@_newSecret</MudText>
                <MudIconButton Icon="@Icons.Material.Filled.ContentCopy" Size="Size.Small" Class="ml-2" aria-label="Copy key" OnClick="CopySecretAsync" />
            </div>
        </MudAlert>
    }

    <MudGrid>
        <MudItem xs="12" md="4">
            <MudTextField @bind-Value="_name" Label="Name" Placeholder="Halo sync" Variant="Variant.Outlined" Immediate="true"
                          Error="@(_nameError is not null)" ErrorText="@_nameError" MaxLength="@ApiKeyManagementService.MaximumNameLength" />
        </MudItem>
        <MudItem xs="12" md="3">
            <MudSelect T="int?" @bind-Value="_roleId" Label="Role" Variant="Variant.Outlined">
                @foreach (var role in _offeredRoles)
                {
                    <MudSelectItem T="int?" Value="@role.Id">@role.Name</MudSelectItem>
                }
            </MudSelect>
        </MudItem>
        <MudItem xs="12" md="2">
            <MudSelect T="int" @bind-Value="_lifetimeDays" Label="Expires after" Variant="Variant.Outlined">
                @foreach (var days in ApiKeyManagementService.LifetimeDays)
                {
                    <MudSelectItem T="int" Value="@days">@days days</MudSelectItem>
                }
            </MudSelect>
        </MudItem>
        <MudItem xs="12" md="3" Class="d-flex align-start">
            <MudButton Variant="Variant.Filled" Color="Color.Primary" Class="button-beside-input" StartIcon="@Icons.Material.Filled.Key"
                       Disabled="@(_roleId is null || _busy)" OnClick="CreateAsync">Create key</MudButton>
        </MudItem>
        @if (SelectedRoleIsScopable)
        {
            <MudItem xs="12">
                <MudSelect T="int" Label="Limit to groups (none means every group)" MultiSelection="true" Variant="Variant.Outlined"
                           SelectedValues="_groupIds" SelectedValuesChanged="@(ids => _groupIds = ids.ToHashSet())"
                           ToStringFunc="@(id => _groups.FirstOrDefault(group => group.Id == id)?.Name ?? "")">
                    @foreach (var group in _groups)
                    {
                        <MudSelectItem T="int" Value="@group.Id">@group.Name</MudSelectItem>
                    }
                </MudSelect>
            </MudItem>
        }
    </MudGrid>

    @if (_keys is { Count: > 0 })
    {
        <MudTable Items="_keys" Hover="true" Dense="true" Class="mt-4" T="ApiKey">
            <HeaderContent>
                <MudTh>Name</MudTh>
                <MudTh>Key</MudTh>
                <MudTh>Role</MudTh>
                <MudTh>Groups</MudTh>
                <MudTh>Created</MudTh>
                <MudTh>Expires</MudTh>
                <MudTh>Last used</MudTh>
                <MudTh></MudTh>
            </HeaderContent>
            <RowTemplate>
                <MudTd Style="@(IsInUse(context) ? "" : "opacity: 0.55")">@context.Name</MudTd>
                <MudTd Style="font-family: monospace">@context.Prefix...</MudTd>
                <MudTd>@(context.Role?.Name ?? "(deleted role)")</MudTd>
                <MudTd>@(context.ScopedGroups.Count == 0 ? "All" : string.Join(", ", context.ScopedGroups.Select(group => group.Name)))</MudTd>
                <MudTd>@context.CreatedUtc.ToString("yyyy-MM-dd") by @context.CreatedBy</MudTd>
                <MudTd>
                    @context.ExpiresUtc.ToString("yyyy-MM-dd")
                    @if (context.RevokedUtc is not null)
                    {
                        <MudChip T="string" Size="Size.Small" Class="ml-1">Revoked</MudChip>
                    }
                    else if (context.ExpiresUtc <= _nowUtc)
                    {
                        <MudChip T="string" Size="Size.Small" Class="ml-1">Expired</MudChip>
                    }
                    else if (context.ExpiresUtc - _nowUtc <= AlertingService.ApiKeyExpiryWarning)
                    {
                        <MudChip T="string" Size="Size.Small" Color="Color.Warning" Class="ml-1">Expires soon</MudChip>
                    }
                </MudTd>
                <MudTd>@(context.LastUsedUtc?.ToString("yyyy-MM-dd HH:mm") ?? "Never")</MudTd>
                <MudTd>
                    @if (context.RevokedUtc is null)
                    {
                        <MudButton Size="Size.Small" Variant="Variant.Outlined" Color="Color.Error" OnClick="@(() => RevokeAsync(context))">Revoke</MudButton>
                    }
                </MudTd>
            </RowTemplate>
        </MudTable>
    }
</MudPaper>

@code {
    private List<ApiKey>? _keys;
    private List<Role> _offeredRoles = [];
    private List<Group> _groups = [];
    private string _name = "";
    private string? _nameError;
    private int? _roleId;
    private int _lifetimeDays = 90;
    private HashSet<int> _groupIds = [];
    private string? _newSecret;
    private bool _busy;
    private DateTimeOffset _nowUtc = DateTimeOffset.UtcNow;

    private bool SelectedRoleIsScopable => _offeredRoles.FirstOrDefault(role => role.Id == _roleId)?.IsScopable == true;

    private bool IsInUse(ApiKey key) => key.IsActive(_nowUtc);

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _nowUtc = DateTimeOffset.UtcNow;
        _keys = await ApiKeyManagementService.ListAsync(db);
        _offeredRoles = (await db.Roles.AsNoTracking().OrderBy(role => role.Name).ToListAsync())
            .Where(role => !role.Permissions.Contains(Permission.AccessManage))
            .ToList();
        _groups = await db.Groups.AsNoTracking().OrderBy(group => group.Name).ToListAsync();
    }

    private async Task CreateAsync()
    {
        if (_roleId is not { } roleId)
        {
            return;
        }

        _busy = true;
        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            var groupIds = SelectedRoleIsScopable ? _groupIds.ToList() : [];
            var result = await ApiKeyManagementService.CreateAsync(db, await AuditActorAccessor.GetAsync(), _name, roleId, groupIds, _lifetimeDays);
            _nameError = result.Error switch
            {
                CreateApiKeyError.InvalidName => $"Give the key a name of up to {ApiKeyManagementService.MaximumNameLength} characters.",
                CreateApiKeyError.NameInUse => "A key in use already has that name.",
                _ => null,
            };
            if (result.Error is CreateApiKeyError.RoleNotFound or CreateApiKeyError.RoleCanManageAccess or CreateApiKeyError.InvalidLifetime)
            {
                Snackbar.Add("That role can't be given to a key. Choose another.", Severity.Error);
            }

            if (result.Secret is not null)
            {
                _newSecret = result.Secret;
                _name = "";
                _groupIds = [];
                await LoadAsync();
            }
        }
        catch (Exception)
        {
            Snackbar.Add("Failed to create the key. Try again.", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RevokeAsync(ApiKey key)
    {
        var confirmed = await DialogService.ShowMessageBoxAsync("Revoke API key",
            $"Revoke {key.Name}? Anything using it stops working straight away. This can't be undone.", yesText: "Revoke", cancelText: "Cancel");
        if (confirmed != true)
        {
            return;
        }

        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await ApiKeyManagementService.RevokeAsync(db, await AuditActorAccessor.GetAsync(), key.Id);
            await LoadAsync();
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to revoke {key.Name}. Try again.", Severity.Error);
        }
    }

    private async Task CopySecretAsync()
    {
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", _newSecret);
        Snackbar.Add("Key copied.", Severity.Success);
    }
}
```

If `ShowMessageBoxAsync`'s signature in MudBlazor 9.8 differs, follow how `ManageAccess.razor` (or another page) already confirms a removal, and record the ruling.

- [ ] **Step 2: Split the Access page into tabs**

The page today is two stacked cards, Roles then Access grants. It becomes three tabs, following DomainDetail.razor's
pattern of a slug in the URL so each tab can be linked to and survives a reload:

| Tab | Slug | Content |
| --- | --- | --- |
| People | `people` (default) | the existing "Access grants" card's contents |
| Roles | `roles` | the existing "Roles" card's contents |
| API keys | `api-keys` | `<ApiKeysSection />` |

People comes first because granting and revoking people is what the page is opened for most.

1. Add a second route under the first:

```razor
@page "/access"
@page "/access/{Tab}"
```

and `@inject NavigationManager Navigation` with the other injects.

2. Replace the two `<MudPaper>` cards (Roles, then Access grants) with tabs. Move each card's inner markup unchanged into
its panel, dropping the card's own `<MudText Typo="Typo.h6">` heading (the tab names it) and keeping everything else,
including the Roles card's `Class` spacing on inner elements:

```razor
<MudTabs ActivePanelIndex="_activeTabIndex" ActivePanelIndexChanged="OnActiveTabIndexChanged" Elevation="1" Rounded="true" PanelClass="pa-4">
    <MudTabPanel Text="People" Icon="@Icons.Material.Filled.People">
        @* the former "Access grants" card's contents, minus its heading *@
    </MudTabPanel>
    <MudTabPanel Text="Roles" Icon="@Icons.Material.Filled.Badge">
        @* the former "Roles" card's contents, minus its heading *@
    </MudTabPanel>
    <MudTabPanel Text="API keys" Icon="@Icons.Material.Filled.Key">
        <ApiKeysSection />
    </MudTabPanel>
</MudTabs>
```

(The two comments mark where the moved markup goes; they are not left in the file.)

`ApiKeysSection` renders its own `<MudPaper>`; inside a tab that doubles the card, so change its outer element from
`<MudPaper Class="pa-4 mb-4" Elevation="1">` to a plain `<div>` and drop its `<MudText Typo="Typo.h6">API keys</MudText>`
heading, keeping the docs link and the explanation.

3. Update the intro paragraph under "Manage access" to cover keys:

```razor
<MudText Typo="Typo.body2" Class="mb-4 mud-text-secondary">
    Roles are built from fine-grained permissions and control what someone can see and do. Admin and Viewer come built
    in; custom roles can cover any subset. A Viewer can optionally be scoped to specific Groups, for external clients who
    should only see their own domains. API keys get a role the same way, for scripts and other tools.
</MudText>
```

4. In `@code`, add:

```csharp
    private static readonly string[] TabSlugs = ["people", "roles", "api-keys"];

    [Parameter]
    public string? Tab { get; set; }

    private int _activeTabIndex;

    protected override void OnParametersSet()
    {
        var index = Tab is null ? -1 : Array.IndexOf(TabSlugs, Tab.ToLowerInvariant());
        _activeTabIndex = index >= 0 ? index : 0;
    }

    private void OnActiveTabIndexChanged(int index)
    {
        _activeTabIndex = index;
        var slug = TabSlugs[index >= 0 && index < TabSlugs.Length ? index : 0];
        Navigation.NavigateTo($"/access/{slug}", replace: true);
    }
```

If `ManageAccess.razor` already overrides `OnParametersSet`, put the two lines into the existing override.

5. The role list on the People tab and the key form's role list read roles when each loads. A role added on the Roles
tab must show in the other two without a reload: if People's grant form caches roles in a field loaded once in
`OnInitializedAsync`, reload that list after a role is added, renamed or removed (call the page's existing load method
at the end of those handlers, if it doesn't already). `ApiKeysSection` reloads its roles every time its panel renders,
since MudTabs only renders the active panel: confirm by switching tabs after adding a role; if it doesn't, add
`KeepPanelsAlive="false"` (the default) and leave `OnInitializedAsync` loading as written.

Run: `dotnet build src/DotMarc`
Expected: build succeeds with no new warnings.

- [ ] **Step 3: Verify in the browser (playwright-edge, never install Chrome)**

Stop any running demo, then run the app in demo mode: `dotnet run --project src/DotMarc --Demo:Enabled=true` (in the background). With the playwright-edge tools:
1. Open the demo page and sign in as the admin persona, then open `/access`. Expect the People tab, with the grants
   table as before. Click Roles: the URL becomes `/access/roles` and the roles table shows; reload and Roles is still
   selected. Open `/access/api-keys` directly: the API keys tab is selected. Open `/access/nonsense`: People.
   Add a role on the Roles tab, switch to API keys, and check the new role is in the key form's role list.
2. Create a key named "Browser check" with the Viewer role, limited to one group, 30 days. Expect the success alert with a `dmk_` key and a copy button; the table lists the key with its prefix, role, group and "Never".
3. Confirm the role list has no role that includes AccessManage (Admin isn't offered).
4. Call `/api/v1/groups` with the copied key (`browser_evaluate` with `fetch('/api/v1/groups', { headers: { Authorization: 'Bearer ' + key } })`) and expect 200 with only the chosen group. Reload `/access`: "Last used" now has a time.
5. Revoke the key, confirm in the dialog; the row dims and shows "Revoked"; the same fetch now returns 401.
6. Take a screenshot of each tab at phone width (390px) and check none scrolls sideways and the tab bar fits or
   scrolls within itself.
Stop the demo afterwards.

- [ ] **Step 4: Commit**

```bash
git add src/DotMarc
git commit -m "Split the Access page into People, Roles and API keys tabs"
```

---

### Task 9: OpenAPI document and its committed copy

**Files:**
- Create: `src/DotMarc/Api/ApiDocument.cs`, `scripts/update-openapi.mjs`, `website/data/openapi/dotmarc-api.json` (generated)
- Modify: `src/DotMarc/DotMarc.csproj`, `src/DotMarc/Program.cs`, `scripts/release.mjs`
- Test: `test/DotMarc.Tests/Api/OpenApiDocumentTests.cs`

**Interfaces:**
- Consumes: `ApiPermissionMetadata`, every endpoint's `WithName/WithSummary/WithDescription`, `ApiTestHost`.
- Produces: `ApiDocument.AddDotMarcOpenApi(this IServiceCollection)`, `ApiDocument.MapDotMarcOpenApi(this WebApplication)`, `ApiDocument.Version` (the `VersionPrefix`), `ApiDocument.DocumentPath` (`"/api/v1/openapi.json"`).

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Api/OpenApiDocumentTests.cs`:

```csharp
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotMarc.Tests.Internal;
using Microsoft.OpenApi.Reader;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed partial class OpenApiDocumentTests : IAsyncLifetime
{
    private const string UpdateVariable = "DOTMARC_UPDATE_OPENAPI";
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public OpenApiDocumentTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dotMARC.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Couldn't find dotMARC.sln above the test output.");
    }

    private static string CommittedPath() => Path.Combine(RepositoryRoot(), "website", "data", "openapi", "dotmarc-api.json");

    /// <summary>Sorted keys and two-space indentation, so the committed copy diffs cleanly.</summary>
    private static string Normalize(string json) =>
        Sort(JsonNode.Parse(json))!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject jsonObject => new JsonObject(jsonObject.OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => KeyValuePair.Create(property.Key, Sort(property.Value?.DeepClone())))),
        JsonArray jsonArray => new JsonArray(jsonArray.Select(item => Sort(item?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    private async Task<string> FetchDocumentAsync()
    {
        using var client = _host.ClientFor(null);
        var response = await client.GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task TheCommittedCopy_MatchesTheApp()
    {
        var current = Normalize(await FetchDocumentAsync());
        var path = CommittedPath();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, current);
            return;
        }

        var committed = File.Exists(path) ? (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n") : "";
        Assert.True(committed == current,
            $"The API changed; run `node scripts/update-openapi.mjs` (or set {UpdateVariable}=1 and run this test) and commit website/data/openapi/dotmarc-api.json.");
    }

    [Fact]
    public async Task EveryOperation_HasASummaryAndItsPermission()
    {
        using var document = JsonDocument.Parse(await FetchDocumentAsync());
        var operations = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(operation => (Name: $"{operation.Name.ToUpperInvariant()} {path.Name}", Body: operation.Value)))
            .ToList();

        Assert.Equal(12, operations.Count);
        Assert.All(operations, operation =>
        {
            Assert.True(operation.Body.TryGetProperty("summary", out _), $"{operation.Name} has no summary");
            Assert.True(operation.Body.TryGetProperty("x-dotmarc-permission", out _), $"{operation.Name} has no permission");
        });
        Assert.All(document.RootElement.GetProperty("paths").EnumerateObject(), path => Assert.StartsWith("/api/v1/", path.Name));
    }

    [Fact]
    public async Task TheDocument_IsValidOpenApi3_WithABearerScheme()
    {
        var json = await FetchDocumentAsync();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        var readResult = await OpenApiDocument.LoadAsync(stream, "json");

        Assert.Empty(readResult.Diagnostic!.Errors);
        using var document = JsonDocument.Parse(json);
        Assert.StartsWith("3.", document.RootElement.GetProperty("openapi").GetString());
        Assert.Equal("bearer", document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("bearer").GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task TheDocumentsVersion_IsTheProjectVersion()
    {
        var props = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "Directory.Build.props"));
        var projectVersion = VersionPrefixPattern().Match(props).Groups[1].Value;
        using var document = JsonDocument.Parse(await FetchDocumentAsync());

        Assert.Equal(projectVersion, document.RootElement.GetProperty("info").GetProperty("version").GetString());
    }

    [GeneratedRegex("<VersionPrefix>([^<]+)</VersionPrefix>")]
    private static partial Regex VersionPrefixPattern();
}
```

The 12 operations: GET groups, GET tags, GET domains, GET domains/{id}, GET reports/summary, POST domains, PUT groups, PUT tags, PUT monitoring, POST import, GET alerts, POST acknowledge. If `OpenApiDocument.LoadAsync`'s namespace or signature differs in the Microsoft.OpenApi version the app references, use that version's reader (for 1.x: `new OpenApiStringReader().Read(json, out var diagnostic)` from `Microsoft.OpenApi.Readers`) and record the ruling.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~OpenApiDocumentTests"`
Expected: FAIL (the document endpoint returns 404 problem+json).

- [ ] **Step 3: Add the package and the document**

In `src/DotMarc/DotMarc.csproj`, add beside the other ASP.NET packages:

```xml
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.11" />
```

(Use the newest 10.0.x patch `dotnet restore` accepts if 10.0.11 doesn't exist.)

`src/DotMarc/Api/ApiDocument.cs`:

```csharp
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;

namespace DotMarc.Api;

/// <summary>The public API's OpenAPI document, served anonymously at /api/v1/openapi.json. Only /api/v1 endpoints are
/// described; servers are left out so the committed copy doesn't depend on where it was generated.</summary>
public static class ApiDocument
{
    public const string DocumentName = "v1";
    public const string DocumentPath = "/api/v1/openapi.json";
    private const string PermissionExtension = "x-dotmarc-permission";

    /// <summary>The project's VersionPrefix, from the informational version without any "+commit" suffix.</summary>
    public static string Version { get; } =
        (typeof(ApiDocument).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static IServiceCollection AddDotMarcOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
            options.ShouldInclude = description => description.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true;

            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "dotMARC API",
                    Version = Version,
                    Description = "Read domains, DNS health, DMARC report summaries, groups, tags and alerts, and add, import and organise domains. Authenticate with an API key from dotMARC's Access page: `Authorization: Bearer dmk_...`. Each operation needs the permission named in its description, and a key limited to certain groups only sees their domains. Each key may make 120 requests a minute.",
                };
                document.Servers = [];
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "dmk_ API key",
                        Description = "An API key created on dotMARC's Access page.",
                    },
                };
                document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] }];
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, cancellationToken) =>
            {
                var permission = context.Description.ActionDescriptor.EndpointMetadata.OfType<ApiPermissionMetadata>().FirstOrDefault();
                if (permission is not null)
                {
                    operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                    operation.Extensions[PermissionExtension] = new JsonNodeExtension(JsonValue.Create(permission.Permission.ToString()));
                    operation.Description = $"{operation.Description}\n\nNeeds the {permission.Permission} permission.".TrimStart();
                }

                return Task.CompletedTask;
            });

            options.AddSchemaTransformer((schema, context, cancellationToken) =>
            {
                if (ApiExamples.For(context.JsonTypeInfo.Type) is { } example)
                {
                    schema.Example = example;
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }

    public static void MapDotMarcOpenApi(this WebApplication app) =>
        app.MapOpenApi("/api/{documentName}/openapi.json").AllowAnonymous();
}

/// <summary>Example values for the API's main types, serialized the way the API serializes them (web defaults, enums as
/// strings).</summary>
internal static class ApiExamples
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly DateTimeOffset ExampleTime = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);

    private static readonly Dictionary<Type, object> Samples = new()
    {
        [typeof(ApiDomain)] = new ApiDomain(42, "contoso.com", true, [new ApiNamedRef(3, "Contoso")], [new ApiNamedRef(7, "Microsoft 365")], ExampleTime, 0.987),
        [typeof(ApiGroup)] = new ApiGroup(3, "Contoso", 12),
        [typeof(ApiTag)] = new ApiTag(7, "Microsoft 365", "Primary", 30),
        [typeof(ApiCheck)] = new ApiCheck("Ok", ExampleTime, null),
        [typeof(ApiSource)] = new ApiSource("203.0.113.25", 1480, "Pass", "Pass", "None"),
        [typeof(ApiAlert)] = new ApiAlert(901, "SpfRecordBroken", "SPF record broken", "contoso.com", new ApiNamedRef(42, "contoso.com"), "Warning",
            "SPF record broken", "contoso.com's SPF record has more than 10 DNS lookups.", ExampleTime, false, null, false),
        [typeof(ApiAddDomainRequest)] = new ApiAddDomainRequest("contoso.com"),
        [typeof(ApiSetGroupsRequest)] = new ApiSetGroupsRequest([3, 5]),
        [typeof(ApiSetTagsRequest)] = new ApiSetTagsRequest([7]),
        [typeof(ApiSetMonitoringRequest)] = new ApiSetMonitoringRequest(true),
        [typeof(ApiImportRequest)] = new ApiImportRequest("skip", "skip",
            [new ApiImportDomain("contoso.com", ["Contoso"], ["Microsoft 365"], true), new ApiImportDomain("fabrikam.com", ["Fabrikam"], null, null)]),
        [typeof(ApiAcknowledgement)] = new ApiAcknowledgement(true),
    };

    public static JsonNode? For(Type type) =>
        Samples.TryGetValue(type, out var sample) ? JsonSerializer.SerializeToNode(sample, type, SerializerOptions) : null;
}
```

Microsoft.AspNetCore.OpenApi 10 builds on Microsoft.OpenApi 2.x, where the model types are in `Microsoft.OpenApi` and extensions use `JsonNodeExtension`. If the compiler reports different names (for example `OpenApiSecuritySchemeReference` or `JsonNodeExtension` missing), use that version's equivalents with the same output and record the ruling; the tests define the required output.

In `Program.cs`: after `builder.Services.AddDotMarcApi(builder.Configuration);` add `builder.Services.AddDotMarcOpenApi();`, and after `app.MapDotMarcApi();` add `app.MapDotMarcOpenApi();`.

- [ ] **Step 4: Generate the committed copy and run the tests**

`scripts/update-openapi.mjs`:

```js
#!/usr/bin/env node

// Regenerates website/data/openapi/dotmarc-api.json from the running API, by running the test that compares them with
// DOTMARC_UPDATE_OPENAPI=1. Needs Docker, like the rest of the test suite.
import {execFileSync} from 'node:child_process';
import {dirname, join} from 'node:path';
import {fileURLToPath} from 'node:url';

const repositoryRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
execFileSync('dotnet', ['test', 'test/DotMarc.Tests', '--filter', 'FullyQualifiedName~OpenApiDocumentTests.TheCommittedCopy_MatchesTheApp'], {
  cwd: repositoryRoot,
  stdio: 'inherit',
  env: {...process.env, DOTMARC_UPDATE_OPENAPI: '1'},
});
console.log('[openapi] Updated website/data/openapi/dotmarc-api.json');
```

Run: `node scripts/update-openapi.mjs`
Expected: the test passes and `website/data/openapi/dotmarc-api.json` exists. Read it: 12 paths under `/api/v1/`, `info.version` `0.7.3` (the current VersionPrefix), a `bearer` scheme, no `servers`, examples on the main schemas.

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~OpenApiDocumentTests"`
Expected: PASS.

- [ ] **Step 5: Keep the copy in step with releases**

In `scripts/release.mjs`:

1. Below `const blogDirectory = ...` add:

```js
const openApiFile = join(repositoryRoot, 'website', 'data', 'openapi', 'dotmarc-api.json');
const openApiRelativePath = 'website/data/openapi/dotmarc-api.json';
```

2. In `check`, after the project version check, add:

```js
  const openApiVersion = JSON.parse(readFileSync(openApiFile, 'utf8')).info?.version;
  if (openApiVersion !== version) {
    fail(`${openApiRelativePath} is for ${openApiVersion}, expected ${version}. Run: node scripts/update-openapi.mjs`);
  }
```

3. In `prepare`, inside the `if (currentProjectVersion() !== version) { ... }` block, after the "Updated project version" log, add:

```js
    run('node', [join('scripts', 'update-openapi.mjs')]);
    console.log(`[release] Regenerated ${openApiRelativePath} for ${version}`);
```

4. In `tag`, allow and stage the file: change the filter to

```js
    .filter((file) => file !== 'Directory.Build.props' && file !== `website/blog/${blogFile}` && file !== openApiRelativePath);
```

and the add to

```js
  run('git', ['add', 'Directory.Build.props', `website/blog/${blogFile}`, openApiRelativePath]);
```

Run: `node scripts/release.mjs check 0.7.3 --allow-existing-tag`
Expected: `[release] v0.7.3 is ready: ...` (the copy's version matches the current project version).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test test/DotMarc.Tests > .superpowers/sdd/2026-10-05-public-api/task9-suite.txt 2>&1; tail -5 .superpowers/sdd/2026-10-05-public-api/task9-suite.txt`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc test/DotMarc.Tests scripts website/data/openapi
git commit -m "Describe the API in OpenAPI and keep a copy for the website"
```

---

### Task 10: API documentation page

**Files:**
- Create: `website/docs/api.mdx`
- Modify: `website/docs/permissions-and-access.mdx` (one paragraph linking to the API page)

- [ ] **Step 1: Write the page**

Match the front matter and heading style of `website/docs/import-domains.mdx` (read it first). Content, no em dashes:

~~~mdx
---
title: API
description: Use dotMARC from scripts and other tools with an API key.
---

# API

dotMARC has a JSON API for the things MSPs most often script: listing domains and their DNS health, pulling DMARC report
summaries for client reporting, adding or importing domains, organising them into groups and tags, and acknowledging
alerts. Everything else stays in the web app for now.

The [interactive reference](/api) lists every endpoint with examples. The same document is served by your own dotMARC at
`/api/v1/openapi.json`.

## Create a key

On the **Access** page's **API keys** tab, give the key a name, choose a role and how long it lasts (30, 90, 180 or 365
days), and select **Create key**. Copy the key straight away: dotMARC keeps only a hash of it, so it can't be shown again.

- A key can do what its role allows. For a Viewer you can also limit the key to certain groups, so it only sees those
  clients' domains.
- Roles that can manage access aren't offered. Keys can't create keys or change who has access.
- A key can't be edited. To change what it can do, create a new key and revoke the old one.
- Two weeks before a key expires, dotMARC raises an **API key expiring** alert through your alert channels.

## Authenticate

Send the key as a bearer token:

```bash
curl https://dotmarc.example.com/api/v1/domains \
  -H "Authorization: Bearer dmk_..."
```

A missing, expired or revoked key gets `401`. A key whose role lacks the permission an endpoint needs gets `403`.

## Keys limited to groups

A key limited to groups sees only domains in at least one of its groups, and only those groups. A domain or alert outside
them is reported as not found (`404`). It can't add or import domains, since a new domain isn't in any group yet. When it
sets a domain's groups, groups outside its own are kept.

## Limits

Each key can make 120 requests a minute. Over that, dotMARC answers `429` with a `Retry-After` header giving the seconds
to wait. Lists return 50 items a page by default and at most 200; use `page` and `pageSize`.

## Errors

Errors use the standard problem details format (`application/problem+json`), with a `title`, a `detail` saying what to
do, and for invalid input an `errors` object naming each field.

| Status | Meaning |
| --- | --- |
| 400 | The request is invalid. `errors` says which field. |
| 401 | No key, or the key is unknown, expired or revoked. |
| 403 | The key's role doesn't allow this, or a group-limited key tried something it can't. |
| 404 | No such item, or the key can't see it. |
| 409 | The domain already exists, or the alert can't be acknowledged. |
| 429 | Too many requests this minute. Wait for `Retry-After`. |

## Importing domains

`POST /api/v1/domains/import` takes up to 500 domains and behaves like the [Import domains](./import-domains) page.
`existingDomains` is `skip` (default), `add` or `match`, and `unknownNames` is `skip` (default) or `create`. Add
`?dryRun=true` to see what would happen without changing anything.
~~~

If the website has no `/api` route yet (the OpenAPI plugin isn't installed), keep the link; it starts working when the
plugin is configured to publish there. Mention this in the hand-off.

In `website/docs/permissions-and-access.mdx`, add after the roles section:

```mdx
API keys are granted a role the same way people are. See [API](./api).
```

- [ ] **Step 2: Check the website builds**

Run (in `website/`): `npm run typecheck`
Expected: no errors. (A full `npm run build` will warn about the `/api` link until the plugin is installed; that is expected.)

- [ ] **Step 3: Commit**

```bash
git add website/docs
git commit -m "Document the API"
```
