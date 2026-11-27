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
    public void FromAddress_KeepsTheDefaultStartDate_WhenTheAddressSetsNoDates()
    {
        var defaultFrom = new DateOnly(2026, 10, 25);

        var filter = AuditFilter.FromAddress(new QueryCollection(QueryHelpers.ParseQuery("?who=sam")), defaultFrom);

        Assert.Equal(defaultFrom, filter.From);
        Assert.Null(filter.To);
        Assert.Equal("sam", filter.Who);
    }

    [Theory]
    [InlineData("?from=2026-09-01", "2026-09-01")]
    [InlineData("?to=2026-09-29", null)]
    public void FromAddress_UsesTheAddressDates_WhenTheyAreSet(string queryString, string? expectedFrom)
    {
        var filter = AuditFilter.FromAddress(new QueryCollection(QueryHelpers.ParseQuery(queryString)), new DateOnly(2026, 10, 25));

        Assert.Equal(expectedFrom is null ? null : DateOnly.Parse(expectedFrom), filter.From);
    }

    [Theory]
    [InlineData("?kind=Change", true)]
    [InlineData("?who=sam", true)]
    [InlineData("?action=group.renamed", true)]
    [InlineData("?target=contoso.com", true)]
    [InlineData("?summary=renamed", true)]
    [InlineData("?from=2026-09-01&pageViews=true", false)]
    [InlineData("?who=%20", false)]
    public void HasColumnFilters_IsTrueOnlyForTheFiltersInTheHeaderRow(string queryString, bool expected)
    {
        var filter = AuditFilter.FromQuery(new QueryCollection(QueryHelpers.ParseQuery(queryString)));

        Assert.Equal(expected, filter.HasColumnFilters);
    }

    [Fact]
    public void Describe_SummarisesTheFilterForTheExportEntry()
    {
        var filter = new AuditFilter { From = new DateOnly(2026, 9, 1), Who = "sam" };

        Assert.Equal("from 2026-09-01, who contains \"sam\"", filter.Describe());
        Assert.Equal("everything", new AuditFilter().Describe());
    }
}
