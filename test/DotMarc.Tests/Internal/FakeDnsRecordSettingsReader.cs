using DotMarc.DnsPush;
using DotMarc.Notifications;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDnsRecordSettingsReader : IDnsRecordSettingsReader
{
    public DnsRecordSettings Settings { get; set; } = new();

    public Task<DnsRecordSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
}
