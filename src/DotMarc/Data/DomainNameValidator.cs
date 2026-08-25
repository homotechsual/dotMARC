namespace DotMarc.Data;

/// <summary>Pure validation/normalization for a user-supplied domain name, used when a domain is
/// added for monitoring before any report has arrived for it (see DomainManagementService).
/// Lowercasing here is not cosmetic: PollingService matches an incoming report's domain to a
/// Domain row by exact string equality on Name (PollingService.cs:200), and DMARC aggregate report
/// XML conventionally reports the domain in lowercase — a mixed-case Name stored here would
/// silently fail to match its first real report and produce a duplicate row instead.</summary>
public static class DomainNameValidator
{
    public static bool TryNormalize(string input, out string normalized)
    {
        normalized = input.Trim().ToLowerInvariant();
        return normalized.Length > 0 && normalized.Contains('.') && !normalized.Any(char.IsWhiteSpace);
    }
}
