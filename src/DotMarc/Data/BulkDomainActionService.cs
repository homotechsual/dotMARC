using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Baselines;
using DotMarc.Dns;
using DotMarc.DnsPush;
using DotMarc.Graph;
using DotMarc.Ingestion;
using DotMarc.Portal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotMarc.Data;

public enum BulkDomainAction { Remove, AddGroups, RemoveGroups, AddTags, RemoveTags, RecheckProviders, CheckDns, SetTarget }
public enum DomainActionOutcome { Succeeded, Skipped, Failed }
public sealed record DomainActionResult(int DomainId, DomainActionOutcome Outcome, string Message);
public sealed record BulkDomainActionRequest(BulkDomainAction Action, IReadOnlyCollection<int> AssignmentIds, DmarcTarget? Target = null);

public sealed class BulkDomainActionService(
    IDbContextFactory<DotMarcDbContext> factory,
    IAuthorizationService authorization,
    DnsProviderCheckService providerChecks,
    ISpfDnsChecker spf, IMxDnsChecker mx, IDmarcDnsChecker dmarc, ITlsrptDnsChecker tlsrpt, IDkimDnsChecker dkim,
    IOptions<GraphOptions> graph,
    ILogger<BulkDomainActionService> logger)
{
    public static Permission RequiredPermission(BulkDomainAction action) => action switch
    {
        BulkDomainAction.Remove => Permission.DomainsDelete,
        BulkDomainAction.SetTarget => Permission.BaselinesManage,
        _ => Permission.DomainsEdit
    };

    public async Task<DomainActionResult> ExecuteAsync(int domainId, BulkDomainActionRequest request,
        ClaimsPrincipal user, AuditActor actor, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Action))
            throw new ArgumentOutOfRangeException(nameof(request));
        if (ClientPortalGate.IsPortalUser(user) ||
            !(await authorization.AuthorizeAsync(user, nameof(Permission.DomainsView))).Succeeded ||
            !(await authorization.AuthorizeAsync(user, RequiredPermission(request.Action).ToString())).Succeeded)
            return new(domainId, DomainActionOutcome.Skipped, "You no longer have permission for this action.");

        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var scope = PortalData.ScopedGroupIds(user);
            var domain = await db.Domains.Include(domain => domain.Groups).Include(domain => domain.Tags).Include(domain => domain.DkimRecords).AsSplitQuery()
                .SingleOrDefaultAsync(domain => domain.Id == domainId &&
                    (scope.Count == 0 || domain.Groups.Any(group => scope.Contains(group.Id))), cancellationToken);
            if (domain is null)
                return new(domainId, DomainActionOutcome.Skipped, "Domain no longer exists or is outside your current scope.");

            var assignments = request.AssignmentIds.Distinct().ToArray();
            switch (request.Action)
            {
                case BulkDomainAction.Remove:
                    await DomainManagementService.RemoveDomainAsync(db, actor, domainId, cancellationToken);
                    break;
                case BulkDomainAction.AddGroups:
                case BulkDomainAction.RemoveGroups:
                    if (assignments.Length == 0 || assignments.Any(id => scope.Count > 0 && !scope.Contains(id)) ||
                        await db.Groups.CountAsync(group => assignments.Contains(group.Id), cancellationToken) != assignments.Length)
                        return new(domainId, DomainActionOutcome.Skipped, "Choose existing groups within your current scope.");
                    var visible = domain.Groups.Where(group => scope.Count == 0 || scope.Contains(group.Id)).Select(group => group.Id);
                    var requested = request.Action == BulkDomainAction.AddGroups
                        ? visible.Union(assignments).ToArray()
                        : visible.Except(assignments).ToArray();
                    var groupResult = await GroupManagementService.SetScopedDomainGroupsAsync(db, actor, domainId, scope, requested, cancellationToken);
                    if (groupResult != GroupManagementService.SetScopedDomainGroupsResult.Updated)
                        return new(domainId, DomainActionOutcome.Skipped, groupResult == GroupManagementService.SetScopedDomainGroupsResult.LastAccessibleGroup
                            ? "Keep at least one of your accessible groups." : "Domain or groups are no longer available.");
                    break;
                case BulkDomainAction.AddTags:
                case BulkDomainAction.RemoveTags:
                    if (assignments.Length == 0 || await db.Tags.CountAsync(tag => assignments.Contains(tag.Id), cancellationToken) != assignments.Length)
                        return new(domainId, DomainActionOutcome.Skipped, "Choose existing tags.");
                    var tags = domain.Tags.Select(tag => tag.Id);
                    var requestedTags = request.Action == BulkDomainAction.AddTags ? tags.Union(assignments) : tags.Except(assignments);
                    await TagManagementService.SetDomainTagsAsync(db, actor, domainId, requestedTags.ToArray(), cancellationToken);
                    break;
                case BulkDomainAction.SetTarget:
                    if (request.Target is { } target && (!Enum.IsDefined(target.Policy) ||
                        target.SubdomainPolicy is { } subdomain && !Enum.IsDefined(subdomain)))
                        return new(domainId, DomainActionOutcome.Skipped, "Choose a valid DMARC target.");
                    await DmarcTargetService.SetDomainAsync(db, actor, domainId, request.Target, cancellationToken);
                    break;
                case BulkDomainAction.RecheckProviders:
                    var provider = await providerChecks.CheckAsync(domainId, scope, cancellationToken);
                    return new(domainId, provider.Succeeded ? DomainActionOutcome.Succeeded : DomainActionOutcome.Failed,
                        provider.Error ?? "Provider check completed. DNS records were not changed.");
                case BulkDomainAction.CheckDns:
                    if (string.IsNullOrWhiteSpace(graph.Value.MailboxAddress))
                        return new(domainId, DomainActionOutcome.Skipped, "Configure the reporting mailbox before running DNS checks.");
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeout.CancelAfter(TimeSpan.FromMinutes(2));
                        // SPF must run first: null-sender handling changes the DMARC and DKIM checks.
                        await PollingService.RunSingleSpfCheckAsync(domain, spf, timeout.Token);
                        await PollingService.RunSingleMxCheckAsync(domain, mx, timeout.Token);
                        await PollingService.RunSingleDmarcCheckAsync(domain, dmarc, graph.Value.MailboxAddress, timeout.Token);
                        await PollingService.RunSingleDmarcAuthorizationCheckAsync(domain, dmarc, graph.Value.MailboxAddress, timeout.Token);
                        await PollingService.RunSingleTlsrptCheckAsync(domain, tlsrpt, graph.Value.MailboxAddress, timeout.Token);
                        await PollingService.RunSingleDkimCheckAsync(domain, dkim, timeout.Token);
                    }
                    await db.SaveChangesAsync(cancellationToken);
                    return new(domainId, DomainActionOutcome.Succeeded, "DNS checks completed; review the resulting statuses. No DNS or hosting changes were made.");
            }
            return new(domainId, DomainActionOutcome.Succeeded, "Action completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or Npgsql.NpgsqlException
            or DbUpdateException or HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Domain action {Action} failed for domain {DomainId}", request.Action, domainId);
            return new(domainId, DomainActionOutcome.Failed, "Action failed or timed out. Refresh the domain before retrying; completed domains are not rolled back.");
        }
    }
}
