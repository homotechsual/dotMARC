using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

namespace DotMarc.Api;

/// <summary>Read-only access to alert rules and the names of the destinations they send to. Rules name groups, tags and domains across the whole
/// estate, so a key limited to some groups can't read them, as the rules page is closed to people limited to some groups. A destination's URL
/// is a secret and is never returned.</summary>
public static class AlertRuleEndpoints
{
    private const int DefaultPageSize = 50;

    public sealed class AlertRuleFilters
    {
        [FromQuery(Name = "kind")] public string[]? Kind { get; set; }
        [FromQuery(Name = "enabled")] public string? Enabled { get; set; }
        [FromQuery(Name = "page")] public string? Page { get; set; }
        [FromQuery(Name = "pageSize")] public string? PageSize { get; set; }
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/alert-rules", ListRulesAsync)
            .RequirePermission(Permission.AlertsView)
            .WithTags(ApiTags.AlertRules)
            .WithName("ListAlertRules")
            .WithSummary("List alert rules")
            .WithDescription($"Every alert rule in the order dotMARC tries them: by kind of alert, then top to bottom. {DefaultPageSize} a page by default and at most {DomainReadEndpoints.MaximumPageSize}. A key limited to certain groups can't read rules, because they name groups, tags and domains across the whole estate.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .DocumentsFilter("kind", "One or more kinds of alert." + ApiFilterDocs.ListHint, AlertTypes.All.Select(type => type.Key))
            .DocumentsFilter("enabled", "true for rules that are on, false for rules that are off.", type: JsonSchemaType.Boolean)
            .DocumentsFilter("page", "Which page, from 1 (default 1).", type: JsonSchemaType.Integer)
            .DocumentsFilter("pageSize", $"How many a page, 1 to {DomainReadEndpoints.MaximumPageSize} (default {DefaultPageSize}).", type: JsonSchemaType.Integer);

        api.MapGet("/alert-rules/{id:int}", GetRuleAsync)
            .RequirePermission(Permission.AlertsView)
            .WithTags(ApiTags.AlertRules)
            .WithName("GetAlertRule")
            .WithSummary("Get an alert rule")
            .WithDescription("One alert rule. A key limited to certain groups gets a 404, as for the list.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/alert-destinations", ListDestinationsAsync)
            .RequirePermission(Permission.AlertsView)
            .WithTags(ApiTags.AlertRules)
            .WithName("ListAlertDestinations")
            .WithSummary("List alert destinations")
            .WithDescription("The places alerts are sent: each destination's id, name, kind (Teams, Slack or GenericWebhook) and whether it is on. The webhook URL is a secret and is never returned. A key limited to certain groups gets an empty list.");
    }

    private static async Task<Results<Ok<ApiPage<ApiAlertRule>>, ValidationProblem, ProblemHttpResult>> ListRulesAsync(
        [AsParameters] AlertRuleFilters filters, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var reader = new ApiFilterReader();
        var kinds = reader.OneOf("kind", filters.Kind, AlertTypes.All.Select(type => type.Key).ToList());
        var enabled = reader.Bool("enabled", filters.Enabled);
        var (pageNumber, size) = reader.Paging(filters.Page, filters.PageSize, DefaultPageSize);
        if (reader.Problem is { } problem)
        {
            return problem;
        }

        if (ApiScope.From(user).IsScoped)
        {
            return ApiProblems.NotFound("alert rules");
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = context.AlertRules.AsNoTracking().AsQueryable();
        if (kinds.Count > 0)
        {
            query = query.Where(rule => kinds.Contains(rule.Kind));
        }

        if (enabled is { } wantEnabled)
        {
            query = query.Where(rule => rule.Enabled == wantEnabled);
        }

        var total = await query.CountAsync(cancellationToken);
        var rules = await query
            .Include(rule => rule.Scope).Include(rule => rule.Destinations).Include(rule => rule.EscalationDestinations).AsSplitQuery()
            .OrderBy(rule => rule.Kind).ThenBy(rule => rule.Position).ThenBy(rule => rule.Id)
            .Skip((pageNumber - 1) * size).Take(size)
            .ToListAsync(cancellationToken);
        var items = await ToApiAsync(context, rules, cancellationToken);
        return TypedResults.Ok(new ApiPage<ApiAlertRule>(items, pageNumber, size, total));
    }

    private static async Task<Results<Ok<ApiAlertRule>, ProblemHttpResult>> GetRuleAsync(
        int id, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        if (ApiScope.From(user).IsScoped)
        {
            return ApiProblems.NotFound($"alert rule {id}");
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rule = await context.AlertRules.AsNoTracking()
            .Include(candidate => candidate.Scope).Include(candidate => candidate.Destinations).Include(candidate => candidate.EscalationDestinations).AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (rule is null)
        {
            return ApiProblems.NotFound($"alert rule {id}");
        }

        return TypedResults.Ok((await ToApiAsync(context, [rule], cancellationToken)).Single());
    }

    private static async Task<Ok<ApiPage<ApiAlertDestination>>> ListDestinationsAsync(
        ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        if (ApiScope.From(user).IsScoped)
        {
            return TypedResults.Ok(new ApiPage<ApiAlertDestination>([], 1, DefaultPageSize, 0));
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var destinations = await AlertDestinationService.ListSummariesAsync(context, cancellationToken);
        var items = destinations.Select(destination => new ApiAlertDestination(destination.Id, destination.Name, destination.Kind.ToString(), destination.Enabled)).ToList();
        return TypedResults.Ok(new ApiPage<ApiAlertDestination>(items, 1, Math.Max(items.Count, DefaultPageSize), items.Count));
    }

    private static async Task<IReadOnlyList<ApiAlertRule>> ToApiAsync(DotMarcDbContext context, IReadOnlyList<AlertRule> rules, CancellationToken cancellationToken)
    {
        var destinationNames = (await AlertDestinationService.ListSummariesAsync(context, cancellationToken)).ToDictionary(destination => destination.Id, destination => destination.Name);
        var groups = await context.Groups.AsNoTracking().ToDictionaryAsync(group => group.Id, group => group.Name, cancellationToken);
        var tags = await context.Tags.AsNoTracking().ToDictionaryAsync(tag => tag.Id, tag => tag.Name, cancellationToken);
        var domains = await context.Domains.AsNoTracking().ToDictionaryAsync(domain => domain.Id, domain => domain.Name, cancellationToken);
        var names = new AlertScopeNames(groups, tags, domains);

        ApiNamedRef[] Named(IEnumerable<int> ids) =>
            [.. ids.Where(destinationNames.ContainsKey).Select(id => new ApiNamedRef(id, destinationNames[id]))];

        return [.. rules.Select(rule =>
        {
            var input = AlertRulePresentation.ToInput(rule);
            var hasDomainScope = AlertRuleKinds.Find(rule.Kind)?.HasDomainScope ?? false;
            return new ApiAlertRule(
                rule.Id, rule.Kind, rule.Name, rule.Enabled, rule.Severity, rule.Position, rule.StopProcessing, rule.CooldownMinutes, rule.AutoCloseDays,
                input.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value),
                rule.DestinationMode.ToString(), Named(rule.Destinations.Select(link => link.AlertDestinationId)), rule.TicketMode.ToString(),
                new ApiQuietHours(rule.QuietMode.ToString(),
                    rule.QuietStartMinutes is { } start ? AlertRulePresentation.ClockTime(start) : null,
                    rule.QuietEndMinutes is { } end ? AlertRulePresentation.ClockTime(end) : null,
                    rule.QuietIgnoredForCritical),
                new ApiEscalation(rule.EscalateAfterMinutes, Named(rule.EscalationDestinations.Select(link => link.AlertDestinationId))),
                AlertRulePresentation.DescribeScope(rule, names, hasDomainScope),
                [.. rule.Scope.Select(entry => new ApiRuleScopeEntry(entry.Target.ToString(), entry.TargetId, entry.Exclude))]);
        })];
    }
}
