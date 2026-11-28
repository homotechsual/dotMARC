namespace DotMarc.Dns;

public enum ObservedDnsRecordType { Ns = 2, Cname = 5, Mx = 15, Txt = 16 }
public sealed record ObservedDnsRecord(string Name, ObservedDnsRecordType Type, string Value);

public interface IObservedDnsRecordLookup
{
    Task<IReadOnlyList<ObservedDnsRecord>> GetRecordsAsync(string name, ObservedDnsRecordType type, CancellationToken cancellationToken);
}
