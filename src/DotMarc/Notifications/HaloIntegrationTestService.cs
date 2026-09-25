using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Notifications;

public enum HaloTestOutcome { Running, Passed, Warning, Failed }

public sealed record HaloTestStep(string Name, HaloTestOutcome Outcome, string Detail);

public sealed record HaloTestRun(IReadOnlyList<HaloTestStep> Steps, bool NeedsClientChoice)
{
    /// <summary>True only when the whole lifecycle ran and Halo's webhook came back. Earlier steps may
    /// carry a warning (sync switched off, a client chosen by hand) and still count, but the webhook
    /// step must have passed: a timeout is only a warning, and must never read as success. A step
    /// that never ran (because an earlier one failed) isn't in the list, so this can't pass by
    /// omission either.</summary>
    public bool Succeeded =>
        Steps.Count == HaloIntegrationTestService.StepNames.Length
        && Steps.All(step => step.Outcome is HaloTestOutcome.Passed or HaloTestOutcome.Warning)
        && Steps.Single(step => step.Name == HaloIntegrationTestService.WebhookStep).Outcome == HaloTestOutcome.Passed;
}

/// <summary>Runs the HaloPSA integration through its whole lifecycle against the live Halo tenant, so
/// "is it actually working?" has a one-click answer: create a clearly marked test ticket from the
/// most recent alert's details, close it through the same client code real alerts use, then wait for
/// Halo's outbound webhook to call back.
///
/// It deliberately does not touch real alerts or link the ticket to one, so nothing on the alerts
/// page changes. What it proves is everything on Halo's side of the integration: sign-in, the ticket
/// type, priority and client, the closed status, and that the webhook reaches dotMARC with a payload
/// dotMARC understands. Resolving an alert from a webhook is covered by automated tests.</summary>
public sealed class HaloIntegrationTestService(
    IDbContextFactory<DotMarcDbContext> dbFactory,
    IHaloPsaClient haloClient,
    HaloWebhookActivity webhooks,
    ILogger<HaloIntegrationTestService> logger)
{
    public const string SettingsStep = "Check settings";
    public const string AlertStep = "Pick an alert and Halo client";
    public const string CreateStep = "Create a ticket";
    public const string CloseStep = "Close the ticket";
    public const string WebhookStep = "Wait for Halo's webhook";

    public static readonly string[] StepNames = [SettingsStep, AlertStep, CreateStep, CloseStep, WebhookStep];

    private static readonly TimeSpan DefaultWebhookTimeout = TimeSpan.FromSeconds(30);

    public async Task<HaloTestRun> RunAsync(int? chosenHaloClientId, IProgress<HaloTestStep>? progress, TimeSpan? webhookTimeout, CancellationToken cancellationToken)
    {
        var steps = new List<HaloTestStep>();

        void Report(string name, HaloTestOutcome outcome, string detail)
        {
            var step = new HaloTestStep(name, outcome, detail);
            var index = steps.FindIndex(existing => existing.Name == name);
            if (index >= 0)
            {
                steps[index] = step;
            }
            else
            {
                steps.Add(step);
            }

            progress?.Report(step);
        }

        HaloTestRun Finish(bool needsClientChoice = false) => new(steps.ToList(), needsClientChoice);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // 1. Settings. The test uses the saved settings, exactly what the real pipeline reads.
        Report(SettingsStep, HaloTestOutcome.Running, "Reading the saved settings...");
        var settings = await HaloPsaSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        var missing = MissingSettings(settings);
        if (missing.Count > 0)
        {
            Report(SettingsStep, HaloTestOutcome.Failed, $"Not saved yet: {string.Join(", ", missing)}. Save PSA settings first, since this test uses the saved settings.");
            return Finish();
        }

        Report(
            SettingsStep,
            settings.Enabled ? HaloTestOutcome.Passed : HaloTestOutcome.Warning,
            settings.Enabled
                ? "All the required settings are saved."
                : "All the required settings are saved, but HaloPSA ticket sync is switched off, so real alerts won't create tickets until you enable it.");

        // 2. An alert to base the ticket on, and the Halo client it would really go to.
        Report(AlertStep, HaloTestOutcome.Running, "Looking at the most recent alert...");
        var alert = await db.AlertEvents.AsNoTracking().OrderByDescending(a => a.CreatedUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        int? mappedClientId = null;
        if (alert is not null)
        {
            var domain = await db.Domains.Include(d => d.Groups).AsNoTracking().SingleOrDefaultAsync(d => d.Name == alert.DomainName, cancellationToken).ConfigureAwait(false);
            mappedClientId = domain is null ? null : HaloClientResolver.Resolve(domain);
        }

        var clientId = chosenHaloClientId ?? mappedClientId;
        if (clientId is null)
        {
            Report(
                AlertStep,
                HaloTestOutcome.Failed,
                alert is null
                    ? "There are no alerts yet, so there's no domain to look up a Halo client for. Choose a Halo client to test with."
                    : $"{alert.DomainName} isn't mapped to a Halo client (set one on its Group under Manage groups), so real alerts for it wouldn't create a ticket. Choose a Halo client to test with anyway.");
            return Finish(needsClientChoice: true);
        }

        var domainName = alert?.DomainName ?? "dotmarc-test.example";
        var alertType = alert?.AlertType ?? "IntegrationTest";
        var title = alert?.Title ?? "Integration test alert";
        var message = alert?.Message ?? "No real alert exists yet, so this is a sample.";

        if (alert is null)
        {
            Report(AlertStep, HaloTestOutcome.Warning, $"No alerts yet, so a sample alert is used, against the Halo client you chose (#{clientId}).");
        }
        else if (mappedClientId is null)
        {
            Report(AlertStep, HaloTestOutcome.Warning, $"Latest alert: {alertType} for {domainName}. That domain has no Halo client mapped, so real alerts for it create no ticket. Using the client you chose (#{clientId}) for this test.");
        }
        else if (chosenHaloClientId is not null && chosenHaloClientId != mappedClientId)
        {
            Report(AlertStep, HaloTestOutcome.Passed, $"Latest alert: {alertType} for {domainName}. Using the client you chose (#{clientId}) instead of the one it's mapped to (#{mappedClientId}).");
        }
        else
        {
            Report(AlertStep, HaloTestOutcome.Passed, $"Latest alert: {alertType} for {domainName}. Its Halo client is #{clientId}.");
        }

        // 3. Create.
        Report(CreateStep, HaloTestOutcome.Running, "Creating the test ticket...");
        string ticketId;
        try
        {
            ticketId = await haloClient.CreateTicketAsync(
                settings,
                clientId.Value,
                domainName,
                alertType,
                $"[dotMARC test] {title}",
                $"{message}\n\nThis ticket was created by dotMARC's integration test and is closed automatically straight afterwards.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "HaloPSA integration test: creating the ticket failed");
            Report(CreateStep, HaloTestOutcome.Failed, $"Halo wouldn't create the ticket: {exception.Message} Check the ticket type, default priority and client are valid in Halo.");
            return Finish();
        }

        Report(CreateStep, HaloTestOutcome.Passed, $"Ticket #{ticketId} created, titled \"[dotMARC test] {title}\".");

        // 4. Close, using the closed status the app has configured.
        Report(CloseStep, HaloTestOutcome.Running, $"Closing ticket #{ticketId}...");
        var closedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            await haloClient.CloseTicketAsync(settings, ticketId, "Closed by dotMARC's integration test.", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "HaloPSA integration test: closing ticket {TicketId} failed", ticketId);
            // Halo won't close a ticket nobody is assigned to, and says so in its own words.
            var advice = exception.Message.Contains("assign", StringComparison.OrdinalIgnoreCase)
                ? "Halo needs the ticket assigned to someone before it can be closed: choose an agent under \"Assign new tickets to\", or set up Halo to assign new tickets itself. Then close this ticket by hand in Halo."
                : "Check the closed status, and close the ticket by hand in Halo.";
            Report(CloseStep, HaloTestOutcome.Failed, $"Ticket #{ticketId} was created but couldn't be closed: {exception.Message} {advice}");
            return Finish();
        }

        Report(CloseStep, HaloTestOutcome.Passed, $"Ticket #{ticketId} closed using your closed status.");

        // 5. The return leg: Halo should now call dotMARC's webhook.
        if (!int.TryParse(ticketId, out var ticketNumber))
        {
            Report(WebhookStep, HaloTestOutcome.Warning, $"Halo returned a ticket id ({ticketId}) that isn't a number, so its webhook call can't be matched to this ticket.");
            return Finish();
        }

        var timeout = webhookTimeout ?? DefaultWebhookTimeout;
        Report(WebhookStep, HaloTestOutcome.Running, $"Waiting up to {timeout.TotalSeconds:0} seconds for Halo to call the webhook...");
        var receipt = await webhooks.WaitForAsync(
            candidate => candidate.TicketId == ticketNumber || candidate.Delivery is HaloWebhookDelivery.Unreadable or HaloWebhookDelivery.WrongSecret,
            closedAtUtc,
            timeout,
            cancellationToken).ConfigureAwait(false);

        var waited = receipt is null ? timeout : receipt.ReceivedUtc - closedAtUtc;
        switch (receipt?.Delivery)
        {
            case HaloWebhookDelivery.ClosedStatus:
                Report(WebhookStep, HaloTestOutcome.Passed, $"Halo called the webhook {waited.TotalSeconds:0.#} seconds after the close, reporting ticket #{ticketNumber} with status {receipt.StatusId}, which matches your closed status. For a real alert, this is what resolves it in dotMARC.");
                break;
            case HaloWebhookDelivery.OtherStatus:
                Report(WebhookStep, HaloTestOutcome.Failed, $"Halo called the webhook, but reported status {receipt.StatusId} for ticket #{ticketNumber}, and your closed status is {settings.ClosedStatusId}. Real closes wouldn't resolve alerts. Check the Closed status setting matches the status Halo sends.");
                break;
            case HaloWebhookDelivery.Unreadable:
                Report(WebhookStep, HaloTestOutcome.Failed, $"Halo called the webhook, but dotMARC couldn't find the ticket in the body. {receipt.Detail} Change the webhook's payload in Halo to one that includes the ticket's id and status.");
                break;
            case HaloWebhookDelivery.StatusUnknown:
                Report(WebhookStep, HaloTestOutcome.Failed, $"Halo called the webhook for ticket #{ticketNumber} without saying its status, and asking Halo for the status failed: {receipt.Detail} Either send a payload that includes the status, or let the API agent read tickets.");
                break;
            case HaloWebhookDelivery.WrongSecret:
                Report(WebhookStep, HaloTestOutcome.Failed, "A request reached the webhook with the wrong secret. Save PSA settings, then copy the webhook URL into Halo again.");
                break;
            default:
                Report(WebhookStep, HaloTestOutcome.Warning, $"No webhook call arrived within {timeout.TotalSeconds:0} seconds. Check that Halo's outbound webhook points at the URL above and triggers on ticket status change. Ticket #{ticketNumber} was closed either way.");
                break;
        }

        return Finish();
    }

    private static List<string> MissingSettings(HaloPsaSettings settings)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.AuthServerUrl)) missing.Add("auth server URL");
        if (string.IsNullOrWhiteSpace(settings.ResourceServerUrl)) missing.Add("resource server URL");
        if (string.IsNullOrWhiteSpace(settings.ClientId)) missing.Add("client ID");
        if (!settings.ClientSecretConfigured) missing.Add("client secret");
        if (settings.TicketTypeId is null) missing.Add("ticket type");
        if (settings.DefaultPriorityId is null) missing.Add("default priority");
        if (settings.ClosedStatusId is null) missing.Add("closed status");
        if (string.IsNullOrWhiteSpace(settings.WebhookSecret)) missing.Add("webhook secret");
        return missing;
    }
}
