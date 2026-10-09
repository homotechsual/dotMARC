using System.Net.Mail;
using DotMarc.Email;

namespace DotMarc.Reporting.ClientReports;

/// <summary>Report recipient lists, tidied and checked the same way wherever they're entered.</summary>
public static class ReportRecipients
{
    /// <summary>Trimmed, lower-cased, de-duplicated and sorted; throws for an invalid address or too many.</summary>
    public static List<string> Normalise(IEnumerable<string> recipients)
    {
        var tidied = recipients
            .Select(recipient => recipient.Trim().ToLowerInvariant())
            .Where(recipient => recipient.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var recipient in tidied)
        {
            if (recipient.Length > 254 || !MailAddress.TryCreate(recipient, out var parsed) || parsed.Address != recipient)
            {
                throw new ArgumentException($"{recipient} isn't a valid email address.", nameof(recipients));
            }
        }

        if (tidied.Count > EmailLimits.MaximumRecipients)
        {
            throw new ArgumentException($"A report can go to at most {EmailLimits.MaximumRecipients} recipients.", nameof(recipients));
        }

        return tidied;
    }
}
