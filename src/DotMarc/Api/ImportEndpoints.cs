using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DomainImport;
using DotMarc.MtaSts;
using DotMarc.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

/// <summary>The bulk import page's planner and service behind a JSON body: rows become the same table a CSV with
/// domain, groups, tags and monitored columns would, so validation, permissions and auditing match the UI exactly.</summary>
public static class ImportEndpoints
{
    public const int MaximumRows = 500;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/domains/import", ImportAsync)
            .RequirePermission(Permission.DomainsAdd)
            .WithName("ImportDomains")
            .WithSummary("Import domains in bulk")
            .WithDescription($"Adds up to {MaximumRows} domains, with groups, tags and monitoring, exactly as the Import domains page does. existingDomains: skip (default), add (add groups and tags; a name written as -Name removes one) or match (make groups and tags match; send groups and tags on every domain or on none, with an empty list to clear). unknownNames: skip (default) or create (needs GroupsAdd or TagsAdd). Changing existing domains needs DomainsEdit. dryRun=true returns the plan without changing anything.");
    }

    private static async Task<Results<Ok<ApiImportResponse>, ValidationProblem, ProblemHttpResult>> ImportAsync(
        ApiImportRequest request, bool? dryRun, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, IMxHostsLookup mxHostsLookup,
        CancellationToken cancellationToken)
    {
        if (ApiScope.From(user).IsScoped)
        {
            return ApiProblems.Forbidden(ApiProblems.ScopedKeyCantAdd);
        }

        ExistingDomainMode mode;
        switch ((request.ExistingDomains ?? "skip").ToLowerInvariant())
        {
            case "skip": mode = ExistingDomainMode.Skip; break;
            case "add": mode = ExistingDomainMode.Add; break;
            case "match": mode = ExistingDomainMode.Match; break;
            default: return ApiProblems.Validation("existingDomains", "Use skip, add or match.");
        }

        var unknownNames = (request.UnknownNames ?? "skip").ToLowerInvariant();
        if (unknownNames is not ("skip" or "create"))
        {
            return ApiProblems.Validation("unknownNames", "Use skip or create.");
        }

        var domains = request.Domains ?? [];
        if (domains.Count is 0 or > MaximumRows)
        {
            return ApiProblems.Validation("domains", $"Send between 1 and {MaximumRows} domains.");
        }

        if (domains.Any(domain => (domain.Groups ?? []).Concat(domain.Tags ?? []).Any(name => name.Contains(';'))))
        {
            return ApiProblems.Validation("domains", "Group and tag names can't contain ';'.");
        }

        // In match mode a blank cell clears the list. A row that leaves out a list other rows send would become a blank
        // cell, so its groups or tags would be wiped although it never mentioned them.
        if (mode == ExistingDomainMode.Match)
        {
            foreach (var (field, sentBy) in new (string Field, Func<ApiImportDomain, bool> SentBy)[]
                     {
                         ("groups", domain => domain.Groups is not null),
                         ("tags", domain => domain.Tags is not null),
                     })
            {
                if (domains.Any(sentBy) && !domains.All(sentBy))
                {
                    return ApiProblems.Validation("domains",
                        $"In match mode, send {field} on every domain or on none. Use an empty list to clear a domain's {field}.");
                }
            }
        }

        ImportTable table;
        try
        {
            table = ImportTable.FromRows(ToRows(domains));
        }
        catch (ImportInputException exception)
        {
            return ApiProblems.Validation("domains", exception.Message);
        }

        var permissions = new ImportPermissions(
            CanEditDomains: Has(user, Permission.DomainsEdit),
            CanManageMtaSts: false,
            CanAddGroups: Has(user, Permission.GroupsAdd),
            CanAddTags: Has(user, Permission.TagsAdd));
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await ImportSnapshotLoader.LoadAsync(context, table, null, null, mxHostsLookup, cancellationToken);
        var plan = DomainImportPlanner.Plan(table, snapshot, mode, permissions);
        if (unknownNames == "skip" && plan.UnknownNames.Count > 0)
        {
            var leaveOut = plan.UnknownNames.ToDictionary(name => name.Key, _ => new NameResolution(NameChoice.LeaveOut));
            plan = DomainImportPlanner.Plan(table, snapshot, mode, permissions, leaveOut);
        }

        var notesByLine = plan.Rows.ToDictionary(row => row.LineNumber, row => row.Notes);
        var unknown = plan.UnknownNames
            .Select(name => new ApiImportUnknownName(name.Kind.ToString(), name.Name, name.Resolution.Choice == NameChoice.Create ? "create" : "leave out"))
            .ToList();
        if (dryRun == true)
        {
            var plannedRows = plan.Rows.Select(row => new ApiImportRow(row.LineNumber, row.Domain ?? row.RawDomain, DryRunOutcome(plan, row), row.Notes)).ToList();
            return TypedResults.Ok(new ApiImportResponse(true, plan.NewCount, plan.UpdateCount, plan.UnchangedCount, plan.SkippedExistingCount,
                plan.InvalidCount, plan.DuplicateCount, plannedRows, unknown, plan.Notices));
        }

        var result = await DomainImportService.ApplyAsync(context, AuditActor.FromPrincipal(user), plan, cancellationToken);
        var appliedRows = result.Rows
            .Select(row => new ApiImportRow(row.LineNumber, row.Domain, row.Outcome, notesByLine.GetValueOrDefault(row.LineNumber) ?? []))
            .ToList();
        return TypedResults.Ok(new ApiImportResponse(false, result.Added, result.Updated, result.Unchanged, result.SkippedExisting,
            result.Invalid, result.Duplicates, appliedRows, unknown, plan.Notices));
    }

    /// <summary>A header row naming only the columns some domain actually sent, so match mode never reads a column
    /// nobody sent as "clear it".</summary>
    private static List<ImportRow> ToRows(IReadOnlyList<ApiImportDomain> domains)
    {
        var sendsGroups = domains.Any(domain => domain.Groups is not null);
        var sendsTags = domains.Any(domain => domain.Tags is not null);
        var sendsMonitored = domains.Any(domain => domain.Monitored is not null);
        var header = new List<string> { "domain" };
        if (sendsGroups)
        {
            header.Add("groups");
        }

        if (sendsTags)
        {
            header.Add("tags");
        }

        if (sendsMonitored)
        {
            header.Add("monitored");
        }

        var rows = new List<ImportRow> { new(0, header) };
        for (var index = 0; index < domains.Count; index++)
        {
            var domain = domains[index];
            var cells = new List<string> { (domain.Name ?? "").Trim() };
            if (sendsGroups)
            {
                cells.Add(string.Join(';', domain.Groups ?? []));
            }

            if (sendsTags)
            {
                cells.Add(string.Join(';', domain.Tags ?? []));
            }

            if (sendsMonitored)
            {
                cells.Add(domain.Monitored switch { true => "yes", false => "no", null => "" });
            }

            rows.Add(new ImportRow(index + 1, cells));
        }

        return rows;
    }

    private static string DryRunOutcome(ImportPlan plan, PlannedRow row) => row.Status switch
    {
        ImportRowStatus.New => "add",
        ImportRowStatus.Duplicate => "duplicate",
        ImportRowStatus.Invalid => "invalid",
        _ when plan.Mode == ExistingDomainMode.Skip => "skip",
        _ when row.Target is null => "unchanged",
        _ => "update",
    };

    private static bool Has(ClaimsPrincipal user, Permission permission) =>
        user.HasClaim(UserAccessClaimsTransformation.PermissionClaimType, permission.ToString());
}
