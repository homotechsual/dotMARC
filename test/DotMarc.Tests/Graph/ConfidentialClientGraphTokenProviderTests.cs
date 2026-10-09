using DotMarc.Graph;
using Xunit;

namespace DotMarc.Tests.Graph;

/// <summary>A cached Graph token carries the permissions granted when it was issued, so after permissions change the
/// provider must be able to fetch a fresh one rather than reuse the cached one.</summary>
public sealed class ConfidentialClientGraphTokenProviderTests
{
    [Fact]
    public async Task TokensComeFromTheCache_UntilInvalidated_ThenOneFreshTokenIsFetched()
    {
        var forceRefreshes = new List<bool>();
        var provider = new ConfidentialClientGraphTokenProvider((forceRefresh, _) =>
        {
            forceRefreshes.Add(forceRefresh);
            return Task.FromResult("token");
        });

        await provider.GetAccessTokenAsync(CancellationToken.None);
        provider.Invalidate();
        await provider.GetAccessTokenAsync(CancellationToken.None);
        await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal([false, true, false], forceRefreshes);
    }
}
