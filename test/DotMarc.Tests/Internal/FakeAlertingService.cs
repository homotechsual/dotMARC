using DotMarc.Notifications;
using DotMarc.Reporting;

namespace DotMarc.Tests.Internal;

internal sealed class FakeAlertingService : IAlertingService
{
    public List<string> ResolvedDomains { get; } = [];
    public List<string> FlaggedNullRoutedDomains { get; } = [];
    public List<ReasonBreakdown> FlaggedReasonBreakdowns { get; } = [];

    public Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default)
    {
        ResolvedDomains.Add(domainName);
        return Task.CompletedTask;
    }

    public Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default)
    {
        FlaggedNullRoutedDomains.Add(domainName);
        FlaggedReasonBreakdowns.Add(reasonBreakdown);
        return Task.CompletedTask;
    }
}
