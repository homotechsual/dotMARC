using System.Security.Cryptography;
using System.Text;

namespace DotMarc.DnsPush;

/// <summary>A stable fingerprint of what a push would change, so a plan someone reviewed can be compared with the plan worked out again
/// when the push runs. Two plans have the same fingerprint only when every change matches in every field and in the same order.</summary>
public static class DnsPlanFingerprint
{
    public static string Of(IReadOnlyList<DnsRecordChange> changes)
    {
        var text = new StringBuilder();
        Append(text, changes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var change in changes)
        {
            Append(text, change.Kind.ToString());
            Append(text, change.RecordType);
            Append(text, change.Name);
            Append(text, change.DesiredValue);
            Append(text, change.ExistingValue);
            Append(text, change.ZoneName);
            Append(text, change.ExistingRecordType);

            // Null and empty mean the same thing here: nothing to remove.
            var valuesToRemove = change.ValuesToRemove ?? [];
            Append(text, valuesToRemove.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var value in valuesToRemove)
            {
                Append(text, value);
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Each value is written with its length in front, and a missing one is written differently from an empty one, so two lists
    /// can't produce the same text by splitting the same characters differently between fields.</summary>
    private static void Append(StringBuilder text, string? value)
    {
        if (value is null)
        {
            text.Append("-;");
            return;
        }

        text.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }
}
