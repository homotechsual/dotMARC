using DotMarc.Data;
using DotMarc.MtaSts;
using Xunit;

namespace DotMarc.Tests.MtaSts;

public class MtaStsPolicyRendererTests
{
    [Fact]
    public void Render_ProducesCrLfSeparatedFieldsInOrder_ForTestingMode()
    {
        var policy = MtaStsPolicyRenderer.Render(MtaStsMode.Testing, ["mail.contoso.io"], 604_800);

        Assert.Equal("version: STSv1\r\nmode: testing\r\nmx: mail.contoso.io\r\nmax_age: 604800\r\n", policy);
    }

    [Fact]
    public void Render_LowercasesMode_ForEnforceAndNone()
    {
        Assert.Contains("mode: enforce", MtaStsPolicyRenderer.Render(MtaStsMode.Enforce, [], 86_400));
        Assert.Contains("mode: none", MtaStsPolicyRenderer.Render(MtaStsMode.None, [], 86_400));
    }

    [Fact]
    public void Render_EmitsOneMxLinePerHost_InTheGivenOrder()
    {
        var policy = MtaStsPolicyRenderer.Render(MtaStsMode.Enforce, ["mail.contoso.io", "*.contoso.io"], 604_800);

        var mxLines = policy.Split("\r\n").Where(line => line.StartsWith("mx: ", StringComparison.Ordinal));
        Assert.Equal(["mx: mail.contoso.io", "mx: *.contoso.io"], mxLines);
    }

    [Fact]
    public void Render_EmitsNoMxLines_WhenHostListIsEmpty()
    {
        var policy = MtaStsPolicyRenderer.Render(MtaStsMode.None, [], 604_800);

        Assert.DoesNotContain("mx:", policy);
    }
}
