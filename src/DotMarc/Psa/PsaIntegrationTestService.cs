using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Psa;

public enum PsaTestOutcome { Running, Passed, Warning, Failed }

public sealed record PsaTestStep(string Name, PsaTestOutcome Outcome, string Detail);

public sealed record PsaTestRun(PsaKind Psa, IReadOnlyList<PsaTestStep> Steps, bool NeedsCompanyChoice)
{
    /// <summary>True only when every step ran and none failed. For HaloPSA the webhook step must have passed: a timeout
    /// is only a warning there and must never read as success. A step that never ran (because an earlier one failed)
    /// isn't in the list, so this can't pass by omission either.</summary>
    public bool Succeeded =>
        Steps.Count == PsaIntegrationTestService.StepNames(Psa).Count
        && Steps.All(step => step.Outcome is PsaTestOutcome.Passed or PsaTestOutcome.Warning)
        && (Psa != PsaKind.HaloPsa || Steps.Single(step => step.Name == PsaIntegrationTestService.WebhookStep).Outcome == PsaTestOutcome.Passed);
}

/// <summary>Runs one PSA's integration through its whole lifecycle against the live PSA, so "is it actually working?"
/// has a one-click answer: create a clearly marked test ticket from the most recent alert's details, read it back as
/// open, close it through the same code real alerts use, and read it back as closed, which is what the poller relies on.
/// For HaloPSA it then waits for Halo's outbound webhook to call back.
///
/// It deliberately does not touch real alerts or link the ticket to one, so nothing on the alerts page changes.</summary>
public sealed class PsaIntegrationTestService(
    IDbContextFactory<DotMarcDbContext> dbFactory,
    IEnumerable<IPsaProvider> providers,
    HaloWebhookActivity webhooks,
    ILogger<PsaIntegrationTestService> logger)
{
    public const string SettingsStep = "Check settings";
    public const string AlertStep = "Pick an alert and company";
    public const string CreateStep = "Create a ticket";
    public const string OpenStep = "Read the new ticket";
    public const string CloseStep = "Close the ticket";
    public const string ClosedStep = "Read the closed ticket";
    public const string WebhookStep = "Wait for Halo's webhook";

    private static readonly TimeSpan DefaultWebhookTimeout = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<string> StepNames(PsaKind psa) => psa == PsaKind.HaloPsa
        ? [SettingsStep, AlertStep, CreateStep, OpenStep, CloseStep, ClosedStep, WebhookStep]
        : [SettingsStep, AlertStep, CreateStep, OpenStep, CloseStep, ClosedStep];

    public async Task<PsaTestRun> RunAsync(PsaKind psa, string? chosenCompanyId, IProgress<PsaTestStep>? progress, TimeSpan? webhookTimeout, CancellationToken cancellationToken)
    {
        var steps = new List<PsaTestStep>();
        var name = psa.DisplayName();
        var companyLabel = psa.CompanyLabel();

        void Report(string stepName, PsaTestOutcome outcome, string detail)
        {
            var step = new PsaTestStep(stepName, outcome, detail);
            var index = steps.FindIndex(existing => existing.Name == stepName);
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

        PsaTestRun Finish(bool needsCompanyChoice = false) => new(psa, steps.ToList(), needsCompanyChoice);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // 1. Settings. The test uses the saved settings, exactly what the real pipeline reads.
        Report(SettingsStep, PsaTestOutcome.Running, "Reading the saved settings...");
        var provider = providers.FirstOrDefault(candidate => candidate.Kind == psa);
        if (provider is null)
        {
            Report(SettingsStep, PsaTestOutcome.Failed, $"{name} isn't available on this server.");
            return Finish();
        }

        var readiness = await provider.GetReadinessAsync(db, cancellationToken).ConfigureAwait(false);
        var missing = readiness.Missing.ToList();
        HaloPsaSettings? haloSettings = null;
        if (psa == PsaKind.HaloPsa)
        {
            haloSettings = await HaloPsaSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(haloSettings.WebhookSecret))
            {
                missing.Add("webhook secret");
            }
        }

        if (missing.Count > 0)
        {
            Report(SettingsStep, PsaTestOutcome.Failed, $"Not saved yet: {string.Join(", ", missing)}. Save {name} settings first, since this test uses the saved settings.");
            return Finish();
        }

        Report(
            SettingsStep,
            readiness.Enabled ? PsaTestOutcome.Passed : PsaTestOutcome.Warning,
            readiness.Enabled
                ? "All the required settings are saved."
                : $"All the required settings are saved, but {name} tickets are switched off, so real alerts won't create tickets until you turn them on.");

        // 2. An alert to base the ticket on, and the company it would really go to.
        Report(AlertStep, PsaTestOutcome.Running, "Looking at the most recent alert...");
        var alert = await db.AlertEvents.AsNoTracking().OrderByDescending(candidate => candidate.CreatedUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        string? mappedCompanyId = null;
        if (alert is not null)
        {
            var domain = await PsaCompanyResolver.IncludeLinks(db.Domains).AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Name == alert.DomainName, cancellationToken).ConfigureAwait(false);
            mappedCompanyId = domain is null ? null : PsaCompanyResolver.Resolve(domain, psa)?.CompanyId;
        }

        var companyId = chosenCompanyId ?? mappedCompanyId;
        if (companyId is null)
        {
            Report(
                AlertStep,
                PsaTestOutcome.Failed,
                alert is null
                    ? $"There are no alerts yet, so there's no domain to look up a {companyLabel} for. Choose a {companyLabel} to test with."
                    : $"{alert.DomainName} isn't linked to a {companyLabel} (set one on its Group under Manage groups), so real alerts for it wouldn't create a ticket. Choose a {companyLabel} to test with anyway.");
            return Finish(needsCompanyChoice: true);
        }

        var domainName = alert?.DomainName ?? "dotmarc-test.example";
        var alertType = alert?.AlertType ?? "IntegrationTest";
        var title = alert?.Title ?? "Integration test alert";
        var message = alert?.Message ?? "No real alert exists yet, so this is a sample.";

        if (alert is null)
        {
            Report(AlertStep, PsaTestOutcome.Warning, $"No alerts yet, so a sample alert is used, against the {companyLabel} you chose (#{companyId}).");
        }
        else if (mappedCompanyId is null)
        {
            Report(AlertStep, PsaTestOutcome.Warning, $"Latest alert: {alertType} for {domainName}. That domain has no {companyLabel} mapped, so real alerts for it create no ticket. Using the {companyLabel} you chose (#{companyId}) for this test.");
        }
        else if (chosenCompanyId is not null && chosenCompanyId != mappedCompanyId)
        {
            Report(AlertStep, PsaTestOutcome.Passed, $"Latest alert: {alertType} for {domainName}. Using the {companyLabel} you chose (#{companyId}) instead of the one it's mapped to (#{mappedCompanyId}).");
        }
        else
        {
            Report(AlertStep, PsaTestOutcome.Passed, $"Latest alert: {alertType} for {domainName}. Its {companyLabel} is #{companyId}.");
        }

        // 3. Create.
        Report(CreateStep, PsaTestOutcome.Running, "Creating the test ticket...");
        string ticketId;
        try
        {
            ticketId = await provider.CreateTicketAsync(
                db,
                new PsaTicketRequest(companyId, domainName, alertType, $"[dotMARC test] {title}",
                    $"{message}\n\nThis ticket was created by dotMARC's integration test and is closed automatically straight afterwards."),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "{Psa} integration test: creating the ticket failed", name);
            Report(CreateStep, PsaTestOutcome.Failed, $"{name} wouldn't create the ticket: {exception.Message} Check the ticket settings and the {companyLabel} are valid in {name}.");
            return Finish();
        }

        Report(CreateStep, PsaTestOutcome.Passed, $"Ticket #{ticketId} created, titled \"[dotMARC test] {title}\".");

        // 4. Read it back: a new ticket must read as open, or real closes could never be told apart from it.
        Report(OpenStep, PsaTestOutcome.Running, $"Reading ticket #{ticketId}...");
        var stateAfterCreate = await ReadStateAsync(provider, db, ticketId, cancellationToken).ConfigureAwait(false);
        if (stateAfterCreate is not PsaTicketState.Open)
        {
            var detail = stateAfterCreate is null
                ? $"Ticket #{ticketId} was created, but reading it back failed, so dotMARC couldn't tell when it's closed. Check the API user can read tickets, then close #{ticketId} by hand in {name}."
                : $"Ticket #{ticketId} was created but reads as {stateAfterCreate.Value.ToString().ToLowerInvariant()}. Check the {name} closed status setting isn't the status new tickets start in, then close #{ticketId} by hand in {name}.";
            Report(OpenStep, PsaTestOutcome.Failed, detail);
            return Finish();
        }

        Report(OpenStep, PsaTestOutcome.Passed, $"Ticket #{ticketId} reads as open.");

        // 5. Close, using the closed status the app has configured.
        Report(CloseStep, PsaTestOutcome.Running, $"Closing ticket #{ticketId}...");
        var closedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            await provider.CloseTicketAsync(db, ticketId, "Closed by dotMARC's integration test.", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "{Psa} integration test: closing ticket {TicketId} failed", name, ticketId);
            // Halo won't close a ticket nobody is assigned to, and says so in its own words.
            var advice = psa == PsaKind.HaloPsa && exception.Message.Contains("assign", StringComparison.OrdinalIgnoreCase)
                ? "Halo needs the ticket assigned to someone before it can be closed: choose an agent under \"Assign new tickets to\", or set up Halo to assign new tickets itself. Then close this ticket by hand in Halo."
                : $"Check the closed status, and close the ticket by hand in {name}.";
            Report(CloseStep, PsaTestOutcome.Failed, $"Ticket #{ticketId} was created but couldn't be closed: {exception.Message} {advice}");
            return Finish();
        }

        Report(CloseStep, PsaTestOutcome.Passed, $"Ticket #{ticketId} closed using your closed status.");

        // 6. Read it back again: this is how dotMARC sees a tech's close and resolves the alert.
        Report(ClosedStep, PsaTestOutcome.Running, $"Reading ticket #{ticketId} again...");
        switch (await ReadStateAsync(provider, db, ticketId, cancellationToken).ConfigureAwait(false))
        {
            case PsaTicketState.Closed:
                Report(ClosedStep, PsaTestOutcome.Passed, $"dotMARC sees ticket #{ticketId} as closed, so a tech closing a real alert's ticket resolves the alert within the poll interval.");
                break;
            case PsaTicketState.Open:
                Report(ClosedStep, PsaTestOutcome.Failed, $"Ticket #{ticketId} still reads as open after closing. Check the closed status matches the one {name} uses for closed tickets.");
                return Finish();
            case PsaTicketState.Missing:
                Report(ClosedStep, PsaTestOutcome.Warning, $"Ticket #{ticketId} can't be found any more after closing, so dotMARC stops checking it. That's fine unless {name} deletes closed tickets.");
                break;
            default:
                Report(ClosedStep, PsaTestOutcome.Failed, $"Ticket #{ticketId} was closed, but reading it back failed. Check the API user can read tickets.");
                return Finish();
        }

        if (psa != PsaKind.HaloPsa)
        {
            return Finish();
        }

        // 7. HaloPSA only: the return leg, where Halo calls dotMARC's webhook.
        if (!int.TryParse(ticketId, out var ticketNumber))
        {
            Report(WebhookStep, PsaTestOutcome.Warning, $"Halo returned a ticket id ({ticketId}) that isn't a number, so its webhook call can't be matched to this ticket.");
            return Finish();
        }

        var timeout = webhookTimeout ?? DefaultWebhookTimeout;
        Report(WebhookStep, PsaTestOutcome.Running, $"Waiting up to {timeout.TotalSeconds:0} seconds for Halo to call the webhook...");
        var receipt = await webhooks.WaitForAsync(
            candidate => candidate.TicketId == ticketNumber || candidate.Delivery is HaloWebhookDelivery.Unreadable or HaloWebhookDelivery.WrongSecret,
            closedAtUtc,
            timeout,
            cancellationToken).ConfigureAwait(false);

        var waited = receipt is null ? timeout : receipt.ReceivedUtc - closedAtUtc;
        switch (receipt?.Delivery)
        {
            case HaloWebhookDelivery.ClosedStatus:
                Report(WebhookStep, PsaTestOutcome.Passed, $"Halo called the webhook {waited.TotalSeconds:0.#} seconds after the close, reporting ticket #{ticketNumber} with status {receipt.StatusId}, which matches your closed status. For a real alert, this is what resolves it in dotMARC straight away.");
                break;
            case HaloWebhookDelivery.OtherStatus:
                Report(WebhookStep, PsaTestOutcome.Failed, $"Halo called the webhook, but reported status {receipt.StatusId} for ticket #{ticketNumber}, and your closed status is {haloSettings!.ClosedStatusId}. Real closes wouldn't resolve alerts. Check the Closed status setting matches the status Halo sends.");
                break;
            case HaloWebhookDelivery.Unreadable:
                Report(WebhookStep, PsaTestOutcome.Failed, $"Halo called the webhook, but dotMARC couldn't find the ticket in the body. {receipt.Detail} Change the webhook's payload in Halo to one that includes the ticket's id and status.");
                break;
            case HaloWebhookDelivery.StatusUnknown:
                Report(WebhookStep, PsaTestOutcome.Failed, $"Halo called the webhook for ticket #{ticketNumber} without saying its status, and asking Halo for the status failed: {receipt.Detail} Either send a payload that includes the status, or let the API agent read tickets.");
                break;
            case HaloWebhookDelivery.WrongSecret:
                Report(WebhookStep, PsaTestOutcome.Failed, "A request reached the webhook with the wrong secret. Save HaloPSA settings, then copy the webhook URL into Halo again.");
                break;
            default:
                Report(WebhookStep, PsaTestOutcome.Warning, $"No webhook call arrived within {timeout.TotalSeconds:0} seconds. Check that Halo's outbound webhook points at the URL above and triggers on ticket status change. Ticket #{ticketNumber} was closed either way, and dotMARC's regular check still sees closes, only more slowly.");
                break;
        }

        return Finish();
    }

    /// <summary>The ticket's state, or null when reading it failed.</summary>
    private async Task<PsaTicketState?> ReadStateAsync(IPsaProvider provider, DotMarcDbContext db, string ticketId, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetTicketStateAsync(db, ticketId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "{Psa} integration test: reading ticket {TicketId} failed", provider.Kind.DisplayName(), ticketId);
            return null;
        }
    }
}
