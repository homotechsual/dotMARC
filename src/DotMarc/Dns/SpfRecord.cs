namespace DotMarc.Dns;

public enum SpfTermKind
{
    Mechanism,
    Modifier,

    /// <summary>Something this parser doesn't recognise. Kept exactly as written.</summary>
    Unknown
}

/// <summary>One space-separated term of an SPF record (RFC 7208): a mechanism such as <c>include:_spf.google.com</c>
/// with its qualifier, a modifier such as <c>redirect=example.com</c>, or anything else kept as written.
/// <see cref="Text"/> is what's written back, so parsing then formatting changes nothing.</summary>
public sealed record SpfTerm(SpfTermKind Kind, char Qualifier, string Name, string Argument, string Text)
{
    private static readonly HashSet<string> MechanismNames = new(StringComparer.OrdinalIgnoreCase) { "all", "include", "a", "mx", "ptr", "ip4", "ip6", "exists" };
    private static readonly HashSet<string> LookupMechanisms = new(StringComparer.OrdinalIgnoreCase) { "include", "a", "mx", "ptr", "exists" };

    public static SpfTerm Parse(string text)
    {
        var body = text;
        var qualifier = '+';
        var explicitQualifier = body.Length > 0 && body[0] is '+' or '-' or '~' or '?';
        if (explicitQualifier)
        {
            qualifier = body[0];
            body = body[1..];
        }

        var equalsIndex = body.IndexOf('=');
        var colonIndex = body.IndexOf(':');
        if (!explicitQualifier && equalsIndex > 0 && (colonIndex < 0 || equalsIndex < colonIndex))
        {
            return new SpfTerm(SpfTermKind.Modifier, '+', body[..equalsIndex], body[(equalsIndex + 1)..], text);
        }

        var nameEnd = body.IndexOfAny([':', '/']);
        var name = nameEnd < 0 ? body : body[..nameEnd];
        var argument = nameEnd < 0 ? "" : body[nameEnd..];
        return MechanismNames.Contains(name)
            ? new SpfTerm(SpfTermKind.Mechanism, qualifier, name, argument, text)
            : new SpfTerm(SpfTermKind.Unknown, qualifier, name, argument, text);
    }

    public static SpfTerm Include(string host) => Parse($"include:{host}");

    public static SpfTerm All(char qualifier) => Parse($"{qualifier}all");

    public bool IsAll => Kind == SpfTermKind.Mechanism && Name.Equals("all", StringComparison.OrdinalIgnoreCase);
    public bool IsInclude => Kind == SpfTermKind.Mechanism && Name.Equals("include", StringComparison.OrdinalIgnoreCase);
    public bool IsRedirect => Kind == SpfTermKind.Modifier && Name.Equals("redirect", StringComparison.OrdinalIgnoreCase);

    /// <summary>RFC 7208 4.6.4: these terms each cost a DNS lookup, and receivers stop at 10.</summary>
    public bool CostsLookup => (Kind == SpfTermKind.Mechanism && LookupMechanisms.Contains(Name)) || IsRedirect;

    /// <summary>The domain an include or redirect points at.</summary>
    public string? Target =>
        IsRedirect ? Argument
        : IsInclude && Argument.StartsWith(':') ? Argument[1..].Split('/')[0]
        : null;

    /// <summary>For an all term: how strictly it treats unlisted senders, so merges keep the strictest.</summary>
    public int Strictness => Qualifier switch { '-' => 3, '~' => 2, '?' => 1, _ => 0 };
}

/// <summary>An SPF record as a list of terms, so an edit changes only what it means to and keeps everything else in
/// order. See docs/superpowers/specs/2026-10-05-spf-dkim-push-design.md.</summary>
public sealed record SpfRecord(IReadOnlyList<SpfTerm> Terms)
{
    private const string Version = "v=spf1";

    public static bool IsSpf(string? txt) =>
        txt is not null
        && txt.StartsWith(Version, StringComparison.OrdinalIgnoreCase)
        && (txt.Length == Version.Length || char.IsWhiteSpace(txt[Version.Length]));

    public static SpfRecord Parse(string txt)
    {
        if (!IsSpf(txt))
        {
            throw new ArgumentException("Not an SPF record: it doesn't start with v=spf1.", nameof(txt));
        }

        return new SpfRecord(txt[Version.Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SpfTerm.Parse)
            .ToList());
    }

    public string Format() => Terms.Count == 0 ? Version : $"{Version} {string.Join(' ', Terms.Select(term => term.Text))}";

    public SpfTerm? AllTerm => Terms.LastOrDefault(term => term.IsAll);

    public IReadOnlyList<string> Includes => Terms.Where(term => term.IsInclude).Select(term => term.Target!).ToList();

    /// <summary><c>v=spf1 -all</c> alone: the domain sends no mail.</summary>
    public bool IsNullRecord => Terms.Count == 1 && Terms[0].IsAll && Terms[0].Qualifier == '-';

    /// <summary>Adds an include before the ending (the all term, or redirect), unless it's already there.</summary>
    public SpfRecord WithInclude(string host)
    {
        if (Includes.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return this;
        }

        var terms = Terms.ToList();
        var endingIndex = terms.FindIndex(term => term.IsAll || term.IsRedirect);
        terms.Insert(endingIndex < 0 ? terms.Count : endingIndex, SpfTerm.Include(host));
        return new SpfRecord(terms);
    }

    public SpfRecord WithoutInclude(string host) =>
        new(Terms.Where(term => !(term.IsInclude && string.Equals(term.Target, host, StringComparison.OrdinalIgnoreCase))).ToList());

    /// <summary>Sets the ending. Any redirect is dropped too: once an all term is present, receivers ignore it.</summary>
    public SpfRecord WithAll(char qualifier) =>
        new(Terms.Where(term => !term.IsAll && !term.IsRedirect).Append(SpfTerm.All(qualifier)).ToList());

    /// <summary>Several SPF records combined into one (RFC 7208 allows only one): their terms in order of appearance
    /// with duplicates removed, and one ending, the strictest all present. A redirect is kept only if no record has
    /// an all term, since an all term makes it ignored.</summary>
    public static SpfRecord Merge(IReadOnlyList<SpfRecord> records)
    {
        var allTerms = records.SelectMany(record => record.Terms).ToList();
        var terms = allTerms
            .Where(term => !term.IsAll && !term.IsRedirect)
            .DistinctBy(term => $"{term.Qualifier}{term.Name.ToLowerInvariant()}{term.Argument.ToLowerInvariant()}")
            .ToList();

        var strictestAll = allTerms.Where(term => term.IsAll).OrderByDescending(term => term.Strictness).FirstOrDefault();
        if (strictestAll is not null)
        {
            terms.Add(strictestAll);
        }
        else if (allTerms.FirstOrDefault(term => term.IsRedirect) is { } redirect)
        {
            terms.Add(redirect);
        }

        return new SpfRecord(terms);
    }
}
