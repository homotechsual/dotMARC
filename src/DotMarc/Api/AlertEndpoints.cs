using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class AlertEndpoints
{
    private const int DefaultPageSize = 50;

    public static IReadOnlyList<string> Severities { get; } = AlertSeverities.All;

    private static readonly IReadOnlyList<string> Statuses = ["open", "resolved", "all"];

    /// <summary>The alert list's filters; see the spec's section 3.</summary>
    public sealed class AlertFilters
    {
        [FromQuery(Name = "status")] public string? Status { get; set; }
        [FromQuery(Name = "type")] public string[]? Type { get; set; }
        [FromQuery(Name = "severity")] public string[]? Severity { get; set; }
        [FromQuery(Name = "domain")] public string[]? Domain { get; set; }
        [FromQuery(Name = "group")] public string[]? Group { get; set; }
        [FromQuery(Name = "tag")] public string[]? Tag { get; set; }
        [FromQuery(Name = "acknowledgeable")] public string? Acknowledgeable { get; set; }
        [FromQuery(Name = "hasOpenTicket")] public string? HasOpenTicket { get; set; }
        [FromQuery(Name = "raisedAfter")] public string? RaisedAfter { get; set; }
        [FromQuery(Name = "raisedBefore")] public string? RaisedBefore { get; set; }
        [FromQuery(Name = "resolvedAfter")] public string? ResolvedAfter { get; set; }
        [FromQuery(Name = "resolvedBefore")] public string? ResolvedBefore { get; set; }
        [FromQuery(Name = "page")] public string? Page { get; set; }
        [FromQuery(Name = "pageSize")] public string? PageSize { get; set; }
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/alerts", ListAlertsAsync)
            .RequirePermission(Permission.AlertsView)
            .WithTags(ApiTags.Alerts)
            .WithName("ListAlerts")
            .WithSummary("List alerts")
            .WithDescription($"Alerts newest first, open ones by default, {DefaultPageSize} a page by default and at most {DomainReadEndpoints.MaximumPageSize}. Filters combine: an alert must match every filter given. A key limited to certain groups sees only alerts about its domains.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .DocumentsFilter("status", "open (default), resolved or all.", Statuses)
            .DocumentsFilter("type", "One or more alert types." + ApiFilterDocs.ListHint, AlertTypes.All.Select(type => type.Key))
            .DocumentsFilter("severity", "One or more severities." + ApiFilterDocs.ListHint, Severities)
            .DocumentsFilter("domain", "One or more domains, by id or name. A domain this key can't see is a 404." + ApiFilterDocs.ListHint)
            .DocumentsFilter("group", "One or more group ids: alerts for domains in any of them." + ApiFilterDocs.ListHint, type: JsonSchemaType.Integer)
            .DocumentsFilter("tag", "One or more tag ids: alerts for domains with any of them." + ApiFilterDocs.ListHint, type: JsonSchemaType.Integer)
            .DocumentsFilter("acknowledgeable", "true for open alerts this API can acknowledge, false for the rest.", type: JsonSchemaType.Boolean)
            .DocumentsFilter("hasOpenTicket", "true for alerts with an open PSA ticket, false for those without.", type: JsonSchemaType.Boolean)
            .DocumentsFilter("raisedAfter", "Raised at or after this date (2026-10-01) or time with a zone (2026-10-01T09:30:00Z).")
            .DocumentsFilter("raisedBefore", "Raised before this date or time.")
            .DocumentsFilter("resolvedAfter", "Resolved at or after this date or time. Open alerts never match.")
            .DocumentsFilter("resolvedBefore", "Resolved before this date or time. Open alerts never match.")
            .DocumentsFilter("page", "Which page, from 1 (default 1).", type: JsonSchemaType.Integer)
            .DocumentsFilter("pageSize", $"How many a page, 1 to {DomainReadEndpoints.MaximumPageSize} (default {DefaultPageSize}).", type: JsonSchemaType.Integer);

        api.MapPost("/alerts/{id:int}/acknowledge", AcknowledgeAsync)
            .RequirePermission(Permission.AlertsManage)
            .WithTags(ApiTags.Alerts)
            .WithName("AcknowledgeAlert")
            .WithSummary("Acknowledge an alert")
            .WithDescription("Closes a DMARC policy weakened or nameservers changed alert and accepts the current value as normal, closing its PSA ticket too. Other alerts close themselves once fixed, so acknowledging them is a conflict. ticketClosed is false when the ticket couldn't be closed and needs closing by hand.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Results<Ok<ApiPage<ApiAlert>>, ValidationProblem, ProblemHttpResult>> ListAlertsAsync(
        [AsParameters] AlertFilters filters, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var reader = new ApiFilterReader();
        var status = reader.Single("status", filters.Status, Statuses) ?? "open";
        var types = reader.OneOf("type", filters.Type, AlertTypes.All.Select(type => type.Key).ToList());
        var severities = reader.OneOf("severity", filters.Severity, Severities);
        var groupIds = reader.Ids("group", filters.Group);
        var tagIds = reader.Ids("tag", filters.Tag);
        var acknowledgeable = reader.Bool("acknowledgeable", filters.Acknowledgeable);
        var hasOpenTicket = reader.Bool("hasOpenTicket", filters.HasOpenTicket);
        var raisedAfter = reader.Date("raisedAfter", filters.RaisedAfter);
        var raisedBefore = reader.Date("raisedBefore", filters.RaisedBefore);
        var resolvedAfter = reader.Date("resolvedAfter", filters.ResolvedAfter);
        var resolvedBefore = reader.Date("resolvedBefore", filters.ResolvedBefore);
        reader.Range("raisedAfter", raisedAfter, "raisedBefore", raisedBefore);
        reader.Range("resolvedAfter", resolvedAfter, "resolvedBefore", resolvedBefore);
        var (pageNumber, size) = reader.Paging(filters.Page, filters.PageSize, DefaultPageSize);
        if (reader.Problem is { } problem)
        {
            return problem;
        }

        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = context.AlertEvents.AsNoTracking();
        query = status switch
        {
            "open" => query.Where(alert => !alert.IsResolved),
            "resolved" => query.Where(alert => alert.IsResolved),
            _ => query,
        };

        var visibleDomains = scope.Domains(context.Domains.AsNoTracking());
        if (scope.IsScoped)
        {
            var visibleNames = visibleDomains.Select(domain => domain.Name);
            query = query.Where(alert => visibleNames.Contains(alert.DomainName));
        }

        if (types.Count > 0)
        {
            query = query.Where(alert => types.Contains(alert.AlertType));
        }

        if (severities.Count > 0)
        {
            query = query.Where(alert => severities.Contains(alert.Severity));
        }

        var domainKeys = ApiFilterReader.Values(filters.Domain);
        if (domainKeys.Count > 0)
        {
            var resolved = await ApiDomainKey.ResolveAsync(visibleDomains, domainKeys, cancellationToken);
            if (resolved.MissingKey is { } missingKey)
            {
                return ApiProblems.NotFound($"domain {missingKey}");
            }

            var domainNames = resolved.Domains.Select(domain => domain.Name).ToList();
            query = query.Where(alert => domainNames.Contains(alert.DomainName));
        }

        if (groupIds.Count > 0)
        {
            // Groups outside the key's scope count for nothing, rather than revealing which visible domains are in them.
            var inScope = groupIds.Where(scope.Includes).ToList();
            var inGroups = visibleDomains.Where(domain => domain.Groups.Any(group => inScope.Contains(group.Id))).Select(domain => domain.Name);
            query = query.Where(alert => inGroups.Contains(alert.DomainName));
        }

        if (tagIds.Count > 0)
        {
            var tagged = visibleDomains.Where(domain => domain.Tags.Any(tag => tagIds.Contains(tag.Id))).Select(domain => domain.Name);
            query = query.Where(alert => tagged.Contains(alert.DomainName));
        }

        if (acknowledgeable is { } onlyAcknowledgeable)
        {
            var acknowledgeableTypes = AlertTypes.All.Select(type => type.Key).Where(AlertAcknowledgement.IsAcknowledgeable).ToList();
            query = onlyAcknowledgeable
                ? query.Where(alert => !alert.IsResolved && acknowledgeableTypes.Contains(alert.AlertType))
                : query.Where(alert => alert.IsResolved || !acknowledgeableTypes.Contains(alert.AlertType));
        }

        if (hasOpenTicket is { } wantsOpenTicket)
        {
            query = query.Where(alert => context.AlertTickets.Any(ticket => ticket.AlertEventId == alert.Id && ticket.IsOpen) == wantsOpenTicket);
        }

        if (raisedAfter is { } raisedFrom)
        {
            query = query.Where(alert => alert.CreatedUtc >= raisedFrom);
        }

        if (raisedBefore is { } raisedUntil)
        {
            query = query.Where(alert => alert.CreatedUtc < raisedUntil);
        }

        if (resolvedAfter is { } resolvedFrom)
        {
            query = query.Where(alert => alert.ResolvedUtc != null && alert.ResolvedUtc >= resolvedFrom);
        }

        if (resolvedBefore is { } resolvedUntil)
        {
            query = query.Where(alert => alert.ResolvedUtc != null && alert.ResolvedUtc < resolvedUntil);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var alerts = await query
            .OrderByDescending(alert => alert.CreatedUtc)
            .ThenByDescending(alert => alert.Id)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);
        var alertIds = alerts.Select(alert => alert.Id).ToList();
        var tickets = (await context.AlertTickets.AsNoTracking().Where(ticket => alertIds.Contains(ticket.AlertEventId))
                .OrderBy(ticket => ticket.CreatedUtc).ToListAsync(cancellationToken))
            .ToLookup(ticket => ticket.AlertEventId);
        var subjects = alerts.Select(alert => alert.DomainName).Distinct().ToList();
        var domainIds = await scope.Domains(context.Domains.AsNoTracking())
            .Where(domain => subjects.Contains(domain.Name))
            .ToDictionaryAsync(domain => domain.Name, domain => domain.Id, cancellationToken);
        var ruleIds = alerts.Where(alert => alert.AlertRuleId is not null).Select(alert => alert.AlertRuleId!.Value).Distinct().ToList();
        var ruleNames = scope.IsScoped || ruleIds.Count == 0
            ? new Dictionary<int, string>()
            : await context.AlertRules.AsNoTracking().Where(rule => ruleIds.Contains(rule.Id)).ToDictionaryAsync(rule => rule.Id, rule => rule.Name, cancellationToken);
        var items = alerts.Select(alert => new ApiAlert(
                alert.Id,
                alert.AlertType,
                AlertTypes.Find(alert.AlertType)?.DisplayName ?? alert.AlertType,
                alert.DomainName,
                domainIds.TryGetValue(alert.DomainName, out var domainId) ? new ApiNamedRef(domainId, alert.DomainName) : null,
                alert.Severity,
                alert.Title,
                alert.Message,
                alert.CreatedUtc,
                alert.IsResolved,
                alert.ResolvedUtc,
                !alert.IsResolved && AlertAcknowledgement.IsAcknowledgeable(alert.AlertType),
                tickets[alert.Id].Select(ticket => new ApiAlertTicket(ticket.Psa.ToString(), ticket.TicketId, ticket.IsOpen, ticket.CreatedUtc, ticket.LastCheckedUtc)).ToList(),
                alert.AlertRuleId is { } ruleId && ruleNames.TryGetValue(ruleId, out var ruleName) ? new ApiNamedRef(ruleId, ruleName) : null))
            .ToList();
        return TypedResults.Ok(new ApiPage<ApiAlert>(items, pageNumber, size, totalCount));
    }

    private static async Task<Results<Ok<ApiAcknowledgement>, ProblemHttpResult>> AcknowledgeAsync(
        int id, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, IPsaTicketService psaTicketService, CancellationToken cancellationToken)
    {
        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var alert = await context.AlertEvents.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (alert is null || (scope.IsScoped && !await scope.Domains(context.Domains).AnyAsync(domain => domain.Name == alert.DomainName, cancellationToken)))
        {
            return ApiProblems.NotFound($"alert {id}");
        }

        if (alert.IsResolved || !AlertAcknowledgement.IsAcknowledgeable(alert.AlertType))
        {
            return ApiProblems.Conflict(alert.IsResolved
                ? "This alert is already closed."
                : "Only DMARC policy weakened and nameservers changed alerts can be acknowledged. Other alerts close themselves once the problem is fixed.");
        }

        var result = await AlertAcknowledgement.AcknowledgeAsync(context, AuditActor.FromPrincipal(user), id, psaTicketService, cancellationToken);
        return result.Outcome switch
        {
            AcknowledgeOutcome.Acknowledged or AcknowledgeOutcome.AcknowledgedButTicketNotClosed =>
                TypedResults.Ok(new ApiAcknowledgement(result.Tickets.Failed == 0, result.Tickets.Closed, result.Tickets.Failed)),
            _ => ApiProblems.Conflict("This alert can't be acknowledged."),
        };
    }
}
