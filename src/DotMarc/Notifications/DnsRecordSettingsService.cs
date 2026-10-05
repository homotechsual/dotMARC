using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class DnsRecordSettingsService
{
    public static Task<DnsRecordSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.DnsRecordSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, SpfAllQualifier spfAllQualifier, CancellationToken cancellationToken = default)
    {
        var settings = await context.DnsRecordSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Ending for new SPF records",
            $"{DnsRecordSettings.ToQualifier(settings.SpfAllQualifier)}all", $"{DnsRecordSettings.ToQualifier(spfAllQualifier)}all");
        if (!changes.Any)
        {
            return;
        }

        settings.SpfAllQualifier = spfAllQualifier;
        AuditLog.Record(context, actor, AuditActions.DnsRecordSettingsSaved, AuditTarget.Settings("DNS records"), "Saved DNS record settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
