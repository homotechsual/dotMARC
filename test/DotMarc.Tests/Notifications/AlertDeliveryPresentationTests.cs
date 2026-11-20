using System.Reflection;
using DotMarc.Components.Dialogs;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

/// <summary>What the rules page and dialog know about destinations, and how a rule's delivery reads in its row.</summary>
public sealed class AlertDeliveryPresentationTests
{
    private static readonly Dictionary<int, string> Names = new() { [1] = "Shared Teams", [2] = "Aurora Slack" };

    private static AlertRule Rule(AlertDestinationMode mode, AlertTicketMode tickets, params int[] destinationIds) => new()
    {
        Kind = AlertTypes.MissedReport, Name = "Rule", Severity = "Warning", DestinationMode = mode, TicketMode = tickets,
        Destinations = [.. destinationIds.Select(id => new AlertRuleDestination { AlertDestinationId = id })]
    };

    [Fact]
    public void ADefaultDelivery_SaysNothing()
    {
        Assert.Null(AlertRulePresentation.DescribeDelivery(Rule(AlertDestinationMode.All, AlertTicketMode.FollowRules), Names));
    }

    [Fact]
    public void SelectedDestinations_AreNamed()
    {
        Assert.Equal("Sends to Shared Teams and Aurora Slack.", AlertRulePresentation.DescribeDelivery(Rule(AlertDestinationMode.Selected, AlertTicketMode.FollowRules, 1, 2), Names));
    }

    [Fact]
    public void SelectedWithNone_SaysItSendsNowhere()
    {
        Assert.Equal("Sends nowhere.", AlertRulePresentation.DescribeDelivery(Rule(AlertDestinationMode.Selected, AlertTicketMode.FollowRules), Names));
    }

    [Theory]
    [InlineData(AlertTicketMode.Always, "Always opens a ticket.")]
    [InlineData(AlertTicketMode.Never, "Never opens a ticket.")]
    public void ATicketModeOtherThanFollowRules_IsStated(AlertTicketMode mode, string expected)
    {
        Assert.Equal(expected, AlertRulePresentation.DescribeDelivery(Rule(AlertDestinationMode.All, mode), Names));
    }

    [Fact]
    public void DestinationAndTicketModeTogether_ReadAsTwoSentences()
    {
        Assert.Equal("Sends to Aurora Slack. Never opens a ticket.", AlertRulePresentation.DescribeDelivery(Rule(AlertDestinationMode.Selected, AlertTicketMode.Never, 2), Names));
    }

    [Fact]
    public void ADeletedDestination_IsNotNamed()
    {
        Assert.Equal("Sends to Shared Teams.", AlertRulePresentation.DescribeDelivery(Rule(AlertDestinationMode.Selected, AlertTicketMode.FollowRules, 1, 99), Names));
    }

    [Fact]
    public void WhatThePagesKnowAboutADestination_HasNoUrl()
    {
        var members = typeof(AlertDestinationSummary).GetProperties().Select(property => property.Name).Order().ToList();

        Assert.Equal(["Enabled", "Id", "Kind", "Name"], members);
    }

    [Fact]
    public void TheDestinationDialog_AsksForTheDestinationByParameter_AndIsNotARoutablePage()
    {
        var parameters = typeof(AlertDestinationDialog).GetProperties()
            .Where(property => property.GetCustomAttribute<ParameterAttribute>() is not null)
            .Select(property => property.Name)
            .ToList();

        Assert.Equal(["DestinationId"], parameters);
        Assert.Null(typeof(AlertDestinationDialog).GetCustomAttribute<RouteAttribute>());
    }
}

[Collection("Postgres")]
public sealed class AlertDestinationSummariesTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task ListSummaries_ReturnsEveryDestinationByName_WithoutItsUrl()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);
            await context.Database.MigrateAsync();
            context.AlertDestinations.AddRange(
                new AlertDestination { Name = "Shared Teams", Kind = AlertChannel.Teams, Url = "https://example.test/secret-token" },
                new AlertDestination { Name = "Aurora Slack", Kind = AlertChannel.Slack, Url = "https://hooks.slack.com/services/T0/B0/x", Enabled = false });
            await context.SaveChangesAsync();

            var summaries = await AlertDestinationService.ListSummariesAsync(context);

            Assert.Equal(["Aurora Slack", "Shared Teams"], summaries.Select(summary => summary.Name));
            Assert.Equal([false, true], summaries.Select(summary => summary.Enabled));
        }
    }
}
