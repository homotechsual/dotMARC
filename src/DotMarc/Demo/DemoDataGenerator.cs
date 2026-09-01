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
                status: DmarcCheckStatus.Ok, detail: null, daysOfHistory: HistoryDays,
                mtaStsEnabled: true, mtaStsMode: MtaStsMode.Enforce, mtaStsStatus: MtaStsStatus.Active,
                mtaStsDetail: "Policy is live and being enforced."),
            BuildDomain(random, nowUtc, sortOrder: 1, name: "shop.aurora-retail.example", groupName: "Aurora Retail",
                orgs: ["google.com", "yahoo.com"], passRateForDay: _ => 0.996,
                status: DmarcCheckStatus.Ok, detail: null, daysOfHistory: HistoryDays,
                mtaStsEnabled: true, mtaStsMode: MtaStsMode.Testing, mtaStsStatus: MtaStsStatus.Active,
                mtaStsDetail: "Policy is live in testing mode."),
            BuildDomain(random, nowUtc, sortOrder: 2, name: "brightline-legal.example", groupName: "Brightline Legal",
                orgs: ["google.com", "outlook.com"], passRateForDay: day => Lerp(0.93, 0.995, day / (double)(HistoryDays - 1)),
                status: DmarcCheckStatus.Ok, detail: null, daysOfHistory: HistoryDays,
                mtaStsEnabled: true, mtaStsMode: MtaStsMode.Enforce, mtaStsStatus: MtaStsStatus.PendingCertificate,
                mtaStsDetail: "DNS resolved; a TLS certificate is being issued."),
            BuildDomain(random, nowUtc, sortOrder: 3, name: "cobalt-freight.example", groupName: "Cobalt Freight",
                orgs: ["google.com", "outlook.com"], passRateForDay: _ => 0.87,
                status: DmarcCheckStatus.Ok, detail: null, daysOfHistory: HistoryDays,
                mtaStsEnabled: true, mtaStsMode: MtaStsMode.Enforce, mtaStsStatus: MtaStsStatus.Failed,
                mtaStsDetail: "Certificate renewal failed: mta-sts.cobalt-freight.example no longer resolves to the hosting hostname."),
            BuildDomain(random, nowUtc, sortOrder: 4, name: "fleet.cobalt-freight.example", groupName: "Cobalt Freight",
                orgs: ["google.com"], passRateForDay: _ => 0.98,
                status: DmarcCheckStatus.Ok, detail: null, daysOfHistory: HistoryDays - 4,
                mtaStsEnabled: true, mtaStsMode: MtaStsMode.Testing, mtaStsStatus: MtaStsStatus.PendingDns,
                mtaStsDetail: "Waiting for mta-sts.fleet.cobalt-freight.example to resolve."),
            BuildDomain(random, nowUtc, sortOrder: 5, name: "driftwood-media.example", groupName: "Driftwood Media",
                orgs: ["yahoo.com", "protonmail.com"], passRateForDay: _ => 0.85,
                status: DmarcCheckStatus.MissingAuthorizationRecord,
                detail: "No TXT record found at driftwood-media.example._report._dmarc.nova-msp.example",
                daysOfHistory: HistoryDays),
            BuildDomain(random, nowUtc, sortOrder: 6, name: "driftwood-events.example", groupName: null,
                orgs: ["google.com"], passRateForDay: _ => 0.97,
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
        string[] orgs, Func<int, double> passRateForDay, DmarcCheckStatus status, string? detail, int daysOfHistory,
        bool mtaStsEnabled = false, MtaStsMode mtaStsMode = MtaStsMode.Testing, MtaStsStatus mtaStsStatus = MtaStsStatus.NotConfigured,
        string? mtaStsDetail = null)
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
                    records.Add(new DemoRecordSeed(ProblemSourceIp(sortOrder), failingVolume, AuthResult.Fail, AuthResult.Fail,
                        failingVolume > totalVolume / 4 ? DispositionResult.Quarantine : DispositionResult.None));
                }

                reports.Add(new DemoReportSeed(org, $"demo-{name}-{org}-{day:D3}", rangeBegin, rangeEnd, records));
                lastReportReceivedUtc = rangeEnd;
            }
        }

        return new DemoDomainSeed(
            name, groupName, sortOrder, nowUtc.AddDays(-HistoryDays), lastReportReceivedUtc, status, detail, reports,
            MtaStsEnabled: mtaStsEnabled,
            MtaStsMode: mtaStsMode,
            MtaStsStatus: mtaStsStatus,
            MtaStsCheckedUtc: mtaStsEnabled ? nowUtc.AddHours(-3) : null,
            MtaStsCheckDetail: mtaStsDetail,
            MtaStsMaxAgeSeconds: 604_800,
            MtaStsMxHosts: mtaStsEnabled ? [$"mail.{name}", $"mail2.{name}"] : []);
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
    /// each shows up as its own row in that domain's Sources tab. Not a real allocation. Derived
    /// from the domain's sortOrder (not domainName.GetHashCode(), which .NET Core randomizes
    /// per-process) so the same domain always gets the same IP across container restarts, per
    /// this class's own reproducibility contract and the design spec's "a given day's dataset is
    /// reproducible if the container restarts without crossing a reset boundary."</summary>
    private static string ProblemSourceIp(int sortOrder) =>
        "203.0.113." + (10 + (sortOrder * 37 % 200));

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
        // for texture, without depending on a low-probability random draw. Rewrite a cycle in
        // the MIDDLE of the window rather than the last one: Dashboard.razor shows the most
        // recent poll cycle's status prominently, so forcing the failure onto cycles[^1] would
        // make the demo's landing page show a failed "last poll" on the rare reset where the
        // random roll above never fires.
        if (!failureInjected && cycles.Count > 0)
        {
            var middleIndex = cycles.Count / 2;
            var middle = cycles[middleIndex];
            cycles[middleIndex] = middle with { Succeeded = false, ErrorMessage = "Graph API request timed out.", MessagesChecked = 0, ReportsParsed = 0 };
        }

        return cycles;
    }

    private static List<DemoPollCycleDailySummarySeed> BuildPollCycleDailySummaries(Random random, DateTimeOffset nowUtc)
    {
        var summaries = new List<DemoPollCycleDailySummarySeed>();
        for (var day = RawPollCycleDays + 1; day < HistoryDays; day++)
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
