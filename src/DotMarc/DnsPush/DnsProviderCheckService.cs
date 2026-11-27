using DotMarc.Data;
using DotMarc.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DnsPush;

public sealed record DnsProviderCheckOutcome(int DomainId, string DomainName, bool Succeeded, string? Error);

public sealed class DnsProviderCheckService(
    IDbContextFactory<DotMarcDbContext> factory, IDnsProviderDetector detector, ILogger<DnsProviderCheckService> logger)
{
    public async Task<DnsProviderCheckOutcome> CheckAsync(int domainId, IReadOnlyCollection<int> scope, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.SingleOrDefaultAsync(domain => domain.Id == domainId
            && (scope.Count == 0 || domain.Groups.Any(group => scope.Contains(group.Id))), cancellationToken);
        if (domain is null) return new(domainId, "", false, "Domain no longer exists or is outside your current scope.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await PollingService.RunSingleDnsProviderCheckAsync(domain, detector, timeout.Token);
            await db.SaveChangesAsync(cancellationToken);
            return new(domainId, domain.Name, true, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException
            or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Checking DNS provider for {Domain} failed", domain.Name);
            return new(domainId, domain.Name, false, "Provider lookup failed or timed out. Previous detection was kept; try again.");
        }
    }
}
