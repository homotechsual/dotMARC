using DotMarc.Notifications;
using DotMarc.Reporting;

namespace DotMarc.Tests.Internal;

/// <summary>Records client report alerts; the domain alert methods do nothing.</summary>
internal sealed class RecordingAlerts : IAlertingService
{
    public List<(int GroupId, string GroupName, string PeriodLabel, string Error)> Raised { get; } = [];
    public List<int> Resolved { get; } = [];

    public Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken cancellationToken = default)
    {
        Raised.Add((groupId, groupName, periodLabel, error));
        return Task.CompletedTask;
    }

    public Task ResolveClientReportFailedAsync(int groupId, CancellationToken cancellationToken = default)
    {
        Resolved.Add(groupId);
        return Task.CompletedTask;
    }
}
