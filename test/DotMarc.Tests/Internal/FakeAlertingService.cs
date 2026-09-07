using DotMarc.Notifications;

namespace DotMarc.Tests.Internal;

internal sealed class FakeAlertingService : IAlertingService
{
    public List<string> ResolvedDomains { get; } = [];
    public List<string> FlaggedNullRoutedDomains { get; } = [];

    public Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default)
    {
        ResolvedDomains.Add(domainName);
        return Task.CompletedTask;
    }

    public Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, CancellationToken cancellationToken = default)
    {
        FlaggedNullRoutedDomains.Add(domainName);
        return Task.CompletedTask;
    }
}
