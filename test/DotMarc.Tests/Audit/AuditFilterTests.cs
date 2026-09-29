using DotMarc.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace DotMarc.Tests.Audit;

public sealed class AuditFilterTests
{
    [Fact]
    public void AFilter_SurvivesTheExportLinksQueryString()
    {
        var filter = new AuditFilter
        {
            From = new DateOnly(2026, 9, 1),
            To = new DateOnly(2026, 9, 29),
            Kind = AuditEntryKind.Change,
            IncludePageViews = true,
            Who = "sam & co",
            Action = AuditActions.GroupRenamed,
            Target = "contoso.com",
            Summary = "100%"
        };

        var roundTripped = AuditFilter.FromQuery(new QueryCollection(QueryHelpers.ParseQuery(filter.ToQueryString())));

        Assert.Equal(filter, roundTripped);
    }

    [Fact]
    public void AnEmptyQuery_IsAnEmptyFilter()
    {
        Assert.Equal(new AuditFilter(), AuditFilter.FromQuery(new QueryCollection()));
        Assert.Equal("", new AuditFilter().ToQueryString());
    }

    [Fact]
    public void Describe_SummarisesTheFilterForTheExportEntry()
    {
        var filter = new AuditFilter { From = new DateOnly(2026, 9, 1), Who = "sam" };

        Assert.Equal("from 2026-09-01, who contains \"sam\"", filter.Describe());
        Assert.Equal("everything", new AuditFilter().Describe());
    }
}
