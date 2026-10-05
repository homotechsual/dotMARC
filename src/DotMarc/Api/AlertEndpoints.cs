using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class AlertEndpoints
{
    private const int DefaultPageSize = 50;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/alerts", ListAlertsAsync)
            .RequirePermission(Permission.AlertsView)
            .WithName("ListAlerts")
            .WithSummary("List alerts")
            .WithDescription($"Alerts newest first, open ones by default (status=all includes resolved), {DefaultPageSize} a page by default and at most {DomainReadEndpoints.MaximumPageSize}. A key limited to certain groups sees only alerts about its domains.");

        api.MapPost("/alerts/{id:int}/acknowledge", AcknowledgeAsync)
            .RequirePermission(Permission.AlertsManage)
            .WithName("AcknowledgeAlert")
            .WithSummary("Acknowledge an alert")
            .WithDescription("Closes a DMARC policy weakened or nameservers changed alert and accepts the current value as normal, closing its PSA ticket too. Other alerts close themselves once fixed, so acknowledging them is a conflict. ticketClosed is false when the ticket couldn't be closed and needs closing by hand.");
    }

    private static async Task<Results<Ok<ApiPage<ApiAlert>>, ValidationProblem>> ListAlertsAsync(
        ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, string? status, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var includeResolved = (status ?? "open").ToLowerInvariant() switch
        {
            "open" => (bool?)false,
            "all" => true,
            _ => null,
        };
        if (includeResolved is null)
        {
            return ApiProblems.Validation("status", "Use open or all.");
        }

        var pageNumber = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (pageNumber < 1)
        {
            return ApiProblems.Validation("page", "Must be 1 or more.");
        }

        if (size is < 1 or > DomainReadEndpoints.MaximumPageSize)
        {
            return ApiProblems.Validation("pageSize", $"Must be between 1 and {DomainReadEndpoints.MaximumPageSize}.");
        }

        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = context.AlertEvents.AsNoTracking();
        if (includeResolved == false)
        {
            query = query.Where(alert => !alert.IsResolved);
        }

        if (scope.IsScoped)
        {
            var visibleNames = scope.Domains(context.Domains).Select(domain => domain.Name);
            query = query.Where(alert => visibleNames.Contains(alert.DomainName));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var alerts = await query
            .OrderByDescending(alert => alert.CreatedUtc)
            .ThenByDescending(alert => alert.Id)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);
        var subjects = alerts.Select(alert => alert.DomainName).Distinct().ToList();
        var domainIds = await scope.Domains(context.Domains.AsNoTracking())
            .Where(domain => subjects.Contains(domain.Name))
            .ToDictionaryAsync(domain => domain.Name, domain => domain.Id, cancellationToken);
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
                !alert.IsResolved && AlertAcknowledgement.IsAcknowledgeable(alert.AlertType)))
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

        var outcome = await AlertAcknowledgement.AcknowledgeAsync(context, AuditActor.FromPrincipal(user), id, psaTicketService, cancellationToken);
        return outcome switch
        {
            AcknowledgeOutcome.Acknowledged => TypedResults.Ok(new ApiAcknowledgement(true)),
            AcknowledgeOutcome.AcknowledgedButTicketNotClosed => TypedResults.Ok(new ApiAcknowledgement(false)),
            _ => ApiProblems.Conflict("This alert can't be acknowledged."),
        };
    }
}
