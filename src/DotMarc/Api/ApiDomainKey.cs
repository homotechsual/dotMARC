using System.Globalization;
using DotMarc.Data;

namespace DotMarc.Api;

/// <summary>How the API's /domains/{domain} routes find a domain: by its id, or by its name, so a script that knows the
/// name needn't look the id up first. Names are unique, and a domain name can never be all digits (no top-level domain
/// is numeric), so digits always mean an id.</summary>
public static class ApiDomainKey
{
    public const string RouteParameter = "{domain}";

    public const string Description = "Accepts the domain's id or its name, such as contoso.example.";

    public static IQueryable<Domain> Matching(IQueryable<Domain> domains, string key)
    {
        if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return domains.Where(domain => domain.Id == id);
        }

        // The same normalising the app does when a domain is added: case, a trailing dot, international names.
        return DomainNameValidator.TryNormalize(key, out var name)
            ? domains.Where(domain => domain.Name == name)
            : domains.Where(_ => false);
    }
}
