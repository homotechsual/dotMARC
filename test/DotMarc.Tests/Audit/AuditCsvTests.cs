using DotMarc.Audit;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class AuditCsvTests
{
    private static async Task<string[]> WriteAsync(params AuditEntry[] entries)
    {
        await using var writer = new StringWriter();
        await AuditCsv.WriteAsync(writer, entries.ToAsyncEnumerable(), CancellationToken.None);
        return writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static AuditEntry Entry(string summary, params AuditFieldChange[] changes) => new()
    {
        OccurredUtc = new DateTimeOffset(2026, 9, 29, 10, 30, 0, TimeSpan.Zero),
        Kind = AuditEntryKind.Change, ActorName = "Sam Jones", ActorEmail = "sam@contoso.com",
        Action = AuditActions.GroupRenamed, TargetType = "Group", TargetName = "Client B", Summary = summary, Changes = [.. changes]
    };

    [Fact]
    public async Task TheFirstLine_IsTheHeader()
    {
        var lines = await WriteAsync();

        Assert.Equal("Time (UTC),Kind,Who,Email,Action,Target type,Target,Summary,Changes", lines.Single());
    }

    [Fact]
    public async Task ARow_HasEveryColumn_WithChangesFlattened()
    {
        var lines = await WriteAsync(Entry("Renamed group", new AuditFieldChange("Name", "Client A", "Client B"), new AuditFieldChange("Client secret", null, null, true)));

        Assert.Equal("2026-09-29 10:30:00,Change,Sam Jones,sam@contoso.com,group.renamed,Group,Client B,Renamed group,Name: Client A -> Client B; Client secret: changed (value not recorded)", lines[1]);
    }

    [Fact]
    public async Task CommasQuotesAndLineBreaks_StayInOneCell()
    {
        await using var writer = new StringWriter();
        await AuditCsv.WriteAsync(writer, new[] { Entry("Said \"hi\", then\nleft") }.ToAsyncEnumerable(), CancellationToken.None);

        Assert.Contains("\"Said \"\"hi\"\", then\nleft\"", writer.ToString());
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")")]
    [InlineData("+1+1")]
    [InlineData("-2")]
    [InlineData("@SUM(A1)")]
    public async Task ACellStartingLikeAFormula_IsEscaped(string summary)
    {
        var lines = await WriteAsync(Entry(summary));

        // The apostrophe goes first in the cell. A formula containing quotes is also wrapped in quotes, so the
        // cell may start with a quote before the apostrophe.
        Assert.Contains("'" + summary.Replace("\"", "\"\""), lines[1]);
    }
}
