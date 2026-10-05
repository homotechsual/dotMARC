using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class AzureDnsPushProviderTests
{
    [Theory]
    [InlineData("contoso.com", "contoso.com", "@")]
    [InlineData("mta-sts.contoso.com", "contoso.com", "mta-sts")]
    [InlineData("selector1._domainkey.mail.contoso.co.uk", "contoso.co.uk", "selector1._domainkey.mail")]
    [InlineData("Contoso.com.", "contoso.com", "@")]
    public void RelativeName_IsTheApexSymbolOrThePartBeforeTheZone(string recordName, string zoneName, string expected)
    {
        Assert.Equal(expected, AzureDnsPushProvider.RelativeName(recordName, zoneName));
    }
}
