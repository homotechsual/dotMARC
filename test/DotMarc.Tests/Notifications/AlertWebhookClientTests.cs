using DotMarc.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertWebhookClientTests
{
    private const string TeamsUrl = "https://example.test/teams";
    private const string SlackUrl = "https://hooks.slack.com/services/T000/B000/secret";
    private const string GenericUrl = "https://example.test/generic";

    private readonly FakeChannelClient _teams = new();
    private readonly FakeChannelClient _slack = new();
    private readonly FakeChannelClient _generic = new();

    private AlertWebhookClient CreateClient() => new(_teams, _slack, _generic, NullLogger<AlertWebhookClient>.Instance);

    private static AlertDestination Teams() => new() { Name = "Teams", Kind = AlertChannel.Teams, Url = TeamsUrl };
    private static AlertDestination Slack() => new() { Name = "Slack", Kind = AlertChannel.Slack, Url = SlackUrl };
    private static AlertDestination Generic() => new() { Name = "Generic", Kind = AlertChannel.GenericWebhook, Url = GenericUrl };

    private Task SendAsync(AlertWebhookClient client, params AlertDestination[] destinations) =>
        client.SendAlertAsync(destinations, "contoso.io", "MissedReport", "Warning", "Missing report", "contoso.io has not sent a report.", CancellationToken.None);

    [Fact]
    public async Task SendAlertAsync_SendsToEachGivenDestination_ByItsKind()
    {
        await SendAsync(CreateClient(), Teams(), Generic());

        Assert.Equal([TeamsUrl], _teams.SentTo);
        Assert.Empty(_slack.SentTo);
        Assert.Equal([GenericUrl], _generic.SentTo);
    }

    [Fact]
    public async Task SendAlertAsync_SendsNothing_WhenGivenNoDestinations()
    {
        await SendAsync(CreateClient());

        Assert.Empty(_teams.SentTo.Concat(_slack.SentTo).Concat(_generic.SentTo));
    }

    [Fact]
    public async Task SendAlertAsync_SendsToTwoDestinationsOfTheSameKind()
    {
        var second = Teams();
        second.Url = "https://example.test/teams-two";

        await SendAsync(CreateClient(), Teams(), second);

        Assert.Equal([TeamsUrl, "https://example.test/teams-two"], _teams.SentTo);
    }

    [Fact]
    public async Task SendAlertAsync_StillSendsToTheOtherDestinations_WhenOneFails()
    {
        _teams.Failure = new HttpRequestException("Teams is down.");

        await SendAsync(CreateClient(), Teams(), Slack(), Generic());

        Assert.Single(_slack.SentTo);
        Assert.Single(_generic.SentTo);
    }

    [Fact]
    public async Task SendTestAsync_SendsAMarkedTestAlert_AndReportsSuccess()
    {
        var problem = await CreateClient().SendTestAsync(AlertChannel.Slack, SlackUrl, CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal([SlackUrl], _slack.SentTo);
        Assert.Contains("test", _slack.LastTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendTestAsync_ReportsWhatWentWrong()
    {
        _teams.Failure = new HttpRequestException("Response status code does not indicate success: 404 (Not Found).");

        var problem = await CreateClient().SendTestAsync(AlertChannel.Teams, TeamsUrl, CancellationToken.None);

        Assert.Contains("404", problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.test/insecure")]
    [InlineData("https://user:password@example.test/hook")]
    public async Task SendTestAsync_RefusesAMissingOrUnsafeUrl_WithoutSending(string webhookUrl)
    {
        var problem = await CreateClient().SendTestAsync(AlertChannel.GenericWebhook, webhookUrl, CancellationToken.None);

        Assert.NotNull(problem);
        Assert.Empty(_generic.SentTo);
    }

    private sealed class FakeChannelClient : ITeamsWebhookClient, ISlackWebhookClient, IGenericWebhookClient
    {
        public List<string> SentTo { get; } = [];
        public string LastTitle { get; private set; } = "";
        public Exception? Failure { get; set; }

        public Task SendAlertAsync(string webhookUrl, string domainName, string alertType, string severity, string title, string message, CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            SentTo.Add(webhookUrl);
            LastTitle = title;
            return Task.CompletedTask;
        }
    }
}
