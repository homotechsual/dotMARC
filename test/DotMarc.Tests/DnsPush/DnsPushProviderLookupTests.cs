using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsPushProviderLookupTests
{
    private sealed class FakeDnsPushProvider(string providerKey, bool isConfigured) : IDnsPushProvider
    {
        public string ProviderKey => providerKey;

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(isConfigured);

        public Task<string> BuildAuthorizationUrlAsync(string state, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<DnsPushResult> ExchangeAndPushAsync(string code, string codeVerifier, string redirectUri, DnsRecordChange change, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    [Fact]
    public async Task FindConfiguredAsync_ReturnsNull_WhenProviderKeyIsNull()
    {
        var providers = new[] { new FakeDnsPushProvider("cloudflare", isConfigured: true) };

        var result = await providers.FindConfiguredAsync(null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindConfiguredAsync_ReturnsNull_WhenNoProviderKeyMatches()
    {
        var providers = new[] { new FakeDnsPushProvider("cloudflare", isConfigured: true) };

        var result = await providers.FindConfiguredAsync("azure-dns", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindConfiguredAsync_ReturnsNull_WhenMatchingProviderIsNotConfigured()
    {
        var providers = new[] { new FakeDnsPushProvider("cloudflare", isConfigured: false) };

        var result = await providers.FindConfiguredAsync("cloudflare", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindConfiguredAsync_ReturnsProvider_WhenMatchingProviderIsConfigured()
    {
        var configured = new FakeDnsPushProvider("cloudflare", isConfigured: true);
        var providers = new IDnsPushProvider[] { new FakeDnsPushProvider("azure-dns", isConfigured: true), configured };

        var result = await providers.FindConfiguredAsync("cloudflare", CancellationToken.None);

        Assert.Same(configured, result);
    }
}
