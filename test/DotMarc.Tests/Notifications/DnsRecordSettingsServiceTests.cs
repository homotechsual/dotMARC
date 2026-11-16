using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class DnsRecordSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DnsRecordSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task AFreshDatabase_EndsNewSpfRecordsWithSoftFail()
    {
        await using var context = CreateContext();

        Assert.Equal(SpfAllQualifier.SoftFail, (await DnsRecordSettingsService.GetAsync(context)).SpfAllQualifier);
    }

    [Fact]
    public async Task SaveAsync_SavesAndAudits_AndRecordsNothingWhenUnchanged()
    {
        await using (var context = CreateContext())
        {
            await DnsRecordSettingsService.SaveAsync(context, TestActors.Admin, SpfAllQualifier.Fail, true, null);
            await DnsRecordSettingsService.SaveAsync(context, TestActors.Admin, SpfAllQualifier.Fail, true, null);
        }

        await using var verify = CreateContext();
        Assert.Equal(SpfAllQualifier.Fail, (await DnsRecordSettingsService.GetAsync(verify)).SpfAllQualifier);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.DnsRecordSettingsSaved, entry.Action);
        Assert.Contains(new AuditFieldChange("Ending for new SPF records", "~all", "-all"), entry.Changes);
    }

    [Fact]
    public async Task AFreshDatabase_KeepsOtherRuaAddresses_AndHasNoAdditionalAddress()
    {
        await using var context = CreateContext();

        var settings = await DnsRecordSettingsService.GetAsync(context);

        Assert.True(settings.KeepOtherRuaAddresses);
        Assert.Null(settings.AdditionalRuaAddress);
    }

    [Fact]
    public async Task SaveAsync_SavesTheRuaSettings_TrimmingTheAddress_AndAuditsThem()
    {
        await using (var context = CreateContext())
        {
            Assert.True(await DnsRecordSettingsService.SaveAsync(context, TestActors.Admin, SpfAllQualifier.SoftFail, false, "  second@tool.example "));
        }

        await using var verify = CreateContext();
        var settings = await DnsRecordSettingsService.GetAsync(verify);
        Assert.False(settings.KeepOtherRuaAddresses);
        Assert.Equal("second@tool.example", settings.AdditionalRuaAddress);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Contains(new AuditFieldChange("Keep other rua addresses on DMARC push", "yes", "no"), entry.Changes);
        Assert.Contains(new AuditFieldChange("Additional rua address", "none", "second@tool.example"), entry.Changes);
    }

    [Theory]
    [InlineData("mailto:a@example.com")]
    [InlineData("a@example.com,b@example.com")]
    [InlineData("not an address")]
    [InlineData("a@example.com!10m")]
    public async Task SaveAsync_RejectsAnAdditionalAddressThatWouldBreakTheRuaList_ChangingNothing(string address)
    {
        await using var context = CreateContext();

        Assert.False(await DnsRecordSettingsService.SaveAsync(context, TestActors.Admin, SpfAllQualifier.Fail, false, address));

        await using var verify = CreateContext();
        var settings = await DnsRecordSettingsService.GetAsync(verify);
        Assert.Equal((SpfAllQualifier.SoftFail, true, null), (settings.SpfAllQualifier, settings.KeepOtherRuaAddresses, settings.AdditionalRuaAddress));
        Assert.Empty(await verify.AuditEntries.ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_ClearsTheAdditionalAddress_WhenSavedBlank()
    {
        await using var context = CreateContext();
        await DnsRecordSettingsService.SaveAsync(context, TestActors.Admin, SpfAllQualifier.SoftFail, true, "second@tool.example");

        await DnsRecordSettingsService.SaveAsync(context, TestActors.Admin, SpfAllQualifier.SoftFail, true, "  ");

        await using var verify = CreateContext();
        Assert.Null((await DnsRecordSettingsService.GetAsync(verify)).AdditionalRuaAddress);
    }
}
