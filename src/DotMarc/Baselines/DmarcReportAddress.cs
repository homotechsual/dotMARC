using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Baselines;

public sealed record DmarcReportAddress(string? Address, string Source, bool Conflict = false)
{
    public static DmarcReportAddress Resolve(Domain domain, DnsRecordSettings settings)
    {
        if (domain.OverrideAdditionalRuaAddress)
            return new(domain.AdditionalRuaAddress, "Domain override");
        var groups = domain.Groups.Where(group => group.OverrideAdditionalRuaAddress).ToList();
        if (groups.Count == 0) return new(settings.AdditionalRuaAddress, "System default");
        var addresses = groups.Select(group => group.AdditionalRuaAddress ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return addresses.Count > 1
            ? new(null, "Conflicting group report addresses; set a domain override before reviewing DMARC DNS.", true)
            : new(groups[0].AdditionalRuaAddress, "Group override");
    }

    public static async Task SaveAsync(DotMarcDbContext db, AuditActor actor, int? domainId, int? groupId,
        bool overrideAddress, string? address, CancellationToken cancellationToken = default)
    {
        if (!DnsRecordSettings.TryNormaliseRuaAddress(address, out var normalized))
            throw new ArgumentException("Enter one plain email address, without mailto:, spaces or separators.", nameof(address));
        if (domainId is not null && groupId is not null) throw new ArgumentException("Choose one scope.");
        if (!overrideAddress && (domainId is not null || groupId is not null)) normalized = null;
        bool previousOverride;
        string? previous;
        AuditTarget target;
        Action store;
        if (domainId is { } id)
        {
            var domain = await db.Domains.SingleAsync(domain => domain.Id == id, cancellationToken);
            previousOverride = domain.OverrideAdditionalRuaAddress;
            previous = domain.AdditionalRuaAddress;
            target = AuditTarget.For(domain);
            store = () => { domain.OverrideAdditionalRuaAddress = overrideAddress; domain.AdditionalRuaAddress = normalized; };
        }
        else if (groupId is { } groupKey)
        {
            var group = await db.Groups.SingleAsync(group => group.Id == groupKey, cancellationToken);
            previousOverride = group.OverrideAdditionalRuaAddress;
            previous = group.AdditionalRuaAddress;
            target = AuditTarget.For(group);
            store = () => { group.OverrideAdditionalRuaAddress = overrideAddress; group.AdditionalRuaAddress = normalized; };
        }
        else
        {
            var settings = await DnsRecordSettingsService.GetAsync(db, cancellationToken);
            previousOverride = true;
            overrideAddress = true;
            previous = settings.AdditionalRuaAddress;
            target = AuditTarget.Settings("DNS records");
            store = () => settings.AdditionalRuaAddress = normalized;
        }
        var changes = new AuditChanges().Field("Override secondary DMARC report address", previousOverride, overrideAddress)
            .Field("Secondary DMARC report address", previous, normalized);
        if (!changes.Any) return;
        store();
        AuditLog.Record(db, actor, AuditActions.DnsRecordSettingsSaved, target, "Saved DMARC report destination", changes);
        await db.SaveChangesAsync(cancellationToken);
    }
}
