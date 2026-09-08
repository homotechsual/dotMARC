# DMARC Report Insights Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture the DMARC policy-override reason codes and per-mechanism auth_results detail that the parser already sees but discards, make IP enrichment proactive instead of view-gated, and surface both through Sources-tab drill-down, per-domain/org-wide insight panels, and a new alert type.

**Architecture:** Two new child tables (`ReportRecordAuthDetail`, `ReportRecordPolicyOverrideReason`) capture per-record detail at ingestion time; two new leader-locked `PollingService` cycles handle proactive IP enrichment and historical backfill; a shared pure-function aggregation helper (`DomainStatistics.GetReasonBreakdown`) feeds both UI panels and the new alert check.

**Tech Stack:** .NET 10 / Blazor Server, EF Core + Npgsql, MudBlazor, DmarcRua 2.0.1, xunit + Testcontainers (Postgres).

**Spec:** docs/superpowers/specs/2026-09-08-dmarc-report-insights-design.md

## Global Constraints

- `ReportRecord.SpfResult`/`DkimResult`/`Disposition` stay exactly as-is (Pass/Fail/disposition) - all new detail is additive on new child tables/columns, never a replacement.
- `DmarcMechanismResult` mirrors the union of DmarcRua's `DKIMResultType`/`SpfResultType` (not the existing `AuthResult` Pass/Fail enum) - `None, Default, Neutral, Pass, Fail, Policy, SoftFail, TempError, Invalid, Unknown, PermError, HardFail`.
- `DmarcPolicyOverrideType` mirrors DmarcRua's `PolicyOverrideType` member-for-member: `None, Forwarded, SampledOut, TrustedForwarder, MailingList, LocalPolicy, Other`.
- No admin-configured/manual trigger for IP enrichment or backfill - both run as leader-locked `PollingService` cycles, matching `RunDnsProviderCheckCycleAsync`'s exact shape (`pg_try_advisory_xact_lock`, per-item try/catch that logs and continues).
- No change to `CloudflareDnsPushProvider.cs`, `AzureDnsPushProvider.cs`, `GoogleCloudDnsPushProvider.cs`, or `TlsrptFailureDetail`'s existing modeling - unrelated to this plan.
- No new bUnit component tests (established convention) - Razor/UI changes verified by build + manual browser check.
- New enum-typed columns use `HasConversion<string>()`, matching `DispositionResult`/`AuthResult`/`DetectedDnsProvider`'s existing convention.
- Backfill and proactive enrichment must be idempotent and resumable across restarts - a report/IP already handled must never be reprocessed on the next cycle.

---

### Task 1: Data model - enums, child entities, Report backfill marker

**Files:**
- Create: `src/DotMarc/Data/DmarcAuthMechanism.cs`
- Create: `src/DotMarc/Data/DmarcMechanismResult.cs`
- Create: `src/DotMarc/Data/DmarcPolicyOverrideType.cs`
- Create: `src/DotMarc/Data/ReportRecordAuthDetail.cs`
- Create: `src/DotMarc/Data/ReportRecordPolicyOverrideReason.cs`
- Modify: `src/DotMarc/Data/ReportRecord.cs`
- Modify: `src/DotMarc/Data/Report.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs`
- Create: EF Core migration (generated, verified by hand)

**Interfaces:**
- Produces: `DmarcAuthMechanism { Dkim, Spf }`, `DmarcMechanismResult { None, Default, Neutral, Pass, Fail, Policy, SoftFail, TempError, Invalid, Unknown, PermError, HardFail }`, `DmarcPolicyOverrideType { None, Forwarded, SampledOut, TrustedForwarder, MailingList, LocalPolicy, Other }` - consumed by Task 2's parser mapping and every later task.
- Produces: `ReportRecordAuthDetail { Id, ReportRecordId, ReportRecord, Mechanism, Domain, Result, Selector, Scope, HumanResult }` - consumed by Tasks 3, 5, 6.
- Produces: `ReportRecordPolicyOverrideReason { Id, ReportRecordId, ReportRecord, Type, Comment }` - consumed by Tasks 3, 5, 6, 7.
- Produces: `ReportRecord.AuthDetails`/`ReportRecord.OverrideReasons` (both `List<T>`, default `[]`) - the nav collections Tasks 3/5/6/7 write to and read from.
- Produces: `Report.AuthDetailBackfilledUtc` (nullable `DateTimeOffset`) - Task 3 sets it at ingestion time (new reports never need backfill), Task 5's backfill cycle selects on `IS NULL` and sets it once processed (success or skip), so a report is never reprocessed once handled.

This task is pure schema - no new business logic, so no dedicated test file (matches this codebase's convention: `DetectedDnsProvider.GoogleCloudDns`'s own addition had no standalone test either; the enums/entities get exercised by Tasks 2/3/5/6/7's tests). Verified by `dotnet build` and a migration that applies cleanly.

- [ ] **Step 1: Create the three new enums**

`src/DotMarc/Data/DmarcAuthMechanism.cs`:
```csharp
namespace DotMarc.Data;

public enum DmarcAuthMechanism
{
    Dkim,
    Spf
}
```

`src/DotMarc/Data/DmarcMechanismResult.cs`:
```csharp
namespace DotMarc.Data;

// Mirrors the union of DmarcRua's DKIMResultType/SpfResultType (both IANA-registered, stable
// result codes) - deliberately NOT the existing AuthResult enum (Pass/Fail only), which is the
// collapsed DMARC-alignment result already on ReportRecord. Collapsing away TempError vs PermError
// vs Neutral here would throw away exactly the operational distinction this feature exists to
// surface ("transient DNS hiccup" vs "sender never published a valid record at all"). Member names
// match both source enums' ToString() output exactly, so DmarcReportParser can Enum.Parse directly
// from either without a manual mapping switch.
public enum DmarcMechanismResult
{
    None,
    Default,
    Neutral,
    Pass,
    Fail,
    Policy,
    SoftFail,
    TempError,
    Invalid,
    Unknown,
    PermError,
    HardFail
}
```

`src/DotMarc/Data/DmarcPolicyOverrideType.cs`:
```csharp
namespace DotMarc.Data;

// Mirrors DmarcRua's PolicyOverrideType member-for-member, so DmarcReportParser can Enum.Parse
// directly from its ToString() output.
public enum DmarcPolicyOverrideType
{
    None,
    Forwarded,
    SampledOut,
    TrustedForwarder,
    MailingList,
    LocalPolicy,
    Other
}
```

- [ ] **Step 2: Create the two child entities**

`src/DotMarc/Data/ReportRecordAuthDetail.cs`:
```csharp
namespace DotMarc.Data;

/// <summary>One DKIM signature or SPF check found in a report record's &lt;auth_results&gt; -
/// a record commonly has one of each, but DKIM can carry several signatures. Mirrors
/// TlsrptFailureDetail's one-parent-many-children shape.</summary>
public sealed class ReportRecordAuthDetail
{
    public int Id { get; set; }
    public int ReportRecordId { get; set; }
    public ReportRecord ReportRecord { get; set; } = null!;
    public required DmarcAuthMechanism Mechanism { get; set; }
    public required string Domain { get; set; }
    public required DmarcMechanismResult Result { get; set; }
    public string? Selector { get; set; }   // DKIM only
    public string? Scope { get; set; }      // SPF only: "MFrom" or "Helo"
    public string? HumanResult { get; set; }
}
```

`src/DotMarc/Data/ReportRecordPolicyOverrideReason.cs`:
```csharp
namespace DotMarc.Data;

/// <summary>One &lt;reason&gt; entry from a report record's policy_evaluated block - usually 0 or
/// 1 per record, but the DMARC schema allows several.</summary>
public sealed class ReportRecordPolicyOverrideReason
{
    public int Id { get; set; }
    public int ReportRecordId { get; set; }
    public ReportRecord ReportRecord { get; set; } = null!;
    public required DmarcPolicyOverrideType Type { get; set; }
    public string? Comment { get; set; }
}
```

- [ ] **Step 3: Add the nav collections to ReportRecord and the backfill marker to Report**

Modify `src/DotMarc/Data/ReportRecord.cs` - add two properties at the end of the class:
```csharp
    public List<ReportRecordAuthDetail> AuthDetails { get; set; } = [];
    public List<ReportRecordPolicyOverrideReason> OverrideReasons { get; set; } = [];
}
```

Modify `src/DotMarc/Data/Report.cs` - add one property after `ReceivedUtc`:
```csharp
    public DateTimeOffset ReceivedUtc { get; set; }
    public DateTimeOffset? AuthDetailBackfilledUtc { get; set; }

    public List<ReportRecord> Records { get; set; } = [];
```

- [ ] **Step 4: Register the new DbSets and entity configuration**

Modify `src/DotMarc/Data/DotMarcDbContext.cs` - add two DbSet lines directly after the existing `ReportRecords` line:
```csharp
    public DbSet<ReportRecord> ReportRecords => Set<ReportRecord>();
    public DbSet<ReportRecordAuthDetail> ReportRecordAuthDetails => Set<ReportRecordAuthDetail>();
    public DbSet<ReportRecordPolicyOverrideReason> ReportRecordPolicyOverrideReasons => Set<ReportRecordPolicyOverrideReason>();
```

In `OnModelCreating`, add two new entity blocks directly after the existing `modelBuilder.Entity<ReportRecord>(entity => { ... });` block:
```csharp
        modelBuilder.Entity<ReportRecordAuthDetail>(entity =>
        {
            entity.HasOne(d => d.ReportRecord)
                .WithMany(r => r.AuthDetails)
                .HasForeignKey(d => d.ReportRecordId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(d => d.Mechanism).HasConversion<string>();
            entity.Property(d => d.Result).HasConversion<string>();
        });

        modelBuilder.Entity<ReportRecordPolicyOverrideReason>(entity =>
        {
            entity.HasOne(o => o.ReportRecord)
                .WithMany(r => r.OverrideReasons)
                .HasForeignKey(o => o.ReportRecordId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(o => o.Type).HasConversion<string>();
        });
```

- [ ] **Step 5: Generate and verify the migration**

Run: `dotnet ef migrations add AddDmarcReportInsightDetail --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

Expected: a new migration creating two tables (`ReportRecordAuthDetails`, `ReportRecordPolicyOverrideReasons`, both with a cascade FK to `ReportRecords`) and adding one nullable `AuthDetailBackfilledUtc` column to `Reports`. Verify by hand: no default value on the new column (nullable columns don't need one - every existing historical row gets `NULL`, which is exactly "needs backfill"), no changes to any other table.

- [ ] **Step 6: Build and commit**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

```bash
git add src/DotMarc/Data/DmarcAuthMechanism.cs src/DotMarc/Data/DmarcMechanismResult.cs src/DotMarc/Data/DmarcPolicyOverrideType.cs src/DotMarc/Data/ReportRecordAuthDetail.cs src/DotMarc/Data/ReportRecordPolicyOverrideReason.cs src/DotMarc/Data/ReportRecord.cs src/DotMarc/Data/Report.cs src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Migrations/
git commit -m "Add DMARC auth-detail and policy-override-reason data model"
```

---

### Task 2: Parser - capture auth_results and reason detail

**Files:**
- Modify: `src/DotMarc/Ingestion/ParsedReport.cs`
- Modify: `src/DotMarc/Ingestion/DmarcReportParser.cs`
- Create: `test/DotMarc.Tests/Fixtures/sample-report-with-detail.xml`
- Test: `test/DotMarc.Tests/Ingestion/DmarcReportParserTests.cs`

**Interfaces:**
- Consumes: `DmarcAuthMechanism`, `DmarcMechanismResult`, `DmarcPolicyOverrideType` (Task 1).
- Produces: `ParsedAuthDetail(DmarcAuthMechanism Mechanism, string Domain, DmarcMechanismResult Result, string? Selector, string? Scope, string? HumanResult)` and `ParsedPolicyOverrideReason(DmarcPolicyOverrideType Type, string? Comment)` - consumed by Task 3's ingestion wiring and Task 5's backfill cycle.
- Produces: `ParsedReportRecord` gains `AuthDetails`/`OverrideReasons` (both `IReadOnlyList<T>`) as its last two positional parameters.

- [ ] **Step 1: Create the fixture XML**

`test/DotMarc.Tests/Fixtures/sample-report-with-detail.xml` - one record with a policy-override reason and two DKIM signatures (one passing, one failing), to exercise full fidelity capture:
```xml
<?xml version="1.0" encoding="UTF-8" ?>
<feedback>
  <report_metadata>
    <org_name>google.com</org_name>
    <email>noreply-dmarc-support@google.com</email>
    <report_id>detail-report-1</report_id>
    <date_range>
      <begin>1754438400</begin>
      <end>1754524800</end>
    </date_range>
  </report_metadata>
  <policy_published>
    <domain>contoso.io</domain>
    <adkim>r</adkim>
    <aspf>r</aspf>
    <p>quarantine</p>
    <sp>quarantine</sp>
    <pct>100</pct>
  </policy_published>
  <record>
    <row>
      <source_ip>203.0.113.50</source_ip>
      <count>12</count>
      <policy_evaluated>
        <disposition>none</disposition>
        <dkim>pass</dkim>
        <spf>fail</spf>
        <reason>
          <type>local_policy</type>
          <comment>arc allowed</comment>
        </reason>
      </policy_evaluated>
    </row>
    <identifiers>
      <header_from>contoso.io</header_from>
    </identifiers>
    <auth_results>
      <spf>
        <domain>envelope.contoso.io</domain>
        <result>fail</result>
      </spf>
      <dkim>
        <domain>contoso.io</domain>
        <result>pass</result>
        <selector>default</selector>
      </dkim>
      <dkim>
        <domain>relay.contoso.io</domain>
        <result>temperror</result>
        <selector>backup</selector>
      </dkim>
    </auth_results>
  </record>
</feedback>
```

- [ ] **Step 2: Write the failing tests**

Add to `test/DotMarc.Tests/Ingestion/DmarcReportParserTests.cs`:
```csharp
    [Fact]
    public void Parse_MapsAuthDetails_ForEveryDkimAndSpfEntry()
    {
        var xmlBytes = File.ReadAllBytes("Fixtures/sample-report-with-detail.xml");

        var result = DmarcReportParser.Parse(xmlBytes);

        var record = result.Records.Single();
        Assert.Equal(3, record.AuthDetails.Count);

        var spf = record.AuthDetails.Single(d => d.Mechanism == DmarcAuthMechanism.Spf);
        Assert.Equal("envelope.contoso.io", spf.Domain);
        Assert.Equal(DmarcMechanismResult.Fail, spf.Result);
        Assert.Null(spf.Selector);

        var dkimPass = record.AuthDetails.Single(d => d.Mechanism == DmarcAuthMechanism.Dkim && d.Selector == "default");
        Assert.Equal("contoso.io", dkimPass.Domain);
        Assert.Equal(DmarcMechanismResult.Pass, dkimPass.Result);

        var dkimTempError = record.AuthDetails.Single(d => d.Mechanism == DmarcAuthMechanism.Dkim && d.Selector == "backup");
        Assert.Equal("relay.contoso.io", dkimTempError.Domain);
        Assert.Equal(DmarcMechanismResult.TempError, dkimTempError.Result);
    }

    [Fact]
    public void Parse_MapsPolicyOverrideReasons()
    {
        var xmlBytes = File.ReadAllBytes("Fixtures/sample-report-with-detail.xml");

        var result = DmarcReportParser.Parse(xmlBytes);

        var reason = result.Records.Single().OverrideReasons.Single();
        Assert.Equal(DmarcPolicyOverrideType.LocalPolicy, reason.Type);
        Assert.Equal("arc allowed", reason.Comment);
    }

    [Fact]
    public void Parse_ReturnsEmptyAuthDetailsAndReasons_WhenTheReportHasNone()
    {
        var xmlBytes = File.ReadAllBytes("Fixtures/sample-report.xml");

        var result = DmarcReportParser.Parse(xmlBytes);

        var passing = result.Records.Single(r => r.SourceIp == "198.51.100.7");
        Assert.Empty(passing.OverrideReasons);
        Assert.Equal(2, passing.AuthDetails.Count); // this fixture's passing record already has one spf + one dkim entry
    }
```

Add `using DotMarc.Data;` to the top of `test/DotMarc.Tests/Ingestion/DmarcReportParserTests.cs`.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~DmarcReportParserTests`
Expected: FAIL - `ParsedReportRecord` has no `AuthDetails`/`OverrideReasons` members yet (compile error).

- [ ] **Step 4: Extend ParsedReport.cs**

Replace the full contents of `src/DotMarc/Ingestion/ParsedReport.cs`:
```csharp
using DotMarc.Data;

namespace DotMarc.Ingestion;

public sealed record ParsedReport(
    string Domain,
    string ReportingOrg,
    string ReportId,
    DateTimeOffset DateRangeBeginUtc,
    DateTimeOffset DateRangeEndUtc,
    IReadOnlyList<ParsedReportRecord> Records);

public sealed record ParsedReportRecord(
    string SourceIp,
    int MessageCount,
    string Disposition,
    string SpfResult,
    string DkimResult,
    string HeaderFrom,
    IReadOnlyList<ParsedAuthDetail> AuthDetails,
    IReadOnlyList<ParsedPolicyOverrideReason> OverrideReasons);

public sealed record ParsedAuthDetail(DmarcAuthMechanism Mechanism, string Domain, DmarcMechanismResult Result, string? Selector, string? Scope, string? HumanResult);

public sealed record ParsedPolicyOverrideReason(DmarcPolicyOverrideType Type, string? Comment);
```

- [ ] **Step 5: Extend DmarcReportParser.cs**

Modify `src/DotMarc/Ingestion/DmarcReportParser.cs` - add `using DotMarc.Data;` after the existing `using DmarcRua;` line, replace the `records` projection, and add two new private static methods:
```csharp
using DmarcRua;
using DotMarc.Data;

namespace DotMarc.Ingestion;

/// <summary>Wraps DmarcRua's AggregateReport parser. DmarcRua itself only throws for input it
/// cannot deserialize as XML at all (e.g. garbage bytes) - well-formed XML that fails schema
/// validation instead sets ValidReport = false without throwing (confirmed empirically against
/// DmarcRua 2.0.1). This wrapper treats both cases identically as failures, since PollingService's
/// failure handling (Task 6) needs a single exception type to catch.</summary>
public static class DmarcReportParser
{
    public static ParsedReport Parse(byte[] xmlBytes)
    {
        AggregateReport report;
        try
        {
            using var stream = new MemoryStream(xmlBytes);
            report = new AggregateReport(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException("Could not deserialize DMARC aggregate report XML.", ex);
        }

        if (!report.ValidReport || report.Feedback is null)
        {
            throw new InvalidDataException("DMARC aggregate report failed schema validation.");
        }

        var feedback = report.Feedback;
        var records = feedback.Record.Select(r => new ParsedReportRecord(
            r.Row.SourceIp,
            r.Row.Count,
            r.Row.PolicyEvaluated.Disposition.ToString(),
            r.Row.PolicyEvaluated.Spf.ToString(),
            r.Row.PolicyEvaluated.Dkim.ToString(),
            r.Identifiers.HeaderFrom,
            MapAuthDetails(r.AuthResults),
            MapOverrideReasons(r.Row.PolicyEvaluated.Reason))).ToList();

        return new ParsedReport(
            feedback.PolicyPublished.Domain,
            feedback.ReportMetadata.OrgName,
            feedback.ReportMetadata.ReportId,
            DateTimeOffset.FromUnixTimeSeconds(feedback.ReportMetadata.DateRange.Begin),
            DateTimeOffset.FromUnixTimeSeconds(feedback.ReportMetadata.DateRange.End),
            records);
    }

    private static List<ParsedAuthDetail> MapAuthDetails(AuthResultType authResults)
    {
        var details = new List<ParsedAuthDetail>();

        foreach (var dkim in authResults.Dkim ?? [])
        {
            details.Add(new ParsedAuthDetail(
                DmarcAuthMechanism.Dkim,
                dkim.Domain,
                Enum.Parse<DmarcMechanismResult>(dkim.Result.ToString()),
                dkim.Selector,
                null,
                dkim.HumanResult));
        }

        foreach (var spf in authResults.Spf ?? [])
        {
            details.Add(new ParsedAuthDetail(
                DmarcAuthMechanism.Spf,
                spf.Domain,
                Enum.Parse<DmarcMechanismResult>(spf.Result.ToString()),
                null,
                spf.Scope?.ToString(),
                spf.HumanResult));
        }

        return details;
    }

    private static List<ParsedPolicyOverrideReason> MapOverrideReasons(PolicyOverrideReason[]? reasons) =>
        (reasons ?? []).Select(r => new ParsedPolicyOverrideReason(Enum.Parse<DmarcPolicyOverrideType>(r.Type.ToString()), r.Comment)).ToList();
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~DmarcReportParserTests`
Expected: PASS, all tests including the three new ones and the two pre-existing ones.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Ingestion/ParsedReport.cs src/DotMarc/Ingestion/DmarcReportParser.cs test/DotMarc.Tests/Ingestion/DmarcReportParserTests.cs test/DotMarc.Tests/Fixtures/sample-report-with-detail.xml
git commit -m "Parse DMARC auth_results detail and policy-override reasons"
```

---

### Task 3: Ingestion wiring - persist auth detail and reasons, mark new reports backfilled

**Files:**
- Modify: `src/DotMarc/Ingestion/PollingService.cs`
- Test: `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs`

**Interfaces:**
- Consumes: `ParsedAuthDetail`, `ParsedPolicyOverrideReason` (Task 2), `ReportRecordAuthDetail`, `ReportRecordPolicyOverrideReason`, `Report.AuthDetailBackfilledUtc` (Task 1).
- Produces: every newly-ingested `Report` has `AuthDetailBackfilledUtc` set at insert time (so Task 5's backfill cycle never picks it up) and its `ReportRecord`s carry populated `AuthDetails`/`OverrideReasons` collections.

- [ ] **Step 1: Write the failing test**

Add to `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs` (uses the same reason+multi-DKIM shape as Task 2's fixture, inlined as a const so this test file stays self-contained like its existing `ValidReportXml`):
```csharp
    private const string ReportWithDetailXml = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <feedback>
          <report_metadata>
            <org_name>google.com</org_name>
            <email>noreply-dmarc-support@google.com</email>
            <report_id>detail-1</report_id>
            <date_range><begin>1754438400</begin><end>1754524800</end></date_range>
          </report_metadata>
          <policy_published><domain>contoso.io</domain><adkim>r</adkim><aspf>r</aspf><p>quarantine</p><sp>quarantine</sp><pct>100</pct></policy_published>
          <record>
            <row>
              <source_ip>203.0.113.50</source_ip>
              <count>12</count>
              <policy_evaluated>
                <disposition>none</disposition><dkim>pass</dkim><spf>fail</spf>
                <reason><type>local_policy</type><comment>arc allowed</comment></reason>
              </policy_evaluated>
            </row>
            <identifiers><header_from>contoso.io</header_from></identifiers>
            <auth_results>
              <spf><domain>envelope.contoso.io</domain><result>fail</result></spf>
              <dkim><domain>contoso.io</domain><result>pass</result><selector>default</selector></dkim>
            </auth_results>
          </record>
        </feedback>
        """;

    [Fact]
    public async Task PollOnceAsync_StoresAuthDetailAndOverrideReasons_AndMarksTheReportBackfilled()
    {
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-1", "Report domain: contoso.io", true));
        graphClient.Attachments["msg-1"] = [new MailboxAttachment("report.xml.gz", "application/gzip", GzipOf(ReportWithDetailXml))];

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        using (var verify = CreateContext())
        {
            var report = verify.Reports
                .Include(r => r.Records).ThenInclude(rec => rec.AuthDetails)
                .Include(r => r.Records).ThenInclude(rec => rec.OverrideReasons)
                .Single();

            Assert.NotNull(report.AuthDetailBackfilledUtc);

            var record = report.Records.Single();
            Assert.Equal(2, record.AuthDetails.Count);
            Assert.Contains(record.AuthDetails, d => d.Mechanism == DmarcAuthMechanism.Spf && d.Domain == "envelope.contoso.io" && d.Result == DmarcMechanismResult.Fail);
            Assert.Contains(record.AuthDetails, d => d.Mechanism == DmarcAuthMechanism.Dkim && d.Selector == "default" && d.Result == DmarcMechanismResult.Pass);

            var reason = record.OverrideReasons.Single();
            Assert.Equal(DmarcPolicyOverrideType.LocalPolicy, reason.Type);
            Assert.Equal("arc allowed", reason.Comment);
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~PollOnceAsync_StoresAuthDetailAndOverrideReasons`
Expected: FAIL - `AuthDetailBackfilledUtc` is null and `AuthDetails`/`OverrideReasons` are empty, since `StoreReportAsync` doesn't populate them yet.

- [ ] **Step 3: Wire the child collections into StoreReportAsync**

Modify `src/DotMarc/Ingestion/PollingService.cs` - find the `foreach (var record in parsed.Records)` loop inside `StoreReportAsync` (currently around line 1260) and replace it, along with the two lines directly after it:
```csharp
        foreach (var record in parsed.Records)
        {
            var reportRecord = new ReportRecord
            {
                SourceIp = record.SourceIp,
                MessageCount = record.MessageCount,
                Disposition = Enum.Parse<DispositionResult>(record.Disposition),
                SpfResult = Enum.Parse<AuthResult>(record.SpfResult),
                DkimResult = Enum.Parse<AuthResult>(record.DkimResult),
                HeaderFrom = record.HeaderFrom
            };

            foreach (var detail in record.AuthDetails)
            {
                reportRecord.AuthDetails.Add(new ReportRecordAuthDetail
                {
                    Mechanism = detail.Mechanism,
                    Domain = detail.Domain,
                    Result = detail.Result,
                    Selector = detail.Selector,
                    Scope = detail.Scope,
                    HumanResult = detail.HumanResult
                });
            }

            foreach (var reason in record.OverrideReasons)
            {
                reportRecord.OverrideReasons.Add(new ReportRecordPolicyOverrideReason
                {
                    Type = reason.Type,
                    Comment = reason.Comment
                });
            }

            report.Records.Add(reportRecord);
        }

        // A newly-ingested report already has its detail captured above, so it never needs the
        // historical backfill cycle (Task 5) to touch it - only pre-existing reports from before
        // this feature shipped start with this null.
        report.AuthDetailBackfilledUtc = DateTimeOffset.UtcNow;

        context.Reports.Add(report);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return domain;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~PollingServiceTests`
Expected: PASS, all tests including the new one and every pre-existing one (the existing `PollOnceAsync_ParsesAndStoresAValidReport_ThenMarksMessageRead` test's fixture has one SPF entry and no reason, so it isn't affected by this change beyond now also getting `AuthDetailBackfilledUtc` set - not asserted there, so no update needed to that test).

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Ingestion/PollingService.cs test/DotMarc.Tests/Ingestion/PollingServiceTests.cs
git commit -m "Persist DMARC auth detail and override reasons at ingestion time"
```

---

### Task 4: Proactive IP enrichment cycle

**Files:**
- Modify: `src/DotMarc/Ingestion/PollingService.cs`
- Modify: `test/DotMarc.Tests/Internal/FakeIpInfoLookup.cs`
- Test: `test/DotMarc.Tests/Ingestion/IpEnrichmentCycleTests.cs`

**Interfaces:**
- Consumes: `IpInfoService.GetCachedAsync`, `IpInfoService.NeedsLookup`, `IpInfoService.EnrichAsync` (all pre-existing, unchanged), `IIpInfoLookup` (pre-existing).
- Produces: `internal const long PollingService.IpEnrichmentLeaderLockKey = 84_200_021`, `internal async Task PollingService.RunIpEnrichmentCycleAsync(DotMarcDbContext context, IIpInfoLookup ipLookup, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)` - no other task depends on this signature, but it must not collide with any other cycle's advisory lock key (existing keys run 84_200_001 through 84_200_019; Task 5 takes 84_200_023, two higher, leaving this one clear).

- [ ] **Step 1: Extend FakeIpInfoLookup to support per-IP failure**

Modify `test/DotMarc.Tests/Internal/FakeIpInfoLookup.cs` - add a throw-list, matching the `ShouldThrow`-style convention other fakes in this directory already use:
```csharp
using DotMarc.Data;
using DotMarc.IpEnrichment;

namespace DotMarc.Tests.Internal;

internal sealed class FakeIpInfoLookup : IIpInfoLookup
{
    public IpLookupResult Result { get; set; } = new(IpLookupStatus.Ok, "Example Org", "US");
    public List<string> LookedUpIps { get; } = [];
    public HashSet<string> IpsToThrowFor { get; } = [];

    public Task<IpLookupResult> LookupAsync(string ip, CancellationToken cancellationToken)
    {
        LookedUpIps.Add(ip);
        if (IpsToThrowFor.Contains(ip))
        {
            throw new HttpRequestException($"Simulated failure for {ip}.");
        }
        return Task.FromResult(Result);
    }
}
```

- [ ] **Step 2: Write the failing tests**

`test/DotMarc.Tests/Ingestion/IpEnrichmentCycleTests.cs`:
```csharp
using DotMarc.Data;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class IpEnrichmentCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public IpEnrichmentCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static PollingService CreateService(DotMarcDbContext context) =>
        new(new FakeGraphMailboxClient(), context, NullLogger<PollingService>.Instance);

    private async Task SeedReportRecordAsync(string sourceIp)
    {
        await using var context = CreateContext();
        context.Domains.Add(new Domain
        {
            Name = $"domain-for-{sourceIp}.test",
            FirstSeenUtc = DateTimeOffset.UtcNow,
            Reports =
            {
                new Report
                {
                    ReportingOrg = "google.com",
                    ReportId = Guid.NewGuid().ToString(),
                    DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    DateRangeEndUtc = DateTimeOffset.UtcNow,
                    RawXml = "<feedback/>",
                    ReceivedUtc = DateTimeOffset.UtcNow,
                    AuthDetailBackfilledUtc = DateTimeOffset.UtcNow,
                    Records = { new ReportRecord { SourceIp = sourceIp, MessageCount = 1, HeaderFrom = "contoso.io" } }
                }
            }
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task RunIpEnrichmentCycleAsync_EnrichesASourceIpNeverLookedUpBefore()
    {
        await SeedReportRecordAsync("203.0.113.10");

        using var context = CreateContext();
        var lookup = new FakeIpInfoLookup { Result = new(IpLookupStatus.Ok, "Example Org", "US") };
        var service = CreateService(context);
        await service.RunIpEnrichmentCycleAsync(context, lookup, new FakeDbContextFactory(_connectionString), CancellationToken.None);

        Assert.Contains("203.0.113.10", lookup.LookedUpIps);

        using var verify = CreateContext();
        var info = verify.IpInfos.Single(i => i.Ip == "203.0.113.10");
        Assert.Equal("Example Org", info.Organization);
        Assert.Equal(IpLookupStatus.Ok, info.Status);
    }

    [Fact]
    public async Task RunIpEnrichmentCycleAsync_SkipsAnIpWithARecentSuccessfulLookup()
    {
        await SeedReportRecordAsync("203.0.113.11");
        using (var seed = CreateContext())
        {
            seed.IpInfos.Add(new IpInfo { Ip = "203.0.113.11", Organization = "Cached Org", Status = IpLookupStatus.Ok, LookedUpUtc = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using var context = CreateContext();
        var lookup = new FakeIpInfoLookup();
        var service = CreateService(context);
        await service.RunIpEnrichmentCycleAsync(context, lookup, new FakeDbContextFactory(_connectionString), CancellationToken.None);

        Assert.Empty(lookup.LookedUpIps);
    }

    [Fact]
    public async Task RunIpEnrichmentCycleAsync_RetriesAnIpWhoseFailedLookupIsOlderThanTheRetryWindow()
    {
        await SeedReportRecordAsync("203.0.113.12");
        using (var seed = CreateContext())
        {
            seed.IpInfos.Add(new IpInfo { Ip = "203.0.113.12", Status = IpLookupStatus.LookupFailed, LookedUpUtc = DateTimeOffset.UtcNow.AddHours(-25) });
            await seed.SaveChangesAsync();
        }

        using var context = CreateContext();
        var lookup = new FakeIpInfoLookup { Result = new(IpLookupStatus.Ok, "Now Reachable", "GB") };
        var service = CreateService(context);
        await service.RunIpEnrichmentCycleAsync(context, lookup, new FakeDbContextFactory(_connectionString), CancellationToken.None);

        Assert.Contains("203.0.113.12", lookup.LookedUpIps);
    }

    [Fact]
    public async Task RunIpEnrichmentCycleAsync_ContinuesPastOneFailingLookup()
    {
        await SeedReportRecordAsync("203.0.113.13");
        await SeedReportRecordAsync("203.0.113.14");

        using var context = CreateContext();
        var lookup = new FakeIpInfoLookup { Result = new(IpLookupStatus.Ok, "Example Org", "US") };
        lookup.IpsToThrowFor.Add("203.0.113.13");
        var service = CreateService(context);
        await service.RunIpEnrichmentCycleAsync(context, lookup, new FakeDbContextFactory(_connectionString), CancellationToken.None);

        using var verify = CreateContext();
        Assert.Null(verify.IpInfos.SingleOrDefault(i => i.Ip == "203.0.113.13"));
        Assert.NotNull(verify.IpInfos.SingleOrDefault(i => i.Ip == "203.0.113.14"));
    }

    [Fact]
    public async Task RunIpEnrichmentCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        await SeedReportRecordAsync("203.0.113.15");

        using var context = CreateContext();
        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.IpEnrichmentLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var lookup = new FakeIpInfoLookup();
        var service = CreateService(context);
        await service.RunIpEnrichmentCycleAsync(context, lookup, new FakeDbContextFactory(_connectionString), CancellationToken.None);

        Assert.Empty(lookup.LookedUpIps);

        await lockTransaction.RollbackAsync();
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~IpEnrichmentCycleTests`
Expected: FAIL - `RunIpEnrichmentCycleAsync` and `IpEnrichmentLeaderLockKey` don't exist yet (compile error).

- [ ] **Step 4: Add the leader lock key and cycle method**

Modify `src/DotMarc/Ingestion/PollingService.cs` - add the new lock key constant directly after the existing `DnsProviderCheckLeaderLockKey` line:
```csharp
    internal const long DnsProviderCheckLeaderLockKey = 84_200_019;
    internal const long IpEnrichmentLeaderLockKey = 84_200_021;
```

Add the new method directly after `RunDnsProviderCheckCycleAsync`'s closing brace (the method ends around line 552, right before the DMARC-authorization-check region begins):
```csharp
    private const int IpEnrichmentBatchSize = 25;

    /// <summary>Enriches source IPs proactively instead of waiting for a domain's Sources tab to
    /// be viewed (the only trigger before this cycle existed). Bounded per cycle so a large
    /// backlog after a bulk import doesn't turn one poll interval into a long RDAP hammering
    /// session - the remainder is simply picked up on the next cycle.</summary>
    internal async Task RunIpEnrichmentCycleAsync(DotMarcDbContext context, IIpInfoLookup ipLookup, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        bool acquired;
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", IpEnrichmentLeaderLockKey);
            acquired = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!acquired)
        {
            _logger.LogDebug("Another instance already holds the IP-enrichment lock for this cycle; skipping.");
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var candidateIps = await context.ReportRecords
            .Select(r => r.SourceIp)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cachedInfo = await IpInfoService.GetCachedAsync(context, candidateIps, cancellationToken).ConfigureAwait(false);

        var toEnrich = candidateIps
            .Where(ip => IpInfoService.NeedsLookup(cachedInfo.GetValueOrDefault(ip), nowUtc))
            .Take(IpEnrichmentBatchSize)
            .ToList();

        foreach (var ip in toEnrich)
        {
            try
            {
                await IpInfoService.EnrichAsync(dbFactory, ipLookup, ip, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IP enrichment failed for {SourceIp}; will retry next cycle.", ip);
            }
        }
    }
```

- [ ] **Step 5: Wire the cycle into ExecuteAsync**

Modify `src/DotMarc/Ingestion/PollingService.cs` - add a new try/catch block directly after the existing DNS-provider-check block (which ends `_logger.LogWarning(ex, "DNS provider check cycle failed; will retry next interval."); }`), before the `if (!string.IsNullOrWhiteSpace(_options!.TlsrptMailboxAddress))` block:
```csharp
                    try
                    {
                        context.ChangeTracker.Clear();
                        var ipLookup = scope.ServiceProvider.GetRequiredService<IIpInfoLookup>();
                        var ipDbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
                        await RunIpEnrichmentCycleAsync(context, ipLookup, ipDbFactory, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "IP enrichment cycle failed; will retry next interval.");
                    }
```

Add `using DotMarc.IpEnrichment;` to the top of `src/DotMarc/Ingestion/PollingService.cs` if not already present.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~IpEnrichmentCycleTests`
Expected: PASS, all 5 tests.

Run: `dotnet build`
Expected: 0 warnings, 0 errors (confirms the `ExecuteAsync` wiring compiles).

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Ingestion/PollingService.cs test/DotMarc.Tests/Internal/FakeIpInfoLookup.cs test/DotMarc.Tests/Ingestion/IpEnrichmentCycleTests.cs
git commit -m "Add proactive IP enrichment polling cycle"
```

---

### Task 5: Historical backfill cycle

**Files:**
- Modify: `src/DotMarc/Ingestion/PollingService.cs`
- Test: `test/DotMarc.Tests/Ingestion/AuthDetailBackfillCycleTests.cs`

**Interfaces:**
- Consumes: `DmarcReportParser.Parse` (Task 2), `Report.AuthDetailBackfilledUtc`, `ReportRecordAuthDetail`, `ReportRecordPolicyOverrideReason` (Task 1).
- Produces: `internal const long PollingService.AuthDetailBackfillLeaderLockKey = 84_200_023`, `internal async Task PollingService.RunAuthDetailBackfillCycleAsync(DotMarcDbContext context, CancellationToken cancellationToken)` - no other task depends on this signature.

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Ingestion/AuthDetailBackfillCycleTests.cs`:
```csharp
using DotMarc.Data;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public sealed class AuthDetailBackfillCycleTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AuthDetailBackfillCycleTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static PollingService CreateService(DotMarcDbContext context) =>
        new(new FakeGraphMailboxClient(), context, NullLogger<PollingService>.Instance);

    private const string RawXmlWithDetail = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <feedback>
          <report_metadata>
            <org_name>google.com</org_name>
            <email>noreply-dmarc-support@google.com</email>
            <report_id>backfill-1</report_id>
            <date_range><begin>1754438400</begin><end>1754524800</end></date_range>
          </report_metadata>
          <policy_published><domain>contoso.io</domain><adkim>r</adkim><aspf>r</aspf><p>quarantine</p><sp>quarantine</sp><pct>100</pct></policy_published>
          <record>
            <row>
              <source_ip>203.0.113.60</source_ip>
              <count>5</count>
              <policy_evaluated>
                <disposition>reject</disposition><dkim>fail</dkim><spf>fail</spf>
                <reason><type>other</type></reason>
              </policy_evaluated>
            </row>
            <identifiers><header_from>contoso.io</header_from></identifiers>
            <auth_results>
              <spf><domain>contoso.io</domain><result>fail</result></spf>
            </auth_results>
          </record>
        </feedback>
        """;

    private async Task<int> SeedUnbackfilledReportAsync()
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
        var report = new Report
        {
            ReportingOrg = "google.com",
            ReportId = "backfill-1",
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = RawXmlWithDetail,
            ReceivedUtc = DateTimeOffset.UtcNow,
            AuthDetailBackfilledUtc = null,
            Records = { new ReportRecord { SourceIp = "203.0.113.60", MessageCount = 5, HeaderFrom = "contoso.io", Disposition = DispositionResult.Reject, SpfResult = AuthResult.Fail, DkimResult = AuthResult.Fail } }
        };
        domain.Reports.Add(report);
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return report.Id;
    }

    [Fact]
    public async Task RunAuthDetailBackfillCycleAsync_PopulatesDetailForAnUnbackfilledReport_AndMarksItBackfilled()
    {
        var reportId = await SeedUnbackfilledReportAsync();

        using var context = CreateContext();
        var service = CreateService(context);
        await service.RunAuthDetailBackfillCycleAsync(context, CancellationToken.None);

        using var verify = CreateContext();
        var report = verify.Reports
            .Include(r => r.Records).ThenInclude(rec => rec.AuthDetails)
            .Include(r => r.Records).ThenInclude(rec => rec.OverrideReasons)
            .Single(r => r.Id == reportId);

        Assert.NotNull(report.AuthDetailBackfilledUtc);
        var record = report.Records.Single();
        Assert.Single(record.AuthDetails);
        Assert.Equal(DmarcMechanismResult.Fail, record.AuthDetails[0].Result);
        Assert.Single(record.OverrideReasons);
        Assert.Equal(DmarcPolicyOverrideType.Other, record.OverrideReasons[0].Type);
    }

    [Fact]
    public async Task RunAuthDetailBackfillCycleAsync_SkipsAReportAlreadyBackfilled()
    {
        await using (var context = CreateContext())
        {
            var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
            domain.Reports.Add(new Report
            {
                ReportingOrg = "google.com",
                ReportId = "already-done",
                DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
                DateRangeEndUtc = DateTimeOffset.UtcNow,
                RawXml = RawXmlWithDetail,
                ReceivedUtc = DateTimeOffset.UtcNow,
                AuthDetailBackfilledUtc = DateTimeOffset.UtcNow,
                Records = { new ReportRecord { SourceIp = "203.0.113.61", MessageCount = 1, HeaderFrom = "contoso.io" } }
            });
            context.Domains.Add(domain);
            await context.SaveChangesAsync();
        }

        using var context2 = CreateContext();
        var service = CreateService(context2);
        await service.RunAuthDetailBackfillCycleAsync(context2, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.ReportRecordAuthDetails);
    }

    [Fact]
    public async Task RunAuthDetailBackfillCycleAsync_SkipsAndMarksAReportWhoseRawXmlNoLongerParses_WithoutStoppingTheBatch()
    {
        int goodReportId;
        await using (var context = CreateContext())
        {
            var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
            var badReport = new Report
            {
                ReportingOrg = "google.com",
                ReportId = "bad-xml",
                DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
                DateRangeEndUtc = DateTimeOffset.UtcNow,
                RawXml = "not valid xml at all",
                ReceivedUtc = DateTimeOffset.UtcNow,
                AuthDetailBackfilledUtc = null,
                Records = { new ReportRecord { SourceIp = "203.0.113.62", MessageCount = 1, HeaderFrom = "contoso.io" } }
            };
            var goodReport = new Report
            {
                ReportingOrg = "google.com",
                ReportId = "backfill-1",
                DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
                DateRangeEndUtc = DateTimeOffset.UtcNow,
                RawXml = RawXmlWithDetail,
                ReceivedUtc = DateTimeOffset.UtcNow,
                AuthDetailBackfilledUtc = null,
                Records = { new ReportRecord { SourceIp = "203.0.113.60", MessageCount = 5, HeaderFrom = "contoso.io" } }
            };
            domain.Reports.Add(badReport);
            domain.Reports.Add(goodReport);
            context.Domains.Add(domain);
            await context.SaveChangesAsync();
            goodReportId = goodReport.Id;
        }

        using var context2 = CreateContext();
        var service = CreateService(context2);
        await service.RunAuthDetailBackfillCycleAsync(context2, CancellationToken.None);

        using var verify = CreateContext();
        var goodReport2 = verify.Reports.Include(r => r.Records).ThenInclude(rec => rec.AuthDetails).Single(r => r.Id == goodReportId);
        Assert.NotEmpty(goodReport2.Records.Single().AuthDetails);

        var badReport2 = verify.Reports.Single(r => r.ReportId == "bad-xml");
        Assert.NotNull(badReport2.AuthDetailBackfilledUtc); // marked handled so it isn't retried forever
    }

    [Fact]
    public async Task RunAuthDetailBackfillCycleAsync_SkipsEntirely_WhenAnotherInstanceHoldsTheLock()
    {
        await SeedUnbackfilledReportAsync();

        using var context = CreateContext();
        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", PollingService.AuthDetailBackfillLeaderLockKey);
            await lockCommand.ExecuteNonQueryAsync();
        }

        var service = CreateService(context);
        await service.RunAuthDetailBackfillCycleAsync(context, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.ReportRecordAuthDetails);

        await lockTransaction.RollbackAsync();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AuthDetailBackfillCycleTests`
Expected: FAIL - `RunAuthDetailBackfillCycleAsync` and `AuthDetailBackfillLeaderLockKey` don't exist yet (compile error).

- [ ] **Step 3: Add the leader lock key and cycle method**

Modify `src/DotMarc/Ingestion/PollingService.cs` - add the lock key directly after `IpEnrichmentLeaderLockKey`:
```csharp
    internal const long IpEnrichmentLeaderLockKey = 84_200_021;
    internal const long AuthDetailBackfillLeaderLockKey = 84_200_023;
```

Add the new method directly after `RunIpEnrichmentCycleAsync`'s closing brace:
```csharp
    private const int AuthDetailBackfillBatchSize = 25;

    /// <summary>One-time-per-report catch-up for reports ingested before this feature shipped:
    /// re-parses each report's stored RawXml and populates the AuthDetail/OverrideReason child
    /// rows its ReportRecords never got. Matches candidate reports by
    /// AuthDetailBackfilledUtc IS NULL (set unconditionally once a report is processed here,
    /// success or skip - not by "has any child rows", since a report with genuinely no
    /// auth_results detail would otherwise look perpetually unbackfilled and be retried forever).
    /// Bounded per cycle and resumable, same shape as RunIpEnrichmentCycleAsync.</summary>
    internal async Task RunAuthDetailBackfillCycleAsync(DotMarcDbContext context, CancellationToken cancellationToken)
    {
        var connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DotMarcDbContext has no connection string configured.");

        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        bool acquired;
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", lockConnection, lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", AuthDetailBackfillLeaderLockKey);
            acquired = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!acquired)
        {
            _logger.LogDebug("Another instance already holds the auth-detail-backfill lock for this cycle; skipping.");
            return;
        }

        var candidateReports = await context.Reports
            .Include(r => r.Records)
            .Where(r => r.AuthDetailBackfilledUtc == null)
            .OrderBy(r => r.Id)
            .Take(AuthDetailBackfillBatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var report in candidateReports)
        {
            try
            {
                var parsed = DmarcReportParser.Parse(System.Text.Encoding.UTF8.GetBytes(report.RawXml));

                // Both lists were derived from the same feedback.Record array in the same order
                // (see DmarcReportParser.Parse and StoreReportAsync's foreach) - Id ascending is a
                // safe, unambiguous ordinal proxy for "the order these were originally inserted in".
                var orderedRecords = report.Records.OrderBy(r => r.Id).ToList();

                if (orderedRecords.Count == parsed.Records.Count)
                {
                    for (var i = 0; i < orderedRecords.Count; i++)
                    {
                        var storedRecord = orderedRecords[i];
                        var parsedRecord = parsed.Records[i];

                        foreach (var detail in parsedRecord.AuthDetails)
                        {
                            storedRecord.AuthDetails.Add(new ReportRecordAuthDetail
                            {
                                Mechanism = detail.Mechanism,
                                Domain = detail.Domain,
                                Result = detail.Result,
                                Selector = detail.Selector,
                                Scope = detail.Scope,
                                HumanResult = detail.HumanResult
                            });
                        }

                        foreach (var reason in parsedRecord.OverrideReasons)
                        {
                            storedRecord.OverrideReasons.Add(new ReportRecordPolicyOverrideReason
                            {
                                Type = reason.Type,
                                Comment = reason.Comment
                            });
                        }
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Report {ReportId} has {StoredCount} stored records but re-parsing RawXml produced {ParsedCount}; leaving it without auth detail.",
                        report.Id, orderedRecords.Count, parsed.Records.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to backfill auth detail for report {ReportId}; RawXml may no longer be parseable.", report.Id);
            }

            // Marked handled either way - a permanently-unparseable RawXml or a genuine
            // record-count mismatch must not be retried forever on every future cycle.
            report.AuthDetailBackfilledUtc = DateTimeOffset.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Wire the cycle into ExecuteAsync**

Modify `src/DotMarc/Ingestion/PollingService.cs` - add another try/catch block directly after the IP-enrichment block added in Task 4, still before the TLSRPT block:
```csharp
                    try
                    {
                        context.ChangeTracker.Clear();
                        await RunAuthDetailBackfillCycleAsync(context, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Auth-detail backfill cycle failed; will retry next interval.");
                    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AuthDetailBackfillCycleTests`
Expected: PASS, all 4 tests.

Run: `dotnet test`
Expected: PASS, full suite (confirms nothing in Tasks 1-4 regressed).

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Ingestion/PollingService.cs test/DotMarc.Tests/Ingestion/AuthDetailBackfillCycleTests.cs
git commit -m "Add historical auth-detail backfill polling cycle"
```

---

### Task 6: Sources tab drill-down (aggregation + UI)

**Files:**
- Modify: `src/DotMarc/Reporting/DomainStatistics.cs`
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor`
- Test: `test/DotMarc.Tests/Reporting/DomainStatisticsTests.cs`

**Interfaces:**
- Consumes: `ReportRecord.AuthDetails`/`OverrideReasons` (Task 1), `DmarcAuthMechanism`, `DmarcMechanismResult`, `DmarcPolicyOverrideType` (Task 1).
- Produces: `AuthDetailSummary(DmarcAuthMechanism Mechanism, string Domain, DmarcMechanismResult Result)` - a new record in `DotMarc.Reporting`. `SourceAggregate` gains two new trailing fields: `IReadOnlyList<DmarcPolicyOverrideType> OverrideReasonTypes`, `IReadOnlyList<AuthDetailSummary> AuthDetails`. No later task consumes these directly (Task 7's `GetReasonBreakdown` reads `ReportRecord.OverrideReasons` directly, not through `SourceAggregate`).

- [ ] **Step 1: Write the failing test**

Add to `test/DotMarc.Tests/Reporting/DomainStatisticsTests.cs` (the file's existing `Record` helper only sets `SourceIp`/`MessageCount`/`SpfResult`/`DkimResult`/`Disposition`/`HeaderFrom` - this test builds its `ReportRecord` inline since it needs the new child collections too):
```csharp
    [Fact]
    public void GetSourceAggregates_CombinesOverrideReasonsAndAuthDetails_AcrossASourcesRecordsInWindow()
    {
        var recordA = Record("203.0.113.70", 5, AuthResult.Fail, AuthResult.Pass, DispositionResult.Quarantine);
        recordA.OverrideReasons.Add(new ReportRecordPolicyOverrideReason { Type = DmarcPolicyOverrideType.LocalPolicy });
        recordA.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Spf, Domain = "envelope.contoso.io", Result = DmarcMechanismResult.Fail });

        var recordB = Record("203.0.113.70", 3, AuthResult.Fail, AuthResult.Pass, DispositionResult.Quarantine);
        recordB.OverrideReasons.Add(new ReportRecordPolicyOverrideReason { Type = DmarcPolicyOverrideType.LocalPolicy }); // same type as recordA - must not duplicate
        recordB.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Dkim, Domain = "contoso.io", Result = DmarcMechanismResult.Pass, Selector = "default" });

        var report = ReportWith(recordA, recordB);

        var aggregates = DomainStatistics.GetSourceAggregates([report]);

        var source = aggregates.Single();
        Assert.Equal(8, source.Volume);
        Assert.Equal([DmarcPolicyOverrideType.LocalPolicy], source.OverrideReasonTypes);
        Assert.Equal(2, source.AuthDetails.Count);
        Assert.Contains(source.AuthDetails, d => d.Mechanism == DmarcAuthMechanism.Spf && d.Domain == "envelope.contoso.io" && d.Result == DmarcMechanismResult.Fail);
        Assert.Contains(source.AuthDetails, d => d.Mechanism == DmarcAuthMechanism.Dkim && d.Domain == "contoso.io" && d.Result == DmarcMechanismResult.Pass);
    }

    [Fact]
    public void GetSourceAggregates_ReturnsEmptyReasonsAndAuthDetails_WhenTheSourceHasNone()
    {
        var report = ReportWith(Record("198.51.100.50", 1, AuthResult.Pass, AuthResult.Pass));

        var source = DomainStatistics.GetSourceAggregates([report]).Single();

        Assert.Empty(source.OverrideReasonTypes);
        Assert.Empty(source.AuthDetails);
    }
```

Add `using DotMarc.Data;` to the top of `test/DotMarc.Tests/Reporting/DomainStatisticsTests.cs` if not already present (the existing `Record`/`ReportWith` helpers already reference `Data` types, so it's likely already there - verify before adding a duplicate).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~GetSourceAggregates_CombinesOverrideReasonsAndAuthDetails`
Expected: FAIL - `SourceAggregate` has no `OverrideReasonTypes`/`AuthDetails` members yet (compile error).

- [ ] **Step 3: Extend DomainStatistics.cs**

Modify `src/DotMarc/Reporting/DomainStatistics.cs` - replace `GetSourceAggregates` and the `SourceAggregate` record at the bottom of the file:
```csharp
    public static List<SourceAggregate> GetSourceAggregates(IEnumerable<Report> reportsInWindow) =>
        reportsInWindow
            .SelectMany(r => r.Records)
            .GroupBy(r => r.SourceIp)
            .Select(g => new SourceAggregate(
                g.Key,
                g.Sum(r => r.MessageCount),
                CombineAuthResult(g.Select(r => r.SpfResult)),
                CombineAuthResult(g.Select(r => r.DkimResult)),
                CombineDisposition(g.Select(r => r.Disposition)),
                g.SelectMany(r => r.OverrideReasons).Select(o => o.Type).Distinct().ToList(),
                g.SelectMany(r => r.AuthDetails).Select(d => new AuthDetailSummary(d.Mechanism, d.Domain, d.Result)).Distinct().ToList()))
            .ToList();
```

And the record declarations at the very end of the file:
```csharp
/// <summary>One source IP's aggregated activity within the report window.</summary>
public sealed record SourceAggregate(string SourceIp, int Volume, AuthResult SpfResult, AuthResult DkimResult, DispositionResult Disposition, IReadOnlyList<DmarcPolicyOverrideType> OverrideReasonTypes, IReadOnlyList<AuthDetailSummary> AuthDetails);

/// <summary>One distinct (mechanism, domain, result) combination seen for a source in-window -
/// deduplicated so a source failing the same way on every report doesn't repeat itself.</summary>
public sealed record AuthDetailSummary(DmarcAuthMechanism Mechanism, string Domain, DmarcMechanismResult Result);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~DomainStatisticsTests`
Expected: PASS, all tests including the two new ones.

- [ ] **Step 5: Extend the Sources tab query, row model, and table**

Modify `src/DotMarc/Components/Pages/DomainDetail.razor` - the `OnInitializedAsync` query (around line 417-426) needs the two new child collections included. Add two more `.Include`/`.ThenInclude` chains directly after the existing `.Include(d => d.Reports.Where(r => r.ReceivedUtc >= cutoff)).ThenInclude(r => r.Records)` line:
```csharp
        _domain = await db.Domains
            .AsNoTracking()
            .Include(d => d.Reports.Where(r => r.ReceivedUtc >= cutoff))
            .ThenInclude(r => r.Records)
            .Include(d => d.Reports.Where(r => r.ReceivedUtc >= cutoff))
            .ThenInclude(r => r.Records)
            .ThenInclude(rec => rec.AuthDetails)
            .Include(d => d.Reports.Where(r => r.ReceivedUtc >= cutoff))
            .ThenInclude(r => r.Records)
            .ThenInclude(rec => rec.OverrideReasons)
            .Include(d => d.TlsrptReports.Where(r => r.ReceivedUtc >= cutoff))
            .ThenInclude(r => r.Policies)
            .ThenInclude(policy => policy.FailureDetails)
            .Include(d => d.Groups)
            .SingleOrDefaultAsync(d => d.Name == DomainName);
```

The `_sources` projection (around line 453-456) needs to carry the new fields through to `SourceRow`:
```csharp
        _sources = DomainStatistics.GetSourceAggregates(_domain.Reports)
            .Select(a => new SourceRow(a.SourceIp, a.Volume, a.SpfResult.ToString(), a.DkimResult.ToString(), a.Disposition.ToString(), a.OverrideReasonTypes, a.AuthDetails))
            .OrderByDescending(s => s.Volume)
            .ToList();
```

The `SourceRow` class (near the bottom of the file, around line 1065) gains two constructor parameters and two helper methods:
```csharp
    private sealed class SourceRow(string sourceIp, int volume, string spfResult, string dkimResult, string disposition, IReadOnlyList<DmarcPolicyOverrideType> overrideReasonTypes, IReadOnlyList<AuthDetailSummary> authDetails)
    {
        public string SourceIp { get; } = sourceIp;
        public int Volume { get; } = volume;
        public string SpfResult { get; } = spfResult;
        public string DkimResult { get; } = dkimResult;
        public string Disposition { get; } = disposition;
        public IReadOnlyList<DmarcPolicyOverrideType> OverrideReasonTypes { get; } = overrideReasonTypes;
        public IReadOnlyList<AuthDetailSummary> AuthDetails { get; } = authDetails;
        public string? Organization { get; set; }
        public string? Country { get; set; }
        public bool IsEnrichmentPending { get; set; } = true;

        public string ReasonSummary => OverrideReasonTypes.Count == 0
            ? ""
            : string.Join(", ", OverrideReasonTypes.Select(t => t.ToString()));

        public string AuthDetailTooltip(DmarcAuthMechanism mechanism)
        {
            var matching = AuthDetails.Where(d => d.Mechanism == mechanism).ToList();
            return matching.Count == 0
                ? "No per-mechanism detail was reported for this source."
                : string.Join("; ", matching.Select(d => $"{d.Domain}: {d.Result}"));
        }
```

(This inserts before the existing `IsEnrichmentPending` line's closing - keep the class's existing closing brace as-is; only the constructor signature and the properties above `Organization` change, plus the two new members added.)

Add `using DotMarc.Reporting;` to the top of `DomainDetail.razor`'s `@using` block if `AuthDetailSummary`/`SourceAggregate` aren't already resolvable (the file already has `@using DotMarc.Reporting` - verify before adding a duplicate).

The Sources table itself (around line 313-343) gains a Reason column and tooltips on SPF/DKIM:
```razor
        <MudTabPanel Text="Sources">
            <MudTable Items="_sources" Hover="true" T="SourceRow">
                <HeaderContent>
                    <MudTh>Source IP</MudTh>
                    <MudTh>Volume</MudTh>
                    <MudTh>SPF</MudTh>
                    <MudTh>DKIM</MudTh>
                    <MudTh>Disposition</MudTh>
                    <MudTh>Reason</MudTh>
                    <MudTh>Owner</MudTh>
                    <MudTh>Country</MudTh>
                </HeaderContent>
                <RowTemplate>
                    <MudTd>@context.SourceIp</MudTd>
                    <MudTd>@context.Volume</MudTd>
                    <MudTd>
                        <MudTooltip Text="@context.AuthDetailTooltip(DmarcAuthMechanism.Spf)" Arrow="true">
                            <span>@context.SpfResult</span>
                        </MudTooltip>
                    </MudTd>
                    <MudTd>
                        <MudTooltip Text="@context.AuthDetailTooltip(DmarcAuthMechanism.Dkim)" Arrow="true">
                            <span>@context.DkimResult</span>
                        </MudTooltip>
                    </MudTd>
                    <MudTd>@context.Disposition</MudTd>
                    <MudTd>@context.ReasonSummary</MudTd>
                    <MudTd>
                        @if (context.IsEnrichmentPending)
                        {
                            <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                        }
                        else
                        {
                            @(context.Organization ?? "—")
                        }
                    </MudTd>
                    <MudTd>@(context.IsEnrichmentPending ? "" : (context.Country ?? "—"))</MudTd>
                </RowTemplate>
            </MudTable>
        </MudTabPanel>
```

- [ ] **Step 6: Build and manually verify**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

Start the app, open a monitored domain's Sources tab, confirm the new Reason column renders (blank for sources with no override) and hovering the SPF/DKIM badges shows a tooltip (either the "no detail reported" fallback text for historical rows not yet backfilled, or real detail once Task 5's backfill cycle has run).

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Reporting/DomainStatistics.cs src/DotMarc/Components/Pages/DomainDetail.razor test/DotMarc.Tests/Reporting/DomainStatisticsTests.cs
git commit -m "Show DMARC override reasons and auth detail on the Sources tab"
```

---

### Task 7: DomainStatistics.GetReasonBreakdown

**Files:**
- Modify: `src/DotMarc/Reporting/DomainStatistics.cs`
- Test: `test/DotMarc.Tests/Reporting/DomainStatisticsTests.cs`

**Interfaces:**
- Consumes: `ReportRecord.OverrideReasons`, `DmarcPolicyOverrideType` (Task 1).
- Produces: `public sealed record ReasonBreakdown(int BenignOverride, int LocalPolicy, int Other, int NoReasonGiven)` with a computed `int Total` property, `public static ReasonBreakdown GetReasonBreakdown(IEnumerable<Report> reportsInWindow)`, and `public static ReasonBreakdown GetReasonBreakdown(IEnumerable<IEnumerable<Report>> perDomainReportsInWindow)` - both consumed by Task 8 (per-domain panel), Task 9 (org-wide panel), and Task 11 (alerting).

- [ ] **Step 1: Write the failing tests**

Add to `test/DotMarc.Tests/Reporting/DomainStatisticsTests.cs`:
```csharp
    [Fact]
    public void GetReasonBreakdown_ExcludesNoneDispositionRecords()
    {
        var report = ReportWith(Record("198.51.100.60", 100, AuthResult.Pass, AuthResult.Pass, DispositionResult.None));

        var breakdown = DomainStatistics.GetReasonBreakdown([report]);

        Assert.Equal(0, breakdown.Total);
    }

    [Fact]
    public void GetReasonBreakdown_BucketsARejectWithNoReasonAsNoReasonGiven()
    {
        var report = ReportWith(Record("198.51.100.61", 50, AuthResult.Fail, AuthResult.Fail, DispositionResult.Reject));

        var breakdown = DomainStatistics.GetReasonBreakdown([report]);

        Assert.Equal(50, breakdown.NoReasonGiven);
        Assert.Equal(0, breakdown.BenignOverride);
    }

    [Fact]
    public void GetReasonBreakdown_BucketsATrustedForwarderReasonAsBenignOverride()
    {
        var record = Record("198.51.100.62", 30, AuthResult.Fail, AuthResult.Pass, DispositionResult.Quarantine);
        record.OverrideReasons.Add(new ReportRecordPolicyOverrideReason { Type = DmarcPolicyOverrideType.TrustedForwarder });
        var report = ReportWith(record);

        var breakdown = DomainStatistics.GetReasonBreakdown([report]);

        Assert.Equal(30, breakdown.BenignOverride);
    }

    [Fact]
    public void GetReasonBreakdown_PrefersBenignOverLocalPolicy_WhenARecordHasBoth()
    {
        var record = Record("198.51.100.63", 20, AuthResult.Fail, AuthResult.Fail, DispositionResult.Reject);
        record.OverrideReasons.Add(new ReportRecordPolicyOverrideReason { Type = DmarcPolicyOverrideType.LocalPolicy });
        record.OverrideReasons.Add(new ReportRecordPolicyOverrideReason { Type = DmarcPolicyOverrideType.MailingList });
        var report = ReportWith(record);

        var breakdown = DomainStatistics.GetReasonBreakdown([report]);

        Assert.Equal(20, breakdown.BenignOverride);
        Assert.Equal(0, breakdown.LocalPolicy);
    }

    [Fact]
    public void GetReasonBreakdown_BucketsAnOtherOnlyReasonAsOther()
    {
        var record = Record("198.51.100.64", 15, AuthResult.Fail, AuthResult.Fail, DispositionResult.Reject);
        record.OverrideReasons.Add(new ReportRecordPolicyOverrideReason { Type = DmarcPolicyOverrideType.Other });
        var report = ReportWith(record);

        var breakdown = DomainStatistics.GetReasonBreakdown([report]);

        Assert.Equal(15, breakdown.Other);
    }

    [Fact]
    public void GetReasonBreakdown_MultiDomainOverload_SumsAcrossDomains()
    {
        var domainA = ReportWith(Record("198.51.100.65", 10, AuthResult.Fail, AuthResult.Fail, DispositionResult.Reject));
        var domainB = ReportWith(Record("198.51.100.66", 40, AuthResult.Fail, AuthResult.Fail, DispositionResult.Reject));

        var breakdown = DomainStatistics.GetReasonBreakdown([[domainA], [domainB]]);

        Assert.Equal(50, breakdown.NoReasonGiven);
        Assert.Equal(50, breakdown.Total);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~GetReasonBreakdown`
Expected: FAIL - `GetReasonBreakdown` and `ReasonBreakdown` don't exist yet (compile error).

- [ ] **Step 3: Implement GetReasonBreakdown**

Modify `src/DotMarc/Reporting/DomainStatistics.cs` - add these methods to the `DomainStatistics` class, directly after `GetSourceAggregates` (and before its private helpers):
```csharp
    /// <summary>Buckets every Reject/Quarantine record's message volume by why it was
    /// disposed-against - the direct signal for "does this look like benign forwarding or a real
    /// spoofing attempt". Disposition == None records are excluded (nothing to explain). A record
    /// with multiple reason entries buckets by priority: Benign wins if any entry is
    /// Forwarded/SampledOut/TrustedForwarder/MailingList (one benign explanation is enough), else
    /// LocalPolicy wins if any entry is LocalPolicy, else Other. No reason at all is the least
    /// benign-looking signal, since a real receiver usually only omits &lt;reason&gt; when
    /// disposition matches assessment exactly.</summary>
    public static ReasonBreakdown GetReasonBreakdown(IEnumerable<Report> reportsInWindow)
    {
        int benign = 0, localPolicy = 0, other = 0, noReason = 0;

        foreach (var record in reportsInWindow.SelectMany(r => r.Records).Where(r => r.Disposition != DispositionResult.None))
        {
            var reasonTypes = record.OverrideReasons.Select(o => o.Type).ToList();

            if (reasonTypes.Count == 0)
            {
                noReason += record.MessageCount;
            }
            else if (reasonTypes.Any(IsBenignOverride))
            {
                benign += record.MessageCount;
            }
            else if (reasonTypes.Contains(DmarcPolicyOverrideType.LocalPolicy))
            {
                localPolicy += record.MessageCount;
            }
            else
            {
                other += record.MessageCount;
            }
        }

        return new ReasonBreakdown(benign, localPolicy, other, noReason);
    }

    /// <summary>Same bucketing as the single-domain overload, summed across every supplied
    /// domain's in-window reports - mirrors GetOverallPassRate's existing multi-domain shape.</summary>
    public static ReasonBreakdown GetReasonBreakdown(IEnumerable<IEnumerable<Report>> perDomainReportsInWindow) =>
        GetReasonBreakdown(perDomainReportsInWindow.SelectMany(reports => reports));

    private static bool IsBenignOverride(DmarcPolicyOverrideType type) =>
        type is DmarcPolicyOverrideType.Forwarded or DmarcPolicyOverrideType.SampledOut or DmarcPolicyOverrideType.TrustedForwarder or DmarcPolicyOverrideType.MailingList;
```

Add the `ReasonBreakdown` record at the bottom of the file, alongside `SourceAggregate`/`AuthDetailSummary`:
```csharp
/// <summary>Reject/Quarantine message volume in a window, bucketed by why it happened. See
/// DomainStatistics.GetReasonBreakdown for the bucketing rules.</summary>
public sealed record ReasonBreakdown(int BenignOverride, int LocalPolicy, int Other, int NoReasonGiven)
{
    public int Total => BenignOverride + LocalPolicy + Other + NoReasonGiven;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~DomainStatisticsTests`
Expected: PASS, all tests including the six new ones.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Reporting/DomainStatistics.cs test/DotMarc.Tests/Reporting/DomainStatisticsTests.cs
git commit -m "Add DomainStatistics.GetReasonBreakdown"
```

---

### Task 8: Per-domain insights panel

**Files:**
- Modify: `src/DotMarc/Components/Pages/DomainDetail.razor`

**Interfaces:**
- Consumes: `DomainStatistics.GetReasonBreakdown(IEnumerable<Report>)`, `ReasonBreakdown` (Task 7).

No dedicated test file (UI-only, matches this codebase's established convention of verifying Razor changes by build + manual check - the underlying `GetReasonBreakdown` logic is already covered by Task 7's tests).

- [ ] **Step 1: Add the reason-breakdown panel to the Overview tab**

Modify `src/DotMarc/Components/Pages/DomainDetail.razor` - insert a new `MudPaper` directly after the existing `<MudChart ChartType="ChartType.Line" ... />` line and before the `<MudPaper Class="pa-4 mt-4" Elevation="1">` that holds "Domain health" (around line 62-63):
```razor
            <MudChart ChartType="ChartType.Line" ChartSeries="@_series" ChartLabels="@_xAxisLabels" Width="100%" Height="250px" />
            @if (_reasonBreakdown is { Total: > 0 } breakdown)
            {
                <MudPaper Class="pa-4 mt-4" Elevation="1">
                    <MudText Typo="Typo.subtitle1" Class="mb-2">Why rejects/quarantines happened (30d)</MudText>
                    <MudGrid>
                        <MudItem xs="12" sm="5">
                            <MudChart ChartType="ChartType.Donut" InputData="@_reasonData" InputLabels="@_reasonLabels" Width="220px" Height="220px" />
                        </MudItem>
                        <MudItem xs="12" sm="7" Class="d-flex align-center">
                            <MudText Typo="Typo.body2">
                                Of @breakdown.Total rejected/quarantined messages: @breakdown.BenignOverride benign override
                                (forwarders/mailing lists), @breakdown.LocalPolicy local policy, @breakdown.Other other,
                                @breakdown.NoReasonGiven no reason given.
                            </MudText>
                        </MudItem>
                    </MudGrid>
                </MudPaper>
            }
            <MudPaper Class="pa-4 mt-4" Elevation="1">
```

- [ ] **Step 2: Compute the breakdown in OnInitializedAsync**

Modify `src/DotMarc/Components/Pages/DomainDetail.razor` - add two new private fields near the existing `_series`/`_xAxisLabels` fields (in the `@code` block):
```csharp
    private ReasonBreakdown? _reasonBreakdown;
    private double[] _reasonData = [];
    private string[] _reasonLabels = ["Benign override", "Local policy", "Other", "No reason given"];
```

Add the computation directly after the existing `_series = [...]` assignment at the end of `OnInitializedAsync`:
```csharp
        _reasonBreakdown = DomainStatistics.GetReasonBreakdown(_domain.Reports);
        _reasonData = [_reasonBreakdown.BenignOverride, _reasonBreakdown.LocalPolicy, _reasonBreakdown.Other, _reasonBreakdown.NoReasonGiven];
    }
```

(This closing brace is `OnInitializedAsync`'s own - the addition goes as the method's last two statements, directly before its existing closing brace.)

- [ ] **Step 3: Build and manually verify**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

Start the app, open a monitored domain with reject/quarantine volume in its 30-day window, confirm the new panel renders a donut chart and the summary sentence with correct counts. Open a domain with zero reject/quarantine volume and confirm the panel is hidden entirely (not an empty/zeroed chart).

- [ ] **Step 4: Commit**

```bash
git add src/DotMarc/Components/Pages/DomainDetail.razor
git commit -m "Add per-domain reject/quarantine reason breakdown panel"
```

---

### Task 9: Org-wide insights panel

**Files:**
- Modify: `src/DotMarc/Reporting/DashboardSummary.cs`
- Modify: `src/DotMarc/Components/Pages/Dashboard.razor`
- Test: `test/DotMarc.Tests/Reporting/DashboardSummaryTests.cs`

**Interfaces:**
- Consumes: `DomainStatistics.GetReasonBreakdown(IEnumerable<IEnumerable<Report>>)`, `ReasonBreakdown` (Task 7).
- Produces: `DashboardSummary` gains a trailing `ReasonBreakdown ReasonBreakdown` positional field. No later task depends on this.

- [ ] **Step 1: Check for an existing DashboardSummary test file**

Run: `dotnet test --list-tests --filter FullyQualifiedName~DashboardSummaryTests` (or check the file directly) to see whether `test/DotMarc.Tests/Reporting/DashboardSummaryTests.cs` already exists. If it does, add the step below's test to it and skip creating a new file. If it doesn't exist, create it fresh with the namespace/imports shown below plus the one test.

- [ ] **Step 2: Write the failing test**

```csharp
using DotMarc.Data;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public class DashboardSummaryTests
{
    [Fact]
    public void Build_ComputesReasonBreakdown_AcrossAllSuppliedDomains()
    {
        var domainA = new Domain { Name = "a.test", FirstSeenUtc = DateTimeOffset.UtcNow, SortOrder = 0 };
        domainA.Reports.Add(new Report
        {
            ReportingOrg = "google.com",
            ReportId = "r1",
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = "<feedback/>",
            ReceivedUtc = DateTimeOffset.UtcNow,
            Records = { new ReportRecord { SourceIp = "203.0.113.1", MessageCount = 40, Disposition = DispositionResult.Reject, SpfResult = AuthResult.Fail, DkimResult = AuthResult.Fail, HeaderFrom = "a.test" } }
        });

        var (summary, _) = DashboardSummary.Build([domainA], parseFailureCount: 0);

        Assert.Equal(40, summary.ReasonBreakdown.NoReasonGiven);
        Assert.Equal(40, summary.ReasonBreakdown.Total);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~DashboardSummaryTests`
Expected: FAIL - `DashboardSummary` has no `ReasonBreakdown` member yet (compile error).

- [ ] **Step 4: Extend DashboardSummary.cs**

Replace the full contents of `src/DotMarc/Reporting/DashboardSummary.cs`:
```csharp
using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Aggregate stats shown across the Dashboard's summary tiles, plus the per-domain table
/// rows derived from the same data. <see cref="Build"/> takes a fully-loaded domain list (Reports
/// already filtered to the report window by the caller's EF query) and a parse-failure count,
/// keeping this calculation testable without EF or Blazor - same "pure core, thin I/O adapter"
/// split as <see cref="DomainStatistics"/>.</summary>
public sealed record DashboardSummary(int DomainCount, double OverallPassRate, int WarningCount, int MissingCount, int ParseFailureCount, int SourceCount, ReasonBreakdown ReasonBreakdown)
{
    public static (DashboardSummary Summary, List<DashboardDomainRow> Rows) Build(IReadOnlyList<Domain> domains, int parseFailureCount)
    {
        var rows = domains
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .Select(d =>
            {
                var passRate = DomainStatistics.GetPassRate(d.Reports);

                var isNullRouted = d.SpfCheckStatus == SpfCheckStatus.NullSpf;
                var missingReport = d.IsMonitored && !isNullRouted && (d.LastReportReceivedUtc is null || d.LastReportReceivedUtc < DateTimeOffset.UtcNow.AddDays(-2));
                var status = missingReport ? "Missing" : passRate is null or >= 0.95 ? "OK" : "Warning";
                var color = status switch { "Missing" => Color.Error, "Warning" => Color.Warning, _ => Color.Success };

                return new DashboardDomainRow(d.Id, d.Name, status, color, passRate, d.LastReportReceivedUtc, d.IsMonitored, d.DmarcCheckStatus, d.DmarcAuthorizationCheckStatus, d.MtaStsStatus, d.SpfCheckStatus);
            })
            .ToList();

        var sourceCount = domains.SelectMany(d => d.Reports).Select(r => r.ReportingOrg).Distinct().Count();

        var summary = new DashboardSummary(
            rows.Count,
            DomainStatistics.GetOverallPassRate(domains.Select(d => (IEnumerable<Report>)d.Reports)),
            rows.Count(r => r.Status == "Warning"),
            rows.Count(r => r.Status == "Missing"),
            parseFailureCount,
            sourceCount,
            DomainStatistics.GetReasonBreakdown(domains.Select(d => (IEnumerable<Report>)d.Reports)));

        return (summary, rows);
    }
}

/// <summary>One domain's row in the Dashboard's table.</summary>
public sealed record DashboardDomainRow(int Id, string Name, string Status, Color StatusColor, double? PassRate, DateTimeOffset? LastReportReceivedUtc, bool IsMonitored, DmarcCheckStatus DmarcCheckStatus, DmarcAuthorizationCheckStatus DmarcAuthorizationCheckStatus, MtaStsStatus MtaStsStatus, SpfCheckStatus SpfCheckStatus);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~DashboardSummaryTests`
Expected: PASS.

- [ ] **Step 6: Include OverrideReasons in Dashboard's query, and add the panel**

Modify `src/DotMarc/Components/Pages/Dashboard.razor` - the `LoadAsync` query (around line 229-233) needs `OverrideReasons` included (not `AuthDetails` - the org-wide panel only needs reason types, not per-mechanism auth detail):
```csharp
        var query = db.Domains
            .AsNoTracking()
            .Include(d => d.Reports.Where(r => r.ReceivedUtc >= cutoff))
            .ThenInclude(r => r.Records)
            .ThenInclude(rec => rec.OverrideReasons)
            .AsQueryable();
```

Add the new panel directly after the closing `</MudGrid>` of the stat-tile grid (around line 88) and before the `<MudTable Items="_rows" ...>`:
```razor
    </MudGrid>

    @if (_summary.ReasonBreakdown.Total > 0)
    {
        <MudPaper Class="pa-4 mb-4" Elevation="1">
            <MudText Typo="Typo.subtitle1" Class="mb-2">Why rejects/quarantines happened, across every monitored domain (30d)</MudText>
            <MudGrid>
                <MudItem xs="12" sm="5">
                    <MudChart ChartType="ChartType.Donut"
                              InputData="@(new double[] { _summary.ReasonBreakdown.BenignOverride, _summary.ReasonBreakdown.LocalPolicy, _summary.ReasonBreakdown.Other, _summary.ReasonBreakdown.NoReasonGiven })"
                              InputLabels="@(new[] { "Benign override", "Local policy", "Other", "No reason given" })"
                              Width="220px" Height="220px" />
                </MudItem>
                <MudItem xs="12" sm="7" Class="d-flex align-center">
                    <MudText Typo="Typo.body2">
                        Of @_summary.ReasonBreakdown.Total rejected/quarantined messages: @_summary.ReasonBreakdown.BenignOverride benign
                        override (forwarders/mailing lists), @_summary.ReasonBreakdown.LocalPolicy local policy,
                        @_summary.ReasonBreakdown.Other other, @_summary.ReasonBreakdown.NoReasonGiven no reason given.
                    </MudText>
                </MudItem>
            </MudGrid>
        </MudPaper>
    }

    <MudTable Items="_rows" Hover="true" T="DashboardDomainRow" CustomHeader="true">
```

- [ ] **Step 7: Build and manually verify**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

Start the app, open the Dashboard, confirm the new panel renders above the domain table when there's reject/quarantine volume across any monitored domain, and stays hidden when there's none.

- [ ] **Step 8: Commit**

```bash
git add src/DotMarc/Reporting/DashboardSummary.cs src/DotMarc/Components/Pages/Dashboard.razor test/DotMarc.Tests/Reporting/DashboardSummaryTests.cs
git commit -m "Add org-wide reject/quarantine reason breakdown panel"
```

---

### Task 10: SuspiciousRejectActivity settings

**Files:**
- Modify: `src/DotMarc/Notifications/NotificationSettings.cs`
- Modify: `src/DotMarc/Notifications/NotificationSettingsService.cs`
- Modify: `src/DotMarc/Components/Pages/AlertsSettings.razor`
- Create: EF Core migration (generated, verified by hand)

**Interfaces:**
- Produces: `NotificationSettings.SuspiciousRejectMinVolume` (int, default 10), `NotificationSettings.SuspiciousRejectNonBenignPercent` (int, default 50) - consumed by Task 11's alerting check.

No dedicated test file - `NotificationSettingsService` has no existing test file of its own either (its `SaveAsync`/`GetAsync` are exercised indirectly through `AlertingServiceTests`'s `SeedSettingsAsync` helper); Task 11's tests exercise these new fields' actual effect.

- [ ] **Step 1: Add the two settings**

Modify `src/DotMarc/Notifications/NotificationSettings.cs` - add two properties after `MonitorIntervalSeconds`:
```csharp
    public int MonitorIntervalSeconds { get; set; } = 300;
    public int SuspiciousRejectMinVolume { get; set; } = 10;
    public int SuspiciousRejectNonBenignPercent { get; set; } = 50;
}
```

- [ ] **Step 2: Persist them in SaveAsync**

Modify `src/DotMarc/Notifications/NotificationSettingsService.cs` - add two lines directly after `existing.MonitorIntervalSeconds = updated.MonitorIntervalSeconds;`:
```csharp
        existing.MonitorIntervalSeconds = updated.MonitorIntervalSeconds;
        existing.SuspiciousRejectMinVolume = updated.SuspiciousRejectMinVolume;
        existing.SuspiciousRejectNonBenignPercent = updated.SuspiciousRejectNonBenignPercent;
```

- [ ] **Step 3: Generate and verify the migration**

Run: `dotnet ef migrations add AddSuspiciousRejectAlertSettings --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj`

Expected: a migration adding two non-nullable int columns to `NotificationSettings`. **Verify by hand**: since `NotificationSettings` has an existing seeded row (`Id = 1` via `HasData`) and these are non-nullable columns, the generated `AddColumn` calls must each carry `defaultValue: 10` / `defaultValue: 50` (EF Core derives this from the C# property initializers automatically when the model snapshot already reflects them) - without a default, applying this migration against a database that already has the seeded row would fail. If the generated migration is missing either `defaultValue`, add it by hand before proceeding.

- [ ] **Step 4: Add the settings UI**

Modify `src/DotMarc/Components/Pages/AlertsSettings.razor` - add two `MudNumericField` items directly after the existing "Monitor interval (seconds)" `MudItem`, before the closing `</MudGrid>`:
```razor
            <MudItem xs="12" md="6">
                <MudNumericField Label="Monitor interval (seconds)" @bind-Value="_settings.MonitorIntervalSeconds" Min="30" Max="86400" Variant="Variant.Outlined" />
            </MudItem>

            <MudItem xs="12" md="6">
                <MudNumericField Label="Suspicious reject minimum volume" @bind-Value="_settings.SuspiciousRejectMinVolume" Min="1" Max="1000000" Variant="Variant.Outlined"
                                  HelperText="Don't alert below this many rejected/quarantined messages in a domain's 30-day window." />
            </MudItem>

            <MudItem xs="12" md="6">
                <MudNumericField Label="Suspicious reject non-benign %" @bind-Value="_settings.SuspiciousRejectNonBenignPercent" Min="1" Max="100" Variant="Variant.Outlined"
                                  HelperText="Alert when at least this % of rejects/quarantines have no benign override reason (forwarder/mailing list/sampling)." />
            </MudItem>
        </MudGrid>
```

- [ ] **Step 5: Build and manually verify**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

Start the app, open Alert settings, confirm the two new fields render, save, and reload correctly.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Notifications/NotificationSettings.cs src/DotMarc/Notifications/NotificationSettingsService.cs src/DotMarc/Components/Pages/AlertsSettings.razor src/DotMarc/Migrations/
git commit -m "Add SuspiciousRejectActivity alert threshold settings"
```

---

### Task 11: SuspiciousRejectActivity alert check

**Files:**
- Modify: `src/DotMarc/Notifications/AlertingService.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertingServiceTests.cs`

**Interfaces:**
- Consumes: `DomainStatistics.GetReasonBreakdown(IEnumerable<Report>)`, `ReasonBreakdown` (Task 7), `NotificationSettings.SuspiciousRejectMinVolume`/`SuspiciousRejectNonBenignPercent` (Task 10).
- Produces: nothing new consumed by later tasks - this is a leaf.

**Note:** `CheckPinnedDomainsAsync`'s existing three-way branch uses `continue` after each `if`. This task restructures those into `if`/`else if`/`else` (behavior-identical - the three conditions were already mutually exclusive) so a new unconditional check can run for every domain regardless of which branch it took, without duplicating the call at three separate `continue` points.

- [ ] **Step 1: Extend the test helpers**

Modify `test/DotMarc.Tests/Notifications/AlertingServiceTests.cs` - extend `SeedSettingsAsync` with two new optional parameters:
```csharp
    private async Task SeedSettingsAsync(bool enabled = true, int missingReportThresholdDays = 2, int cooldownMinutes = 180, int suspiciousRejectMinVolume = 10, int suspiciousRejectNonBenignPercent = 50)
    {
        await using var context = CreateContext();
        await NotificationSettingsService.SaveAsync(context, new NotificationSettings
        {
            Enabled = enabled,
            DeliveryMode = "Teams",
            TeamsWebhookUrl = "https://example.test/webhook",
            MissingReportThresholdDays = missingReportThresholdDays,
            CooldownMinutes = cooldownMinutes,
            SuspiciousRejectMinVolume = suspiciousRejectMinVolume,
            SuspiciousRejectNonBenignPercent = suspiciousRejectNonBenignPercent
        });
    }
```

Add a new seed helper directly after `SeedNullRoutedDomainAsync`:
```csharp
    private async Task SeedMonitoredDomainWithRejectsAsync(string name, DateTimeOffset lastReportReceivedUtc, params (int MessageCount, DmarcPolicyOverrideType? ReasonType)[] rejectedRecords)
    {
        await using var context = CreateContext();
        var domain = new Domain
        {
            Name = name,
            IsMonitored = true,
            FirstSeenUtc = DateTimeOffset.UtcNow.AddDays(-10),
            LastReportReceivedUtc = lastReportReceivedUtc
        };
        var report = new Report
        {
            ReportingOrg = "google.com",
            ReportId = Guid.NewGuid().ToString(),
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = "<feedback/>",
            ReceivedUtc = lastReportReceivedUtc,
            AuthDetailBackfilledUtc = DateTimeOffset.UtcNow
        };
        foreach (var (messageCount, reasonType) in rejectedRecords)
        {
            var record = new ReportRecord { SourceIp = "203.0.113.9", MessageCount = messageCount, Disposition = DispositionResult.Reject, SpfResult = AuthResult.Fail, DkimResult = AuthResult.Fail, HeaderFrom = name };
            if (reasonType is { } type)
            {
                record.OverrideReasons.Add(new ReportRecordPolicyOverrideReason { Type = type });
            }
            report.Records.Add(record);
        }
        domain.Reports.Add(report);
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
    }
```

- [ ] **Step 2: Write the failing tests**

Add to `test/DotMarc.Tests/Notifications/AlertingServiceTests.cs`:
```csharp
    [Fact]
    public async Task CheckPinnedDomainsAsync_CreatesSuspiciousRejectActivityAlert_WhenRejectsAreMostlyNonBenign()
    {
        await SeedSettingsAsync(suspiciousRejectMinVolume: 10, suspiciousRejectNonBenignPercent: 50);
        await SeedMonitoredDomainWithRejectsAsync("contoso.io", DateTimeOffset.UtcNow, (20, null));

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        var alert = await verifyContext.AlertEvents.SingleAsync(e => e.AlertType == "SuspiciousRejectActivity");
        Assert.Equal("contoso.io", alert.DomainName);
        Assert.False(alert.IsResolved);
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_DoesNotCreateSuspiciousRejectActivityAlert_WhenRejectsAreMostlyBenign()
    {
        await SeedSettingsAsync(suspiciousRejectMinVolume: 10, suspiciousRejectNonBenignPercent: 50);
        await SeedMonitoredDomainWithRejectsAsync("contoso.io", DateTimeOffset.UtcNow, (20, DmarcPolicyOverrideType.TrustedForwarder));

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        Assert.False(await verifyContext.AlertEvents.AnyAsync(e => e.AlertType == "SuspiciousRejectActivity"));
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_DoesNotCreateSuspiciousRejectActivityAlert_BelowMinVolume()
    {
        await SeedSettingsAsync(suspiciousRejectMinVolume: 100, suspiciousRejectNonBenignPercent: 50);
        await SeedMonitoredDomainWithRejectsAsync("contoso.io", DateTimeOffset.UtcNow, (20, null));

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        Assert.False(await verifyContext.AlertEvents.AnyAsync(e => e.AlertType == "SuspiciousRejectActivity"));
    }

    [Fact]
    public async Task CheckPinnedDomainsAsync_ResolvesSuspiciousRejectActivityAlert_OnceRatioDropsBelowThreshold()
    {
        await SeedSettingsAsync(suspiciousRejectMinVolume: 10, suspiciousRejectNonBenignPercent: 50, cooldownMinutes: 0);
        await SeedMonitoredDomainWithRejectsAsync("contoso.io", DateTimeOffset.UtcNow, (20, null));

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);
        await service.CheckPinnedDomainsAsync();

        await using (var midContext = CreateContext())
        {
            Assert.True(await midContext.AlertEvents.AnyAsync(e => e.AlertType == "SuspiciousRejectActivity" && !e.IsResolved));
        }

        // The underlying activity now looks benign - mutate the existing record's reasons in
        // place rather than reseeding, so this is still the same domain/report the first check saw.
        await using (var mutate = CreateContext())
        {
            var record = await mutate.ReportRecords.Include(r => r.OverrideReasons).SingleAsync(r => r.SourceIp == "203.0.113.9");
            record.OverrideReasons.Add(new ReportRecordPolicyOverrideReason { Type = DmarcPolicyOverrideType.MailingList });
            await mutate.SaveChangesAsync();
        }

        await service.CheckPinnedDomainsAsync();

        await using var verifyContext = CreateContext();
        var alert = await verifyContext.AlertEvents.Where(e => e.AlertType == "SuspiciousRejectActivity").OrderByDescending(e => e.CreatedUtc).FirstAsync();
        Assert.True(alert.IsResolved);
    }
```

Add `using DotMarc.Reporting;` to the top of the test file if not already present.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SuspiciousRejectActivity`
Expected: FAIL - no `SuspiciousRejectActivity` alert is ever created, since `CheckPinnedDomainsAsync` doesn't check for it yet.

- [ ] **Step 4: Implement the check**

Modify `src/DotMarc/Notifications/AlertingService.cs` - replace `CheckPinnedDomainsAsync`'s body from the `var domains = ...` line through the end of its `foreach` loop:
```csharp
        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-settings.MissingReportThresholdDays);
        var reasonWindowCutoffUtc = DomainStatistics.GetWindowCutoffUtc();
        var domains = await db.Domains
            .AsNoTracking()
            .Where(d => d.IsMonitored)
            .Include(d => d.Reports.Where(r => r.ReceivedUtc >= reasonWindowCutoffUtc))
            .ThenInclude(r => r.Records)
            .ThenInclude(rec => rec.OverrideReasons)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var domain in domains)
        {
            if (domain.SpfCheckStatus == SpfCheckStatus.NullSpf)
            {
                // Null-routed (SPF v=spf1 -all): no reports is the expected, healthy state, not a
                // problem - resolve any pre-existing alert from before the domain became
                // null-routed and skip the missing-report check entirely for it.
                await ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);

                // UnexpectedActivityOnNullRoutedDomain resolves only once the domain has gone
                // quiet again (no report within the missing-report threshold window). This branch
                // runs unconditionally for every null-routed domain each cycle, so resolving it
                // unconditionally here would auto-close the alert on the very next poll after it
                // fires, defeating its purpose of surfacing unexpected activity for an admin to
                // see. Same threshold semantics as MissedReport, just inverted: fires when a
                // report unexpectedly arrives, stays open while reports keep arriving within the
                // threshold window, auto-resolves once activity stops for the threshold period.
                if (domain.LastReportReceivedUtc is null || domain.LastReportReceivedUtc < cutoffUtc)
                {
                    await ResolveAlertAsync(domain.Name, "UnexpectedActivityOnNullRoutedDomain", cancellationToken).ConfigureAwait(false);
                }
            }
            else if (domain.LastReportReceivedUtc is { } lastReport && lastReport >= cutoffUtc)
            {
                await ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var message = domain.LastReportReceivedUtc is { } receivedUtc
                    ? $"The monitored domain '{domain.Name}' has not received a DMARC report since {receivedUtc:O}."
                    : $"The monitored domain '{domain.Name}' has not received a DMARC report yet.";
                await EnsureAlertAsync(db, settings, domain.Name, "MissedReport", "Warning", "Missing expected DMARC report", message, cancellationToken).ConfigureAwait(false);
            }

            // Independent of the report-freshness branch above - a domain can be reporting fine
            // AND have a reject mix worth flagging, so this always runs.
            await CheckSuspiciousRejectActivityAsync(db, settings, domain, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CheckSuspiciousRejectActivityAsync(DotMarcDbContext context, NotificationSettings settings, Domain domain, CancellationToken cancellationToken)
    {
        var breakdown = DomainStatistics.GetReasonBreakdown(domain.Reports);
        var nonBenign = breakdown.LocalPolicy + breakdown.Other + breakdown.NoReasonGiven;
        var nonBenignPercent = breakdown.Total == 0 ? 0 : (double)nonBenign / breakdown.Total * 100;

        if (breakdown.Total >= settings.SuspiciousRejectMinVolume && nonBenignPercent >= settings.SuspiciousRejectNonBenignPercent)
        {
            var message = $"'{domain.Name}' rejected/quarantined {breakdown.Total} message(s) in the last 30 days, and {nonBenignPercent:F0}% of those had no benign override reason (forwarder/mailing list/sampling) - this looks like more than benign forwarding.";
            await EnsureAlertAsync(context, settings, domain.Name, "SuspiciousRejectActivity", "Warning", "Reject activity looks like more than benign forwarding", message, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ResolveAlertAsync(domain.Name, "SuspiciousRejectActivity", cancellationToken).ConfigureAwait(false);
        }
    }
```

Add `using DotMarc.Reporting;` to the top of `src/DotMarc/Notifications/AlertingService.cs` if not already present.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AlertingServiceTests`
Expected: PASS, all tests including the four new ones and every pre-existing one (the restructured `if`/`else if`/`else` must not change any existing `MissedReport`/`UnexpectedActivityOnNullRoutedDomain` test's outcome).

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Notifications/AlertingService.cs test/DotMarc.Tests/Notifications/AlertingServiceTests.cs
git commit -m "Add SuspiciousRejectActivity alert check"
```

---

### Task 12: Enrich the null-routed-domain alert with reason context

**Files:**
- Modify: `src/DotMarc/Notifications/AlertingService.cs`
- Modify: `src/DotMarc/Ingestion/PollingService.cs`
- Modify: `test/DotMarc.Tests/Internal/FakeAlertingService.cs`
- Test: `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs`
- Test: `test/DotMarc.Tests/Notifications/AlertingServiceTests.cs`

**Interfaces:**
- Consumes: `ReasonBreakdown`, `DomainStatistics.GetReasonBreakdown(IEnumerable<Report>)` (Task 7).
- Produces: `IAlertingService.FlagUnexpectedActivityForNullRoutedDomainAsync` gains a `ReasonBreakdown reasonBreakdown` parameter (second positional, before the optional `CancellationToken`) - this is the plan's last interface change.

- [ ] **Step 1: Write the failing tests**

Add to `test/DotMarc.Tests/Notifications/AlertingServiceTests.cs`:
```csharp
    [Fact]
    public async Task FlagUnexpectedActivityForNullRoutedDomainAsync_MentionsForwarding_WhenTheBreakdownIsBenignDominant()
    {
        await SeedSettingsAsync();
        await SeedNullRoutedDomainAsync("contoso.io");

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.FlagUnexpectedActivityForNullRoutedDomainAsync("contoso.io", new ReasonBreakdown(BenignOverride: 90, LocalPolicy: 0, Other: 0, NoReasonGiven: 10));

        await using var verifyContext = CreateContext();
        var alert = await verifyContext.AlertEvents.SingleAsync(e => e.AlertType == "UnexpectedActivityOnNullRoutedDomain");
        Assert.Contains("forwarder or mailing list", alert.Message);
    }

    [Fact]
    public async Task FlagUnexpectedActivityForNullRoutedDomainAsync_MentionsSpoofing_WhenTheBreakdownIsNotBenignDominant()
    {
        await SeedSettingsAsync();
        await SeedNullRoutedDomainAsync("contoso.io");

        var fakeNotifier = new FakeAlertWebhookClient();
        var service = new AlertingService(new FakeDbContextFactory(_connectionString), fakeNotifier, CreateNoOpPsaTicketService(), NullLogger<AlertingService>.Instance);

        await service.FlagUnexpectedActivityForNullRoutedDomainAsync("contoso.io", new ReasonBreakdown(BenignOverride: 0, LocalPolicy: 0, Other: 0, NoReasonGiven: 40));

        await using var verifyContext = CreateContext();
        var alert = await verifyContext.AlertEvents.SingleAsync(e => e.AlertType == "UnexpectedActivityOnNullRoutedDomain");
        Assert.Contains("genuine spoofing attempt", alert.Message);
    }
```

Add `using DotMarc.Reporting;` to the top of `test/DotMarc.Tests/Notifications/AlertingServiceTests.cs` if not already present (Task 11 may have already added it).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~FlagUnexpectedActivityForNullRoutedDomainAsync`
Expected: FAIL - compile error, `FlagUnexpectedActivityForNullRoutedDomainAsync` doesn't accept a `ReasonBreakdown` argument yet.

- [ ] **Step 3: Extend the interface and implementation**

Modify `src/DotMarc/Notifications/AlertingService.cs` - change the interface method signature:
```csharp
    Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default);
```

Replace the `FlagUnexpectedActivityForNullRoutedDomainAsync` implementation:
```csharp
    public async Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        // Benign-dominant (at least half the reject/quarantine volume has a forwarder/mailing-list/
        // sampling override reason) reads as "probably not an attack"; anything else, including no
        // reject/quarantine volume at all yet, adds no extra sentence rather than guessing.
        var reasonContext = reasonBreakdown.Total == 0
            ? ""
            : reasonBreakdown.BenignOverride * 2 >= reasonBreakdown.Total
                ? " This looks like a forwarder or mailing list, not spoofing."
                : " No benign override reason was given - this looks like a genuine spoofing attempt.";

        var message = $"'{domainName}' is marked null-routed (SPF v=spf1 -all - no authorized senders) but a DMARC aggregate report just arrived showing mail activity. This may be legitimate traffic that needs accounting for, or a spoofing attempt.{reasonContext}";
        await EnsureAlertAsync(db, settings, domainName, "UnexpectedActivityOnNullRoutedDomain", "Warning", "Unexpected mail activity on a null-routed domain", message, cancellationToken).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Update FakeAlertingService**

Replace the full contents of `test/DotMarc.Tests/Internal/FakeAlertingService.cs`:
```csharp
using DotMarc.Notifications;
using DotMarc.Reporting;

namespace DotMarc.Tests.Internal;

internal sealed class FakeAlertingService : IAlertingService
{
    public List<string> ResolvedDomains { get; } = [];
    public List<string> FlaggedNullRoutedDomains { get; } = [];
    public List<ReasonBreakdown> FlaggedReasonBreakdowns { get; } = [];

    public Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default)
    {
        ResolvedDomains.Add(domainName);
        return Task.CompletedTask;
    }

    public Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default)
    {
        FlaggedNullRoutedDomains.Add(domainName);
        FlaggedReasonBreakdowns.Add(reasonBreakdown);
        return Task.CompletedTask;
    }
}
```

(`FlaggedNullRoutedDomains` keeps its existing `List<string>` shape so the two pre-existing assertions in `PollingServiceTests.cs` - `Assert.Contains("contoso.io", alertingService.FlaggedNullRoutedDomains)` and `Assert.Empty(alertingService.FlaggedNullRoutedDomains)` - keep compiling and passing unchanged.)

- [ ] **Step 5: Compute and pass the breakdown from PollingService**

Modify `src/DotMarc/Ingestion/PollingService.cs` - `StoreReportAsync`'s return type changes from `Task<Domain>` to `Task<(Domain Domain, ReasonBreakdown ReasonBreakdown)>`. Its signature line and both `return` statements change:
```csharp
    private async Task<(Domain Domain, ReasonBreakdown ReasonBreakdown)> StoreReportAsync(DotMarcDbContext context, ParsedReport parsed, string rawXml, CancellationToken cancellationToken)
    {
```

The `isDuplicate` early return (nothing new was stored, so there's no new activity to characterize):
```csharp
        if (isDuplicate)
        {
            // Same report already stored from an earlier attempt at this message (see the
            // MarkAsReadAsync-failure handling in ProcessMessageAsync). Nothing to insert - the
            // caller still retries marking the message read.
            return (domain, new ReasonBreakdown(0, 0, 0, 0));
        }
```

The end of the method, directly after the `report.AuthDetailBackfilledUtc = DateTimeOffset.UtcNow;` line added in Task 3:
```csharp
        report.AuthDetailBackfilledUtc = DateTimeOffset.UtcNow;

        context.Reports.Add(report);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (domain, DomainStatistics.GetReasonBreakdown([report]));
    }
```

Update `ProcessMessageAsync`'s call site:
```csharp
                var (domain, reasonBreakdown) = await StoreReportAsync(context, parsed, System.Text.Encoding.UTF8.GetString(decompressed), cancellationToken).ConfigureAwait(false);
                await RecordProcessedMessageAsync(context, message.Id, cancellationToken).ConfigureAwait(false);

                if (_alertingService is not null)
                {
                    await _alertingService.ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);

                    if (domain.SpfCheckStatus == SpfCheckStatus.NullSpf)
                    {
                        await _alertingService.FlagUnexpectedActivityForNullRoutedDomainAsync(domain.Name, reasonBreakdown, cancellationToken).ConfigureAwait(false);
                    }
                }
```

Add `using DotMarc.Reporting;` to the top of `src/DotMarc/Ingestion/PollingService.cs` if not already present.

- [ ] **Step 6: Add a PollingService-level test for the pass-through**

Add to `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs` (reuses `ReportWithDetailXml` from Task 3's test, whose one record has `<reason><type>local_policy</type></reason>` and `disposition=none` - since disposition is `none`, `GetReasonBreakdown` excludes it, so this specific fixture yields an empty breakdown; a dedicated reject fixture is used here instead to get a non-empty one):
```csharp
    private const string NullRoutedRejectReportXml = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <feedback>
          <report_metadata>
            <org_name>google.com</org_name>
            <email>noreply-dmarc-support@google.com</email>
            <report_id>null-routed-1</report_id>
            <date_range><begin>1754438400</begin><end>1754524800</end></date_range>
          </report_metadata>
          <policy_published><domain>contoso.io</domain><adkim>r</adkim><aspf>r</aspf><p>reject</p><sp>reject</sp><pct>100</pct></policy_published>
          <record>
            <row>
              <source_ip>203.0.113.80</source_ip>
              <count>25</count>
              <policy_evaluated><disposition>reject</disposition><dkim>fail</dkim><spf>fail</spf></policy_evaluated>
            </row>
            <identifiers><header_from>contoso.io</header_from></identifiers>
            <auth_results><spf><domain>contoso.io</domain><result>fail</result></spf></auth_results>
          </record>
        </feedback>
        """;

    [Fact]
    public async Task PollOnceAsync_PassesTheNewReportsReasonBreakdown_ToTheNullRoutedFlag()
    {
        using (var seed = CreateContext())
        {
            seed.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, SpfCheckStatus = SpfCheckStatus.NullSpf });
            await seed.SaveChangesAsync();
        }

        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-1", "Report domain: contoso.io", true));
        graphClient.Attachments["msg-1"] = [new MailboxAttachment("report.xml.gz", "application/gzip", GzipOf(NullRoutedRejectReportXml))];

        var alertingService = new FakeAlertingService();
        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, alertingService, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        var breakdown = Assert.Single(alertingService.FlaggedReasonBreakdowns);
        Assert.Equal(25, breakdown.NoReasonGiven);
        Assert.Equal(25, breakdown.Total);
    }
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AlertingServiceTests`
Expected: PASS, all tests including the two new ones.

Run: `dotnet test --filter FullyQualifiedName~PollingServiceTests`
Expected: PASS, all tests including the new one and every pre-existing one (the two `FlaggedNullRoutedDomains` assertions in the existing null-routed tests still compile and pass unchanged).

Run: `dotnet test`
Expected: PASS, full suite - this is the last task, so this is the final confirmation before the whole-branch review.

- [ ] **Step 8: Commit**

```bash
git add src/DotMarc/Notifications/AlertingService.cs src/DotMarc/Ingestion/PollingService.cs test/DotMarc.Tests/Internal/FakeAlertingService.cs test/DotMarc.Tests/Ingestion/PollingServiceTests.cs test/DotMarc.Tests/Notifications/AlertingServiceTests.cs
git commit -m "Pass the incoming report's reason breakdown to the null-routed-domain alert"
```

---

## Final Verification

- [ ] Run the full test suite one more time: `dotnet test` - expect PASS, no failures.
- [ ] Run `dotnet build` - expect 0 warnings, 0 errors.
- [ ] Confirm every migration applies cleanly from a fresh database (already exercised by every Postgres-backed test class's `InitializeAsync`, but worth a final `dotnet ef database update` sanity check against a scratch database if time allows).
- [ ] Skim the spec's Non-goals once more: `ReportRecord.SpfResult`/`DkimResult`/`Disposition` untouched (confirmed - no task modifies them), no time-series/trend view built (confirmed - only the current-window donut panels), no ML/heuristic scoring (confirmed - `SuspiciousRejectActivity` is a plain volume+ratio threshold), `CloudflareDnsPushProvider.cs`/`AzureDnsPushProvider.cs`/`GoogleCloudDnsPushProvider.cs`/`TlsrptFailureDetail` untouched (confirmed - none appear in any task's file list).
- [ ] Note for whoever deploys this: the two new `PollingService` cycles (IP enrichment, auth-detail backfill) both run automatically on the existing poll interval - no configuration needed to enable them. The backfill cycle will take several cycles to work through a large existing report history (25 reports/cycle); this is expected and self-limiting, not a bug to chase.
