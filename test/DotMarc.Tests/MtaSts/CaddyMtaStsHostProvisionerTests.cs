using DotMarc.MtaSts;
using Xunit;

namespace DotMarc.Tests.MtaSts;

public sealed class CaddyMtaStsHostProvisionerTests
{
    [Fact]
    public async Task GetDomainVerificationIdAsync_ReturnsNull()
    {
        var provisioner = new CaddyMtaStsHostProvisioner();

        var result = await provisioner.GetDomainVerificationIdAsync(CancellationToken.None);

        Assert.Null(result);
    }
}
