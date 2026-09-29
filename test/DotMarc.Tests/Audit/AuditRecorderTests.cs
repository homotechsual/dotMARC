using DotMarc.Audit;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Audit;

[Collection("Postgres")]
public sealed class AuditRecorderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AuditRecorderTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = new FakeDbContextFactory(_connectionString).CreateDbContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private static AuditEntry SampleEntry() =>
        AuditLog.Create(TestActors.Admin, AuditEntryKind.PageView, AuditActions.PageViewed, null, "Opened /dashboard");

    [Fact]
    public async Task RecordAsync_SavesTheEntry()
    {
        var recorder = new AuditRecorder(new FakeDbContextFactory(_connectionString), NullLogger<AuditRecorder>.Instance);

        await recorder.RecordAsync(SampleEntry());

        await using var verify = new FakeDbContextFactory(_connectionString).CreateDbContext();
        Assert.Equal("Opened /dashboard", (await verify.AuditEntries.SingleAsync()).Summary);
    }

    [Fact]
    public async Task RecordAsync_DoesNotThrow_WhenTheDatabaseIsUnreachable()
    {
        var unreachable = new FakeDbContextFactory("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");
        var recorder = new AuditRecorder(unreachable, NullLogger<AuditRecorder>.Instance);

        var recording = await Record.ExceptionAsync(() => recorder.RecordAsync(SampleEntry()));

        Assert.Null(recording);
    }
}
