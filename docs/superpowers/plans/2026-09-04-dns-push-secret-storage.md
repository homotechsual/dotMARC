# DNS Push Secret Storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move Cloudflare/Azure DNS push OAuth client credentials off deploy-time environment variables onto the same DB-backed, admin-editable pattern HaloPSA already uses — a new settings page, no redeploy needed to configure or rotate them — and generalize HaloPSA's secret-store code so it's shared across all three secrets instead of duplicated a second and third time.

**Architecture:** `IHaloSecretStore` becomes a generic, keyed `ISecretStore` (`SetSecretAsync(key, value)` / `GetSecretAsync(key)`), backed by a new shared `EncryptedSecrets(Key, ProtectedValue)` table (Postgres + Data Protection, default) or `KeyVaultSecretStore` (Azure, opt-in via the renamed `enableKeyVaultWrite`) — one deployment-wide choice, not per-integration. Two new DB-backed settings entities (`CloudflareDnsSettings`, `AzureDnsSettings`) replace `CloudflareDnsOptions`/`AzureDnsOptions`. `CloudflareDnsPushProvider`/`AzureDnsPushProvider` read settings fresh per call instead of caching `IOptions<T>` at construction, which forces two `IDnsPushProvider` interface members from sync to async — a confirmed-safe change since neither is ever read from Razor markup.

**Tech Stack:** ASP.NET Core 10 / Blazor Server, EF Core + Npgsql, MudBlazor, `Azure.Security.KeyVault.Secrets`, xUnit + `Testcontainers.PostgreSql`.

**Spec:** `docs/superpowers/specs/2026-09-04-dns-push-secret-storage-design.md`

## Global Constraints

- Clean break from `CloudflareDns__*`/`AzureDns__*` env vars — no read-as-seed fallback, no back-compat path.
- The `ClientSecretConfigured`-set-if-and-only-if-a-new-secret-was-provided invariant applies to `CloudflareDnsSettings`/`AzureDnsSettings` exactly as it already does to `HaloPsaSettings` — get this right the first time (see Task 4 of the HaloPSA plan, which got it wrong on the first pass).
- Round-trip tests for any settings service must read back via a **fresh** `DotMarcDbContext`, never the context that performed the write (EF Core's identity map otherwise makes the assertion pass even if persistence is broken — this exact bug was caught and fixed once already in this codebase).
- Follow this codebase's static-class-over-caller-supplied-`DotMarcDbContext` convention for any new CRUD service.
- Migration commands: `dotnet ef migrations add <Name> --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`, run from the repo root.
- No automated test coverage for Razor/Blazor component rendering, or for `CloudflareDnsPushProvider`/`AzureDnsPushProvider`'s live external I/O (OAuth exchange, Cloudflare/Azure API calls) — established, accepted gaps in this codebase; verified manually, not something to build new test infrastructure for.

---

## Task 1: Generalize the secret store

**Files:**
- Create: `src/DotMarc/Notifications/ISecretStore.cs`
- Create: `src/DotMarc/Notifications/EncryptedSecret.cs`
- Create: `src/DotMarc/Notifications/DatabaseSecretStore.cs`
- Create: `src/DotMarc/Notifications/KeyVaultSecretStore.cs`
- Delete: `src/DotMarc/Notifications/IHaloSecretStore.cs`
- Delete: `src/DotMarc/Notifications/DatabaseHaloSecretStore.cs`
- Delete: `src/DotMarc/Notifications/KeyVaultHaloSecretStore.cs`
- Modify: `src/DotMarc/Notifications/HaloPsaSettings.cs`
- Modify: `src/DotMarc/Notifications/HaloPsaSettingsService.cs`
- Modify: `src/DotMarc/Notifications/HaloPsaClient.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Modify: `src/DotMarc/Components/Pages/AlertsSettings.razor`
- Modify: `src/DotMarc/Program.cs`
- Modify: `infra/main.bicep`
- Modify: `infra/main.parameters.json`
- Create (generated): `src/DotMarc/Migrations/<timestamp>_GeneralizeSecretStorage.cs` and `.Designer.cs`
- Create: `test/DotMarc.Tests/Notifications/DatabaseSecretStoreTests.cs`
- Create: `test/DotMarc.Tests/Notifications/KeyVaultSecretStoreTests.cs`
- Delete: `test/DotMarc.Tests/Notifications/DatabaseHaloSecretStoreTests.cs`
- Delete: `test/DotMarc.Tests/Notifications/KeyVaultHaloSecretStoreTests.cs`
- Modify: `test/DotMarc.Tests/Notifications/HaloPsaSettingsServiceTests.cs`
- Modify: `test/DotMarc.Tests/Notifications/HaloPsaClientTests.cs`

**Interfaces:**
- Produces: `ISecretStore` (`SetSecretAsync(string key, string value, CancellationToken)`, `GetSecretAsync(string key, CancellationToken) -> string?`), `HaloPsaSettings.SecretStoreKey = "HaloPsa.ClientSecret"`. Consumed by Task 2 (`CloudflareDnsSettings`/`AzureDnsSettings` follow the same `SecretStoreKey` convention) and Task 3 (`CloudflareDnsPushProvider`/`AzureDnsPushProvider`).

This is the largest task in this plan because it's a rename-and-rework of already-shipped, working code — every piece has to move together or the build breaks. There's no way to land it in smaller independently-buildable slices.

- [ ] **Step 1: Define `ISecretStore`**

```csharp
// src/DotMarc/Notifications/ISecretStore.cs
namespace DotMarc.Notifications;

/// <summary>Stores and retrieves an encrypted secret by key. Two implementations:
/// DatabaseSecretStore (Postgres + Data Protection, the default/fallback) and KeyVaultSecretStore
/// (Azure, opt-in) — selected in Program.cs on whether KeyVault:VaultUri is configured. Shared
/// across every integration that needs a runtime-editable secret (HaloPSA, Cloudflare DNS push,
/// Azure DNS push) rather than one near-identical store per integration. Keys are dot-namespaced
/// business names (e.g. "HaloPsa.ClientSecret") defined as a SecretStoreKey constant on the
/// settings entity the secret belongs to.</summary>
public interface ISecretStore
{
    Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Define the `EncryptedSecret` entity**

```csharp
// src/DotMarc/Notifications/EncryptedSecret.cs
namespace DotMarc.Notifications;

/// <summary>One row per secret key, written and read only by DatabaseSecretStore. ProtectedValue
/// is Data Protection-encrypted ciphertext, never plaintext.</summary>
public sealed class EncryptedSecret
{
    public required string Key { get; set; }
    public required string ProtectedValue { get; set; }
}
```

- [ ] **Step 3: Remove `ProtectedClientSecret` from `HaloPsaSettings`, add `SecretStoreKey`**

```csharp
// src/DotMarc/Notifications/HaloPsaSettings.cs
namespace DotMarc.Notifications;

/// <summary>Singleton settings row for the HaloPSA PSA integration — same "exactly one row,
/// seeded via migration HasData" pattern as NotificationSettings. The client secret itself lives
/// in ISecretStore under SecretStoreKey, never on this entity — every reader of this entity should
/// treat ClientSecretConfigured as the only signal about the secret's presence.</summary>
public sealed class HaloPsaSettings
{
    public const string SecretStoreKey = "HaloPsa.ClientSecret";

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
}
```

(`ProtectedClientSecret` removed entirely — it's not renamed or kept, the value now lives in `EncryptedSecrets`.)

- [ ] **Step 4: Register `EncryptedSecret` in `DotMarcDbContext`**

Add to the `DbSet` list, after `HaloPsaSettings`:

```csharp
public DbSet<EncryptedSecret> EncryptedSecrets => Set<EncryptedSecret>();
```

Add to `OnModelCreating`, after the `AlertEvent` configuration block (`EncryptedSecret.Key` isn't named `Id`, so EF's default-PK convention won't pick it up — needs an explicit `HasKey`):

```csharp
modelBuilder.Entity<EncryptedSecret>(entity =>
{
    entity.HasKey(s => s.Key);
});
```

- [ ] **Step 5: Implement `DatabaseSecretStore`**

```csharp
// src/DotMarc/Notifications/DatabaseSecretStore.cs
using System.Security.Cryptography;
using DotMarc.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public sealed class DatabaseSecretStore : ISecretStore
{
    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly IDataProtector _protector;

    public DatabaseSecretStore(IDbContextFactory<DotMarcDbContext> dbFactory, IDataProtectionProvider dataProtectionProvider)
    {
        _dbFactory = dbFactory;
        _protector = dataProtectionProvider.CreateProtector("DotMarc.Notifications.EncryptedSecret.v1");
    }

    public async Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var protectedValue = _protector.Protect(value);
        var existing = await context.EncryptedSecrets.SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            context.EncryptedSecrets.Add(new EncryptedSecret { Key = key, ProtectedValue = protectedValue });
        }
        else
        {
            existing.ProtectedValue = protectedValue;
        }
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await context.EncryptedSecrets.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(existing.ProtectedValue);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 6: Implement `KeyVaultSecretStore`**

```csharp
// src/DotMarc/Notifications/KeyVaultSecretStore.cs
using Azure;
using Azure.Security.KeyVault.Secrets;

namespace DotMarc.Notifications;

/// <summary>Stores secrets in the Key Vault infra/main.bicep already provisions, one Key Vault
/// secret per store key — dots aren't valid in Key Vault secret names, so "HaloPsa.ClientSecret"
/// becomes "HaloPsa-ClientSecret" (matching the name already in production use). Selected instead
/// of DatabaseSecretStore when KeyVault:VaultUri is configured (see Program.cs) — requires the
/// container's managed identity to hold the write role infra/main.bicep grants only when
/// enableKeyVaultWrite is true. Values never touch Postgres.</summary>
public sealed class KeyVaultSecretStore : ISecretStore
{
    private readonly SecretClient _secretClient;

    public KeyVaultSecretStore(SecretClient secretClient) => _secretClient = secretClient;

    public async Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default) =>
        await _secretClient.SetSecretAsync(ToKeyVaultName(key), value, cancellationToken).ConfigureAwait(false);

    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = await _secretClient.GetSecretAsync(ToKeyVaultName(key), cancellationToken: cancellationToken).ConfigureAwait(false);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static string ToKeyVaultName(string key) => key.Replace('.', '-');
}
```

- [ ] **Step 7: Delete the old `IHaloSecretStore`/`DatabaseHaloSecretStore`/`KeyVaultHaloSecretStore` files**

```bash
rm src/DotMarc/Notifications/IHaloSecretStore.cs src/DotMarc/Notifications/DatabaseHaloSecretStore.cs src/DotMarc/Notifications/KeyVaultHaloSecretStore.cs
```

- [ ] **Step 8: Update `HaloPsaSettingsService` to use `ISecretStore`**

```csharp
// src/DotMarc/Notifications/HaloPsaSettingsService.cs
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class HaloPsaSettingsService
{
    public static Task<HaloPsaSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.HaloPsaSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, ISecretStore secretStore, HaloPsaSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
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
            await secretStore.SetSecretAsync(HaloPsaSettings.SecretStoreKey, newClientSecret, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

(Only two things changed from the current file: the parameter type `IHaloSecretStore` → `ISecretStore`, and the call `secretStore.SetClientSecretAsync(newClientSecret, ct)` → `secretStore.SetSecretAsync(HaloPsaSettings.SecretStoreKey, newClientSecret, ct)`.)

- [ ] **Step 9: Update `HaloPsaClient` to use `ISecretStore`**

In `src/DotMarc/Notifications/HaloPsaClient.cs`, change the field type and constructor parameter `IHaloSecretStore _secretStore` → `ISecretStore _secretStore`, and inside the private `SendAsync` method change:

```csharp
var clientSecret = await _secretStore.GetClientSecretAsync(cancellationToken).ConfigureAwait(false)
    ?? throw new InvalidOperationException("HaloPSA client secret is not configured.");
```

to:

```csharp
var clientSecret = await _secretStore.GetSecretAsync(HaloPsaSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false)
    ?? throw new InvalidOperationException("HaloPSA client secret is not configured.");
```

Nothing else in this file changes.

- [ ] **Step 10: Update `AlertsSettings.razor`'s injected type**

Change `@inject IHaloSecretStore SecretStoreAccessor` to `@inject ISecretStore SecretStoreAccessor`. Nothing else on this page changes (the injected instance is still passed straight through to `HaloPsaSettingsService.SaveAsync`, whose signature already takes the new interface as of Step 8).

- [ ] **Step 11: Update `Program.cs`'s DI registration**

```csharp
// src/DotMarc/Program.cs — replace the existing IHaloSecretStore block
// KeyVault:VaultUri is only set by infra/main.bicep when enableKeyVaultWrite is true (see
// KeyVault__VaultUri there); every other deployment — including local/Docker Compose — leaves it
// unset and falls back to the Postgres-backed store.
var keyVaultUri = builder.Configuration["KeyVault:VaultUri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Services.AddSingleton(new SecretClient(new Uri(keyVaultUri), new DefaultAzureCredential()));
    builder.Services.AddSingleton<ISecretStore, KeyVaultSecretStore>();
}
else
{
    builder.Services.AddSingleton<ISecretStore, DatabaseSecretStore>();
}
```

- [ ] **Step 12: Rename the Bicep flag and role**

In `infra/main.bicep`:

- Rename the param `enableHaloPsaKeyVaultWrite` → `enableKeyVaultWrite`, updating its description to no longer be Halo-specific:

```bicep
@description('Grant the container app write access to its own Key Vault, used to store runtime-editable secrets (HaloPSA API client secret, Cloudflare/Azure DNS push OAuth client secrets) entered through their respective settings pages rather than in Postgres. Off by default, since it widens the managed identity beyond Key Vault Secrets User (read-only) — see deploy-to-azure.mdx.')
param enableKeyVaultWrite bool = false
```

- Rename every reference to `enableHaloPsaKeyVaultWrite` → `enableKeyVaultWrite` (the `KeyVault__VaultUri` env var's conditional, and the two role resources' `if` conditions).
- Rename `haloPsaKeyVaultWriteRole` → `keyVaultWriteRole` and `haloPsaKeyVaultWriteRoleAssignment` → `keyVaultWriteRoleAssignment` (resource symbolic names, their `name:`/`roleName:` GUIDs and display strings, and the `description:` field — drop "HaloPSA" from the description, e.g. `'Lets dotMARC write runtime secrets into this Key Vault at runtime.'`). The permission itself (`dataActions: ['Microsoft.KeyVault/vaults/secrets/setSecret/action']`) is unchanged — it already covers arbitrary secret names.

- [ ] **Step 13: Rename the parameter in `main.parameters.json`**

```json
"enableKeyVaultWrite": { "value": false }
```

(Replaces the `"enableHaloPsaKeyVaultWrite"` entry.)

- [ ] **Step 14: Validate the Bicep template still compiles**

Run: `az bicep build --file infra/main.bicep --stdout`
Expected: no errors, no "declared but never used" warnings.

- [ ] **Step 15: Generate and apply the migration**

Run: `dotnet ef migrations add GeneralizeSecretStorage --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

Open the generated migration and confirm it contains: a `DropColumn` for `HaloPsaSettings.ProtectedClientSecret`, and a `CreateTable` for `EncryptedSecrets` with `Key` as the primary key and `ProtectedValue` non-nullable. Apply it:

```powershell
docker compose up postgres -d
dotnet ef database update --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj
```

- [ ] **Step 16: Rewrite `DatabaseHaloSecretStoreTests.cs` as `DatabaseSecretStoreTests.cs`**

```bash
rm test/DotMarc.Tests/Notifications/DatabaseHaloSecretStoreTests.cs
```

```csharp
// test/DotMarc.Tests/Notifications/DatabaseSecretStoreTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class DatabaseSecretStoreTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DatabaseSecretStoreTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
        DataProtectionProvider.Create("DotMarc.Tests.EncryptedSecret");

    [Fact]
    public async Task SetThenGet_RoundTripsTheSecret_UnderItsKey()
    {
        var store = new DatabaseSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());

        await store.SetSecretAsync("Test.Key", "super-secret-value");
        var result = await store.GetSecretAsync("Test.Key");

        Assert.Equal("super-secret-value", result);
    }

    [Fact]
    public async Task DifferentKeys_StoreIndependentValues()
    {
        var store = new DatabaseSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());

        await store.SetSecretAsync("Test.KeyA", "value-a");
        await store.SetSecretAsync("Test.KeyB", "value-b");

        Assert.Equal("value-a", await store.GetSecretAsync("Test.KeyA"));
        Assert.Equal("value-b", await store.GetSecretAsync("Test.KeyB"));
    }

    [Fact]
    public async Task SetSecretAsync_OverwritesAnExistingValueUnderTheSameKey()
    {
        var store = new DatabaseSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());
        await store.SetSecretAsync("Test.Key", "first-value");

        await store.SetSecretAsync("Test.Key", "second-value");

        Assert.Equal("second-value", await store.GetSecretAsync("Test.Key"));
    }

    [Fact]
    public async Task GetSecretAsync_ReturnsNull_WhenTheKeyWasNeverSet()
    {
        var store = new DatabaseSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());

        Assert.Null(await store.GetSecretAsync("Test.NeverSet"));
    }

    [Fact]
    public async Task GetSecretAsync_ReturnsNull_WhenProtectedWithADifferentKeyRing()
    {
        var store = new DatabaseSecretStore(new FakeDbContextFactory(_connectionString), CreateProtectionProvider());
        await store.SetSecretAsync("Test.Key", "super-secret-value");

        var storeWithADifferentKeyRing = new DatabaseSecretStore(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.SomeOtherApp"));

        Assert.Null(await storeWithADifferentKeyRing.GetSecretAsync("Test.Key"));
    }
}
```

- [ ] **Step 17: Run the tests to verify they pass**

Run: `dotnet test dotMARC.sln --filter DatabaseSecretStoreTests`
Expected: PASS (all five tests).

- [ ] **Step 18: Rewrite `KeyVaultHaloSecretStoreTests.cs` as `KeyVaultSecretStoreTests.cs`**

```bash
rm test/DotMarc.Tests/Notifications/KeyVaultHaloSecretStoreTests.cs
```

```csharp
// test/DotMarc.Tests/Notifications/KeyVaultSecretStoreTests.cs
using System.Net;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Secrets;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class KeyVaultSecretStoreTests
{
    private static (KeyVaultSecretStore store, FakeHttpMessageHandler handler) CreateStore()
    {
        var handler = new FakeHttpMessageHandler();
        var options = new SecretClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        var client = new SecretClient(new Uri("https://fake-vault.vault.azure.net/"), new FakeTokenCredential(), options);
        return (new KeyVaultSecretStore(client), handler);
    }

    [Fact]
    public async Task SetSecretAsync_PutsToTheSecretsEndpoint_UsingTheKeyWithDotsReplacedByDashes()
    {
        var (store, handler) = CreateStore();
        handler.ResponseBody = """{"value":"x","id":"https://fake-vault.vault.azure.net/secrets/HaloPsa-ClientSecret/v1"}""";

        await store.SetSecretAsync("HaloPsa.ClientSecret", "super-secret-value");

        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("/secrets/HaloPsa-ClientSecret"));
    }

    [Fact]
    public async Task GetSecretAsync_ReturnsNull_WhenTheSecretDoesNotExist()
    {
        var (store, handler) = CreateStore();
        handler.StatusCode = HttpStatusCode.NotFound;
        handler.ResponseBody = """{"error":{"code":"SecretNotFound","message":"not found"}}""";

        Assert.Null(await store.GetSecretAsync("HaloPsa.ClientSecret"));
    }
}
```

- [ ] **Step 19: Run the tests to verify they pass**

Run: `dotnet test dotMARC.sln --filter KeyVaultSecretStoreTests`
Expected: PASS (both tests).

- [ ] **Step 20: Update `HaloPsaSettingsServiceTests.cs` for the new interface/methods**

In `test/DotMarc.Tests/Notifications/HaloPsaSettingsServiceTests.cs`:

Change:

```csharp
private DatabaseHaloSecretStore CreateSecretStore() =>
    new(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.HaloPsaSettingsService"));
```

to:

```csharp
private DatabaseSecretStore CreateSecretStore() =>
    new(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.HaloPsaSettingsService"));
```

Then replace every `secretStore.GetClientSecretAsync()` call in this file with `secretStore.GetSecretAsync(HaloPsaSettings.SecretStoreKey)` (three occurrences: one each in `SaveAsync_UpdatesNonSecretFields_AndLeavesSecretUnconfigured_WhenNoneProvided`, `SaveAsync_StoresTheSecretAndMarksItConfigured_WhenProvided`, `SaveAsync_LeavesAnExistingSecretInPlace_WhenNotReplaced`). No other changes — the three tests' structure, assertions, and fresh-context read-back pattern are otherwise unchanged.

- [ ] **Step 21: Update `HaloPsaClientTests.cs`'s fake secret store**

In `test/DotMarc.Tests/Notifications/HaloPsaClientTests.cs`, change:

```csharp
private sealed class FixedHaloSecretStore(string secret) : IHaloSecretStore
{
    public Task SetClientSecretAsync(string clientSecret, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<string?> GetClientSecretAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(secret);
}
```

to:

```csharp
private sealed class FixedSecretStore(string secret) : ISecretStore
{
    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(secret);
}
```

Then update the two constructor call sites (`new FixedHaloSecretStore("the-secret")` → `new FixedSecretStore("the-secret")`) — one in `CreateClient()`'s helper, one in the token-caching test that constructs a client directly. No other changes to this file.

- [ ] **Step 22: Run the full test suite**

Run: `dotnet test dotMARC.sln`
Expected: PASS, no regressions (should be the same total count as before this task, since Steps 16/18 delete-and-recreate 1-for-1 plus add three new `DatabaseSecretStoreTests` cases).

- [ ] **Step 23: Build check**

Run: `dotnet build dotMARC.sln`
Expected: 0 warnings, 0 errors — confirms no other file in the solution still references `IHaloSecretStore`/`DatabaseHaloSecretStore`/`KeyVaultHaloSecretStore` (a `grep -rn IHaloSecretStore src/ test/` returning nothing is a good independent check before moving on).

- [ ] **Step 24: Commit**

```bash
git add src/DotMarc/Notifications/ISecretStore.cs src/DotMarc/Notifications/EncryptedSecret.cs src/DotMarc/Notifications/DatabaseSecretStore.cs src/DotMarc/Notifications/KeyVaultSecretStore.cs src/DotMarc/Notifications/IHaloSecretStore.cs src/DotMarc/Notifications/DatabaseHaloSecretStore.cs src/DotMarc/Notifications/KeyVaultHaloSecretStore.cs src/DotMarc/Notifications/HaloPsaSettings.cs src/DotMarc/Notifications/HaloPsaSettingsService.cs src/DotMarc/Notifications/HaloPsaClient.cs src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Components/Pages/AlertsSettings.razor src/DotMarc/Program.cs infra/main.bicep infra/main.parameters.json src/DotMarc/Migrations/ test/DotMarc.Tests/Notifications/
git commit -m "Generalize the HaloPSA secret store into a shared, keyed ISecretStore"
```

---

## Task 2: `CloudflareDnsSettings`/`AzureDnsSettings` entities and services

**Files:**
- Create: `src/DotMarc/Notifications/CloudflareDnsSettings.cs`
- Create: `src/DotMarc/Notifications/AzureDnsSettings.cs`
- Create: `src/DotMarc/Notifications/CloudflareDnsSettingsService.cs`
- Create: `src/DotMarc/Notifications/AzureDnsSettingsService.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Create (generated): `src/DotMarc/Migrations/<timestamp>_AddDnsPushSettings.cs` and `.Designer.cs`
- Create: `test/DotMarc.Tests/Notifications/CloudflareDnsSettingsServiceTests.cs`
- Create: `test/DotMarc.Tests/Notifications/AzureDnsSettingsServiceTests.cs`

**Interfaces:**
- Consumes: `ISecretStore` (Task 1).
- Produces: `CloudflareDnsSettings`/`AzureDnsSettings` entities with `SecretStoreKey` constants, `CloudflareDnsSettingsService`/`AzureDnsSettingsService` (`GetAsync`/`SaveAsync`, same shape as `HaloPsaSettingsService`). Consumed by Task 3 (`CloudflareDnsPushProvider`/`AzureDnsPushProvider`) and Task 4 (the new settings page).

- [ ] **Step 1: Define `CloudflareDnsSettings`**

```csharp
// src/DotMarc/Notifications/CloudflareDnsSettings.cs
namespace DotMarc.Notifications;

/// <summary>Singleton settings row for Cloudflare DNS push, same pattern as HaloPsaSettings — the
/// client secret lives in ISecretStore under SecretStoreKey, never on this entity.</summary>
public sealed class CloudflareDnsSettings
{
    public const string SecretStoreKey = "CloudflareDns.ClientSecret";

    public int Id { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
}
```

- [ ] **Step 2: Define `AzureDnsSettings`**

```csharp
// src/DotMarc/Notifications/AzureDnsSettings.cs
namespace DotMarc.Notifications;

/// <summary>Singleton settings row for Azure DNS push, same pattern as HaloPsaSettings — the
/// client secret lives in ISecretStore under SecretStoreKey, never on this entity.</summary>
public sealed class AzureDnsSettings
{
    public const string SecretStoreKey = "AzureDns.ClientSecret";

    public int Id { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
}
```

- [ ] **Step 3: Register both entities in `DotMarcDbContext`**

Add to the `DbSet` list, after `EncryptedSecret`:

```csharp
public DbSet<CloudflareDnsSettings> CloudflareDnsSettings => Set<CloudflareDnsSettings>();
public DbSet<AzureDnsSettings> AzureDnsSettings => Set<AzureDnsSettings>();
```

Add to `OnModelCreating`, after the `HaloPsaSettings` seed:

```csharp
modelBuilder.Entity<CloudflareDnsSettings>().HasData(new CloudflareDnsSettings { Id = 1 });
modelBuilder.Entity<AzureDnsSettings>().HasData(new AzureDnsSettings { Id = 1 });
```

- [ ] **Step 4: Write the failing tests for `CloudflareDnsSettingsService`**

```csharp
// test/DotMarc.Tests/Notifications/CloudflareDnsSettingsServiceTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class CloudflareDnsSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public CloudflareDnsSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DatabaseSecretStore CreateSecretStore() =>
        new(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.CloudflareDnsSettingsService"));

    [Fact]
    public async Task SaveAsync_UpdatesClientId_AndLeavesSecretUnconfigured_WhenNoneProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await CloudflareDnsSettingsService.SaveAsync(context, secretStore, new CloudflareDnsSettings { ClientId = "client-id" }, newClientSecret: null);

        await using var verify = CreateContext();
        var saved = await CloudflareDnsSettingsService.GetAsync(verify);
        Assert.Equal("client-id", saved.ClientId);
        Assert.False(saved.ClientSecretConfigured);
        Assert.Null(await secretStore.GetSecretAsync(CloudflareDnsSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_StoresTheSecretAndMarksItConfigured_WhenProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await CloudflareDnsSettingsService.SaveAsync(context, secretStore, new CloudflareDnsSettings { ClientId = "client-id" }, newClientSecret: "the-real-secret");

        await using var verify = CreateContext();
        var saved = await CloudflareDnsSettingsService.GetAsync(verify);
        Assert.True(saved.ClientSecretConfigured);
        Assert.Equal("the-real-secret", await secretStore.GetSecretAsync(CloudflareDnsSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_LeavesAnExistingSecretInPlace_WhenNotReplaced()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();
        await CloudflareDnsSettingsService.SaveAsync(context, secretStore, new CloudflareDnsSettings { ClientId = "client-id" }, newClientSecret: "first-secret");

        await using var secondContext = CreateContext();
        await CloudflareDnsSettingsService.SaveAsync(secondContext, secretStore, new CloudflareDnsSettings { ClientId = "changed" }, newClientSecret: null);

        Assert.Equal("first-secret", await secretStore.GetSecretAsync(CloudflareDnsSettings.SecretStoreKey));

        await using var verify = CreateContext();
        var verified = await CloudflareDnsSettingsService.GetAsync(verify);
        Assert.True(verified.ClientSecretConfigured);
        Assert.Equal("changed", verified.ClientId);
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test dotMARC.sln --filter CloudflareDnsSettingsServiceTests`
Expected: FAIL — `CloudflareDnsSettingsService` doesn't exist yet.

- [ ] **Step 6: Implement `CloudflareDnsSettingsService`**

```csharp
// src/DotMarc/Notifications/CloudflareDnsSettingsService.cs
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class CloudflareDnsSettingsService
{
    public static Task<CloudflareDnsSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.CloudflareDnsSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, ISecretStore secretStore, CloudflareDnsSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        var existing = await context.CloudflareDnsSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.ClientId = updated.ClientId;

        if (!string.IsNullOrWhiteSpace(newClientSecret))
        {
            await secretStore.SetSecretAsync(CloudflareDnsSettings.SecretStoreKey, newClientSecret, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test dotMARC.sln --filter CloudflareDnsSettingsServiceTests`
Expected: PASS (all three tests).

- [ ] **Step 8: Write the failing tests for `AzureDnsSettingsService`**

```csharp
// test/DotMarc.Tests/Notifications/AzureDnsSettingsServiceTests.cs
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class AzureDnsSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AzureDnsSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DatabaseSecretStore CreateSecretStore() =>
        new(new FakeDbContextFactory(_connectionString), DataProtectionProvider.Create("DotMarc.Tests.AzureDnsSettingsService"));

    [Fact]
    public async Task SaveAsync_UpdatesTenantIdAndClientId_AndLeavesSecretUnconfigured_WhenNoneProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await AzureDnsSettingsService.SaveAsync(context, secretStore, new AzureDnsSettings { TenantId = "tenant-id", ClientId = "client-id" }, newClientSecret: null);

        await using var verify = CreateContext();
        var saved = await AzureDnsSettingsService.GetAsync(verify);
        Assert.Equal("tenant-id", saved.TenantId);
        Assert.Equal("client-id", saved.ClientId);
        Assert.False(saved.ClientSecretConfigured);
        Assert.Null(await secretStore.GetSecretAsync(AzureDnsSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_StoresTheSecretAndMarksItConfigured_WhenProvided()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();

        await AzureDnsSettingsService.SaveAsync(context, secretStore, new AzureDnsSettings { TenantId = "tenant-id", ClientId = "client-id" }, newClientSecret: "the-real-secret");

        await using var verify = CreateContext();
        var saved = await AzureDnsSettingsService.GetAsync(verify);
        Assert.True(saved.ClientSecretConfigured);
        Assert.Equal("the-real-secret", await secretStore.GetSecretAsync(AzureDnsSettings.SecretStoreKey));
    }

    [Fact]
    public async Task SaveAsync_LeavesAnExistingSecretInPlace_WhenNotReplaced()
    {
        await using var context = CreateContext();
        var secretStore = CreateSecretStore();
        await AzureDnsSettingsService.SaveAsync(context, secretStore, new AzureDnsSettings { TenantId = "tenant-id", ClientId = "client-id" }, newClientSecret: "first-secret");

        await using var secondContext = CreateContext();
        await AzureDnsSettingsService.SaveAsync(secondContext, secretStore, new AzureDnsSettings { TenantId = "tenant-id", ClientId = "changed" }, newClientSecret: null);

        Assert.Equal("first-secret", await secretStore.GetSecretAsync(AzureDnsSettings.SecretStoreKey));

        await using var verify = CreateContext();
        var verified = await AzureDnsSettingsService.GetAsync(verify);
        Assert.True(verified.ClientSecretConfigured);
        Assert.Equal("changed", verified.ClientId);
    }
}
```

- [ ] **Step 9: Run the tests to verify they fail**

Run: `dotnet test dotMARC.sln --filter AzureDnsSettingsServiceTests`
Expected: FAIL — `AzureDnsSettingsService` doesn't exist yet.

- [ ] **Step 10: Implement `AzureDnsSettingsService`**

```csharp
// src/DotMarc/Notifications/AzureDnsSettingsService.cs
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class AzureDnsSettingsService
{
    public static Task<AzureDnsSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.AzureDnsSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, ISecretStore secretStore, AzureDnsSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        var existing = await context.AzureDnsSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.TenantId = updated.TenantId;
        existing.ClientId = updated.ClientId;

        if (!string.IsNullOrWhiteSpace(newClientSecret))
        {
            await secretStore.SetSecretAsync(AzureDnsSettings.SecretStoreKey, newClientSecret, cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 11: Run the tests to verify they pass**

Run: `dotnet test dotMARC.sln --filter AzureDnsSettingsServiceTests`
Expected: PASS (all three tests).

- [ ] **Step 12: Generate and apply the migration**

Run: `dotnet ef migrations add AddDnsPushSettings --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

Confirm the generated migration creates the `CloudflareDnsSettings` and `AzureDnsSettings` tables (with their seed rows via `InsertData`). Apply it:

```powershell
dotnet ef database update --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj
```

- [ ] **Step 13: Commit**

```bash
git add src/DotMarc/Notifications/CloudflareDnsSettings.cs src/DotMarc/Notifications/AzureDnsSettings.cs src/DotMarc/Notifications/CloudflareDnsSettingsService.cs src/DotMarc/Notifications/AzureDnsSettingsService.cs src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Migrations/ test/DotMarc.Tests/Notifications/CloudflareDnsSettingsServiceTests.cs test/DotMarc.Tests/Notifications/AzureDnsSettingsServiceTests.cs
git commit -m "Add CloudflareDnsSettings/AzureDnsSettings entities and services"
```

---

## Task 3: Async `IDnsPushProvider` and DB-backed providers

**Files:**
- Modify: `src/DotMarc/DnsPush/IDnsPushProvider.cs`
- Create: `src/DotMarc/DnsPush/DnsPushProviderLookup.cs`
- Modify: `src/DotMarc/DnsPush/CloudflareDnsPushProvider.cs`
- Modify: `src/DotMarc/DnsPush/AzureDnsPushProvider.cs`
- Delete: `src/DotMarc/DnsPush/CloudflareDnsOptions.cs`
- Delete: `src/DotMarc/DnsPush/AzureDnsOptions.cs`
- Modify: `src/DotMarc/Program.cs`
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor`
- Modify: `src/DotMarc/Components/Pages/ManageMtaSts.razor`

**Interfaces:**
- Consumes: `CloudflareDnsSettings`/`AzureDnsSettings`/their services (Task 2), `ISecretStore` (Task 1).
- Produces: `IDnsPushProvider` with `IsConfiguredAsync`/`BuildAuthorizationUrlAsync` (both now `Task`-returning), and `DnsPushProviderLookup.FindConfiguredAsync` (a shared helper replacing five near-identical inline lookups). Consumed by `Program.cs`'s two `/dns-push/{provider}/*` endpoints, `DomainDetail.razor`, `ManageMtaSts.razor`.

> **Why `IsConfigured` and `BuildAuthorizationUrl` can safely become async:** confirmed during design by grepping every call site before committing to this change. `IsConfigured` is never read from Razor markup directly — the "Push via your DNS provider" button always renders under the `DomainsEdit`/equivalent policy; the configured-provider check happens only inside an `async` click handler, falling back to a Snackbar warning if none match. All five call sites already live inside `async` methods.

- [ ] **Step 1: Make `IDnsPushProvider` async**

```csharp
// src/DotMarc/DnsPush/IDnsPushProvider.cs
namespace DotMarc.DnsPush;

public enum DnsPushOutcome { Pushed, ZoneNotFound, ProviderError }

public sealed record DnsPushResult(DnsPushOutcome Outcome, string? DetailMessage);

/// <summary>One implementation per supported DNS provider. The provider's own OAuth client
/// credentials are DB-backed (CloudflareDnsSettings/AzureDnsSettings), read fresh per call rather
/// than cached — IsConfiguredAsync/BuildAuthorizationUrlAsync need a DB round trip, which is why
/// both are async even though they're conceptually simple lookups. Everything about the end-user's
/// own OAuth exchange stays exactly as stateless as before: nothing about the access token is ever
/// persisted; it exists only as a local variable for the duration of ExchangeAndPushAsync.</summary>
public interface IDnsPushProvider
{
    /// <summary>Matches DetectedDnsProvider and the {provider} route segment in
    /// /dns-push/{provider}/start|callback — "cloudflare" or "azure-dns".</summary>
    string ProviderKey { get; }

    /// <summary>False when this provider's OAuth app isn't configured for this deployment — the
    /// push button never renders in that case.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default);

    Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, DnsRecordChange change, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Add the shared `FindConfiguredAsync` lookup helper**

Every one of the five call sites this task updates does the exact same "find the provider matching this key that's actually configured" lookup — extracting it once avoids five near-identical `foreach` blocks.

```csharp
// src/DotMarc/DnsPush/DnsPushProviderLookup.cs
namespace DotMarc.DnsPush;

public static class DnsPushProviderLookup
{
    /// <summary>Returns the provider matching providerKey if (and only if) it's actually
    /// configured for this deployment — null for an unknown key, a null key, or a matching but
    /// unconfigured provider (its push button/redirect never renders in that case).</summary>
    public static async Task<IDnsPushProvider?> FindConfiguredAsync(this IEnumerable<IDnsPushProvider> providers, string? providerKey, CancellationToken cancellationToken = default)
    {
        if (providerKey is null)
        {
            return null;
        }

        foreach (var candidate in providers)
        {
            if (candidate.ProviderKey == providerKey && await candidate.IsConfiguredAsync(cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }
}
```

- [ ] **Step 3: Rework `CloudflareDnsPushProvider`**

```csharp
// src/DotMarc/DnsPush/CloudflareDnsPushProvider.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DnsPush;

/// <summary>Pushes a DNS record change to Cloudflare, authenticated via a fresh OAuth 2.0
/// Authorization Code + PKCE exchange each time — see the design spec's "Auth model" section for
/// why nothing about the end-user's push-time token is ever persisted. The app's own OAuth client
/// credentials (registered once per deployment with Cloudflare) are DB-backed
/// (CloudflareDnsSettings/ISecretStore), read fresh per call since they're admin-editable at
/// runtime. Endpoints confirmed against Cloudflare's own OIDC discovery document
/// (https://dash.cloudflare.com/.well-known/openid-configuration); the DNS API itself is
/// documented at https://developers.cloudflare.com/api/resources/dns/subresources/records/.</summary>
public sealed class CloudflareDnsPushProvider : IDnsPushProvider
{
    private const string AuthorizationEndpoint = "https://dash.cloudflare.com/oauth2/auth";
    private const string TokenEndpoint = "https://dash.cloudflare.com/oauth2/token";
    private const string ApiBase = "https://api.cloudflare.com/client/v4";

    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly ISecretStore _secretStore;
    private readonly HttpClient _http;

    public CloudflareDnsPushProvider(IDbContextFactory<DotMarcDbContext> dbFactory, ISecretStore secretStore, HttpClient http)
    {
        _dbFactory = dbFactory;
        _secretStore = secretStore;
        _http = http;
    }

    public string ProviderKey => "cloudflare";

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrEmpty(settings.ClientId) && settings.ClientSecretConfigured;
    }

    public async Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = settings.ClientId!,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "com.cloudflare.api.account.zone.dns",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        return AuthorizationEndpoint + "?" + string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var clientSecret = await _secretStore.GetSecretAsync(CloudflareDnsSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(settings.ClientId) || string.IsNullOrEmpty(clientSecret))
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Cloudflare push is not configured for this deployment.");
        }

        var accessToken = await ExchangeCodeForTokenAsync(settings.ClientId, clientSecret, code, codeVerifier, redirectUri, cancellationToken).ConfigureAwait(false);
        if (accessToken is null)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Cloudflare rejected the authorization code exchange.");
        }

        var zoneName = ZoneNameFor(change.Name);
        var (zoneId, zoneErrorStatus) = await FindZoneIdAsync(zoneName, accessToken, cancellationToken).ConfigureAwait(false);
        if (zoneErrorStatus.HasValue)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the zone lookup ({zoneErrorStatus}).");
        }
        if (zoneId is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound, $"Couldn't find {zoneName} in the Cloudflare account you authorized.");
        }

        return change.Kind == DnsRecordChangeKind.Merge
            ? await UpdateExistingRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false)
            : await CreateRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CloudflareDnsSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await CloudflareDnsSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ExchangeCodeForTokenAsync(string clientId, string clientSecret, string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code_verifier"] = codeVerifier
            })
        };
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return token?.AccessToken;
    }

    private async Task<(string? Id, int? ErrorStatusCode)> FindZoneIdAsync(string zoneName, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/zones?name={Uri.EscapeDataString(zoneName)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (null, (int)response.StatusCode);
        }
        var zones = await response.Content.ReadFromJsonAsync<ApiResponse<List<IdRecord>>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return (zones?.Result?.FirstOrDefault()?.Id, null);
    }

    private async Task<DnsPushResult> CreateRecordAsync(string zoneId, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/zones/{zoneId}/dns_records")
        {
            Content = JsonContent.Create(new DnsRecordPayload(change.RecordType, change.Name, change.DesiredValue))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? new DnsPushResult(DnsPushOutcome.Pushed, null)
            : new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the record push ({(int)response.StatusCode}).");
    }

    private async Task<DnsPushResult> UpdateExistingRecordAsync(string zoneId, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        using var findRequest = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/zones/{zoneId}/dns_records?type={change.RecordType}&name={Uri.EscapeDataString(change.Name)}");
        findRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var findResponse = await _http.SendAsync(findRequest, cancellationToken).ConfigureAwait(false);
        if (!findResponse.IsSuccessStatusCode)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the record lookup ({(int)findResponse.StatusCode}).");
        }
        var existing = await findResponse.Content.ReadFromJsonAsync<ApiResponse<List<IdRecord>>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        var recordId = existing?.Result?.FirstOrDefault()?.Id;
        if (recordId is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound, $"{change.Name} no longer exists at Cloudflare — it may have been removed since this page loaded.");
        }

        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/zones/{zoneId}/dns_records/{recordId}")
        {
            Content = JsonContent.Create(new DnsRecordPayload(change.RecordType, change.Name, change.DesiredValue))
        };
        updateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var updateResponse = await _http.SendAsync(updateRequest, cancellationToken).ConfigureAwait(false);
        return updateResponse.IsSuccessStatusCode
            ? new DnsPushResult(DnsPushOutcome.Pushed, null)
            : new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the record update ({(int)updateResponse.StatusCode}).");
    }

    /// <summary>dotMARC only ever calls this with a name of the form "mta-sts.&lt;domain&gt;" or
    /// "_dmarc.&lt;domain&gt;", so stripping the first label always yields the zone name — this
    /// would not generalize to arbitrary multi-label zones, and doesn't need to.</summary>
    private static string ZoneNameFor(string recordName)
    {
        var firstDot = recordName.IndexOf('.');
        return firstDot < 0 ? recordName : recordName[(firstDot + 1)..];
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
    private sealed record ApiResponse<T>([property: JsonPropertyName("result")] T? Result);
    private sealed record IdRecord([property: JsonPropertyName("id")] string Id);
    private sealed record DnsRecordPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("content")] string Content);
}
```

(Only the top of the file changed from what's there today: the constructor's dependencies, `IsConfigured`/`BuildAuthorizationUrl` becoming async and DB-backed, and `ExchangeAndPushAsync`'s first few lines fetching settings/secret instead of reading `_options`. Every private HTTP-calling method below `GetSettingsAsync` is untouched.)

- [ ] **Step 4: Rework `AzureDnsPushProvider`**

```csharp
// src/DotMarc/DnsPush/AzureDnsPushProvider.cs
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;

namespace DotMarc.DnsPush;

/// <summary>Pushes a DNS record change to Azure DNS via a delegated Entra ID authorization-code
/// exchange — the push only succeeds if the SIGNED-IN USER's own Azure RBAC grants them write
/// access on the target zone; dotMARC never holds a standing grant of its own. Same "nothing about
/// the end-user's push-time token is ever persisted" contract as CloudflareDnsPushProvider. The
/// app's own OAuth client credentials are DB-backed (AzureDnsSettings/ISecretStore), read fresh
/// per call.</summary>
public sealed class AzureDnsPushProvider : IDnsPushProvider
{
    private const string Scope = "https://management.azure.com/user_impersonation";

    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly ISecretStore _secretStore;

    public AzureDnsPushProvider(IDbContextFactory<DotMarcDbContext> dbFactory, ISecretStore secretStore)
    {
        _dbFactory = dbFactory;
        _secretStore = secretStore;
    }

    public string ProviderKey => "azure-dns";

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrEmpty(settings.TenantId) && !string.IsNullOrEmpty(settings.ClientId) && settings.ClientSecretConfigured;
    }

    public async Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId!,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = $"{Scope} openid",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        return $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/authorize?" +
            string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<DnsPushResult> ExchangeAndPushAsync(
        string code, string codeVerifier, string redirectUri, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var clientSecret = await _secretStore.GetSecretAsync(AzureDnsSettings.SecretStoreKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(settings.TenantId) || string.IsNullOrEmpty(settings.ClientId) || string.IsNullOrEmpty(clientSecret))
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, "Azure DNS push is not configured for this deployment.");
        }

        var confidentialClient = ConfidentialClientApplicationBuilder.Create(settings.ClientId)
            .WithClientSecret(clientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{settings.TenantId}")
            .WithRedirectUri(redirectUri)
            .Build();

        AuthenticationResult authResult;
        try
        {
            authResult = await confidentialClient
                .AcquireTokenByAuthorizationCode([Scope], code)
                .WithPkceCodeVerifier(codeVerifier)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MsalException ex)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Azure rejected the authorization code exchange: {ex.Message}");
        }

        var armClient = new ArmClient(new FixedTokenCredential(authResult.AccessToken, authResult.ExpiresOn));

        var zoneName = ZoneNameFor(change.Name);
        var zone = await FindZoneAsync(armClient, zoneName, cancellationToken).ConfigureAwait(false);
        if (zone is null)
        {
            return new DnsPushResult(DnsPushOutcome.ZoneNotFound,
                $"Couldn't find {zoneName} in any subscription you authorized — check you have DNS Zone Contributor rights on it.");
        }

        return await PushRecordAsync(zone, zoneName, change, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AzureDnsSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await AzureDnsSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DnsZoneResource?> FindZoneAsync(ArmClient armClient, string zoneName, CancellationToken cancellationToken)
    {
        await foreach (var subscription in armClient.GetSubscriptions().GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await foreach (var candidate in subscription.GetDnsZonesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(candidate.Data.Name, zoneName, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    private static async Task<DnsPushResult> PushRecordAsync(DnsZoneResource zone, string zoneName, DnsRecordChange change, CancellationToken cancellationToken)
    {
        // "mta-sts.contoso.co.uk" under zone "contoso.co.uk" -> relative record name "mta-sts".
        var relativeName = change.Name[..^(zoneName.Length + 1)];

        try
        {
            if (string.Equals(change.RecordType, "CNAME", StringComparison.OrdinalIgnoreCase))
            {
                var cnameRecords = zone.GetDnsCnameRecords();
                if (change.Kind == DnsRecordChangeKind.Create
                    && (await cnameRecords.ExistsAsync(relativeName, cancellationToken).ConfigureAwait(false)).Value)
                {
                    return new DnsPushResult(DnsPushOutcome.ProviderError,
                        $"A DNS record already exists at {change.Name} — remove it or update it manually rather than risk overwriting it.");
                }

                var data = new DnsCnameRecordData { TtlInSeconds = 3600, Cname = change.DesiredValue };
                await cnameRecords.CreateOrUpdateAsync(WaitUntil.Completed, relativeName, data, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var txtRecords = zone.GetDnsTxtRecords();
                if (change.Kind == DnsRecordChangeKind.Create
                    && (await txtRecords.ExistsAsync(relativeName, cancellationToken).ConfigureAwait(false)).Value)
                {
                    return new DnsPushResult(DnsPushOutcome.ProviderError,
                        $"A DNS record already exists at {change.Name} — remove it or update it manually rather than risk overwriting it.");
                }

                var data = new DnsTxtRecordData { TtlInSeconds = 3600 };
                data.DnsTxtRecords.Add(new DnsTxtRecordInfo { Values = { change.DesiredValue } });
                await txtRecords.CreateOrUpdateAsync(WaitUntil.Completed, relativeName, data, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RequestFailedException ex)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Azure rejected the record push: {ex.Message}");
        }

        return new DnsPushResult(DnsPushOutcome.Pushed, null);
    }

    private static string ZoneNameFor(string recordName)
    {
        var firstDot = recordName.IndexOf('.');
        return firstDot < 0 ? recordName : recordName[(firstDot + 1)..];
    }

    /// <summary>Wraps an access token already obtained via the delegated authorization-code
    /// exchange above — ArmClient needs a TokenCredential, but there is nothing for it to actually
    /// fetch here; it already has the one token this whole operation is scoped to.</summary>
    private sealed class FixedTokenCredential : TokenCredential
    {
        private readonly string _accessToken;
        private readonly DateTimeOffset _expiresOn;

        public FixedTokenCredential(string accessToken, DateTimeOffset expiresOn)
        {
            _accessToken = accessToken;
            _expiresOn = expiresOn;
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(_accessToken, _expiresOn);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(new AccessToken(_accessToken, _expiresOn));
    }
}
```

- [ ] **Step 5: Delete `CloudflareDnsOptions.cs`/`AzureDnsOptions.cs`**

```bash
rm src/DotMarc/DnsPush/CloudflareDnsOptions.cs src/DotMarc/DnsPush/AzureDnsOptions.cs
```

- [ ] **Step 6: Update `Program.cs`'s DI registration**

Replace:

```csharp
builder.Services.Configure<DotMarc.DnsPush.CloudflareDnsOptions>(builder.Configuration.GetSection(DotMarc.DnsPush.CloudflareDnsOptions.SectionName));
builder.Services.AddHttpClient<DotMarc.DnsPush.CloudflareDnsPushProvider>();
builder.Services.AddSingleton<DotMarc.DnsPush.IDnsPushProvider>(sp => sp.GetRequiredService<DotMarc.DnsPush.CloudflareDnsPushProvider>());

builder.Services.Configure<DotMarc.DnsPush.AzureDnsOptions>(builder.Configuration.GetSection(DotMarc.DnsPush.AzureDnsOptions.SectionName));
builder.Services.AddSingleton<DotMarc.DnsPush.AzureDnsPushProvider>();
builder.Services.AddSingleton<DotMarc.DnsPush.IDnsPushProvider>(sp => sp.GetRequiredService<DotMarc.DnsPush.AzureDnsPushProvider>());
```

with:

```csharp
builder.Services.AddHttpClient<DotMarc.DnsPush.CloudflareDnsPushProvider>();
builder.Services.AddSingleton<DotMarc.DnsPush.IDnsPushProvider>(sp => sp.GetRequiredService<DotMarc.DnsPush.CloudflareDnsPushProvider>());

builder.Services.AddSingleton<DotMarc.DnsPush.AzureDnsPushProvider>();
builder.Services.AddSingleton<DotMarc.DnsPush.IDnsPushProvider>(sp => sp.GetRequiredService<DotMarc.DnsPush.AzureDnsPushProvider>());
```

Both providers' constructors now take `IDbContextFactory<DotMarcDbContext>` and `ISecretStore` (both already registered elsewhere in this file) instead of `IOptions<T>` — no new registrations needed for those, DI resolves the new constructor parameters automatically since both dependencies are already in the container. `CloudflareDnsPushProvider` still additionally needs `HttpClient`, which `AddHttpClient<T>()` continues to supply.

- [ ] **Step 7: Update `Program.cs`'s two `/dns-push/{provider}/*` endpoints**

In `/dns-push/{provider}/start`, replace:

```csharp
var pushProvider = pushProviders.SingleOrDefault(p => p.ProviderKey == provider && p.IsConfigured);
if (pushProvider is null)
{
    return Results.NotFound();
}

var (codeVerifier, codeChallenge) = PkceGenerator.Generate();
var state = stateProtector.Protect(domainId, target, codeVerifier, DateTimeOffset.UtcNow);
var redirectUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/dns-push/{provider}/callback";

return Results.Redirect(pushProvider.BuildAuthorizationUrl(state, codeChallenge, redirectUri));
```

with:

```csharp
var pushProvider = await pushProviders.FindConfiguredAsync(provider);
if (pushProvider is null)
{
    return Results.NotFound();
}

var (codeVerifier, codeChallenge) = PkceGenerator.Generate();
var state = stateProtector.Protect(domainId, target, codeVerifier, DateTimeOffset.UtcNow);
var redirectUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/dns-push/{provider}/callback";

return Results.Redirect(await pushProvider.BuildAuthorizationUrlAsync(state, codeChallenge, redirectUri));
```

In `/dns-push/{provider}/callback`, replace:

```csharp
var pushProvider = pushProviders.SingleOrDefault(p => p.ProviderKey == provider && p.IsConfigured);
```

with:

```csharp
var pushProvider = await pushProviders.FindConfiguredAsync(provider);
```

(Nothing else in either endpoint changes — `ExchangeAndPushAsync`'s call and signature are untouched.)

- [ ] **Step 8: Update `DomainDetail.razor`'s two call sites**

In `PushDmarcRecordAsync`, replace:

```csharp
var pushProvider = providerKey is null ? null : DnsPushProviders.SingleOrDefault(p => p.ProviderKey == providerKey && p.IsConfigured);
```

with:

```csharp
var pushProvider = await DnsPushProviders.FindConfiguredAsync(providerKey);
```

In `PushTlsrptRecordAsync`, replace:

```csharp
var pushProvider = providerKey is null ? null : DnsPushProviders.SingleOrDefault(provider => provider.ProviderKey == providerKey && provider.IsConfigured);
```

with:

```csharp
var pushProvider = await DnsPushProviders.FindConfiguredAsync(providerKey);
```

`DomainDetail.razor` already has `@using DotMarc.DnsPush` (confirmed: line 11) — needed for the `FindConfiguredAsync` extension method to resolve, no `@using` addition required.

- [ ] **Step 9: Update `ManageMtaSts.razor`'s one call site**

In `PushCnameAsync`, replace:

```csharp
var pushProvider = providerKey is null ? null : DnsPushProviders.SingleOrDefault(p => p.ProviderKey == providerKey && p.IsConfigured);
```

with:

```csharp
var pushProvider = await DnsPushProviders.FindConfiguredAsync(providerKey);
```

`ManageMtaSts.razor` already has `@using DotMarc.DnsPush` too (confirmed: line 5) — no `@using` addition required here either.

- [ ] **Step 10: Build and run the full test suite**

Run: `dotnet build dotMARC.sln`
Expected: 0 warnings, 0 errors — confirms no remaining synchronous `.IsConfigured`/`.BuildAuthorizationUrl` call sites anywhere (a `grep -rn "\.IsConfigured\b\|\.BuildAuthorizationUrl\b" src/` returning only the new async member names is a good independent check).

Run: `dotnet test dotMARC.sln`
Expected: PASS, no regressions (this task adds no new automated tests — see Global Constraints — so the count should be unchanged from Task 2's end state).

- [ ] **Step 11: Manual smoke test**

Bring up the app locally (same approach as prior manual-verification tasks in this codebase: fetch a live page over HTTP with a session cookie if no browser is available in the environment) and confirm:
- `/domains` → a domain's detail page still loads and its "Push via your DNS provider" buttons still render (they're gated by `DomainsEdit`, not by provider configuration, so they should render exactly as before).
- `/mta-sts` still loads and its per-domain rows still render.
- With no Cloudflare/Azure DNS settings configured (the seeded default), clicking either push action still shows the existing "Couldn't find a configured DNS push option..." Snackbar warning rather than an exception or a blank page — this is the one behavior a broken async conversion would most likely break.

- [ ] **Step 12: Commit**

```bash
git add src/DotMarc/DnsPush/IDnsPushProvider.cs src/DotMarc/DnsPush/DnsPushProviderLookup.cs src/DotMarc/DnsPush/CloudflareDnsPushProvider.cs src/DotMarc/DnsPush/AzureDnsPushProvider.cs src/DotMarc/DnsPush/CloudflareDnsOptions.cs src/DotMarc/DnsPush/AzureDnsOptions.cs src/DotMarc/Program.cs src/DotMarc/Components/Pages/DomainDetail.razor src/DotMarc/Components/Pages/ManageMtaSts.razor
git commit -m "Make IDnsPushProvider read its OAuth client credentials from the DB instead of IOptions"
```

---

## Task 4: `DnsPushManage` permission and settings page

**Files:**
- Modify: `src/DotMarc/Data/Permission.cs`
- Create: `src/DotMarc/Components/Pages/DnsPushSettings.razor`
- Modify: `src/DotMarc/Components/Layout/MainLayout.razor`

**Interfaces:**
- Consumes: `CloudflareDnsSettingsService`/`AzureDnsSettingsService` (Task 2), `ISecretStore` (Task 1).
- Produces: `Permission.DnsPushManage`, the `/dns-push/settings` page.

No automated test for this task — Blazor component rendering has no test harness in this codebase (established gap, verified manually elsewhere, same as the HaloPSA integration's Alert settings/Manage Groups/Manage Domains UI tasks).

- [ ] **Step 1: Add the `DnsPushManage` permission**

```csharp
// src/DotMarc/Data/Permission.cs — add DnsPushManage after AlertsManage (the current last entry)
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
    AccessManage,
    MtaStsView,
    MtaStsManage,
    AlertsView,
    AlertsManage,
    DnsPushManage
}
```

No changes needed to `AccessBootstrapper.ViewerPermissions` — `DnsPushManage` has no corresponding `View` permission (a single Manage-only permission, matching `AccessManage`'s shape, since there's no separate read-only audience for OAuth client credentials), so it's never part of the default Viewer bundle, same as `MtaStsManage`/`AlertsManage` aren't either.

- [ ] **Step 2: Create the settings page**

```razor
@* src/DotMarc/Components/Pages/DnsPushSettings.razor *@
@page "/dns-push/settings"
@attribute [Authorize(Policy = "DnsPushManage")]
@using DotMarc.Data
@using DotMarc.Notifications
@using Microsoft.AspNetCore.Authorization
@using Microsoft.EntityFrameworkCore
@inject IDbContextFactory<DotMarcDbContext> DbFactory
@inject ISecretStore SecretStoreAccessor
@inject ISnackbar Snackbar

<PageTitle>dotMARC - DNS Push Settings</PageTitle>
<MudText Typo="Typo.h4" Class="mb-4">DNS push settings</MudText>

@if (_cloudflareSettings is not null)
{
    <MudPaper Class="pa-4 mb-4">
        <MudText Typo="Typo.h5" Class="mb-4">Cloudflare</MudText>
        <MudGrid>
            <MudItem xs="12" md="6">
                <MudTextField Label="Client ID" @bind-Value="_cloudflareSettings.ClientId" Variant="Variant.Outlined" />
            </MudItem>
            <MudItem xs="12" md="6">
                <MudTextField Label="Client secret" @bind-Value="_newCloudflareClientSecret" InputType="InputType.Password" Variant="Variant.Outlined"
                              HelperText="@(_cloudflareSettings.ClientSecretConfigured ? "A secret is already configured — leave blank to keep it." : "No secret configured yet.")" />
            </MudItem>
        </MudGrid>
        <MudButton Variant="Variant.Filled" Color="Color.Primary" Class="mt-4" OnClick="SaveCloudflareAsync">Save Cloudflare settings</MudButton>
    </MudPaper>
}

@if (_azureDnsSettings is not null)
{
    <MudPaper Class="pa-4">
        <MudText Typo="Typo.h5" Class="mb-4">Azure DNS</MudText>
        <MudGrid>
            <MudItem xs="12" md="4">
                <MudTextField Label="Tenant ID" @bind-Value="_azureDnsSettings.TenantId" Variant="Variant.Outlined" />
            </MudItem>
            <MudItem xs="12" md="4">
                <MudTextField Label="Client ID" @bind-Value="_azureDnsSettings.ClientId" Variant="Variant.Outlined" />
            </MudItem>
            <MudItem xs="12" md="4">
                <MudTextField Label="Client secret" @bind-Value="_newAzureDnsClientSecret" InputType="InputType.Password" Variant="Variant.Outlined"
                              HelperText="@(_azureDnsSettings.ClientSecretConfigured ? "A secret is already configured — leave blank to keep it." : "No secret configured yet.")" />
            </MudItem>
        </MudGrid>
        <MudButton Variant="Variant.Filled" Color="Color.Primary" Class="mt-4" OnClick="SaveAzureDnsAsync">Save Azure DNS settings</MudButton>
    </MudPaper>
}

@code {
    private CloudflareDnsSettings? _cloudflareSettings;
    private AzureDnsSettings? _azureDnsSettings;
    private string? _newCloudflareClientSecret;
    private string? _newAzureDnsClientSecret;

    protected override async Task OnInitializedAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _cloudflareSettings = await CloudflareDnsSettingsService.GetAsync(db);
        _azureDnsSettings = await AzureDnsSettingsService.GetAsync(db);
    }

    private async Task SaveCloudflareAsync()
    {
        if (_cloudflareSettings is null)
        {
            return;
        }

        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await CloudflareDnsSettingsService.SaveAsync(db, SecretStoreAccessor, _cloudflareSettings, string.IsNullOrWhiteSpace(_newCloudflareClientSecret) ? null : _newCloudflareClientSecret);
            _newCloudflareClientSecret = null;
            Snackbar.Add("Cloudflare settings saved.", Severity.Success);

            await using var reloadDb = await DbFactory.CreateDbContextAsync();
            _cloudflareSettings = await CloudflareDnsSettingsService.GetAsync(reloadDb);
        }
        catch (Exception)
        {
            Snackbar.Add("Failed to save Cloudflare settings. Try again.", Severity.Error);
        }
    }

    private async Task SaveAzureDnsAsync()
    {
        if (_azureDnsSettings is null)
        {
            return;
        }

        try
        {
            await using var db = await DbFactory.CreateDbContextAsync();
            await AzureDnsSettingsService.SaveAsync(db, SecretStoreAccessor, _azureDnsSettings, string.IsNullOrWhiteSpace(_newAzureDnsClientSecret) ? null : _newAzureDnsClientSecret);
            _newAzureDnsClientSecret = null;
            Snackbar.Add("Azure DNS settings saved.", Severity.Success);

            await using var reloadDb = await DbFactory.CreateDbContextAsync();
            _azureDnsSettings = await AzureDnsSettingsService.GetAsync(reloadDb);
        }
        catch (Exception)
        {
            Snackbar.Add("Failed to save Azure DNS settings. Try again.", Severity.Error);
        }
    }
}
```

- [ ] **Step 3: Add the nav link**

In `src/DotMarc/Components/Layout/MainLayout.razor`, add a new `AuthorizeView` block inside the existing "Manage" `MudMenu`, right after the `MtaStsManage` entry:

```razor
<AuthorizeView Policy="MtaStsManage">
    <MudMenuItem Href="/mta-sts" Icon="@Icons.Material.Filled.Security">MTA-STS</MudMenuItem>
</AuthorizeView>
<AuthorizeView Policy="DnsPushManage">
    <MudMenuItem Href="/dns-push/settings" Icon="@Icons.Material.Filled.CloudSync">DNS push settings</MudMenuItem>
</AuthorizeView>
<AuthorizeView Policy="AlertsManage">
    <MudMenuItem Href="/alerts/settings" Icon="@Icons.Material.Filled.NotificationsActive">Alert settings</MudMenuItem>
</AuthorizeView>
```

(Only the middle block is new — the `MtaStsManage` and `AlertsManage` blocks shown above are existing, unchanged context to locate the insertion point.)

- [ ] **Step 4: Build and manual smoke test**

Run: `dotnet build dotMARC.sln`
Expected: 0 warnings, 0 errors.

Bring up the app locally (demo mode or real auth per local setup), sign in as an Admin (which has every permission), navigate to `/dns-push/settings`, confirm both sections render, and that saving each (with and without a new client secret) persists without error and correctly shows "configured"/"not configured" state afterward. Confirm the "DNS push settings" nav item appears under **Manage**.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Data/Permission.cs src/DotMarc/Components/Pages/DnsPushSettings.razor src/DotMarc/Components/Layout/MainLayout.razor
git commit -m "Add DnsPushManage permission and the DNS push settings page"
```

---

## Task 5: Infra unwind

**Files:**
- Modify: `infra/main.bicep`
- Modify: `infra/main.parameters.json`
- Modify: `docker-compose.yml`

**Interfaces:** None — infrastructure only. (Task 1 already renamed `enableHaloPsaKeyVaultWrite`/`haloPsaKeyVaultWriteRole` to their generic names; this task removes the now-obsolete DNS-provider-push deploy-time config entirely.)

- [ ] **Step 1: Remove the DNS push params from `infra/main.bicep`**

Delete (note the real file escapes the apostrophes in these description strings as `\'` — match on that exact text, not a plain apostrophe):

```bicep
@description('Non-secret Cloudflare DNS push config (see getting-started.mdx#dns-provider-push-optional). Leave blank to leave this provider\'s push button off; the client secret is set into Key Vault after deployment like the other secrets below.')
param cloudflareDnsClientId string = ''

@description('Non-secret Azure DNS push config (see getting-started.mdx#dns-provider-push-optional). Leave blank to leave this provider\'s push button off; the client secret is set into Key Vault after deployment like the other secrets below.')
param azureDnsTenantId string = ''
param azureDnsClientId string = ''
```

- [ ] **Step 2: Remove the DNS push Key Vault secrets and container references**

Delete the two secret resources near the bottom of the file:

```bicep
// Also provisioned empty, but optional: DNS provider push works with either, both, or neither
// set — see deploy-to-azure.mdx's "Optional: DNS provider push secrets" section. Left unset,
// CloudflareDns__ClientSecret/AzureDns__ClientSecret resolve to an empty string, and that
// provider's push button simply never renders.
resource cloudflareDnsClientSecretRef 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'CloudflareDns-ClientSecret'
  properties: {
    value: ''
  }
}

resource azureDnsClientSecretRef 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'AzureDns-ClientSecret'
  properties: {
    value: ''
  }
}
```

Delete the two matching `secrets:` entries on the container app:

```bicep
{
  name: 'cloudflaredns-client-secret'
  keyVaultUrl: '${keyVault.properties.vaultUri}secrets/CloudflareDns-ClientSecret'
  identity: 'System'
}
{
  name: 'azuredns-client-secret'
  keyVaultUrl: '${keyVault.properties.vaultUri}secrets/AzureDns-ClientSecret'
  identity: 'System'
}
```

Delete the five DNS push `env:` entries:

```bicep
// Harmless to always set, same as MtaSts__HostingHostname above: both DNS push
// providers are independently optional, and each one's push button simply never
// renders while its config is blank (see CloudflareDnsOptions/AzureDnsOptions).
{ name: 'CloudflareDns__ClientId', value: cloudflareDnsClientId }
{ name: 'AzureDns__TenantId', value: azureDnsTenantId }
{ name: 'AzureDns__ClientId', value: azureDnsClientId }
```

```bicep
{ name: 'CloudflareDns__ClientSecret', secretRef: 'cloudflaredns-client-secret' }
{ name: 'AzureDns__ClientSecret', secretRef: 'azuredns-client-secret' }
```

- [ ] **Step 3: Validate the template compiles**

Run: `az bicep build --file infra/main.bicep --stdout`
Expected: no errors, no warnings about unused/undeclared symbols.

- [ ] **Step 4: Remove the DNS push params from `infra/main.parameters.json`**

Delete the three entries:

```json
"cloudflareDnsClientId": { "value": "" },
"azureDnsTenantId": { "value": "" },
"azureDnsClientId": { "value": "" },
```

(Confirm the remaining JSON is still valid — no trailing comma left behind on the preceding line.)

- [ ] **Step 5: Remove the DNS push env vars from `docker-compose.yml`**

Delete the five lines added earlier this session:

```yaml
CloudflareDns__ClientId: ${CLOUDFLARE_DNS_CLIENT_ID:-}
CloudflareDns__ClientSecret: ${CLOUDFLARE_DNS_CLIENT_SECRET:-}
AzureDns__TenantId: ${AZURE_DNS_TENANT_ID:-}
AzureDns__ClientId: ${AZURE_DNS_CLIENT_ID:-}
AzureDns__ClientSecret: ${AZURE_DNS_CLIENT_SECRET:-}
```

- [ ] **Step 6: Commit**

```bash
git add infra/main.bicep infra/main.parameters.json docker-compose.yml
git commit -m "Remove deploy-time DNS push config now that credentials are DB-backed"
```

---

## Task 6: Docs

**Files:**
- Modify: `website/docs/getting-started.mdx`
- Modify: `website/docs/deploy-to-azure.mdx`
- Modify: `website/docs/permissions-and-access.mdx`

**Interfaces:** None — documentation only.

- [ ] **Step 1: Rewrite the "DNS provider push (optional)" section in `getting-started.mdx`**

The OAuth app registration walkthroughs (creating the Cloudflare OAuth client, registering the third Azure DNS Entra app) are unchanged — replace only the "how you tell dotMARC about it" part. Replace the entire section (from `### DNS provider push (optional)` to the end of the file, i.e. everything from that heading through the closing "Next:" line) with:

```mdx
### DNS provider push (optional)

If a domain's DNS is hosted on Cloudflare or Azure DNS, dotMARC can push the MTA-STS CNAME or the
DMARC TXT record straight there instead of you copying it in by hand. Each push is authenticated
fresh through that provider's own consent screen, with nothing about that authentication stored.

**Cloudflare**: register a self-managed OAuth client (**Manage account** → **OAuth clients** in the
Cloudflare dashboard), scoped to `Zone.DNS` edit, with a redirect URI of
`https://<your-deployment-host>/dns-push/cloudflare/callback`.

**Azure DNS**: register a *third*, separate Entra app registration (do not reuse the mailbox or
dashboard app). Go to **App registrations** → **New registration**, then **Authentication** → add a
**Web** redirect URI of `https://<your-deployment-host>/dns-push/azure-dns/callback`, then **API
permissions** → add the delegated **Azure Service Management** → `user_impersonation` permission.

Both providers are independently optional and configured the same way regardless of how you're
running dotMARC (Docker Compose or Azure): sign in with an account holding the `DnsPushManage`
permission and go to **Manage → DNS push settings**, enter the OAuth client ID/secret (and, for
Azure DNS, tenant ID) for whichever provider(s) you registered, and save. Leave either unconfigured
and that provider's push button simply never appears, with no other effect on the app.

Next: [Local Development](./local-development.mdx) to run and test the app from source, or
[Deploy to Azure](./deploy-to-azure.mdx) to run it in production.
```

- [ ] **Step 2: Remove the DNS push subsection from `deploy-to-azure.mdx`, generalize the Key Vault one**

Delete the entire "### Optional: DNS provider push secrets" subsection (from that heading through the line before "### Optional: HaloPSA Key Vault storage").

Replace "### Optional: HaloPSA Key Vault storage" with:

```mdx
### Optional: Key Vault-backed secret storage

By default, runtime-editable secrets (the HaloPSA API client secret, Cloudflare/Azure DNS push
OAuth client secrets — anything configured through an in-app settings page rather than a deployment
parameter) are encrypted and stored in Postgres. To store them in this deployment's Key Vault
instead, redeploy with `enableKeyVaultWrite` set to `true` — this grants the container app's
managed identity a narrowly-scoped write role on the vault (see `infra/main.bicep`'s
`keyVaultWriteRole`, it adds only `secrets/setSecret`, read is already covered by the existing
`Key Vault Secrets User` assignment). No manual `az keyvault secret set` needed here, the app
writes each secret itself the first time you save it from the relevant settings page.
```

- [ ] **Step 3: Add `DnsPushManage` to `permissions-and-access.mdx`**

Change:

```mdx
Admins can also create custom roles covering any subset of the available permissions, domain
management, Group/Tag management, access management, MTA-STS hosting (`MtaStsView`,
`MtaStsManage`), and [alerting](./alerts.mdx) (`AlertsView`, `AlertsManage`) are each independently
grantable.
```

to:

```mdx
Admins can also create custom roles covering any subset of the available permissions, domain
management, Group/Tag management, access management, MTA-STS hosting (`MtaStsView`,
`MtaStsManage`), [alerting](./alerts.mdx) (`AlertsView`, `AlertsManage`), and DNS provider push
configuration (`DnsPushManage`) are each independently grantable.
```

- [ ] **Step 4: Build the docs site to verify links/anchors still resolve**

```powershell
cd website
npx docusaurus build --out-dir build-check
```

Expected: build succeeds (a broken internal link fails the build — in particular this confirms `getting-started.mdx`'s own `#dns-provider-push-optional` anchor, referenced from `psa-integration.mdx` and elsewhere, still exists, since the heading itself is unchanged even though its content is rewritten). Remove `build-check` afterward.

- [ ] **Step 5: Commit**

```bash
git add website/docs/getting-started.mdx website/docs/deploy-to-azure.mdx website/docs/permissions-and-access.mdx
git commit -m "Document DB-backed DNS push settings"
```

---

## Self-review notes

- **Spec coverage:** every section of the spec (`2026-09-04-dns-push-secret-storage-design.md`) maps to a task — the generalized secret store → Task 1; settings entities/services → Task 2; provider interface changes → Task 3; the new settings page and permission → Task 4; the infra unwind → Task 5; docs → Task 6.
- **Existing-code touch points confirmed exhaustively**, not assumed: every current reference to `IHaloSecretStore` (`Program.cs`, `AlertsSettings.razor`, `HaloPsaClient.cs`, `HaloPsaSettingsService.cs`, `KeyVaultHaloSecretStore.cs`, `DatabaseHaloSecretStore.cs`, `HaloPsaSettings.cs`, plus three test files) and every current reference to `.IsConfigured`/`.BuildAuthorizationUrl` (`Program.cs` ×2, `DomainDetail.razor` ×2, `ManageMtaSts.razor` ×1) was individually located and accounted for in Tasks 1 and 3 respectively, by reading the actual current file contents rather than working from the spec's description alone.
- **Type consistency check:** `ISecretStore`, `HaloPsaSettings.SecretStoreKey`/`CloudflareDnsSettings.SecretStoreKey`/`AzureDnsSettings.SecretStoreKey`, `CloudflareDnsSettingsService`/`AzureDnsSettingsService`, `IDnsPushProvider`'s three async members, and `DnsPushProviderLookup.FindConfiguredAsync` all use the same signatures everywhere they're referenced across Tasks 1–4.
- **Migration sequencing:** Task 1's migration (drop `HaloPsaSettings.ProtectedClientSecret`, add `EncryptedSecrets`) and Task 2's migration (add `CloudflareDnsSettings`/`AzureDnsSettings`) are independent — neither touches a table or column the other depends on — so their order relative to each other doesn't matter beyond Task 1 needing to land first per the task dependency chain (Task 2's settings services take `ISecretStore`, which only exists after Task 1).
