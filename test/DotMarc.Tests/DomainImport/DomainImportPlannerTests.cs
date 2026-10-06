using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DomainImport;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.DomainImport;

public sealed class DomainImportPlannerTests
{
    private static ImportTable Table(string csv) => ImportTable.FromRows(CsvImportReader.Read(csv));

    private static ExistingDomain Existing(string name, IReadOnlyList<string>? groups = null, bool monitored = true,
        bool mtaStsEnabled = false, IReadOnlyList<string>? mxHosts = null) =>
        new(Math.Abs(name.GetHashCode()) % 10_000 + 1, name, groups ?? [], [], null, monitored, [], mtaStsEnabled, MtaStsMode.Testing, mxHosts ?? [], 604_800);

    private static ImportSnapshot Snapshot(IReadOnlyList<ExistingDomain>? domains = null, IReadOnlyList<HaloClient>? haloClients = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? lookedUpMx = null) =>
        new((domains ?? []).ToDictionary(domain => domain.Name),
            ["Client A", "Client B", "Contoso Limited"], ["primary", "europe"],
            haloClients, haloClients is null ? "HaloPSA isn't connected." : null,
            lookedUpMx ?? new Dictionary<string, IReadOnlyList<string>>());

    private static ImportPlan Plan(string csv, ImportSnapshot? snapshot = null, ExistingDomainMode mode = ExistingDomainMode.Add,
        ImportPermissions? permissions = null, Dictionary<NameKey, NameResolution>? resolutions = null) =>
        DomainImportPlanner.Plan(Table(csv), snapshot ?? Snapshot(), mode, permissions ?? ImportPermissions.All, resolutions);

    private static PlannedRow Row(ImportPlan plan, string domain) => plan.Rows.Single(row => row.Domain == domain && row.Status != ImportRowStatus.Duplicate);

    [Fact]
    public void Rows_GetNewExistingAndInvalidStatuses()
    {
        var plan = Plan("new.com\nold.com\nhttps://bad.com/", Snapshot([Existing("old.com")]));

        Assert.Equal([ImportRowStatus.New, ImportRowStatus.AlreadyMonitored, ImportRowStatus.Invalid], plan.Rows.Select(row => row.Status));
        Assert.Contains("web address", plan.Rows[2].InvalidReason);
        Assert.Equal((1, 1), (plan.NewCount, plan.InvalidCount));
    }

    [Fact]
    public void TheSameDomainWrittenTwoWays_IsOneDomain()
    {
        var plan = Plan("domain,groups,monitored\nContoso.com,Client A,yes\ncontoso.com.,Client B,no");

        Assert.Equal([ImportRowStatus.New, ImportRowStatus.Duplicate], plan.Rows.Select(row => row.Status));
        Assert.Equal(2, plan.Rows[1].MergedIntoLine);
        var target = plan.Rows[0].Target!;
        Assert.Equal(["Client A", "Client B"], target.Groups!.Add);
        Assert.False(target.Monitored);
    }

    [Fact]
    public void SkipMode_LeavesExistingDomainsAlone()
    {
        var plan = Plan("domain,groups\nold.com,Client A", Snapshot([Existing("old.com")]), ExistingDomainMode.Skip);

        var row = plan.Rows.Single();
        Assert.Null(row.Target);
        Assert.Empty(row.Changes);
        Assert.Equal(1, plan.SkippedExistingCount);
    }

    [Fact]
    public void SkipMode_DoesntListOrCreateNamesOnlySkippedRowsUse()
    {
        var plan = Plan("domain,groups,tags\nnew.com,,\nold.com,Client Z,brand-new-tag", Snapshot([Existing("old.com")]), ExistingDomainMode.Skip);

        Assert.Empty(plan.UnknownNames);
        Assert.Empty(plan.GroupsToCreate);
        Assert.Empty(plan.TagsToCreate);
    }

    [Fact]
    public void AddMode_AddsGroups_AndRemovesDashEntries_ShowingTheRemoval()
    {
        var plan = Plan("domain,groups\nold.com,Client B;-Client A", Snapshot([Existing("old.com", groups: ["Client A"])]), ExistingDomainMode.Add);

        var row = plan.Rows.Single();
        Assert.Equal([new AuditFieldChange("Groups", "Client A", "Client B")], row.Changes);
        Assert.Equal(["Group \"Client A\" removed"], row.Removals);
        Assert.Equal(1, plan.UpdateCount);
    }

    [Fact]
    public void MatchMode_ClearsGroupsForABlankCell_OnlyWhenTheColumnExists()
    {
        var existing = Snapshot([Existing("old.com", groups: ["Client A"])]);

        var withColumn = Plan("domain,groups\nold.com,", existing, ExistingDomainMode.Match).Rows.Single();
        var withoutColumn = Plan("domain,monitored\nold.com,yes", existing, ExistingDomainMode.Match).Rows.Single();

        Assert.Equal(["Group \"Client A\" removed"], withColumn.Removals);
        Assert.True(withColumn.Target!.Groups!.ReplaceAll);
        Assert.Empty(withoutColumn.Removals);
        Assert.Null(withoutColumn.Target);
    }

    [Fact]
    public void UnknownNames_AreListedOnce_AndACaseOnlyDifferenceIsKnown()
    {
        var plan = Plan("domain,groups,tags\na.com,client a;Client C,Primary\nb.com,Client C,");

        var unknown = Assert.Single(plan.UnknownNames);
        Assert.Equal((ImportNameKind.Group, "Client C"), (unknown.Kind, unknown.Name));
        Assert.Equal([2, 3], unknown.LineNumbers);
        Assert.Equal(["Client A"], plan.Rows[0].Target!.Groups!.Add.Where(name => name == "Client A"));
    }

    [Fact]
    public void AnUnknownName_DefaultsToItsClosestMatch_ThenCreate_ThenLeaveOut()
    {
        var plan = Plan("domain,groups\na.com,Contoso Ltd;Brand New", permissions: ImportPermissions.All);
        var withoutCreate = Plan("domain,groups\na.com,Brand New", permissions: ImportPermissions.All with { CanAddGroups = false });

        Assert.Equal(new NameResolution(NameChoice.MapTo, "Contoso Limited"), plan.UnknownNames.Single(name => name.Name == "Contoso Ltd").Resolution);
        Assert.Equal(NameChoice.Create, plan.UnknownNames.Single(name => name.Name == "Brand New").Resolution.Choice);
        Assert.Equal(["Brand New"], plan.GroupsToCreate);
        Assert.Equal(NameChoice.LeaveOut, withoutCreate.UnknownNames.Single().Resolution.Choice);
        Assert.False(withoutCreate.UnknownNames.Single().CanCreate);
    }

    [Fact]
    public void ANearMissTypo_IsSuggested_ButNotChosenForYou()
    {
        // "Client C" is one letter from "Client A", but they are different clients: suggest, don't map.
        var plan = Plan("domain,groups\na.com,Client C");

        var unknown = plan.UnknownNames.Single();
        Assert.Contains("Client A", unknown.Suggestions);
        Assert.Equal(NameChoice.Create, unknown.Resolution.Choice);
    }

    [Fact]
    public void AResolution_OverridesTheDefault()
    {
        var resolutions = new Dictionary<NameKey, NameResolution>
        {
            [new NameKey(ImportNameKind.Group, "contoso ltd")] = new(NameChoice.MapTo, "Client B"),
            [new NameKey(ImportNameKind.Group, "brand new")] = new(NameChoice.LeaveOut),
        };

        var plan = Plan("domain,groups\na.com,Contoso Ltd;Brand New", resolutions: resolutions);

        Assert.Equal(["Client B"], plan.Rows.Single().Target!.Groups!.Add);
        Assert.Empty(plan.GroupsToCreate);
    }

    [Fact]
    public void MapToWithNothingPickedYet_StaysMapTo_LeavesTheNameOut_AndHoldsTheImport()
    {
        var resolutions = new Dictionary<NameKey, NameResolution> { [new NameKey(ImportNameKind.Group, "brand new")] = new(NameChoice.MapTo) };

        var plan = Plan("domain,groups\na.com,Brand New", resolutions: resolutions);

        Assert.Equal(new NameResolution(NameChoice.MapTo), plan.UnknownNames.Single().Resolution);
        Assert.Null(plan.Rows.Single().Target!.Groups!.Add.SingleOrDefault());
        Assert.Empty(plan.GroupsToCreate);
        Assert.True(plan.HasUnfinishedChoices);
        Assert.False(Plan("domain,groups\na.com,Brand New").HasUnfinishedChoices);
    }

    [Fact]
    public void HaloClients_CantBeCreated_AndTheColumnIsIgnoredWithoutHalo()
    {
        var clients = new[] { new HaloClient(7, "Contoso Limited"), new HaloClient(8, "Fabrikam") };

        var connected = Plan("domain,halo client\na.com,Contoso Ltd\nb.com,Nobody", Snapshot(haloClients: clients));
        var notConnected = Plan("domain,halo client\na.com,Fabrikam");

        Assert.Equal(7, connected.Rows[0].Target!.HaloClientId);
        var unknownClient = connected.UnknownNames.Single(name => name.Name == "Nobody");
        Assert.False(unknownClient.CanCreate);
        Assert.Equal(NameChoice.LeaveOut, unknownClient.Resolution.Choice);
        Assert.Contains("HaloPSA isn't connected.", notConnected.Notices);
        Assert.False(notConnected.Rows.Single().Target!.SetHaloClient);
    }

    [Fact]
    public void Columns_AreIgnoredWithoutTheirPermission()
    {
        var plan = Plan("domain,groups,mta-sts mode,mx hosts\na.com,Client A,testing,mail.a.com",
            permissions: new ImportPermissions(CanEditDomains: false, CanManageMtaSts: false, CanAddGroups: true, CanAddTags: true));

        var target = plan.Rows.Single().Target!;
        Assert.Null(target.Groups);
        Assert.Null(target.MtaSts);
        Assert.Equal(2, plan.Notices.Count);
        Assert.Empty(plan.UnknownNames);
    }

    [Fact]
    public void MtaSts_TurnsOnWithLookedUpMxHosts_AndNotWhenNoneAreFound()
    {
        var snapshot = Snapshot(lookedUpMx: new Dictionary<string, IReadOnlyList<string>> { ["found.com"] = ["mail.found.com"], ["empty.com"] = [] });

        var plan = Plan("domain,mta-sts mode\nfound.com,enforce\nempty.com,testing", snapshot);

        Assert.Equal(new MtaStsTarget(true, MtaStsMode.Enforce, ["mail.found.com"], 604_800), Row(plan, "found.com").Target!.MtaSts, new MtaStsTargetComparer());
        Assert.Null(Row(plan, "empty.com").Target!.MtaSts);
        Assert.Contains(Row(plan, "empty.com").Notes, note => note.Contains("wasn't turned on"));
    }

    [Fact]
    public void MtaSts_AlreadyOnWithNoMxHosts_GetsThemFromDns()
    {
        var snapshot = Snapshot([Existing("broken.com", mtaStsEnabled: true)],
            lookedUpMx: new Dictionary<string, IReadOnlyList<string>> { ["broken.com"] = ["mail.broken.com"] });

        var plan = Plan("domain,mta-sts mode\nbroken.com,testing", snapshot);

        Assert.Equal(["mail.broken.com"], Row(plan, "broken.com").Target!.MtaSts!.MxHosts);
    }

    [Fact]
    public void MtaSts_AlreadyOnWithNoMxHostsAndNoneInDns_IsLeftAlone_RatherThanSavedEmpty()
    {
        var snapshot = Snapshot([Existing("broken.com", mtaStsEnabled: true)],
            lookedUpMx: new Dictionary<string, IReadOnlyList<string>> { ["broken.com"] = [] });

        var plan = Plan("domain,mta-sts mode\nbroken.com,enforce", snapshot);

        Assert.Null(Row(plan, "broken.com").Target?.MtaSts);
        Assert.Contains(Row(plan, "broken.com").Notes, note => note.Contains("no MX hosts"));
    }

    [Fact]
    public void TurningMonitoringAndMtaStsOff_OnAnExistingDomain_AreRemovals()
    {
        var plan = Plan("domain,monitored,mta-sts mode\nold.com,no,off",
            Snapshot([Existing("old.com", monitored: true, mtaStsEnabled: true, mxHosts: ["mail.old.com"])]));

        Assert.Equal(["Monitoring turned off", "MTA-STS turned off"], plan.Rows.Single().Removals);
    }

    [Fact]
    public void RemovingANameTheDomainDoesntHave_IsIgnoredWithANote()
    {
        var plan = Plan("domain,tags\nold.com,-europe", Snapshot([Existing("old.com")]));

        var row = plan.Rows.Single();
        Assert.Contains(row.Notes, note => note.Contains("nothing to remove"));
        Assert.Empty(row.Removals);
    }

    /// <summary>Records holding lists compare the lists by reference, so compare MTA-STS targets by value.</summary>
    private sealed class MtaStsTargetComparer : IEqualityComparer<MtaStsTarget?>
    {
        public bool Equals(MtaStsTarget? first, MtaStsTarget? second) =>
            first is not null && second is not null
            && (first.Enabled, first.Mode, first.MaxAgeSeconds) == (second.Enabled, second.Mode, second.MaxAgeSeconds)
            && first.MxHosts.SequenceEqual(second.MxHosts);

        public int GetHashCode(MtaStsTarget? target) => 0;
    }
}
