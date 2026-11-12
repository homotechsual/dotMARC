using DotMarc.DomainImport;
using DotMarc.Psa;
using Xunit;

namespace DotMarc.Tests.Psa;

public sealed class PsaKindTests
{
    [Fact]
    public void PsasAreListedHaloNinjaConnectWiseAutotaskSuperOps()
    {
        Assert.Equal([PsaKind.HaloPsa, PsaKind.NinjaOne, PsaKind.ConnectWise, PsaKind.Autotask, PsaKind.SuperOps], Enum.GetValues<PsaKind>());
    }

    [Fact]
    public void SuperOps_HasItsNameAndCompanyLabel()
    {
        Assert.Equal("SuperOps", PsaKind.SuperOps.DisplayName());
        Assert.Equal("SuperOps client", PsaKind.SuperOps.CompanyLabel());
    }

    [Fact]
    public void NinjaOne_HasItsNameAndCompanyLabel()
    {
        Assert.Equal("NinjaOne", PsaKind.NinjaOne.DisplayName());
        Assert.Equal("NinjaOne organisation", PsaKind.NinjaOne.CompanyLabel());
    }

    [Fact]
    public void ImportColumns_FollowThePsaOrder()
    {
        Assert.Equal(Enum.GetValues<PsaKind>(), PsaImportColumns.All.Select(column => column.Psa));
    }
}
