using DotMarc.Audit;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class AuditChangesTests
{
    [Fact]
    public void Field_RecordsOnlyValuesThatDiffer()
    {
        var changes = new AuditChanges()
            .Field("Name", "Client A", "Client B")
            .Field("Halo client", (int?)37, (int?)37);

        Assert.Equal([new AuditFieldChange("Name", "Client A", "Client B")], changes.Items);
        Assert.True(changes.Any);
    }

    [Fact]
    public void Field_FormatsBooleansAsYesAndNo_AndNullAsNull()
    {
        var changes = new AuditChanges()
            .Field("Monitored", true, false)
            .Field("Halo client", (int?)null, (int?)12);

        Assert.Equal(
            [new AuditFieldChange("Monitored", "Yes", "No"), new AuditFieldChange("Halo client", null, "12")],
            changes.Items);
    }

    [Fact]
    public void Set_IgnoresOrderAndRecordsTheValuesSorted()
    {
        var unchanged = new AuditChanges().Set("Groups", ["Beta", "Alpha"], ["Alpha", "Beta"]);
        var changed = new AuditChanges().Set("Groups", ["Beta"], ["gamma", "Beta", "alpha"]);

        Assert.False(unchanged.Any);
        Assert.Equal([new AuditFieldChange("Groups", "Beta", "alpha, Beta, gamma")], changed.Items);
    }

    [Fact]
    public void Set_RecordsNoneForAnEmptySet()
    {
        var changes = new AuditChanges().Set("Groups", [], ["Client A"]);

        Assert.Equal([new AuditFieldChange("Groups", "None", "Client A")], changes.Items);
    }

    [Fact]
    public void Secret_RecordsThatItChangedWithoutAnyValue()
    {
        var changes = new AuditChanges()
            .Secret("Client secret", changed: true)
            .Secret("Webhook secret", changed: false);

        Assert.Equal([new AuditFieldChange("Client secret", null, null, Secret: true)], changes.Items);
    }
}
