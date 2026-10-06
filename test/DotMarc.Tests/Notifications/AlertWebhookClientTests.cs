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

    private static NotificationSettings Settings(bool teams, bool slack, bool generic) => new()
    {
        Enabled = true,
        TeamsEnabled = teams, TeamsWebhookUrl = TeamsUrl,
        SlackEnabled = slack, SlackWebhookUrl = SlackUrl,
        GenericWebhookEnabled = generic, GenericWebhookUrl = GenericUrl,
    };

    private Task SendAsync(AlertWebhookClient client, NotificationSettings settings) =>
        client.SendAlertAsync(settings, "contoso.io", "MissedReport", "Missing report", "contoso.io has not sent a report.", CancellationToken.None);

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task SendAlertAsync_SendsToEachChannelThatsSwitchedOn(bool teams, bool slack, bool generic)
    {
        await SendAsync(CreateClient(), Settings(teams, slack, generic));

        Assert.Equal(teams ? [TeamsUrl] : [], _teams.SentTo);
        Assert.Equal(slack ? [SlackUrl] : [], _slack.SentTo);
        Assert.Equal(generic ? [GenericUrl] : [], _generic.SentTo);
    }

    [Fact]
    public async Task SendAlertAsync_SendsNothing_WhenNotificationsAreOff()
    {
        var settings = Settings(true, true, true);
        settings.Enabled = false;

        await SendAsync(CreateClient(), settings);

        Assert.Empty(_teams.SentTo.Concat(_slack.SentTo).Concat(_generic.SentTo));
    }

    [Fact]
    public async Task SendAlertAsync_SkipsAChannelThatsOnButHasNoUrl()
    {
        var settings = Settings(true, true, false);
        settings.SlackWebhookUrl = " ";

        await SendAsync(CreateClient(), settings);

        Assert.Single(_teams.SentTo);
        Assert.Empty(_slack.SentTo);
    }

    [Fact]
    public async Task SendAlertAsync_StillSendsToTheOtherChannels_WhenOneFails()
    {
        _teams.Failure = new HttpRequestException("Teams is down.");

        await SendAsync(CreateClient(), Settings(true, true, true));

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

        public Task SendAlertAsync(string webhookUrl, string domainName, string alertType, string title, string message, CancellationToken cancellationToken = default)
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
