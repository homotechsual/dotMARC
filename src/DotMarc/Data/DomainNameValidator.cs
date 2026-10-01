using System.Globalization;

namespace DotMarc.Data;

/// <summary>Pure validation/normalization for a user-supplied domain name, used when a domain is
/// added for monitoring before any report has arrived for it (see DomainManagementService).
/// Lowercasing here is not cosmetic: PollingService matches an incoming report's domain to a
/// Domain row by exact string equality on Name (PollingService.cs:200), and DMARC aggregate report
/// XML conventionally reports the domain in lowercase - a mixed-case Name stored here would
/// silently fail to match its first real report and produce a duplicate row instead. Names must be
/// real hostnames, so URLs, email addresses and IP addresses are refused.</summary>
public static class DomainNameValidator
{
    private const int MaximumLength = 253;
    private const int MaximumLabelLength = 63;
    private static readonly IdnMapping Idn = new();

    public static bool TryNormalize(string? input, out string normalized) => TryNormalize(input, out normalized, out _);

    /// <summary>As <see cref="TryNormalize(string?, out string)"/>, with a sentence saying why a name was refused, for
    /// the import preview.</summary>
    public static bool TryNormalize(string? input, out string normalized, out string? reason)
    {
        normalized = "";
        var candidate = input?.Trim() ?? "";

        reason = candidate switch
        {
            "" => "The domain is empty.",
            _ when candidate.Contains("://", StringComparison.Ordinal) => "That's a web address. Use just the domain, for example contoso.com.",
            _ when candidate.Contains('@') => "That's an email address. Use just the domain, for example contoso.com.",
            _ when candidate.Any(char.IsWhiteSpace) => "A domain can't contain spaces.",
            _ => null
        };
        if (reason is not null)
        {
            return false;
        }

        if (candidate.EndsWith('.'))
        {
            candidate = candidate[..^1];
        }

        var rawLabels = candidate.Split('.');
        if (rawLabels.Length < 2)
        {
            reason = "A domain needs at least two parts, for example contoso.com.";
            return false;
        }

        if (rawLabels.Any(label => label.Length == 0))
        {
            reason = "A domain can't have an empty part between dots.";
            return false;
        }

        string ascii;
        try
        {
            // Unicode names are stored in their xn-- form, as DMARC reports carry them.
            ascii = Idn.GetAscii(candidate).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            reason = "That isn't a valid domain name.";
            return false;
        }

        if (ascii.Length > MaximumLength)
        {
            reason = $"A domain can be at most {MaximumLength} characters.";
            return false;
        }

        var labels = ascii.Split('.');
        foreach (var label in labels)
        {
            if (label.Length > MaximumLabelLength)
            {
                reason = $"Each part between the dots can be at most {MaximumLabelLength} characters.";
                return false;
            }

            if (!label.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                reason = "A domain can only contain letters, digits, hyphens and dots.";
                return false;
            }

            if (label.StartsWith('-') || label.EndsWith('-'))
            {
                reason = "A part of a domain can't start or end with a hyphen.";
                return false;
            }
        }

        if (labels[^1].All(char.IsAsciiDigit))
        {
            reason = "That's an IP address, not a domain.";
            return false;
        }

        normalized = ascii;
        return true;
    }
}
