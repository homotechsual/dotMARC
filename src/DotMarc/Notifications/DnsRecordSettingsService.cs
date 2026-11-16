using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class DnsRecordSettingsService
{
    public static Task<DnsRecordSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.DnsRecordSettings.SingleAsync(cancellationToken);

    /// <summary>Saves the edited values. Returns false, changing nothing, when <paramref name="additionalRuaAddress"/>
    /// is not a single plain address.</summary>
    public static async Task<bool> SaveAsync(DotMarcDbContext context, AuditActor actor, SpfAllQualifier spfAllQualifier,
        bool keepOtherRuaAddresses, string? additionalRuaAddress, CancellationToken cancellationToken = default)
    {
        if (!DnsRecordSettings.TryNormaliseRuaAddress(additionalRuaAddress, out var additional))
        {
            return false;
        }

        var settings = await context.DnsRecordSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Ending for new SPF records", $"{DnsRecordSettings.ToQualifier(settings.SpfAllQualifier)}all", $"{DnsRecordSettings.ToQualifier(spfAllQualifier)}all")
            .Field("Keep other rua addresses on DMARC push", settings.KeepOtherRuaAddresses ? "yes" : "no", keepOtherRuaAddresses ? "yes" : "no")
            .Field("Additional rua address", settings.AdditionalRuaAddress ?? "none", additional ?? "none");
        if (!changes.Any)
        {
            return true;
        }

        settings.SpfAllQualifier = spfAllQualifier;
        settings.KeepOtherRuaAddresses = keepOtherRuaAddresses;
        settings.AdditionalRuaAddress = additional;
        AuditLog.Record(context, actor, AuditActions.DnsRecordSettingsSaved, AuditTarget.Settings("DNS records"), "Saved DNS record settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
