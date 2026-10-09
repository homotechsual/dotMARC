using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class DomainWriteEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/domains", AddDomainAsync)
            .RequirePermission(Permission.DomainsAdd)
            .WithTags(ApiTags.Domains)
            .WithName("AddDomain")
            .WithSummary("Add a domain")
            .WithDescription("Adds a domain for dotMARC to monitor. It shows as missing reports until its first DMARC report arrives.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/domains/{domain}/groups", SetGroupsAsync)
            .RequirePermission(Permission.DomainsEdit)
            .WithTags(ApiTags.Domains)
            .WithName("SetDomainGroups")
            .WithSummary("Set a domain's groups")
            .WithDescription($"Replaces the domain's groups with exactly these. A key limited to certain groups can only name its own groups, and the domain keeps any groups outside them. {ApiDomainKey.Description}")
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/domains/{domain}/tags", SetTagsAsync)
            .RequirePermission(Permission.DomainsEdit)
            .WithTags(ApiTags.Domains)
            .WithName("SetDomainTags")
            .WithSummary("Set a domain's tags")
            .WithDescription($"Replaces the domain's tags with exactly these. {ApiDomainKey.Description}")
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/domains/{domain}/monitoring", SetMonitoringAsync)
            .RequirePermission(Permission.DomainsEdit)
            .WithTags(ApiTags.Domains)
            .WithName("SetDomainMonitoring")
            .WithSummary("Turn monitoring on or off")
            .WithDescription($"Whether dotMARC alerts on the domain's missing reports and DNS health. {ApiDomainKey.Description}")
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Results<Created<ApiDomain>, ValidationProblem, ProblemHttpResult>> AddDomainAsync(
        ApiAddDomainRequest request, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var scope = ApiScope.From(user);
        if (scope.IsScoped)
        {
            return ApiProblems.Forbidden(ApiProblems.ScopedKeyCantAdd);
        }

        if (!DomainNameValidator.TryNormalize(request.Name ?? "", out var normalizedName))
        {
            return ApiProblems.Validation("name", "That isn't a valid domain name.");
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var result = await DomainManagementService.AddDomainAsync(context, AuditActor.FromPrincipal(user), normalizedName, cancellationToken);
        if (result == DomainManagementService.AddDomainResult.AlreadyMonitored)
        {
            return ApiProblems.Conflict($"{normalizedName} is already in dotMARC.");
        }

        if (result == DomainManagementService.AddDomainResult.InvalidName)
        {
            return ApiProblems.Validation("name", "That isn't a valid domain name.");
        }

        var domainId = await context.Domains.Where(domain => domain.Name == normalizedName).Select(domain => domain.Id).SingleAsync(cancellationToken);
        var added = await DomainReadEndpoints.LoadForApiAsync(context, scope, domainId, cancellationToken);
        // Just added, so it has no reports and no pass rate yet.
        return TypedResults.Created($"/api/v1/domains/{domainId}", ApiDomain.From(added!, scope, passRate: null));
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetGroupsAsync(
        [Microsoft.AspNetCore.Mvc.FromRoute(Name = "domain")] string domainKey, ApiSetGroupsRequest request, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        if (request.GroupIds is not { } requestedIds)
        {
            return ApiProblems.Validation("groupIds", "Send groupIds, an array of group ids (empty to remove every group).");
        }

        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await ApiDomainKey.Matching(scope.Domains(context.Domains.AsNoTracking()), domainKey).Include(candidate => candidate.Groups).SingleOrDefaultAsync(cancellationToken);
        if (domain is null)
        {
            return ApiProblems.NotFound($"domain {domainKey}");
        }

        // Scope first: a scoped key's own groups exist, so answering "no such group" for others would tell it which
        // hidden group ids exist.
        var distinctIds = requestedIds.Distinct().ToList();
        if (distinctIds.Any(groupId => !scope.Includes(groupId)))
        {
            return ApiProblems.Forbidden("This key can only put domains in its own groups.");
        }

        var existingIds = await context.Groups.Where(group => distinctIds.Contains(group.Id)).Select(group => group.Id).ToListAsync(cancellationToken);
        var missingIds = distinctIds.Except(existingIds).ToList();
        if (missingIds.Count > 0)
        {
            return ApiProblems.Validation("groupIds", $"There's no group {missingIds[0]}.");
        }

        // A scoped key can't see the domain's other groups, so it can't remove them either.
        var keptOutsideScope = domain.Groups.Where(group => !scope.Includes(group.Id)).Select(group => group.Id);
        await GroupManagementService.SetDomainGroupsAsync(context, AuditActor.FromPrincipal(user), domain.Id, distinctIds.Concat(keptOutsideScope).Distinct().ToList(), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetTagsAsync(
        [Microsoft.AspNetCore.Mvc.FromRoute(Name = "domain")] string domainKey, ApiSetTagsRequest request, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        if (request.TagIds is not { } requestedIds)
        {
            return ApiProblems.Validation("tagIds", "Send tagIds, an array of tag ids (empty to remove every tag).");
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var id = await ApiDomainKey.Matching(ApiScope.From(user).Domains(context.Domains), domainKey).Select(domain => (int?)domain.Id).SingleOrDefaultAsync(cancellationToken);
        if (id is null)
        {
            return ApiProblems.NotFound($"domain {domainKey}");
        }

        var distinctIds = requestedIds.Distinct().ToList();
        var existingIds = await context.Tags.Where(tag => distinctIds.Contains(tag.Id)).Select(tag => tag.Id).ToListAsync(cancellationToken);
        var missingIds = distinctIds.Except(existingIds).ToList();
        if (missingIds.Count > 0)
        {
            return ApiProblems.Validation("tagIds", $"There's no tag {missingIds[0]}.");
        }

        await TagManagementService.SetDomainTagsAsync(context, AuditActor.FromPrincipal(user), id.Value, distinctIds, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetMonitoringAsync(
        [Microsoft.AspNetCore.Mvc.FromRoute(Name = "domain")] string domainKey, ApiSetMonitoringRequest request, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        if (request.Monitored is not { } monitored)
        {
            return ApiProblems.Validation("monitored", "Send monitored, true or false.");
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var id = await ApiDomainKey.Matching(ApiScope.From(user).Domains(context.Domains), domainKey).Select(domain => (int?)domain.Id).SingleOrDefaultAsync(cancellationToken);
        if (id is null)
        {
            return ApiProblems.NotFound($"domain {domainKey}");
        }

        await DomainManagementService.SetMonitoredAsync(context, AuditActor.FromPrincipal(user), id.Value, monitored, cancellationToken);
        return TypedResults.NoContent();
    }
}
