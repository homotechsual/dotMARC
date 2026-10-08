using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.Graph;
using DotMarc.Notifications;
using DotMarc.Portal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotMarc.Email;

/// <summary>Builds the sender for the saved settings each time it's asked, so a settings change takes effect at once.
/// The demo never sends email, whatever its settings say.</summary>
public sealed class EmailSenderFactory(
    IDbContextFactory<DotMarcDbContext> dbFactory,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IServiceProvider services,
    IOptions<DemoOptions> demoOptions) : IEmailSenderFactory
{
    public async Task<IEmailSender?> GetAsync(CancellationToken cancellationToken)
    {
        if (demoOptions.Value.Enabled)
        {
            return null;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await EmailSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        switch (settings.Provider)
        {
            case EmailProvider.Graph:
                return new GraphEmailSender(httpClientFactory.CreateClient(GraphEmailSender.HttpClientName),
                    services.GetRequiredService<IGraphTokenProvider>(), settings.FromAddress!);
            case EmailProvider.Smtp:
                var password = settings.SmtpPasswordConfigured
                    ? await secretStore.GetSecretAsync(EmailSettings.SmtpPasswordSecretKey, cancellationToken).ConfigureAwait(false)
                    : null;
                var fromName = settings.FromName ?? (await BrandingSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false)).ProductName;
                return new SmtpEmailSender(new SmtpConnection(settings.SmtpHost!, settings.SmtpPort, settings.SmtpSecurity,
                    settings.SmtpUsername, password, settings.FromAddress!, fromName));
            default:
                return null;
        }
    }
}
