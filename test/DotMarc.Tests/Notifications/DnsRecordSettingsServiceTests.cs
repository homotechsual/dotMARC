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
            await DnsRecordSettingsService.SaveAsync(context, TestActors.Admin, SpfAllQualifier.Fail);
            await DnsRecordSettingsService.SaveAsync(context, TestActors.Admin, SpfAllQualifier.Fail);
        }

        await using var verify = CreateContext();
        Assert.Equal(SpfAllQualifier.Fail, (await DnsRecordSettingsService.GetAsync(verify)).SpfAllQualifier);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.DnsRecordSettingsSaved, entry.Action);
        Assert.Contains(new AuditFieldChange("Ending for new SPF records", "~all", "-all"), entry.Changes);
    }
}
