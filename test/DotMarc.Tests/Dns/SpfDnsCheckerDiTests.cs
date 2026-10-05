using DotMarc.Dns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotMarc.Tests.Dns;

/// <summary>SpfDnsChecker gained a second constructor taking an ITxtRecordLookup, which the app also registers, so
/// typed-client activation must still find exactly one constructor to use.</summary>
public sealed class SpfDnsCheckerDiTests
{
    [Fact]
    public void TheTypedClient_ResolvesAlongsideTheTxtLookup()
    {
        var services = new ServiceCollection();
        services.AddHttpClient<ITxtRecordLookup, TxtRecordLookup>(client => client.BaseAddress = new Uri("https://cloudflare-dns.com/"));
        services.AddHttpClient<ISpfDnsChecker, SpfDnsChecker>(client => client.BaseAddress = new Uri("https://cloudflare-dns.com/"));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SpfDnsChecker>(provider.GetRequiredService<ISpfDnsChecker>());
    }
}
