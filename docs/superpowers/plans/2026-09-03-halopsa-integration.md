# HaloPSA Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a HaloPSA connector that opens a Halo ticket when a dotMARC alert fires (routed to the right Halo client via the domain's Group), closes that ticket when the alert resolves in dotMARC, and resolves the alert back when a tech closes the ticket directly in Halo.

**Architecture:** A new `HaloPsaSettings` singleton DB row (mirroring `NotificationSettings`) holds non-secret Halo config; the API client secret lives in the existing Azure Key Vault when opted into, or Postgres (Data-Protection-encrypted) otherwise, behind an `IHaloSecretStore` abstraction selected the same way `IMtaStsHostProvisioner` picks Caddy vs. Azure. `AlertingService` calls a new `IPsaTicketService` (best-effort, same try/catch shape as the existing Teams/webhook call) which resolves the domain's Halo client via its Group, and talks to Halo through `IHaloPsaClient`. A new inbound webhook endpoint lets Halo tell dotMARC when a ticket closes.

**Tech Stack:** ASP.NET Core 10 / Blazor Server, EF Core + Npgsql, MudBlazor, `Azure.Identity` + `Azure.Security.KeyVault.Secrets`, `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`, xUnit + `Testcontainers.PostgreSql`.

**Spec:** `docs/superpowers/specs/2026-09-02-halopsa-integration-design.md`

## Global Constraints

- One Halo account per deployment; at most one active PSA connector at a time (no multi-PSA).
- The Halo API client secret is never stored in plaintext anywhere the app can read it back as plaintext without going through `IHaloSecretStore`; it never appears in `HaloPsaSettings`'s own public-facing surface.
- `HaloPsaSettings.WebhookSecret` is plaintext, same trust tier as `NotificationSettings.TeamsWebhookUrl`/`GenericWebhookUrl` today.
- Every Halo API call made from the alerting path (`CreateTicketAsync`, `CloseTicketAsync`) is best-effort: caught, logged as a warning, never allowed to block alert creation/resolution.
- Follow this codebase's static-class-over-caller-supplied-`DotMarcDbContext` convention for any new CRUD service (see `NotificationSettingsService`, `GroupManagementService`).
- Follow the existing typed-`HttpClient` + interface pattern for any new HTTP client (see `TeamsWebhookClient`, `GenericWebhookClient`).
- Migration commands: `dotnet ef migrations add <Name> --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`, run from the repo root.
- Tests: `Testcontainers.PostgreSql` (see `PostgresContainerFixture`, the `[Collection("Postgres")]` pattern) for anything touching the DB; `FakeHttpMessageHandler` for anything making outbound HTTP calls; `WebApplicationFactory<Program>` (see `DemoSignInEndpointTests`) for the new minimal-API endpoint.
- No automated test coverage for Razor/Blazor component rendering — established gap in this codebase (verified manually), not something to build test infrastructure for as a side effect of this feature.

---

## Task 1: Data model, DbContext wiring, and migration

**Files:**
- Create: `src/DotMarc/Notifications/HaloPsaSettings.cs`
- Modify: `src/DotMarc/Data/Group.cs`
- Modify: `src/DotMarc/Data/Domain.cs`
- Modify: `src/DotMarc/Notifications/AlertEvent.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Create (generated): `src/DotMarc/Migrations/<timestamp>_AddHaloPsaIntegration.cs` and `.Designer.cs`

**Interfaces:**
- Produces: `HaloPsaSettings` entity (`Id`, `Enabled`, `AccountName`, `AuthServerUrl`, `ResourceServerUrl`, `ClientId`, `ClientSecretConfigured`, `TicketTypeId`, `DefaultPriorityId`, `ClosedStatusId`, `WebhookSecret`, `ProtectedClientSecret` — the last one internal-use-only, see Task 2). `Group.HaloClientId` (`int?`). `Domain.HaloClientId` (`int?`). `AlertEvent.ExternalTicketProvider`/`ExternalTicketId` (`string?`).

- [ ] **Step 1: Create the `HaloPsaSettings` entity**

```csharp
// src/DotMarc/Notifications/HaloPsaSettings.cs
namespace DotMarc.Notifications;

/// <summary>Singleton settings row for the HaloPSA PSA integration — same "exactly one row,
/// seeded via migration HasData" pattern as NotificationSettings. ProtectedClientSecret is
/// written and read only by DatabaseHaloSecretStore (see IHaloSecretStore); every other reader
/// of this entity should treat ClientSecretConfigured as the only signal about the secret's
/// presence, never the protected value itself.</summary>
public sealed class HaloPsaSettings
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string? AccountName { get; set; }
    public string? AuthServerUrl { get; set; }
    public string? ResourceServerUrl { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
    public int? TicketTypeId { get; set; }
    public int? DefaultPriorityId { get; set; }
    public int? ClosedStatusId { get; set; }
    public string? WebhookSecret { get; set; }
    public string? ProtectedClientSecret { get; set; }
}
```

- [ ] **Step 2: Add `HaloClientId` to `Group`**

```csharp
// src/DotMarc/Data/Group.cs — add alongside the existing properties
public int? HaloClientId { get; set; }
```

- [ ] **Step 3: Add `HaloClientId` to `Domain`**

```csharp
// src/DotMarc/Data/Domain.cs — add alongside the existing MTA-STS properties
public int? HaloClientId { get; set; } // override; null means "use the Group's mapping"
```

- [ ] **Step 4: Add ticket correlation fields to `AlertEvent`**

```csharp
// src/DotMarc/Notifications/AlertEvent.cs — add after Message
public string? ExternalTicketProvider { get; set; } // "HaloPSA" today; null if no ticket was created
public string? ExternalTicketId { get; set; }
```

- [ ] **Step 5: Register the new `DbSet` and seed row in `DotMarcDbContext`**

Add to the `DbSet` list:

```csharp
public DbSet<HaloPsaSettings> HaloPsaSettings => Set<HaloPsaSettings>();
```

Add to `OnModelCreating`, after the existing `NotificationSettings` seed:

```csharp
modelBuilder.Entity<HaloPsaSettings>().HasData(new HaloPsaSettings { Id = 1 });
```

- [ ] **Step 6: Generate the migration**

Run: `dotnet ef migrations add AddHaloPsaIntegration --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

Open the generated migration and confirm it contains: the `HaloPsaSettings` table (with the seed row's `InsertData`), `HaloClientId` columns added to `Groups` and `Domains`, and `ExternalTicketProvider`/`ExternalTicketId` columns added to `AlertEvents`. If anything is missing, the entity/`OnModelCreating` change above wasn't picked up — fix it before proceeding, don't hand-edit the migration.

- [ ] **Step 7: Apply and verify against a real database**

```powershell
docker compose up postgres -d
dotnet ef database update --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj
```

Expected: completes with no errors. Confirm the `HaloPsaSettings` table has exactly one row (`Id = 1`) with default values.

- [ ] **Step 8: Commit**

```bash
git add src/DotMarc/Notifications/HaloPsaSettings.cs src/DotMarc/Data/Group.cs src/DotMarc/Data/Domain.cs src/DotMarc/Notifications/AlertEvent.cs src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Migrations/
git commit -m "Add HaloPSA data model: settings row, client mapping fields, ticket correlation fields"
```

---

## Task 2: Data Protection key persistence + Postgres-backed secret store

**Files:**
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Modify: `src/DotMarc/DotMarc.csproj`
- Create: `src/DotMarc/Notifications/IHaloSecretStore.cs`
- Create: `src/DotMarc/Notifications/DatabaseHaloSecretStore.cs`
- Modify: `src/DotMarc/Program.cs`
- Create (generated): `src/DotMarc/Migrations/<timestamp>_AddDataProtectionKeys.cs` and `.Designer.cs`
- Test: `test/DotMarc.Tests/Notifications/DatabaseHaloSecretStoreTests.cs`

**Interfaces:**
- Consumes: `HaloPsaSettings` (Task 1).
- Produces: `IHaloSecretStore` (`SetClientSecretAsync(string, CancellationToken)`, `GetClientSecretAsync(CancellationToken) -> string?`), consumed by Task 5 (`HaloPsaClient`) and the Alert settings UI (Task 9).

- [ ] **Step 1: Add the Data Protection EF Core package**

```powershell
dotnet add src/DotMarc/DotMarc.csproj package Microsoft.AspNetCore.DataProtection.EntityFrameworkCore
```

- [ ] **Step 2: Make `DotMarcDbContext` a Data Protection key store**

```csharp
// src/DotMarc/Data/DotMarcDbContext.cs
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
// ... existing usings

public sealed class DotMarcDbContext : DbContext, IDataProtectionKeyContext
{
    // ... existing constructor/DbSets unchanged

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
```

No `OnModelCreating` changes needed — `IDataProtectionKeyContext` brings its own conventional mapping for `DataProtectionKey`.

- [ ] **Step 3: Generate and apply the migration**

Run: `dotnet ef migrations add AddDataProtectionKeys --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

Confirm the generated migration creates a `DataProtectionKeys` table (columns `Id`, `FriendlyName`, `Xml`). Apply it:

```powershell
dotnet ef database update --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj
```

- [ ] **Step 4: Wire `PersistKeysToDbContext` into `Program.cs`**

Add near the top, right after `AddDbContextFactory<DotMarcDbContext>` is registered:

```csharp
// src/DotMarc/Program.cs
using Microsoft.AspNetCore.DataProtection;
// ... existing usings

// Previously unconfigured — Data Protection fell back to its default (non-durable across
// restarts/redeploys/replicas) key store, which DnsPushStateProtector tolerated only because its
// state is minutes-lived. The HaloPSA client secret (see DatabaseHaloSecretStore) needs real
// durability, the same argument that already moved NotificationSettings into Postgres.
builder.Services.AddDataProtection().PersistKeysToDbContext<DotMarcDbContext>();
```

- [ ] **Step 5: Write the `IHaloSecretStore` interface**

```csharp
// src/DotMarc/Notifications/IHaloSecretStore.cs
namespace DotMarc.Notifications;

/// <summary>Stores and retrieves the HaloPSA API client secret. Two implementations: this one
/// (Postgres + Data Protection, the default/fallback) and KeyVaultHaloSecretStore (Azure,
/// opt-in) — selected in Program.cs on whether KeyVault:VaultUri is configured. Never exposes the
/// value through HaloPsaSettings itself.</summary>
public interface IHaloSecretStore
{
    Task SetClientSecretAsync(string clientSecret, CancellationToken cancellationToken = default);
    Task<string?> GetClientSecretAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 6: Write the failing test for `DatabaseHaloSecretStore`**

```csharp
// test/DotMarc.Tests/Notifications/DatabaseHaloSecretStoreTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class DatabaseHaloSecretStoreTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DatabaseHaloSecretStoreTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static IDataProtectionProvider CreateProtectionProvider() =>
        DataProtectionProvider.Create("DotMarc.Tests.HaloPsa");

    [Fact]
    public async Task SetThenGet_RoundTripsTheSecret()
    {
        var store = new DatabaseHaloSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());

        await store.SetClientSecretAsync("super-secret-value");
        var result = await store.GetClientSecretAsync();

        Assert.Equal("super-secret-value", result);
    }

    [Fact]
    public async Task GetClientSecretAsync_ReturnsNull_WhenNeverSet()
    {
        var store = new DatabaseHaloSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());

        Assert.Null(await store.GetClientSecretAsync());
    }

    [Fact]
    public async Task GetClientSecretAsync_ReturnsNull_WhenProtectedWithADifferentKeyRing()
    {
        var store = new DatabaseHaloSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());
        await store.SetClientSecretAsync("super-secret-value");

        var storeWithADifferentKeyRing = new DatabaseHaloSecretStore(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.SomeOtherApp"));

        Assert.Null(await storeWithADifferentKeyRing.GetClientSecretAsync());
    }
}
```

- [ ] **Step 7: Run the test to verify it fails**

Run: `dotnet test dotMARC.sln --filter DatabaseHaloSecretStoreTests`
Expected: FAIL — `DatabaseHaloSecretStore` doesn't exist yet.

- [ ] **Step 8: Implement `DatabaseHaloSecretStore`**

```csharp
// src/DotMarc/Notifications/DatabaseHaloSecretStore.cs
using System.Security.Cryptography;
using DotMarc.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public sealed class DatabaseHaloSecretStore : IHaloSecretStore
{
    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly IDataProtector _protector;

    public DatabaseHaloSecretStore(IDbContextFactory<DotMarcDbContext> dbFactory, IDataProtectionProvider dataProtectionProvider)
    {
        _dbFactory = dbFactory;
        _protector = dataProtectionProvider.CreateProtector("DotMarc.Notifications.HaloPsaClientSecret.v1");
    }

    public async Task SetClientSecretAsync(string clientSecret, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await context.HaloPsaSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        settings.ProtectedClientSecret = _protector.Protect(clientSecret);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetClientSecretAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await context.HaloPsaSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(settings.ProtectedClientSecret))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(settings.ProtectedClientSecret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 9: Run the test to verify it passes**

Run: `dotnet test dotMARC.sln --filter DatabaseHaloSecretStoreTests`
Expected: PASS (all three tests).

- [ ] **Step 10: Commit**

```bash
git add src/DotMarc/DotMarc.csproj src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Program.cs src/DotMarc/Notifications/IHaloSecretStore.cs src/DotMarc/Notifications/DatabaseHaloSecretStore.cs src/DotMarc/Migrations/ test/DotMarc.Tests/Notifications/DatabaseHaloSecretStoreTests.cs
git commit -m "Persist Data Protection keys to Postgres; add the Postgres-backed Halo secret store"
```

---

## Task 3: Key Vault-backed secret store + infra opt-in

**Files:**
- Modify: `src/DotMarc/DotMarc.csproj`
- Create: `src/DotMarc/Notifications/KeyVaultHaloSecretStore.cs`
- Modify: `src/DotMarc/Program.cs`
- Modify: `infra/main.bicep`
- Modify: `infra/main.parameters.json`
- Test: `test/DotMarc.Tests/Notifications/KeyVaultHaloSecretStoreTests.cs` (unit test against a fake `SecretClient` transport — no real Azure resource; the actual live Key Vault path is verified manually, same acceptance as `AzureMtaStsHostProvisioner`)

**Interfaces:**
- Consumes: `IHaloSecretStore` (Task 2).
- Produces: `KeyVaultHaloSecretStore : IHaloSecretStore`, DI selection logic in `Program.cs`.

- [ ] **Step 1: Add the Key Vault Secrets package**

```powershell
dotnet add src/DotMarc/DotMarc.csproj package Azure.Security.KeyVault.Secrets
```

- [ ] **Step 2: Implement `KeyVaultHaloSecretStore`**

```csharp
// src/DotMarc/Notifications/KeyVaultHaloSecretStore.cs
using Azure;
using Azure.Security.KeyVault.Secrets;

namespace DotMarc.Notifications;

/// <summary>Stores the HaloPSA client secret in the Key Vault infra/main.bicep already
/// provisions, under a fixed secret name. Selected instead of DatabaseHaloSecretStore when
/// KeyVault:VaultUri is configured (see Program.cs) — requires the container's managed identity
/// to hold the write role infra/main.bicep grants only when enableHaloPsaKeyVaultWrite is true.
/// The value never touches Postgres.</summary>
public sealed class KeyVaultHaloSecretStore : IHaloSecretStore
{
    private const string SecretName = "HaloPsa-ClientSecret";
    private readonly SecretClient _secretClient;

    public KeyVaultHaloSecretStore(SecretClient secretClient) => _secretClient = secretClient;

    public async Task SetClientSecretAsync(string clientSecret, CancellationToken cancellationToken = default) =>
        await _secretClient.SetSecretAsync(SecretName, clientSecret, cancellationToken).ConfigureAwait(false);

    public async Task<string?> GetClientSecretAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = await _secretClient.GetSecretAsync(SecretName, cancellationToken: cancellationToken).ConfigureAwait(false);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
```

- [ ] **Step 3: Write the test against a fake transport**

`Azure.Security.KeyVault.Secrets`'s `SecretClient` supports a custom `HttpPipelineTransport` for testing without a real vault. Use `SecretClientOptions.Transport`:

```csharp
// test/DotMarc.Tests/Notifications/KeyVaultHaloSecretStoreTests.cs
using System.Net;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Secrets;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class KeyVaultHaloSecretStoreTests
{
    private static (KeyVaultHaloSecretStore store, FakeHttpMessageHandler handler) CreateStore()
    {
        var handler = new FakeHttpMessageHandler();
        var options = new SecretClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        var client = new SecretClient(new Uri("https://fake-vault.vault.azure.net/"), new FakeTokenCredential(), options);
        return (new KeyVaultHaloSecretStore(client), handler);
    }

    [Fact]
    public async Task SetClientSecretAsync_PutsToTheSecretsEndpoint()
    {
        var (store, handler) = CreateStore();
        handler.ResponseBody = """{"value":"x","id":"https://fake-vault.vault.azure.net/secrets/HaloPsa-ClientSecret/v1"}""";

        await store.SetClientSecretAsync("super-secret-value");

        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("/secrets/HaloPsa-ClientSecret"));
    }

    [Fact]
    public async Task GetClientSecretAsync_ReturnsNull_WhenTheSecretDoesNotExist()
    {
        var (store, handler) = CreateStore();
        handler.StatusCode = HttpStatusCode.NotFound;
        handler.ResponseBody = """{"error":{"code":"SecretNotFound","message":"not found"}}""";

        Assert.Null(await store.GetClientSecretAsync());
    }
}
```

This needs a minimal `FakeTokenCredential` (Key Vault's client requires a `TokenCredential` even against a fake transport):

```csharp
// test/DotMarc.Tests/Internal/FakeTokenCredential.cs
using Azure.Core;

namespace DotMarc.Tests.Internal;

internal sealed class FakeTokenCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}
```

- [ ] **Step 4: Run the tests, fix `FakeHttpMessageHandler` content-type if needed, verify pass**

Run: `dotnet test dotMARC.sln --filter KeyVaultHaloSecretStoreTests`
Expected: PASS. If the Azure SDK's pipeline rejects `FakeHttpMessageHandler`'s fixed `application/json` content type or adds required headers the fake doesn't echo back, adjust the fake's response headers rather than the production code — this is a test-only concern.

- [ ] **Step 5: Wire DI selection in `Program.cs`**

```csharp
// src/DotMarc/Program.cs — near the other typed-client/store registrations
var keyVaultUri = builder.Configuration["KeyVault:VaultUri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Services.AddSingleton(new SecretClient(new Uri(keyVaultUri), new DefaultAzureCredential()));
    builder.Services.AddSingleton<IHaloSecretStore, KeyVaultHaloSecretStore>();
}
else
{
    builder.Services.AddSingleton<IHaloSecretStore, DatabaseHaloSecretStore>();
}
```

Add `using Azure.Identity;` and `using Azure.Security.KeyVault.Secrets;` to `Program.cs`'s usings.

- [ ] **Step 6: Add the opt-in Key Vault write role to `infra/main.bicep`**

Add a new param, right after `azureDnsClientId`:

```bicep
@description('Grant the container app write access to its own Key Vault, used to store the HaloPSA API client secret entered through Alert settings at runtime rather than in Postgres. Off by default, since it widens the managed identity beyond Key Vault Secrets User (read-only) — see deploy-to-azure.mdx.')
param enableHaloPsaKeyVaultWrite bool = false
```

Add a new env var to the container's `env` array (harmless-to-set pattern, same as the others):

```bicep
{ name: 'KeyVault__VaultUri', value: enableHaloPsaKeyVaultWrite ? keyVault.properties.vaultUri : '' }
```

Add the new custom role and its assignment, near the existing MTA-STS custom roles (after `mtaStsManagedEnvironmentRoleAssignment`):

```bicep
// The container app can already read every secret in this vault (Key Vault Secrets User,
// assigned above). Writing the HaloPSA client secret at runtime needs one narrow addition on top
// of that — not a broader get+set role — matching the MTA-STS custom roles' precedent of the
// smallest permission delta Azure's RBAC surface allows, gated off by default since it's a real
// widening of what this identity can do.
resource haloPsaKeyVaultWriteRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = if (enableHaloPsaKeyVaultWrite) {
  name: guid(keyVault.id, 'dotMARC HaloPSA Key Vault Write Role')
  properties: {
    roleName: 'dotMARC HaloPSA Key Vault Write Role (${baseName})'
    description: 'Lets dotMARC write its own HaloPSA API client secret into this Key Vault at runtime.'
    type: 'CustomRole'
    permissions: [
      {
        actions: []
        notActions: []
        dataActions: [
          'Microsoft.KeyVault/vaults/secrets/setSecret/action'
        ]
        notDataActions: []
      }
    ]
    assignableScopes: [
      resourceGroup().id
    ]
  }
}

resource haloPsaKeyVaultWriteRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableHaloPsaKeyVaultWrite) {
  name: guid(keyVault.id, containerApp.id, 'dotMARC HaloPSA Key Vault Write Role Assignment')
  scope: keyVault
  properties: {
    roleDefinitionId: haloPsaKeyVaultWriteRole.id
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}
```

- [ ] **Step 7: Validate the template compiles**

Run: `az bicep build --file infra/main.bicep --stdout`
Expected: no errors. Also confirm `enableHaloPsaKeyVaultWrite` and `KeyVault__VaultUri` show no "declared but never used" warnings.

- [ ] **Step 8: Add the param to `infra/main.parameters.json` for discoverability**

```json
"enableHaloPsaKeyVaultWrite": { "value": false }
```

(Add alongside the other parameters, before the closing brace.)

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc/DotMarc.csproj src/DotMarc/Notifications/KeyVaultHaloSecretStore.cs src/DotMarc/Program.cs infra/main.bicep infra/main.parameters.json test/DotMarc.Tests/Notifications/KeyVaultHaloSecretStoreTests.cs test/DotMarc.Tests/Internal/FakeTokenCredential.cs
git commit -m "Add Key Vault-backed Halo secret store, opt-in via enableHaloPsaKeyVaultWrite"
```

---

## Task 4: `HaloPsaSettings` CRUD service

**Files:**
- Create: `src/DotMarc/Notifications/HaloPsaSettingsService.cs`
- Test: `test/DotMarc.Tests/Notifications/HaloPsaSettingsServiceTests.cs`

**Interfaces:**
- Consumes: `HaloPsaSettings` (Task 1), `IHaloSecretStore` (Task 2/3).
- Produces: `HaloPsaSettingsService.GetAsync(context) -> HaloPsaSettings`, `HaloPsaSettingsService.SaveAsync(context, secretStore, updated, newClientSecret) -> Task`, consumed by Task 9 (UI) and indirectly by Tasks 5/7 (which read `HaloPsaSettings` via `GetAsync`).

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/Notifications/HaloPsaSettingsServiceTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class HaloPsaSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public HaloPsaSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DatabaseHaloSecretStore CreateSecretStore() =>
        new(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.HaloPsaSettingsService"));

    [Fact]
    public async Task SaveAsync_UpdatesNonSecretFields_AndLeavesSecretUnconfigured_WhenNoneProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await HaloPsaSettingsService.SaveAsync(context, secretStore, new HaloPsaSettings
        {
            Enabled = true,
            AccountName = "contoso",
            AuthServerUrl = "https://contoso.halopsa.com/auth",
            ResourceServerUrl = "https://contoso.halopsa.com/api",
            ClientId = "client-id",
            TicketTypeId = 5,
            DefaultPriorityId = 2,
            ClosedStatusId = 9,
            WebhookSecret = "webhook-secret"
        }, newClientSecret: null);

        var saved = await HaloPsaSettingsService.GetAsync(context);
        Assert.True(saved.Enabled);
        Assert.Equal("contoso", saved.AccountName);
        Assert.False(saved.ClientSecretConfigured);
        Assert.Null(await secretStore.GetClientSecretAsync());
    }

    [Fact]
    public async Task SaveAsync_StoresTheSecretAndMarksItConfigured_WhenProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await HaloPsaSettingsService.SaveAsync(context, secretStore, new HaloPsaSettings { Enabled = true }, newClientSecret: "the-real-secret");

        var saved = await HaloPsaSettingsService.GetAsync(context);
        Assert.True(saved.ClientSecretConfigured);
        Assert.Equal("the-real-secret", await secretStore.GetClientSecretAsync());
    }

    [Fact]
    public async Task SaveAsync_LeavesAnExistingSecretInPlace_WhenNotReplaced()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();
        await HaloPsaSettingsService.SaveAsync(context, secretStore, new HaloPsaSettings { Enabled = true }, newClientSecret: "first-secret");

        await using var secondContext = CreateContext();
        await HaloPsaSettingsService.SaveAsync(secondContext, secretStore, new HaloPsaSettings { Enabled = false, AccountName = "changed" }, newClientSecret: null);

        Assert.Equal("first-secret", await secretStore.GetClientSecretAsync());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test dotMARC.sln --filter HaloPsaSettingsServiceTests`
Expected: FAIL — `HaloPsaSettingsService` doesn't exist yet.

- [ ] **Step 3: Implement `HaloPsaSettingsService`**

```csharp
// src/DotMarc/Notifications/HaloPsaSettingsService.cs
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

/// <summary>Read/update the singleton HaloPsaSettings row. Follows NotificationSettingsService's
/// convention exactly, plus the client secret's own write path via IHaloSecretStore — the secret
/// never travels through the HaloPsaSettings object this returns to a caller.</summary>
public static class HaloPsaSettingsService
{
    public static Task<HaloPsaSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.HaloPsaSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, IHaloSecretStore secretStore, HaloPsaSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        var existing = await context.HaloPsaSettings.SingleAsync(cancellationToken).ConfigureAwait(false);

        existing.Enabled = updated.Enabled;
        existing.AccountName = updated.AccountName;
        existing.AuthServerUrl = updated.AuthServerUrl;
        existing.ResourceServerUrl = updated.ResourceServerUrl;
        existing.ClientId = updated.ClientId;
        existing.TicketTypeId = updated.TicketTypeId;
        existing.DefaultPriorityId = updated.DefaultPriorityId;
        existing.ClosedStatusId = updated.ClosedStatusId;
        existing.WebhookSecret = updated.WebhookSecret;

        if (!string.IsNullOrWhiteSpace(newClientSecret))
        {
            await secretStore.SetClientSecretAsync(newClientSecret, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test dotMARC.sln --filter HaloPsaSettingsServiceTests`
Expected: PASS (all three tests).

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Notifications/HaloPsaSettingsService.cs test/DotMarc.Tests/Notifications/HaloPsaSettingsServiceTests.cs
git commit -m "Add HaloPsaSettingsService"
```

---

## Task 5: Halo API client (OAuth2 token acquisition, list/create/close)

**Files:**
- Create: `src/DotMarc/Notifications/HaloPsaModels.cs`
- Create: `src/DotMarc/Notifications/IHaloPsaClient.cs`
- Create: `src/DotMarc/Notifications/HaloPsaTokenCache.cs`
- Create: `src/DotMarc/Notifications/HaloPsaClient.cs`
- Modify: `src/DotMarc/Program.cs`
- Test: `test/DotMarc.Tests/Notifications/HaloPsaClientTests.cs`

**Interfaces:**
- Consumes: `HaloPsaSettings` (Task 1), `IHaloSecretStore` (Task 2/3).
- Produces: `HaloClient(int Id, string Name)`, `HaloTicketType(int Id, string Name)`, `HaloTicketStatus(int Id, string Name)`, `IHaloPsaClient` with `ListClientsAsync`, `ListTicketTypesAsync`, `ListStatusesAsync`, `CreateTicketAsync(...) -> string` (the created ticket's ID), `CloseTicketAsync(...)`. Consumed by Task 7 (`PsaTicketService`) and Task 9 (UI dropdowns).

> **Note on the exact wire format:** Halo's OAuth2 `client_credentials` token endpoint and REST
> field names for ticket create/list operations are not fully confirmed from public
> documentation (see the spec's "what's confirmed vs. needs live verification" section). The
> implementation below is a concrete, best-effort mapping based on the confirmed pieces
> (`{AuthServerUrl}/token`, `CreateTicketRequest`-style fields `summary`/`details`/`client_id`/
> `tickettype_id`). **Step 8 below is a manual verification step against a real or trial Halo
> tenant** — if field names don't match, fix `HaloPsaClient`'s request/response DTOs only; the
> public interface and every other task built on top of it do not change.

- [ ] **Step 1: Define the read-model records**

```csharp
// src/DotMarc/Notifications/HaloPsaModels.cs
namespace DotMarc.Notifications;

public sealed record HaloClient(int Id, string Name);
public sealed record HaloTicketType(int Id, string Name);
public sealed record HaloTicketStatus(int Id, string Name);
```

- [ ] **Step 2: Define `IHaloPsaClient`**

```csharp
// src/DotMarc/Notifications/IHaloPsaClient.cs
namespace DotMarc.Notifications;

public interface IHaloPsaClient
{
    Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default);
    Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default);
    Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Write the failing tests**

```csharp
// test/DotMarc.Tests/Notifications/HaloPsaClientTests.cs
using System.Net;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class HaloPsaClientTests
{
    private sealed class FixedHaloSecretStore(string secret) : IHaloSecretStore
    {
        public Task SetClientSecretAsync(string clientSecret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetClientSecretAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(secret);
    }

    private static HaloPsaSettings Settings => new()
    {
        AccountName = "contoso",
        AuthServerUrl = "https://contoso.halopsa.com/auth",
        ResourceServerUrl = "https://contoso.halopsa.com/api",
        ClientId = "client-id",
        TicketTypeId = 5,
        DefaultPriorityId = 2
    };

    private static (HaloPsaClient client, FakeHttpMessageHandler handler) CreateClient()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler);
        var client = new HaloPsaClient(http, new FixedHaloSecretStore("the-secret"), new HaloPsaTokenCache());
        return (client, handler);
    }

    [Fact]
    public async Task CreateTicketAsync_AcquiresATokenThenPostsTheTicket_AndReturnsTheNewTicketId()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("""{"id":4242}""");

        var ticketId = await client.CreateTicketAsync(Settings, haloClientId: 7, "contoso.io", "MissedReport", "Missing report", "contoso.io has not sent a report.");

        Assert.Equal("4242", ticketId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://contoso.halopsa.com/auth/token", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("Tickets", handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("Bearer the-token", handler.Requests[1].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task CreateTicketAsync_ReusesTheCachedToken_WithinItsLifetime()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBodies.Enqueue("""{"id":1}""");
        handler.ResponseBodies.Enqueue("""{"id":2}""");

        await client.CreateTicketAsync(Settings, 7, "a.example", "MissedReport", "t", "m");
        await client.CreateTicketAsync(Settings, 7, "b.example", "MissedReport", "t", "m");

        // One token request, two ticket-creation requests — the second call reused the cached token.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.ToString().EndsWith("/token")));
    }

    [Fact]
    public async Task CloseTicketAsync_PostsToTheTicketWithTheClosedStatus()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBody = "{}";

        await client.CloseTicketAsync(Settings, "4242", "Resolved automatically by dotMARC.");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("Tickets", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task ListClientsAsync_ReturnsTheParsedClientList()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBodies.Enqueue("""{"access_token":"the-token","expires_in":3600}""");
        handler.ResponseBody = """{"clients":[{"id":1,"name":"Client A"},{"id":2,"name":"Client B"}]}""";

        var clients = await client.ListClientsAsync(Settings);

        Assert.Equal(2, clients.Count);
        Assert.Contains(clients, c => c is { Id: 1, Name: "Client A" });
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test dotMARC.sln --filter HaloPsaClientTests`
Expected: FAIL — `HaloPsaClient`/`HaloPsaTokenCache` don't exist yet.

- [ ] **Step 5: Implement `HaloPsaTokenCache`**

```csharp
// src/DotMarc/Notifications/HaloPsaTokenCache.cs
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Notifications;

/// <summary>Caches the OAuth2 client_credentials token in memory for the lifetime of this
/// singleton instance — safe even across multiple Container Apps replicas, since each replica
/// just acquires its own token independently; no shared/distributed cache is needed at this call
/// volume (alert-triggered, not a per-request hot path).</summary>
public sealed class HaloPsaTokenCache
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAtUtc;

    public async Task<string> GetTokenAsync(HttpClient httpClient, HaloPsaSettings settings, string clientSecret, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAtUtc)
            {
                return _token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.AuthServerUrl!.TrimEnd('/')}/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = settings.ClientId!,
                    ["client_secret"] = clientSecret,
                    ["scope"] = "edit:tickets read:tickets read:customers read:teams"
                })
            };

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);

            _token = payload!.AccessToken;
            // Refresh a minute early so a call starting right before expiry doesn't race a 401.
            _expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresInSeconds - 60);
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresInSeconds);
}
```

- [ ] **Step 6: Implement `HaloPsaClient`**

```csharp
// src/DotMarc/Notifications/HaloPsaClient.cs
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Notifications;

public sealed class HaloPsaClient : IHaloPsaClient
{
    private readonly HttpClient _httpClient;
    private readonly IHaloSecretStore _secretStore;
    private readonly HaloPsaTokenCache _tokenCache;

    public HaloPsaClient(HttpClient httpClient, IHaloSecretStore secretStore, HaloPsaTokenCache tokenCache)
    {
        _httpClient = httpClient;
        _secretStore = secretStore;
        _tokenCache = tokenCache;
    }

    public async Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, settings, "Client", null, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<ClientListResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload?.Clients.Select(c => new HaloClient(c.Id, c.Name)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, settings, "TicketType", null, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<List<IdNameEntry>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload?.Select(e => new HaloTicketType(e.Id, e.Name)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, settings, "Status", null, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<List<IdNameEntry>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload?.Select(e => new HaloTicketStatus(e.Id, e.Name)).ToList() ?? [];
    }

    public async Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
    {
        var body = new CreateTicketRequest(title, $"{message}\n\nDomain: {domainName}\nAlert type: {alertType}\nRaised automatically by dotMARC.", haloClientId, settings.TicketTypeId, settings.DefaultPriorityId);
        using var response = await SendAsync(HttpMethod.Post, settings, "Tickets", body, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<CreateTicketResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload!.Id.ToString();
    }

    public async Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        var body = new CloseTicketRequest(int.Parse(ticketId), settings.ClosedStatusId, note);
        using var response = await SendAsync(HttpMethod.Post, settings, $"Tickets/{ticketId}", body, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, HaloPsaSettings settings, string relativePath, object? body, CancellationToken cancellationToken)
    {
        var clientSecret = await _secretStore.GetClientSecretAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("HaloPSA client secret is not configured.");
        var token = await _tokenCache.GetTokenAsync(_httpClient, settings, clientSecret, cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(method, $"{settings.ResourceServerUrl!.TrimEnd('/')}/{relativePath}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private sealed record IdNameEntry([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("name")] string Name);
    private sealed record ClientListResponse([property: JsonPropertyName("clients")] List<IdNameEntry> Clients);
    private sealed record CreateTicketRequest(
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("details")] string Details,
        [property: JsonPropertyName("client_id")] int ClientId,
        [property: JsonPropertyName("tickettype_id")] int? TicketTypeId,
        [property: JsonPropertyName("priority_id")] int? PriorityId);
    private sealed record CreateTicketResponse([property: JsonPropertyName("id")] int Id);
    private sealed record CloseTicketRequest(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("status_id")] int? StatusId,
        [property: JsonPropertyName("note")] string Note);
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test dotMARC.sln --filter HaloPsaClientTests`
Expected: PASS (all four tests).

- [ ] **Step 8: Manual live verification (not automatable in CI)**

Against a real or trial HaloPSA tenant: create an API application (Configuration → Integrations → HaloPSA API) with `edit:tickets read:tickets read:customers read:teams`, plug its account name/auth URL/resource URL/client ID/secret into a locally-running dotMARC's Alert settings (once Task 9 exists), and confirm `ListClientsAsync`/`CreateTicketAsync`/`CloseTicketAsync` succeed against the real API. Fix field names in Step 6's request/response DTOs if Halo's actual responses differ — nothing else in this plan depends on the wire format, only on `IHaloPsaClient`'s public shape.

- [ ] **Step 9: Register `HaloPsaClient` in `Program.cs`**

```csharp
// src/DotMarc/Program.cs
builder.Services.AddSingleton<HaloPsaTokenCache>();
builder.Services.AddHttpClient<IHaloPsaClient, HaloPsaClient>();
```

- [ ] **Step 10: Commit**

```bash
git add src/DotMarc/Notifications/HaloPsaModels.cs src/DotMarc/Notifications/IHaloPsaClient.cs src/DotMarc/Notifications/HaloPsaTokenCache.cs src/DotMarc/Notifications/HaloPsaClient.cs src/DotMarc/Program.cs test/DotMarc.Tests/Notifications/HaloPsaClientTests.cs
git commit -m "Add HaloPsaClient: OAuth2 client_credentials token acquisition and ticket/list operations"
```

---

## Task 6: Client mapping — Group/Domain `HaloClientId` + resolution rule

**Files:**
- Modify: `src/DotMarc/Data/GroupManagementService.cs`
- Modify: `src/DotMarc/Data/DomainManagementService.cs`
- Create: `src/DotMarc/Notifications/HaloClientResolver.cs`
- Test: `test/DotMarc.Tests/Notifications/HaloClientResolverTests.cs`
- Test: `test/DotMarc.Tests/Data/GroupManagementServiceTests.cs` (add to existing file if present; check first)

**Interfaces:**
- Consumes: `Group.HaloClientId`/`Domain.HaloClientId` (Task 1).
- Produces: `HaloClientResolver.Resolve(Domain domain) -> int?`, consumed by Task 7 (`PsaTicketService`). `GroupManagementService.SetHaloClientIdAsync`, `DomainManagementService.SetHaloClientIdAsync`, consumed by Task 10 (UI).

- [ ] **Step 1: Write the failing test for the resolution rule**

```csharp
// test/DotMarc.Tests/Notifications/HaloClientResolverTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class HaloClientResolverTests
{
    [Fact]
    public void Resolve_ReturnsTheDomainOverride_WhenSet()
    {
        var domain = new Domain { Name = "contoso.io", HaloClientId = 99, Groups = [new Group { Id = 1, Name = "g", HaloClientId = 1 }] };

        Assert.Equal(99, HaloClientResolver.Resolve(domain));
    }

    [Fact]
    public void Resolve_ReturnsTheLowestIdGroupWithAMapping_WhenNoOverride()
    {
        var domain = new Domain
        {
            Name = "contoso.io",
            Groups =
            [
                new Group { Id = 5, Name = "later", HaloClientId = 50 },
                new Group { Id = 2, Name = "earlier", HaloClientId = 20 },
                new Group { Id = 3, Name = "unmapped", HaloClientId = null }
            ]
        };

        Assert.Equal(20, HaloClientResolver.Resolve(domain));
    }

    [Fact]
    public void Resolve_SkipsGroupsWithNoMapping_EvenIfTheyHaveTheLowestId()
    {
        var domain = new Domain
        {
            Name = "contoso.io",
            Groups =
            [
                new Group { Id = 1, Name = "unmapped", HaloClientId = null },
                new Group { Id = 2, Name = "mapped", HaloClientId = 42 }
            ]
        };

        Assert.Equal(42, HaloClientResolver.Resolve(domain));
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNothingIsMapped()
    {
        var domain = new Domain { Name = "contoso.io", Groups = [new Group { Id = 1, Name = "g" }] };

        Assert.Null(HaloClientResolver.Resolve(domain));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test dotMARC.sln --filter HaloClientResolverTests`
Expected: FAIL — `HaloClientResolver` doesn't exist yet.

- [ ] **Step 3: Implement `HaloClientResolver`**

```csharp
// src/DotMarc/Notifications/HaloClientResolver.cs
using DotMarc.Data;

namespace DotMarc.Notifications;

/// <summary>Resolves which Halo client a domain's ticket should be created against. Domain and
/// Group is an implicit EF many-to-many with no order column, so "the domain's Groups" has no
/// natural order — lowest Group.Id (oldest-created) is the deterministic tie-break.</summary>
public static class HaloClientResolver
{
    public static int? Resolve(Domain domain)
    {
        if (domain.HaloClientId is { } domainOverride)
        {
            return domainOverride;
        }

        return domain.Groups
            .Where(g => g.HaloClientId is not null)
            .OrderBy(g => g.Id)
            .Select(g => g.HaloClientId)
            .FirstOrDefault();
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test dotMARC.sln --filter HaloClientResolverTests`
Expected: PASS (all four tests).

- [ ] **Step 5: Add `SetHaloClientIdAsync` to `GroupManagementService`**

```csharp
// src/DotMarc/Data/GroupManagementService.cs — add as a new method
/// <summary>Sets (or clears, with null) a Group's Halo client mapping, from the "Halo Client"
/// column on Manage Groups.</summary>
public static async Task SetHaloClientIdAsync(DotMarcDbContext context, int groupId, int? haloClientId, CancellationToken cancellationToken = default)
{
    var group = await context.Groups.SingleAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
    group.HaloClientId = haloClientId;
    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
}
```

- [ ] **Step 6: Add `SetHaloClientIdAsync` to `DomainManagementService`**

```csharp
// src/DotMarc/Data/DomainManagementService.cs — add as a new method, mirroring SetMonitoredAsync
/// <summary>Sets (or clears, with null) a domain's Halo client override, from Manage Domains.</summary>
public static async Task SetHaloClientIdAsync(DotMarcDbContext context, int domainId, int? haloClientId, CancellationToken cancellationToken = default)
{
    var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
    domain.HaloClientId = haloClientId;
    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
}
```

- [ ] **Step 7: Write and run a round-trip test for both setters**

First run `Glob test/DotMarc.Tests/Data/*Tests.cs` to check whether `GroupManagementServiceTests.cs`/`DomainManagementServiceTests.cs` already exist. If either does, add the matching test method below into it (same `[Collection("Postgres")]`/`PostgresContainerFixture` class shape as `AlertingServiceTests` in Task 4). If neither exists, create both files with this shape:

```csharp
// test/DotMarc.Tests/Data/GroupManagementServiceTests.cs (add this test method; create the file with the class shell below if it doesn't exist yet)
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
    public async Task SetHaloClientIdAsync_UpdatesTheGroupsMapping()
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Client A" };
        context.Groups.Add(group);
        await context.SaveChangesAsync();

        await GroupManagementService.SetHaloClientIdAsync(context, group.Id, 42);

        await using var verify = CreateContext();
        Assert.Equal(42, (await verify.Groups.SingleAsync(g => g.Id == group.Id)).HaloClientId);
    }

    [Fact]
    public async Task SetHaloClientIdAsync_ClearsTheMapping_WhenPassedNull()
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Client A", HaloClientId = 42 };
        context.Groups.Add(group);
        await context.SaveChangesAsync();

        await GroupManagementService.SetHaloClientIdAsync(context, group.Id, null);

        await using var verify = CreateContext();
        Assert.Null((await verify.Groups.SingleAsync(g => g.Id == group.Id)).HaloClientId);
    }
}
```

```csharp
// test/DotMarc.Tests/Data/DomainManagementServiceTests.cs (add this test method; create the file with the same class shell as above, renamed to DomainManagementServiceTests, if it doesn't exist yet)
[Fact]
public async Task SetHaloClientIdAsync_UpdatesTheDomainsOverride()
{
    await using var context = CreateContext();
    var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
    context.Domains.Add(domain);
    await context.SaveChangesAsync();

    await DomainManagementService.SetHaloClientIdAsync(context, domain.Id, 7);

    await using var verify = CreateContext();
    Assert.Equal(7, (await verify.Domains.SingleAsync(d => d.Id == domain.Id)).HaloClientId);
}
```

Run: `dotnet test dotMARC.sln --filter "SetHaloClientIdAsync"`
Expected: PASS (all three tests).

- [ ] **Step 8: Commit**

```bash
git add src/DotMarc/Notifications/HaloClientResolver.cs src/DotMarc/Data/GroupManagementService.cs src/DotMarc/Data/DomainManagementService.cs test/DotMarc.Tests/Notifications/HaloClientResolverTests.cs test/DotMarc.Tests/Data/
git commit -m "Add Halo client mapping resolution rule and Group/Domain setters"
```

---

## Task 7: PSA ticket service + `AlertingService` wiring

**Files:**
- Create: `src/DotMarc/Notifications/IPsaTicketService.cs`
- Create: `src/DotMarc/Notifications/PsaTicketService.cs`
- Modify: `src/DotMarc/Notifications/AlertingService.cs`
- Modify: `src/DotMarc/Program.cs`
- Test: `test/DotMarc.Tests/Notifications/PsaTicketServiceTests.cs`
- Modify: `test/DotMarc.Tests/Notifications/AlertingServiceTests.cs`

**Interfaces:**
- Consumes: `HaloPsaSettingsService` (Task 4), `IHaloPsaClient` (Task 5), `HaloClientResolver` (Task 6).
- Produces: `IPsaTicketService` with `CreateTicketAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken)` and `CloseTicketAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken)`. Consumed by `AlertingService`.

> **Signature note vs. the spec:** the spec sketches `CreateTicketAsync(context, alert, domain, ...)`, but `AlertingService.EnsureAlertAsync` only ever has a `domainName` string in scope, not a loaded `Domain` entity (see `AlertingService.cs`'s actual signature). `PsaTicketService` therefore loads the `Domain` (with `Groups` included) itself, by `alert.DomainName`. Everything else about the design is unchanged.

- [ ] **Step 1: Define `IPsaTicketService`**

```csharp
// src/DotMarc/Notifications/IPsaTicketService.cs
using DotMarc.Data;

namespace DotMarc.Notifications;

public interface IPsaTicketService
{
    Task CreateTicketAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default);
    Task CloseTicketAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
// test/DotMarc.Tests/Notifications/PsaTicketServiceTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class PsaTicketServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PsaTicketServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private sealed class FakeHaloPsaClient : IHaloPsaClient
    {
        public int CreateCallCount { get; private set; }
        public int CloseCallCount { get; private set; }
        public string NextTicketId { get; set; } = "1000";

        public Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloClient>>([]);
        public Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketType>>([]);
        public Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketStatus>>([]);

        public Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            return Task.FromResult(NextTicketId);
        }

        public Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
        {
            CloseCallCount++;
            return Task.CompletedTask;
        }
    }

    private async Task EnableHaloAsync()
    {
        await using var context = CreateContext();
        var settings = await context.HaloPsaSettings.SingleAsync();
        settings.Enabled = true;
        settings.AccountName = "contoso";
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task CreateTicketAsync_CreatesATicket_ForADomainInAMappedGroup()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var group = new Group { Name = "Client A", HaloClientId = 7 };
        context.Groups.Add(group);
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [group] };
        context.Domains.Add(domain);
        var alert = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();

        var fakeClient = new FakeHaloPsaClient();
        var service = new PsaTicketService(fakeClient);
        await service.CreateTicketAsync(context, alert);
        await context.SaveChangesAsync();

        Assert.Equal(1, fakeClient.CreateCallCount);
        var saved = await context.AlertEvents.SingleAsync();
        Assert.Equal("HaloPSA", saved.ExternalTicketProvider);
        Assert.Equal("1000", saved.ExternalTicketId);
    }

    [Fact]
    public async Task CreateTicketAsync_DoesNothing_ForADomainWithNoMapping()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var domain = new Domain { Name = "unmapped.io", FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        var alert = new AlertEvent { DomainName = "unmapped.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();

        var fakeClient = new FakeHaloPsaClient();
        var service = new PsaTicketService(fakeClient);
        await service.CreateTicketAsync(context, alert);

        Assert.Equal(0, fakeClient.CreateCallCount);
        Assert.Null((await context.AlertEvents.SingleAsync()).ExternalTicketId);
    }

    [Fact]
    public async Task CloseTicketAsync_ClosesTheTicket_WhenOneWasCreated()
    {
        await EnableHaloAsync();
        await using var context = CreateContext();
        var alert = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m", ExternalTicketProvider = "HaloPSA", ExternalTicketId = "4242" };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();

        var fakeClient = new FakeHaloPsaClient();
        var service = new PsaTicketService(fakeClient);
        await service.CloseTicketAsync(context, alert);

        Assert.Equal(1, fakeClient.CloseCallCount);
    }

    [Fact]
    public async Task CloseTicketAsync_DoesNothing_WhenNoTicketWasCreated()
    {
        await using var context = CreateContext();
        var alert = new AlertEvent { DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m" };
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();

        var fakeClient = new FakeHaloPsaClient();
        var service = new PsaTicketService(fakeClient);
        await service.CloseTicketAsync(context, alert);

        Assert.Equal(0, fakeClient.CloseCallCount);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test dotMARC.sln --filter PsaTicketServiceTests`
Expected: FAIL — `PsaTicketService` doesn't exist yet.

- [ ] **Step 4: Implement `PsaTicketService`**

```csharp
// src/DotMarc/Notifications/PsaTicketService.cs
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public sealed class PsaTicketService : IPsaTicketService
{
    private const string ProviderName = "HaloPSA";
    private readonly IHaloPsaClient _haloPsaClient;

    public PsaTicketService(IHaloPsaClient haloPsaClient) => _haloPsaClient = haloPsaClient;

    public async Task CreateTicketAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default)
    {
        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var domain = await context.Domains
            .Include(d => d.Groups)
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Name == alert.DomainName, cancellationToken)
            .ConfigureAwait(false);
        if (domain is null)
        {
            return;
        }

        var haloClientId = HaloClientResolver.Resolve(domain);
        if (haloClientId is null)
        {
            return;
        }

        var ticketId = await _haloPsaClient.CreateTicketAsync(settings, haloClientId.Value, alert.DomainName, alert.AlertType, alert.Title, alert.Message, cancellationToken).ConfigureAwait(false);
        alert.ExternalTicketProvider = ProviderName;
        alert.ExternalTicketId = ticketId;
    }

    public async Task CloseTicketAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken = default)
    {
        if (alert.ExternalTicketProvider != ProviderName || alert.ExternalTicketId is null)
        {
            return;
        }

        var settings = await HaloPsaSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        await _haloPsaClient.CloseTicketAsync(settings, alert.ExternalTicketId, "Resolved automatically by dotMARC.", cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test dotMARC.sln --filter PsaTicketServiceTests`
Expected: PASS (all four tests).

- [ ] **Step 6: Wire `IPsaTicketService` into `AlertingService`**

`AlertingService` currently takes `(IDbContextFactory<DotMarcDbContext> dbFactory, IAlertWebhookClient alertWebhookClient, ILogger<AlertingService> logger)`. Add a fourth constructor parameter and two call sites:

```csharp
// src/DotMarc/Notifications/AlertingService.cs
public sealed class AlertingService : IAlertingService
{
    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly IAlertWebhookClient _alertWebhookClient;
    private readonly IPsaTicketService _psaTicketService;
    private readonly ILogger<AlertingService> _logger;

    public AlertingService(IDbContextFactory<DotMarcDbContext> dbFactory, IAlertWebhookClient alertWebhookClient, IPsaTicketService psaTicketService, ILogger<AlertingService> logger)
    {
        _dbFactory = dbFactory;
        _alertWebhookClient = alertWebhookClient;
        _psaTicketService = psaTicketService;
        _logger = logger;
    }
```

In `ResolveAlertAsync`, right before `await db.SaveChangesAsync(cancellationToken)`:

```csharp
        activeAlert.IsResolved = true;
        activeAlert.ResolvedUtc = DateTimeOffset.UtcNow;

        try
        {
            await _psaTicketService.CloseTicketAsync(db, activeAlert, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close PSA ticket for {DomainName} alert {AlertType}.", activeAlert.DomainName, activeAlert.AlertType);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
```

In `EnsureAlertAsync`, right after the existing `_alertWebhookClient.SendAlertAsync` try/catch block (same method, right before the closing brace):

```csharp
        try
        {
            await _alertWebhookClient.SendAlertAsync(settings, domainName, alertType, title, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification for {DomainName} alert {AlertType}.", domainName, alertType);
        }

        try
        {
            await _psaTicketService.CreateTicketAsync(context, alert, cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create PSA ticket for {DomainName} alert {AlertType}.", domainName, alertType);
        }
```

- [ ] **Step 7: Update `AlertingServiceTests`'s existing `FakeAlertWebhookClient`-only constructions**

Every `new AlertingService(...)` call in `test/DotMarc.Tests/Notifications/AlertingServiceTests.cs` needs a fourth argument. Add a `FakeHaloPsaClient`-backed `PsaTicketService` (or a trivial `FakePsaTicketService : IPsaTicketService` that no-ops both methods) and pass it in each construction, e.g.:

```csharp
private static IPsaTicketService CreateNoOpPsaTicketService() => new PsaTicketService(new NoOpHaloPsaClient());

private sealed class NoOpHaloPsaClient : IHaloPsaClient
{
    public Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloClient>>([]);
    public Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketType>>([]);
    public Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketStatus>>([]);
    public Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default) => Task.FromResult("unused");
    public Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

(`PsaTicketService.CreateTicketAsync`/`CloseTicketAsync` both already no-op when `HaloPsaSettings.Enabled` is `false`, which is the seeded default — `NoOpHaloPsaClient` is never actually called in these tests, it just satisfies the constructor.) Update every `new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, NullLogger<AlertingService>.Instance)` call to `new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance)`.

- [ ] **Step 8: Register `IPsaTicketService` in `Program.cs`**

`AlertingService` is registered `AddSingleton`, and a singleton cannot depend on a scoped service — `IPsaTicketService` must be `AddSingleton` too, matching `IAlertWebhookClient`'s existing registration right above it:

```csharp
// src/DotMarc/Program.cs — add right before the existing AlertingService registration
builder.Services.AddSingleton<IPsaTicketService, PsaTicketService>();
builder.Services.AddSingleton<IAlertingService, AlertingService>();
```

- [ ] **Step 9: Run the full test suite**

Run: `dotnet test dotMARC.sln --filter "AlertingServiceTests|PsaTicketServiceTests"`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/DotMarc/Notifications/IPsaTicketService.cs src/DotMarc/Notifications/PsaTicketService.cs src/DotMarc/Notifications/AlertingService.cs src/DotMarc/Program.cs test/DotMarc.Tests/Notifications/PsaTicketServiceTests.cs test/DotMarc.Tests/Notifications/AlertingServiceTests.cs
git commit -m "Wire PSA ticket creation/close into AlertingService"
```

---

## Task 8: Inbound webhook endpoint

**Files:**
- Create: `src/DotMarc/Notifications/HaloWebhookTicketPayload.cs`
- Create: `src/DotMarc/Notifications/HaloWebhookStatusMatcher.cs`
- Modify: `src/DotMarc/Program.cs`
- Test: `test/DotMarc.Tests/Notifications/HaloWebhookStatusMatcherTests.cs`
- Test: `test/DotMarc.Tests/Notifications/HaloWebhookEndpointTests.cs`

**Interfaces:**
- Consumes: `HaloPsaSettings.ClosedStatusId`, `HaloPsaSettings.WebhookSecret` (Task 1/4), `AlertEvent.ExternalTicketProvider`/`ExternalTicketId` (Task 1).
- Produces: `POST /integrations/halopsa/webhook/{secret}`.

- [ ] **Step 1: Define the payload DTO**

```csharp
// src/DotMarc/Notifications/HaloWebhookTicketPayload.cs
using System.Text.Json.Serialization;

namespace DotMarc.Notifications;

public sealed record HaloWebhookTicketPayload(
    [property: JsonPropertyName("ticket_id")] int TicketId,
    [property: JsonPropertyName("status_id")] int StatusId);
```

- [ ] **Step 2: Write the failing test for the pure status-matching logic**

```csharp
// test/DotMarc.Tests/Notifications/HaloWebhookStatusMatcherTests.cs
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class HaloWebhookStatusMatcherTests
{
    [Fact]
    public void IsClosedStatus_ReturnsTrue_WhenTheStatusIdMatchesTheConfiguredClosedStatus()
    {
        var payload = new HaloWebhookTicketPayload(TicketId: 4242, StatusId: 9);
        var settings = new HaloPsaSettings { ClosedStatusId = 9 };

        Assert.True(HaloWebhookStatusMatcher.IsClosedStatus(payload, settings));
    }

    [Fact]
    public void IsClosedStatus_ReturnsFalse_ForADifferentStatus()
    {
        var payload = new HaloWebhookTicketPayload(TicketId: 4242, StatusId: 3);
        var settings = new HaloPsaSettings { ClosedStatusId = 9 };

        Assert.False(HaloWebhookStatusMatcher.IsClosedStatus(payload, settings));
    }

    [Fact]
    public void IsClosedStatus_ReturnsFalse_WhenNoClosedStatusIsConfigured()
    {
        var payload = new HaloWebhookTicketPayload(TicketId: 4242, StatusId: 9);
        var settings = new HaloPsaSettings { ClosedStatusId = null };

        Assert.False(HaloWebhookStatusMatcher.IsClosedStatus(payload, settings));
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test dotMARC.sln --filter HaloWebhookStatusMatcherTests`
Expected: FAIL — `HaloWebhookStatusMatcher` doesn't exist yet.

- [ ] **Step 4: Implement `HaloWebhookStatusMatcher`**

```csharp
// src/DotMarc/Notifications/HaloWebhookStatusMatcher.cs
namespace DotMarc.Notifications;

public static class HaloWebhookStatusMatcher
{
    public static bool IsClosedStatus(HaloWebhookTicketPayload payload, HaloPsaSettings settings) =>
        settings.ClosedStatusId is { } closedStatusId && payload.StatusId == closedStatusId;
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test dotMARC.sln --filter HaloWebhookStatusMatcherTests`
Expected: PASS.

- [ ] **Step 6: Write the failing endpoint integration test**

```csharp
// test/DotMarc.Tests/Notifications/HaloWebhookEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class HaloWebhookEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program>? _factory;

    public HaloWebhookEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using (var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options))
        {
            await context.Database.MigrateAsync();
            var settings = await context.HaloPsaSettings.SingleAsync();
            settings.WebhookSecret = "the-webhook-secret";
            settings.ClosedStatusId = 9;
            context.AlertEvents.Add(new AlertEvent
            {
                DomainName = "contoso.io", AlertType = "MissedReport", Severity = "Warning", Title = "t", Message = "m",
                ExternalTicketProvider = "HaloPSA", ExternalTicketId = "4242"
            });
            await context.SaveChangesAsync();
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", _connectionString);
            builder.UseSetting("Demo:Enabled", "true");
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    [Fact]
    public async Task ClosedStatusPayload_ResolvesTheMatchingAlert()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync("/integrations/halopsa/webhook/the-webhook-secret", new { ticket_id = 4242, status_id = 9 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        var alert = await context.AlertEvents.SingleAsync();
        Assert.True(alert.IsResolved);
    }

    [Fact]
    public async Task WrongSecret_ReturnsNotFound_AndDoesNotResolveAnything()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync("/integrations/halopsa/webhook/wrong-secret", new { ticket_id = 4242, status_id = 9 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        Assert.False((await context.AlertEvents.SingleAsync()).IsResolved);
    }

    [Fact]
    public async Task UnrelatedStatusChange_ReturnsOk_AndDoesNotResolveAnything()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsJsonAsync("/integrations/halopsa/webhook/the-webhook-secret", new { ticket_id = 4242, status_id = 3 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        Assert.False((await context.AlertEvents.SingleAsync()).IsResolved);
    }
}
```

- [ ] **Step 7: Run the tests to verify they fail**

Run: `dotnet test dotMARC.sln --filter HaloWebhookEndpointTests`
Expected: FAIL — the endpoint doesn't exist yet (404 on every request, including the "wrong secret" case which expects 404 for a different reason — check the response body/logs to confirm it's actually "no such route", not the intended behavior, before moving on).

- [ ] **Step 8: Add the endpoint to `Program.cs`**

```csharp
// src/DotMarc/Program.cs — after the existing /dns-push/{provider}/callback endpoint
using System.Security.Cryptography;
using System.Text;
// ... add to existing usings

// Unauthenticated by necessity — HaloPSA's own outbound webhook config isn't confirmed to support
// custom headers, so the shared secret travels in the path instead. A non-matching secret returns
// 404 rather than 401 so an unauthenticated caller can't even confirm this endpoint exists.
app.MapPost("/integrations/halopsa/webhook/{secret}", async (
    string secret, HaloWebhookTicketPayload payload, IDbContextFactory<DotMarcDbContext> dbContextFactory) =>
{
    await using var context = await dbContextFactory.CreateDbContextAsync();
    var settings = await context.HaloPsaSettings.SingleAsync();

    if (string.IsNullOrEmpty(settings.WebhookSecret) ||
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(settings.WebhookSecret)))
    {
        return Results.NotFound();
    }

    if (!HaloWebhookStatusMatcher.IsClosedStatus(payload, settings))
    {
        return Results.Ok();
    }

    var ticketId = payload.TicketId.ToString();
    var alert = await context.AlertEvents.FirstOrDefaultAsync(e =>
        e.ExternalTicketProvider == "HaloPSA" && e.ExternalTicketId == ticketId && !e.IsResolved);

    if (alert is not null)
    {
        alert.IsResolved = true;
        alert.ResolvedUtc = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync();
    }

    return Results.Ok();
}).AllowAnonymous();
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test dotMARC.sln --filter HaloWebhookEndpointTests`
Expected: PASS (all three tests).

- [ ] **Step 10: Commit**

```bash
git add src/DotMarc/Notifications/HaloWebhookTicketPayload.cs src/DotMarc/Notifications/HaloWebhookStatusMatcher.cs src/DotMarc/Program.cs test/DotMarc.Tests/Notifications/HaloWebhookStatusMatcherTests.cs test/DotMarc.Tests/Notifications/HaloWebhookEndpointTests.cs
git commit -m "Add inbound HaloPSA webhook endpoint that resolves alerts on ticket close"
```

---

## Task 9: Alert settings UI — PSA integration section

**Files:**
- Modify: `src/DotMarc/Components/Pages/AlertsSettings.razor`

**Interfaces:**
- Consumes: `HaloPsaSettingsService` (Task 4), `IHaloPsaClient` (Task 5).

No automated test for this task — Blazor component rendering has no test harness in this codebase (established gap, verified manually elsewhere). Manual verification steps are listed at the end instead of a TDD cycle.

- [ ] **Step 1: Add the PSA integration section to `AlertsSettings.razor`**

Add `@inject IHaloPsaClient HaloPsaClient` alongside the existing injects, and a new `MudPaper` section after the existing Save button, with its own state and handlers:

```razor
@* Add to the existing @using/@inject block at the top *@
@inject IHaloPsaClient HaloPsaClient
```

```razor
@* Add after the closing </MudPaper> of the existing settings block *@
@if (_haloSettings is not null)
{
    <MudPaper Class="pa-4 mt-4">
        <MudText Typo="Typo.h5" Class="mb-4">PSA integration (HaloPSA)</MudText>
        <MudGrid>
            <MudItem xs="12">
                <MudSwitch @bind-Value="_haloSettings.Enabled" Color="Color.Primary" Label="Enable HaloPSA ticket sync" />
            </MudItem>
            <MudItem xs="12" md="6">
                <MudTextField Label="Account name" @bind-Value="_haloSettings.AccountName" Variant="Variant.Outlined" />
            </MudItem>
            <MudItem xs="12" md="6">
                <MudTextField Label="Client ID" @bind-Value="_haloSettings.ClientId" Variant="Variant.Outlined" />
            </MudItem>
            <MudItem xs="12" md="6">
                <MudTextField Label="Auth server URL" @bind-Value="_haloSettings.AuthServerUrl" Placeholder="https://<account>.halopsa.com/auth" Variant="Variant.Outlined" />
            </MudItem>
            <MudItem xs="12" md="6">
                <MudTextField Label="Resource server URL" @bind-Value="_haloSettings.ResourceServerUrl" Placeholder="https://<account>.halopsa.com/api" Variant="Variant.Outlined" />
            </MudItem>
            <MudItem xs="12" md="6">
                <MudTextField Label="Client secret" @bind-Value="_newHaloClientSecret" InputType="InputType.Password" Variant="Variant.Outlined"
                              HelperText="@(_haloSettings.ClientSecretConfigured ? "A secret is already configured — leave blank to keep it." : "No secret configured yet.")" />
            </MudItem>
            <MudItem xs="12" md="6" Class="d-flex align-center">
                <MudButton Variant="Variant.Outlined" OnClick="LoadHaloOptionsAsync">Load ticket types / priorities / statuses from Halo</MudButton>
            </MudItem>
            <MudItem xs="12" md="4">
                <MudSelect T="int?" Label="Ticket type" @bind-Value="_haloSettings.TicketTypeId" Variant="Variant.Outlined">
                    @foreach (var ticketType in _haloTicketTypes)
                    {
                        <MudSelectItem T="int?" Value="@((int?)ticketType.Id)">@ticketType.Name</MudSelectItem>
                    }
                </MudSelect>
            </MudItem>
            <MudItem xs="12" md="4">
                <MudSelect T="int?" Label="Default priority" @bind-Value="_haloSettings.DefaultPriorityId" Variant="Variant.Outlined">
                    @foreach (var status in _haloStatuses)
                    {
                        <MudSelectItem T="int?" Value="@((int?)status.Id)">@status.Name</MudSelectItem>
                    }
                </MudSelect>
            </MudItem>
            <MudItem xs="12" md="4">
                <MudSelect T="int?" Label="Closed status" @bind-Value="_haloSettings.ClosedStatusId" Variant="Variant.Outlined">
                    @foreach (var status in _haloStatuses)
                    {
                        <MudSelectItem T="int?" Value="@((int?)status.Id)">@status.Name</MudSelectItem>
                    }
                </MudSelect>
            </MudItem>
            <MudItem xs="12">
                <MudTextField Label="Webhook URL" ReadOnly="true" Value="@WebhookUrl" Variant="Variant.Outlined"
                              HelperText="Point HaloPSA's outbound webhook (on ticket status change) at this URL." />
            </MudItem>
            <MudItem xs="12" Class="d-flex align-center" Style="gap: 0.5rem;">
                <MudButton Variant="Variant.Outlined" OnClick="GenerateWebhookSecretAsync">Generate new webhook secret</MudButton>
            </MudItem>
        </MudGrid>
        <MudButton Variant="Variant.Filled" Color="Color.Primary" Class="mt-4" OnClick="SaveHaloSettingsAsync">Save PSA settings</MudButton>
    </MudPaper>
}
```

- [ ] **Step 2: Add the backing state and handlers to `@code`**

```csharp
// AlertsSettings.razor — add to the existing @code block
private HaloPsaSettings? _haloSettings;
private string? _newHaloClientSecret;
private List<HaloTicketType> _haloTicketTypes = [];
private List<HaloTicketStatus> _haloStatuses = [];

private string WebhookUrl => _haloSettings?.WebhookSecret is { Length: > 0 } secret
    ? $"{NavigationManager.BaseUri.TrimEnd('/')}/integrations/halopsa/webhook/{secret}"
    : "Generate a webhook secret first.";

private async Task LoadHaloOptionsAsync()
{
    if (_haloSettings is null)
    {
        return;
    }

    try
    {
        _haloTicketTypes = (await HaloPsaClient.ListTicketTypesAsync(_haloSettings)).ToList();
        _haloStatuses = (await HaloPsaClient.ListStatusesAsync(_haloSettings)).ToList();
        Snackbar.Add("Loaded ticket types and statuses from Halo.", Severity.Success);
    }
    catch (Exception)
    {
        Snackbar.Add("Couldn't reach HaloPSA — save your account/credentials first, then try again.", Severity.Error);
    }
}

private Task GenerateWebhookSecretAsync()
{
    if (_haloSettings is not null)
    {
        _haloSettings.WebhookSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    }
    return Task.CompletedTask;
}

private async Task SaveHaloSettingsAsync()
{
    if (_haloSettings is null)
    {
        return;
    }

    try
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var secretStore = SecretStoreAccessor;
        await HaloPsaSettingsService.SaveAsync(db, secretStore, _haloSettings, string.IsNullOrWhiteSpace(_newHaloClientSecret) ? null : _newHaloClientSecret);
        _newHaloClientSecret = null;
        Snackbar.Add("PSA settings saved.", Severity.Success);

        await using var reloadDb = await DbFactory.CreateDbContextAsync();
        _haloSettings = await HaloPsaSettingsService.GetAsync(reloadDb);
    }
    catch (Exception)
    {
        Snackbar.Add("Failed to save PSA settings. Try again.", Severity.Error);
    }
}
```

Add `@inject IHaloSecretStore SecretStoreAccessor` and `@inject NavigationManager NavigationManager` to the top of the file alongside the other injects, and load `_haloSettings` in the existing `OnInitializedAsync`:

```csharp
protected override async Task OnInitializedAsync()
{
    await using var db = await DbFactory.CreateDbContextAsync();
    _settings = await NotificationSettingsService.GetAsync(db);
    _haloSettings = await HaloPsaSettingsService.GetAsync(db);
}
```

- [ ] **Step 3: Build and smoke-test manually**

```powershell
docker compose up postgres -d
dotnet run --project src/DotMarc/DotMarc.csproj
```

Sign in (demo mode or real auth per local setup), navigate to `/alerts/settings`, confirm the new "PSA integration (HaloPSA)" section renders, "Generate new webhook secret" populates the read-only webhook URL field, and "Save PSA settings" persists without error (check the `HaloPsaSettings` row in Postgres directly if needed). "Load ticket types..." will fail gracefully (toast, not a crash) without real Halo credentials — that's expected at this stage.

- [ ] **Step 4: Commit**

```bash
git add src/DotMarc/Components/Pages/AlertsSettings.razor
git commit -m "Add PSA integration section to Alert settings"
```

---

## Task 10: Manage Groups / Manage Domains UI — Halo Client mapping

**Files:**
- Modify: `src/DotMarc/Components/Pages/ManageGroups.razor`
- Modify: `src/DotMarc/Components/Pages/ManageDomains.razor`

**Interfaces:**
- Consumes: `GroupManagementService.SetHaloClientIdAsync`, `DomainManagementService.SetHaloClientIdAsync` (Task 6), `IHaloPsaClient.ListClientsAsync` (Task 5).

No automated test — same established gap as Task 9.

- [ ] **Step 1: Add the Halo Client column to `ManageGroups.razor`**

Add `@inject IHaloPsaClient HaloPsaClient` and `@inject IDbContextFactory<DotMarcDbContext> DbFactory` is already present — add loading the Halo client list and current `HaloPsaSettings` in `OnInitializedAsync`, and extend `GroupRow`:

```csharp
// @code block additions
private List<HaloClient> _haloClients = [];
private bool _haloConfigured;
```

In `LoadAsync`, after loading `_tags`:

```csharp
await using var haloSettingsDb = await DbFactory.CreateDbContextAsync();
var haloSettings = await HaloPsaSettingsService.GetAsync(haloSettingsDb);
_haloConfigured = haloSettings.Enabled && haloSettings.ClientSecretConfigured;
if (_haloConfigured)
{
    try
    {
        _haloClients = (await HaloPsaClient.ListClientsAsync(haloSettings)).ToList();
    }
    catch (Exception)
    {
        _haloClients = [];
    }
}
```

Change `GroupRow` to carry the mapping and a suggested match:

```csharp
private sealed record GroupRow(int Id, string Name, int DomainCount, int? HaloClientId);
```

Update the `_groups` projection in `LoadAsync`:

```csharp
_groups = await db.Groups
    .AsNoTracking()
    .OrderBy(g => g.Name)
    .Select(g => new GroupRow(g.Id, g.Name, g.Domains.Count, g.HaloClientId))
    .ToListAsync();
```

Add a new `<MudTh>Halo Client</MudTh>` header and cell (only rendered when `_haloConfigured`, matching the spec's "the push button simply never appears" pattern for unconfigured integrations):

```razor
@if (_haloConfigured)
{
    <MudTh>Halo Client</MudTh>
}
```

```razor
@if (_haloConfigured)
{
    <MudTd>
        <MudSelect T="int?" Value="context.HaloClientId" ValueChanged="@(id => SetHaloClientIdAsync(context, id))" Clearable="true">
            @foreach (var haloClient in _haloClients)
            {
                <MudSelectItem T="int?" Value="@((int?)haloClient.Id)">@haloClient.Name</MudSelectItem>
            }
        </MudSelect>
    </MudTd>
}
```

Add the handler:

```csharp
private async Task SetHaloClientIdAsync(GroupRow row, int? haloClientId)
{
    try
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        await GroupManagementService.SetHaloClientIdAsync(db, row.Id, haloClientId, CancellationToken.None);
    }
    catch (Exception)
    {
        Snackbar.Add($"Failed to update {row.Name}'s Halo client. Try again.", Severity.Error);
    }
    await LoadAsync();
}
```

- [ ] **Step 2: Add the name-match suggestion**

`MudSelect`'s `Value` is already bound to `context.HaloClientId`, which is `null` for an unmapped Group — a case-insensitive name match is a *suggestion* shown next to the picker, not auto-applied (matching the MTA-STS MX-hosts sync icon precedent: review then explicitly save). Add a small hint under the picker when unmapped and a match exists:

```razor
@{
    var suggestion = !context.HaloClientId.HasValue
        ? _haloClients.FirstOrDefault(c => string.Equals(c.Name, context.Name, StringComparison.OrdinalIgnoreCase))
        : null;
}
@if (suggestion is not null)
{
    <MudText Typo="Typo.caption">
        Suggested match: @suggestion.Name.
        <MudLink OnClick="@(() => SetHaloClientIdAsync(context, suggestion.Id))">Use it</MudLink>
    </MudText>
}
```

(Place this `MudText` immediately below the `MudSelect` inside the same `MudTd`.)

- [ ] **Step 3: Add the override field to `ManageDomains.razor`**

Add `@inject IHaloPsaClient HaloPsaClient` alongside the existing injects. Add the same two fields as `ManageGroups.razor` got in Step 1:

```csharp
// @code block additions
private List<HaloClient> _haloClients = [];
private bool _haloConfigured;
```

In `LoadAsync`, after the existing `_allTags` load:

```csharp
await using var haloSettingsDb = await DbFactory.CreateDbContextAsync();
var haloSettings = await HaloPsaSettingsService.GetAsync(haloSettingsDb);
_haloConfigured = haloSettings.Enabled && haloSettings.ClientSecretConfigured;
if (_haloConfigured)
{
    try
    {
        _haloClients = (await HaloPsaClient.ListClientsAsync(haloSettings)).ToList();
    }
    catch (Exception)
    {
        _haloClients = [];
    }
}
```

Extend `DomainRow` and its projection in `LoadAsync`:

```csharp
private sealed record DomainRow(int Id, string Name, bool IsMonitored, int ReportCount, DateTimeOffset? LastReportReceivedUtc, List<int> GroupIds, List<int> TagIds, int? HaloClientId);
```

```csharp
_domains = await db.Domains
    .AsNoTracking()
    .OrderBy(d => d.SortOrder)
    .ThenBy(d => d.Name)
    .Select(d => new DomainRow(d.Id, d.Name, d.IsMonitored, d.Reports.Count, d.LastReportReceivedUtc,
        d.Groups.Select(g => g.Id).ToList(), d.Tags.Select(t => t.Id).ToList(), d.HaloClientId))
    .ToListAsync();
```

Add a new conditional header, right after the existing `<MudTh>Tags</MudTh>`:

```razor
@if (_haloConfigured)
{
    <MudTh>Halo Client override</MudTh>
}
```

Add the matching cell, right after the existing Tags `<MudTd>` block:

```razor
@if (_haloConfigured)
{
    <MudTd>
        <MudSelect T="int?" Value="context.HaloClientId" ValueChanged="@(id => SetHaloClientIdAsync(context, id))" Clearable="true">
            @foreach (var haloClient in _haloClients)
            {
                <MudSelectItem T="int?" Value="@((int?)haloClient.Id)">@haloClient.Name</MudSelectItem>
            }
        </MudSelect>
    </MudTd>
}
```

No name-match suggestion here — this is explicitly the override/exception path, not the common one. Add the handler:

```csharp
private async Task SetHaloClientIdAsync(DomainRow row, int? haloClientId)
{
    try
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        await DomainManagementService.SetHaloClientIdAsync(db, row.Id, haloClientId, CancellationToken.None);
    }
    catch (Exception)
    {
        Snackbar.Add($"Failed to update {row.Name}'s Halo client. Try again.", Severity.Error);
    }
    await LoadAsync();
}
```

- [ ] **Step 4: Build and smoke-test manually**

```powershell
dotnet build dotMARC.sln
dotnet run --project src/DotMarc/DotMarc.csproj
```

Navigate to `/groups` and `/domains`. Without Halo configured (`_haloConfigured` false), confirm neither page shows a Halo Client column at all (no regression to the existing layout). This can't be verified end-to-end against real Halo without live credentials (Task 5 Step 8) — confirm at minimum that the column correctly stays hidden when unconfigured.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Components/Pages/ManageGroups.razor src/DotMarc/Components/Pages/ManageDomains.razor
git commit -m "Add Halo Client mapping to Manage Groups and Manage Domains"
```

---

## Task 11: Docs

**Files:**
- Create: `website/docs/psa-integration.mdx`
- Modify: `website/docs/deploy-to-azure.mdx`
- Modify: `website/docs/alerts.mdx`

**Interfaces:**
- None — documentation only.

- [ ] **Step 1: Write `website/docs/psa-integration.mdx`**

```mdx
---
sidebar_position: 8
description: Sync dotMARC alerts to HaloPSA tickets, opened and closed automatically in both directions.
---

# PSA integration (HaloPSA)

dotMARC can open a HaloPSA ticket when an alert fires, close it automatically when the alert
resolves, and resolve the alert back when a tech closes the ticket directly in Halo. See
[Alerts](./alerts.mdx) for how alerting itself works — this page covers wiring it to HaloPSA
specifically.

## Set up the Halo API application

In HaloPSA, go to **Configuration → Integrations → HaloPSA API** and create an application
with these scopes: `edit:tickets`, `read:tickets`, `read:customers`, `read:teams`. Note its
account name, auth server URL, resource server URL, client ID, and client secret.

## Configure dotMARC

From **Alert settings**, in the **PSA integration (HaloPSA)** section: enable it, fill in the
account name/URLs/client ID, and paste in the client secret (it's write-only — once saved, you
won't see it again, only whether one is configured). Click **Load ticket types / priorities /
statuses from Halo** to populate the three dropdowns, pick the ticket type new tickets should use,
the default priority, and which Halo status counts as "closed" for resolving the alert back.

Click **Generate new webhook secret**, then copy the shown webhook URL into HaloPSA's own outbound
webhook configuration, triggered on ticket status change. This is what lets a ticket closed
directly in Halo resolve the dotMARC alert.

Where the client secret itself is stored (Postgres, encrypted, or Azure Key Vault) depends on your
deployment, see [Deploy to Azure](./deploy-to-azure.mdx#optional-halopsa-key-vault-storage) if
you're running on Azure and want it in Key Vault.

## Route tickets to the right client

Tickets need to land against the right Halo client (company). From **Manage groups**, each Group
gets a **Halo Client** picker — set once per Group (typically once per MSP client), every domain
in that Group routes there. If a Group's name matches a Halo client's name, a suggested match
appears; review and click **Use it** rather than it being applied automatically. A domain that
needs to route differently than its Group can be overridden individually from **Manage domains**.

A domain with no mapping (no Group, or a Group with none set) simply doesn't get a ticket, same as
leaving the generic webhook URL blank, it isn't an error.

## What syncs, and what doesn't

- Alert fires → ticket created, using the alert's title/message as the ticket's summary/details.
- Alert resolves in dotMARC (the report comes back, or a later TLSRPT report has no failures) →
  ticket closed in Halo automatically.
- Ticket closed in Halo → alert resolved in dotMARC automatically, via the webhook above.
- Nothing else syncs: no comments, no reassignment, no priority changes flow back from Halo. If
  the underlying condition is still active after a ticket's closed early in Halo, the next check
  cycle re-opens the alert (and a new ticket) once the cooldown allows — this is expected, not a
  sync bug.
```

- [ ] **Step 2: Add the Key Vault storage subsection to `deploy-to-azure.mdx`**

Add after the existing "Optional: DNS provider push secrets" subsection (before "## 5. Re-running the template later"):

```mdx
### Optional: HaloPSA Key Vault storage

By default the HaloPSA API client secret entered through Alert settings is encrypted and stored
in Postgres. To store it in this deployment's Key Vault instead, redeploy with
`enableHaloPsaKeyVaultWrite` set to `true` — this grants the container app's managed identity a
narrowly-scoped write role on the vault (see `infra/main.bicep`'s `haloPsaKeyVaultWriteRole`, it
adds only `secrets/setSecret`, read is already covered by the existing `Key Vault Secrets User`
assignment). No manual `az keyvault secret set` needed here, the app writes the secret itself the
first time you save it from Alert settings.
```

- [ ] **Step 3: Cross-link from `alerts.mdx`**

Add a line after the existing "Delivery channels" intro paragraph in `website/docs/alerts.mdx` (find the right spot by reading the file's current structure first):

```mdx
For syncing alerts to a PSA instead of (or alongside) Teams/webhook delivery, see [PSA
integration](./psa-integration.mdx).
```

- [ ] **Step 4: Build the docs site to verify links/anchors resolve**

```powershell
cd website
npx docusaurus build --out-dir build-check
```

Expected: build succeeds (a broken internal link fails the build). Confirm the docs page count increased by one. Remove `build-check` afterward.

- [ ] **Step 5: Commit**

```bash
git add website/docs/psa-integration.mdx website/docs/deploy-to-azure.mdx website/docs/alerts.mdx
git commit -m "Document the HaloPSA integration"
```

---

## Self-review notes

- **Spec coverage:** every section of the spec (`2026-09-02-halopsa-integration-design.md`) maps to a task — data model → Task 1; secret storage (both backends) → Tasks 2–3; Halo API client → Task 5; client mapping/resolution → Task 6; ticket lifecycle wiring → Task 7; inbound webhook → Task 8; UI → Tasks 9–10; testing conventions and docs are folded into each task and Task 11 respectively.
- **Signature correction:** `IPsaTicketService.CreateTicketAsync` drops the `Domain domain` parameter the spec sketched — `AlertingService.EnsureAlertAsync` only ever has `domainName` (a string) in scope, never a loaded `Domain` entity, so `PsaTicketService` loads it itself (with `Groups` included, since `HaloClientResolver` needs that navigation populated). Flagged inline in Task 7.
- **Type consistency check:** `IHaloPsaClient`, `IPsaTicketService`, `IHaloSecretStore`, `HaloPsaSettings`, `HaloClientResolver.Resolve`, and `HaloWebhookStatusMatcher.IsClosedStatus` all use the same signatures everywhere they're referenced across Tasks 4–10.
- **`AlertingService` singleton/scoped check:** `IPsaTicketService` is registered `AddSingleton` (Task 7 Step 8), matching `IAlertWebhookClient`'s existing lifetime — `AlertingService` itself is a singleton and cannot depend on a scoped service.
