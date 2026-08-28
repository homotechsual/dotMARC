# Public Demo Instance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `Demo__Enabled` mode to dotMARC that replaces real Entra/Graph auth with a two-persona picker and replaces live mailbox polling with a generated, narrative dataset for a fictional MSP ("Nova MSP"), then deploy it as `demo.dotmarc.app` via Docker Compose + the VM's existing Caddy, with CI/CD.

**Architecture:** Everything lives under a new `src/DotMarc/Demo/` namespace and is gated behind `DemoOptions.Enabled`, so normal (non-demo) startup is byte-for-byte unchanged. A pure `DemoDataGenerator` produces an in-memory dataset; a thin `DemoDataSeeder` truncates and rewrites the database from it; `DemoDataResetService` re-runs that nightly; `Program.cs` branches at startup to skip Graph/Entra config entirely and swap in cookie auth plus a persona sign-in endpoint.

**Tech Stack:** ASP.NET Core 10 / Blazor Server, EF Core + Npgsql, MudBlazor, xUnit + Testcontainers.PostgreSql, Docker Compose, GitHub Actions.

**Spec:** [docs/superpowers/specs/2026-08-28-demo-instance-design.md](../specs/2026-08-28-demo-instance-design.md)

## Global Constraints

* Zero behavior change to the app when `Demo__Enabled` is unset/false — every addition in this plan is additive and gated.
* Demo personas: `demo-admin@nova-msp.example` (built-in `Admin` role) and `demo-viewer@nova-msp.example` (built-in `Viewer` role, scoped to the "Aurora Retail" group).
* **Refinement from the spec:** the spec describes 60 days of history. Reading the actual UI code (`DomainDetail.razor:103`, `DomainStatistics.ReportWindow`) shows every page — Dashboard, DomainDetail, its chart, Sources — filters strictly to the last 30 days; nothing in the app ever displays data older than that (the README's own Scope section confirms a 12-month rollup is explicitly out of scope, and no shorter rollup beyond 30 days exists either). Generating 60 days would make 30 of them permanently invisible. This plan generates exactly `DomainStatistics.ReportWindow` (30 days) of history instead — same visible outcome the spec asked for (a domain visibly ramping up over the window a visitor can actually see), less wasted generation.
* Follow this codebase's established "pure core, thin I/O adapter" split (see `DomainStatistics`, `DmarcReportParser`) and its convention of static classes operating on a caller-supplied `DotMarcDbContext` (see `DomainManagementService`, `AccessBootstrapper`).
* The demo sign-in endpoint must not exist (404) when `Demo__Enabled` is false — this is a real auth-bypass surface if left reachable in the production app. Every task touching it must preserve/verify this.

---

## Task 1: Demo configuration flag

**Files:**
- Create: `src/DotMarc/Demo/DemoOptions.cs`
- Modify: `src/DotMarc/Program.cs` (add binding only — no branching behavior yet)
- Test: `test/DotMarc.Tests/Demo/DemoOptionsTests.cs`

**Interfaces:**
- Produces: `DotMarc.Demo.DemoOptions` with `SectionName = "Demo"`, `bool Enabled` (default `false`), `int ResetHourUtc` (default `4`). Consumed by every later task.

- [ ] **Step 1: Write the failing test**

```csharp
// test/DotMarc.Tests/Demo/DemoOptionsTests.cs
using DotMarc.Demo;
using Xunit;

namespace DotMarc.Tests.Demo;

public sealed class DemoOptionsTests
{
    [Fact]
    public void DefaultsToDisabled_WithA4AmUtcResetHour()
    {
        var options = new DemoOptions();

        Assert.False(options.Enabled);
        Assert.Equal(4, options.ResetHourUtc);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DemoOptionsTests`
Expected: FAIL (build error) — `DotMarc.Demo` namespace / `DemoOptions` type doesn't exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
// src/DotMarc/Demo/DemoOptions.cs
namespace DotMarc.Demo;

/// <summary>Gates every demo-mode addition in this app. See
/// docs/superpowers/specs/2026-08-28-demo-instance-design.md. When Enabled is false (the
/// default), nothing in the DotMarc.Demo namespace runs — real Entra/Graph auth and ingestion
/// behave exactly as before this feature existed.</summary>
public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    public bool Enabled { get; set; }

    /// <summary>UTC hour DemoDataResetService resets the dataset each day.</summary>
    public int ResetHourUtc { get; set; } = 4;
}
```

- [ ] **Step 4: Bind it in Program.cs**

In `src/DotMarc/Program.cs`, immediately after the `builder.Services.Configure<InitialAdminsOptions>(...)` line (currently line 117), add:

```csharp
builder.Services.Configure<DotMarc.Demo.DemoOptions>(builder.Configuration.GetSection(DotMarc.Demo.DemoOptions.SectionName));

var demoOptions = new DotMarc.Demo.DemoOptions();
builder.Configuration.GetSection(DotMarc.Demo.DemoOptions.SectionName).Bind(demoOptions);
```

`demoOptions` is a plain bound instance (not `IOptions<T>`) because later tasks need its value immediately, before `builder.Build()`, to decide which services to register — the `Configure<DemoOptions>` call alongside it is what makes `IOptions<DemoOptions>` injectable everywhere else (MainLayout, DemoDataResetService).

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DemoOptionsTests`
Expected: PASS

- [ ] **Step 6: Run the full existing test suite to confirm nothing broke**

Run: `dotnet build dotMARC.sln && dotnet test dotMARC.sln`
Expected: all existing tests still PASS (this step only added an unused-so-far binding).

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Demo/DemoOptions.cs src/DotMarc/Program.cs test/DotMarc.Tests/Demo/DemoOptionsTests.cs
git commit -m "Add DemoOptions configuration flag (inert until wired up)"
```

---

## Task 2: Demo dataset model types

**Files:**
- Create: `src/DotMarc/Demo/DemoDataset.cs`

**Interfaces:**
- Consumes: `DotMarc.Data.AuthResult`, `DotMarc.Data.DispositionResult`, `DotMarc.Data.DmarcCheckStatus` (existing enums).
- Produces: `DemoDataset`, `DemoGroupSeed`, `DemoDomainSeed`, `DemoReportSeed`, `DemoRecordSeed`, `DemoPollCycleSeed`, `DemoPollCycleDailySummarySeed`, `DemoParseFailureSeed` — plain immutable records with no EF/DB dependency. Consumed by `DemoDataGenerator` (Task 3, produces these) and `DemoDataSeeder` (Task 4, consumes these).

This task is data-shape-only (no logic), so there's no meaningful failing test to write first — it's exercised by Task 3's tests. Write it directly:

- [ ] **Step 1: Write the model types**

```csharp
// src/DotMarc/Demo/DemoDataset.cs
using DotMarc.Data;

namespace DotMarc.Demo;

/// <summary>Everything DemoDataSeeder needs to (re)populate the database for one reset cycle.
/// Produced by the pure DemoDataGenerator — see that class for the narrative this data tells.</summary>
public sealed record DemoDataset(
    List<DemoGroupSeed> Groups,
    List<DemoDomainSeed> Domains,
    List<DemoPollCycleSeed> PollCycles,
    List<DemoPollCycleDailySummarySeed> PollCycleDailySummaries,
    List<DemoParseFailureSeed> ParseFailures);

public sealed record DemoGroupSeed(string Name);

public sealed record DemoDomainSeed(
    string Name,
    string? GroupName,
    int SortOrder,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset? LastReportReceivedUtc,
    DmarcCheckStatus DmarcCheckStatus,
    string? DmarcCheckDetail,
    List<DemoReportSeed> Reports);

public sealed record DemoReportSeed(
    string ReportingOrg,
    string ReportId,
    DateTimeOffset DateRangeBeginUtc,
    DateTimeOffset DateRangeEndUtc,
    List<DemoRecordSeed> Records);

public sealed record DemoRecordSeed(
    string SourceIp,
    int MessageCount,
    AuthResult SpfResult,
    AuthResult DkimResult,
    DispositionResult Disposition);

public sealed record DemoPollCycleSeed(
    DateTimeOffset PolledUtc,
    int MessagesChecked,
    int ReportsParsed,
    int ParseFailures,
    bool Succeeded,
    string? ErrorMessage);

public sealed record DemoPollCycleDailySummarySeed(
    DateOnly Date,
    int TotalCycles,
    int SuccessfulCycles,
    int FailedCycles,
    int TotalMessagesChecked,
    int TotalReportsParsed,
    int TotalParseFailures);

public sealed record DemoParseFailureSeed(
    string GraphMessageId,
    string Reason,
    int AttemptCount,
    DateTimeOffset LastAttemptedUtc);
```

- [ ] **Step 2: Confirm it builds**

Run: `dotnet build dotMARC.sln`
Expected: builds cleanly (no consumers yet, so nothing else to check).

- [ ] **Step 3: Commit**

```bash
git add src/DotMarc/Demo/DemoDataset.cs
git commit -m "Add demo dataset model types"
```

---

## Task 3: Demo data generator (pure, narrative)

**Files:**
- Create: `src/DotMarc/Demo/DemoDataGenerator.cs`
- Test: `test/DotMarc.Tests/Demo/DemoDataGeneratorTests.cs`

**Interfaces:**
- Consumes: `DemoDataset` and friends (Task 2).
- Produces: `DemoDataGenerator.Generate(Random random, DateTimeOffset nowUtc) : DemoDataset`, `DemoDataGenerator.HistoryDays : int`. Consumed by `DemoDataSeeder` usage sites in Program.cs and `DemoDataResetService` (Task 5).

This is the piece that carries the "Nova MSP" narrative from the spec: Aurora Retail (healthy), Brightline Legal (ramping up), Cobalt Freight (a problem source + a stale domain), Driftwood Media (legacy/unauthorized), plus one ungrouped domain.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/Demo/DemoDataGeneratorTests.cs
using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Demo;

public sealed class DemoDataGeneratorTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 28, 6, 0, 0, TimeSpan.Zero);

    private static DemoDataset Generate() => DemoDataGenerator.Generate(new Random(42), NowUtc);

    [Fact]
    public void GeneratesExactlySevenDomainsAcrossFourGroups()
    {
        var dataset = Generate();

        Assert.Equal(4, dataset.Groups.Count);
        Assert.Equal(7, dataset.Domains.Count);
        Assert.Contains(dataset.Domains, d => d.GroupName is null);
    }

    [Fact]
    public void AuroraRetailDomains_StayConsistentlyHealthy()
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == "aurora-retail.example");

        var passRate = DomainStatistics.GetPassRate(ToReports(domain));
        Assert.True(passRate is > 0.99, $"expected >99% pass rate, got {passRate}");
        Assert.Equal("Aurora Retail", domain.GroupName);
    }

    [Fact]
    public void BrightlineLegal_PassRateClimbsAcrossTheWindow()
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == "brightline-legal.example");

        var firstWeek = domain.Reports.Where(r => r.DateRangeBeginUtc < NowUtc.AddDays(-DemoDataGenerator.HistoryDays + 7)).ToList();
        var lastWeek = domain.Reports.Where(r => r.DateRangeBeginUtc >= NowUtc.AddDays(-7)).ToList();

        var earlyPassRate = DomainStatistics.GetPassRate(ToReports(domain, firstWeek))!.Value;
        var latePassRate = DomainStatistics.GetPassRate(ToReports(domain, lastWeek))!.Value;

        Assert.True(latePassRate > earlyPassRate, $"expected pass rate to climb: early={earlyPassRate}, late={latePassRate}");
        Assert.True(latePassRate >= 0.95, $"expected the domain to read as healthy by the end of the window, got {latePassRate}");
    }

    [Fact]
    public void CobaltFreight_FirstDomainReadsAsWarning_DueToAPersistentFailingSource()
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == "cobalt-freight.example");

        var passRate = DomainStatistics.GetPassRate(ToReports(domain))!.Value;
        Assert.True(passRate < 0.95, $"expected a Warning-level pass rate (<95%), got {passRate}");

        var sources = DomainStatistics.GetSourceAggregates(ToReports(domain));
        Assert.Contains(sources, s => s.SpfResult == AuthResult.Fail && s.DkimResult == AuthResult.Fail);
    }

    [Fact]
    public void CobaltFreight_SecondDomainHasNoReportsInTheLastThreeDays()
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == "fleet.cobalt-freight.example");

        Assert.NotNull(domain.LastReportReceivedUtc);
        Assert.True(domain.LastReportReceivedUtc < NowUtc.AddDays(-2), "expected this domain to read as Missing");
    }

    [Fact]
    public void DriftwoodMedia_HasAMissingAuthorizationRecordStatus()
    {
        var dataset = Generate();
        var domain = dataset.Domains.Single(d => d.Name == "driftwood-media.example");

        Assert.Equal(DmarcCheckStatus.MissingAuthorizationRecord, domain.DmarcCheckStatus);
        Assert.False(string.IsNullOrWhiteSpace(domain.DmarcCheckDetail));
    }

    [Fact]
    public void PollCycles_CoverTheLast7DaysRaw_AndOlderDaysAreDailySummariesOnly()
    {
        var dataset = Generate();

        Assert.NotEmpty(dataset.PollCycles);
        Assert.All(dataset.PollCycles, p => Assert.True(p.PolledUtc >= NowUtc.AddDays(-7)));
        Assert.Contains(dataset.PollCycles, p => !p.Succeeded);

        Assert.NotEmpty(dataset.PollCycleDailySummaries);
        Assert.All(dataset.PollCycleDailySummaries, s => Assert.True(s.Date < DateOnly.FromDateTime(NowUtc.AddDays(-7).UtcDateTime)));
    }

    [Fact]
    public void ParseFailures_AreNotEmpty()
    {
        var dataset = Generate();
        Assert.NotEmpty(dataset.ParseFailures);
    }

    [Fact]
    public void SameSeedAndTime_ProducesTheSameDataset()
    {
        var first = DemoDataGenerator.Generate(new Random(7), NowUtc);
        var second = DemoDataGenerator.Generate(new Random(7), NowUtc);

        Assert.Equal(first.Domains.Select(d => d.Reports.Count), second.Domains.Select(d => d.Reports.Count));
    }

    private static List<Report> ToReports(DemoDomainSeed domain, List<DemoReportSeed>? reports = null) =>
        (reports ?? domain.Reports).Select(r => new Report
        {
            Domain = null!,
            ReportingOrg = r.ReportingOrg,
            ReportId = r.ReportId,
            DateRangeBeginUtc = r.DateRangeBeginUtc,
            DateRangeEndUtc = r.DateRangeEndUtc,
            RawXml = "",
            ReceivedUtc = r.DateRangeEndUtc,
            Records = r.Records.Select(rec => new ReportRecord
            {
                Report = null!,
                SourceIp = rec.SourceIp,
                MessageCount = rec.MessageCount,
                Disposition = rec.Disposition,
                SpfResult = rec.SpfResult,
                DkimResult = rec.DkimResult,
                HeaderFrom = domain.Name
            }).ToList()
        }).ToList();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DemoDataGeneratorTests`
Expected: FAIL (build error) — `DemoDataGenerator` doesn't exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
// src/DotMarc/Demo/DemoDataGenerator.cs
using DotMarc.Data;
using DotMarc.Reporting;

namespace DotMarc.Demo;

/// <summary>Pure generator for the "Nova MSP" demo dataset — see
/// docs/superpowers/specs/2026-08-28-demo-instance-design.md for the narrative this implements,
/// and this plan's Global Constraints for why it covers 30 days (DomainStatistics.ReportWindow),
/// not the 60 the spec originally described. Takes no dependencies beyond a Random and the
/// current time, so it's fully unit-testable without a database — same "pure core, thin I/O
/// adapter" split as DomainStatistics/DmarcReportParser; DemoDataSeeder is the I/O adapter that
/// writes this output.</summary>
public static class DemoDataGenerator
{
    public static readonly int HistoryDays = (int)DomainStatistics.ReportWindow.TotalDays;
    public const int RawPollCycleDays = 7;

    public static DemoDataset Generate(Random random, DateTimeOffset nowUtc)
    {
        var groups = new List<DemoGroupSeed>
        {
            new("Aurora Retail"),
            new("Brightline Legal"),
            new("Cobalt Freight"),
            new("Driftwood Media"),
        };

        var domains = new List<DemoDomainSeed>
        {
            BuildDomain(random, nowUtc, sortOrder: 0, name: "aurora-retail.example", groupName: "Aurora Retail",
                orgs: ["google.com", "outlook.com"], passRateForDay: _ => 0.997,
                status: DmarcCheckStatus.Ok, detail: null, daysOfHistory: HistoryDays),
            BuildDomain(random, nowUtc, sortOrder: 1, name: "shop.aurora-retail.example", groupName: "Aurora Retail",
                orgs: ["google.com", "yahoo.com"], passRateForDay: _ => 0.996,
                status: DmarcCheckStatus.Ok, detail: null, daysOfHistory: HistoryDays),
            BuildDomain(random, nowUtc, sortOrder: 2, name: "brightline-legal.example", groupName: "Brightline Legal",
                orgs: ["google.com", "outlook.com"], passRateForDay: day => Lerp(0.70, 0.99, day / (double)(HistoryDays - 1)),
                status: DmarcCheckStatus.Ok, detail: null, daysOfHistory: HistoryDays),
            BuildDomain(random, nowUtc, sortOrder: 3, name: "cobalt-freight.example", groupName: "Cobalt Freight",
                orgs: ["google.com", "outlook.com"], passRateForDay: _ => 0.87,
                status: DmarcCheckStatus.Ok, detail: null, daysOfHistory: HistoryDays),
            BuildDomain(random, nowUtc, sortOrder: 4, name: "fleet.cobalt-freight.example", groupName: "Cobalt Freight",
                orgs: ["google.com"], passRateForDay: _ => 0.98,
                status: DmarcCheckStatus.Ok, detail: null, daysOfHistory: HistoryDays - 4),
            BuildDomain(random, nowUtc, sortOrder: 5, name: "driftwood-media.example", groupName: "Driftwood Media",
                orgs: ["yahoo.com", "protonmail.com"], passRateForDay: _ => 0.85,
                status: DmarcCheckStatus.MissingAuthorizationRecord,
                detail: "No TXT record found at driftwood-media.example._report._dmarc.nova-msp.example",
                daysOfHistory: HistoryDays),
            BuildDomain(random, nowUtc, sortOrder: 6, name: "driftwood-events.example", groupName: null,
                orgs: ["google.com"], passRateForDay: _ => 0.93,
                status: DmarcCheckStatus.NotChecked, detail: null, daysOfHistory: HistoryDays),
        };

        return new DemoDataset(
            groups,
            domains,
            BuildPollCycles(random, nowUtc),
            BuildPollCycleDailySummaries(random, nowUtc),
            BuildParseFailures(nowUtc));
    }

    private static double Lerp(double from, double to, double t) => from + ((to - from) * t);

    private static DemoDomainSeed BuildDomain(
        Random random, DateTimeOffset nowUtc, int sortOrder, string name, string? groupName,
        string[] orgs, Func<int, double> passRateForDay, DmarcCheckStatus status, string? detail, int daysOfHistory)
    {
        var reports = new List<DemoReportSeed>();
        DateTimeOffset? lastReportReceivedUtc = null;

        for (var day = 0; day < daysOfHistory; day++)
        {
            var rangeBegin = new DateTimeOffset(nowUtc.AddDays(-HistoryDays + day).Date, TimeSpan.Zero);
            var rangeEnd = rangeBegin.AddDays(1).AddSeconds(-1);
            var passRate = passRateForDay(day);

            foreach (var org in orgs)
            {
                var totalVolume = 800 + random.Next(0, 700);
                var passingVolume = (int)Math.Round(totalVolume * passRate);
                var failingVolume = totalVolume - passingVolume;

                var records = new List<DemoRecordSeed>
                {
                    new(LegitimateSourceIp(org), passingVolume, AuthResult.Pass, AuthResult.Pass, DispositionResult.None)
                };

                if (failingVolume > 0)
                {
                    records.Add(new DemoRecordSeed(ProblemSourceIp(name), failingVolume, AuthResult.Fail, AuthResult.Fail,
                        failingVolume > totalVolume / 4 ? DispositionResult.Quarantine : DispositionResult.None));
                }

                reports.Add(new DemoReportSeed(org, $"demo-{name}-{org}-{day:D3}", rangeBegin, rangeEnd, records));
                lastReportReceivedUtc = rangeEnd;
            }
        }

        return new DemoDomainSeed(name, groupName, sortOrder, nowUtc.AddDays(-HistoryDays), lastReportReceivedUtc, status, detail, reports);
    }

    private static string LegitimateSourceIp(string org) => org switch
    {
        "google.com" => "142.250.10.20",
        "outlook.com" => "40.92.90.30",
        "yahoo.com" => "67.195.204.65",
        "protonmail.com" => "185.70.40.20",
        _ => "192.0.2.10"
    };

    /// <summary>A fixed, deliberately "third-party ESP"-looking address, distinct per domain so
    /// each shows up as its own row in that domain's Sources tab. Not a real allocation.</summary>
    private static string ProblemSourceIp(string domainName) =>
        "203.0.113." + (Math.Abs(domainName.GetHashCode()) % 200 + 10);

    private static List<DemoPollCycleSeed> BuildPollCycles(Random random, DateTimeOffset nowUtc)
    {
        var cycles = new List<DemoPollCycleSeed>();
        var cursor = nowUtc.AddDays(-RawPollCycleDays);
        var failureInjected = false;

        while (cursor < nowUtc)
        {
            var injectFailureHere = !failureInjected && cursor > nowUtc.AddDays(-3) && random.Next(0, 50) == 0;
            if (injectFailureHere)
            {
                failureInjected = true;
                cycles.Add(new DemoPollCycleSeed(cursor, 0, 0, 0, false, "Graph API request timed out."));
            }
            else
            {
                var messages = random.Next(0, 4);
                cycles.Add(new DemoPollCycleSeed(cursor, messages, messages, 0, true, null));
            }

            cursor = cursor.AddMinutes(15);
        }

        // Guarantee the injected failure exists even if the random roll above never hit it —
        // the test suite (and a visitor looking at the poll status page) expects at least one,
        // for texture, without depending on a low-probability random draw.
        if (!failureInjected && cycles.Count > 0)
        {
            var last = cycles[^1];
            cycles[^1] = last with { Succeeded = false, ErrorMessage = "Graph API request timed out.", MessagesChecked = 0, ReportsParsed = 0 };
        }

        return cycles;
    }

    private static List<DemoPollCycleDailySummarySeed> BuildPollCycleDailySummaries(Random random, DateTimeOffset nowUtc)
    {
        var summaries = new List<DemoPollCycleDailySummarySeed>();
        for (var day = RawPollCycleDays; day < HistoryDays; day++)
        {
            var date = DateOnly.FromDateTime(nowUtc.AddDays(-day).Date);
            const int cyclesPerDay = 96;
            var failed = day % 17 == 0 ? 1 : 0;
            summaries.Add(new DemoPollCycleDailySummarySeed(
                date, cyclesPerDay, cyclesPerDay - failed, failed,
                TotalMessagesChecked: 20 + random.Next(0, 30),
                TotalReportsParsed: 10 + random.Next(0, 15),
                TotalParseFailures: 0));
        }

        return summaries;
    }

    private static List<DemoParseFailureSeed> BuildParseFailures(DateTimeOffset nowUtc) =>
    [
        new("demo-msg-0001", "Attachment was not a valid gzip or zip archive.", 3, nowUtc.AddHours(-6)),
        new("demo-msg-0002", "XML document did not match the expected DMARC aggregate report schema.", 1, nowUtc.AddDays(-2)),
    ];
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DemoDataGeneratorTests`
Expected: PASS. If `CobaltFreight_FirstDomainReadsAsWarning...` or `BrightlineLegal_PassRateClimbsAcrossTheWindow` are flaky against the fixed seed (42), tune the fixed `passRateForDay` values above (0.87 for Cobalt Freight, 0.70→0.99 for Brightline) — the volumes are randomized per day so the exact resulting rate has small noise around the target; both assertions use headroom (`<0.95` vs target 0.87, `>=0.95` vs target 0.99) specifically to absorb that noise, but re-check with the real random draws if it fails.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Demo/DemoDataGenerator.cs test/DotMarc.Tests/Demo/DemoDataGeneratorTests.cs
git commit -m "Add pure demo data generator for the Nova MSP narrative"
```

---

## Task 4: Demo data seeder (EF writer)

**Files:**
- Create: `src/DotMarc/Demo/DemoDataSeeder.cs`
- Test: `test/DotMarc.Tests/Demo/DemoDataSeederTests.cs`

**Interfaces:**
- Consumes: `DemoDataset` (Task 2), `DotMarcDbContext` (existing).
- Produces: `DemoDataSeeder.ResetAsync(DotMarcDbContext context, DemoDataset dataset, CancellationToken)`, plus public constants `DemoDataSeeder.AdminEmail`, `DemoDataSeeder.ViewerEmail`, `DemoDataSeeder.ViewerScopedGroupName`. Consumed by Program.cs (Task 5), `DemoDataResetService` (Task 5), and the sign-in endpoint (Task 6).

- [ ] **Step 1: Write the failing tests**

```csharp
// test/DotMarc.Tests/Demo/DemoDataSeederTests.cs
using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Demo;

[Collection("Postgres")]
public sealed class DemoDataSeederTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DemoDataSeederTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static DemoDataset SampleDataset() => DemoDataGenerator.Generate(new Random(1), new DateTimeOffset(2026, 8, 28, 6, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ResetAsync_WritesAllDomainsGroupsAndReports()
    {
        using var context = CreateContext();

        await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);

        using var verify = CreateContext();
        Assert.Equal(7, await verify.Domains.CountAsync());
        Assert.Equal(4, await verify.Groups.CountAsync());
        Assert.True(await verify.Reports.CountAsync() > 0);
        Assert.True(await verify.ReportRecords.CountAsync() > 0);
    }

    [Fact]
    public async Task ResetAsync_SeedsAdminAndViewerRolesAndGrants()
    {
        using var context = CreateContext();

        await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);

        using var verify = CreateContext();
        var admin = await verify.Roles.SingleAsync(r => r.Name == "Admin");
        var viewer = await verify.Roles.SingleAsync(r => r.Name == "Viewer");

        var adminGrant = await verify.UserAccesses.SingleAsync(u => u.Email == DemoDataSeeder.AdminEmail);
        Assert.Equal(admin.Id, adminGrant.RoleId);

        var viewerGrant = await verify.UserAccesses
            .Include(u => u.ScopedGroups)
            .SingleAsync(u => u.Email == DemoDataSeeder.ViewerEmail);
        Assert.Equal(viewer.Id, viewerGrant.RoleId);
        Assert.Equal(DemoDataSeeder.ViewerScopedGroupName, Assert.Single(viewerGrant.ScopedGroups).Name);
    }

    [Fact]
    public async Task ResetAsync_IsRepeatable_WithoutAccumulatingDuplicateRows()
    {
        using (var context = CreateContext())
        {
            await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);
        }

        using (var context = CreateContext())
        {
            await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);
        }

        using var verify = CreateContext();
        Assert.Equal(7, await verify.Domains.CountAsync());
        Assert.Equal(2, await verify.Roles.CountAsync());
        Assert.Equal(2, await verify.UserAccesses.CountAsync());
    }

    [Fact]
    public async Task ResetAsync_WritesPollCyclesAndParseFailures()
    {
        using var context = CreateContext();

        await DemoDataSeeder.ResetAsync(context, SampleDataset(), CancellationToken.None);

        using var verify = CreateContext();
        Assert.True(await verify.PollCycles.CountAsync() > 0);
        Assert.True(await verify.PollCycleDailySummaries.CountAsync() > 0);
        Assert.True(await verify.ParseFailures.CountAsync() > 0);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DemoDataSeederTests`
Expected: FAIL (build error) — `DemoDataSeeder` doesn't exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
// src/DotMarc/Demo/DemoDataSeeder.cs
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Demo;

/// <summary>Wipes and rewrites every app-owned table from a DemoDataset — the same code path
/// runs on first boot and on every scheduled reset (see DemoDataResetService), so there is only
/// one seeding path, not two. Deliberately does not use AccessBootstrapper's advisory-lock
/// pattern: this always runs against a single demo instance from either Program.cs's startup
/// block or DemoDataResetService's own serial loop, never concurrently with itself.</summary>
public static class DemoDataSeeder
{
    public const string AdminEmail = "demo-admin@nova-msp.example";
    public const string ViewerEmail = "demo-viewer@nova-msp.example";
    public const string ViewerScopedGroupName = "Aurora Retail";

    public static async Task ResetAsync(DotMarcDbContext context, DemoDataset dataset, CancellationToken cancellationToken = default)
    {
        await TruncateAllTablesAsync(context, cancellationToken).ConfigureAwait(false);
        await WriteAsync(context, dataset, cancellationToken).ConfigureAwait(false);
    }

    internal static Task TruncateAllTablesAsync(DotMarcDbContext context, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                "Domains", "Reports", "ReportRecords", "Groups", "Tags", "Roles", "UserAccesses",
                "PollCycles", "PollCycleDailySummaries", "ParseFailures", "ProcessedMessages",
                "UserAccessScopedGroups", "DomainGroup", "DomainTag"
            RESTART IDENTITY CASCADE
            """,
            cancellationToken);

    internal static async Task WriteAsync(DotMarcDbContext context, DemoDataset dataset, CancellationToken cancellationToken)
    {
        var adminRole = new Role { Name = "Admin", IsLocked = true, IsScopable = false, Permissions = [.. Enum.GetValues<Permission>()] };
        var viewerRole = new Role { Name = "Viewer", IsLocked = false, IsScopable = true, Permissions = [Permission.DomainsView, Permission.GroupsView, Permission.TagsView] };
        context.Roles.AddRange(adminRole, viewerRole);

        var groupsByName = dataset.Groups.ToDictionary(g => g.Name, g => new Group { Name = g.Name });
        context.Groups.AddRange(groupsByName.Values);

        foreach (var domainSeed in dataset.Domains)
        {
            var domain = new Domain
            {
                Name = domainSeed.Name,
                IsMonitored = true,
                SortOrder = domainSeed.SortOrder,
                FirstSeenUtc = domainSeed.FirstSeenUtc,
                LastReportReceivedUtc = domainSeed.LastReportReceivedUtc,
                DmarcCheckStatus = domainSeed.DmarcCheckStatus,
                DmarcCheckedUtc = domainSeed.DmarcCheckStatus == DmarcCheckStatus.NotChecked ? null : domainSeed.FirstSeenUtc,
                DmarcCheckDetail = domainSeed.DmarcCheckDetail
            };

            if (domainSeed.GroupName is not null)
            {
                domain.Groups.Add(groupsByName[domainSeed.GroupName]);
            }

            foreach (var reportSeed in domainSeed.Reports)
            {
                var report = new Report
                {
                    Domain = domain,
                    ReportingOrg = reportSeed.ReportingOrg,
                    ReportId = reportSeed.ReportId,
                    DateRangeBeginUtc = reportSeed.DateRangeBeginUtc,
                    DateRangeEndUtc = reportSeed.DateRangeEndUtc,
                    RawXml = "<!-- demo data: no raw report retained -->",
                    ReceivedUtc = reportSeed.DateRangeEndUtc
                };

                foreach (var recordSeed in reportSeed.Records)
                {
                    report.Records.Add(new ReportRecord
                    {
                        Report = report,
                        SourceIp = recordSeed.SourceIp,
                        MessageCount = recordSeed.MessageCount,
                        Disposition = recordSeed.Disposition,
                        SpfResult = recordSeed.SpfResult,
                        DkimResult = recordSeed.DkimResult,
                        HeaderFrom = domainSeed.Name
                    });
                }

                domain.Reports.Add(report);
            }

            context.Domains.Add(domain);
        }

        foreach (var pollCycle in dataset.PollCycles)
        {
            context.PollCycles.Add(new PollCycle
            {
                PolledUtc = pollCycle.PolledUtc,
                MessagesChecked = pollCycle.MessagesChecked,
                ReportsParsed = pollCycle.ReportsParsed,
                ParseFailures = pollCycle.ParseFailures,
                Succeeded = pollCycle.Succeeded,
                ErrorMessage = pollCycle.ErrorMessage
            });
        }

        foreach (var summary in dataset.PollCycleDailySummaries)
        {
            context.PollCycleDailySummaries.Add(new PollCycleDailySummary
            {
                Date = summary.Date,
                TotalCycles = summary.TotalCycles,
                SuccessfulCycles = summary.SuccessfulCycles,
                FailedCycles = summary.FailedCycles,
                TotalMessagesChecked = summary.TotalMessagesChecked,
                TotalReportsParsed = summary.TotalReportsParsed,
                TotalParseFailures = summary.TotalParseFailures
            });
        }

        foreach (var failure in dataset.ParseFailures)
        {
            context.ParseFailures.Add(new ParseFailure
            {
                GraphMessageId = failure.GraphMessageId,
                Reason = failure.Reason,
                AttemptCount = failure.AttemptCount,
                LastAttemptedUtc = failure.LastAttemptedUtc
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        context.UserAccesses.AddRange(
            new UserAccess { Email = AdminEmail, Role = adminRole },
            new UserAccess { Email = ViewerEmail, Role = viewerRole, ScopedGroups = [groupsByName[ViewerScopedGroupName]] });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DemoDataSeederTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Demo/DemoDataSeeder.cs test/DotMarc.Tests/Demo/DemoDataSeederTests.cs
git commit -m "Add demo data seeder (truncate + rewrite from a DemoDataset)"
```

---

## Task 5: Program.cs demo-mode wiring + nightly reset

**Files:**
- Create: `src/DotMarc/Demo/DemoDataResetService.cs`
- Modify: `src/DotMarc/Program.cs`
- Modify: `test/DotMarc.Tests/DotMarc.Tests.csproj` (add `Microsoft.AspNetCore.Mvc.Testing`)
- Test: `test/DotMarc.Tests/Demo/DemoDataResetServiceTests.cs`
- Test: `test/DotMarc.Tests/Demo/DemoModeStartupTests.cs`

**Interfaces:**
- Consumes: `DemoOptions` (Task 1), `DemoDataGenerator`/`DemoDataSeeder` (Tasks 3–4).
- Produces: `DemoDataResetService` (a `BackgroundService`, registered only when `Demo:Enabled=true`), internal `DemoDataResetService.SeedFor(DateTimeOffset) : int` and `DemoDataResetService.GetDelayUntilNextReset(DateTimeOffset nowUtc, int resetHourUtc) : TimeSpan`. Consumed by Task 6's end-to-end tests (which rely on the app having already seeded demo data at startup).

This is the task where `Demo__Enabled=true` actually changes app behavior: real Entra/Graph auth and `PollingService` are skipped entirely, cookie auth is used instead, and the dataset is seeded synchronously before the app starts serving traffic (every startup, including every redeploy — simpler and race-free compared to seeding from a background service, which could let a visitor sign in before the first seed completes).

- [ ] **Step 1: Add the test project's new package reference**

In `test/DotMarc.Tests/DotMarc.Tests.csproj`, add inside the existing `<ItemGroup>` with the other `PackageReference` entries:

```xml
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
```

- [ ] **Step 2: Write the failing scheduling test (pure, no DB)**

```csharp
// test/DotMarc.Tests/Demo/DemoDataResetServiceTests.cs
using DotMarc.Demo;
using Xunit;

namespace DotMarc.Tests.Demo;

public sealed class DemoDataResetServiceTests
{
    [Fact]
    public void GetDelayUntilNextReset_ReturnsTimeUntilTodaysResetHour_WhenBeforeIt()
    {
        var now = new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);

        var delay = DemoDataResetService.GetDelayUntilNextReset(now, resetHourUtc: 4);

        Assert.Equal(TimeSpan.FromHours(3), delay);
    }

    [Fact]
    public void GetDelayUntilNextReset_RollsOverToTomorrow_WhenAfterTodaysResetHour()
    {
        var now = new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);

        var delay = DemoDataResetService.GetDelayUntilNextReset(now, resetHourUtc: 4);

        Assert.Equal(TimeSpan.FromHours(18), delay);
    }

    [Fact]
    public void SeedFor_IsStableWithinTheSameUtcDay_ButDiffersAcrossDays()
    {
        var morning = new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);
        var evening = new DateTimeOffset(2026, 8, 28, 23, 0, 0, TimeSpan.Zero);
        var nextDay = new DateTimeOffset(2026, 8, 29, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(DemoDataResetService.SeedFor(morning), DemoDataResetService.SeedFor(evening));
        Assert.NotEqual(DemoDataResetService.SeedFor(morning), DemoDataResetService.SeedFor(nextDay));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DemoDataResetServiceTests`
Expected: FAIL (build error) — `DemoDataResetService` doesn't exist yet.

- [ ] **Step 4: Write DemoDataResetService**

```csharp
// src/DotMarc/Demo/DemoDataResetService.cs
using DotMarc.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotMarc.Demo;

/// <summary>Resets the demo dataset once a day at DemoOptions.ResetHourUtc. Registered only when
/// Demo:Enabled is true (see Program.cs). The very first seed happens synchronously in
/// Program.cs's own startup block, not here — this service only ever handles the recurring
/// reset, so there's no window where a visitor could sign in before any data exists.</summary>
public sealed class DemoDataResetService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DemoOptions _options;
    private readonly ILogger<DemoDataResetService> _logger;

    public DemoDataResetService(IServiceScopeFactory scopeFactory, IOptions<DemoOptions> options, ILogger<DemoDataResetService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextReset(DateTimeOffset.UtcNow, _options.ResetHourUtc);
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DotMarcDbContext>();
                var nowUtc = DateTimeOffset.UtcNow;
                var dataset = DemoDataGenerator.Generate(new Random(SeedFor(nowUtc)), nowUtc);
                await DemoDataSeeder.ResetAsync(context, dataset, stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Demo dataset reset completed at {NowUtc}.", nowUtc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Demo dataset reset failed; will retry at the next scheduled reset.");
            }
        }
    }

    /// <summary>Deterministic per-UTC-day seed: a container restart between resets reproduces
    /// the same dataset instead of drawing a new random one, while each calendar day still gets
    /// its own variation. internal so tests can verify it directly.</summary>
    internal static int SeedFor(DateTimeOffset nowUtc) => nowUtc.UtcDateTime.Date.GetHashCode();

    /// <summary>internal so tests can verify the scheduling math without waiting on real time —
    /// the only production caller is ExecuteAsync above.</summary>
    internal static TimeSpan GetDelayUntilNextReset(DateTimeOffset nowUtc, int resetHourUtc)
    {
        var todayReset = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, resetHourUtc, 0, 0, TimeSpan.Zero);
        var nextReset = nowUtc < todayReset ? todayReset : todayReset.AddDays(1);
        return nextReset - nowUtc;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DemoDataResetServiceTests`
Expected: PASS

- [ ] **Step 6: Wire demo mode into Program.cs**

In `src/DotMarc/Program.cs`, the registrations that need to become conditional are NOT one contiguous block — the existing `IDmarcDnsChecker` HTTP client registration sits in between two pieces that both need wrapping, and it must stay exactly where it is, unconditional. Make two separate edits:

**Edit A** — replace this existing block (`AddOptions<GraphOptions>()` through the `AddHttpClient<IGraphMailboxClient, ...>` call, i.e. everything from just after `AddDbContextFactory<DotMarcDbContext>` up to — but NOT including — the `AddHttpClient<IDmarcDnsChecker, ...>` call):

```csharp
builder.Services.AddOptions<GraphOptions>()
    .Bind(builder.Configuration.GetSection(GraphOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IGraphTokenProvider, ConfidentialClientGraphTokenProvider>();

builder.Services.AddHttpClient<IGraphMailboxClient, GraphMailboxClient>(client =>
{
    client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
});
```

with:

```csharp
if (!demoOptions.Enabled)
{
    builder.Services.AddOptions<GraphOptions>()
        .Bind(builder.Configuration.GetSection(GraphOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddSingleton<IGraphTokenProvider, ConfidentialClientGraphTokenProvider>();

    builder.Services.AddHttpClient<IGraphMailboxClient, GraphMailboxClient>(client =>
    {
        client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
    });
}
```

**Edit B** — leave the `AddHttpClient<IDmarcDnsChecker, DmarcDnsChecker>(...)` call immediately after Edit A's block completely untouched (it's dead weight in demo mode — only `PollingService` ever calls it — but harmless, and leaving it registered keeps this diff smaller). Then replace the block that comes right after it (the `PollingService` comment plus its `AddHostedService<PollingService>` call):

```csharp
// PollingService has two constructors (one for direct test construction, one for the real
// DI-scoped host path), both with 3 parameters. The built-in container's own constructor
// selection does NOT consult [ActivatorUtilitiesConstructor] when activating a plain
// AddHostedService<PollingService>() registration, so that alone throws "ambiguous
// constructors" here (both IGraphMailboxClient and DotMarcDbContext are also registered in
// this container). Routing activation through ActivatorUtilities.CreateInstance explicitly
// does honor that attribute, so it deterministically selects the host constructor.
builder.Services.AddHostedService<PollingService>(sp => ActivatorUtilities.CreateInstance<PollingService>(sp));
```

with:

```csharp
if (demoOptions.Enabled)
{
    builder.Services.AddHostedService<DotMarc.Demo.DemoDataResetService>();
}
else
{
    // PollingService has two constructors (one for direct test construction, one for the real
    // DI-scoped host path), both with 3 parameters. The built-in container's own constructor
    // selection does NOT consult [ActivatorUtilitiesConstructor] when activating a plain
    // AddHostedService<PollingService>() registration, so that alone throws "ambiguous
    // constructors" here (both IGraphMailboxClient and DotMarcDbContext are also registered in
    // this container). Routing activation through ActivatorUtilities.CreateInstance explicitly
    // does honor that attribute, so it deterministically selects the host constructor.
    builder.Services.AddHostedService<PollingService>(sp => ActivatorUtilities.CreateInstance<PollingService>(sp));
}
```

Then replace the `AddAuthentication`/`AddMicrosoftIdentityWebApp`/`Configure<CookieAuthenticationOptions>` block (immediately below the block just replaced) with:

```csharp
if (demoOptions.Enabled)
{
    builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.LoginPath = "/demo";
            options.AccessDeniedPath = "/AccessDenied";
        });
}
else
{
    builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("EntraId"));

    // AddMicrosoftIdentityWebApp wires up cookie authentication under CookieAuthenticationDefaults's
    // standard "Cookies" scheme alongside OpenIdConnect. Its default AccessDeniedPath sends a denied
    // user to a generic ASP.NET Core 403 page; pointing it at our own AccessDenied.razor instead gives
    // them an explanation instead of a raw 404/403.
    builder.Services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
        Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
        options => options.AccessDeniedPath = "/AccessDenied");
}
```

Then, in the existing startup block (the `using (var scope = app.Services.CreateScope())` block, after `app.Build()`, that runs migrations and `AccessBootstrapper` — note its line numbers have shifted down by the few lines Task 1 inserted above it), add the synchronous initial/redeploy seed right after the `AccessBootstrapper.BootstrapWithLeaderLockAsync(...)` call, still inside the `using`:

```csharp
    if (demoOptions.Enabled)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var dataset = DotMarc.Demo.DemoDataGenerator.Generate(new Random(DotMarc.Demo.DemoDataResetService.SeedFor(nowUtc)), nowUtc);
        await DotMarc.Demo.DemoDataSeeder.ResetAsync(context, dataset);
    }
```

Finally, add the demo sign-in endpoint mapping (Task 6 will fill in its body; for this task, map a stub so the "must 404 when disabled" test below has something meaningful to check once Task 6 lands — add this immediately before `app.MapRazorComponents<DotMarc.Components.App>()` near the end of the file):

```csharp
if (demoOptions.Enabled)
{
    app.MapPost("/demo/sign-in/{persona}", () => Results.NotFound("Not implemented yet."));
}
```

- [ ] **Step 7: Write the startup smoke tests**

```csharp
// test/DotMarc.Tests/Demo/DemoModeStartupTests.cs
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DotMarc.Tests.Demo;

[Collection("Postgres")]
public sealed class DemoModeStartupTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DemoModeStartupTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private WebApplicationFactory<Program> CreateFactory(bool demoEnabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", _connectionString);
            builder.UseSetting("Demo:Enabled", demoEnabled ? "true" : "false");
        });

    [Fact]
    public async Task StartsSuccessfully_WithNoGraphOrEntraIdConfiguration_WhenDemoModeIsEnabled()
    {
        await using var factory = CreateFactory(demoEnabled: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/demo");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RedirectsUnauthenticatedRequests_ToTheDemoPicker_WhenDemoModeIsEnabled()
    {
        await using var factory = CreateFactory(demoEnabled: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/demo", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task DemoSignInEndpoint_DoesNotExist_WhenDemoModeIsDisabled()
    {
        // Regression guard for the auth-bypass risk this endpoint would otherwise be: it must
        // be completely unreachable in the real (non-demo) app.
        await using var factory = CreateFactory(demoEnabled: false).WithWebHostBuilder(builder =>
        {
            // The real (non-demo) app requires Graph/EntraId config to start; provide the
            // minimum placeholder values so the host builds far enough to route the request —
            // ValidateOnStart only rejects missing values, not unreachable ones.
            builder.UseSetting("Graph:ClientId", "placeholder");
            builder.UseSetting("Graph:TenantId", "placeholder");
            builder.UseSetting("Graph:ClientSecret", "placeholder");
            builder.UseSetting("Graph:MailboxAddress", "placeholder@example.com");
            builder.UseSetting("EntraId:TenantId", "placeholder");
            builder.UseSetting("EntraId:ClientId", "placeholder");
            builder.UseSetting("EntraId:ClientSecret", "placeholder");
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/demo/sign-in/admin", content: null);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DemoModeStartupTests`
Expected: PASS. (`DemoSignInEndpoint_DoesNotExist_WhenDemoModeIsDisabled` currently passes trivially since the endpoint mapping itself is skipped when `demoOptions.Enabled` is false — this is the important guarantee, not an artifact of the stub body.)

- [ ] **Step 9: Run the full test suite**

Run: `dotnet build dotMARC.sln && dotnet test dotMARC.sln`
Expected: all tests PASS, including every pre-existing test — confirms the non-demo path is unchanged.

- [ ] **Step 10: Commit**

```bash
git add src/DotMarc/Demo/DemoDataResetService.cs src/DotMarc/Program.cs test/DotMarc.Tests/DotMarc.Tests.csproj test/DotMarc.Tests/Demo/DemoDataResetServiceTests.cs test/DotMarc.Tests/Demo/DemoModeStartupTests.cs
git commit -m "Wire demo mode into Program.cs: skip Graph/Entra, seed on startup, nightly reset"
```

---

## Task 6: Persona sign-in + picker page + banner

**Files:**
- Create: `src/DotMarc/Components/Pages/Demo/DemoSignIn.razor`
- Modify: `src/DotMarc/Program.cs` (replace the stub sign-in endpoint from Task 5)
- Modify: `src/DotMarc/Components/Layout/MainLayout.razor`
- Test: `test/DotMarc.Tests/Demo/DemoSignInEndpointTests.cs`

**Interfaces:**
- Consumes: `DemoDataSeeder.AdminEmail` / `.ViewerEmail` / `.ViewerScopedGroupName` (Task 4), `DemoOptions` (Task 1).
- Produces: the real `/demo/sign-in/{persona}` behavior (cookie sign-in), replacing Task 5's stub.

- [ ] **Step 1: Write the failing end-to-end tests**

```csharp
// test/DotMarc.Tests/Demo/DemoSignInEndpointTests.cs
using DotMarc.Demo;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DotMarc.Tests.Demo;

[Collection("Postgres")]
public sealed class DemoSignInEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private WebApplicationFactory<Program>? _factory;

    public DemoSignInEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
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
    public async Task SigningInAsAdmin_GrantsAccessToTheDashboard()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var signInResponse = await client.PostAsync("/demo/sign-in/admin", content: null);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, signInResponse.StatusCode);

        var dashboardResponse = await client.GetAsync("/dashboard");
        Assert.Equal(System.Net.HttpStatusCode.OK, dashboardResponse.StatusCode);
    }

    [Fact]
    public async Task SigningInAsViewer_AlsoGrantsAccessToTheDashboard_ButNotManageAccess()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.PostAsync("/demo/sign-in/viewer", content: null);

        var dashboardResponse = await client.GetAsync("/dashboard");
        Assert.Equal(System.Net.HttpStatusCode.OK, dashboardResponse.StatusCode);

        var manageAccessResponse = await client.GetAsync("/access");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, manageAccessResponse.StatusCode);
        Assert.Contains("AccessDenied", manageAccessResponse.Headers.Location!.ToString());
    }

    [Fact]
    public async Task UnknownPersona_ReturnsBadRequest()
    {
        using var client = _factory!.CreateClient();

        var response = await client.PostAsync("/demo/sign-in/superuser", content: null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DemoSignInEndpointTests`
Expected: FAIL — the endpoint currently always returns 404 (Task 5's stub).

- [ ] **Step 3: Replace the stub endpoint in Program.cs**

Replace the stub added in Task 5:

```csharp
if (demoOptions.Enabled)
{
    app.MapPost("/demo/sign-in/{persona}", () => Results.NotFound("Not implemented yet."));
}
```

with:

```csharp
if (demoOptions.Enabled)
{
    app.MapPost("/demo/sign-in/{persona}", async (string persona, HttpContext httpContext) =>
    {
        string email;
        string displayName;
        switch (persona)
        {
            case "admin":
                email = DotMarc.Demo.DemoDataSeeder.AdminEmail;
                displayName = "Demo Admin";
                break;
            case "viewer":
                email = DotMarc.Demo.DemoDataSeeder.ViewerEmail;
                displayName = $"Demo Viewer ({DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName})";
                break;
            default:
                return Results.BadRequest($"Unknown demo persona '{persona}'.");
        }

        // No antiforgery token: the only effect of this endpoint is changing which fixed demo
        // persona the calling browser's own session views as — there's no cross-user or
        // cross-tenant side effect a forged request could cause, so skipping CSRF protection
        // here (unlike every other mutating endpoint in this app, which goes through Blazor's
        // own antiforgery-protected form handling) is a deliberate, low-risk simplification.
        var identity = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("preferred_username", email),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, displayName)
            ],
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
            nameType: System.Security.Claims.ClaimTypes.Name,
            roleType: System.Security.Claims.ClaimTypes.Role);

        await httpContext.SignInAsync(
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
            new System.Security.Claims.ClaimsPrincipal(identity));

        return Results.Redirect("/");
    });
}
```

- [ ] **Step 4: Add the persona picker page**

```razor
@* src/DotMarc/Components/Pages/Demo/DemoSignIn.razor *@
@page "/demo"
@attribute [Microsoft.AspNetCore.Authorization.AllowAnonymous]
@using DotMarc.Demo
@using Microsoft.Extensions.Options
@inject IOptions<DemoOptions> DemoOptions
@inject NavigationManager Navigation

@if (DemoOptions.Value.Enabled)
{
    <MudContainer MaxWidth="MaxWidth.Small" Class="mt-8">
        <MudText Typo="Typo.h4" Class="mb-4">dotMARC demo</MudText>
        <MudText Class="mb-6">
            This is a live demo with simulated data for a fictional MSP, Nova MSP. Nothing you
            enter is sent anywhere, and the dataset resets automatically every day.
        </MudText>

        <MudPaper Class="pa-4 mb-4" Elevation="1">
            <MudText Typo="Typo.h6">Demo Admin</MudText>
            <MudText Typo="Typo.body2" Class="mb-2">Full access to every domain, group, and the access management pages.</MudText>
            <form method="post" action="/demo/sign-in/admin">
                <MudButton ButtonType="ButtonType.Submit" Variant="Variant.Filled" Color="Color.Primary">Continue as Demo Admin</MudButton>
            </form>
        </MudPaper>

        <MudPaper Class="pa-4" Elevation="1">
            <MudText Typo="Typo.h6">Demo Viewer</MudText>
            <MudText Typo="Typo.body2" Class="mb-2">Scoped to Aurora Retail only — shows what a limited, client-scoped viewer sees.</MudText>
            <form method="post" action="/demo/sign-in/viewer">
                <MudButton ButtonType="ButtonType.Submit" Variant="Variant.Filled" Color="Color.Secondary">Continue as Demo Viewer</MudButton>
            </form>
        </MudPaper>
    </MudContainer>
}

@code {
    protected override void OnInitialized()
    {
        // Defense in depth: this page (and the sign-in endpoint it posts to, which Program.cs
        // only maps when Demo:Enabled is true) only do anything in demo mode. Reachable in the
        // real app only if someone guesses the URL; redirect them away rather than showing a
        // picker that leads nowhere. Done in OnInitialized rather than inline in markup — calling
        // NavigationManager.NavigateTo from within BuildRenderTree isn't reliable under
        // interactive server rendering (the mode this whole app uses).
        if (!DemoOptions.Value.Enabled)
        {
            Navigation.NavigateTo("/", forceLoad: true);
        }
    }
}
```

- [ ] **Step 5: Add the demo banner to MainLayout**

In `src/DotMarc/Components/Layout/MainLayout.razor`, add two using statements after the existing ones (line 3):

```razor
@using DotMarc.Demo
@using Microsoft.Extensions.Options
```

Add an injection after the existing `@inject IJSRuntime Js` (line 4):

```razor
@inject IOptions<DemoOptions> DemoOptions
```

Then, inside `<MudAppBar Elevation="1">`, immediately before the existing `<MudSpacer />` (line 18), add:

```razor
@if (DemoOptions.Value.Enabled)
{
    <AuthorizeView>
        <Authorized>
            <MudChip T="string" Color="Color.Warning" Class="mr-2">Demo — viewing as @context.User.Identity?.Name</MudChip>
            <MudButton Href="/demo" Color="Color.Inherit">Switch persona</MudButton>
        </Authorized>
    </AuthorizeView>
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests/DotMarc.Tests.csproj --filter DemoSignInEndpointTests`
Expected: PASS

- [ ] **Step 7: Run the full test suite**

Run: `dotnet build dotMARC.sln && dotnet test dotMARC.sln`
Expected: all tests PASS.

- [ ] **Step 8: Manually verify in a browser**

```bash
cd J:\Projects\dotMARC
$env:Demo__Enabled = "true"
$env:ConnectionStrings__DotMarc = "Host=localhost;Database=dotmarc;Username=dotmarc;Password=dotmarc"
docker compose up postgres -d
dotnet run --project src/DotMarc/DotMarc.csproj
```

Open `http://localhost:8080` — expect a redirect to `/demo`. Click "Continue as Demo Admin" — expect the Dashboard showing 7 domains across 4 groups with the pass rates/statuses described in the spec. Click "Switch persona" → "Continue as Demo Viewer" — expect the Dashboard to show only the two Aurora Retail domains, and `/access` to redirect to `/AccessDenied`.

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc/Components/Pages/Demo/DemoSignIn.razor src/DotMarc/Program.cs src/DotMarc/Components/Layout/MainLayout.razor test/DotMarc.Tests/Demo/DemoSignInEndpointTests.cs
git commit -m "Add demo persona picker, sign-in endpoint, and banner"
```

---

## Task 7: Docker Compose + deployment docs

**Files:**
- Create: `docker-compose.demo.yml`
- Modify: `README.md` (new "Demo instance" section)

No automated test for this task — it's infrastructure configuration and documentation. Verification is a manual `docker compose config` syntax check plus (optionally) an actual local run.

- [ ] **Step 1: Write docker-compose.demo.yml**

```yaml
# docker-compose.demo.yml
services:
  dotmarc-demo:
    image: ${DOTMARC_IMAGE}
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    environment:
      ConnectionStrings__DotMarc: Host=postgres;Database=dotmarc;Username=dotmarc;Password=${POSTGRES_PASSWORD}
      Demo__Enabled: "true"
    expose:
      - "8080"
    networks:
      - default
      - proxy

  postgres:
    image: postgres:18
    environment:
      POSTGRES_DB: dotmarc
      POSTGRES_USER: dotmarc
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - dotmarc-demo-postgres-data:/var/lib/postgresql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U dotmarc -d dotmarc"]
      interval: 5s
      timeout: 5s
      retries: 10

networks:
  proxy:
    external: true

volumes:
  dotmarc-demo-postgres-data:
```

- [ ] **Step 2: Verify the compose file's syntax**

Run: `docker compose -f docker-compose.demo.yml config`
Expected: prints the fully-resolved config with no errors (`DOTMARC_IMAGE`/`POSTGRES_PASSWORD` will show as empty since they're not set in this shell — that's fine, this step only checks YAML/Compose syntax, not that it can actually run).

- [ ] **Step 3: Add the README section**

In `README.md`, add a new section after `## Deploy to Azure` (before `## Development`):

```markdown
## Demo instance

`Demo__Enabled=true` switches the app into demo mode: real Entra/Graph auth and mailbox polling
are skipped entirely, an anonymous `/demo` page lets a visitor sign in as one of two fixed
personas (Demo Admin, or Demo Viewer scoped to one client group), and a generated dataset for a
fictional MSP is (re)written on every startup and again every night at `Demo__ResetHourUtc`
(default `4`, UTC). See
[docs/superpowers/specs/2026-08-28-demo-instance-design.md](docs/superpowers/specs/2026-08-28-demo-instance-design.md)
for the full design and the narrative the generated data tells.

No `Graph__*`/`EntraId__*`/`InitialAdmins__Emails` variables are needed in this mode — only
`ConnectionStrings__DotMarc` and `Demo__Enabled`.

### Running the demo stack

```powershell
docker compose -f docker-compose.demo.yml --env-file .env.demo up -d
```

with a `.env.demo` file next to `docker-compose.demo.yml` containing:

```
DOTMARC_IMAGE=ghcr.io/homotechsual/dotmarc:demo
POSTGRES_PASSWORD=<pick a password>
```

The `dotmarc-demo` container joins an external Docker network named `proxy` and only `expose`s
port 8080 — it does not publish a host port or run its own reverse proxy. Point your existing
Caddy instance (on that same `proxy` network) at it, e.g.:

```
demo.dotmarc.app {
    reverse_proxy dotmarc-demo:8080
}
```

Deployment to the demo VM is automated by `.github/workflows/demo-deploy.yml` on every push to
`main` — see that workflow for the required repository secrets/variables.
```

- [ ] **Step 4: Commit**

```bash
git add docker-compose.demo.yml README.md
git commit -m "Add docker-compose.demo.yml and demo instance documentation"
```

---

## Task 8: CI/CD deploy workflow

**Files:**
- Create: `.github/workflows/demo-deploy.yml`

No automated test — this is a CI workflow definition; it's validated by actually running (which requires the secrets set up below to exist first).

- [ ] **Step 1: Write the workflow**

```yaml
# .github/workflows/demo-deploy.yml
name: Deploy demo

on:
  push:
    branches: [main]
  workflow_dispatch:

env:
  REGISTRY: ghcr.io
  DEPLOY_PATH: /opt/dotmarc-demo

jobs:
  build-and-push:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    outputs:
      image: ${{ steps.image.outputs.name }}
    steps:
      - uses: actions/checkout@v7

      - name: Compute image name
        id: image
        run: echo "name=${REGISTRY}/$(echo '${{ github.repository }}' | tr '[:upper:]' '[:lower:]')" >> "$GITHUB_OUTPUT"

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v4

      - name: Log in to GHCR
        uses: docker/login-action@v4
        with:
          registry: ${{ env.REGISTRY }}
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Build and push demo image
        uses: docker/build-push-action@v7
        with:
          context: .
          file: src/DotMarc/Dockerfile
          target: final
          push: true
          tags: |
            ${{ steps.image.outputs.name }}:demo
            ${{ steps.image.outputs.name }}:demo-${{ github.sha }}
          cache-from: type=gha
          cache-to: type=gha,mode=max

  deploy:
    needs: build-and-push
    runs-on: ubuntu-latest
    env:
      DOTMARC_IMAGE: ${{ needs.build-and-push.outputs.image }}:demo
      DOTMARC_DEMO_DOMAIN: ${{ vars.DOTMARC_DEMO_DOMAIN }}
      POSTGRES_PASSWORD: ${{ secrets.DOTMARC_DEMO_POSTGRES_PASSWORD }}
    steps:
      - uses: actions/checkout@v7

      - name: Set up SSH
        run: |
          mkdir -p ~/.ssh
          echo "${{ secrets.DOTMARC_DEMO_DEPLOY_SSH_KEY }}" | base64 -d > ~/.ssh/deploy_key
          chmod 600 ~/.ssh/deploy_key
          ssh-keygen -y -f ~/.ssh/deploy_key > /dev/null
          ssh-keyscan -H "${{ secrets.DOTMARC_DEMO_DEPLOY_HOST }}" >> ~/.ssh/known_hosts

      - name: Copy stack file to server
        run: |
          ssh -i ~/.ssh/deploy_key -o StrictHostKeyChecking=no \
            ${{ secrets.DOTMARC_DEMO_DEPLOY_USER }}@${{ secrets.DOTMARC_DEMO_DEPLOY_HOST }} \
            "mkdir -p ${{ env.DEPLOY_PATH }}"
          scp -i ~/.ssh/deploy_key -o StrictHostKeyChecking=no \
            docker-compose.demo.yml \
            ${{ secrets.DOTMARC_DEMO_DEPLOY_USER }}@${{ secrets.DOTMARC_DEMO_DEPLOY_HOST }}:${{ env.DEPLOY_PATH }}/

      - name: Write .env on server
        run: |
          {
            echo "DOTMARC_IMAGE=$DOTMARC_IMAGE"
            echo "POSTGRES_PASSWORD=$POSTGRES_PASSWORD"
          } | base64 -w0 | ssh -i ~/.ssh/deploy_key -o StrictHostKeyChecking=no \
            ${{ secrets.DOTMARC_DEMO_DEPLOY_USER }}@${{ secrets.DOTMARC_DEMO_DEPLOY_HOST }} \
            'base64 -d > ${{ env.DEPLOY_PATH }}/.env && chmod 600 ${{ env.DEPLOY_PATH }}/.env'

      - name: Roll out stack
        run: |
          ssh -i ~/.ssh/deploy_key -o StrictHostKeyChecking=no \
            ${{ secrets.DOTMARC_DEMO_DEPLOY_USER }}@${{ secrets.DOTMARC_DEMO_DEPLOY_HOST }} \
            "cd ${{ env.DEPLOY_PATH }} && \
             docker compose -f docker-compose.demo.yml --env-file .env pull && \
             docker compose -f docker-compose.demo.yml --env-file .env up -d && \
             docker image prune -f"

      - name: Clean up SSH key
        if: always()
        run: rm -f ~/.ssh/deploy_key
```

`DOTMARC_DEMO_DOMAIN` is fetched into the job's `env` for documentation/consistency with the
Caddyfile snippet in the README, even though it isn't otherwise used in the deploy steps (Caddy
config lives outside this repo, per this plan's Task 7) — it is intentionally a repository
**variable**, not a secret: see the reasoning already applied to `DOCKERHUB_USERNAME` in
`release.yml` (a job output/log containing a secret's value gets silently masked/dropped by the
Actions runner; a public hostname isn't sensitive and shouldn't risk that).

- [ ] **Step 2: Document and create the required GitHub secrets/variables**

This workflow needs, on the `homotechsual/dotMARC` repository:

| Name | Kind | Value |
| --- | --- | --- |
| `DOTMARC_DEMO_DEPLOY_SSH_KEY` | secret | base64-encoded private SSH key for a deploy user on the target VM |
| `DOTMARC_DEMO_DEPLOY_HOST` | secret | the VM's hostname/IP |
| `DOTMARC_DEMO_DEPLOY_USER` | secret | the SSH user to deploy as |
| `DOTMARC_DEMO_POSTGRES_PASSWORD` | secret | a password for the demo's own Postgres instance (distinct from any other stack's) |
| `DOTMARC_DEMO_DOMAIN` | **variable** | `demo.dotmarc.app` |

These involve credential material this plan should not generate or guess — set them once, e.g.:

```bash
gh variable set DOTMARC_DEMO_DOMAIN --repo homotechsual/dotMARC --body "demo.dotmarc.app"
gh secret set DOTMARC_DEMO_DEPLOY_HOST --repo homotechsual/dotMARC --body "<vm host/ip>"
gh secret set DOTMARC_DEMO_DEPLOY_USER --repo homotechsual/dotMARC --body "<ssh user>"
gh secret set DOTMARC_DEMO_POSTGRES_PASSWORD --repo homotechsual/dotMARC --body "<a generated password>"
base64 -w0 ~/.ssh/id_ed25519_dotmarc_demo | gh secret set DOTMARC_DEMO_DEPLOY_SSH_KEY --repo homotechsual/dotMARC
```

(matching the same `base64`-encoded-private-key convention `deploy.yml` uses in the GCT repo).
Also add the public half of that key to the deploy user's `~/.ssh/authorized_keys` on the VM, and
fold the Caddyfile snippet from Task 7's README section into that VM's actual Caddy config.

- [ ] **Step 3: Commit the workflow**

```bash
git add .github/workflows/demo-deploy.yml
git commit -m "Add CI/CD workflow to deploy the demo instance"
```

- [ ] **Step 4: After the secrets/variable above are set, verify end-to-end**

Push to `main` (or run `gh workflow run demo-deploy.yml --repo homotechsual/dotMARC`), watch it with
`gh run watch --repo homotechsual/dotMARC`, then confirm `https://demo.dotmarc.app/demo` loads and
both personas work as in Task 6's manual verification step.
