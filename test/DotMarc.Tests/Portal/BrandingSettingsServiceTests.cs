using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Portal;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Portal;

[Collection("Postgres")]
public sealed class BrandingSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public BrandingSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task Save_StoresTheBrand_AndAuditsTheChanges()
    {
        await using var context = CreateContext();
        var updated = await BrandingSettingsService.GetAsync(context);
        updated.ProductName = "Nova MSP";
        updated.PrimaryColour = "#0B5FFF";
        updated.SupportEmail = "help@nova-msp.example";

        await BrandingSettingsService.SaveAsync(context, TestActors.Admin, updated);

        await using var verify = CreateContext();
        var saved = await BrandingSettingsService.GetAsync(verify);
        Assert.Equal(("Nova MSP", "#0B5FFF", "help@nova-msp.example"), (saved.ProductName, saved.PrimaryColour, saved.SupportEmail));
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.BrandingSettingsSaved, entry.Action);
        Assert.Contains(entry.Changes, change => change.Field == "Product name" && change.New == "Nova MSP");
    }

    [Theory]
    [InlineData("PrimaryColour", "blue", "Primary colour must be a hex colour such as #1A73E8.")]
    [InlineData("SupportEmail", "not-an-email", "Support email isn't a valid email address.")]
    [InlineData("SupportUrl", "http://nova.example", "Support URL must be an absolute https:// address.")]
    [InlineData("ProductName", "", "Product name can't be empty.")]
    public async Task Save_RefusesInvalidValues(string field, string value, string message)
    {
        await using var context = CreateContext();
        var updated = await BrandingSettingsService.GetAsync(context);
        typeof(BrandingSettings).GetProperty(field)!.SetValue(updated, value);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => BrandingSettingsService.SaveAsync(context, TestActors.Admin, updated));

        Assert.StartsWith(message, exception.Message);
    }

    [Fact]
    public async Task ReplacingTheLogo_DeletesTheOldImage()
    {
        await using var context = CreateContext();
        var first = await BrandingSettingsService.UploadImageAsync(context, TestActors.Admin, PngBytes());
        var settings = await BrandingSettingsService.GetAsync(context);
        settings.LogoImageId = first.ImageId;
        await BrandingSettingsService.SaveAsync(context, TestActors.Admin, settings);

        var second = await BrandingSettingsService.UploadImageAsync(context, TestActors.Admin, PngBytes());
        settings.LogoImageId = second.ImageId;
        await BrandingSettingsService.SaveAsync(context, TestActors.Admin, settings);

        await using var verify = CreateContext();
        Assert.Equal(second.ImageId, (await verify.BrandingImages.SingleAsync()).Id);
    }

    [Fact]
    public async Task AGroupsLogo_SurvivesAnMspSaveThatReleasesAnotherLogo()
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Aurora Retail" };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        var groupLogo = await BrandingSettingsService.UploadImageAsync(context, TestActors.Admin, PngBytes());
        await GroupManagementService.SetBrandingAsync(context, TestActors.Admin, group.Id, new GroupBrandingInput(null, groupLogo.ImageId, null, null, null));
        var mspLogo = await BrandingSettingsService.UploadImageAsync(context, TestActors.Admin, PngBytes());
        var settings = await BrandingSettingsService.GetAsync(context);
        settings.LogoImageId = mspLogo.ImageId;
        await BrandingSettingsService.SaveAsync(context, TestActors.Admin, settings);

        settings.LogoImageId = null;
        await BrandingSettingsService.SaveAsync(context, TestActors.Admin, settings);

        await using var verify = CreateContext();
        Assert.Equal(groupLogo.ImageId, (await verify.BrandingImages.SingleAsync()).Id);
    }

    [Fact]
    public async Task ALogoThatNoLongerExists_IsLeftOutOfThePortalBrand_SoTheProductNameShows()
    {
        await using (var context = CreateContext())
        {
            var settings = await context.BrandingSettings.SingleAsync();
            settings.LogoImageId = Guid.NewGuid();
            settings.DarkLogoImageId = Guid.NewGuid();
            await context.SaveChangesAsync();
        }

        var brand = await new PortalBrandLoader(new FakeDbContextFactory(_connectionString)).LoadAsync([]);

        Assert.Equal(((Guid?)null, (Guid?)null), (brand.LogoImageId, brand.DarkLogoImageId));
    }

    [Fact]
    public async Task Save_RefusesALogoThatNoLongerExists()
    {
        await using var context = CreateContext();
        var settings = await BrandingSettingsService.GetAsync(context);
        settings.LogoImageId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => BrandingSettingsService.SaveAsync(context, TestActors.Admin, settings));

        Assert.StartsWith(BrandingImages.ExpiredUpload, exception.Message);
    }

    [Fact]
    public async Task GroupBranding_RefusesALogoThatNoLongerExists()
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Aurora Retail" };
        context.Groups.Add(group);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            GroupManagementService.SetBrandingAsync(context, TestActors.Admin, group.Id, new GroupBrandingInput(null, null, Guid.NewGuid(), null, null)));

        Assert.StartsWith(BrandingImages.ExpiredUpload, exception.Message);
    }

    [Fact]
    public async Task UploadingSomethingThatIsntALogo_SaysWhy_AndStoresNothing()
    {
        await using var context = CreateContext();

        var upload = await BrandingSettingsService.UploadImageAsync(context, TestActors.Admin, "GIF89a"u8.ToArray());

        Assert.Equal((null, BrandingImages.WrongTypeOrSize), (upload.ImageId, upload.Problem));
        Assert.Empty(context.BrandingImages);
    }

    private static byte[] PngBytes() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
}
