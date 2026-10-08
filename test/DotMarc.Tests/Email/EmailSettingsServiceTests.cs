using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Email;

[Collection("Postgres")]
public sealed class EmailSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public EmailSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task EmailStartsOff()
    {
        await using var context = CreateContext();

        Assert.Equal(EmailProvider.Off, (await EmailSettingsService.GetAsync(context)).Provider);
    }

    [Fact]
    public async Task SavingSmtp_StoresThePasswordAsASecret_AndAuditsWithoutIt()
    {
        await using var context = CreateContext();
        var secrets = new FakeSecretStore();
        var updated = await EmailSettingsService.GetAsync(context);
        updated.Provider = EmailProvider.Smtp;
        updated.FromAddress = "reports@nova-msp.example";
        updated.SmtpHost = "smtp.nova-msp.example";

        await EmailSettingsService.SaveAsync(context, TestActors.Admin, secrets, updated, "the-password");

        await using var verify = CreateContext();
        var saved = await EmailSettingsService.GetAsync(verify);
        Assert.Equal((EmailProvider.Smtp, true), (saved.Provider, saved.SmtpPasswordConfigured));
        Assert.Equal("the-password", secrets.Secrets[EmailSettings.SmtpPasswordSecretKey]);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.EmailSettingsSaved, entry.Action);
        Assert.DoesNotContain(entry.Changes, change => change.New == "the-password");
    }

    [Theory]
    [InlineData(EmailProvider.Graph, null, null, "From address is required to send email.")]
    [InlineData(EmailProvider.Graph, "not-an-email", null, "From address isn't a valid email address.")]
    [InlineData(EmailProvider.Smtp, "reports@nova-msp.example", null, "SMTP host is required.")]
    public async Task Save_RefusesIncompleteSettings(EmailProvider provider, string? fromAddress, string? smtpHost, string message)
    {
        await using var context = CreateContext();
        var updated = await EmailSettingsService.GetAsync(context);
        updated.Provider = provider;
        updated.FromAddress = fromAddress;
        updated.SmtpHost = smtpHost;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            EmailSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), updated, null));

        Assert.StartsWith(message, exception.Message);
    }

    [Fact]
    public async Task Save_RefusesAPortOutOfRange()
    {
        await using var context = CreateContext();
        var updated = await EmailSettingsService.GetAsync(context);
        updated.Provider = EmailProvider.Smtp;
        updated.FromAddress = "reports@nova-msp.example";
        updated.SmtpHost = "smtp.nova-msp.example";
        updated.SmtpPort = 70000;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            EmailSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), updated, null));

        Assert.StartsWith("SMTP port must be between 1 and 65535.", exception.Message);
    }
}
