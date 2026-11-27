using System.Text.Json;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Reporting;

/// <summary>What a person has chosen to start the Dashboard with: the view, the Group and Tag filters, and the table's
/// column filters. Free-text searches are left out on purpose, since a saved search would hide domains with no hint why.</summary>
public sealed record DashboardPreferences(
    bool GroupedView = false,
    int? GroupId = null,
    int? TagId = null,
    bool ShowFilters = false,
    string? Status = null,
    double? MinPassRatePercent = null,
    bool? Monitored = null,
    string? Dmarc = null,
    string? MtaSts = null,
    string? Dkim = null);

public static class DashboardPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<DomainListDefaults> GetListsAsync(DotMarcDbContext db, string userKey)
    {
        var json = await db.UserDashboardPreferences.AsNoTracking().Where(row => row.UserKey == userKey)
            .Select(row => row.SettingsJson).SingleOrDefaultAsync().ConfigureAwait(false);
        return json is null ? new() : DomainListDefaults.Parse(json);
    }

    public static async Task SaveListAsync(DotMarcDbContext db, string userKey, bool inventory, DomainListState? state)
    {
        state?.Validate();
        var defaults = await GetListsAsync(db, userKey).ConfigureAwait(false);
        defaults = inventory ? defaults with { Inventory = state?.AsDefault() } : defaults with { Dashboard = state?.AsDefault() };
        var existing = await db.UserDashboardPreferences.SingleOrDefaultAsync(row => row.UserKey == userKey).ConfigureAwait(false);
        if (existing is null)
        {
            existing = new UserDashboardPreference { UserKey = userKey, SettingsJson = "" };
            db.UserDashboardPreferences.Add(existing);
        }
        existing.SettingsJson = JsonSerializer.Serialize(defaults, DomainListDefaults.JsonOptions);
        existing.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>The saved preferences, or null when this person has none (or what is saved can't be read, in which case
    /// they simply start from the defaults).</summary>
    public static async Task<DashboardPreferences?> GetAsync(DotMarcDbContext db, string userKey, CancellationToken cancellationToken = default)
    {
        var json = await db.UserDashboardPreferences.AsNoTracking()
            .Where(p => p.UserKey == userKey)
            .Select(p => p.SettingsJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DashboardPreferences>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task SaveAsync(DotMarcDbContext db, string userKey, DashboardPreferences preferences, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(preferences, JsonOptions);
        var existing = await db.UserDashboardPreferences.SingleOrDefaultAsync(p => p.UserKey == userKey, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.UserDashboardPreferences.Add(new UserDashboardPreference { UserKey = userKey, SettingsJson = json, UpdatedUtc = DateTimeOffset.UtcNow });
        }
        else
        {
            existing.SettingsJson = json;
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task ClearAsync(DotMarcDbContext db, string userKey, CancellationToken cancellationToken = default) =>
        await db.UserDashboardPreferences.Where(p => p.UserKey == userKey).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Who these preferences belong to: the Entra object ID, which survives a rename, else the email.</summary>
    public static string? UserKeyOf(DotMarc.Audit.AuditActor actor)
    {
        var key = actor.ObjectId ?? actor.Email;
        return string.IsNullOrWhiteSpace(key) || key.StartsWith("api-key:", StringComparison.Ordinal)
            ? null
            : key.Length > UserDashboardPreference.MaxUserKeyLength ? key[..UserDashboardPreference.MaxUserKeyLength] : key;
    }
}
