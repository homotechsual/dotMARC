using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public sealed record AlertDestinationInput(string Name, AlertChannel Kind, string Url, bool Enabled);

/// <summary>What the rules page and dialog show about a destination. It has no URL on purpose: the URL is a secret, and the only
/// screen that handles it is the destination dialog on the Settings tab of Alerts &amp; PSA.</summary>
public sealed record AlertDestinationSummary(int Id, string Name, AlertChannel Kind, bool Enabled);

/// <summary>A destination change was refused. The message is a sentence a person can act on and never contains the URL.</summary>
public sealed class AlertDestinationException(string message) : Exception(message);

/// <summary>The one way to create, change and delete alert destinations: validated and recorded in the audit log. Who may do it is
/// decided by the caller (the Manage alerts permission); this does not check. The URL carries the token that lets anyone post to the
/// channel, so the audit log records that it changed and never what it was.</summary>
public static class AlertDestinationService
{
    private const int MaxNameLength = 100;

    public static async Task<AlertDestination> CreateAsync(DotMarcDbContext context, AuditActor actor, AlertDestinationInput input, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(context, input, exceptId: null, cancellationToken).ConfigureAwait(false);
        var created = new AlertDestination { Name = input.Name.Trim(), Kind = input.Kind, Url = input.Url.Trim(), Enabled = input.Enabled, CreatedUtc = DateTimeOffset.UtcNow };
        context.AlertDestinations.Add(created);
        await AuditLog.SaveAndRecordAsync(
            context,
            () => AuditLog.Record(context, actor, AuditActions.AlertDestinationCreated, AuditTarget.For(created), $"Created the alert destination '{created.Name}'",
                new AuditChanges().Field("Kind", (string?)null, created.Kind.ToString()).Field("Enabled", (bool?)null, created.Enabled)),
            cancellationToken).ConfigureAwait(false);
        return created;
    }

    public static async Task UpdateAsync(DotMarcDbContext context, AuditActor actor, int id, AlertDestinationInput input, CancellationToken cancellationToken = default)
    {
        var destination = await LoadAsync(context, id, cancellationToken).ConfigureAwait(false);
        await ValidateAsync(context, input, id, cancellationToken).ConfigureAwait(false);

        var changes = new AuditChanges()
            .Field("Name", destination.Name, input.Name.Trim())
            .Field("Kind", destination.Kind.ToString(), input.Kind.ToString())
            .Field("Enabled", destination.Enabled, input.Enabled)
            .Secret("Webhook URL", destination.Url != input.Url.Trim());
        if (!changes.Any)
        {
            return;
        }

        destination.Name = input.Name.Trim();
        destination.Kind = input.Kind;
        destination.Url = input.Url.Trim();
        destination.Enabled = input.Enabled;
        AuditLog.Record(context, actor, AuditActions.AlertDestinationChanged, AuditTarget.For(destination), $"Changed the alert destination '{destination.Name}'", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every destination by name, without its URL.</summary>
    public static async Task<IReadOnlyList<AlertDestinationSummary>> ListSummariesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await context.AlertDestinations.AsNoTracking().OrderBy(destination => destination.Name)
            .Select(destination => new AlertDestinationSummary(destination.Id, destination.Name, destination.Kind, destination.Enabled))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>How many rules choose this destination. Only rules in Selected mode count: a rule in All mode sends to it without
    /// choosing it. Deleting a destination leaves those rules choosing nothing, which sends their alerts nowhere.</summary>
    public static Task<int> CountRulesAsync(DotMarcDbContext context, int id, CancellationToken cancellationToken = default) =>
        context.AlertRuleDestinations.CountAsync(link => link.AlertDestinationId == id
            && context.AlertRules.Any(rule => rule.Id == link.AlertRuleId && rule.DestinationMode == AlertDestinationMode.Selected), cancellationToken);

    public static async Task DeleteAsync(DotMarcDbContext context, AuditActor actor, int id, CancellationToken cancellationToken = default)
    {
        var destination = await LoadAsync(context, id, cancellationToken).ConfigureAwait(false);
        AuditLog.Record(context, actor, AuditActions.AlertDestinationDeleted, AuditTarget.For(destination), $"Deleted the alert destination '{destination.Name}'");
        context.AlertDestinations.Remove(destination);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AlertDestination> LoadAsync(DotMarcDbContext context, int id, CancellationToken cancellationToken) =>
        await context.AlertDestinations.SingleOrDefaultAsync(destination => destination.Id == id, cancellationToken).ConfigureAwait(false)
        ?? throw new AlertDestinationException("That destination no longer exists. Someone else may have deleted it.");

    private static async Task ValidateAsync(DotMarcDbContext context, AlertDestinationInput input, int? exceptId, CancellationToken cancellationToken)
    {
        var name = input.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new AlertDestinationException("Name is required.");
        }

        if (name.Length > MaxNameLength)
        {
            throw new AlertDestinationException($"Name must be {MaxNameLength} characters or fewer.");
        }

        if (string.IsNullOrWhiteSpace(input.Url))
        {
            throw new AlertDestinationException("The webhook URL is required.");
        }

        if (WebhookUrls.Problem(input.Url.Trim(), "The webhook URL") is { } problem)
        {
            throw new AlertDestinationException(problem);
        }

        var lowered = name.ToLower();
        if (await context.AlertDestinations.AnyAsync(destination => destination.Id != exceptId && destination.Name.ToLower() == lowered, cancellationToken).ConfigureAwait(false))
        {
            throw new AlertDestinationException("Another destination already has that name.");
        }
    }
}
