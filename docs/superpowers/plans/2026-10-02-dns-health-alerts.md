# DNS Health Alerts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Raise alerts when a domain's DNS health check breaks, its DMARC policy weakens or its nameservers change, confirmed by a quick recheck and delivered through the existing alert path.

**Architecture:** A new `DomainAlertStates` table remembers, per domain and watched item, whether a check has passed, the accepted policy or nameservers, and a failure waiting for confirmation. A pure `DnsHealthAlertEvaluator` compares each monitored domain's stored check results with that state on every alert-monitor cycle and returns raise/resolve actions, which `AlertingService` carries out through its existing `EnsureAlertAsync`/`ResolveAlertAsync`. Polling re-runs a check whose confirmation recheck is due; policy and nameserver alerts close by Acknowledge, Halo ticket close or optional auto-close, each accepting the current value as the new baseline.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor 9.8, EF Core 10 + Npgsql (PostgreSQL), xUnit with the Testcontainers Postgres fixture, dotnet-ef 10.0.10 (local tool).

**Spec:** `docs/superpowers/specs/2026-10-02-dns-health-alerts-design.md`

## Global Constraints

- Alert type keys, once shipped, never change: `DmarcRecordBroken`, `DmarcAuthorizationBroken`, `TlsrptRecordBroken`, `SpfRecordBroken`, `MxRecordBroken`, `DkimRecordBroken`, `MtaStsFailing`, `DmarcPolicyWeakened`, `NameserversChanged`.
- `DomainAlertState.Item` values, once shipped, never change: `Dmarc`, `DmarcAuthorization`, `Tlsrpt`, `Spf`, `Mx`, `Dkim`, `MtaSts`, `DmarcPolicy`, `Nameservers`.
- Confirmation delay is 15 minutes.
- Every check mode defaults to *When it breaks*; DMARC policy weakened and nameservers changed default to on; auto-close days default to 0 (never).
- Severity: `Warning` for the seven check alerts and DMARC policy weakened, `Info` for nameservers changed. Nameservers changed doesn't create a ticket by default; the other eight do.
- User-facing text (UI, alert messages, docs) uses no em dashes.
- Follow the repo's style: meaningful variable names (no `r`, `m`, `x` even in tests), comments explain why, `ConfigureAwait(false)` in library code.

## Review Focus

1. **Upgrading with many half-configured domains** must raise nothing on the first cycle. Pinned in Task 4 (`FirstCycle_OnExistingDomains_RaisesNothing`).
2. **A DNS blip** (a check fails once and passes on the recheck) must never alert. Pinned in Task 3 (`ACheckThatPassesAgainBeforeItsRecheck_NeverAlerts_AndResolves`).
3. **A deliberate DMARC downgrade that's been acknowledged** must not alert again on the next cycle. Pinned in Task 6 (`Acknowledge_AcceptsTheCurrentPolicy_SoTheNextCycleRaisesNothing`).
4. **Nameservers listed in a different order or case** must not count as a change. Pinned in Task 3 (`NameserversInADifferentOrderOrCase_AreTheSame`).
5. **A domain that stops being monitored** must not leave a DNS health alert open forever. Pinned in Task 4 (`AlertsForADomainNoLongerMonitored_AreResolved`).

---

### Task 1: Model, settings and migration

**Files:**
- Create: `src/DotMarc/Data/DmarcPolicyLevel.cs`
- Create: `src/DotMarc/Data/DomainAlertState.cs`
- Create: `src/DotMarc/Notifications/DnsHealthAlertMode.cs`
- Modify: `src/DotMarc/Data/Domain.cs`
- Modify: `src/DotMarc/Notifications/NotificationSettings.cs`
- Modify: `src/DotMarc/Notifications/NotificationSettingsService.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Create (generated): `src/DotMarc/Migrations/<timestamp>_AddDnsHealthAlerts.cs`
- Test: `test/DotMarc.Tests/Notifications/NotificationSettingsServiceTests.cs`, `test/DotMarc.Tests/Data/DomainAlertStateTests.cs`

**Interfaces:**
- Produces: `enum DmarcPolicyLevel { None, Quarantine, Reject }` (`DotMarc.Data`); `Domain.DmarcPolicy`, `Domain.DmarcSubdomainPolicy` (`DmarcPolicyLevel?`), `Domain.DmarcPercent` (`int?`), `Domain.AlertStates` (`List<DomainAlertState>`); `class DomainAlertState { int Id; int DomainId; string Item; bool HasPassed; string? Baseline; DateTimeOffset? PendingSinceUtc; DateTimeOffset? RecheckDueUtc; }` (`DotMarc.Data`); `DotMarcDbContext.DomainAlertStates`; `enum DnsHealthAlertMode { WhenItBreaks, WheneverItFails, Off }` (`DotMarc.Notifications`); `NotificationSettings.DmarcAlertMode`, `DmarcAuthorizationAlertMode`, `TlsrptAlertMode`, `SpfAlertMode`, `MxAlertMode`, `DkimAlertMode`, `MtaStsAlertMode` (`DnsHealthAlertMode`), `DmarcPolicyWeakenedEnabled`, `NameserversChangedEnabled` (`bool`), `AcknowledgeableAutoCloseDays` (`int`).

- [ ] **Step 1: Write the failing tests**

Add to `NotificationSettingsServiceTests` (it already has `CreateContext()` and the Postgres fixture):

```csharp
    [Fact]
    public async Task AFreshDatabase_HasTheDnsHealthAlertDefaults()
    {
        await using var context = CreateContext();

        var settings = await NotificationSettingsService.GetAsync(context);

        Assert.All(
            [settings.DmarcAlertMode, settings.DmarcAuthorizationAlertMode, settings.TlsrptAlertMode, settings.SpfAlertMode,
             settings.MxAlertMode, settings.DkimAlertMode, settings.MtaStsAlertMode],
            mode => Assert.Equal(DnsHealthAlertMode.WhenItBreaks, mode));
        Assert.True(settings.DmarcPolicyWeakenedEnabled);
        Assert.True(settings.NameserversChangedEnabled);
        Assert.Equal(0, settings.AcknowledgeableAutoCloseDays);
    }

    [Fact]
    public async Task SaveAsync_SavesAndAuditsTheDnsHealthAlertSettings()
    {
        NotificationSettings updated;
        await using (var loadContext = CreateContext())
        {
            updated = await loadContext.NotificationSettings.AsNoTracking().SingleAsync();
        }
        updated.SpfAlertMode = DnsHealthAlertMode.WheneverItFails;
        updated.DkimAlertMode = DnsHealthAlertMode.Off;
        updated.NameserversChangedEnabled = false;
        updated.AcknowledgeableAutoCloseDays = 7;

        await using (var context = CreateContext())
        {
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, updated);
        }

        await using var verify = CreateContext();
        var saved = await NotificationSettingsService.GetAsync(verify);
        Assert.Equal(DnsHealthAlertMode.WheneverItFails, saved.SpfAlertMode);
        Assert.Equal(DnsHealthAlertMode.Off, saved.DkimAlertMode);
        Assert.False(saved.NameserversChangedEnabled);
        Assert.Equal(7, saved.AcknowledgeableAutoCloseDays);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Contains(new AuditFieldChange("SPF alerts", "WhenItBreaks", "WheneverItFails"), entry.Changes);
        Assert.Contains(new AuditFieldChange("Close policy and nameserver alerts after (days)", "0", "7"), entry.Changes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public async Task SaveAsync_RefusesAnAutoCloseOutsideZeroTo365Days(int days)
    {
        await using var context = CreateContext();
        var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
        settings.AcknowledgeableAutoCloseDays = days;

        await Assert.ThrowsAsync<ArgumentException>(() => NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings));
    }
```

Create `test/DotMarc.Tests/Data/DomainAlertStateTests.cs` with the usual Postgres boilerplate (copy the fields, constructor, `InitializeAsync`, `DisposeAsync` and `CreateContext()` from `test/DotMarc.Tests/Audit/AuditLogTests.cs`, renaming the class), then:

```csharp
    [Fact]
    public async Task AStateRow_IsSaved_AndOnlyOnePerDomainAndItem()
    {
        await using (var context = CreateContext())
        {
            var domain = new Domain { Name = "contoso.com", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true };
            domain.AlertStates.Add(new DomainAlertState { Item = "Spf", HasPassed = true });
            domain.AlertStates.Add(new DomainAlertState { Item = "DmarcPolicy", Baseline = "p=reject; sp=reject; pct=100" });
            context.Domains.Add(domain);
            await context.SaveChangesAsync();
        }

        await using (var verify = CreateContext())
        {
            var states = await verify.DomainAlertStates.OrderBy(state => state.Item).ToListAsync();
            Assert.Equal(["DmarcPolicy", "Spf"], states.Select(state => state.Item));
            Assert.Equal("p=reject; sp=reject; pct=100", states[0].Baseline);
            Assert.True(states[1].HasPassed);
        }

        await using var duplicate = CreateContext();
        var domainId = (await duplicate.Domains.SingleAsync()).Id;
        duplicate.DomainAlertStates.Add(new DomainAlertState { DomainId = domainId, Item = "Spf" });
        await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
    }

    [Fact]
    public async Task DeletingADomain_DeletesItsStateRows()
    {
        await using (var context = CreateContext())
        {
            var domain = new Domain { Name = "contoso.com", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true };
            domain.AlertStates.Add(new DomainAlertState { Item = "Mx", HasPassed = true });
            context.Domains.Add(domain);
            await context.SaveChangesAsync();
            context.Domains.Remove(domain);
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.DomainAlertStates);
    }

    [Fact]
    public async Task ADomainsDmarcPolicyFields_AreSaved()
    {
        await using (var context = CreateContext())
        {
            context.Domains.Add(new Domain
            {
                Name = "contoso.com", FirstSeenUtc = DateTimeOffset.UtcNow,
                DmarcPolicy = DmarcPolicyLevel.Quarantine, DmarcSubdomainPolicy = DmarcPolicyLevel.None, DmarcPercent = 50
            });
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        var saved = await verify.Domains.SingleAsync();
        Assert.Equal((DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.None, 50), (saved.DmarcPolicy!.Value, saved.DmarcSubdomainPolicy!.Value, saved.DmarcPercent!.Value));
    }
```

(Needed usings in the new file: `DotMarc.Data`, `DotMarc.Tests.Internal`, `Microsoft.EntityFrameworkCore`, `Xunit`. Add `using DotMarc.Audit;` to `NotificationSettingsServiceTests` if it isn't there.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~NotificationSettingsServiceTests|FullyQualifiedName~DomainAlertStateTests" -nologo -v q`
Expected: build FAILS, `DnsHealthAlertMode`, `DomainAlertState` and `DmarcPolicyLevel` not found.

- [ ] **Step 3: Add the types**

Create `src/DotMarc/Data/DmarcPolicyLevel.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>A DMARC policy (the p= or sp= tag), weakest first, so a lower value is a weaker policy.</summary>
public enum DmarcPolicyLevel
{
    None,
    Quarantine,
    Reject
}
```

Create `src/DotMarc/Data/DomainAlertState.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>What the DNS health alerts remember about one watched item of one domain between alert-monitor cycles:
/// whether a check has ever passed, the accepted DMARC policy or nameservers, and a failure or change waiting for its
/// confirmation recheck. See docs/superpowers/specs/2026-10-02-dns-health-alerts-design.md.</summary>
public sealed class DomainAlertState
{
    public int Id { get; set; }
    public int DomainId { get; set; }

    /// <summary>One of DnsHealthItems. Stored, so the values never change.</summary>
    public required string Item { get; set; }

    public bool HasPassed { get; set; }

    /// <summary>The accepted DMARC policy ("p=reject; sp=reject; pct=100") or nameservers (sorted, ";"-joined). Null
    /// for the seven checks.</summary>
    public string? Baseline { get; set; }

    public DateTimeOffset? PendingSinceUtc { get; set; }
    public DateTimeOffset? RecheckDueUtc { get; set; }
}
```

Create `src/DotMarc/Notifications/DnsHealthAlertMode.cs`:

```csharp
namespace DotMarc.Notifications;

/// <summary>When a DNS health check raises an alert. WhenItBreaks is first, so it's the default for a new column.</summary>
public enum DnsHealthAlertMode
{
    /// <summary>Only once the check has passed at least once, so domains that were never set up stay quiet.</summary>
    WhenItBreaks,
    WheneverItFails,
    Off
}
```

In `src/DotMarc/Data/Domain.cs`, after `public string? DmarcCheckDetail { get; set; }`, add:

```csharp
    // Read from the DMARC record by the DMARC check, with sp and pct filled in from their defaults; all null when
    // there's no DMARC record. The DMARC policy weakened alert compares these with the accepted policy.
    public DmarcPolicyLevel? DmarcPolicy { get; set; }
    public DmarcPolicyLevel? DmarcSubdomainPolicy { get; set; }
    public int? DmarcPercent { get; set; }
```

and after `public List<Tag> Tags { get; set; } = [];` add:

```csharp
    public List<DomainAlertState> AlertStates { get; set; } = [];
```

In `src/DotMarc/Notifications/NotificationSettings.cs`, after `SuspiciousRejectNonBenignPercent`, add:

```csharp
    public DnsHealthAlertMode DmarcAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public DnsHealthAlertMode DmarcAuthorizationAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public DnsHealthAlertMode TlsrptAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public DnsHealthAlertMode SpfAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public DnsHealthAlertMode MxAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public DnsHealthAlertMode DkimAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public DnsHealthAlertMode MtaStsAlertMode { get; set; } = DnsHealthAlertMode.WhenItBreaks;
    public bool DmarcPolicyWeakenedEnabled { get; set; } = true;
    public bool NameserversChangedEnabled { get; set; } = true;

    /// <summary>Close an open DMARC policy weakened or nameservers changed alert this many days after it was raised,
    /// as if acknowledged. 0 means never.</summary>
    public int AcknowledgeableAutoCloseDays { get; set; }
```

- [ ] **Step 4: Configure the model**

In `src/DotMarc/Data/DotMarcDbContext.cs`:

Add the set after `AlertTicketRules`:

```csharp
    public DbSet<DomainAlertState> DomainAlertStates => Set<DomainAlertState>();
```

Inside the existing `modelBuilder.Entity<Domain>(entity => { ... })` block, after `entity.Property(d => d.DnsProvider).HasConversion<string>();`, add:

```csharp
            entity.Property(d => d.DmarcPolicy).HasConversion<string>();
            entity.Property(d => d.DmarcSubdomainPolicy).HasConversion<string>();
```

After the `modelBuilder.Entity<AlertTicketRule>(...)` block, add:

```csharp
        modelBuilder.Entity<DomainAlertState>(entity =>
        {
            entity.Property(state => state.Item).HasMaxLength(40);
            entity.Property(state => state.Baseline).HasMaxLength(2000);
            entity.HasIndex(state => new { state.DomainId, state.Item }).IsUnique();
            entity.HasOne<Domain>().WithMany(domain => domain.AlertStates).HasForeignKey(state => state.DomainId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationSettings>(entity =>
        {
            entity.Property(settings => settings.DmarcAlertMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(settings => settings.DmarcAuthorizationAlertMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(settings => settings.TlsrptAlertMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(settings => settings.SpfAlertMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(settings => settings.MxAlertMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(settings => settings.DkimAlertMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(settings => settings.MtaStsAlertMode).HasConversion<string>().HasMaxLength(20);
        });
```

- [ ] **Step 5: Save and audit the settings**

In `NotificationSettingsService.SaveAsync`, after the two `ValidateWebhookUrl` calls, add:

```csharp
        if (updated.AcknowledgeableAutoCloseDays is < 0 or > 365)
        {
            throw new ArgumentException("Close policy and nameserver alerts after (days) must be from 0 to 365.", nameof(updated));
        }
```

Extend the `changes` chain after the suspicious-reject line:

```csharp
            .Field("Suspicious reject non-benign %", saved.SuspiciousRejectNonBenignPercent, updated.SuspiciousRejectNonBenignPercent)
            .Field("DMARC record alerts", saved.DmarcAlertMode, updated.DmarcAlertMode)
            .Field("DMARC authorization record alerts", saved.DmarcAuthorizationAlertMode, updated.DmarcAuthorizationAlertMode)
            .Field("TLS-RPT record alerts", saved.TlsrptAlertMode, updated.TlsrptAlertMode)
            .Field("SPF alerts", saved.SpfAlertMode, updated.SpfAlertMode)
            .Field("MX alerts", saved.MxAlertMode, updated.MxAlertMode)
            .Field("DKIM alerts", saved.DkimAlertMode, updated.DkimAlertMode)
            .Field("MTA-STS alerts", saved.MtaStsAlertMode, updated.MtaStsAlertMode)
            .Field("DMARC policy weakened alerts", saved.DmarcPolicyWeakenedEnabled, updated.DmarcPolicyWeakenedEnabled)
            .Field("Nameservers changed alerts", saved.NameserversChangedEnabled, updated.NameserversChangedEnabled)
            .Field("Close policy and nameserver alerts after (days)", saved.AcknowledgeableAutoCloseDays, updated.AcknowledgeableAutoCloseDays);
```

(Replace the existing final `.Field("Suspicious reject non-benign %", ...);` line, which ended the chain, with the block above.) After `existing.SuspiciousRejectNonBenignPercent = updated.SuspiciousRejectNonBenignPercent;` add:

```csharp
        existing.DmarcAlertMode = updated.DmarcAlertMode;
        existing.DmarcAuthorizationAlertMode = updated.DmarcAuthorizationAlertMode;
        existing.TlsrptAlertMode = updated.TlsrptAlertMode;
        existing.SpfAlertMode = updated.SpfAlertMode;
        existing.MxAlertMode = updated.MxAlertMode;
        existing.DkimAlertMode = updated.DkimAlertMode;
        existing.MtaStsAlertMode = updated.MtaStsAlertMode;
        existing.DmarcPolicyWeakenedEnabled = updated.DmarcPolicyWeakenedEnabled;
        existing.NameserversChangedEnabled = updated.NameserversChangedEnabled;
        existing.AcknowledgeableAutoCloseDays = updated.AcknowledgeableAutoCloseDays;
```

- [ ] **Step 6: Generate the migration and check its defaults**

Run: `dotnet dotnet-ef migrations add AddDnsHealthAlerts --project src/DotMarc --startup-project src/DotMarc`
Expected: a new migration creating `DomainAlertStates` (with the unique index and cascade foreign key), adding the three nullable `Domains` columns, and adding the ten `NotificationSettings` columns.

Open the generated migration and check the existing settings row ends up with the spec's defaults:
- Each `...AlertMode` `AddColumn<string>` must have `defaultValue: "WhenItBreaks"`. If EF wrote `defaultValue: ""`, change it to `"WhenItBreaks"` by hand (an empty string can't be read back as the enum).
- `DmarcPolicyWeakenedEnabled` and `NameserversChangedEnabled` must end up `true` on row 1: either their `AddColumn<bool>` has `defaultValue: true`, or the migration has an `UpdateData` for `NotificationSettings` row 1 setting them to `true`. If neither, set `defaultValue: true` on both `AddColumn<bool>` calls by hand.
- `AcknowledgeableAutoCloseDays` defaults to `0`.

The `AFreshDatabase_HasTheDnsHealthAlertDefaults` test is what proves this.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~NotificationSettingsServiceTests|FullyQualifiedName~DomainAlertStateTests|FullyQualifiedName~DotMarcDbContextTests|FullyQualifiedName~DatabaseMigratorTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 8: Commit**

```powershell
git add src/DotMarc/Data src/DotMarc/Notifications src/DotMarc/Migrations test/DotMarc.Tests
git commit -m "Add the DNS health alert settings, state table and DMARC policy fields"
```

---

### Task 2: Reading the DMARC policy

**Files:**
- Create: `src/DotMarc/Dns/DmarcPolicyTags.cs`
- Modify: `src/DotMarc/Dns/DmarcCheckResult.cs`, `src/DotMarc/Dns/DmarcDnsChecker.cs`, `src/DotMarc/Ingestion/PollingService.cs` (`RunSingleDmarcCheckAsync`)
- Test: `test/DotMarc.Tests/Dns/DmarcPolicyTagsTests.cs`, `test/DotMarc.Tests/Dns/DmarcDnsCheckerTests.cs`, `test/DotMarc.Tests/Ingestion/DmarcCheckCycleTests.cs`

**Interfaces:**
- Consumes: Task 1's `DmarcPolicyLevel` and the three `Domain` fields.
- Produces: `sealed record DmarcPolicyTags(DmarcPolicyLevel Policy, DmarcPolicyLevel SubdomainPolicy, int Percent)` (`DotMarc.Dns`) with `static DmarcPolicyTags? Parse(string? record)`, `static DmarcPolicyTags? Of(Domain domain)`, `string Format()`, `bool IsWeakerThan(DmarcPolicyTags other)`; `DmarcCheckResult(DmarcCheckStatus Status, string? Detail, DmarcPolicyTags? Policy = null)`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Dns/DmarcPolicyTagsTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class DmarcPolicyTagsTests
{
    [Fact]
    public void Parse_ReadsPAndFillsSpAndPctFromTheirDefaults()
    {
        Assert.Equal(new DmarcPolicyTags(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 100),
            DmarcPolicyTags.Parse("v=DMARC1; p=reject; rua=mailto:rua@example.com"));
    }

    [Fact]
    public void Parse_IgnoresCaseAndSpaces()
    {
        Assert.Equal(new DmarcPolicyTags(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.None, 50),
            DmarcPolicyTags.Parse("v=DMARC1;  P = Quarantine ;SP=NONE; pct= 50"));
    }

    [Theory]
    [InlineData("v=DMARC1; rua=mailto:rua@example.com")]
    [InlineData("v=DMARC1; p=bogus")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_IsNull_WithoutAValidP(string? record)
    {
        Assert.Null(DmarcPolicyTags.Parse(record));
    }

    [Theory]
    [InlineData("v=DMARC1; p=reject; sp=bogus", DmarcPolicyLevel.Reject)]
    [InlineData("v=DMARC1; p=none; sp=", DmarcPolicyLevel.None)]
    public void Parse_FallsBackToP_ForAnInvalidSp(string record, DmarcPolicyLevel expected)
    {
        Assert.Equal(expected, DmarcPolicyTags.Parse(record)!.SubdomainPolicy);
    }

    [Theory]
    [InlineData("pct=150")]
    [InlineData("pct=-1")]
    [InlineData("pct=abc")]
    [InlineData("pct=")]
    public void Parse_FallsBackTo100_ForAnInvalidPct(string pctTag)
    {
        Assert.Equal(100, DmarcPolicyTags.Parse($"v=DMARC1; p=reject; {pctTag}")!.Percent);
    }

    [Fact]
    public void Parse_UsesTheFirstOfARepeatedTag()
    {
        Assert.Equal(DmarcPolicyLevel.Quarantine, DmarcPolicyTags.Parse("v=DMARC1; p=quarantine; p=reject")!.Policy);
    }

    [Fact]
    public void Format_ReadsBackAsTheSamePolicy()
    {
        var tags = new DmarcPolicyTags(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.None, 25);

        Assert.Equal("p=quarantine; sp=none; pct=25", tags.Format());
        Assert.Equal(tags, DmarcPolicyTags.Parse(tags.Format()));
    }

    [Theory]
    [InlineData("p=quarantine; sp=reject; pct=100", true)]
    [InlineData("p=reject; sp=none; pct=100", true)]
    [InlineData("p=reject; sp=reject; pct=50", true)]
    [InlineData("p=reject; sp=reject; pct=100", false)]
    public void IsWeakerThan_ComparesEachTag(string current, bool weaker)
    {
        var baseline = new DmarcPolicyTags(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 100);

        Assert.Equal(weaker, DmarcPolicyTags.Parse(current)!.IsWeakerThan(baseline));
    }

    [Fact]
    public void AStrongerPolicy_IsNotWeaker()
    {
        var baseline = new DmarcPolicyTags(DmarcPolicyLevel.None, DmarcPolicyLevel.None, 50);

        Assert.False(new DmarcPolicyTags(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 100).IsWeakerThan(baseline));
    }

    [Fact]
    public void Of_ReadsADomainsStoredPolicy_OrNullWithout()
    {
        Assert.Equal(new DmarcPolicyTags(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.Quarantine, 100),
            DmarcPolicyTags.Of(new Domain { Name = "contoso.com", DmarcPolicy = DmarcPolicyLevel.Quarantine }));
        Assert.Null(DmarcPolicyTags.Of(new Domain { Name = "contoso.com" }));
    }
}
```

Add to `DmarcDnsCheckerTests`:

```csharp
    [Fact]
    public async Task CheckAsync_ReadsThePolicy_WhenTheRecordIsOk()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=quarantine; pct=50; rua=mailto:rua.dmarc@mjco.uk\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Ok, result.Status);
        Assert.Equal(new DmarcPolicyTags(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.Quarantine, 50), result.Policy);
    }

    [Fact]
    public async Task CheckAsync_ReadsThePolicy_EvenWhenRuaIsWrong()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """
            {"Status":0,"Answer":[{"type":16,"data":"\"v=DMARC1; p=reject; rua=mailto:other@example.com\""}]}
            """;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Equal(DmarcCheckStatus.Misconfigured, result.Status);
        Assert.Equal(DmarcPolicyLevel.Reject, result.Policy!.Policy);
    }

    [Fact]
    public async Task CheckAsync_HasNoPolicy_WithoutARecord()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = NxDomainResponse;

        var result = await checker.CheckAsync("contoso.io", "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Null(result.Policy);
    }
```

Add to `DmarcCheckCycleTests` (it has `CreateContext()` and `CreateService(context)` like `SpfCheckCycleTests`):

```csharp
    [Fact]
    public async Task RunDmarcCheckCycleAsync_StoresThePolicy_AndClearsItWhenTheRecordGoes()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        var checker = new FakeDmarcDnsChecker { Result = new(DmarcCheckStatus.Ok, null, new DmarcPolicyTags(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Quarantine, 50)) };
        var service = CreateService(context);

        await service.RunDmarcCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        var domain = context.Domains.Single();
        Assert.Equal((DmarcPolicyLevel.Reject, DmarcPolicyLevel.Quarantine, 50), (domain.DmarcPolicy!.Value, domain.DmarcSubdomainPolicy!.Value, domain.DmarcPercent!.Value));

        domain.DmarcCheckedUtc = DateTimeOffset.UtcNow.AddDays(-2);
        await context.SaveChangesAsync();
        checker.Result = new(DmarcCheckStatus.MissingOwnRecord, "No TXT record found at _dmarc.contoso.io");
        await service.RunDmarcCheckCycleAsync(context, checker, "rua.dmarc@mjco.uk", CancellationToken.None);

        Assert.Null(domain.DmarcPolicy);
        Assert.Null(domain.DmarcSubdomainPolicy);
        Assert.Null(domain.DmarcPercent);
    }
```

(Add `using DotMarc.Dns;` to `DmarcCheckCycleTests` if it isn't there.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DmarcPolicyTagsTests|FullyQualifiedName~DmarcDnsCheckerTests|FullyQualifiedName~DmarcCheckCycleTests" -nologo -v q`
Expected: build FAILS, `DmarcPolicyTags` not found.

- [ ] **Step 3: Write the policy tags**

Create `src/DotMarc/Dns/DmarcPolicyTags.cs`:

```csharp
using System.Globalization;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>A DMARC record's policy tags, with sp and pct filled in from their defaults (sp is p; pct is 100). Also
/// the format the DMARC policy weakened alert stores its accepted policy in.</summary>
public sealed record DmarcPolicyTags(DmarcPolicyLevel Policy, DmarcPolicyLevel SubdomainPolicy, int Percent)
{
    /// <summary>Reads p, sp and pct from a DMARC record, or from a baseline written by <see cref="Format"/>. Null if
    /// p is missing or isn't none, quarantine or reject. Tag names and values ignore case and spaces, and the first
    /// of a repeated tag wins.</summary>
    public static DmarcPolicyTags? Parse(string? record)
    {
        if (string.IsNullOrWhiteSpace(record))
        {
            return null;
        }

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in record.Split(';'))
        {
            var separator = part.IndexOf('=');
            if (separator > 0)
            {
                tags.TryAdd(part[..separator].Trim(), part[(separator + 1)..].Trim());
            }
        }

        if (!tags.TryGetValue("p", out var policyText) || ParseLevel(policyText) is not { } policy)
        {
            return null;
        }

        var subdomainPolicy = tags.TryGetValue("sp", out var subdomainText) ? ParseLevel(subdomainText) ?? policy : policy;
        var percent = tags.TryGetValue("pct", out var percentText)
                      && int.TryParse(percentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPercent)
                      && parsedPercent is >= 0 and <= 100
            ? parsedPercent
            : 100;
        return new DmarcPolicyTags(policy, subdomainPolicy, percent);
    }

    /// <summary>A domain's policy as the DMARC check last stored it, or null if it had no DMARC record.</summary>
    public static DmarcPolicyTags? Of(Domain domain) =>
        domain.DmarcPolicy is { } policy
            ? new DmarcPolicyTags(policy, domain.DmarcSubdomainPolicy ?? policy, domain.DmarcPercent ?? 100)
            : null;

    public string Format() => $"p={Name(Policy)}; sp={Name(SubdomainPolicy)}; pct={Percent.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>True if any of p, sp or pct is lower than in <paramref name="other"/>.</summary>
    public bool IsWeakerThan(DmarcPolicyTags other) =>
        Policy < other.Policy || SubdomainPolicy < other.SubdomainPolicy || Percent < other.Percent;

    private static DmarcPolicyLevel? ParseLevel(string text) => text.ToLowerInvariant() switch
    {
        "none" => DmarcPolicyLevel.None,
        "quarantine" => DmarcPolicyLevel.Quarantine,
        "reject" => DmarcPolicyLevel.Reject,
        _ => null
    };

    private static string Name(DmarcPolicyLevel level) => level.ToString().ToLowerInvariant();
}
```

- [ ] **Step 4: Return and store the policy**

Replace `src/DotMarc/Dns/DmarcCheckResult.cs`'s record line with:

```csharp
public sealed record DmarcCheckResult(DmarcCheckStatus Status, string? Detail, DmarcPolicyTags? Policy = null);
```

In `DmarcDnsChecker.CheckAsync`, after the `v=DMARC1` check, read the policy and pass it to both remaining results:

```csharp
        var policy = DmarcPolicyTags.Parse(ownRecord);
        var ruaAddresses = ParseRuaAddresses(ownRecord);
        if (!ruaAddresses.Any(a => string.Equals(a, mailboxAddress, StringComparison.OrdinalIgnoreCase)))
        {
            return new DmarcCheckResult(DmarcCheckStatus.Misconfigured,
                ruaAddresses.Count == 0
                    ? $"_dmarc.{domainName} has no rua= tag"
                    : $"_dmarc.{domainName}'s rua= points to {string.Join(", ", ruaAddresses)}, not {mailboxAddress}",
                policy);
        }

        return new DmarcCheckResult(DmarcCheckStatus.Ok, null, policy);
```

In `PollingService.RunSingleDmarcCheckAsync`, after `domain.DmarcCheckDetail = result.Detail;` add:

```csharp
        domain.DmarcPolicy = result.Policy?.Policy;
        domain.DmarcSubdomainPolicy = result.Policy?.SubdomainPolicy;
        domain.DmarcPercent = result.Policy?.Percent;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DmarcPolicyTagsTests|FullyQualifiedName~DmarcDnsCheckerTests|FullyQualifiedName~DmarcCheckCycleTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/DotMarc/Dns src/DotMarc/Ingestion/PollingService.cs test/DotMarc.Tests
git commit -m "Read and store each domain's DMARC policy, subdomain policy and percentage"
```

---

### Task 3: The alert types and the evaluator

**Files:**
- Modify: `src/DotMarc/Notifications/AlertTypes.cs`
- Create: `src/DotMarc/Notifications/DnsHealthItems.cs`
- Create: `src/DotMarc/Notifications/DnsHealthChecks.cs`
- Create: `src/DotMarc/Notifications/DnsHealthAlertEvaluator.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertTypesTests.cs`, `test/DotMarc.Tests/Notifications/DnsHealthAlertEvaluatorTests.cs`

**Interfaces:**
- Consumes: Task 1's `DomainAlertState`, `DnsHealthAlertMode` and settings fields; Task 2's `DmarcPolicyTags`.
- Produces:
  - `AlertTypes` constants `DmarcRecordBroken`, `DmarcAuthorizationBroken`, `TlsrptRecordBroken`, `SpfRecordBroken`, `MxRecordBroken`, `DkimRecordBroken`, `MtaStsFailing`, `DmarcPolicyWeakened`, `NameserversChanged`, and `IReadOnlyList<string> AlertTypes.DnsHealth` (all nine).
  - `static class DnsHealthItems` with the nine item constants.
  - `enum DnsCheckHealth { Passing, Failing, Ignored }`; `sealed record DnsHealthCheck(string Item, string AlertType, string Label, Func<Domain, DnsCheckHealth> Health, Func<Domain, DateTimeOffset?> CheckedUtc, Func<Domain, string> Status, Func<Domain, string?> Detail, Func<NotificationSettings, DnsHealthAlertMode> Mode)`; `static class DnsHealthChecks` with `IReadOnlyList<DnsHealthCheck> All` and `DnsHealthCheck For(string item)`.
  - `enum DnsHealthActionKind { Raise, Resolve }`; `sealed record DnsHealthAlertAction(DnsHealthActionKind Kind, string AlertType, string Severity = "", string Title = "", string Message = "")`.
  - `static class DnsHealthAlertEvaluator` with `TimeSpan ConfirmationDelay` (15 minutes), `IReadOnlyList<DnsHealthAlertAction> Evaluate(Domain domain, List<DomainAlertState> states, NotificationSettings settings, DateTimeOffset nowUtc)` (updates the passed states and adds any missing ones, with `DomainId = domain.Id`), and `string? NameserverKey(IEnumerable<string> nameservers)`.

- [ ] **Step 1: Write the failing tests**

In `AlertTypesTests`, replace `All_ListsTheFourAlertTypesDotMarcRaises` and `EveryAlertType_HasANameADescriptionAndCreatesTicketsByDefault` with:

```csharp
    [Fact]
    public void All_ListsTheAlertTypesDotMarcRaises()
    {
        Assert.Equal(
            ["MissedReport", "SuspiciousRejectActivity", "TlsrptFailure", "UnexpectedActivityOnNullRoutedDomain",
             "DmarcRecordBroken", "DmarcAuthorizationBroken", "TlsrptRecordBroken", "SpfRecordBroken", "MxRecordBroken",
             "DkimRecordBroken", "MtaStsFailing", "DmarcPolicyWeakened", "NameserversChanged"],
            AlertTypes.All.Select(alertType => alertType.Key));
    }

    [Fact]
    public void EveryAlertType_HasANameAndADescription_AndAllButNameserverChangesCreateTickets()
    {
        foreach (var alertType in AlertTypes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(alertType.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(alertType.Description));
            Assert.Equal(alertType.Key != AlertTypes.NameserversChanged, alertType.CreatesTicketByDefault);
        }
    }

    [Fact]
    public void DnsHealth_ListsTheNineDnsHealthAlertTypes()
    {
        Assert.Equal(9, AlertTypes.DnsHealth.Count);
        Assert.All(AlertTypes.DnsHealth, key => Assert.NotNull(AlertTypes.Find(key)));
    }
```

(`EveryKeyConstant_IsInTheRegistry...` stays: it reads `string` constants, and `DnsHealth` is a property, not a constant.)

Create `test/DotMarc.Tests/Notifications/DnsHealthAlertEvaluatorTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class DnsHealthAlertEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static Domain HealthyDomain() => new()
    {
        Id = 7,
        Name = "contoso.com",
        IsMonitored = true,
        FirstSeenUtc = Now.AddDays(-30),
        DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcCheckedUtc = Now.AddHours(-1),
        DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.Ok, DmarcAuthorizationCheckedUtc = Now.AddHours(-1),
        TlsrptCheckStatus = TlsrptCheckStatus.Ok, TlsrptCheckedUtc = Now.AddHours(-1),
        SpfCheckStatus = SpfCheckStatus.Ok, SpfCheckedUtc = Now.AddHours(-1),
        MxCheckStatus = MxCheckStatus.Ok, MxCheckedUtc = Now.AddHours(-1),
        DkimSelectors = ["selector1"], DkimCheckStatus = DkimCheckStatus.Ok, DkimCheckedUtc = Now.AddHours(-1),
        MtaStsEnabled = true, MtaStsStatus = MtaStsStatus.Active, MtaStsCheckedUtc = Now.AddHours(-1),
        DmarcPolicy = DmarcPolicyLevel.Reject, DmarcSubdomainPolicy = DmarcPolicyLevel.Reject, DmarcPercent = 100,
        DnsNameservers = ["ns1.example.net", "ns2.example.net"], DnsProviderCheckedUtc = Now.AddHours(-1),
    };

    private static List<DnsHealthAlertAction> Run(Domain domain, List<DomainAlertState> states, NotificationSettings? settings = null, DateTimeOffset? now = null) =>
        DnsHealthAlertEvaluator.Evaluate(domain, states, settings ?? new NotificationSettings(), now ?? Now).ToList();

    private static IEnumerable<string> Raised(IEnumerable<DnsHealthAlertAction> actions) =>
        actions.Where(action => action.Kind == DnsHealthActionKind.Raise).Select(action => action.AlertType);

    private static bool Resolves(IEnumerable<DnsHealthAlertAction> actions, string alertType) =>
        actions.Any(action => action.Kind == DnsHealthActionKind.Resolve && action.AlertType == alertType);

    private static DomainAlertState State(List<DomainAlertState> states, string item) => states.Single(state => state.Item == item);

    /// <summary>States after a first cycle on a healthy domain: every check has passed, baselines are set.</summary>
    private static List<DomainAlertState> StatesAfterAHealthyCycle()
    {
        var states = new List<DomainAlertState>();
        Run(HealthyDomain(), states);
        return states;
    }

    private static Domain WithStatus(string item, string status)
    {
        var domain = HealthyDomain();
        switch (item)
        {
            case DnsHealthItems.Dmarc: domain.DmarcCheckStatus = Enum.Parse<DmarcCheckStatus>(status); break;
            case DnsHealthItems.DmarcAuthorization: domain.DmarcAuthorizationCheckStatus = Enum.Parse<DmarcAuthorizationCheckStatus>(status); break;
            case DnsHealthItems.Tlsrpt: domain.TlsrptCheckStatus = Enum.Parse<TlsrptCheckStatus>(status); break;
            case DnsHealthItems.Spf: domain.SpfCheckStatus = Enum.Parse<SpfCheckStatus>(status); break;
            case DnsHealthItems.Mx: domain.MxCheckStatus = Enum.Parse<MxCheckStatus>(status); break;
            case DnsHealthItems.Dkim: domain.DkimCheckStatus = Enum.Parse<DkimCheckStatus>(status); break;
            case DnsHealthItems.MtaSts: domain.MtaStsStatus = Enum.Parse<MtaStsStatus>(status); break;
        }

        return domain;
    }

    [Theory]
    [InlineData(DnsHealthItems.Dmarc, "Ok", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Dmarc, "MissingOwnRecord", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Dmarc, "Misconfigured", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Dmarc, "MissingAuthorizationRecord", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Dmarc, "NotChecked", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.DmarcAuthorization, "Ok", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.DmarcAuthorization, "Missing", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.DmarcAuthorization, "NotChecked", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.DmarcAuthorization, "NotApplicable", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.Tlsrpt, "Ok", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Tlsrpt, "MissingOwnRecord", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Tlsrpt, "Misconfigured", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Tlsrpt, "NotChecked", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.Spf, "Ok", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Spf, "NullSpf", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Spf, "MissingRecord", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Spf, "MultipleRecords", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Spf, "Misconfigured", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Spf, "NotChecked", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.Mx, "Ok", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Mx, "NullMx", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Mx, "MissingRecord", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Mx, "UnresolvableTarget", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Mx, "NotChecked", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.Dkim, "Ok", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.Dkim, "Missing", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Dkim, "Misconfigured", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.Dkim, "NotConfigured", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.MtaSts, "Active", DnsCheckHealth.Passing)]
    [InlineData(DnsHealthItems.MtaSts, "Failed", DnsCheckHealth.Failing)]
    [InlineData(DnsHealthItems.MtaSts, "PendingDns", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.MtaSts, "PendingCertificate", DnsCheckHealth.Ignored)]
    [InlineData(DnsHealthItems.MtaSts, "NotConfigured", DnsCheckHealth.Ignored)]
    public void EachStatus_IsPassingFailingOrIgnored(string item, string status, DnsCheckHealth expected)
    {
        Assert.Equal(expected, DnsHealthChecks.For(item).Health(WithStatus(item, status)));
    }

    [Fact]
    public void MtaStsTurnedOff_IsIgnoredWhateverItsStatus()
    {
        var domain = WithStatus(DnsHealthItems.MtaSts, "Failed");
        domain.MtaStsEnabled = false;

        Assert.Equal(DnsCheckHealth.Ignored, DnsHealthChecks.For(DnsHealthItems.MtaSts).Health(domain));
    }

    [Fact]
    public void FirstRun_OnAHealthyDomain_RaisesNothing_AndRemembersEverything()
    {
        var states = new List<DomainAlertState>();

        var actions = Run(HealthyDomain(), states);

        Assert.Empty(Raised(actions));
        Assert.Equal(9, states.Count);
        Assert.All(states, state => Assert.Equal(7, state.DomainId));
        Assert.All(DnsHealthChecks.All, check => Assert.True(State(states, check.Item).HasPassed));
        Assert.Equal("p=reject; sp=reject; pct=100", State(states, DnsHealthItems.DmarcPolicy).Baseline);
        Assert.Equal("ns1.example.net;ns2.example.net", State(states, DnsHealthItems.Nameservers).Baseline);
    }

    [Fact]
    public void FirstRun_OnACheckThatHasNeverPassed_RaisesNothing_InWhenItBreaksMode()
    {
        var states = new List<DomainAlertState>();
        var domain = WithStatus(DnsHealthItems.Spf, "MissingRecord");

        var actions = Run(domain, states);
        var later = Run(domain, states, now: Now.AddHours(1));

        Assert.Empty(Raised(actions.Concat(later)));
        Assert.False(State(states, DnsHealthItems.Spf).HasPassed);
        Assert.Null(State(states, DnsHealthItems.Spf).PendingSinceUtc);
    }

    [Fact]
    public void ACheckThatBreaks_IsConfirmedByARecheckBeforeItAlerts()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = WithStatus(DnsHealthItems.Spf, "MissingRecord");
        domain.SpfCheckedUtc = Now;
        domain.SpfCheckDetail = "No TXT record found at contoso.com";

        var firstFailure = Run(domain, states);
        Assert.Empty(Raised(firstFailure));
        Assert.Equal(Now, State(states, DnsHealthItems.Spf).PendingSinceUtc);
        Assert.Equal(Now.AddMinutes(15), State(states, DnsHealthItems.Spf).RecheckDueUtc);

        // Due, but the check hasn't run again yet.
        Assert.Empty(Raised(Run(domain, states, now: Now.AddMinutes(20))));

        domain.SpfCheckedUtc = Now.AddMinutes(17);
        var afterRecheck = Run(domain, states, now: Now.AddMinutes(20));

        var raised = Assert.Single(afterRecheck, action => action.Kind == DnsHealthActionKind.Raise);
        Assert.Equal(AlertTypes.SpfRecordBroken, raised.AlertType);
        Assert.Equal("Warning", raised.Severity);
        Assert.Equal("SPF record broken", raised.Title);
        Assert.Contains("contoso.com", raised.Message);
        Assert.Contains("missing record", raised.Message);
        Assert.Contains("No TXT record found at contoso.com", raised.Message);
        Assert.Contains("It was passing before.", raised.Message);
    }

    [Fact]
    public void ACheckThatPassesAgainBeforeItsRecheck_NeverAlerts_AndResolves()
    {
        var states = StatesAfterAHealthyCycle();
        var failing = WithStatus(DnsHealthItems.Mx, "MissingRecord");
        failing.MxCheckedUtc = Now;
        Run(failing, states);

        var recovered = HealthyDomain();
        recovered.MxCheckedUtc = Now.AddMinutes(17);
        var actions = Run(recovered, states, now: Now.AddMinutes(20));

        Assert.Empty(Raised(actions));
        Assert.True(Resolves(actions, AlertTypes.MxRecordBroken));
        Assert.Null(State(states, DnsHealthItems.Mx).PendingSinceUtc);
        Assert.Null(State(states, DnsHealthItems.Mx).RecheckDueUtc);
    }

    [Fact]
    public void AConfirmedFailure_KeepsBeingRaisedEachCycle_ForTheCooldownToDeduplicate()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = WithStatus(DnsHealthItems.Tlsrpt, "MissingOwnRecord");
        domain.TlsrptCheckedUtc = Now;
        Run(domain, states);
        domain.TlsrptCheckedUtc = Now.AddMinutes(16);

        Assert.Contains(AlertTypes.TlsrptRecordBroken, Raised(Run(domain, states, now: Now.AddMinutes(20))));
        Assert.Contains(AlertTypes.TlsrptRecordBroken, Raised(Run(domain, states, now: Now.AddMinutes(25))));
    }

    [Fact]
    public void WheneverItFails_AlertsOnACheckThatHasNeverPassed()
    {
        var settings = new NotificationSettings { DkimAlertMode = DnsHealthAlertMode.WheneverItFails };
        var states = new List<DomainAlertState>();
        var domain = WithStatus(DnsHealthItems.Dkim, "Missing");
        domain.DkimCheckedUtc = Now;

        Assert.Empty(Raised(Run(domain, states, settings)));
        domain.DkimCheckedUtc = Now.AddMinutes(16);
        var actions = Run(domain, states, settings, Now.AddMinutes(20));

        var raised = Assert.Single(actions, action => action.Kind == DnsHealthActionKind.Raise);
        Assert.Equal(AlertTypes.DkimRecordBroken, raised.AlertType);
        Assert.DoesNotContain("It was passing before.", raised.Message);
    }

    [Fact]
    public void Off_ResolvesTheAlertAndClearsThePendingFailure()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = WithStatus(DnsHealthItems.Dmarc, "MissingOwnRecord");
        Run(domain, states);

        var actions = Run(domain, states, new NotificationSettings { DmarcAlertMode = DnsHealthAlertMode.Off }, Now.AddMinutes(20));

        Assert.Empty(Raised(actions));
        Assert.True(Resolves(actions, AlertTypes.DmarcRecordBroken));
        Assert.Null(State(states, DnsHealthItems.Dmarc).PendingSinceUtc);
    }

    [Fact]
    public void AnIgnoredStatus_ResolvesTheAlertAndClearsThePendingFailure()
    {
        var states = StatesAfterAHealthyCycle();
        Run(WithStatus(DnsHealthItems.Dkim, "Missing"), states);

        // The selectors were removed: the check is no longer configured.
        var actions = Run(WithStatus(DnsHealthItems.Dkim, "NotConfigured"), states, now: Now.AddMinutes(20));

        Assert.True(Resolves(actions, AlertTypes.DkimRecordBroken));
        Assert.Null(State(states, DnsHealthItems.Dkim).PendingSinceUtc);
    }

    [Fact]
    public void AStrongerPolicy_BecomesTheBaseline_AndResolves()
    {
        var states = new List<DomainAlertState> { new() { DomainId = 7, Item = DnsHealthItems.DmarcPolicy, Baseline = "p=quarantine; sp=quarantine; pct=100" } };

        var actions = Run(HealthyDomain(), states);

        Assert.Equal("p=reject; sp=reject; pct=100", State(states, DnsHealthItems.DmarcPolicy).Baseline);
        Assert.True(Resolves(actions, AlertTypes.DmarcPolicyWeakened));
    }

    [Theory]
    [InlineData(DmarcPolicyLevel.Quarantine, DmarcPolicyLevel.Reject, 100, "p=quarantine; sp=reject; pct=100")]
    [InlineData(DmarcPolicyLevel.Reject, DmarcPolicyLevel.None, 100, "p=reject; sp=none; pct=100")]
    [InlineData(DmarcPolicyLevel.Reject, DmarcPolicyLevel.Reject, 25, "p=reject; sp=reject; pct=25")]
    public void AWeakerPolicy_IsConfirmedThenRaised(DmarcPolicyLevel policy, DmarcPolicyLevel subdomainPolicy, int percent, string expectedNow)
    {
        var states = StatesAfterAHealthyCycle();
        var domain = HealthyDomain();
        (domain.DmarcPolicy, domain.DmarcSubdomainPolicy, domain.DmarcPercent, domain.DmarcCheckedUtc) = (policy, subdomainPolicy, percent, Now);

        Assert.Empty(Raised(Run(domain, states)));
        domain.DmarcCheckedUtc = Now.AddMinutes(16);
        var actions = Run(domain, states, now: Now.AddMinutes(20));

        var raised = Assert.Single(actions, action => action.Kind == DnsHealthActionKind.Raise);
        Assert.Equal(AlertTypes.DmarcPolicyWeakened, raised.AlertType);
        Assert.Equal("Warning", raised.Severity);
        Assert.Contains("p=reject; sp=reject; pct=100", raised.Message);
        Assert.Contains(expectedNow, raised.Message);
        Assert.Equal("p=reject; sp=reject; pct=100", State(states, DnsHealthItems.DmarcPolicy).Baseline);
    }

    [Fact]
    public void ARemovedDmarcRecord_LeavesThePolicyAlertAlone()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = HealthyDomain();
        (domain.DmarcPolicy, domain.DmarcSubdomainPolicy, domain.DmarcPercent) = (null, null, null);

        var actions = Run(domain, states);

        Assert.DoesNotContain(actions, action => action.AlertType == AlertTypes.DmarcPolicyWeakened);
        Assert.Equal("p=reject; sp=reject; pct=100", State(states, DnsHealthItems.DmarcPolicy).Baseline);
    }

    [Fact]
    public void ThePolicyAlertTurnedOff_Resolves_AndKeepsTheBaseline()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = HealthyDomain();
        domain.DmarcPolicy = DmarcPolicyLevel.None;

        var actions = Run(domain, states, new NotificationSettings { DmarcPolicyWeakenedEnabled = false });

        Assert.True(Resolves(actions, AlertTypes.DmarcPolicyWeakened));
        Assert.Empty(Raised(actions));
        Assert.Equal("p=reject; sp=reject; pct=100", State(states, DnsHealthItems.DmarcPolicy).Baseline);
    }

    [Fact]
    public void NameserversInADifferentOrderOrCase_AreTheSame()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = HealthyDomain();
        domain.DnsNameservers = ["NS2.Example.Net.", "ns1.example.net"];

        var actions = Run(domain, states);

        Assert.Empty(Raised(actions));
        Assert.Null(State(states, DnsHealthItems.Nameservers).PendingSinceUtc);
        Assert.True(Resolves(actions, AlertTypes.NameserversChanged));
    }

    [Fact]
    public void ChangedNameservers_AreConfirmedThenRaisedAsInfo_AndResolveWhenTheyChangeBack()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = HealthyDomain();
        domain.DnsNameservers = ["anna.ns.cloudflare.com", "bob.ns.cloudflare.com"];
        domain.DnsProvider = DetectedDnsProvider.Cloudflare;
        domain.DnsProviderCheckedUtc = Now;

        Assert.Empty(Raised(Run(domain, states)));
        domain.DnsProviderCheckedUtc = Now.AddMinutes(16);
        var raised = Assert.Single(Run(domain, states, now: Now.AddMinutes(20)), action => action.Kind == DnsHealthActionKind.Raise);

        Assert.Equal(AlertTypes.NameserversChanged, raised.AlertType);
        Assert.Equal("Info", raised.Severity);
        Assert.Contains("ns1.example.net", raised.Message);
        Assert.Contains("anna.ns.cloudflare.com", raised.Message);
        Assert.Contains("Cloudflare", raised.Message);

        var changedBack = Run(HealthyDomain(), states, now: Now.AddMinutes(30));
        Assert.True(Resolves(changedBack, AlertTypes.NameserversChanged));
        Assert.Null(State(states, DnsHealthItems.Nameservers).PendingSinceUtc);
    }

    [Fact]
    public void NoNameserversDetected_LeavesTheBaselineAlone()
    {
        var states = StatesAfterAHealthyCycle();
        var domain = HealthyDomain();
        domain.DnsNameservers = [];

        var actions = Run(domain, states);

        Assert.DoesNotContain(actions, action => action.AlertType == AlertTypes.NameserversChanged);
        Assert.Equal("ns1.example.net;ns2.example.net", State(states, DnsHealthItems.Nameservers).Baseline);
    }

    [Fact]
    public void NameserverKey_IsLowerCaseSortedAndWithoutTrailingDots_OrNullWhenEmpty()
    {
        Assert.Equal("a.example;b.example", DnsHealthAlertEvaluator.NameserverKey(["B.example.", " a.example ", "b.example"]));
        Assert.Null(DnsHealthAlertEvaluator.NameserverKey([]));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertTypesTests|FullyQualifiedName~DnsHealthAlertEvaluatorTests" -nologo -v q`
Expected: build FAILS, `DnsHealthItems`, `DnsHealthChecks`, `DnsHealthAlertEvaluator` and the new `AlertTypes` members not found.

- [ ] **Step 3: Add the alert types**

In `src/DotMarc/Notifications/AlertTypes.cs`, add the constants after `UnexpectedActivityOnNullRoutedDomain`:

```csharp
    public const string DmarcRecordBroken = "DmarcRecordBroken";
    public const string DmarcAuthorizationBroken = "DmarcAuthorizationBroken";
    public const string TlsrptRecordBroken = "TlsrptRecordBroken";
    public const string SpfRecordBroken = "SpfRecordBroken";
    public const string MxRecordBroken = "MxRecordBroken";
    public const string DkimRecordBroken = "DkimRecordBroken";
    public const string MtaStsFailing = "MtaStsFailing";
    public const string DmarcPolicyWeakened = "DmarcPolicyWeakened";
    public const string NameserversChanged = "NameserversChanged";
```

add these entries to the end of `All`:

```csharp
        new(DmarcRecordBroken, "DMARC record broken", "The domain's DMARC record is missing or broken."),
        new(DmarcAuthorizationBroken, "DMARC authorization record broken", "The record that lets reports for this domain go to dotMARC's mailbox is missing."),
        new(TlsrptRecordBroken, "TLS-RPT record broken", "The domain's TLS reporting record is missing or broken."),
        new(SpfRecordBroken, "SPF record broken", "The domain's SPF record is missing, duplicated or broken."),
        new(MxRecordBroken, "MX broken", "The domain has no MX record, or its mail server's name doesn't resolve."),
        new(DkimRecordBroken, "DKIM broken", "A DKIM selector set for the domain is missing or broken."),
        new(MtaStsFailing, "MTA-STS failing", "dotMARC stopped being able to serve the domain's MTA-STS policy."),
        new(DmarcPolicyWeakened, "DMARC policy weakened", "The domain's DMARC policy, subdomain policy or percentage went down."),
        new(NameserversChanged, "Nameservers changed", "The domain's nameservers changed, often the start of a DNS migration.", CreatesTicketByDefault: false),
```

and after `All`, add:

```csharp
    /// <summary>The DNS health alert types (see DnsHealthAlertEvaluator), which close themselves when the domain stops
    /// being monitored.</summary>
    public static IReadOnlyList<string> DnsHealth { get; } =
    [
        DmarcRecordBroken, DmarcAuthorizationBroken, TlsrptRecordBroken, SpfRecordBroken, MxRecordBroken, DkimRecordBroken,
        MtaStsFailing, DmarcPolicyWeakened, NameserversChanged
    ];
```

- [ ] **Step 4: Add the items and checks**

Create `src/DotMarc/Notifications/DnsHealthItems.cs`:

```csharp
namespace DotMarc.Notifications;

/// <summary>What the DNS health alerts watch, as stored in DomainAlertState.Item. Stored, so the values never change.</summary>
public static class DnsHealthItems
{
    public const string Dmarc = "Dmarc";
    public const string DmarcAuthorization = "DmarcAuthorization";
    public const string Tlsrpt = "Tlsrpt";
    public const string Spf = "Spf";
    public const string Mx = "Mx";
    public const string Dkim = "Dkim";
    public const string MtaSts = "MtaSts";
    public const string DmarcPolicy = "DmarcPolicy";
    public const string Nameservers = "Nameservers";
}
```

Create `src/DotMarc/Notifications/DnsHealthChecks.cs`:

```csharp
using DotMarc.Data;

namespace DotMarc.Notifications;

public enum DnsCheckHealth
{
    Passing,
    Failing,

    /// <summary>Not checked yet, not applicable or not set up: never alerts.</summary>
    Ignored
}

/// <summary>One of the seven DNS health checks, as the alerts see it: which status passes or fails, when it last
/// ran, and which setting controls it.</summary>
public sealed record DnsHealthCheck(
    string Item,
    string AlertType,
    string Label,
    Func<Domain, DnsCheckHealth> Health,
    Func<Domain, DateTimeOffset?> CheckedUtc,
    Func<Domain, string> Status,
    Func<Domain, string?> Detail,
    Func<NotificationSettings, DnsHealthAlertMode> Mode);

public static class DnsHealthChecks
{
    public static IReadOnlyList<DnsHealthCheck> All { get; } =
    [
        new(DnsHealthItems.Dmarc, AlertTypes.DmarcRecordBroken, "DMARC record",
            domain => domain.DmarcCheckStatus switch
            {
                DmarcCheckStatus.Ok => DnsCheckHealth.Passing,
                DmarcCheckStatus.NotChecked => DnsCheckHealth.Ignored,
                _ => DnsCheckHealth.Failing
            },
            domain => domain.DmarcCheckedUtc, domain => domain.DmarcCheckStatus.ToString(), domain => domain.DmarcCheckDetail,
            settings => settings.DmarcAlertMode),
        new(DnsHealthItems.DmarcAuthorization, AlertTypes.DmarcAuthorizationBroken, "DMARC authorization record",
            domain => domain.DmarcAuthorizationCheckStatus switch
            {
                DmarcAuthorizationCheckStatus.Ok => DnsCheckHealth.Passing,
                DmarcAuthorizationCheckStatus.Missing => DnsCheckHealth.Failing,
                _ => DnsCheckHealth.Ignored
            },
            domain => domain.DmarcAuthorizationCheckedUtc, domain => domain.DmarcAuthorizationCheckStatus.ToString(), domain => domain.DmarcAuthorizationCheckDetail,
            settings => settings.DmarcAuthorizationAlertMode),
        new(DnsHealthItems.Tlsrpt, AlertTypes.TlsrptRecordBroken, "TLS-RPT record",
            domain => domain.TlsrptCheckStatus switch
            {
                TlsrptCheckStatus.Ok => DnsCheckHealth.Passing,
                TlsrptCheckStatus.NotChecked => DnsCheckHealth.Ignored,
                _ => DnsCheckHealth.Failing
            },
            domain => domain.TlsrptCheckedUtc, domain => domain.TlsrptCheckStatus.ToString(), domain => domain.TlsrptCheckDetail,
            settings => settings.TlsrptAlertMode),
        new(DnsHealthItems.Spf, AlertTypes.SpfRecordBroken, "SPF record",
            domain => domain.SpfCheckStatus switch
            {
                // A null SPF record (-all only) is the healthy state for a domain that sends no mail.
                SpfCheckStatus.Ok or SpfCheckStatus.NullSpf => DnsCheckHealth.Passing,
                SpfCheckStatus.NotChecked => DnsCheckHealth.Ignored,
                _ => DnsCheckHealth.Failing
            },
            domain => domain.SpfCheckedUtc, domain => domain.SpfCheckStatus.ToString(), domain => domain.SpfCheckDetail,
            settings => settings.SpfAlertMode),
        new(DnsHealthItems.Mx, AlertTypes.MxRecordBroken, "MX",
            domain => domain.MxCheckStatus switch
            {
                MxCheckStatus.Ok or MxCheckStatus.NullMx => DnsCheckHealth.Passing,
                MxCheckStatus.NotChecked => DnsCheckHealth.Ignored,
                _ => DnsCheckHealth.Failing
            },
            domain => domain.MxCheckedUtc, domain => domain.MxCheckStatus.ToString(), domain => domain.MxCheckDetail,
            settings => settings.MxAlertMode),
        new(DnsHealthItems.Dkim, AlertTypes.DkimRecordBroken, "DKIM",
            domain => domain.DkimCheckStatus switch
            {
                DkimCheckStatus.Ok => DnsCheckHealth.Passing,
                DkimCheckStatus.NotConfigured => DnsCheckHealth.Ignored,
                _ => DnsCheckHealth.Failing
            },
            domain => domain.DkimCheckedUtc, domain => domain.DkimCheckStatus.ToString(), domain => domain.DkimCheckDetail,
            settings => settings.DkimAlertMode),
        new(DnsHealthItems.MtaSts, AlertTypes.MtaStsFailing, "MTA-STS",
            domain => !domain.MtaStsEnabled
                ? DnsCheckHealth.Ignored
                : domain.MtaStsStatus switch
                {
                    MtaStsStatus.Active => DnsCheckHealth.Passing,
                    MtaStsStatus.Failed => DnsCheckHealth.Failing,
                    // Still being set up: not a failure yet.
                    _ => DnsCheckHealth.Ignored
                },
            domain => domain.MtaStsCheckedUtc, domain => domain.MtaStsStatus.ToString(), domain => domain.MtaStsCheckDetail,
            settings => settings.MtaStsAlertMode),
    ];

    public static DnsHealthCheck For(string item) => All.Single(check => check.Item == item);
}
```

- [ ] **Step 5: Write the evaluator**

Create `src/DotMarc/Notifications/DnsHealthAlertEvaluator.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.Notifications;

public enum DnsHealthActionKind
{
    Raise,
    Resolve
}

/// <summary>What the evaluator wants done: raise an alert (through AlertingService.EnsureAlertAsync, which
/// de-duplicates and applies the cooldown) or resolve one.</summary>
public sealed record DnsHealthAlertAction(DnsHealthActionKind Kind, string AlertType, string Severity = "", string Title = "", string Message = "")
{
    public static DnsHealthAlertAction Resolve(string alertType) => new(DnsHealthActionKind.Resolve, alertType);
}

/// <summary>Decides, for one monitored domain, which DNS health alerts to raise or resolve, from its stored check
/// results and the state remembered from earlier cycles. No database or clock of its own: it updates the states it's
/// given (adding any that are missing) and returns the actions. See
/// docs/superpowers/specs/2026-10-02-dns-health-alerts-design.md.</summary>
public static class DnsHealthAlertEvaluator
{
    /// <summary>How long after a first failure the confirmation recheck becomes due.</summary>
    public static readonly TimeSpan ConfirmationDelay = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<DnsHealthAlertAction> Evaluate(Domain domain, List<DomainAlertState> states, NotificationSettings settings, DateTimeOffset nowUtc)
    {
        var actions = new List<DnsHealthAlertAction>();
        foreach (var check in DnsHealthChecks.All)
        {
            EvaluateCheck(check, domain, StateFor(states, domain, check.Item), settings, nowUtc, actions);
        }

        EvaluatePolicy(domain, StateFor(states, domain, DnsHealthItems.DmarcPolicy), settings, nowUtc, actions);
        EvaluateNameservers(domain, StateFor(states, domain, DnsHealthItems.Nameservers), settings, nowUtc, actions);
        return actions;
    }

    /// <summary>A set of nameservers in one comparable form: lower case, no trailing dot, sorted, ";"-joined. Null
    /// when there are none.</summary>
    public static string? NameserverKey(IEnumerable<string> nameservers)
    {
        var names = nameservers
            .Select(nameserver => nameserver.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(nameserver => nameserver.Length > 0)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        return names.Count == 0 ? null : string.Join(';', names);
    }

    private static void EvaluateCheck(DnsHealthCheck check, Domain domain, DomainAlertState state, NotificationSettings settings, DateTimeOffset nowUtc, List<DnsHealthAlertAction> actions)
    {
        var mode = check.Mode(settings);
        switch (check.Health(domain))
        {
            case DnsCheckHealth.Passing:
                state.HasPassed = true;
                ClearPending(state);
                actions.Add(DnsHealthAlertAction.Resolve(check.AlertType));
                return;
            case DnsCheckHealth.Ignored:
                ClearPending(state);
                actions.Add(DnsHealthAlertAction.Resolve(check.AlertType));
                return;
        }

        if (mode == DnsHealthAlertMode.Off)
        {
            ClearPending(state);
            actions.Add(DnsHealthAlertAction.Resolve(check.AlertType));
            return;
        }

        if (mode == DnsHealthAlertMode.WhenItBreaks && !state.HasPassed)
        {
            // Never passed, so it hasn't broken: a domain that was never set up stays quiet.
            ClearPending(state);
            return;
        }

        if (IsConfirmed(state, check.CheckedUtc(domain), nowUtc))
        {
            var detail = check.Detail(domain) is { Length: > 0 } text ? $" ({text})" : "";
            var history = state.HasPassed ? " It was passing before." : "";
            actions.Add(Raise(check.AlertType, "Warning",
                $"The {check.Label} check for {domain.Name} is failing: {Words(check.Status(domain))}{detail}.{history}"));
        }
    }

    private static void EvaluatePolicy(Domain domain, DomainAlertState state, NotificationSettings settings, DateTimeOffset nowUtc, List<DnsHealthAlertAction> actions)
    {
        if (!settings.DmarcPolicyWeakenedEnabled)
        {
            ClearPending(state);
            actions.Add(DnsHealthAlertAction.Resolve(AlertTypes.DmarcPolicyWeakened));
            return;
        }

        if (DmarcPolicyTags.Of(domain) is not { } current)
        {
            // No record: that's the DMARC check's alert. Leave this one as it is until the record is back.
            ClearPending(state);
            return;
        }

        if (DmarcPolicyTags.Parse(state.Baseline) is not { } baseline)
        {
            state.Baseline = current.Format();
            ClearPending(state);
            return;
        }

        if (!current.IsWeakerThan(baseline))
        {
            state.Baseline = current.Format();
            ClearPending(state);
            actions.Add(DnsHealthAlertAction.Resolve(AlertTypes.DmarcPolicyWeakened));
            return;
        }

        if (IsConfirmed(state, domain.DmarcCheckedUtc, nowUtc))
        {
            actions.Add(Raise(AlertTypes.DmarcPolicyWeakened, "Warning",
                $"The DMARC policy for {domain.Name} went from {baseline.Format()} to {current.Format()}."));
        }
    }

    private static void EvaluateNameservers(Domain domain, DomainAlertState state, NotificationSettings settings, DateTimeOffset nowUtc, List<DnsHealthAlertAction> actions)
    {
        if (!settings.NameserversChangedEnabled)
        {
            ClearPending(state);
            actions.Add(DnsHealthAlertAction.Resolve(AlertTypes.NameserversChanged));
            return;
        }

        if (NameserverKey(domain.DnsNameservers) is not { } current)
        {
            ClearPending(state);
            return;
        }

        if (state.Baseline is null)
        {
            state.Baseline = current;
            ClearPending(state);
            return;
        }

        if (current == state.Baseline)
        {
            ClearPending(state);
            actions.Add(DnsHealthAlertAction.Resolve(AlertTypes.NameserversChanged));
            return;
        }

        if (IsConfirmed(state, domain.DnsProviderCheckedUtc, nowUtc))
        {
            actions.Add(Raise(AlertTypes.NameserversChanged, "Info",
                $"The nameservers for {domain.Name} changed from {state.Baseline.Replace(";", ", ")} to {current.Replace(";", ", ")}. The DNS provider is now {domain.DnsProvider}."));
        }
    }

    /// <summary>Starts a pending failure if there isn't one. True once the check has run again at or after the
    /// recheck time, so a one-off DNS blip never alerts.</summary>
    private static bool IsConfirmed(DomainAlertState state, DateTimeOffset? checkedUtc, DateTimeOffset nowUtc)
    {
        if (state.PendingSinceUtc is null)
        {
            state.PendingSinceUtc = nowUtc;
            state.RecheckDueUtc = nowUtc + ConfirmationDelay;
            return false;
        }

        return checkedUtc is { } lastChecked && state.RecheckDueUtc is { } due && lastChecked >= due;
    }

    private static void ClearPending(DomainAlertState state)
    {
        state.PendingSinceUtc = null;
        state.RecheckDueUtc = null;
    }

    private static DomainAlertState StateFor(List<DomainAlertState> states, Domain domain, string item)
    {
        var state = states.FirstOrDefault(candidate => candidate.Item == item);
        if (state is null)
        {
            state = new DomainAlertState { DomainId = domain.Id, Item = item };
            states.Add(state);
        }

        return state;
    }

    private static DnsHealthAlertAction Raise(string alertType, string severity, string message) =>
        new(DnsHealthActionKind.Raise, alertType, severity, AlertTypes.Find(alertType)!.DisplayName, message);

    /// <summary>"MissingRecord" as "missing record".</summary>
    private static string Words(string status) =>
        string.Concat(status.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? " " + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertTypesTests|FullyQualifiedName~DnsHealthAlertEvaluatorTests|FullyQualifiedName~AlertTicketPolicyTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/DotMarc/Notifications test/DotMarc.Tests/Notifications
git commit -m "Add the DNS health alert types and the evaluator that decides when they fire"
```

---

### Task 4: Running the evaluator in the alert monitor

**Files:**
- Modify: `src/DotMarc/Notifications/AlertingService.cs`
- Create: `test/DotMarc.Tests/Internal/FakeAlertWebhookClient.cs`, `test/DotMarc.Tests/Internal/NoOpHaloPsaClient.cs`
- Modify: `test/DotMarc.Tests/Notifications/AlertingServiceTests.cs` (use the shared fakes)
- Test: `test/DotMarc.Tests/Notifications/DnsHealthAlertingTests.cs`

**Interfaces:**
- Consumes: Task 3's `DnsHealthAlertEvaluator.Evaluate`, `DnsHealthAlertAction`, `AlertTypes.DnsHealth`; Task 1's `DomainAlertStates`.
- Produces: `AlertingService.CheckPinnedDomainsAsync` also evaluates DNS health for every monitored domain and resolves DNS health alerts for domains no longer monitored. Test fakes `FakeAlertWebhookClient` (with `int CallCount`) and `NoOpHaloPsaClient` in `DotMarc.Tests.Internal`.

- [ ] **Step 1: Share the test fakes**

Create `test/DotMarc.Tests/Internal/FakeAlertWebhookClient.cs`:

```csharp
using DotMarc.Notifications;

namespace DotMarc.Tests.Internal;

internal sealed class FakeAlertWebhookClient : IAlertWebhookClient
{
    public int CallCount { get; private set; }
    public List<(string DomainName, string AlertType)> Sent { get; } = [];

    public Task SendAlertAsync(NotificationSettings settings, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
    {
        CallCount++;
        Sent.Add((domainName, alertType));
        return Task.CompletedTask;
    }
}
```

Create `test/DotMarc.Tests/Internal/NoOpHaloPsaClient.cs` with the body of the `NoOpHaloPsaClient` nested class from `AlertingServiceTests` (same eight members), as `internal sealed class NoOpHaloPsaClient : IHaloPsaClient` in namespace `DotMarc.Tests.Internal` with `using DotMarc.Notifications;`. Then delete the nested `FakeAlertWebhookClient` and `NoOpHaloPsaClient` classes from `AlertingServiceTests` (it already has `using DotMarc.Tests.Internal;`, so its uses now resolve to the shared ones).

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertingServiceTests" -nologo -v q`
Expected: PASS (the move changes nothing).

- [ ] **Step 2: Write the failing tests**

Create `test/DotMarc.Tests/Notifications/DnsHealthAlertingTests.cs` with the usual Postgres boilerplate (copy from `AlertingServiceTests`: fields, constructor, `InitializeAsync`, `DisposeAsync`, `CreateContext()`), then:

```csharp
    private async Task EnableAlertsAsync()
    {
        await using var context = CreateContext();
        var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
        settings.Enabled = true;
        settings.TeamsWebhookUrl = "https://example.test/webhook";
        await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
    }

    private async Task<int> SeedDomainAsync(string name, Action<Domain>? configure = null)
    {
        await using var context = CreateContext();
        var checkedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        var domain = new Domain
        {
            Name = name,
            IsMonitored = true,
            FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-30),
            LastReportReceivedUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcCheckedUtc = checkedUtc,
            SpfCheckStatus = SpfCheckStatus.Ok, SpfCheckedUtc = checkedUtc,
            MxCheckStatus = MxCheckStatus.Ok, MxCheckedUtc = checkedUtc,
            DmarcPolicy = DmarcPolicyLevel.Reject, DmarcSubdomainPolicy = DmarcPolicyLevel.Reject, DmarcPercent = 100,
            DnsNameservers = ["ns1.example.net", "ns2.example.net"], DnsProviderCheckedUtc = checkedUtc,
        };
        configure?.Invoke(domain);
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return domain.Id;
    }

    private AlertingService CreateService(FakeAlertWebhookClient notifier) =>
        new(new FakeDbContextFactory(_connectionString), notifier, new PsaTicketService(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);

    [Fact]
    public async Task FirstCycle_OnExistingDomains_RaisesNothing()
    {
        await EnableAlertsAsync();
        await SeedDomainAsync("healthy.example");
        await SeedDomainAsync("half-configured.example", domain =>
        {
            domain.SpfCheckStatus = SpfCheckStatus.MissingRecord;
            domain.TlsrptCheckStatus = TlsrptCheckStatus.MissingOwnRecord;
            domain.TlsrptCheckedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        });
        var notifier = new FakeAlertWebhookClient();

        await CreateService(notifier).CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.Empty(verify.AlertEvents);
        Assert.Equal(0, notifier.CallCount);
        Assert.Equal(18, await verify.DomainAlertStates.CountAsync());
    }

    [Fact]
    public async Task ABrokenCheck_RaisesOneAlertAfterItsRecheck_AndResolvesWhenItPasses()
    {
        await EnableAlertsAsync();
        var domainId = await SeedDomainAsync("contoso.example");
        var notifier = new FakeAlertWebhookClient();
        var service = CreateService(notifier);
        await service.CheckPinnedDomainsAsync();

        await using (var context = CreateContext())
        {
            var domain = await context.Domains.SingleAsync();
            domain.SpfCheckStatus = SpfCheckStatus.MissingRecord;
            domain.SpfCheckedUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        await service.CheckPinnedDomainsAsync();
        await using (var context = CreateContext())
        {
            Assert.Empty(context.AlertEvents);
            var state = await context.DomainAlertStates.SingleAsync(candidate => candidate.DomainId == domainId && candidate.Item == DnsHealthItems.Spf);
            Assert.NotNull(state.RecheckDueUtc);

            // As if 15 minutes passed and polling rechecked SPF, which still fails.
            state.RecheckDueUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            (await context.Domains.SingleAsync()).SpfCheckedUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        await service.CheckPinnedDomainsAsync();
        await service.CheckPinnedDomainsAsync();
        await using (var context = CreateContext())
        {
            var alert = await context.AlertEvents.SingleAsync();
            Assert.Equal((AlertTypes.SpfRecordBroken, "contoso.example", "Warning", false), (alert.AlertType, alert.DomainName, alert.Severity, alert.IsResolved));
            Assert.Equal(1, notifier.CallCount);

            (await context.Domains.SingleAsync()).SpfCheckStatus = SpfCheckStatus.Ok;
            await context.SaveChangesAsync();
        }

        await service.CheckPinnedDomainsAsync();
        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync()).IsResolved);
    }

    [Fact]
    public async Task AlertsForADomainNoLongerMonitored_AreResolved()
    {
        await EnableAlertsAsync();
        await SeedDomainAsync("old.example", domain => domain.IsMonitored = false);
        await using (var context = CreateContext())
        {
            context.AlertEvents.Add(new AlertEvent
            {
                DomainName = "old.example", AlertType = AlertTypes.SpfRecordBroken, Severity = "Warning", Title = "SPF record broken", Message = "m"
            });
            await context.SaveChangesAsync();
        }

        await CreateService(new FakeAlertWebhookClient()).CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync()).IsResolved);
    }

    [Fact]
    public async Task NothingIsEvaluated_WhenAlertsAreDisabled()
    {
        await SeedDomainAsync("contoso.example");
        await using (var context = CreateContext())
        {
            var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
            settings.Enabled = false;
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
        }

        await CreateService(new FakeAlertWebhookClient()).CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.Empty(verify.DomainAlertStates);
    }
```

(Needed usings: `DotMarc.Data`, `DotMarc.Notifications`, `DotMarc.Tests.Internal`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.Logging.Abstractions`, `Xunit`.)

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsHealthAlertingTests" -nologo -v q`
Expected: `FirstCycle_OnExistingDomains_RaisesNothing` FAILS (no state rows saved), `ABrokenCheck_...` and `AlertsForADomainNoLongerMonitored_...` FAIL; `NothingIsEvaluated_WhenAlertsAreDisabled` passes already (the early return exists).

- [ ] **Step 4: Run the evaluator from the monitor**

In `AlertingService.CheckPinnedDomainsAsync`, after the closing brace of the `foreach (var domain in domains)` loop, add:

```csharp
        await CheckDnsHealthAsync(db, settings, domains, cancellationToken).ConfigureAwait(false);
```

and add this method to `AlertingService`:

```csharp
    /// <summary>Runs DnsHealthAlertEvaluator for every monitored domain, saves what it remembers, then raises and
    /// resolves alerts. Resolves are only sent for alerts that are open, so a quiet cycle costs one query, not one per
    /// domain per check.</summary>
    private async Task CheckDnsHealthAsync(DotMarcDbContext db, NotificationSettings settings, List<Domain> domains, CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var domainIds = domains.Select(domain => domain.Id).ToList();
        var states = await db.DomainAlertStates
            .Where(state => domainIds.Contains(state.DomainId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var openAlerts = (await db.AlertEvents
                .Where(alert => !alert.IsResolved && AlertTypes.DnsHealth.Contains(alert.AlertType))
                .Select(alert => new { alert.DomainName, alert.AlertType })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(alert => (alert.DomainName, alert.AlertType))
            .ToHashSet();

        var actionsByDomain = new List<(Domain Domain, IReadOnlyList<DnsHealthAlertAction> Actions)>();
        foreach (var domain in domains)
        {
            var domainStates = states.Where(state => state.DomainId == domain.Id).ToList();
            actionsByDomain.Add((domain, DnsHealthAlertEvaluator.Evaluate(domain, domainStates, settings, nowUtc)));
            db.DomainAlertStates.AddRange(domainStates.Where(state => state.Id == 0));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (domain, actions) in actionsByDomain)
        {
            foreach (var action in actions)
            {
                if (action.Kind == DnsHealthActionKind.Raise)
                {
                    await EnsureAlertAsync(db, settings, domain.Name, action.AlertType, action.Severity, action.Title, action.Message, cancellationToken).ConfigureAwait(false);
                }
                else if (openAlerts.Contains((domain.Name, action.AlertType)))
                {
                    await ResolveAlertAsync(domain.Name, action.AlertType, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // A domain that stops being monitored isn't evaluated any more, so close what it left open.
        var monitoredNames = domains.Select(domain => domain.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (domainName, alertType) in openAlerts.Where(alert => !monitoredNames.Contains(alert.DomainName)))
        {
            await ResolveAlertAsync(domainName, alertType, cancellationToken).ConfigureAwait(false);
        }
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsHealthAlertingTests|FullyQualifiedName~AlertingServiceTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/DotMarc/Notifications/AlertingService.cs test/DotMarc.Tests
git commit -m "Raise and resolve DNS health alerts on every alert monitor cycle"
```

---

### Task 5: Confirmation rechecks in polling

**Files:**
- Modify: `src/DotMarc/Ingestion/PollingService.cs` (the stale-domain queries in `RunDmarcCheckCycleAsync`, `RunTlsrptCheckCycleAsync`, `RunDnsProviderCheckCycleAsync`, `RunDmarcAuthorizationCheckCycleAsync`, `RunSpfCheckCycleAsync`, `RunMxCheckCycleAsync`, `RunDkimCheckCycleAsync`)
- Test: `test/DotMarc.Tests/Ingestion/ConfirmationRecheckTests.cs`

**Interfaces:**
- Consumes: Task 1's `Domain.AlertStates` and `DomainAlertState.RecheckDueUtc`; Task 3's `DnsHealthItems`.
- Produces: each of those seven cycles also re-runs a domain whose state row for its item has `RecheckDueUtc <= now` while the check's last run is before `RecheckDueUtc`. (The MTA-STS cycle already re-runs a non-Active domain every 15 minutes, so it isn't changed.)

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Ingestion/ConfirmationRecheckTests.cs` with the usual Postgres boilerplate (copy from `SpfCheckCycleTests`: fields, constructor, `InitializeAsync`, `DisposeAsync`, `CreateContext()`, `CreateService(context)`), then:

```csharp
    private const string Mailbox = "rua.dmarc@mjco.uk";

    /// <summary>A domain whose checks all ran an hour ago, with a state row whose recheck became due a minute ago.</summary>
    private static async Task SeedDueRecheckAsync(DotMarcDbContext context, string item, DateTimeOffset? recheckDueUtc = null)
    {
        var anHourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        var domain = new Domain
        {
            Name = "contoso.io",
            FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-30),
            DmarcCheckedUtc = anHourAgo, TlsrptCheckedUtc = anHourAgo, DmarcAuthorizationCheckedUtc = anHourAgo,
            SpfCheckedUtc = anHourAgo, MxCheckedUtc = anHourAgo, DkimCheckedUtc = anHourAgo, DnsProviderCheckedUtc = anHourAgo,
            DkimSelectors = ["selector1"],
        };
        domain.AlertStates.Add(new DomainAlertState
        {
            Item = item,
            HasPassed = true,
            PendingSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-16),
            RecheckDueUtc = recheckDueUtc ?? DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task TheDmarcCycle_RechecksADueDmarcCheck()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Dmarc);
        var checker = new FakeDmarcDnsChecker();

        await CreateService(context).RunDmarcCheckCycleAsync(context, checker, Mailbox, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.CheckedDomains);
    }

    [Fact]
    public async Task TheDmarcCycle_RechecksADuePolicyChange()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.DmarcPolicy);
        var checker = new FakeDmarcDnsChecker();

        await CreateService(context).RunDmarcCheckCycleAsync(context, checker, Mailbox, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.CheckedDomains);
    }

    [Fact]
    public async Task TheDmarcAuthorizationCycle_RechecksADueCheck()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.DmarcAuthorization);
        var checker = new FakeDmarcDnsChecker();

        await CreateService(context).RunDmarcAuthorizationCheckCycleAsync(context, checker, Mailbox, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.AuthorizationCheckedDomains);
    }

    [Fact]
    public async Task TheTlsrptCycle_RechecksADueCheck()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Tlsrpt);
        var checker = new FakeTlsrptDnsChecker();

        await CreateService(context).RunTlsrptCheckCycleAsync(context, checker, Mailbox, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.CheckedDomains);
    }

    [Fact]
    public async Task TheSpfCycle_RechecksADueCheck()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Spf);
        var checker = new FakeSpfDnsChecker();

        await CreateService(context).RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.CheckedDomains);
    }

    [Fact]
    public async Task TheMxCycle_RechecksADueCheck()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Mx);
        var checker = new FakeMxDnsChecker();

        await CreateService(context).RunMxCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.CheckedDomains);
    }

    [Fact]
    public async Task TheDkimCycle_RechecksADueCheck()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Dkim);
        var checker = new FakeDkimDnsChecker();

        await CreateService(context).RunDkimCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Equal(["contoso.io"], checker.CheckedDomains);
    }

    [Fact]
    public async Task TheDnsProviderCycle_RechecksADueNameserverChange()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Nameservers);
        var detector = new FakeDnsProviderDetector();

        await CreateService(context).RunDnsProviderCheckCycleAsync(context, detector, CancellationToken.None);

        Assert.Equal(["contoso.io"], detector.CheckedDomains);
    }

    [Fact]
    public async Task ARecheckThatIsntDueYet_IsLeftForLater()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Spf, recheckDueUtc: DateTimeOffset.UtcNow.AddMinutes(10));
        var checker = new FakeSpfDnsChecker();

        await CreateService(context).RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);
    }

    [Fact]
    public async Task ARecheckThatAlreadyRan_IsntRunAgain()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Spf);
        var domain = await context.Domains.SingleAsync();
        domain.SpfCheckedUtc = DateTimeOffset.UtcNow.AddSeconds(-30);
        await context.SaveChangesAsync();
        var checker = new FakeSpfDnsChecker();

        await CreateService(context).RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);
    }

    [Fact]
    public async Task ADueRecheckForAnotherCheck_DoesntTriggerThisOne()
    {
        using var context = CreateContext();
        await SeedDueRecheckAsync(context, DnsHealthItems.Mx);
        var checker = new FakeSpfDnsChecker();

        await CreateService(context).RunSpfCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Empty(checker.CheckedDomains);
    }
```

(Needed usings: `DotMarc.Data`, `DotMarc.Ingestion`, `DotMarc.Notifications`, `DotMarc.Tests.Internal`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.Logging.Abstractions`, `Xunit`.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ConfirmationRecheckTests" -nologo -v q`
Expected: the eight "Rechecks" tests FAIL (nothing is checked); the three "isn't"/"doesn't" tests pass.

- [ ] **Step 3: Include due rechecks in each cycle's query**

In each of the seven cycle methods, the stale-domain query starts with `var cutoff = DateTimeOffset.UtcNow.AddHours(-24);`. Add `var nowUtc = DateTimeOffset.UtcNow;` after it, and extend the `Where`. For the SPF cycle:

```csharp
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            var nowUtc = DateTimeOffset.UtcNow;
            var staleDomains = await context.Domains
                // Also a domain whose DNS health alert is waiting for its confirmation recheck (see
                // DnsHealthAlertEvaluator): re-running the check now is what confirms or clears the failure.
                .Where(d => d.SpfCheckedUtc == null || d.SpfCheckedUtc < cutoff
                    || d.AlertStates.Any(state => state.Item == DnsHealthItems.Spf && state.RecheckDueUtc <= nowUtc && d.SpfCheckedUtc < state.RecheckDueUtc))
```

Make the same change in the other six, with these fields and items:

| Cycle | Checked field | Item condition |
|---|---|---|
| `RunDmarcCheckCycleAsync` | `DmarcCheckedUtc` | `(state.Item == DnsHealthItems.Dmarc \|\| state.Item == DnsHealthItems.DmarcPolicy)` |
| `RunTlsrptCheckCycleAsync` | `TlsrptCheckedUtc` | `state.Item == DnsHealthItems.Tlsrpt` |
| `RunDmarcAuthorizationCheckCycleAsync` | `DmarcAuthorizationCheckedUtc` | `state.Item == DnsHealthItems.DmarcAuthorization` |
| `RunMxCheckCycleAsync` | `MxCheckedUtc` | `state.Item == DnsHealthItems.Mx` |
| `RunDkimCheckCycleAsync` | `DkimCheckedUtc` | `state.Item == DnsHealthItems.Dkim` |
| `RunDnsProviderCheckCycleAsync` | `DnsProviderCheckedUtc` | `state.Item == DnsHealthItems.Nameservers` |

(The comment goes on the SPF query only; the others follow the same shape. The TLS-RPT query names its lambda parameter `domain`, not `d`; keep each query's own name.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ConfirmationRecheckTests|FullyQualifiedName~CheckCycleTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/DotMarc/Ingestion/PollingService.cs test/DotMarc.Tests/Ingestion/ConfirmationRecheckTests.cs
git commit -m "Recheck a failing DNS check within minutes so its alert can be confirmed"
```

---

### Task 6: Acknowledging policy and nameserver alerts

**Files:**
- Create: `src/DotMarc/Notifications/DnsHealthBaselines.cs`
- Create: `src/DotMarc/Notifications/AlertAcknowledgement.cs`
- Modify: `src/DotMarc/Audit/AuditActions.cs`, `src/DotMarc/Audit/AuditTarget.cs`
- Modify: `src/DotMarc/Notifications/AlertingService.cs` (auto-close)
- Modify: `src/DotMarc/Program.cs` (Halo webhook)
- Test: `test/DotMarc.Tests/Notifications/AlertAcknowledgementTests.cs`, `test/DotMarc.Tests/Notifications/HaloWebhookEndpointTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 4.
- Produces: `DnsHealthBaselines.AcceptCurrentAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken)` (no-op for other alert types; doesn't save); `enum AcknowledgeOutcome { Acknowledged, AcknowledgedButTicketNotClosed, NotAcknowledgeable }`; `AlertAcknowledgement.IsAcknowledgeable(string alertType)` and `AlertAcknowledgement.AcknowledgeAsync(DotMarcDbContext context, AuditActor actor, int alertId, IPsaTicketService psaTicketService, CancellationToken cancellationToken = default)`; `AuditActions.AlertAcknowledged = "alert.acknowledged"`; `AuditTarget.For(AlertEvent alert)`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Notifications/AlertAcknowledgementTests.cs` with the usual Postgres boilerplate (copy from `AlertingServiceTests`), then:

```csharp
    private async Task<int> SeedPolicyAlertAsync(string ticketId = "777", bool secondOpenCopy = false)
    {
        await using var context = CreateContext();
        var domain = new Domain
        {
            Name = "contoso.example", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-30), LastReportReceivedUtc = DateTimeOffset.UtcNow,
            DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcCheckedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            DmarcPolicy = DmarcPolicyLevel.Quarantine, DmarcSubdomainPolicy = DmarcPolicyLevel.Quarantine, DmarcPercent = 100,
        };
        domain.AlertStates.Add(new DomainAlertState
        {
            Item = DnsHealthItems.DmarcPolicy, Baseline = "p=reject; sp=reject; pct=100",
            PendingSinceUtc = DateTimeOffset.UtcNow.AddHours(-2), RecheckDueUtc = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(15),
        });
        context.Domains.Add(domain);
        var alert = NewPolicyAlert(ticketId);
        context.AlertEvents.Add(alert);
        if (secondOpenCopy)
        {
            context.AlertEvents.Add(NewPolicyAlert("778"));
        }

        await context.SaveChangesAsync();
        return alert.Id;
    }

    private static AlertEvent NewPolicyAlert(string ticketId) => new()
    {
        DomainName = "contoso.example", AlertType = AlertTypes.DmarcPolicyWeakened, Severity = "Warning", Title = "DMARC policy weakened",
        Message = "m", ExternalTicketProvider = "HaloPSA", ExternalTicketId = ticketId,
    };

    [Fact]
    public async Task Acknowledge_ClosesEveryOpenCopy_AcceptsThePolicy_ClosesTheTickets_AndIsAudited()
    {
        var alertId = await SeedPolicyAlertAsync(secondOpenCopy: true);
        var halo = new ClosingHaloPsaClient();

        AcknowledgeOutcome outcome;
        await using (var context = CreateContext())
        {
            outcome = await AlertAcknowledgement.AcknowledgeAsync(context, TestActors.Admin, alertId, new PsaTicketService(halo));
        }

        Assert.Equal(AcknowledgeOutcome.Acknowledged, outcome);
        Assert.Equal(["777", "778"], halo.ClosedTicketIds.Order());
        await using var verify = CreateContext();
        Assert.All(await verify.AlertEvents.ToListAsync(), alert => Assert.True(alert.IsResolved));
        var state = await verify.DomainAlertStates.SingleAsync();
        Assert.Equal("p=quarantine; sp=quarantine; pct=100", state.Baseline);
        Assert.Null(state.PendingSinceUtc);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.AlertAcknowledged, entry.Action);
        Assert.Contains("contoso.example", entry.Summary);
    }

    [Fact]
    public async Task Acknowledge_AcceptsTheCurrentPolicy_SoTheNextCycleRaisesNothing()
    {
        var alertId = await SeedPolicyAlertAsync();
        await using (var context = CreateContext())
        {
            var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
            settings.TeamsWebhookUrl = "https://example.test/webhook";
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
            await AlertAcknowledgement.AcknowledgeAsync(context, TestActors.Admin, alertId, new PsaTicketService(new ClosingHaloPsaClient()));
        }

        var notifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), notifier, new PsaTicketService(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();
        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.Single(verify.AlertEvents);
        Assert.Equal(0, notifier.CallCount);
    }

    [Fact]
    public async Task Acknowledge_StillClosesTheAlert_WhenTheTicketCantBeClosed()
    {
        var alertId = await SeedPolicyAlertAsync();

        AcknowledgeOutcome outcome;
        await using (var context = CreateContext())
        {
            outcome = await AlertAcknowledgement.AcknowledgeAsync(context, TestActors.Admin, alertId, new PsaTicketService(new ClosingHaloPsaClient { Fails = true }));
        }

        Assert.Equal(AcknowledgeOutcome.AcknowledgedButTicketNotClosed, outcome);
        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync()).IsResolved);
    }

    [Fact]
    public async Task ACheckAlert_CantBeAcknowledged()
    {
        int alertId;
        await using (var context = CreateContext())
        {
            var alert = new AlertEvent { DomainName = "contoso.example", AlertType = AlertTypes.SpfRecordBroken, Severity = "Warning", Title = "SPF record broken", Message = "m" };
            context.AlertEvents.Add(alert);
            await context.SaveChangesAsync();
            alertId = alert.Id;
        }

        await using (var context = CreateContext())
        {
            Assert.Equal(AcknowledgeOutcome.NotAcknowledgeable,
                await AlertAcknowledgement.AcknowledgeAsync(context, TestActors.Admin, alertId, new PsaTicketService(new ClosingHaloPsaClient())));
        }

        await using var verify = CreateContext();
        Assert.False((await verify.AlertEvents.SingleAsync()).IsResolved);
        Assert.Empty(verify.AuditEntries);
    }

    [Fact]
    public async Task AnOldPolicyAlert_IsClosedAutomatically_WhenAutoCloseIsOn()
    {
        var alertId = await SeedPolicyAlertAsync();
        await using (var context = CreateContext())
        {
            var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
            settings.TeamsWebhookUrl = "https://example.test/webhook";
            settings.AcknowledgeableAutoCloseDays = 3;
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
            (await context.AlertEvents.SingleAsync(alert => alert.Id == alertId)).CreatedUtc = DateTimeOffset.UtcNow.AddDays(-4);
            await context.SaveChangesAsync();
        }

        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), new PsaTicketService(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.True((await verify.AlertEvents.SingleAsync()).IsResolved);
        Assert.Equal("p=quarantine; sp=quarantine; pct=100", (await verify.DomainAlertStates.SingleAsync(state => state.Item == DnsHealthItems.DmarcPolicy)).Baseline);
        var entry = await verify.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.AlertAcknowledged);
        Assert.Equal(AuditActorKind.System, entry.ActorKind);
    }

    [Fact]
    public async Task ARecentPolicyAlert_IsLeftOpen_WhenAutoCloseIsOn()
    {
        await SeedPolicyAlertAsync();
        await using (var context = CreateContext())
        {
            var settings = await context.NotificationSettings.AsNoTracking().SingleAsync();
            settings.TeamsWebhookUrl = "https://example.test/webhook";
            settings.AcknowledgeableAutoCloseDays = 3;
            await NotificationSettingsService.SaveAsync(context, TestActors.Admin, settings);
        }

        var service = new AlertingService(new FakeDbContextFactory(_connectionString), new FakeAlertWebhookClient(), new PsaTicketService(new NoOpHaloPsaClient()), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();

        await using var verify = CreateContext();
        Assert.False((await verify.AlertEvents.SingleAsync()).IsResolved);
    }

    private sealed class ClosingHaloPsaClient : IHaloPsaClient
    {
        public bool Fails { get; init; }
        public List<string> ClosedTicketIds { get; } = [];

        public Task CloseTicketAsync(HaloPsaSettings settings, string ticketId, string note, CancellationToken cancellationToken = default)
        {
            if (Fails)
            {
                throw new HttpRequestException("Halo is down.");
            }

            ClosedTicketIds.Add(ticketId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HaloClient>> ListClientsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloClient>>([]);
        public Task<IReadOnlyList<HaloTicketType>> ListTicketTypesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketType>>([]);
        public Task<IReadOnlyList<HaloTicketStatus>> ListStatusesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloTicketStatus>>([]);
        public Task<IReadOnlyList<HaloPriority>> ListPrioritiesAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloPriority>>([]);
        public Task<IReadOnlyList<HaloAgent>> ListAgentsAsync(HaloPsaSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HaloAgent>>([]);
        public Task<int> GetTicketStatusAsync(HaloPsaSettings settings, int ticketId, CancellationToken cancellationToken = default) => Task.FromResult(9);
        public Task<string> CreateTicketAsync(HaloPsaSettings settings, int haloClientId, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default) => Task.FromResult("unused");
    }
```

(Needed usings: `DotMarc.Audit`, `DotMarc.Data`, `DotMarc.Notifications`, `DotMarc.Tests.Internal`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.Logging.Abstractions`, `Xunit`. If `AuditEntry` names the actor kind differently from `ActorKind`, use its name; check `src/DotMarc/Audit/AuditEntry.cs`.)

Add to `HaloWebhookEndpointTests`:

```csharp
    [Fact]
    public async Task ClosingAPolicyAlertsTicket_AcceptsTheCurrentPolicy()
    {
        await using (var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options))
        {
            var domain = new Domain
            {
                Name = "policy.example", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow,
                DmarcPolicy = DmarcPolicyLevel.None, DmarcSubdomainPolicy = DmarcPolicyLevel.None, DmarcPercent = 100,
            };
            domain.AlertStates.Add(new DomainAlertState { Item = DnsHealthItems.DmarcPolicy, Baseline = "p=reject; sp=reject; pct=100" });
            context.Domains.Add(domain);
            context.AlertEvents.Add(new AlertEvent
            {
                DomainName = "policy.example", AlertType = AlertTypes.DmarcPolicyWeakened, Severity = "Warning", Title = "t", Message = "m",
                ExternalTicketProvider = "HaloPSA", ExternalTicketId = "5151"
            });
            await context.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        await client.PostAsJsonAsync("/integrations/halopsa/webhook/the-webhook-secret", new { ticket_id = 5151, status_id = 9 });

        await using var verify = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        Assert.True((await verify.AlertEvents.SingleAsync(alert => alert.ExternalTicketId == "5151")).IsResolved);
        var domainId = (await verify.Domains.SingleAsync(candidate => candidate.Name == "policy.example")).Id;
        Assert.Equal("p=none; sp=none; pct=100", (await verify.DomainAlertStates.SingleAsync(state => state.DomainId == domainId)).Baseline);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertAcknowledgementTests|FullyQualifiedName~HaloWebhookEndpointTests" -nologo -v q`
Expected: build FAILS, `AlertAcknowledgement`, `AcknowledgeOutcome` and `AuditActions.AlertAcknowledged` not found.

- [ ] **Step 3: Add the audit action and target**

In `AuditActions`, after `TicketRuleGroupChanged`, add `public const string AlertAcknowledged = "alert.acknowledged";`, and in `All`, after `(TicketRuleGroupChanged, "Group ticket rule changed"),`, add `(AlertAcknowledged, "Alert acknowledged"),`.

In `AuditTarget`, add `using DotMarc.Notifications;` and:

```csharp
    public static AuditTarget For(AlertEvent alert) => new("Alert", IdText(alert.Id), alert.DomainName);
```

- [ ] **Step 4: Write the baselines and acknowledgement**

Create `src/DotMarc/Notifications/DnsHealthBaselines.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Dns;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class DnsHealthBaselines
{
    /// <summary>When a DMARC policy weakened or nameservers changed alert is closed by a person (Acknowledge, or
    /// closing its Halo ticket) or by auto-close, the change is accepted: the domain's current policy or nameservers
    /// become the baseline and any pending change is cleared, so the alert doesn't come straight back. Does nothing for
    /// other alert types. Doesn't save; the caller does.</summary>
    public static async Task AcceptCurrentAsync(DotMarcDbContext context, AlertEvent alert, CancellationToken cancellationToken)
    {
        var item = alert.AlertType switch
        {
            AlertTypes.DmarcPolicyWeakened => DnsHealthItems.DmarcPolicy,
            AlertTypes.NameserversChanged => DnsHealthItems.Nameservers,
            _ => null
        };
        if (item is null)
        {
            return;
        }

        var domain = await context.Domains.SingleOrDefaultAsync(candidate => candidate.Name == alert.DomainName, cancellationToken).ConfigureAwait(false);
        if (domain is null)
        {
            return;
        }

        var state = await context.DomainAlertStates
            .SingleOrDefaultAsync(candidate => candidate.DomainId == domain.Id && candidate.Item == item, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            state = new DomainAlertState { DomainId = domain.Id, Item = item };
            context.DomainAlertStates.Add(state);
        }

        var current = item == DnsHealthItems.DmarcPolicy
            ? DmarcPolicyTags.Of(domain)?.Format()
            : DnsHealthAlertEvaluator.NameserverKey(domain.DnsNameservers);
        state.Baseline = current ?? state.Baseline;
        state.PendingSinceUtc = null;
        state.RecheckDueUtc = null;
    }
}
```

Create `src/DotMarc/Notifications/AlertAcknowledgement.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public enum AcknowledgeOutcome
{
    Acknowledged,

    /// <summary>The alert is closed, but its Halo ticket couldn't be closed and needs closing by hand.</summary>
    AcknowledgedButTicketNotClosed,
    NotAcknowledgeable
}

/// <summary>Closes a DMARC policy weakened or nameservers changed alert, which may describe a deliberate change, and
/// accepts the current value. Check alerts close themselves when the check passes, so they can't be acknowledged.</summary>
public static class AlertAcknowledgement
{
    public static bool IsAcknowledgeable(string alertType) =>
        alertType is AlertTypes.DmarcPolicyWeakened or AlertTypes.NameserversChanged;

    public static async Task<AcknowledgeOutcome> AcknowledgeAsync(DotMarcDbContext context, AuditActor actor, int alertId, IPsaTicketService psaTicketService, CancellationToken cancellationToken = default)
    {
        var alert = await context.AlertEvents.SingleOrDefaultAsync(candidate => candidate.Id == alertId, cancellationToken).ConfigureAwait(false);
        if (alert is null || alert.IsResolved || !IsAcknowledgeable(alert.AlertType))
        {
            return AcknowledgeOutcome.NotAcknowledgeable;
        }

        // An alert left open past the cooldown is raised again as a new row, so close every open copy of it.
        var openCopies = await context.AlertEvents
            .Where(candidate => candidate.DomainName == alert.DomainName && candidate.AlertType == alert.AlertType && !candidate.IsResolved)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var resolvedUtc = DateTimeOffset.UtcNow;
        foreach (var copy in openCopies)
        {
            copy.IsResolved = true;
            copy.ResolvedUtc = resolvedUtc;
        }

        await DnsHealthBaselines.AcceptCurrentAsync(context, alert, cancellationToken).ConfigureAwait(false);
        AuditLog.Record(context, actor, AuditActions.AlertAcknowledged, AuditTarget.For(alert), $"Acknowledged \"{alert.Title}\" for {alert.DomainName}");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var ticketsClosed = true;
        foreach (var copy in openCopies)
        {
            try
            {
                await psaTicketService.CloseTicketAsync(context, copy, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ticketsClosed = false;
            }
        }

        return ticketsClosed ? AcknowledgeOutcome.Acknowledged : AcknowledgeOutcome.AcknowledgedButTicketNotClosed;
    }
}
```

- [ ] **Step 5: Auto-close, and accept on a Halo ticket close**

In `AlertingService.CheckDnsHealthAsync` (Task 4), at the very start, add:

```csharp
        await AutoCloseAcknowledgeableAlertsAsync(settings, cancellationToken).ConfigureAwait(false);
```

and add to `AlertingService`:

```csharp
    private static readonly AuditActor AutoCloseActor = AuditActor.ForSystem("Alert auto-close");

    /// <summary>With AcknowledgeableAutoCloseDays above 0, closes policy and nameserver alerts left open that long, as
    /// if acknowledged.</summary>
    private async Task AutoCloseAcknowledgeableAlertsAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        if (settings.AcknowledgeableAutoCloseDays <= 0)
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-settings.AcknowledgeableAutoCloseDays);
        var staleAlertIds = await db.AlertEvents
            .Where(alert => !alert.IsResolved && alert.CreatedUtc < cutoffUtc
                && (alert.AlertType == AlertTypes.DmarcPolicyWeakened || alert.AlertType == AlertTypes.NameserversChanged))
            .Select(alert => alert.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var alertId in staleAlertIds)
        {
            await using var alertContext = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var outcome = await AlertAcknowledgement.AcknowledgeAsync(alertContext, AutoCloseActor, alertId, _psaTicketService, cancellationToken).ConfigureAwait(false);
            if (outcome == AcknowledgeOutcome.AcknowledgedButTicketNotClosed)
            {
                _logger.LogWarning("Closed alert {AlertId} automatically, but couldn't close its PSA ticket.", alertId);
            }
        }
    }
```

(Add `using DotMarc.Audit;` to `AlertingService.cs`. An earlier copy of the same alert closed by a later id's acknowledgement returns `NotAcknowledgeable`, which is fine.)

In `src/DotMarc/Program.cs`, in the Halo webhook handler, change the block that resolves the alert to:

```csharp
    if (alert is not null)
    {
        alert.IsResolved = true;
        alert.ResolvedUtc = DateTimeOffset.UtcNow;
        // Closing a policy or nameserver alert's ticket accepts the change, the same as Acknowledge.
        await DnsHealthBaselines.AcceptCurrentAsync(context, alert, request.HttpContext.RequestAborted);
        await context.SaveChangesAsync();
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertAcknowledgementTests|FullyQualifiedName~HaloWebhookEndpointTests|FullyQualifiedName~DnsHealthAlertingTests|FullyQualifiedName~DotMarc.Tests.Audit" -nologo -v q`
Expected: PASS (the audit coverage test sees `AlertAcknowledged` in `All`).

- [ ] **Step 7: Commit**

```powershell
git add src/DotMarc/Notifications src/DotMarc/Audit src/DotMarc/Program.cs test/DotMarc.Tests
git commit -m "Let policy and nameserver alerts be acknowledged, closed in Halo or closed automatically"
```

---

### Task 7: Demo data

**Files:**
- Modify: `src/DotMarc/Demo/DemoDataset.cs`, `src/DotMarc/Demo/DemoDataGenerator.cs`, `src/DotMarc/Demo/DemoDataSeeder.cs`
- Test: `test/DotMarc.Tests/Demo/DemoDataGeneratorTests.cs`, `test/DotMarc.Tests/Demo/DemoDataSeederTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 3.
- Produces: `sealed record DemoAlertStateSeed(string DomainName, string Item, bool HasPassed, string? Baseline)`; `DemoDataset.AlertStates`; `DemoDomainSeed.DmarcPolicy`, `DmarcSubdomainPolicy`, `DmarcPercent` (optional, default null).

- [ ] **Step 1: Write the failing tests**

Add to `DemoDataGeneratorTests` (it has a `Generate()` helper):

```csharp
    [Theory]
    [InlineData("brightline-legal.example", "SpfRecordBroken")]
    [InlineData("aurora-retail.example", "DmarcPolicyWeakened")]
    [InlineData("northstar-nonprofit.example", "NameserversChanged")]
    public void AlertEvents_IncludeOpenDnsHealthAlerts(string domainName, string alertType)
    {
        var dataset = Generate();

        var alert = Assert.Single(dataset.AlertEvents, candidate => candidate.DomainName == domainName);
        Assert.Equal(alertType, alert.AlertType);
        Assert.False(alert.IsResolved);
    }

    [Fact]
    public void AlertStates_BackTheOpenDnsHealthAlerts_SoTheMonitorLeavesThemOpen()
    {
        var dataset = Generate();

        var spf = Assert.Single(dataset.AlertStates, state => state.DomainName == "brightline-legal.example");
        Assert.Equal(("Spf", true), (spf.Item, spf.HasPassed));
        Assert.Equal(SpfCheckStatus.MissingRecord, dataset.Domains.Single(domain => domain.Name == "brightline-legal.example").SpfCheckStatus);

        var policy = Assert.Single(dataset.AlertStates, state => state.DomainName == "aurora-retail.example");
        Assert.Equal(("DmarcPolicy", "p=reject; sp=reject; pct=100"), (policy.Item, policy.Baseline));
        Assert.Equal(DmarcPolicyLevel.Quarantine, dataset.Domains.Single(domain => domain.Name == "aurora-retail.example").DmarcPolicy);

        var nameservers = Assert.Single(dataset.AlertStates, state => state.DomainName == "northstar-nonprofit.example");
        Assert.Equal("Nameservers", nameservers.Item);
        Assert.Contains("cloudflare", nameservers.Baseline);
    }
```

Add to `DemoDataSeederTests`:

```csharp
    [Fact]
    public async Task ResetAsync_WritesTheAlertStatesAndDmarcPolicies()
    {
        using var context = CreateContext();

        await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);

        using var verify = CreateContext();
        Assert.Equal(3, await verify.DomainAlertStates.CountAsync());
        var aurora = await verify.Domains.SingleAsync(domain => domain.Name == "aurora-retail.example");
        Assert.Equal(DmarcPolicyLevel.Quarantine, aurora.DmarcPolicy);
        Assert.Single(await verify.DomainAlertStates.Where(state => state.DomainId == aurora.Id).ToListAsync());
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Demo" -nologo -v q`
Expected: build FAILS, `DemoDataset.AlertStates` not found.

- [ ] **Step 3: Extend the dataset**

In `DemoDataset.cs`, add a last parameter to `DemoDataset`: `List<DemoAlertStateSeed> AlertStates`; add three optional parameters at the end of `DemoDomainSeed`: `DmarcPolicyLevel? DmarcPolicy = null, DmarcPolicyLevel? DmarcSubdomainPolicy = null, int? DmarcPercent = null`; and add:

```csharp
/// <summary>A DNS health alert's remembered state for a demo domain (see DomainAlertState), so the alert monitor
/// leaves the demo's open DNS health alerts open rather than resolving them on its first cycle.</summary>
public sealed record DemoAlertStateSeed(string DomainName, string Item, bool HasPassed, string? Baseline);
```

- [ ] **Step 4: Generate the demo alerts and states**

In `DemoDataGenerator.Generate`, before `return new DemoDataset(`, give aurora-retail.example a weakened policy:

```csharp
        var auroraIndex = domains.FindIndex(domain => domain.Name == "aurora-retail.example");
        domains[auroraIndex] = domains[auroraIndex] with { DmarcPolicy = DmarcPolicyLevel.Quarantine, DmarcSubdomainPolicy = DmarcPolicyLevel.Quarantine, DmarcPercent = 100 };
```

and pass a seventh argument, `BuildAlertStates()`, to `new DemoDataset(...)`. Add:

```csharp
    /// <summary>The states behind the three open DNS health alerts in BuildAlertEvents: SPF that was passing before,
    /// a policy that used to be reject, and nameservers that used to be Cloudflare's.</summary>
    private static List<DemoAlertStateSeed> BuildAlertStates() =>
    [
        new("brightline-legal.example", DnsHealthItems.Spf, HasPassed: true, Baseline: null),
        new("aurora-retail.example", DnsHealthItems.DmarcPolicy, HasPassed: false, Baseline: "p=reject; sp=reject; pct=100"),
        new("northstar-nonprofit.example", DnsHealthItems.Nameservers, HasPassed: false,
            Baseline: DnsHealthAlertEvaluator.NameserverKey(DnsNameserverSamples[DetectedDnsProvider.Cloudflare])),
    ];
```

In `BuildAlertEvents`, add these three to the returned list (and update its doc comment to say six alerts):

```csharp
            new DemoAlertEventSeed(
                "brightline-legal.example", AlertTypes.SpfRecordBroken, "Warning", "SPF record broken",
                "The SPF record check for brightline-legal.example is failing: missing record (No SPF record found). It was passing before.",
                IsResolved: false, CreatedUtc: nowUtc.AddHours(-6), ResolvedUtc: null),

            new DemoAlertEventSeed(
                "aurora-retail.example", AlertTypes.DmarcPolicyWeakened, "Warning", "DMARC policy weakened",
                "The DMARC policy for aurora-retail.example went from p=reject; sp=reject; pct=100 to p=quarantine; sp=quarantine; pct=100.",
                IsResolved: false, CreatedUtc: nowUtc.AddHours(-3), ResolvedUtc: null),

            new DemoAlertEventSeed(
                "northstar-nonprofit.example", AlertTypes.NameserversChanged, "Info", "Nameservers changed",
                $"The nameservers for northstar-nonprofit.example changed from {string.Join(", ", DnsNameserverSamples[DetectedDnsProvider.Cloudflare])} to dns1.legacy-registrar.example, dns2.legacy-registrar.example. The DNS provider is now Unknown.",
                IsResolved: false, CreatedUtc: nowUtc.AddHours(-1), ResolvedUtc: null),
```

(Add `using DotMarc.Notifications;` to `DemoDataGenerator.cs`. If `brightline-legal.example`'s SPF detail in the generator isn't "No SPF record found", use its actual `spfCheckDetail` text in the message.)

- [ ] **Step 5: Write them in the seeder**

In `DemoDataSeeder.TruncateAllTablesAsync`, add `"DomainAlertStates"` to the list after `"AlertEvents"`. In `WriteAsync`, where each `Domain` is built from its seed, add `DmarcPolicy = domainSeed.DmarcPolicy, DmarcSubdomainPolicy = domainSeed.DmarcSubdomainPolicy, DmarcPercent = domainSeed.DmarcPercent,`. Before the `foreach (var alertSeed in dataset.AlertEvents)` loop, add:

```csharp
        foreach (var stateSeed in dataset.AlertStates)
        {
            var domain = context.Domains.Local.Single(candidate => candidate.Name == stateSeed.DomainName);
            domain.AlertStates.Add(new DomainAlertState { Item = stateSeed.Item, HasPassed = stateSeed.HasPassed, Baseline = stateSeed.Baseline });
        }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Demo|FullyQualifiedName~HaloWebhookEndpointTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/DotMarc/Demo test/DotMarc.Tests/Demo
git commit -m "Show open DNS health alerts in the demo"
```

---

### Task 8: Settings panel and the Acknowledge button

**Files:**
- Modify: `src/DotMarc/Components/Pages/AlertsSettings.razor`
- Modify: `src/DotMarc/Components/Pages/Alerts.razor`

**Interfaces:**
- Consumes: Task 1's settings fields and `DnsHealthAlertMode`; Task 6's `AlertAcknowledgement` and `AcknowledgeOutcome`.

There is no component test harness for these pages; they're checked in the demo in Step 4.

- [ ] **Step 1: Add the settings panel**

In `AlertsSettings.razor`, inside the notifications `<MudGrid>`, after the "Suspicious reject non-benign %" `MudItem`, add:

```razor
            <MudItem xs="12">
                <MudText Typo="Typo.h6" Class="mt-2">DNS health alerts</MudText>
                <MudText Typo="Typo.caption" Class="mud-text-secondary d-block">
                    Alert when a domain's DNS health check fails. "When it breaks" alerts only once the check has passed at least
                    once, so domains that were never set up stay quiet. A failure is rechecked about 15 minutes later before it alerts.
                </MudText>
            </MudItem>

            @foreach (var modeSetting in DnsHealthModeSettings)
            {
                <MudItem xs="12" sm="6" md="4">
                    <MudSelect T="DnsHealthAlertMode" Label="@modeSetting.Label" Variant="Variant.Outlined"
                               Value="modeSetting.Get(_settings)" ValueChanged="@(mode => modeSetting.Set(_settings!, mode))">
                        <MudSelectItem T="DnsHealthAlertMode" Value="DnsHealthAlertMode.WhenItBreaks">When it breaks</MudSelectItem>
                        <MudSelectItem T="DnsHealthAlertMode" Value="DnsHealthAlertMode.WheneverItFails">Whenever it fails</MudSelectItem>
                        <MudSelectItem T="DnsHealthAlertMode" Value="DnsHealthAlertMode.Off">Off</MudSelectItem>
                    </MudSelect>
                </MudItem>
            }

            <MudItem xs="12" md="4">
                <MudSwitch @bind-Value="_settings.DmarcPolicyWeakenedEnabled" Color="Color.Primary" Label="Alert when a DMARC policy weakens" />
            </MudItem>
            <MudItem xs="12" md="4">
                <MudSwitch @bind-Value="_settings.NameserversChangedEnabled" Color="Color.Primary" Label="Alert when nameservers change" />
            </MudItem>
            <MudItem xs="12" md="4">
                <MudNumericField Label="Close policy and nameserver alerts after (days)" @bind-Value="_settings.AcknowledgeableAutoCloseDays" Min="0" Max="365" Variant="Variant.Outlined"
                                 HelperText="0 keeps them open until someone acknowledges them or closes their ticket." />
            </MudItem>
```

and in the `@code` block add:

```csharp
    private sealed record DnsHealthModeSetting(string Label, Func<NotificationSettings, DnsHealthAlertMode> Get, Action<NotificationSettings, DnsHealthAlertMode> Set);

    private static readonly DnsHealthModeSetting[] DnsHealthModeSettings =
    [
        new("DMARC record", settings => settings.DmarcAlertMode, (settings, mode) => settings.DmarcAlertMode = mode),
        new("DMARC authorization record", settings => settings.DmarcAuthorizationAlertMode, (settings, mode) => settings.DmarcAuthorizationAlertMode = mode),
        new("TLS-RPT record", settings => settings.TlsrptAlertMode, (settings, mode) => settings.TlsrptAlertMode = mode),
        new("SPF record", settings => settings.SpfAlertMode, (settings, mode) => settings.SpfAlertMode = mode),
        new("MX", settings => settings.MxAlertMode, (settings, mode) => settings.MxAlertMode = mode),
        new("DKIM", settings => settings.DkimAlertMode, (settings, mode) => settings.DkimAlertMode = mode),
        new("MTA-STS", settings => settings.MtaStsAlertMode, (settings, mode) => settings.MtaStsAlertMode = mode),
    ];
```

- [ ] **Step 2: Add the Acknowledge button**

In `Alerts.razor`:
- Add `@using Microsoft.AspNetCore.Components.Authorization` (without it `AuthorizeView` is treated as a plain element), and `@inject AuditActorAccessor AuditActorAccessor`, `@inject IPsaTicketService PsaTicketService`, `@inject IDialogService DialogService`, `@inject ISnackbar Snackbar`.
- Add an empty `<MudTh></MudTh>` at the end of both header rows (the titles row and the filters row).
- Add a last cell to `RowTemplate`:

```razor
            <MudTd>
                @if (!context.IsResolved && AlertAcknowledgement.IsAcknowledgeable(context.AlertType))
                {
                    <AuthorizeView Policy="AlertsManage" Context="authorization">
                        <MudButton Size="Size.Small" Variant="Variant.Outlined" Color="Color.Primary" OnClick="@(() => AcknowledgeAsync(context))">Acknowledge</MudButton>
                    </AuthorizeView>
                }
            </MudTd>
```

- Replace `OnInitializedAsync` with a `LoadAsync` it calls, and add `AcknowledgeAsync`:

```csharp
    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _alerts = await db.AlertEvents
            .AsNoTracking()
            .OrderByDescending(e => e.CreatedUtc)
            .Take(100)
            .ToListAsync();
    }

    private async Task AcknowledgeAsync(AlertEvent alert)
    {
        var accepted = alert.AlertType == AlertTypes.DmarcPolicyWeakened ? "its current DMARC policy" : "its current nameservers";
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Acknowledge alert",
            $"Close this alert for {alert.DomainName} and accept {accepted} as the new normal? Its Halo ticket is closed too, if it has one.",
            yesText: "Acknowledge", cancelText: "Cancel");
        if (confirmed != true)
        {
            return;
        }

        // The button only renders with AlertsManage, and Blazor only runs handlers for rendered elements.
        await using var db = await DbFactory.CreateDbContextAsync();
        var outcome = await AlertAcknowledgement.AcknowledgeAsync(db, await AuditActorAccessor.GetAsync(), alert.Id, PsaTicketService);
        switch (outcome)
        {
            case AcknowledgeOutcome.Acknowledged:
                Snackbar.Add("Alert acknowledged.", Severity.Success);
                break;
            case AcknowledgeOutcome.AcknowledgedButTicketNotClosed:
                Snackbar.Add("Alert acknowledged, but its Halo ticket couldn't be closed. Close it in Halo.", Severity.Warning);
                break;
            default:
                Snackbar.Add("That alert is already closed.", Severity.Info);
                break;
        }

        await LoadAsync();
    }
```

(`AuditActorAccessor` is in `DotMarc.Audit`; add `@using DotMarc.Audit` if the page doesn't resolve it.)

- [ ] **Step 3: Build**

Run: `dotnet build src/DotMarc/DotMarc.csproj -nologo -v q`
Expected: `Build succeeded.` with no new warnings (in particular no `RZ10012` about `AuthorizeView`).

- [ ] **Step 4: Check it in the demo**

Start the demo (`$env:Demo__Enabled='true'; $env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/DotMarc --no-build --no-launch-profile --urls http://localhost:5195`), sign in as Demo Admin, and check:
- **Alerts › Settings** shows the DNS health alerts panel with seven selects on "When it breaks", both switches on and 0 days. Changing SPF to "Whenever it fails" and saving shows "Alert settings saved."; the Audit log has a "Notification settings saved" entry with "SPF alerts: WhenItBreaks → WheneverItFails".
- **Alerts** lists the open SPF record broken, DMARC policy weakened and Nameservers changed alerts. Only the policy and nameserver alerts have an Acknowledge button.
- Acknowledging the policy alert shows the confirmation, then "Alert acknowledged."; the alert shows as resolved; the Audit log has an "Alert acknowledged" entry.
- Signed in as Demo Viewer, the Alerts page shows no Acknowledge buttons.

Stop the app.

- [ ] **Step 5: Run everything and commit**

Run: `dotnet test test/DotMarc.Tests -nologo -v q` → PASS.

```powershell
git add src/DotMarc/Components/Pages/AlertsSettings.razor src/DotMarc/Components/Pages/Alerts.razor
git commit -m "Add the DNS health alert settings and an Acknowledge button for policy and nameserver alerts"
```

---

### Task 9: Docs and roadmap

**Files:**
- Modify: `website/docs/alerts.mdx`
- Modify: `website/scripts/canny-roadmap.json`

- [ ] **Step 1: Document the alerts**

In `website/docs/alerts.mdx`:

Replace the sentence "Both alert types are currently raised at `Warning` severity." with "These alert types are raised at `Warning` severity. The DNS health alerts below have their own severities."

In "The Alerts page" section, add at the end: "Open DMARC policy weakened and nameservers changed alerts have an **Acknowledge** button for anyone with the `AlertsManage` permission. See [Closing policy and nameserver alerts](#closing-policy-and-nameserver-alerts)."

Add these rows to the "Alert settings" table:

```mdx
| DNS health alerts | For each of the seven checks: **When it breaks** (default), **Whenever it fails** or **Off**. See [DNS health alerts](#dns-health-alerts). |
| Alert when a DMARC policy weakens | On by default. |
| Alert when nameservers change | On by default. |
| Close policy and nameserver alerts after (days) | `0` (default) keeps them open until acknowledged or their ticket is closed. |
```

Add a new section before "## Delivery channels":

```mdx
## DNS health alerts

dotMARC checks each domain's DMARC, DMARC authorization, TLS-RPT, SPF, MX, DKIM and MTA-STS records about once a
day. It raises an alert when one of those checks fails, when a domain's DMARC policy gets weaker, or when its
nameservers change.

| Type | Raised when | Resolves when | Severity |
| --- | --- | --- | --- |
| `DmarcRecordBroken` | The DMARC record is missing or broken. | The check passes again. | Warning |
| `DmarcAuthorizationBroken` | The record that lets reports go to dotMARC's mailbox is missing. | The check passes again. | Warning |
| `TlsrptRecordBroken` | The TLS-RPT record is missing or broken. | The check passes again. | Warning |
| `SpfRecordBroken` | The SPF record is missing, duplicated or broken. A `-all`-only record counts as passing. | The check passes again. | Warning |
| `MxRecordBroken` | There's no MX record, or the mail server's name doesn't resolve. A null MX counts as passing. | The check passes again. | Warning |
| `DkimRecordBroken` | A DKIM selector set for the domain is missing or broken. | The check passes again, or the selectors are removed. | Warning |
| `MtaStsFailing` | dotMARC stopped being able to serve the domain's MTA-STS policy. | MTA-STS is active again, or turned off. | Warning |
| `DmarcPolicyWeakened` | `p`, `sp` or `pct` went down, for example from `p=reject` to `p=quarantine`. | The policy goes back up, or the alert is closed (see below). | Warning |
| `NameserversChanged` | The domain's nameservers changed, often the start of a DNS migration. | They change back, or the alert is closed (see below). | Info |

A check that hasn't run yet, or doesn't apply (no DKIM selectors, MTA-STS turned off or still being set up), never
alerts.

### When a check alerts

Each check has a setting:

* **When it breaks** (the default): alerts only once the check has passed at least once. A domain that was never
  set up, for example one with no TLS-RPT record, stays quiet.
* **Whenever it fails**: alerts whenever the check fails.
* **Off**: never alerts, and closes any open alert.

When a check first fails, nothing is sent. dotMARC checks it again about 15 minutes later and only alerts if it's
still failing, so a brief DNS hiccup doesn't page anyone. The same applies to policy and nameserver changes.

Upgrading to a version with DNS health alerts doesn't raise a flood of alerts: on its first run dotMARC notes which
checks pass and the current policy and nameservers, and only alerts on changes from there.

Nameservers changed alerts don't open a HaloPSA ticket by default. Change that with the ticket rules, like any other
alert type.

### Closing policy and nameserver alerts

A weaker DMARC policy or new nameservers may be deliberate, so these alerts compare against an accepted value. Any of
these closes the alert and accepts the current policy or nameservers as the new normal:

* **Acknowledge** on the Alerts page (needs `AlertsManage`). It's recorded in the [audit log](./audit-log.mdx).
* **Closing its HaloPSA ticket.**
* **Closing automatically** after the number of days in **Close policy and nameserver alerts after (days)**, if set.
```

- [ ] **Step 2: Mark the roadmap idea complete**

In `website/scripts/canny-roadmap.json`, on `"Widen alerting beyond MissedReport / TLSRPTFailure"`, change `"status": "planned"` to `"status": "complete"`.

- [ ] **Step 3: Check everything**

Run: `dotnet test test/DotMarc.Tests -nologo -v q` → PASS.
Run: `cd website; yarn build; cd ..` → builds with no broken links or anchors.

- [ ] **Step 4: Commit**

```powershell
git add website/docs/alerts.mdx website/scripts/canny-roadmap.json
git commit -m "Document DNS health alerts"
```
