using DotMarc.MtaSts;

namespace DotMarc.Tests.Internal;

internal sealed class FakeMtaStsCnameLookup : IMtaStsCnameLookup
{
    public string? Cname { get; set; }
    public string? AsuidTxt { get; set; }

    public Task<string?> LookupAsync(string domainName, CancellationToken cancellationToken) => Task.FromResult(Cname);

    public Task<string?> LookupAsuidTxtAsync(string domainName, CancellationToken cancellationToken) => Task.FromResult(AsuidTxt);
}
