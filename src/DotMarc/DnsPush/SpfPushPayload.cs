using System.Text.Json;

namespace DotMarc.DnsPush;

/// <summary>What the SPF editor hands the push: the record to publish and the fingerprint of the live record(s) it
/// was built from. It travels in the encrypted push state.</summary>
public sealed record SpfPushPayload(string Proposed, string Fingerprint)
{
    public string Serialize() => JsonSerializer.Serialize(this);

    public static SpfPushPayload? TryParse(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SpfPushPayload>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
