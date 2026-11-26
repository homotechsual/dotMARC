using DotMarc.DnsPush;
using DotMarc.Notifications;
using DotMarc.Baselines;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDnsRecordSettingsReader : IDnsRecordSettingsReader
{
    public DnsRecordSettings Settings { get; set; } = new();
    public DmarcReportAddress? Destination { get; set; }

    public Task<DnsRecordSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
    public Task<DmarcReportAddress> GetReportAddressAsync(int domainId, CancellationToken cancellationToken) =>
        Task.FromResult(Destination ?? new(Settings.AdditionalRuaAddress, "System default"));
}
