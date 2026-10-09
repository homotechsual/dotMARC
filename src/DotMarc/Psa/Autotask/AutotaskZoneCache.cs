using System.Collections.Concurrent;

namespace DotMarc.Psa.Autotask;

/// <param name="ApiUrl">The zone's REST root, ending ".../ATServicesRest/V1.0/".</param>
/// <param name="WebUrl">The zone's web app, ending "/", or empty when Autotask didn't say.</param>
public sealed record AutotaskZone(string ApiUrl, string WebUrl);

/// <summary>Which Autotask zone (datacentre) each API username lives in. Looked up once per username and kept for the
/// life of the process; a refused sign-in or Clear cached zone forgets it so the next call looks it up again.</summary>
public sealed class AutotaskZoneCache
{
    private readonly ConcurrentDictionary<string, AutotaskZone> _zones = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string username, out AutotaskZone zone) => _zones.TryGetValue(username, out zone!);

    public void Set(string username, AutotaskZone zone) => _zones[username] = zone;

    public void Forget(string username) => _zones.TryRemove(username, out _);

    /// <summary>Forgets every zone, returning how many there were.</summary>
    public int Clear()
    {
        var count = _zones.Count;
        _zones.Clear();
        return count;
    }
}
