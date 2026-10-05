# SPF and DKIM Push Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let people edit a domain's SPF record and publish its DKIM records from dotMARC, pushed through the existing Cloudflare, Azure DNS and Google Cloud DNS integration, with SPF lookup counting in both the editor and the daily check.

**Architecture:** Pure SPF code (a parser, an editor model and a lookup counter over an `ITxtRecordLookup`) feeds a new SPF check status and an SPF editor dialog. DKIM records are stored per selector, compared by the DKIM check and pushed. The push callback's per-target if/else becomes one `IDnsChangeBuilder` per target; SPF's edited record rides in the encrypted push state, and a new "replace values in a TXT set" change kind lets each provider change only the SPF value at the apex.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor 9.8, EF Core 10 + Npgsql, xUnit with the Testcontainers Postgres fixture, Azure.ResourceManager.Dns, dotnet-ef 10.0.10 (local tool).

**Spec:** `docs/superpowers/specs/2026-10-05-spf-dkim-push-design.md`

## Global Constraints

- SPF lookup limit 10; counting stops once past 20; includes followed at most 10 levels deep; more than 2 lookups that find nothing is a problem.
- TXT values are split into strings of at most 255 characters when written to a provider.
- The editor warns above 450 characters.
- A push payload longer than 4000 characters is refused at `/dns-push/{provider}/start`.
- `spf` and `dkim` pushes need `DomainsEdit`; the DNS record settings live on `/dns-push/settings` (`DnsPushManage`).
- New audit actions: `settings.dns_records.saved` ("DNS record settings saved"), `domain.dkim_records_changed` ("Domain DKIM records changed").
- New popup flags: `spf-changed`, `spf-too-many-lookups`, `nothing-to-push`.
- At the domain apex only SPF values change; every other TXT value there is kept.
- The four existing push targets (`mta-sts`, `dmarc`, `dmarc-auth`, `tlsrpt`) keep their exact behaviour.
- User-facing text (UI, check details, docs) uses no em dashes. Meaningful variable names, comments explain why, `ConfigureAwait(false)` in library code.

## Review Focus

1. **Other TXT values at the apex** (site verifications) must survive an SPF push on every provider. Pinned in Task 6 (`ReplaceTxtValues_KeepsOtherValuesAtTheName` for Cloudflare and Google, and `TxtValuesTests` for the shared logic Azure uses).
2. **SPF edited in the provider's console between opening the editor and pushing** must be refused with nothing changed. Pinned in Task 8 (`SpfChangeBuilder_RefusesWhenTheLiveRecordChanged`).
3. **A record over 10 lookups** must not be pushable, except to lower an already over-limit record. Pinned in Task 8 (`SpfEditorTests.BlockReason_*`, `SpfChangeBuilder_RefusesARecordOverTheLimit`).
4. **DKIM values pasted in console formats** (quoted chunks, line breaks, a trailing dot) must store cleanly and compare equal to what DNS returns. Pinned in Task 4 (`DkimRecordValueTests`) and Task 5 (`CheckAsync_TxtExpected_MatchesDespiteWhitespace`).
5. **A slow or failing DNS lookup during the SPF check** must never invent a failure. Pinned in Task 3 (`CheckAsync_StaysOk_WhenCountingLookupsFails`).

---

### Task 1: Reading and editing SPF records

**Files:**
- Create: `src/DotMarc/Dns/SpfRecord.cs`
- Test: `test/DotMarc.Tests/Dns/SpfRecordTests.cs`

**Interfaces:**
- Produces: `enum SpfTermKind { Mechanism, Modifier, Unknown }`; `sealed record SpfTerm(SpfTermKind Kind, char Qualifier, string Name, string Argument, string Text)` with `static SpfTerm Parse(string text)`, `static SpfTerm Include(string host)`, `static SpfTerm All(char qualifier)`, `bool IsAll`, `bool IsInclude`, `bool IsRedirect`, `bool CostsLookup`, `string? Target`, `int Strictness`; `sealed record SpfRecord(IReadOnlyList<SpfTerm> Terms)` with `static bool IsSpf(string? txt)`, `static SpfRecord Parse(string txt)`, `string Format()`, `SpfTerm? AllTerm`, `IReadOnlyList<string> Includes`, `bool IsNullRecord`, `SpfRecord WithInclude(string host)`, `SpfRecord WithoutInclude(string host)`, `SpfRecord WithAll(char qualifier)`, `static SpfRecord Merge(IReadOnlyList<SpfRecord> records)`. All in `DotMarc.Dns`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Dns/SpfRecordTests.cs`:

```csharp
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class SpfRecordTests
{
    [Theory]
    [InlineData("v=spf1", true)]
    [InlineData("v=spf1 -all", true)]
    [InlineData("V=SPF1 ~all", true)]
    [InlineData("v=spf10 -all", false)]
    [InlineData("v=spf2.0/pra -all", false)]
    [InlineData("google-site-verification=abc", false)]
    [InlineData(null, false)]
    public void IsSpf_RecognisesOnlyVersion1Records(string? txt, bool expected)
    {
        Assert.Equal(expected, SpfRecord.IsSpf(txt));
    }

    [Fact]
    public void Parse_ReadsTermsInOrder_WithQualifiersNamesAndArguments()
    {
        var record = SpfRecord.Parse("v=spf1 ip4:192.0.2.0/24 include:_spf.google.com -include:bad.example a mx/24 ~all");

        Assert.Equal(
            [("ip4", '+', ":192.0.2.0/24"), ("include", '+', ":_spf.google.com"), ("include", '-', ":bad.example"), ("a", '+', ""), ("mx", '+', "/24"), ("all", '~', "")],
            record.Terms.Select(term => (term.Name, term.Qualifier, term.Argument)));
        Assert.All(record.Terms, term => Assert.Equal(SpfTermKind.Mechanism, term.Kind));
    }

    [Fact]
    public void Parse_KeepsModifiersAndUnknownTermsExactly()
    {
        var record = SpfRecord.Parse("v=spf1 redirect=_spf.example.com exp=explain.example.com foo:bar");

        Assert.Equal([SpfTermKind.Modifier, SpfTermKind.Modifier, SpfTermKind.Unknown], record.Terms.Select(term => term.Kind));
        Assert.True(record.Terms[0].IsRedirect);
        Assert.Equal("_spf.example.com", record.Terms[0].Target);
        Assert.Equal("v=spf1 redirect=_spf.example.com exp=explain.example.com foo:bar", record.Format());
    }

    [Fact]
    public void Format_UsesSingleSpaces()
    {
        Assert.Equal("v=spf1 include:a.example ~all", SpfRecord.Parse("v=spf1  include:a.example   ~all").Format());
    }

    [Fact]
    public void MechanismNames_IgnoreCase()
    {
        var record = SpfRecord.Parse("v=spf1 INCLUDE:a.example ~ALL");

        Assert.Equal(["a.example"], record.Includes);
        Assert.Equal('~', record.AllTerm!.Qualifier);
    }

    [Theory]
    [InlineData("include:a.example", true)]
    [InlineData("a", true)]
    [InlineData("mx/24", true)]
    [InlineData("ptr", true)]
    [InlineData("exists:%{i}.example", true)]
    [InlineData("redirect=a.example", true)]
    [InlineData("ip4:192.0.2.1", false)]
    [InlineData("ip6:2001:db8::1", false)]
    [InlineData("-all", false)]
    [InlineData("exp=explain.example", false)]
    public void CostsLookup_FollowsRfc7208(string text, bool expected)
    {
        Assert.Equal(expected, SpfTerm.Parse(text).CostsLookup);
    }

    [Theory]
    [InlineData("v=spf1 ip4:192.0.2.1 ~all", "v=spf1 ip4:192.0.2.1 include:new.example ~all")]
    [InlineData("v=spf1 include:a.example redirect=b.example", "v=spf1 include:a.example include:new.example redirect=b.example")]
    [InlineData("v=spf1 ip4:192.0.2.1", "v=spf1 ip4:192.0.2.1 include:new.example")]
    [InlineData("v=spf1 include:NEW.example ~all", "v=spf1 include:NEW.example ~all")]
    public void WithInclude_AddsBeforeTheEnding_AndNeverTwice(string record, string expected)
    {
        Assert.Equal(expected, SpfRecord.Parse(record).WithInclude("new.example").Format());
    }

    [Fact]
    public void WithoutInclude_RemovesOnlyThatInclude()
    {
        Assert.Equal("v=spf1 include:keep.example ~all",
            SpfRecord.Parse("v=spf1 include:keep.example include:OLD.example ~all").WithoutInclude("old.example").Format());
    }

    [Theory]
    [InlineData("v=spf1 a +all", '~', "v=spf1 a ~all")]
    [InlineData("v=spf1 include:a.example redirect=b.example", '-', "v=spf1 include:a.example -all")]
    [InlineData("v=spf1 include:a.example", '~', "v=spf1 include:a.example ~all")]
    public void WithAll_SetsTheEnding_AndDropsRedirect(string record, char qualifier, string expected)
    {
        Assert.Equal(expected, SpfRecord.Parse(record).WithAll(qualifier).Format());
    }

    [Theory]
    [InlineData(new[] { "v=spf1 include:a.example ~all", "v=spf1 include:b.example include:a.example -all" }, "v=spf1 include:a.example include:b.example -all")]
    [InlineData(new[] { "v=spf1 ip4:192.0.2.1", "v=spf1 redirect=x.example" }, "v=spf1 ip4:192.0.2.1 redirect=x.example")]
    [InlineData(new[] { "v=spf1 redirect=x.example", "v=spf1 ?all" }, "v=spf1 ?all")]
    [InlineData(new[] { "v=spf1 +all", "v=spf1 ~all" }, "v=spf1 ~all")]
    public void Merge_CombinesTermsInOrder_WithTheStrictestEnding(string[] records, string expected)
    {
        Assert.Equal(expected, SpfRecord.Merge(records.Select(SpfRecord.Parse).ToList()).Format());
    }

    [Theory]
    [InlineData("v=spf1 -all", true)]
    [InlineData("v=spf1 ~all", false)]
    [InlineData("v=spf1 a -all", false)]
    public void IsNullRecord_IsMinusAllAlone(string record, bool expected)
    {
        Assert.Equal(expected, SpfRecord.Parse(record).IsNullRecord);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SpfRecordTests" -nologo -v q`
Expected: build FAILS, `SpfRecord` not found.

- [ ] **Step 3: Write the record model**

Create `src/DotMarc/Dns/SpfRecord.cs`:

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SpfRecordTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/DotMarc/Dns/SpfRecord.cs test/DotMarc.Tests/Dns/SpfRecordTests.cs
git commit -m "Read and edit SPF records term by term"
```

---

### Task 2: Looking up TXT records and counting SPF lookups

**Files:**
- Create: `src/DotMarc/Dns/ITxtRecordLookup.cs`, `src/DotMarc/Dns/TxtRecordLookup.cs`, `src/DotMarc/Dns/CachingTxtRecordLookup.cs`, `src/DotMarc/Dns/SpfLookupCounter.cs`
- Create: `test/DotMarc.Tests/Internal/FakeTxtRecordLookup.cs`
- Modify: `src/DotMarc/Program.cs` (registrations)
- Test: `test/DotMarc.Tests/Dns/TxtRecordLookupTests.cs`, `test/DotMarc.Tests/Dns/SpfLookupCounterTests.cs`

**Interfaces:**
- Consumes: Task 1's `SpfRecord`, `SpfTerm`; `DnsRecordLookupResult` and `DnsRecordLookupParsing` (`DotMarc.DnsPush`).
- Produces: `interface ITxtRecordLookup { Task<IReadOnlyList<string>> GetTxtValuesAsync(string name, CancellationToken cancellationToken); Task<DnsRecordLookupResult> LookupWithCnameAsync(string name, CancellationToken cancellationToken); }`; `TxtRecordLookup(HttpClient)`; `CachingTxtRecordLookup(ITxtRecordLookup inner)`; `sealed record SpfTermCost(string Term, int Lookups)`; `sealed record SpfLookupCount(int Total, IReadOnlyList<SpfTermCost> TermCosts, IReadOnlyList<string> MissingTargets, IReadOnlyList<string> Problems)` with `const int Limit = 10` and `bool IsOverLimit`; `SpfLookupCounter(ITxtRecordLookup)` with `Task<SpfLookupCount> CountAsync(string domainName, SpfRecord record, CancellationToken cancellationToken)`; test fake `FakeTxtRecordLookup` with `Dictionary<string, List<string>> TxtByName`, `Dictionary<string, DnsRecordLookupResult> LookupsByName`, `List<string> Queried`, `Exception? ThrowOnQuery`.

- [ ] **Step 1: Write the fake and the failing tests**

Create `test/DotMarc.Tests/Internal/FakeTxtRecordLookup.cs`:

```csharp
using DotMarc.Dns;
using DotMarc.DnsPush;

namespace DotMarc.Tests.Internal;

internal sealed class FakeTxtRecordLookup : ITxtRecordLookup
{
    public Dictionary<string, List<string>> TxtByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DnsRecordLookupResult> LookupsByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Queried { get; } = [];
    public Exception? ThrowOnQuery { get; set; }

    public Task<IReadOnlyList<string>> GetTxtValuesAsync(string name, CancellationToken cancellationToken)
    {
        Queried.Add(name);
        if (ThrowOnQuery is not null)
        {
            throw ThrowOnQuery;
        }

        return Task.FromResult<IReadOnlyList<string>>(TxtByName.TryGetValue(name, out var values) ? values : []);
    }

    public Task<DnsRecordLookupResult> LookupWithCnameAsync(string name, CancellationToken cancellationToken)
    {
        Queried.Add(name);
        if (ThrowOnQuery is not null)
        {
            throw ThrowOnQuery;
        }

        return Task.FromResult(LookupsByName.TryGetValue(name, out var result) ? result : new DnsRecordLookupResult(null, null));
    }
}
```

Create `test/DotMarc.Tests/Dns/TxtRecordLookupTests.cs`:

```csharp
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class TxtRecordLookupTests
{
    private static (TxtRecordLookup Lookup, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        return (new TxtRecordLookup(new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") }), handler);
    }

    [Fact]
    public async Task GetTxtValues_ReturnsEveryTxtValue_WithChunksJoined()
    {
        var (lookup, handler) = Create();
        handler.ResponseBody = """
            {"Status":0,"Answer":[
              {"type":16,"data":"\"google-site-verification=abc\""},
              {"type":16,"data":"\"v=spf1 include:_spf.google.com \" \"~all\""}
            ]}
            """;

        var values = await lookup.GetTxtValuesAsync("contoso.com", CancellationToken.None);

        Assert.Equal(["google-site-verification=abc", "v=spf1 include:_spf.google.com ~all"], values);
        Assert.Contains("name=contoso.com", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task GetTxtValues_IsEmpty_ForNxDomain()
    {
        var (lookup, handler) = Create();
        handler.ResponseBody = """{"Status":3}""";

        Assert.Empty(await lookup.GetTxtValuesAsync("nothing.example", CancellationToken.None));
    }

    [Fact]
    public async Task LookupWithCname_NotesTheCnameHop()
    {
        var (lookup, handler) = Create();
        handler.ResponseBody = """
            {"Status":0,"Answer":[
              {"type":5,"data":"selector1-contoso-com._domainkey.contoso.onmicrosoft.com."},
              {"type":16,"data":"\"v=DKIM1; k=rsa; p=MIIBIjAN\""}
            ]}
            """;

        var result = await lookup.LookupWithCnameAsync("selector1._domainkey.contoso.com", CancellationToken.None);

        Assert.Equal("selector1-contoso-com._domainkey.contoso.onmicrosoft.com.", result.DelegatedToCname);
        Assert.Equal("v=DKIM1; k=rsa; p=MIIBIjAN", result.DirectValue);
    }

    [Fact]
    public async Task CachingLookup_AsksEachNameOnce()
    {
        var inner = new FakeTxtRecordLookup();
        inner.TxtByName["a.example"] = ["v=spf1 -all"];
        var caching = new CachingTxtRecordLookup(inner);

        await caching.GetTxtValuesAsync("a.example", CancellationToken.None);
        await caching.GetTxtValuesAsync("A.example", CancellationToken.None);

        Assert.Single(inner.Queried);
    }
}
```

Create `test/DotMarc.Tests/Dns/SpfLookupCounterTests.cs`:

```csharp
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class SpfLookupCounterTests
{
    private readonly FakeTxtRecordLookup _lookup = new();

    private Task<SpfLookupCount> CountAsync(string record) =>
        new SpfLookupCounter(_lookup).CountAsync("contoso.com", SpfRecord.Parse(record), CancellationToken.None);

    [Fact]
    public async Task EachLookupTermCostsOne_AndIpAndAllCostNothing()
    {
        var count = await CountAsync("v=spf1 a mx ptr exists:x.example ip4:192.0.2.1 ip6:2001:db8::1 ~all");

        Assert.Equal(4, count.Total);
        Assert.Empty(count.Problems);
    }

    [Fact]
    public async Task Includes_AreFollowed_AndTheirCostAdded()
    {
        _lookup.TxtByName["a.example"] = ["v=spf1 include:b.example ip4:192.0.2.1 ~all"];
        _lookup.TxtByName["b.example"] = ["v=spf1 a mx ~all"];

        var count = await CountAsync("v=spf1 include:a.example mx ~all");

        Assert.Equal(5, count.Total);
        Assert.Equal([new SpfTermCost("include:a.example", 4), new SpfTermCost("mx", 1)], count.TermCosts);
    }

    [Fact]
    public async Task Redirect_IsFollowedToo()
    {
        _lookup.TxtByName["_spf.example.com"] = ["v=spf1 a mx -all"];

        Assert.Equal(3, (await CountAsync("v=spf1 redirect=_spf.example.com")).Total);
    }

    [Fact]
    public async Task AnIncludeWithNoSpfRecord_IsReportedAsMissing()
    {
        _lookup.TxtByName["gone.example"] = ["some other text"];

        var count = await CountAsync("v=spf1 include:gone.example ~all");

        Assert.Equal(1, count.Total);
        Assert.Equal(["gone.example"], count.MissingTargets);
        Assert.Contains(count.Problems, problem => problem.Contains("gone.example has no SPF record"));
    }

    [Fact]
    public async Task MoreThanTwoLookupsThatFindNothing_IsAProblem()
    {
        var count = await CountAsync("v=spf1 include:a.example include:b.example include:c.example ~all");

        Assert.Contains(count.Problems, problem => problem.Contains("3 lookups found nothing"));
    }

    [Fact]
    public async Task ALoop_IsReported_AndCountingStillFinishes()
    {
        _lookup.TxtByName["a.example"] = ["v=spf1 include:b.example ~all"];
        _lookup.TxtByName["b.example"] = ["v=spf1 include:a.example ~all"];

        var count = await CountAsync("v=spf1 include:a.example ~all");

        Assert.Equal(3, count.Total);
        Assert.Contains(count.Problems, problem => problem.Contains("loops"));
    }

    [Fact]
    public async Task TheSameIncludeTwiceInDifferentBranches_CountsTwice_AndIsNotALoop()
    {
        _lookup.TxtByName["a.example"] = ["v=spf1 include:shared.example ~all"];
        _lookup.TxtByName["b.example"] = ["v=spf1 include:shared.example ~all"];
        _lookup.TxtByName["shared.example"] = ["v=spf1 a ~all"];

        var count = await CountAsync("v=spf1 include:a.example include:b.example ~all");

        Assert.Equal(6, count.Total);
        Assert.Empty(count.Problems);
    }

    [Fact]
    public async Task NestingDeeperThanTenLevels_StopsWithAProblem()
    {
        for (var level = 1; level <= 12; level++)
        {
            _lookup.TxtByName[$"level{level}.example"] = [$"v=spf1 include:level{level + 1}.example ~all"];
        }

        var count = await CountAsync("v=spf1 include:level1.example ~all");

        Assert.Contains(count.Problems, problem => problem.Contains("more than 10 levels"));
        Assert.True(count.IsOverLimit);
    }

    [Fact]
    public async Task CountingStopsOncePastTwenty()
    {
        var count = await CountAsync("v=spf1 " + string.Join(' ', Enumerable.Range(1, 30).Select(number => $"exists:{number}.example")) + " ~all");

        Assert.Equal(21, count.Total);
        Assert.True(count.IsOverLimit);
    }

    [Fact]
    public async Task AMacro_IsCountedButNotFollowed()
    {
        var count = await CountAsync("v=spf1 include:%{d}.example ~all");

        Assert.Equal(1, count.Total);
        Assert.Contains(count.Problems, problem => problem.Contains("macro"));
        Assert.DoesNotContain("%{d}.example", _lookup.Queried);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~TxtRecordLookupTests|FullyQualifiedName~SpfLookupCounterTests" -nologo -v q`
Expected: build FAILS, `ITxtRecordLookup`, `TxtRecordLookup`, `CachingTxtRecordLookup` and `SpfLookupCounter` not found.

- [ ] **Step 3: Write the lookups**

Create `src/DotMarc/Dns/ITxtRecordLookup.cs`:

```csharp
using DotMarc.DnsPush;

namespace DotMarc.Dns;

/// <summary>TXT lookups over the same DNS-over-HTTPS resolver the checks use, for the SPF lookup counter, the SPF
/// editor and the SPF and DKIM pushes.</summary>
public interface ITxtRecordLookup
{
    /// <summary>Every TXT value at the name, each with its quoted strings joined. Empty when there are none.</summary>
    Task<IReadOnlyList<string>> GetTxtValuesAsync(string name, CancellationToken cancellationToken);

    /// <summary>The TXT value at the name, noting when the name is a CNAME to somewhere else.</summary>
    Task<DnsRecordLookupResult> LookupWithCnameAsync(string name, CancellationToken cancellationToken);
}
```

Create `src/DotMarc/Dns/TxtRecordLookup.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.DnsPush;

namespace DotMarc.Dns;

public sealed class TxtRecordLookup : ITxtRecordLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public TxtRecordLookup(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<string>> GetTxtValuesAsync(string name, CancellationToken cancellationToken)
    {
        var answers = await QueryAsync(name, cancellationToken).ConfigureAwait(false);
        return answers
            .Where(answer => answer.Type == 16)
            .Select(answer => string.Join("", answer.Data.Split("\" \"")).Trim('"'))
            .ToList();
    }

    public async Task<DnsRecordLookupResult> LookupWithCnameAsync(string name, CancellationToken cancellationToken)
    {
        var answers = await QueryAsync(name, cancellationToken).ConfigureAwait(false);
        return DnsRecordLookupParsing.ParseTxtWithCnameDetection(answers.Select(answer => (answer.Type, answer.Data)));
    }

    private async Task<List<DnsAnswer>> QueryAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)?.Answer ?? [];
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
```

Create `src/DotMarc/Dns/CachingTxtRecordLookup.cs`:

```csharp
using System.Collections.Concurrent;
using DotMarc.DnsPush;

namespace DotMarc.Dns;

/// <summary>Remembers each name's TXT values for its own lifetime, so the SPF editor can recount after every edit
/// without asking DNS again for includes it has already followed.</summary>
public sealed class CachingTxtRecordLookup(ITxtRecordLookup inner) : ITxtRecordLookup
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> GetTxtValuesAsync(string name, CancellationToken cancellationToken)
    {
        if (_values.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var values = await inner.GetTxtValuesAsync(name, cancellationToken).ConfigureAwait(false);
        _values[name] = values;
        return values;
    }

    public Task<DnsRecordLookupResult> LookupWithCnameAsync(string name, CancellationToken cancellationToken) =>
        inner.LookupWithCnameAsync(name, cancellationToken);
}
```

- [ ] **Step 4: Write the counter**

Create `src/DotMarc/Dns/SpfLookupCounter.cs`:

```csharp
namespace DotMarc.Dns;

public sealed record SpfTermCost(string Term, int Lookups);

/// <summary>How many DNS lookups an SPF record needs. Receivers give up past <see cref="Limit"/>, and SPF then fails
/// for every message.</summary>
public sealed record SpfLookupCount(int Total, IReadOnlyList<SpfTermCost> TermCosts, IReadOnlyList<string> MissingTargets, IReadOnlyList<string> Problems)
{
    public const int Limit = 10;

    public bool IsOverLimit => Total > Limit;
}

/// <summary>Counts an SPF record's DNS lookups as RFC 7208 4.6.4 does: each include, a, mx, ptr, exists and redirect
/// costs one, and include and redirect are followed into their target's record.</summary>
public sealed class SpfLookupCounter(ITxtRecordLookup lookup)
{
    /// <summary>Past this the exact figure no longer matters, and a huge record shouldn't cost hundreds of queries.</summary>
    private const int StopCountingAt = 20;
    private const int MaxDepth = 10;
    private const int MaxVoidLookups = 2;

    public async Task<SpfLookupCount> CountAsync(string domainName, SpfRecord record, CancellationToken cancellationToken)
    {
        var state = new CountState();
        state.Path.Add(domainName.TrimEnd('.').ToLowerInvariant());
        var termCosts = new List<SpfTermCost>();
        foreach (var term in record.Terms.Where(term => term.CostsLookup))
        {
            var before = state.Total;
            await CountTermAsync(term, state, depth: 1, cancellationToken).ConfigureAwait(false);
            termCosts.Add(new SpfTermCost(term.Text, state.Total - before));
            if (state.Total > StopCountingAt)
            {
                break;
            }
        }

        if (state.VoidLookups > MaxVoidLookups)
        {
            state.Problems.Add($"{state.VoidLookups} lookups found nothing; receivers allow at most {MaxVoidLookups}.");
        }

        return new SpfLookupCount(state.Total, termCosts, state.MissingTargets, state.Problems);
    }

    private async Task CountTermAsync(SpfTerm term, CountState state, int depth, CancellationToken cancellationToken)
    {
        state.Total++;
        if (term.Target is not { } target || state.Total > StopCountingAt)
        {
            return;
        }

        if (target.Contains('%'))
        {
            state.Problems.Add($"{term.Text} uses a macro, so it wasn't followed.");
            return;
        }

        if (depth > MaxDepth)
        {
            state.Problems.Add($"{term.Text} is nested more than {MaxDepth} levels deep, so counting stopped there.");
            // Too deep to finish counting is as bad as too many: receivers give up too.
            state.Total = Math.Max(state.Total, SpfLookupCount.Limit + 1);
            return;
        }

        var key = target.TrimEnd('.').ToLowerInvariant();
        if (state.Path.Contains(key))
        {
            state.Problems.Add($"{term.Text} loops back to a domain it's already inside.");
            return;
        }

        var records = (await lookup.GetTxtValuesAsync(target, cancellationToken).ConfigureAwait(false)).Where(SpfRecord.IsSpf).ToList();
        if (records.Count == 0)
        {
            state.VoidLookups++;
            state.MissingTargets.Add(target);
            state.Problems.Add($"{target} has no SPF record.");
            return;
        }

        if (records.Count > 1)
        {
            state.Problems.Add($"{target} has {records.Count} SPF records, so receivers treat it as an error.");
        }

        // The path, not every domain seen so far: the same include in two branches is fine and costs twice.
        state.Path.Add(key);
        foreach (var inner in SpfRecord.Parse(records[0]).Terms.Where(innerTerm => innerTerm.CostsLookup))
        {
            await CountTermAsync(inner, state, depth + 1, cancellationToken).ConfigureAwait(false);
            if (state.Total > StopCountingAt)
            {
                break;
            }
        }

        state.Path.Remove(key);
    }

    private sealed class CountState
    {
        public int Total { get; set; }
        public int VoidLookups { get; set; }
        public HashSet<string> Path { get; } = [];
        public List<string> MissingTargets { get; } = [];
        public List<string> Problems { get; } = [];
    }
}
```

In `src/DotMarc/Program.cs`, after the `IMailServiceDetector` registration, add:

```csharp
builder.Services.AddHttpClient<DotMarc.Dns.ITxtRecordLookup, DotMarc.Dns.TxtRecordLookup>(client =>
{
    client.BaseAddress = new Uri("https://cloudflare-dns.com/");
    client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
});
builder.Services.AddTransient<DotMarc.Dns.SpfLookupCounter>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~TxtRecordLookupTests|FullyQualifiedName~SpfLookupCounterTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/DotMarc/Dns src/DotMarc/Program.cs test/DotMarc.Tests
git commit -m "Count an SPF record's DNS lookups by following its includes"
```

---

### Task 3: The SPF check counts lookups

**Files:**
- Modify: `src/DotMarc/Data/SpfCheckStatus.cs`, `src/DotMarc/Dns/SpfDnsChecker.cs`, `src/DotMarc/Reporting/SpfStatusPresentation.cs`
- Test: `test/DotMarc.Tests/Dns/SpfDnsCheckerTests.cs`, `test/DotMarc.Tests/Notifications/DnsHealthAlertEvaluatorTests.cs`

**Interfaces:**
- Consumes: Task 2's `ITxtRecordLookup`, `TxtRecordLookup`, `SpfLookupCounter`; Task 1's `SpfRecord`.
- Produces: `SpfCheckStatus.TooManyLookups`; `SpfDnsChecker(HttpClient http, ITxtRecordLookup includeLookup)` (the existing one-argument constructor stays).

- [ ] **Step 1: Write the failing tests**

Add to `SpfDnsCheckerTests`:

```csharp
    private static (SpfDnsChecker Checker, FakeHttpMessageHandler Handler, FakeTxtRecordLookup Includes) CreateCountingChecker(string apexRecord)
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseBody = $$"""{"Status":0,"Answer":[{"type":16,"data":"\"{{apexRecord}}\""}]}"""
        };
        var includes = new FakeTxtRecordLookup();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new SpfDnsChecker(http, includes), handler, includes);
    }

    [Fact]
    public async Task CheckAsync_ReturnsTooManyLookups_PastTen()
    {
        var (checker, _, includes) = CreateCountingChecker("v=spf1 include:big.example a mx ~all");
        includes.TxtByName["big.example"] = ["v=spf1 " + string.Join(' ', Enumerable.Range(1, 9).Select(number => $"exists:{number}.example")) + " ~all"];

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.TooManyLookups, result.Status);
        Assert.Contains("12 DNS lookups", result.Detail);
        Assert.Contains("include:big.example (10)", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_ReturnsMisconfigured_WhenAnIncludeHasNoSpfRecord()
    {
        var (checker, _, _) = CreateCountingChecker("v=spf1 include:gone.example ~all");

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.Misconfigured, result.Status);
        Assert.Contains("gone.example", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_StaysOk_WhenCountingLookupsFails()
    {
        var (checker, _, includes) = CreateCountingChecker("v=spf1 include:a.example ~all");
        includes.ThrowOnQuery = new HttpRequestException("DNS is down");

        var result = await checker.CheckAsync("contoso.io", CancellationToken.None);

        Assert.Equal(SpfCheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReturnsOk_WithinTheLimit()
    {
        var (checker, _, includes) = CreateCountingChecker("v=spf1 include:_spf.google.com ~all");
        includes.TxtByName["_spf.google.com"] = ["v=spf1 include:_netblocks.google.com ~all"];
        includes.TxtByName["_netblocks.google.com"] = ["v=spf1 ip4:35.190.247.0/24 ~all"];

        Assert.Equal(SpfCheckStatus.Ok, (await checker.CheckAsync("contoso.io", CancellationToken.None)).Status);
    }
```

In `DnsHealthAlertEvaluatorTests.EachStatus_IsPassingFailingOrIgnored`, add a row:

```csharp
    [InlineData(DnsHealthItems.Spf, "TooManyLookups", DnsCheckHealth.Failing)]
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SpfDnsCheckerTests|FullyQualifiedName~DnsHealthAlertEvaluatorTests" -nologo -v q`
Expected: build FAILS, no two-argument `SpfDnsChecker` constructor and no `TooManyLookups`.

- [ ] **Step 3: Add the status and count lookups in the check**

In `src/DotMarc/Data/SpfCheckStatus.cs`, add `TooManyLookups` as the last value of the enum (statuses are stored as strings, so the position doesn't matter to stored data).

In `SpfDnsChecker`, replace the field and constructor with:

```csharp
    private readonly HttpClient _http;
    private readonly SpfLookupCounter _lookupCounter;

    public SpfDnsChecker(HttpClient http) : this(http, new TxtRecordLookup(http))
    {
    }

    public SpfDnsChecker(HttpClient http, ITxtRecordLookup includeLookup)
    {
        _http = http;
        _lookupCounter = new SpfLookupCounter(includeLookup);
    }
```

and replace the final `return new SpfCheckResult(SpfCheckStatus.Ok, null);` of `CheckAsync` with:

```csharp
        SpfLookupCount count;
        try
        {
            count = await _lookupCounter.CountAsync(domainName, SpfRecord.Parse(spfRecords[0]), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            // A lookup that fails is a resolver problem, not the domain's: leave the check as it would have been.
            return new SpfCheckResult(SpfCheckStatus.Ok, null);
        }

        if (count.IsOverLimit)
        {
            var costliest = count.TermCosts.OrderByDescending(cost => cost.Lookups).Take(3).Select(cost => $"{cost.Term} ({cost.Lookups})");
            return new SpfCheckResult(SpfCheckStatus.TooManyLookups,
                $"{domainName}'s SPF record needs {count.Total}{(count.Total > 20 ? " or more" : "")} DNS lookups. Receivers stop at {SpfLookupCount.Limit}, so SPF fails. Costliest: {string.Join(", ", costliest)}.");
        }

        if (count.MissingTargets.Count > 0)
        {
            return new SpfCheckResult(SpfCheckStatus.Misconfigured,
                $"{domainName}'s SPF record points at {string.Join(", ", count.MissingTargets)}, which {(count.MissingTargets.Count == 1 ? "has" : "have")} no SPF record.");
        }

        return new SpfCheckResult(SpfCheckStatus.Ok, null);
```

In `SpfStatusPresentation`, add `SpfCheckStatus.TooManyLookups` to the `Color.Error` arm of `GetColor`, and `SpfCheckStatus.TooManyLookups => "Too many DNS lookups",` to `GetLabel`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SpfDnsCheckerTests|FullyQualifiedName~DnsHealthAlertEvaluatorTests|FullyQualifiedName~SpfCheckCycleTests" -nologo -v q`
Expected: PASS. (The existing SPF checker tests serve the same record for every query, so an include of themselves is a loop: reported, not a status change.)

- [ ] **Step 5: Commit**

```powershell
git add src/DotMarc/Data/SpfCheckStatus.cs src/DotMarc/Dns/SpfDnsChecker.cs src/DotMarc/Reporting/SpfStatusPresentation.cs test/DotMarc.Tests
git commit -m "Flag SPF records that need more than 10 DNS lookups or include nothing"
```

---

### Task 4: DNS record settings, stored DKIM records and the migration

**Files:**
- Create: `src/DotMarc/Notifications/DnsRecordSettings.cs`, `src/DotMarc/Notifications/DnsRecordSettingsService.cs`
- Create: `src/DotMarc/Data/DkimRecordType.cs`, `src/DotMarc/Data/DomainDkimRecord.cs`, `src/DotMarc/Dns/DkimRecordValue.cs`
- Modify: `src/DotMarc/Data/Domain.cs`, `src/DotMarc/Data/DotMarcDbContext.cs`, `src/DotMarc/Data/DomainManagementService.cs`, `src/DotMarc/Audit/AuditActions.cs`, `src/DotMarc/Demo/DemoDataSeeder.cs` (truncate list)
- Create (generated): `src/DotMarc/Migrations/<timestamp>_AddSpfDkimPush.cs`
- Test: `test/DotMarc.Tests/Dns/DkimRecordValueTests.cs`, `test/DotMarc.Tests/Notifications/DnsRecordSettingsServiceTests.cs`, `test/DotMarc.Tests/Data/DomainManagementServiceTests.cs`

**Interfaces:**
- Produces: `enum SpfAllQualifier { SoftFail, Fail }` and `class DnsRecordSettings { int Id; SpfAllQualifier SpfAllQualifier; static char ToQualifier(SpfAllQualifier) }` (`DotMarc.Notifications`); `DnsRecordSettingsService.GetAsync(DotMarcDbContext, CancellationToken = default)` and `SaveAsync(DotMarcDbContext, AuditActor, SpfAllQualifier, CancellationToken = default)`; `enum DkimRecordType { Cname, Txt }`, `class DomainDkimRecord { int Id; int DomainId; string Selector; DkimRecordType RecordType; string Value }`, `Domain.DkimRecords`, `DotMarcDbContext.DomainDkimRecords`, `DotMarcDbContext.DnsRecordSettings` (`DotMarc.Data`); `sealed record DkimRecordInput(string Selector, DkimRecordType Type, string Value)` (`DotMarc.Data`); `DomainManagementService.SetDkimRecordsAsync(DotMarcDbContext, AuditActor, int domainId, IReadOnlyList<DkimRecordInput>, CancellationToken = default)`; `DkimRecordValue.Normalize`, `Validate`, `PublicKey`, `FastmailTarget`, `Describe` (`DotMarc.Dns`); `AuditActions.DnsRecordSettingsSaved`, `AuditActions.DomainDkimRecordsChanged`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Dns/DkimRecordValueTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class DkimRecordValueTests
{
    [Theory]
    [InlineData("\"v=DKIM1; k=rsa; \" \"p=MIIBIjAN\"", "v=DKIM1; k=rsa; p=MIIBIjAN")]
    [InlineData("v=DKIM1;  k=rsa;\r\n p=MIIB\nIjAN", "v=DKIM1; k=rsa; p=MIIBIjAN")]
    [InlineData("  v=DKIM1; p=ABC  ", "v=DKIM1; p=ABC")]
    public void Normalize_Txt_JoinsQuotedChunksAndLineBreaks(string pasted, string expected)
    {
        Assert.Equal(expected, DkimRecordValue.Normalize(DkimRecordType.Txt, pasted));
    }

    [Theory]
    [InlineData("selector1-contoso-com._domainkey.Contoso.onmicrosoft.com.", "selector1-contoso-com._domainkey.contoso.onmicrosoft.com")]
    [InlineData(" fm1.contoso.com.dkim.fmhosted.com ", "fm1.contoso.com.dkim.fmhosted.com")]
    public void Normalize_Cname_LowersAndDropsTheTrailingDot(string pasted, string expected)
    {
        Assert.Equal(expected, DkimRecordValue.Normalize(DkimRecordType.Cname, pasted));
    }

    [Theory]
    [InlineData(DkimRecordType.Cname, "selector1-contoso-com._domainkey.contoso.onmicrosoft.com", true)]
    [InlineData(DkimRecordType.Cname, "not a host", false)]
    [InlineData(DkimRecordType.Cname, "localhost", false)]
    [InlineData(DkimRecordType.Txt, "v=DKIM1; k=rsa; p=MIIBIjAN", true)]
    [InlineData(DkimRecordType.Txt, "v=DKIM1; k=rsa;", false)]
    [InlineData(DkimRecordType.Txt, "v=DKIM1; p=", false)]
    public void Validate_AcceptsHostNamesAndKeys(DkimRecordType type, string value, bool valid)
    {
        Assert.Equal(valid, DkimRecordValue.Validate(type, value) is null);
    }

    [Fact]
    public void PublicKey_IgnoresWhitespaceInsideTheKey()
    {
        Assert.Equal("MIIBIjAN", DkimRecordValue.PublicKey("v=DKIM1; k=rsa; p=MIIB IjAN"));
        Assert.Null(DkimRecordValue.PublicKey("v=DKIM1; k=rsa"));
    }

    [Theory]
    [InlineData("fm1", "fm1.contoso.com.dkim.fmhosted.com")]
    [InlineData("fm3", "fm3.contoso.com.dkim.fmhosted.com")]
    [InlineData("selector1", null)]
    public void FastmailTarget_IsPredictable(string selector, string? expected)
    {
        Assert.Equal(expected, DkimRecordValue.FastmailTarget(selector, "contoso.com"));
    }
}
```

Create `test/DotMarc.Tests/Notifications/DnsRecordSettingsServiceTests.cs` with the usual Postgres boilerplate (copy the fields, constructor, `InitializeAsync`, `DisposeAsync` and `CreateContext()` from `test/DotMarc.Tests/Audit/AuditLogTests.cs`, renaming the class), then:

```csharp
    [Fact]
    public async Task AFreshDatabase_EndsNewSpfRecordsWithSoftFail()
    {
        await using var context = CreateContext();

        Assert.Equal(SpfAllQualifier.SoftFail, (await DnsRecordSettingsService.GetAsync(context)).SpfAllQualifier);
    }

    [Fact]
    public async Task SaveAsync_SavesAndAudits_AndRecordsNothingWhenUnchanged()
    {
        await using (var context = CreateContext())
        {
            await DnsRecordSettingsService.SaveAsync(context, TestActors.Admin, SpfAllQualifier.Fail);
            await DnsRecordSettingsService.SaveAsync(context, TestActors.Admin, SpfAllQualifier.Fail);
        }

        await using var verify = CreateContext();
        Assert.Equal(SpfAllQualifier.Fail, (await DnsRecordSettingsService.GetAsync(verify)).SpfAllQualifier);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.DnsRecordSettingsSaved, entry.Action);
        Assert.Contains(new AuditFieldChange("Ending for new SPF records", "~all", "-all"), entry.Changes);
    }
```

(Needed usings: `DotMarc.Audit`, `DotMarc.Data`, `DotMarc.Notifications`, `DotMarc.Tests.Internal`, `Microsoft.EntityFrameworkCore`, `Xunit`.)

Add to `DomainManagementServiceTests` (it has `CreateContext()`):

```csharp
    private async Task<int> SeedDomainWithSelectorsAsync(params string[] selectors)
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "dkim.example", FirstSeenUtc = DateTimeOffset.UtcNow, IsMonitored = true, DkimSelectors = [.. selectors] };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return domain.Id;
    }

    [Fact]
    public async Task SetDkimRecordsAsync_SavesTidiedValues_AndAuditsThem()
    {
        var domainId = await SeedDomainWithSelectorsAsync("selector1", "google");

        await using (var context = CreateContext())
        {
            await DomainManagementService.SetDkimRecordsAsync(context, TestActors.Admin, domainId,
            [
                new DkimRecordInput("selector1", DkimRecordType.Cname, "Selector1-dkim-example._domainkey.contoso.onmicrosoft.com."),
                new DkimRecordInput("google", DkimRecordType.Txt, "\"v=DKIM1; k=rsa; \" \"p=MIIBIjAN\""),
                new DkimRecordInput("not-a-selector", DkimRecordType.Txt, "v=DKIM1; p=IGNORED"),
            ]);
        }

        await using var verify = CreateContext();
        var records = await verify.DomainDkimRecords.OrderBy(record => record.Selector).ToListAsync();
        Assert.Equal(
            [("google", DkimRecordType.Txt, "v=DKIM1; k=rsa; p=MIIBIjAN"), ("selector1", DkimRecordType.Cname, "selector1-dkim-example._domainkey.contoso.onmicrosoft.com")],
            records.Select(record => (record.Selector, record.RecordType, record.Value)));
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.DomainDkimRecordsChanged, entry.Action);
        Assert.Contains(new AuditFieldChange("DKIM selector1", null, "CNAME selector1-dkim-example._domainkey.contoso.onmicrosoft.com"), entry.Changes);
    }

    [Fact]
    public async Task SetDkimRecordsAsync_ABlankValueRemovesTheRecord()
    {
        var domainId = await SeedDomainWithSelectorsAsync("google");
        await using (var context = CreateContext())
        {
            await DomainManagementService.SetDkimRecordsAsync(context, TestActors.Admin, domainId, [new DkimRecordInput("google", DkimRecordType.Txt, "v=DKIM1; p=ABC")]);
            await DomainManagementService.SetDkimRecordsAsync(context, TestActors.Admin, domainId, [new DkimRecordInput("google", DkimRecordType.Txt, "  ")]);
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.DomainDkimRecords);
    }

    [Fact]
    public async Task SetDkimRecordsAsync_RefusesAnInvalidValue_NamingTheSelector()
    {
        var domainId = await SeedDomainWithSelectorsAsync("google");
        await using var context = CreateContext();

        var refusal = await Assert.ThrowsAsync<ArgumentException>(() =>
            DomainManagementService.SetDkimRecordsAsync(context, TestActors.Admin, domainId, [new DkimRecordInput("google", DkimRecordType.Txt, "v=DKIM1; k=rsa")]));

        Assert.StartsWith("google:", refusal.Message);
    }

    [Fact]
    public async Task SetDkimSelectorsAsync_RemovesTheRecordsOfRemovedSelectors()
    {
        var domainId = await SeedDomainWithSelectorsAsync("selector1", "selector2");
        await using (var context = CreateContext())
        {
            await DomainManagementService.SetDkimRecordsAsync(context, TestActors.Admin, domainId,
            [
                new DkimRecordInput("selector1", DkimRecordType.Txt, "v=DKIM1; p=ONE"),
                new DkimRecordInput("selector2", DkimRecordType.Txt, "v=DKIM1; p=TWO"),
            ]);
            await DomainManagementService.SetDkimSelectorsAsync(context, TestActors.Admin, domainId, ["selector1"]);
        }

        await using var verify = CreateContext();
        Assert.Equal(["selector1"], await verify.DomainDkimRecords.Select(record => record.Selector).ToListAsync());
    }
```

(Add `using DotMarc.Audit;` and `using DotMarc.Data;` to `DomainManagementServiceTests` if missing.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DkimRecordValueTests|FullyQualifiedName~DnsRecordSettingsServiceTests|FullyQualifiedName~DomainManagementServiceTests" -nologo -v q`
Expected: build FAILS, the new types not found.

- [ ] **Step 3: Add the types**

Create `src/DotMarc/Notifications/DnsRecordSettings.cs`:

```csharp
namespace DotMarc.Notifications;

/// <summary>How a new SPF record ends: <c>~all</c> (softfail) or <c>-all</c> (fail).</summary>
public enum SpfAllQualifier
{
    SoftFail,
    Fail
}

/// <summary>Singleton settings row for the records dotMARC writes, seeded as Id 1 by the migration like the other
/// settings rows.</summary>
public sealed class DnsRecordSettings
{
    public int Id { get; set; }
    public SpfAllQualifier SpfAllQualifier { get; set; } = SpfAllQualifier.SoftFail;

    public static char ToQualifier(SpfAllQualifier qualifier) => qualifier == SpfAllQualifier.Fail ? '-' : '~';
}
```

Create `src/DotMarc/Notifications/DnsRecordSettingsService.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Notifications;

public static class DnsRecordSettingsService
{
    public static Task<DnsRecordSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.DnsRecordSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, SpfAllQualifier spfAllQualifier, CancellationToken cancellationToken = default)
    {
        var settings = await context.DnsRecordSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Field("Ending for new SPF records",
            $"{DnsRecordSettings.ToQualifier(settings.SpfAllQualifier)}all", $"{DnsRecordSettings.ToQualifier(spfAllQualifier)}all");
        if (!changes.Any)
        {
            return;
        }

        settings.SpfAllQualifier = spfAllQualifier;
        AuditLog.Record(context, actor, AuditActions.DnsRecordSettingsSaved, AuditTarget.Settings("DNS records"), "Saved DNS record settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

Create `src/DotMarc/Data/DkimRecordType.cs`:

```csharp
namespace DotMarc.Data;

public enum DkimRecordType
{
    /// <summary>A CNAME to a key the mail platform publishes, as Microsoft 365, Fastmail and Proton Mail use.</summary>
    Cname,

    /// <summary>The key itself, as Google Workspace and Zoho Mail use.</summary>
    Txt
}
```

Create `src/DotMarc/Data/DomainDkimRecord.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>The record a DKIM selector should have, as given by the mail platform: dotMARC pushes it and the DKIM
/// check compares DNS with it. One per selector in the domain's DkimSelectors.</summary>
public sealed class DomainDkimRecord
{
    public int Id { get; set; }
    public int DomainId { get; set; }
    public required string Selector { get; set; }
    public DkimRecordType RecordType { get; set; }
    public required string Value { get; set; }
}

/// <summary>A DKIM record as entered: a blank Value means the selector has no stored record.</summary>
public sealed record DkimRecordInput(string Selector, DkimRecordType Type, string Value);
```

In `Domain.cs`, after `public List<DomainAlertState> AlertStates { get; set; } = [];` add `public List<DomainDkimRecord> DkimRecords { get; set; } = [];`.

Create `src/DotMarc/Dns/DkimRecordValue.cs`:

```csharp
using System.Text.RegularExpressions;
using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>Tidies and checks a DKIM record pasted from a mail platform's admin console, which often wraps a long key
/// in quoted 255-character chunks or breaks it across lines.</summary>
public static partial class DkimRecordValue
{
    public static string Normalize(DkimRecordType type, string pasted)
    {
        var text = pasted.Trim();
        var chunks = QuotedChunk().Matches(text);
        if (chunks.Count > 0)
        {
            text = string.Concat(chunks.Select(chunk => chunk.Groups[1].Value.Replace("\\\"", "\"")));
        }

        if (type == DkimRecordType.Cname)
        {
            return Whitespace().Replace(text, "").TrimEnd('.').ToLowerInvariant();
        }

        // Line breaks inside the key are artefacts of the console's display; spaces between tags are collapsed.
        text = text.Replace("\r", "").Replace("\n", "");
        text = Whitespace().Replace(text, " ").Trim();
        return PublicKeyTag().Replace(text, match => "p=" + Whitespace().Replace(match.Groups[1].Value, ""));
    }

    /// <summary>Why a value can't be used, or null if it can.</summary>
    public static string? Validate(DkimRecordType type, string value) => type switch
    {
        DkimRecordType.Cname when !HostName().IsMatch(value) => "That isn't a host name. Paste the CNAME target the mail platform gives you.",
        DkimRecordType.Txt when string.IsNullOrEmpty(PublicKey(value)) => "A DKIM TXT value needs a p= tag with the public key.",
        _ => null
    };

    /// <summary>The p= tag's key with any whitespace removed, or null if there isn't one.</summary>
    public static string? PublicKey(string txt) =>
        txt.Split(';', StringSplitOptions.TrimEntries)
            .Where(tag => tag.StartsWith("p=", StringComparison.OrdinalIgnoreCase))
            .Select(tag => Whitespace().Replace(tag[2..], ""))
            .FirstOrDefault();

    /// <summary>Fastmail's DKIM records are CNAMEs whose targets follow from the selector and domain.</summary>
    public static string? FastmailTarget(string selector, string domainName) =>
        selector is "fm1" or "fm2" or "fm3" ? $"{selector}.{domainName}.dkim.fmhosted.com" : null;

    /// <summary>How a stored record reads in the audit log, such as "CNAME target.example".</summary>
    public static string Describe(DomainDkimRecord record) => $"{record.RecordType.ToString().ToUpperInvariant()} {record.Value}";

    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex QuotedChunk();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"p=([^;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex PublicKeyTag();

    // Labels of letters, digits, hyphens and underscores (DKIM targets contain _domainkey), at least two of them.
    [GeneratedRegex(@"^(?=.{1,253}$)[a-z0-9_-]{1,63}(\.[a-z0-9_-]{1,63})+$", RegexOptions.IgnoreCase)]
    private static partial Regex HostName();
}
```

In `AuditActions`, add `public const string DomainDkimRecordsChanged = "domain.dkim_records_changed";` after `DomainDkimSelectorsChanged` and `public const string DnsRecordSettingsSaved = "settings.dns_records.saved";` after `GoogleCloudDnsSettingsSaved`, and in `All` add `(DomainDkimRecordsChanged, "Domain DKIM records changed"),` after the DKIM selectors entry and `(DnsRecordSettingsSaved, "DNS record settings saved"),` after the Google Cloud DNS entry.

- [ ] **Step 4: Configure the model and save the records**

In `DotMarcDbContext`, add the sets:

```csharp
    public DbSet<DomainDkimRecord> DomainDkimRecords => Set<DomainDkimRecord>();
    public DbSet<DnsRecordSettings> DnsRecordSettings => Set<DnsRecordSettings>();
```

After the `DomainAlertState` entity block, add:

```csharp
        modelBuilder.Entity<DomainDkimRecord>(entity =>
        {
            entity.Property(record => record.Selector).HasMaxLength(63);
            entity.Property(record => record.RecordType).HasConversion<string>().HasMaxLength(10);
            entity.Property(record => record.Value).HasMaxLength(4096);
            entity.HasIndex(record => new { record.DomainId, record.Selector }).IsUnique();
            entity.HasOne<Domain>().WithMany(domain => domain.DkimRecords).HasForeignKey(record => record.DomainId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DnsRecordSettings>(entity =>
        {
            entity.Property(settings => settings.SpfAllQualifier).HasConversion<string>().HasMaxLength(10);
        });
```

and after `modelBuilder.Entity<GoogleCloudDnsSettings>().HasData(new GoogleCloudDnsSettings { Id = 1 });` add `modelBuilder.Entity<DnsRecordSettings>().HasData(new DnsRecordSettings { Id = 1 });`.

In `DomainManagementService`, add `using DotMarc.Dns;` and this method after `SetDkimSelectorsAsync`:

```csharp
    /// <summary>Sets the stored DKIM record for each of the domain's selectors: a blank value removes it, and inputs
    /// for selectors the domain doesn't have are ignored. Values are tidied, then checked; an invalid one throws an
    /// ArgumentException naming its selector, and nothing is saved.</summary>
    public static async Task SetDkimRecordsAsync(DotMarcDbContext context, AuditActor actor, int domainId, IReadOnlyList<DkimRecordInput> records, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.Include(d => d.DkimRecords).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges();
        foreach (var selector in domain.DkimSelectors)
        {
            var input = records.FirstOrDefault(candidate => string.Equals(candidate.Selector, selector, StringComparison.OrdinalIgnoreCase));
            var existing = domain.DkimRecords.FirstOrDefault(record => string.Equals(record.Selector, selector, StringComparison.OrdinalIgnoreCase));
            var before = existing is null ? null : DkimRecordValue.Describe(existing);

            if (input is null || string.IsNullOrWhiteSpace(input.Value))
            {
                if (existing is not null)
                {
                    domain.DkimRecords.Remove(existing);
                    changes.Field($"DKIM {selector}", before, (string?)null);
                }

                continue;
            }

            var value = DkimRecordValue.Normalize(input.Type, input.Value);
            if (DkimRecordValue.Validate(input.Type, value) is { } problem)
            {
                throw new ArgumentException($"{selector}: {problem}", nameof(records));
            }

            if (existing is null)
            {
                existing = new DomainDkimRecord { Selector = selector, RecordType = input.Type, Value = value };
                domain.DkimRecords.Add(existing);
            }
            else
            {
                existing.RecordType = input.Type;
                existing.Value = value;
            }

            changes.Field($"DKIM {selector}", before, DkimRecordValue.Describe(existing));
        }

        if (!changes.Any)
        {
            return;
        }

        AuditLog.Record(context, actor, AuditActions.DomainDkimRecordsChanged, AuditTarget.For(domain), $"Changed the DKIM records for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
```

In `SetDkimSelectorsAsync`, load the records and remove those of removed selectors. Replace its body with:

```csharp
        var domain = await context.Domains.Include(d => d.DkimRecords).SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges().Set("DKIM selectors", domain.DkimSelectors, selectors);
        if (!changes.Any)
        {
            return;
        }

        // A removed selector's stored record goes with it.
        foreach (var orphan in domain.DkimRecords.Where(record => !selectors.Contains(record.Selector, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            changes.Field($"DKIM {orphan.Selector}", DkimRecordValue.Describe(orphan), (string?)null);
            domain.DkimRecords.Remove(orphan);
        }

        domain.DkimSelectors = selectors;
        AuditLog.Record(context, actor, AuditActions.DomainDkimSelectorsChanged, AuditTarget.For(domain), $"Changed the DKIM selectors for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
```

In `DemoDataSeeder.TruncateAllTablesAsync`, add `"DomainDkimRecords"` after `"DomainAlertStates"`.

- [ ] **Step 5: Generate the migration**

Run: `dotnet dotnet-ef migrations add AddSpfDkimPush --project src/DotMarc --startup-project src/DotMarc`
Expected: a migration creating `DomainDkimRecords` (unique index, cascade) and `DnsRecordSettings` with an `InsertData` for row 1 whose `SpfAllQualifier` is `"SoftFail"`. Open it and check that `InsertData`; if the value is `""`, change it to `"SoftFail"`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DkimRecordValueTests|FullyQualifiedName~DnsRecordSettingsServiceTests|FullyQualifiedName~DomainManagementServiceTests|FullyQualifiedName~DotMarc.Tests.Audit|FullyQualifiedName~DotMarc.Tests.Demo" -nologo -v q`
Expected: PASS (the audit coverage test sees both new actions in `All`).

- [ ] **Step 7: Commit**

```powershell
git add src/DotMarc test/DotMarc.Tests
git commit -m "Store each DKIM selector's record and the ending for new SPF records"
```

---

### Task 5: The DKIM check compares DNS with the stored records

**Files:**
- Create: `src/DotMarc/Dns/DkimExpectedRecord.cs`
- Modify: `src/DotMarc/Dns/IDkimDnsChecker.cs`, `src/DotMarc/Dns/DkimDnsChecker.cs`, `src/DotMarc/Ingestion/PollingService.cs` (`RunSingleDkimCheckAsync`, `RunDkimCheckCycleAsync`), `src/DotMarc/Components/Pages/DomainDetail.razor` (`RecheckDkimAsync`), `test/DotMarc.Tests/Internal/FakeDkimDnsChecker.cs`
- Test: `test/DotMarc.Tests/Dns/DkimDnsCheckerTests.cs`, `test/DotMarc.Tests/Ingestion/DkimCheckCycleTests.cs`

**Interfaces:**
- Consumes: Task 4's `DomainDkimRecord`, `DkimRecordType`, `DkimRecordValue.PublicKey`, `Domain.DkimRecords`.
- Produces: `sealed record DkimExpectedRecord(string Selector, DkimRecordType Type, string Value)`; `IDkimDnsChecker.CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken, IReadOnlyList<DkimExpectedRecord>? expectedRecords = null)`; `FakeDkimDnsChecker.LastExpectedRecords`.

- [ ] **Step 1: Write the failing tests**

Add to `DkimDnsCheckerTests` (it has a `CreateChecker()` returning the checker and a `FakeHttpMessageHandler`; reuse its name):

```csharp
    private static string Answers(params (int Type, string Data)[] answers) =>
        System.Text.Json.JsonSerializer.Serialize(new { Status = 0, Answer = answers.Select(answer => new { type = answer.Type, data = answer.Data }) });

    private static readonly DkimExpectedRecord ExpectedCname =
        new("selector1", DkimRecordType.Cname, "selector1-contoso-io._domainkey.contoso.onmicrosoft.com");

    [Fact]
    public async Task CheckAsync_CnameExpected_IsMissing_WhenNothingIsThere()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = """{"Status":3}""";

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None, [ExpectedCname]);

        Assert.Equal(DkimCheckStatus.Missing, result.Status);
    }

    [Fact]
    public async Task CheckAsync_CnameExpected_IsMisconfigured_WhenItPointsElsewhere()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = Answers((5, "old-target.example."), (16, "\"v=DKIM1; p=ABC\""));

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None, [ExpectedCname]);

        Assert.Equal(DkimCheckStatus.Misconfigured, result.Status);
        Assert.Contains("selector1 points to old-target.example, expected selector1-contoso-io._domainkey.contoso.onmicrosoft.com", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_CnameExpected_IsMissing_WithAHint_WhenTheKeyIsNotPublishedYet()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = Answers((5, "Selector1-contoso-io._domainkey.contoso.onmicrosoft.com."));

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None, [ExpectedCname]);

        Assert.Equal(DkimCheckStatus.Missing, result.Status);
        Assert.Contains("turn on DKIM signing", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_CnameExpected_IsOk_WhenItMatchesAndTheKeyIsThere()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = Answers((5, "selector1-contoso-io._domainkey.contoso.onmicrosoft.com."), (16, "\"v=DKIM1; k=rsa; p=MIIB\""));

        Assert.Equal(DkimCheckStatus.Ok, (await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None, [ExpectedCname])).Status);
    }

    [Fact]
    public async Task CheckAsync_CnameExpected_IsMisconfigured_WhenAPlainTxtRecordIsThere()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = Answers((16, "\"v=DKIM1; p=ABC\""));

        var result = await checker.CheckAsync("contoso.io", ["selector1"], CancellationToken.None, [ExpectedCname]);

        Assert.Equal(DkimCheckStatus.Misconfigured, result.Status);
        Assert.Contains("a CNAME", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_TxtExpected_IsMisconfigured_WhenTheKeyDiffers()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = Answers((16, "\"v=DKIM1; k=rsa; p=OLDKEY\""));

        var result = await checker.CheckAsync("contoso.io", ["google"], CancellationToken.None, [new DkimExpectedRecord("google", DkimRecordType.Txt, "v=DKIM1; k=rsa; p=NEWKEY")]);

        Assert.Equal(DkimCheckStatus.Misconfigured, result.Status);
        Assert.Contains("google", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_TxtExpected_MatchesDespiteWhitespace()
    {
        var (checker, handler) = CreateChecker();
        handler.ResponseBody = Answers((16, "\"v=DKIM1; k=rsa; \" \"p=MIIB IjAN\""));

        var result = await checker.CheckAsync("contoso.io", ["google"], CancellationToken.None, [new DkimExpectedRecord("google", DkimRecordType.Txt, "v=DKIM1; k=rsa; p=MIIBIjAN")]);

        Assert.Equal(DkimCheckStatus.Ok, result.Status);
    }
```

(Add `using DotMarc.Data;` if missing.)

Add to `DkimCheckCycleTests`:

```csharp
    [Fact]
    public async Task RunDkimCheckCycleAsync_PassesTheStoredRecordsToTheCheck()
    {
        using var context = CreateContext();
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow, DkimSelectors = ["google"] };
        domain.DkimRecords.Add(new DomainDkimRecord { Selector = "google", RecordType = DkimRecordType.Txt, Value = "v=DKIM1; p=ABC" });
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        var checker = new FakeDkimDnsChecker();

        await CreateService(context).RunDkimCheckCycleAsync(context, checker, CancellationToken.None);

        Assert.Equal([new DkimExpectedRecord("google", DkimRecordType.Txt, "v=DKIM1; p=ABC")], checker.LastExpectedRecords);
    }
```

(Add `using DotMarc.Dns;` if missing. `CreateService` exists in that class as in `SpfCheckCycleTests`.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DkimDnsCheckerTests|FullyQualifiedName~DkimCheckCycleTests" -nologo -v q`
Expected: build FAILS, `DkimExpectedRecord` not found.

- [ ] **Step 3: Compare with the stored records**

Create `src/DotMarc/Dns/DkimExpectedRecord.cs`:

```csharp
using DotMarc.Data;

namespace DotMarc.Dns;

public sealed record DkimExpectedRecord(string Selector, DkimRecordType Type, string Value);
```

Replace the method in `IDkimDnsChecker` with:

```csharp
    /// <summary>Checks each selector. With an expected record for a selector, DNS must also match it.</summary>
    Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken, IReadOnlyList<DkimExpectedRecord>? expectedRecords = null);
```

Replace `DkimDnsChecker.CheckAsync` and its `QueryTxtAsync` with:

```csharp
    public async Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken, IReadOnlyList<DkimExpectedRecord>? expectedRecords = null)
    {
        var missing = new List<string>();
        var keyNotPublished = new List<string>();
        var noKey = new List<string>();
        var mismatches = new List<string>();

        foreach (var selector in selectors)
        {
            var live = await LookupAsync($"{selector}._domainkey.{domainName}", cancellationToken).ConfigureAwait(false);
            var expected = expectedRecords?.FirstOrDefault(record => string.Equals(record.Selector, selector, StringComparison.OrdinalIgnoreCase));
            var cname = live.DelegatedToCname?.TrimEnd('.');

            switch (expected?.Type)
            {
                case null when live.DirectValue is null:
                    missing.Add(selector);
                    break;
                case null when !live.DirectValue!.Contains("p=", StringComparison.OrdinalIgnoreCase):
                    noKey.Add(selector);
                    break;
                case DkimRecordType.Cname when cname is null && live.DirectValue is null:
                    missing.Add(selector);
                    break;
                case DkimRecordType.Cname when cname is null:
                    mismatches.Add($"{selector} is a TXT record, but a CNAME to {expected.Value} is expected");
                    break;
                case DkimRecordType.Cname when !string.Equals(cname, expected.Value, StringComparison.OrdinalIgnoreCase):
                    mismatches.Add($"{selector} points to {cname}, expected {expected.Value}");
                    break;
                case DkimRecordType.Cname when string.IsNullOrEmpty(DkimRecordValue.PublicKey(live.DirectValue ?? "")):
                    keyNotPublished.Add(selector);
                    break;
                case DkimRecordType.Txt when live.DirectValue is null:
                    missing.Add(selector);
                    break;
                case DkimRecordType.Txt when DkimRecordValue.PublicKey(live.DirectValue!) != DkimRecordValue.PublicKey(expected.Value):
                    mismatches.Add($"{selector}'s key doesn't match the one stored in dotMARC");
                    break;
            }
        }

        if (missing.Count > 0 || keyNotPublished.Count > 0)
        {
            var parts = new List<string>();
            if (missing.Count > 0)
            {
                parts.Add($"No DKIM record found for selector(s): {string.Join(", ", missing)}.");
            }

            if (keyNotPublished.Count > 0)
            {
                parts.Add($"The CNAME for selector(s) {string.Join(", ", keyNotPublished)} is in place, but the mail platform hasn't published the key yet: turn on DKIM signing there.");
            }

            return new DkimCheckResult(DkimCheckStatus.Missing, string.Join(' ', parts));
        }

        if (noKey.Count > 0 || mismatches.Count > 0)
        {
            var parts = new List<string>(mismatches);
            if (noKey.Count > 0)
            {
                parts.Insert(0, $"Selector(s) missing a p= public-key tag: {string.Join(", ", noKey)}");
            }

            return new DkimCheckResult(DkimCheckStatus.Misconfigured, string.Join("; ", parts));
        }

        return new DkimCheckResult(DkimCheckStatus.Ok, null);
    }

    private async Task<DnsRecordLookupResult> LookupAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return DnsRecordLookupParsing.ParseTxtWithCnameDetection(parsed.Answer?.Select(answer => (answer.Type, answer.Data)));
    }
```

(Add `using DotMarc.Data;` and `using DotMarc.DnsPush;` to `DkimDnsChecker.cs`. If the existing missing-record detail ended without a full stop and a test asserts it exactly, keep the test passing by matching its text; check `DkimDnsCheckerTests` for `Detail` assertions first.)

Update `test/DotMarc.Tests/Internal/FakeDkimDnsChecker.cs`'s `CheckAsync` to the new signature, recording the records:

```csharp
    public IReadOnlyList<DkimExpectedRecord>? LastExpectedRecords { get; private set; }

    public Task<DkimCheckResult> CheckAsync(string domainName, IReadOnlyList<string> selectors, CancellationToken cancellationToken, IReadOnlyList<DkimExpectedRecord>? expectedRecords = null)
    {
        LastExpectedRecords = expectedRecords;
        // ...the existing body unchanged...
    }
```

In `PollingService.RunSingleDkimCheckAsync`, pass the stored records:

```csharp
        var expectedRecords = domain.DkimRecords.Select(record => new DkimExpectedRecord(record.Selector, record.RecordType, record.Value)).ToList();
        var result = await dkimChecker.CheckAsync(domain.Name, domain.DkimSelectors, cancellationToken, expectedRecords).ConfigureAwait(false);
```

In `RunDkimCheckCycleAsync`, add `.Include(d => d.DkimRecords)` to the stale-domain query right after `context.Domains`. In `DomainDetail.razor`'s `RecheckDkimAsync`, change `db.Domains.SingleAsync(d => d.Id == _domain!.Id)` to `db.Domains.Include(d => d.DkimRecords).SingleAsync(d => d.Id == _domain!.Id)`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DkimDnsCheckerTests|FullyQualifiedName~DkimCheckCycleTests|FullyQualifiedName~ConfirmationRecheckTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/DotMarc test/DotMarc.Tests
git commit -m "Check DKIM records against the ones stored for each selector"
```

---

### Task 6: TXT values and the provider changes

**Files:**
- Create: `src/DotMarc/DnsPush/TxtValues.cs`, `test/DotMarc.Tests/Internal/FakeSecretStore.cs`
- Modify: `src/DotMarc/DnsPush/DnsRecordChange.cs`, `src/DotMarc/DnsPush/CloudflareDnsPushProvider.cs`, `src/DotMarc/DnsPush/GoogleCloudDnsPushProvider.cs`, `src/DotMarc/DnsPush/AzureDnsPushProvider.cs`
- Test: `test/DotMarc.Tests/DnsPush/TxtValuesTests.cs`, `test/DotMarc.Tests/DnsPush/CloudflareDnsPushProviderTests.cs`, `test/DotMarc.Tests/DnsPush/GoogleCloudDnsPushProviderTests.cs`, `test/DotMarc.Tests/DnsPush/AzureDnsPushProviderTests.cs`

**Interfaces:**
- Produces: `DnsRecordChangeKind.ReplaceTxtValues`; `DnsRecordChange` gains `IReadOnlyList<string>? ValuesToRemove = null` (last parameter); `static class TxtValues` with `const int MaxStringLength = 255`, `IReadOnlyList<string> Split(string value)`, `string ToQuotedText(string value)`, `string FromQuotedText(string text)`, `(List<string> Values, IReadOnlyList<string> Missing) ReplaceValues(IEnumerable<string> current, IReadOnlyList<string> remove, string add)`; `AzureDnsPushProvider.RelativeName(string recordName, string zoneName)` (internal static).

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/DnsPush/TxtValuesTests.cs`:

```csharp
using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class TxtValuesTests
{
    [Fact]
    public void Split_CutsLongValuesInto255CharacterStrings()
    {
        var value = new string('a', 300);

        Assert.Equal([255, 45], TxtValues.Split(value).Select(chunk => chunk.Length));
        Assert.Equal(["short"], TxtValues.Split("short"));
    }

    [Fact]
    public void ToQuotedText_QuotesEachStringAndEscapes()
    {
        Assert.Equal("\"v=spf1 -all\"", TxtValues.ToQuotedText("v=spf1 -all"));
        Assert.Equal("\"say \\\"hi\\\"\"", TxtValues.ToQuotedText("say \"hi\""));
        Assert.Equal(2, TxtValues.ToQuotedText(new string('a', 300)).Split("\" \"").Length);
    }

    [Theory]
    [InlineData("\"v=spf1 \" \"-all\"", "v=spf1 -all")]
    [InlineData("\"say \\\"hi\\\"\"", "say \"hi\"")]
    [InlineData("bare value", "bare value")]
    public void FromQuotedText_ReadsBackTheValue(string text, string expected)
    {
        Assert.Equal(expected, TxtValues.FromQuotedText(text));
    }

    [Fact]
    public void ReplaceValues_KeepsEveryOtherValue()
    {
        var (values, missing) = TxtValues.ReplaceValues(
            ["google-site-verification=abc", "v=spf1 include:old.example ~all", "MS=ms123"],
            ["v=spf1 include:old.example ~all"],
            "v=spf1 include:new.example ~all");

        Assert.Equal(["google-site-verification=abc", "MS=ms123", "v=spf1 include:new.example ~all"], values);
        Assert.Empty(missing);
    }

    [Fact]
    public void ReplaceValues_ReportsValuesNoLongerThere()
    {
        var (_, missing) = TxtValues.ReplaceValues(["v=spf1 -all"], ["v=spf1 include:old.example ~all"], "v=spf1 ~all");

        Assert.Equal(["v=spf1 include:old.example ~all"], missing);
    }

    [Fact]
    public void ReplaceValues_DoesntAddAValueTwice()
    {
        var (values, _) = TxtValues.ReplaceValues(["v=spf1 a ~all", "v=spf1 mx ~all"], ["v=spf1 mx ~all"], "v=spf1 a ~all");

        Assert.Equal(["v=spf1 a ~all"], values);
    }
}
```

Create `test/DotMarc.Tests/Internal/FakeSecretStore.cs`:

```csharp
using DotMarc.Notifications;

namespace DotMarc.Tests.Internal;

internal sealed class FakeSecretStore : ISecretStore
{
    public Dictionary<string, string> Secrets { get; } = [];

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Secrets[key] = value;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Secrets.TryGetValue(key, out var value) ? value : null);
}
```

Create `test/DotMarc.Tests/DnsPush/CloudflareDnsPushProviderTests.cs` with the usual Postgres boilerplate (copy from `AuditLogTests`, renaming), then:

```csharp
    private async Task<CloudflareDnsPushProvider> CreateProviderAsync(FakeHttpMessageHandler handler)
    {
        await using (var context = CreateContext())
        {
            (await context.CloudflareDnsSettings.SingleAsync()).ClientId = "client";
            await context.SaveChangesAsync();
        }

        var secrets = new FakeSecretStore();
        secrets.Secrets[CloudflareDnsSettings.SecretStoreKey] = "secret";
        return new CloudflareDnsPushProvider(new FakeDbContextFactory(_connectionString), secrets, new HttpClient(handler));
    }

    [Fact]
    public async Task ReplaceTxtValues_KeepsOtherValuesAtTheName()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"zone1"}]}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"r1","content":"\"google-site-verification=abc\""},{"id":"r2","content":"\"v=spf1 include:old.example ~all\""}]}""");
        handler.ResponseBodies.Enqueue("{}");
        handler.ResponseBodies.Enqueue("{}");
        var provider = await CreateProviderAsync(handler);
        var change = new DnsRecordChange(DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 include:new.example ~all",
            "v=spf1 include:old.example ~all", "contoso.com", ValuesToRemove: ["v=spf1 include:old.example ~all"]);

        var result = await provider.ExchangeAndPushAsync("code", "verifier", "https://dotmarc.example/callback", [change], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.Pushed, result.Outcome);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[3].Method);
        Assert.Contains("v=spf1 include:new.example ~all", handler.RequestBodies[3]);
        Assert.Equal(HttpMethod.Delete, handler.Requests[4].Method);
        Assert.EndsWith("/dns_records/r2", handler.Requests[4].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ReplaceTxtValues_ChangesNothing_WhenTheOldValueIsGone()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"zone1"}]}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"r1","content":"\"v=spf1 -all\""}]}""");
        var provider = await CreateProviderAsync(handler);
        var change = new DnsRecordChange(DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 ~all",
            "v=spf1 include:old.example ~all", "contoso.com", ValuesToRemove: ["v=spf1 include:old.example ~all"]);

        var result = await provider.ExchangeAndPushAsync("code", "verifier", "https://dotmarc.example/callback", [change], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ALongTxtValue_IsSentAsQuotedStrings()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"result":[{"id":"zone1"}]}""");
        handler.ResponseBodies.Enqueue("{}");
        var provider = await CreateProviderAsync(handler);
        var key = "v=DKIM1; k=rsa; p=" + new string('A', 300);
        var change = new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", "google._domainkey.contoso.com", key, null, "contoso.com");

        await provider.ExchangeAndPushAsync("code", "verifier", "https://dotmarc.example/callback", [change], CancellationToken.None);

        Assert.Contains("\\u0022 \\u0022", handler.RequestBodies[2].Replace("\\\" \\\"", "\\u0022 \\u0022"));
    }
```

(Needed usings: `DotMarc.Data`, `DotMarc.DnsPush`, `DotMarc.Notifications`, `DotMarc.Tests.Internal`, `Microsoft.EntityFrameworkCore`, `Xunit`. The last assertion accepts either JSON escape of the quote character between the two strings.)

Create `test/DotMarc.Tests/DnsPush/GoogleCloudDnsPushProviderTests.cs` the same way, with a `CreateProviderAsync` that sets `GoogleCloudDnsSettings.ClientId` and `GoogleCloudDnsSettings.SecretStoreKey` (check the provider's constructor parameter order in `GoogleCloudDnsPushProvider.cs` and match it), then:

```csharp
    [Fact]
    public async Task ReplaceTxtValues_KeepsOtherValuesAtTheName()
    {
        var handler = new FakeHttpMessageHandler();
        handler.ResponseBodies.Enqueue("""{"access_token":"token"}""");
        handler.ResponseBodies.Enqueue("""{"projects":[{"projectId":"p1"}]}""");
        handler.ResponseBodies.Enqueue("""{"managedZones":[{"name":"z1","dnsName":"contoso.com."}]}""");
        handler.ResponseBodies.Enqueue("""{"rrsets":[{"name":"contoso.com.","type":"TXT","ttl":300,"rrdatas":["\"google-site-verification=abc\"","\"v=spf1 include:old.example ~all\""]}]}""");
        handler.ResponseBodies.Enqueue("{}");
        var provider = await CreateProviderAsync(handler);
        var change = new DnsRecordChange(DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 include:new.example ~all",
            "v=spf1 include:old.example ~all", "contoso.com", ValuesToRemove: ["v=spf1 include:old.example ~all"]);

        var result = await provider.ExchangeAndPushAsync("code", "verifier", "https://dotmarc.example/callback", [change], CancellationToken.None);

        Assert.Equal(DnsPushOutcome.Pushed, result.Outcome);
        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[4]);
        var added = body.RootElement.GetProperty("additions")[0];
        Assert.Equal(300, added.GetProperty("ttl").GetInt32());
        Assert.Equal(["\"google-site-verification=abc\"", "\"v=spf1 include:new.example ~all\""],
            added.GetProperty("rrdatas").EnumerateArray().Select(rrdata => rrdata.GetString()));
        Assert.Equal(2, body.RootElement.GetProperty("deletions")[0].GetProperty("rrdatas").GetArrayLength());
    }
```

Create `test/DotMarc.Tests/DnsPush/AzureDnsPushProviderTests.cs`:

```csharp
using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class AzureDnsPushProviderTests
{
    [Theory]
    [InlineData("contoso.com", "contoso.com", "@")]
    [InlineData("mta-sts.contoso.com", "contoso.com", "mta-sts")]
    [InlineData("selector1._domainkey.mail.contoso.co.uk", "contoso.co.uk", "selector1._domainkey.mail")]
    [InlineData("Contoso.com.", "contoso.com", "@")]
    public void RelativeName_IsTheApexSymbolOrThePartBeforeTheZone(string recordName, string zoneName, string expected)
    {
        Assert.Equal(expected, AzureDnsPushProvider.RelativeName(recordName, zoneName));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~TxtValuesTests|FullyQualifiedName~CloudflareDnsPushProviderTests|FullyQualifiedName~GoogleCloudDnsPushProviderTests|FullyQualifiedName~AzureDnsPushProviderTests" -nologo -v q`
Expected: build FAILS, `TxtValues`, `DnsRecordChangeKind.ReplaceTxtValues` and `RelativeName` not found.

- [ ] **Step 3: Add the change kind and TXT helpers**

In `DnsRecordChange.cs`, change the enum to `public enum DnsRecordChangeKind { Create, Merge, Replace, ReplaceTxtValues }`, document it (`/// ReplaceTxtValues removes ValuesToRemove from the TXT values at Name and adds DesiredValue, leaving every other value there alone: the SPF push at a domain's apex, where site verification records live too.`), and add the last parameter `IReadOnlyList<string>? ValuesToRemove = null` to `DnsRecordChange`.

Create `src/DotMarc/DnsPush/TxtValues.cs`:

```csharp
using System.Text;

namespace DotMarc.DnsPush;

/// <summary>TXT values as DNS stores them: one value is one or more strings of at most 255 characters, which Cloudflare
/// and Google take as zone-file text (<c>"a" "b"</c>) and Azure as a list. DKIM keys are longer than 255.</summary>
public static class TxtValues
{
    public const int MaxStringLength = 255;

    public static IReadOnlyList<string> Split(string value) =>
        value.Length <= MaxStringLength ? [value] : value.Chunk(MaxStringLength).Select(chunk => new string(chunk)).ToList();

    public static string ToQuotedText(string value) =>
        string.Join(' ', Split(value).Select(chunk => $"\"{chunk.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""));

    public static string FromQuotedText(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('"'))
        {
            return trimmed;
        }

        var value = new StringBuilder();
        var inString = false;
        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (character == '"')
            {
                inString = !inString;
            }
            else if (inString && character == '\\' && index + 1 < trimmed.Length)
            {
                value.Append(trimmed[++index]);
            }
            else if (inString)
            {
                value.Append(character);
            }
        }

        return value.ToString();
    }

    /// <summary>The values at a name after removing <paramref name="remove"/> and adding <paramref name="add"/>, and
    /// any of <paramref name="remove"/> that weren't there (a sign the name changed since the push was planned).</summary>
    public static (List<string> Values, IReadOnlyList<string> Missing) ReplaceValues(IEnumerable<string> current, IReadOnlyList<string> remove, string add)
    {
        var currentValues = current.Select(value => value.Trim()).ToList();
        var missing = remove.Where(value => !currentValues.Contains(value.Trim(), StringComparer.Ordinal)).ToList();
        var values = currentValues.Where(value => !remove.Contains(value, StringComparer.Ordinal)).ToList();
        if (!values.Contains(add, StringComparer.Ordinal))
        {
            values.Add(add);
        }

        return (values, missing);
    }
}
```

- [ ] **Step 4: Teach the providers**

**Cloudflare** (`CloudflareDnsPushProvider.cs`):
- `BuildContent`: for TXT return `TxtValues.ToQuotedText(change.DesiredValue)`.
- In `PushOneChangeAsync`'s switch, add `DnsRecordChangeKind.ReplaceTxtValues => await ReplaceTxtValuesAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false),`.
- Add the DTO `private sealed record RecordWithContent([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("content")] string Content);` and:

```csharp
    /// <summary>Replaces only the given values among the TXT records at change.Name. The new record is created before
    /// the old ones are deleted, so the name is never left without SPF; if a delete then fails, the name briefly has
    /// two SPF records, which the message says.</summary>
    private async Task<DnsPushResult> ReplaceTxtValuesAsync(string zoneId, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        using var findRequest = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/zones/{zoneId}/dns_records?type=TXT&name={Uri.EscapeDataString(change.Name)}");
        findRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var findResponse = await _http.SendAsync(findRequest, cancellationToken).ConfigureAwait(false);
        if (!findResponse.IsSuccessStatusCode)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Cloudflare rejected the record lookup ({(int)findResponse.StatusCode}) - nothing was changed.");
        }

        var existing = (await findResponse.Content.ReadFromJsonAsync<ApiResponse<List<RecordWithContent>>>(cancellationToken: cancellationToken).ConfigureAwait(false))?.Result ?? [];
        var records = existing.Select(record => (record.Id, Value: TxtValues.FromQuotedText(record.Content))).ToList();
        var remove = change.ValuesToRemove ?? [];
        var (_, missing) = TxtValues.ReplaceValues(records.Select(record => record.Value), remove, change.DesiredValue);
        if (missing.Count > 0)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"The TXT records at {change.Name} changed since this push started, so nothing was changed. Try again.");
        }

        if (!records.Any(record => record.Value == change.DesiredValue))
        {
            var created = await CreateRecordAsync(zoneId, accessToken, change, cancellationToken).ConfigureAwait(false);
            if (created.Outcome != DnsPushOutcome.Pushed)
            {
                return created;
            }
        }

        foreach (var record in records.Where(record => remove.Contains(record.Value) && record.Value != change.DesiredValue))
        {
            using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"{ApiBase}/zones/{zoneId}/dns_records/{record.Id}");
            deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var deleteResponse = await _http.SendAsync(deleteRequest, cancellationToken).ConfigureAwait(false);
            if (!deleteResponse.IsSuccessStatusCode)
            {
                return new DnsPushResult(DnsPushOutcome.ProviderError,
                    $"The new record was added at {change.Name}, but removing the old \"{record.Value}\" failed ({(int)deleteResponse.StatusCode}), so the name now has more than one SPF record. Remove the old one by hand.");
            }
        }

        return new DnsPushResult(DnsPushOutcome.Pushed, null);
    }
```

**Google** (`GoogleCloudDnsPushProvider.cs`):
- `BuildRrdata`: for TXT return `TxtValues.ToQuotedText(change.DesiredValue)`.
- In `PushOneChangeAsync`'s switch, add `DnsRecordChangeKind.ReplaceTxtValues => await ReplaceTxtValuesAsync(projectId, managedZoneName, accessToken, change, cancellationToken).ConfigureAwait(false),`.
- Add:

```csharp
    /// <summary>Replaces only the given values in the TXT rrset at change.Name, in one atomic change that deletes the
    /// rrset and adds it back with the other values kept.</summary>
    private async Task<DnsPushResult> ReplaceTxtValuesAsync(string projectId, string managedZoneName, string accessToken, DnsRecordChange change, CancellationToken cancellationToken)
    {
        var fqdn = change.Name.TrimEnd('.') + ".";
        var existing = await GetExistingRrsetAsync(projectId, managedZoneName, fqdn, "TXT", accessToken, cancellationToken).ConfigureAwait(false);
        if (existing.ErrorStatusCode.HasValue)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Google rejected the record lookup ({existing.ErrorStatusCode}) - nothing was changed.");
        }

        var current = existing.Rrset?.Rrdatas.Select(TxtValues.FromQuotedText).ToList() ?? [];
        var (values, missing) = TxtValues.ReplaceValues(current, change.ValuesToRemove ?? [], change.DesiredValue);
        if (missing.Count > 0)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"The TXT records at {change.Name} changed since this push started, so nothing was changed. Try again.");
        }

        var additions = new List<ResourceRecordSet> { new(fqdn, "TXT", existing.Rrset?.Ttl ?? 3600, values.Select(TxtValues.ToQuotedText).ToList()) };
        var deletions = existing.Rrset is null ? new List<ResourceRecordSet>() : [existing.Rrset];
        return await ApplyChangeAsync(projectId, managedZoneName, accessToken, additions, deletions, cancellationToken).ConfigureAwait(false);
    }
```

**Azure** (`AzureDnsPushProvider.cs`):
- Add:

```csharp
    /// <summary>The record name relative to its zone, as Azure DNS wants it: "@" for the zone apex itself.</summary>
    internal static string RelativeName(string recordName, string zoneName)
    {
        var name = recordName.TrimEnd('.');
        return string.Equals(name, zoneName.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ? "@" : name[..^(zoneName.TrimEnd('.').Length + 1)];
    }

    private static DnsTxtRecordInfo ToTxtRecordInfo(string value)
    {
        var info = new DnsTxtRecordInfo();
        foreach (var chunk in TxtValues.Split(value))
        {
            info.Values.Add(chunk);
        }

        return info;
    }
```

- In `PushRecordAsync`, replace `var relativeName = change.Name[..^(zoneName.Length + 1)];` with `var relativeName = RelativeName(change.Name, zoneName);`, add right after the `Replace` early return:

```csharp
        if (change.Kind == DnsRecordChangeKind.ReplaceTxtValues)
        {
            return await ReplaceTxtValuesAsync(zone, relativeName, change, cancellationToken).ConfigureAwait(false);
        }
```

and change both `data.DnsTxtRecords.Add(new DnsTxtRecordInfo { Values = { change.DesiredValue } });` lines in the file to `data.DnsTxtRecords.Add(ToTxtRecordInfo(change.DesiredValue));`.
- Add:

```csharp
    /// <summary>Replaces only the given values in the TXT record set at relativeName, keeping its other values and TTL.</summary>
    private static async Task<DnsPushResult> ReplaceTxtValuesAsync(DnsZoneResource zone, string relativeName, DnsRecordChange change, CancellationToken cancellationToken)
    {
        try
        {
            var txtRecords = zone.GetDnsTxtRecords();
            var current = new List<string>();
            long ttl = 3600;
            if ((await txtRecords.ExistsAsync(relativeName, cancellationToken).ConfigureAwait(false)).Value)
            {
                var existing = (await txtRecords.GetAsync(relativeName, cancellationToken).ConfigureAwait(false)).Value;
                current = existing.Data.DnsTxtRecords.Select(record => string.Concat(record.Values)).ToList();
                ttl = existing.Data.TtlInSeconds ?? 3600;
            }

            var (values, missing) = TxtValues.ReplaceValues(current, change.ValuesToRemove ?? [], change.DesiredValue);
            if (missing.Count > 0)
            {
                return new DnsPushResult(DnsPushOutcome.ProviderError, $"The TXT records at {change.Name} changed since this push started, so nothing was changed. Try again.");
            }

            var data = new DnsTxtRecordData { TtlInSeconds = ttl };
            foreach (var value in values)
            {
                data.DnsTxtRecords.Add(ToTxtRecordInfo(value));
            }

            await txtRecords.CreateOrUpdateAsync(WaitUntil.Completed, relativeName, data, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new DnsPushResult(DnsPushOutcome.Pushed, null);
        }
        catch (RequestFailedException exception)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Azure rejected the record push: {exception.Message}");
        }
    }
```

- Make `ReplaceRecordAsync` work in both directions: replace its two up-front guards with one that allows CNAME-to-TXT and TXT-to-CNAME only:

```csharp
        var isCnameToTxt = string.Equals(existingType, "CNAME", StringComparison.OrdinalIgnoreCase) && string.Equals(change.RecordType, "TXT", StringComparison.OrdinalIgnoreCase);
        var isTxtToCname = string.Equals(existingType, "TXT", StringComparison.OrdinalIgnoreCase) && string.Equals(change.RecordType, "CNAME", StringComparison.OrdinalIgnoreCase);
        if (!isCnameToTxt && !isTxtToCname)
        {
            return new DnsPushResult(DnsPushOutcome.ProviderError, $"Don't know how to replace a {existingType} record with a {change.RecordType} record. Nothing was changed.");
        }
```

  then delete the existing record of its own type (`isCnameToTxt ? zone.GetDnsCnameRecords().GetAsync(...)` and `.DeleteAsync(...)` as today, otherwise the same with `zone.GetDnsTxtRecords()`), and create the new one by type: for TXT the existing `DnsTxtRecordData` with `ToTxtRecordInfo(change.DesiredValue)`, for CNAME `new DnsCnameRecordData { TtlInSeconds = 3600, Cname = change.DesiredValue }` through `zone.GetDnsCnameRecords().CreateOrUpdateAsync(...)`. Keep every existing catch and message.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~TxtValuesTests|FullyQualifiedName~CloudflareDnsPushProviderTests|FullyQualifiedName~GoogleCloudDnsPushProviderTests|FullyQualifiedName~AzureDnsPushProviderTests|FullyQualifiedName~DotMarc.Tests.DnsPush" -nologo -v q`
Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/DotMarc/DnsPush test/DotMarc.Tests
git commit -m "Let each DNS provider replace single TXT values and write long TXT values"
```

---

### Task 7: One change-builder per push target

**Files:**
- Create: `src/DotMarc/DnsPush/IDnsChangeBuilder.cs`, `src/DotMarc/DnsPush/MtaStsChangeBuilder.cs`, `src/DotMarc/DnsPush/DmarcChangeBuilder.cs`, `src/DotMarc/DnsPush/DmarcAuthorizationChangeBuilder.cs`, `src/DotMarc/DnsPush/TlsrptChangeBuilder.cs`
- Create: `test/DotMarc.Tests/Internal/FakeDmarcTxtLookup.cs`, `test/DotMarc.Tests/Internal/FakeTlsrptTxtLookup.cs`, `test/DotMarc.Tests/Internal/FakeDmarcAuthorizationTxtLookup.cs`, `test/DotMarc.Tests/Internal/FakeMtaStsCnameLookup.cs`
- Modify: `src/DotMarc/Program.cs` (the two `/dns-push` endpoints, registrations)
- Test: `test/DotMarc.Tests/DnsPush/DnsChangeBuilderTests.cs`

**Interfaces:**
- Produces: `sealed record DnsChangePlan(IReadOnlyList<DnsRecordChange> Changes, string? Refusal)` with `static DnsChangePlan Of(params DnsRecordChange[])` and `static DnsChangePlan Refuse(string flag)`; `sealed record DnsPushRequest(Domain Domain, string Provider, string? DomainZone, string? Payload)`; `interface IDnsChangeBuilder { string Target; string RequiredPolicy; bool WritesToDomainZone; Task<DnsChangePlan> BuildAsync(DnsPushRequest, CancellationToken); }`; `DnsChangeBuilderLookup.Find(this IEnumerable<IDnsChangeBuilder>, string? target)`.

- [ ] **Step 1: Write the fakes and the failing tests**

Create the four fakes in `test/DotMarc.Tests/Internal/`. Each records names and returns a configurable result:

```csharp
using DotMarc.DnsPush;

namespace DotMarc.Tests.Internal;

internal sealed class FakeDmarcTxtLookup : IDmarcTxtLookup
{
    public DnsRecordLookupResult Result { get; set; } = new(null, null);

    public Task<DnsRecordLookupResult> LookupAsync(string domainName, CancellationToken cancellationToken) => Task.FromResult(Result);
}
```

`FakeTlsrptTxtLookup : ITlsrptTxtLookup` and `FakeDmarcAuthorizationTxtLookup : IDmarcAuthorizationTxtLookup` are the same shape (the latter's parameter is `recordName`, and it also keeps `public List<string> LookedUp { get; } = [];`, adding each name). `FakeMtaStsCnameLookup : IMtaStsCnameLookup` (namespace `DotMarc.MtaSts`) has `public string? Cname { get; set; }` and `public string? AsuidTxt { get; set; }` returned from `LookupAsync` and `LookupAsuidTxtAsync`.

Create `test/DotMarc.Tests/DnsPush/DnsChangeBuilderTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Graph;
using DotMarc.MtaSts;
using DotMarc.Tests.Internal;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsChangeBuilderTests
{
    private static readonly Domain Contoso = new() { Id = 1, Name = "contoso.com" };

    private static IOptions<GraphOptions> Graph(string? tlsrptMailbox = "tlsrpt@dotmarc.example") => Options.Create(new GraphOptions
    {
        ClientId = "client", TenantId = "tenant", ClientSecret = "secret", MailboxAddress = "rua@dotmarc.example", TlsrptMailboxAddress = tlsrptMailbox,
    });

    private static DnsPushRequest Request(string provider = "cloudflare") => new(Contoso, provider, "contoso.com", null);

    [Fact]
    public async Task Dmarc_CreatesARecord_WhenThereIsNone()
    {
        var plan = await new DmarcChangeBuilder(new FakeDmarcTxtLookup(), Graph()).BuildAsync(Request(), CancellationToken.None);

        var change = Assert.Single(plan.Changes);
        Assert.Equal((DnsRecordChangeKind.Create, "TXT", "_dmarc.contoso.com", "v=DMARC1; p=none; rua=mailto:rua@dotmarc.example"),
            (change.Kind, change.RecordType, change.Name, change.DesiredValue));
    }

    [Fact]
    public async Task Dmarc_MergesRua_KeepingOtherTags()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("v=DMARC1; p=reject; rua=mailto:old@example.com", null) };

        var change = Assert.Single((await new DmarcChangeBuilder(lookup, Graph()).BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal((DnsRecordChangeKind.Merge, "v=DMARC1; p=reject; rua=mailto:rua@dotmarc.example"), (change.Kind, change.DesiredValue));
    }

    [Fact]
    public async Task Dmarc_ReplacesADelegatedCname()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("v=DMARC1; p=none", "contoso.dmarc-service.example") };

        var change = Assert.Single((await new DmarcChangeBuilder(lookup, Graph()).BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal((DnsRecordChangeKind.Replace, "CNAME"), (change.Kind, change.ExistingRecordType));
    }

    [Fact]
    public async Task Dmarc_RefusesARecordItCantMergeInto()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("not dmarc", null) };

        Assert.Equal("unmergeable", (await new DmarcChangeBuilder(lookup, Graph()).BuildAsync(Request(), CancellationToken.None)).Refusal);
    }

    [Fact]
    public async Task Tlsrpt_RefusesWithoutATlsrptMailbox()
    {
        Assert.Equal("error", (await new TlsrptChangeBuilder(new FakeTlsrptTxtLookup(), Graph(tlsrptMailbox: null)).BuildAsync(Request(), CancellationToken.None)).Refusal);
    }

    [Fact]
    public async Task Tlsrpt_CreatesARecord_WhenThereIsNone()
    {
        var change = Assert.Single((await new TlsrptChangeBuilder(new FakeTlsrptTxtLookup(), Graph()).BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal(("_smtp._tls.contoso.com", "v=TLSRPTv1; rua=mailto:tlsrpt@dotmarc.example"), (change.Name, change.DesiredValue));
    }

    [Fact]
    public async Task DmarcAuthorization_WritesToTheMailboxDomainsZone()
    {
        var detector = new FakeDnsProviderDetector { Result = new(DetectedDnsProvider.Cloudflare, "dotmarc.example", []) };
        var lookup = new FakeDmarcAuthorizationTxtLookup();
        var builder = new DmarcAuthorizationChangeBuilder(lookup, detector, Graph());

        var change = Assert.Single((await builder.BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.False(builder.WritesToDomainZone);
        Assert.Equal(("contoso.com._report._dmarc.dotmarc.example", "v=DMARC1;", "dotmarc.example"), (change.Name, change.DesiredValue, change.ZoneName));
    }

    [Fact]
    public async Task DmarcAuthorization_RefusesWhenTheMailboxDomainIsElsewhere()
    {
        var detector = new FakeDnsProviderDetector { Result = new(DetectedDnsProvider.AzureDns, "dotmarc.example", []) };

        var plan = await new DmarcAuthorizationChangeBuilder(new FakeDmarcAuthorizationTxtLookup(), detector, Graph()).BuildAsync(Request("cloudflare"), CancellationToken.None);

        Assert.Equal("zone-not-found", plan.Refusal);
    }

    [Fact]
    public async Task MtaSts_CreatesTheCname()
    {
        var builder = new MtaStsChangeBuilder(Options.Create(new MtaStsOptions { HostingHostname = "mta-sts.dotmarc.example" }), new FakeMtaStsCnameLookup(), new FakeMtaStsHostProvisioner());

        var change = Assert.Single((await builder.BuildAsync(Request(), CancellationToken.None)).Changes);

        Assert.Equal((DnsRecordChangeKind.Create, "CNAME", "mta-sts.contoso.com", "mta-sts.dotmarc.example"), (change.Kind, change.RecordType, change.Name, change.DesiredValue));
        Assert.Equal("MtaStsManage", builder.RequiredPolicy);
    }

    [Fact]
    public async Task MtaSts_RefusesWithoutAHostingHostname()
    {
        var builder = new MtaStsChangeBuilder(Options.Create(new MtaStsOptions()), new FakeMtaStsCnameLookup(), new FakeMtaStsHostProvisioner());

        Assert.Equal("error", (await builder.BuildAsync(Request(), CancellationToken.None)).Refusal);
    }

    [Fact]
    public void Find_LooksBuildersUpByTarget()
    {
        IDnsChangeBuilder[] builders = [new DmarcChangeBuilder(new FakeDmarcTxtLookup(), Graph()), new TlsrptChangeBuilder(new FakeTlsrptTxtLookup(), Graph())];

        Assert.IsType<TlsrptChangeBuilder>(builders.Find("tlsrpt"));
        Assert.Null(builders.Find("nope"));
        Assert.Null(builders.Find(null));
    }
}
```

(`FakeDnsProviderDetector.Result` is a `DnsProviderDetectionResult`; check its property name in the fake and match it.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsChangeBuilderTests" -nologo -v q`
Expected: build FAILS, the builders and `IDnsChangeBuilder` not found.

- [ ] **Step 3: Write the builders**

Create `src/DotMarc/DnsPush/IDnsChangeBuilder.cs`:

```csharp
using DotMarc.Data;

namespace DotMarc.DnsPush;

/// <summary>The changes to push for one target, or the popup flag explaining why there's nothing to push.</summary>
public sealed record DnsChangePlan(IReadOnlyList<DnsRecordChange> Changes, string? Refusal)
{
    public static DnsChangePlan Of(params DnsRecordChange[] changes) => new(changes, null);

    public static DnsChangePlan Refuse(string flag) => new([], flag);
}

/// <summary>What a builder needs: the domain (with its DKIM records), the provider being pushed to, the domain's zone
/// (null for builders that don't write there), and the payload the push started with (the edited SPF record).</summary>
public sealed record DnsPushRequest(Domain Domain, string Provider, string? DomainZone, string? Payload);

/// <summary>Works out what one push target ("dmarc", "spf" and so on) changes. The /dns-push endpoints look the
/// builder up by target, so each target's permission and logic live in one place.</summary>
public interface IDnsChangeBuilder
{
    string Target { get; }

    /// <summary>The authorization policy a person needs to push this target.</summary>
    string RequiredPolicy { get; }

    /// <summary>False only for a target written to another domain's zone (dmarc-auth writes to the mailbox's
    /// domain), which finds that zone itself.</summary>
    bool WritesToDomainZone { get; }

    Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken);
}

public static class DnsChangeBuilderLookup
{
    public static IDnsChangeBuilder? Find(this IEnumerable<IDnsChangeBuilder> builders, string? target) =>
        target is null ? null : builders.FirstOrDefault(builder => builder.Target == target);
}
```

Create the four builders by moving each target's branch from the `/dns-push/{provider}/callback` handler in `Program.cs` into `BuildAsync`, unchanged except: `domain` becomes `request.Domain`, `domainZone` becomes `request.DomainZone!`, `provider` becomes `request.Provider`, each `return DnsPushPopupResult.Close("x");` becomes `return DnsChangePlan.Refuse("x");`, and `changes = [...]` / `changes.Add(...)` build the list returned as `new DnsChangePlan(changes, null)`. Keep their comments. For example `DmarcChangeBuilder`:

```csharp
using DotMarc.Graph;
using Microsoft.Extensions.Options;

namespace DotMarc.DnsPush;

public sealed class DmarcChangeBuilder(IDmarcTxtLookup dmarcTxtLookup, IOptions<GraphOptions> graphOptions) : IDnsChangeBuilder
{
    public string Target => "dmarc";
    public string RequiredPolicy => "DomainsEdit";
    public bool WritesToDomainZone => true;

    public async Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        var domainName = request.Domain.Name;
        var existing = await dmarcTxtLookup.LookupAsync(domainName, cancellationToken).ConfigureAwait(false);
        var mailbox = graphOptions.Value.MailboxAddress;
        if (existing.DelegatedToCname is not null)
        {
            // The record is a CNAME delegated to a third party - DNS doesn't allow a CNAME to
            // coexist with any other record type at the same name, so there's no in-place merge
            // here, only delete-then-create. The confirm dialog makes this explicit before the
            // user ever reaches this endpoint (DnsRecordPushDecision.NeedsConfirmation always
            // returns true when DelegatedToCname is set).
            return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", $"_dmarc.{domainName}", $"v=DMARC1; p=none; rua=mailto:{mailbox}", existing.DelegatedToCname, request.DomainZone!, ExistingRecordType: "CNAME"));
        }

        if (existing.DirectValue is null)
        {
            return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_dmarc.{domainName}", $"v=DMARC1; p=none; rua=mailto:{mailbox}", null, request.DomainZone!));
        }

        var merged = DmarcRuaMerge.TryMerge(existing.DirectValue, mailbox);
        return merged is null
            ? DnsChangePlan.Refuse("unmergeable")
            : DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_dmarc.{domainName}", merged, existing.DirectValue, request.DomainZone!));
    }
}
```

- `TlsrptChangeBuilder(ITlsrptTxtLookup, IOptions<GraphOptions>)`: target `tlsrpt`, `DomainsEdit`, domain zone; the `else` (TLS-RPT) branch.
- `DmarcAuthorizationChangeBuilder(IDmarcAuthorizationTxtLookup, IDnsProviderDetector, IOptions<GraphOptions>)`: target `dmarc-auth`, `DomainsEdit`, `WritesToDomainZone => false`; the `dmarc-auth` branch, including its own mailbox-zone detection (`HttpRequestException` → `Refuse("error")`, a different provider → `Refuse("zone-not-found")`).
- `MtaStsChangeBuilder(IOptions<MtaStsOptions>, IMtaStsCnameLookup, IMtaStsHostProvisioner)`: target `mta-sts`, `MtaStsManage`, domain zone; the `mta-sts` branch (no hosting hostname → `Refuse("error")`).

- [ ] **Step 4: Dispatch through the builders**

In `Program.cs`, register the builders after the push providers:

```csharp
builder.Services.AddTransient<IDnsChangeBuilder, MtaStsChangeBuilder>();
builder.Services.AddTransient<IDnsChangeBuilder, DmarcChangeBuilder>();
builder.Services.AddTransient<IDnsChangeBuilder, DmarcAuthorizationChangeBuilder>();
builder.Services.AddTransient<IDnsChangeBuilder, TlsrptChangeBuilder>();
```

(Transient, because they hold typed HTTP clients; prefix with `DotMarc.DnsPush.` if `Program.cs` doesn't import that namespace.)

Replace the `/dns-push/{provider}/start` endpoint with:

```csharp
app.MapGet("/dns-push/{provider}/start", async (
    string provider, int domainId, string target, string? payload, HttpContext httpContext,
    IEnumerable<IDnsPushProvider> pushProviders, IEnumerable<IDnsChangeBuilder> changeBuilders, DnsPushStateProtector stateProtector,
    IAuthorizationService authorizationService) =>
{
    // An edited SPF record is a few hundred characters; a payload far longer than that isn't one.
    var changeBuilder = changeBuilders.Find(target);
    if (changeBuilder is null || payload?.Length > 4000)
    {
        return Results.BadRequest();
    }

    var authResult = await authorizationService.AuthorizeAsync(httpContext.User, changeBuilder.RequiredPolicy);
    if (!authResult.Succeeded)
    {
        return Results.Forbid();
    }

    var pushProvider = await pushProviders.FindConfiguredAsync(provider);
    if (pushProvider is null)
    {
        return Results.NotFound();
    }

    var (codeVerifier, codeChallenge) = PkceGenerator.Generate();
    var state = stateProtector.Protect(domainId, target, codeVerifier, DateTimeOffset.UtcNow);
    var redirectUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/dns-push/{provider}/callback";

    return Results.Redirect(await pushProvider.BuildAuthorizationUrlAsync(state, codeChallenge, redirectUri));
});
```

(Task 8 adds the payload to the state.) In the callback endpoint:
- Replace its parameter list's lookups with `IEnumerable<IDnsChangeBuilder> changeBuilders` (keep `pushProviders`, `stateProtector`, `dbContextFactory`, `dnsProviderDetector`, `authorizationService`, `auditRecorder`, `logger`; the per-target lookups and options move into the builders).
- Replace the `requiredPolicy` switch with `var changeBuilder = changeBuilders.Find(decodedState.PushTarget); if (changeBuilder is null) { return DnsPushPopupResult.Close("invalid"); }` and authorize against `changeBuilder.RequiredPolicy`, keeping the comment above it.
- Load the domain with its DKIM records: `context.Domains.AsNoTracking().Include(d => d.DkimRecords).SingleOrDefaultAsync(d => d.Id == decodedState.DomainId)`.
- Change `string domainZone = domain.Name; if (decodedState.PushTarget != "dmarc-auth")` to `string? domainZone = null; if (changeBuilder.WritesToDomainZone)` (keeping the detection block and its comment).
- Replace everything from `List<DnsRecordChange> changes;` through the end of the final `else { ... }` TLS-RPT branch with:

```csharp
    var plan = await changeBuilder.BuildAsync(new DnsPushRequest(domain, provider, domainZone, decodedState.Payload), CancellationToken.None);
    if (plan.Refusal is not null)
    {
        return DnsPushPopupResult.Close(plan.Refusal);
    }

    var changes = plan.Changes;
```

(Task 8 adds `Payload` to `DnsPushState`. Until then, pass `null` instead of `decodedState.Payload`.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DnsChangeBuilderTests|FullyQualifiedName~DotMarc.Tests.DnsPush|FullyQualifiedName~DotMarc.Tests.Audit" -nologo -v q`
Expected: PASS. Then `dotnet build src/DotMarc/DotMarc.csproj -nologo -v q` → `Build succeeded.`

- [ ] **Step 6: Commit**

```powershell
git add src/DotMarc/DnsPush src/DotMarc/Program.cs test/DotMarc.Tests
git commit -m "Split the DNS push callback into one change-builder per target"
```

---

### Task 8: The SPF and DKIM pushes

**Files:**
- Create: `src/DotMarc/Dns/SpfEditor.cs`, `src/DotMarc/Dns/SpfIncludeCatalog.cs`, `src/DotMarc/DnsPush/SpfPushPayload.cs`, `src/DotMarc/DnsPush/SpfChangeBuilder.cs`, `src/DotMarc/DnsPush/DkimChangeBuilder.cs`
- Modify: `src/DotMarc/DnsPush/DnsPushState.cs`, `src/DotMarc/DnsPush/DnsPushStateProtector.cs`, `src/DotMarc/Audit/DnsPushAudit.cs`, `src/DotMarc/Program.cs`
- Test: `test/DotMarc.Tests/Dns/SpfEditorTests.cs`, `test/DotMarc.Tests/DnsPush/SpfChangeBuilderTests.cs`, `test/DotMarc.Tests/DnsPush/DkimChangeBuilderTests.cs`, `test/DotMarc.Tests/DnsPush/DnsPushStateProtectorTests.cs`, `test/DotMarc.Tests/Audit/DnsPushAuditTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, 4, 6 and 7.
- Produces: `SpfEditor.StartingPoint(IReadOnlyList<string> liveValues, IReadOnlyList<string> suggestedIncludes, char defaultEnding) -> (SpfRecord Record, bool IsMerge)`, `SpfEditor.Fingerprint(IEnumerable<string> liveValues) -> string`, `SpfEditor.BlockReason(string proposedText, SpfLookupCount proposedCount, IReadOnlyList<string> liveValues, SpfLookupCount? worstLiveCount) -> string?` (returns `nothing-to-push`, `spf-too-many-lookups` or null); `SpfIncludeCatalog.All` (`IReadOnlyList<(string Name, string Include)>`) and `SpfIncludeCatalog.SuggestedIncludes(IEnumerable<DetectedMailService>)`; `sealed record SpfPushPayload(string Proposed, string Fingerprint)` with `string Serialize()` and `static SpfPushPayload? TryParse(string?)`; `DnsPushState.Payload`; `DnsPushStateProtector.Protect(int, string, string, DateTimeOffset, string? payload = null)`; `SpfChangeBuilder`, `DkimChangeBuilder` (targets `spf`, `dkim`).

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/Dns/SpfEditorTests.cs`:

```csharp
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class SpfEditorTests
{
    private static SpfLookupCount Count(int total) => new(total, [], [], []);

    [Fact]
    public void StartingPoint_IsTheLiveRecord_WhenThereIsOne()
    {
        var (record, isMerge) = SpfEditor.StartingPoint(["v=spf1 include:a.example -all"], ["spf.protection.outlook.com"], '~');

        Assert.Equal("v=spf1 include:a.example -all", record.Format());
        Assert.False(isMerge);
    }

    [Fact]
    public void StartingPoint_MergesSeveralRecords_AndAddsTheDefaultEndingIfNoneHasOne()
    {
        var (record, isMerge) = SpfEditor.StartingPoint(["v=spf1 include:a.example", "v=spf1 include:b.example"], [], '-');

        Assert.Equal("v=spf1 include:a.example include:b.example -all", record.Format());
        Assert.True(isMerge);
    }

    [Fact]
    public void StartingPoint_WithNoRecord_IncludesTheSuggestedServices()
    {
        var (record, _) = SpfEditor.StartingPoint([], ["spf.protection.outlook.com", "_spf.google.com"], '~');

        Assert.Equal("v=spf1 include:spf.protection.outlook.com include:_spf.google.com ~all", record.Format());
    }

    [Fact]
    public void Fingerprint_IgnoresOrder_ButNotContent()
    {
        Assert.Equal(SpfEditor.Fingerprint(["v=spf1 a ~all", "v=spf1 mx ~all"]), SpfEditor.Fingerprint(["v=spf1 mx ~all", "v=spf1 a ~all"]));
        Assert.NotEqual(SpfEditor.Fingerprint(["v=spf1 a ~all"]), SpfEditor.Fingerprint(["v=spf1 a -all"]));
    }

    [Fact]
    public void BlockReason_RefusesARecordOverTheLimit()
    {
        Assert.Equal("spf-too-many-lookups", SpfEditor.BlockReason("v=spf1 a ~all", Count(11), ["v=spf1 mx ~all"], Count(1)));
    }

    [Fact]
    public void BlockReason_AllowsLoweringARecordAlreadyOverTheLimit()
    {
        Assert.Null(SpfEditor.BlockReason("v=spf1 a ~all", Count(12), ["v=spf1 mx ~all"], Count(15)));
        Assert.Equal("spf-too-many-lookups", SpfEditor.BlockReason("v=spf1 a ~all", Count(15), ["v=spf1 mx ~all"], Count(15)));
    }

    [Fact]
    public void BlockReason_RefusesPushingTheLiveRecordUnchanged()
    {
        Assert.Equal("nothing-to-push", SpfEditor.BlockReason("v=spf1 a ~all", Count(1), ["v=spf1  a ~all"], Count(1)));
        Assert.Null(SpfEditor.BlockReason("v=spf1 a ~all", Count(1), ["v=spf1 a ~all", "v=spf1 mx ~all"], Count(1)));
    }

    [Fact]
    public void SuggestedIncludes_FollowTheDetectedServices()
    {
        var detected = new[] { new DetectedMailService("Microsoft 365", DetectedMailServiceKind.Inbox), new DetectedMailService("SendGrid", DetectedMailServiceKind.Sending) };

        Assert.Equal(["spf.protection.outlook.com", "sendgrid.net"], SpfIncludeCatalog.SuggestedIncludes(detected));
    }
}
```

Create `test/DotMarc.Tests/DnsPush/SpfChangeBuilderTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class SpfChangeBuilderTests
{
    private readonly FakeTxtRecordLookup _lookup = new();

    private Task<DnsChangePlan> BuildAsync(string proposed, IReadOnlyList<string> startedFrom) =>
        new SpfChangeBuilder(_lookup, new SpfLookupCounter(_lookup)).BuildAsync(
            new DnsPushRequest(new Domain { Id = 1, Name = "contoso.com" }, "cloudflare", "contoso.com",
                new SpfPushPayload(proposed, SpfEditor.Fingerprint(startedFrom)).Serialize()),
            CancellationToken.None);

    [Fact]
    public async Task SpfChangeBuilder_ReplacesTheLiveSpfValuesOnly()
    {
        _lookup.TxtByName["contoso.com"] = ["google-site-verification=abc", "v=spf1 include:old.example ~all"];
        _lookup.TxtByName["new.example"] = ["v=spf1 a ~all"];

        var plan = await BuildAsync("v=spf1 include:new.example ~all", ["v=spf1 include:old.example ~all"]);

        var change = Assert.Single(plan.Changes);
        Assert.Equal((DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 include:new.example ~all"),
            (change.Kind, change.RecordType, change.Name, change.DesiredValue));
        Assert.Equal(["v=spf1 include:old.example ~all"], change.ValuesToRemove);
    }

    [Fact]
    public async Task SpfChangeBuilder_RefusesWhenTheLiveRecordChanged()
    {
        _lookup.TxtByName["contoso.com"] = ["v=spf1 include:someone-else.example ~all"];

        Assert.Equal("spf-changed", (await BuildAsync("v=spf1 ~all", ["v=spf1 include:old.example ~all"])).Refusal);
    }

    [Fact]
    public async Task SpfChangeBuilder_RefusesARecordOverTheLimit()
    {
        _lookup.TxtByName["contoso.com"] = ["v=spf1 a ~all"];
        var proposed = "v=spf1 " + string.Join(' ', Enumerable.Range(1, 11).Select(number => $"exists:{number}.example")) + " ~all";

        Assert.Equal("spf-too-many-lookups", (await BuildAsync(proposed, ["v=spf1 a ~all"])).Refusal);
    }

    [Fact]
    public async Task SpfChangeBuilder_MergesSeveralLiveRecords()
    {
        _lookup.TxtByName["contoso.com"] = ["v=spf1 a ~all", "v=spf1 mx ~all"];

        var change = Assert.Single((await BuildAsync("v=spf1 a mx ~all", ["v=spf1 a ~all", "v=spf1 mx ~all"])).Changes);

        Assert.Equal(["v=spf1 a ~all", "v=spf1 mx ~all"], change.ValuesToRemove);
        Assert.Equal("v=spf1 a ~all | v=spf1 mx ~all", change.ExistingValue);
    }

    [Fact]
    public async Task SpfChangeBuilder_RefusesAPayloadThatIsntSpf()
    {
        Assert.Equal("invalid", (await BuildAsync("not spf", [])).Refusal);
    }
}
```

Create `test/DotMarc.Tests/DnsPush/DkimChangeBuilderTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DkimChangeBuilderTests
{
    private readonly FakeTxtRecordLookup _lookup = new();

    private Task<DnsChangePlan> BuildAsync(params DomainDkimRecord[] records)
    {
        var domain = new Domain { Id = 1, Name = "contoso.com", DkimSelectors = records.Select(record => record.Selector).ToList(), DkimRecords = [.. records] };
        return new DkimChangeBuilder(_lookup).BuildAsync(new DnsPushRequest(domain, "cloudflare", "contoso.com", null), CancellationToken.None);
    }

    private static DomainDkimRecord Cname(string selector, string target) => new() { Selector = selector, RecordType = DkimRecordType.Cname, Value = target };
    private static DomainDkimRecord Txt(string selector, string key) => new() { Selector = selector, RecordType = DkimRecordType.Txt, Value = key };

    [Fact]
    public async Task CreatesWhatIsMissing_AndSkipsWhatMatches()
    {
        _lookup.LookupsByName["selector2._domainkey.contoso.com"] = new("v=DKIM1; p=KEY", "selector2-target.example.");

        var plan = await BuildAsync(Cname("selector1", "selector1-target.example"), Cname("selector2", "selector2-target.example"));

        var change = Assert.Single(plan.Changes);
        Assert.Equal((DnsRecordChangeKind.Create, "CNAME", "selector1._domainkey.contoso.com", "selector1-target.example"),
            (change.Kind, change.RecordType, change.Name, change.DesiredValue));
    }

    [Fact]
    public async Task UpdatesACnamePointingElsewhere()
    {
        _lookup.LookupsByName["selector1._domainkey.contoso.com"] = new(null, "old-target.example.");

        var change = Assert.Single((await BuildAsync(Cname("selector1", "selector1-target.example"))).Changes);

        Assert.Equal((DnsRecordChangeKind.Merge, "old-target.example"), (change.Kind, change.ExistingValue));
    }

    [Fact]
    public async Task ReplacesACnameWithAnExpectedTxt_AndTheOtherWayRound()
    {
        _lookup.LookupsByName["google._domainkey.contoso.com"] = new("v=DKIM1; p=OLD", "somewhere.example.");
        _lookup.LookupsByName["selector1._domainkey.contoso.com"] = new("v=DKIM1; p=OLD", null);

        var plan = await BuildAsync(Txt("google", "v=DKIM1; p=NEW"), Cname("selector1", "selector1-target.example"));

        Assert.Equal(
            [("TXT", "CNAME"), ("CNAME", "TXT")],
            plan.Changes.Select(change => (change.RecordType, change.ExistingRecordType!)));
        Assert.All(plan.Changes, change => Assert.Equal(DnsRecordChangeKind.Replace, change.Kind));
    }

    [Fact]
    public async Task UpdatesADifferentKey_AndIgnoresWhitespace()
    {
        _lookup.LookupsByName["google._domainkey.contoso.com"] = new("v=DKIM1; p=OLD", null);
        _lookup.LookupsByName["zoho._domainkey.contoso.com"] = new("v=DKIM1; p=SAME KEY", null);

        var plan = await BuildAsync(Txt("google", "v=DKIM1; p=NEW"), Txt("zoho", "v=DKIM1; p=SAMEKEY"));

        var change = Assert.Single(plan.Changes);
        Assert.Equal((DnsRecordChangeKind.Merge, "google._domainkey.contoso.com"), (change.Kind, change.Name));
    }

    [Fact]
    public async Task RefusesWhenEverythingMatches()
    {
        _lookup.LookupsByName["google._domainkey.contoso.com"] = new("v=DKIM1; p=KEY", null);

        Assert.Equal("nothing-to-push", (await BuildAsync(Txt("google", "v=DKIM1; p=KEY"))).Refusal);
    }
}
```

Add to `DnsPushStateProtectorTests` (check how it builds the protector, e.g. with `EphemeralDataProtectionProvider`, and reuse that):

```csharp
    [Fact]
    public void Payload_RoundTrips()
    {
        var protector = new DnsPushStateProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var now = DateTimeOffset.UtcNow;

        var state = protector.Unprotect(protector.Protect(1, "spf", "verifier", now, "the payload"), now);

        Assert.Equal("the payload", state!.Payload);
        Assert.Null(protector.Unprotect(protector.Protect(1, "dmarc", "verifier", now), now)!.Payload);
    }
```

Add to `DnsPushAuditTests`:

```csharp
    [Fact]
    public void CreateEntry_NamesAnSpfReplacementAsSpf()
    {
        IReadOnlyList<DnsRecordChange> spf =
        [
            new(DnsRecordChangeKind.ReplaceTxtValues, "TXT", "contoso.com", "v=spf1 include:new.example ~all", "v=spf1 include:old.example ~all", "contoso.com",
                ValuesToRemove: ["v=spf1 include:old.example ~all"]),
        ];

        var entry = DnsPushAudit.CreateEntry(TestActors.Admin, Contoso, "cloudflare", spf, DnsPushOutcome.Pushed);

        Assert.Equal([new AuditFieldChange("SPF contoso.com", "v=spf1 include:old.example ~all", "v=spf1 include:new.example ~all")], entry!.Changes);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SpfEditorTests|FullyQualifiedName~SpfChangeBuilderTests|FullyQualifiedName~DkimChangeBuilderTests|FullyQualifiedName~DnsPushStateProtectorTests|FullyQualifiedName~DnsPushAuditTests" -nologo -v q`
Expected: build FAILS, the new types and `Payload` not found.

- [ ] **Step 3: Write the editor logic, catalog and payload**

Create `src/DotMarc/Dns/SpfIncludeCatalog.cs`:

```csharp
namespace DotMarc.Dns;

/// <summary>The SPF includes of common mail services, for the SPF editor's "add an include" list and its
/// suggestions for a domain with no record yet.</summary>
public static class SpfIncludeCatalog
{
    public static IReadOnlyList<(string Name, string Include)> All { get; } =
    [
        ("Microsoft 365", "spf.protection.outlook.com"),
        ("Google Workspace", "_spf.google.com"),
        ("Zoho", "zoho.com"),
        ("Zoho (EU)", "zoho.eu"),
        ("Fastmail", "spf.messagingengine.com"),
        ("Proton Mail", "_spf.protonmail.ch"),
        ("Mailchimp", "servers.mcsv.net"),
        ("SendGrid", "sendgrid.net"),
        ("Amazon SES", "amazonses.com"),
        ("Salesforce", "_spf.salesforce.com"),
    ];

    private static readonly Dictionary<string, string> IncludeByDetectedName = new(StringComparer.Ordinal)
    {
        ["Microsoft 365"] = "spf.protection.outlook.com",
        ["Google Workspace"] = "_spf.google.com",
        ["Zoho Mail"] = "zoho.com",
        ["Zoho"] = "zoho.com",
        ["Fastmail"] = "spf.messagingengine.com",
        ["ProtonMail"] = "_spf.protonmail.ch",
        ["Mailchimp"] = "servers.mcsv.net",
        ["SendGrid"] = "sendgrid.net",
        ["Amazon SES"] = "amazonses.com",
        ["Salesforce"] = "_spf.salesforce.com",
    };

    public static IReadOnlyList<string> SuggestedIncludes(IEnumerable<DetectedMailService> detected) =>
        detected.Select(service => IncludeByDetectedName.GetValueOrDefault(service.ProviderName)).OfType<string>().Distinct().ToList();
}
```

Create `src/DotMarc/Dns/SpfEditor.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace DotMarc.Dns;

/// <summary>The SPF editor's rules, kept out of the dialog so they can be tested and reused by the push.</summary>
public static class SpfEditor
{
    public const int LengthWarning = 450;

    /// <summary>Where the editor starts: the live record; several live records merged into one; or, with none, a
    /// record including the detected services with the default ending.</summary>
    public static (SpfRecord Record, bool IsMerge) StartingPoint(IReadOnlyList<string> liveValues, IReadOnlyList<string> suggestedIncludes, char defaultEnding)
    {
        if (liveValues.Count == 1)
        {
            return (SpfRecord.Parse(liveValues[0]), false);
        }

        if (liveValues.Count > 1)
        {
            var merged = SpfRecord.Merge(liveValues.Select(SpfRecord.Parse).ToList());
            return (merged.AllTerm is null && !merged.Terms.Any(term => term.IsRedirect) ? merged.WithAll(defaultEnding) : merged, true);
        }

        var record = SpfRecord.Parse("v=spf1").WithAll(defaultEnding);
        foreach (var include in suggestedIncludes)
        {
            record = record.WithInclude(include);
        }

        return (record, false);
    }

    /// <summary>Identifies the live SPF records the editor started from, so the push can tell if they changed.</summary>
    public static string Fingerprint(IEnumerable<string> liveValues) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', liveValues.Select(value => value.Trim()).Order(StringComparer.Ordinal)))));

    /// <summary>Why the proposed record can't be pushed, as a popup flag, or null if it can.</summary>
    public static string? BlockReason(string proposedText, SpfLookupCount proposedCount, IReadOnlyList<string> liveValues, SpfLookupCount? worstLiveCount)
    {
        if (liveValues.Count == 1 && SpfRecord.IsSpf(liveValues[0]) && SpfRecord.Parse(liveValues[0]).Format() == SpfRecord.Parse(proposedText).Format())
        {
            return "nothing-to-push";
        }

        var lowersAnOverLimitRecord = worstLiveCount is { IsOverLimit: true } && proposedCount.Total < worstLiveCount.Total;
        return proposedCount.IsOverLimit && !lowersAnOverLimitRecord ? "spf-too-many-lookups" : null;
    }
}
```

Create `src/DotMarc/DnsPush/SpfPushPayload.cs`:

```csharp
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
```

- [ ] **Step 4: Carry the payload and write the two builders**

In `DnsPushState.cs`, add the last parameter `string? Payload = null` to the record (and mention it in the doc comment: "Payload carries the SPF editor's proposed record; every other target leaves it null."). In `DnsPushStateProtector.Protect`, add the parameter `string? payload = null` and construct `new DnsPushState(domainId, pushTarget, codeVerifier, nowUtc.Add(Lifetime), payload)`. In the `/start` endpoint, pass `payload` as the last argument to `Protect`, and in the callback pass `decodedState.Payload` to `DnsPushRequest`.

Create `src/DotMarc/DnsPush/SpfChangeBuilder.cs`:

```csharp
using DotMarc.Dns;

namespace DotMarc.DnsPush;

/// <summary>Publishes the SPF editor's record. It re-reads the live record(s) first and refuses if they changed since
/// the editor opened, re-counts the lookups, and replaces only the SPF value(s) among the apex's TXT records.</summary>
public sealed class SpfChangeBuilder(ITxtRecordLookup txtLookup, SpfLookupCounter lookupCounter) : IDnsChangeBuilder
{
    public string Target => "spf";
    public string RequiredPolicy => "DomainsEdit";
    public bool WritesToDomainZone => true;

    public async Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        var payload = SpfPushPayload.TryParse(request.Payload);
        if (payload is null || !SpfRecord.IsSpf(payload.Proposed))
        {
            return DnsChangePlan.Refuse("invalid");
        }

        var domainName = request.Domain.Name;
        var live = (await txtLookup.GetTxtValuesAsync(domainName, cancellationToken).ConfigureAwait(false)).Where(SpfRecord.IsSpf).ToList();
        if (SpfEditor.Fingerprint(live) != payload.Fingerprint)
        {
            return DnsChangePlan.Refuse("spf-changed");
        }

        var proposed = SpfRecord.Parse(payload.Proposed);
        var proposedCount = await lookupCounter.CountAsync(domainName, proposed, cancellationToken).ConfigureAwait(false);
        SpfLookupCount? worstLiveCount = null;
        foreach (var liveValue in live)
        {
            var liveCount = await lookupCounter.CountAsync(domainName, SpfRecord.Parse(liveValue), cancellationToken).ConfigureAwait(false);
            if (worstLiveCount is null || liveCount.Total > worstLiveCount.Total)
            {
                worstLiveCount = liveCount;
            }
        }

        if (SpfEditor.BlockReason(proposed.Format(), proposedCount, live, worstLiveCount) is { } reason)
        {
            return DnsChangePlan.Refuse(reason);
        }

        return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.ReplaceTxtValues, "TXT", domainName, proposed.Format(),
            live.Count == 0 ? null : string.Join(" | ", live), request.DomainZone!, ValuesToRemove: live));
    }
}
```

Create `src/DotMarc/DnsPush/DkimChangeBuilder.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Dns;

namespace DotMarc.DnsPush;

/// <summary>Publishes each stored DKIM record that DNS doesn't already match: create when nothing is there, update
/// when the same type has a different value, replace when the other type is there.</summary>
public sealed class DkimChangeBuilder(ITxtRecordLookup txtLookup) : IDnsChangeBuilder
{
    public string Target => "dkim";
    public string RequiredPolicy => "DomainsEdit";
    public bool WritesToDomainZone => true;

    public async Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        var domain = request.Domain;
        var changes = new List<DnsRecordChange>();
        foreach (var record in domain.DkimRecords.Where(record => domain.DkimSelectors.Contains(record.Selector, StringComparer.OrdinalIgnoreCase)))
        {
            var name = $"{record.Selector}._domainkey.{domain.Name}";
            var live = await txtLookup.LookupWithCnameAsync(name, cancellationToken).ConfigureAwait(false);
            var cname = live.DelegatedToCname?.TrimEnd('.');
            var zone = request.DomainZone!;

            if (record.RecordType == DkimRecordType.Cname)
            {
                if (string.Equals(cname, record.Value, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                changes.Add(cname is not null
                    ? new DnsRecordChange(DnsRecordChangeKind.Merge, "CNAME", name, record.Value, cname, zone)
                    : live.DirectValue is not null
                        ? new DnsRecordChange(DnsRecordChangeKind.Replace, "CNAME", name, record.Value, live.DirectValue, zone, ExistingRecordType: "TXT")
                        : new DnsRecordChange(DnsRecordChangeKind.Create, "CNAME", name, record.Value, null, zone));
            }
            else if (cname is not null)
            {
                changes.Add(new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", name, record.Value, cname, zone, ExistingRecordType: "CNAME"));
            }
            else if (live.DirectValue is null)
            {
                changes.Add(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", name, record.Value, null, zone));
            }
            else if (DkimRecordValue.PublicKey(live.DirectValue) != DkimRecordValue.PublicKey(record.Value))
            {
                changes.Add(new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", name, record.Value, live.DirectValue, zone));
            }
        }

        return changes.Count == 0 ? DnsChangePlan.Refuse("nothing-to-push") : new DnsChangePlan(changes, null);
    }
}
```

Register both in `Program.cs` next to the other builders:

```csharp
builder.Services.AddTransient<IDnsChangeBuilder, SpfChangeBuilder>();
builder.Services.AddTransient<IDnsChangeBuilder, DkimChangeBuilder>();
```

In `DnsPushAudit.CreateEntry`, name an SPF replacement as SPF:

```csharp
            changes.Select(change => new AuditFieldChange(
                change.Kind == DnsRecordChangeKind.ReplaceTxtValues ? $"SPF {change.Name}" : $"{change.RecordType} {change.Name}",
                change.ExistingValue, change.DesiredValue)).ToList());
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SpfEditorTests|FullyQualifiedName~SpfChangeBuilderTests|FullyQualifiedName~DkimChangeBuilderTests|FullyQualifiedName~DnsPushStateProtectorTests|FullyQualifiedName~DnsPushAuditTests|FullyQualifiedName~DnsChangeBuilderTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/DotMarc test/DotMarc.Tests
git commit -m "Push an edited SPF record and the stored DKIM records"
```

---

### Task 9: The SPF editor, DKIM records and push buttons

**Files:**
- Create: `src/DotMarc/Components/Dialogs/SpfEditorDialog.razor`
- Modify: `src/DotMarc/Components/Dialogs/ConfigureDkimSelectorsDialog.razor`, `src/DotMarc/Components/Pages/DomainDetail.razor`, `src/DotMarc/Components/Pages/DnsPushSettings.razor`

**Interfaces:**
- Consumes: everything above. There's no component test harness for these; they're checked in the demo in Step 5.

- [ ] **Step 1: The SPF editor dialog**

Create `src/DotMarc/Components/Dialogs/SpfEditorDialog.razor`:

```razor
@* src/DotMarc/Components/Dialogs/SpfEditorDialog.razor *@
@using DotMarc.Dns
@using DotMarc.DnsPush
@inject ITxtRecordLookup TxtRecordLookup

<MudDialog>
    <TitleContent>Edit SPF for @DomainName</TitleContent>
    <DialogContent>
        @if (_isMerge)
        {
            <MudAlert Severity="Severity.Info" Dense="true" Class="mb-2">
                @DomainName has @LiveValues.Count SPF records, and receivers treat that as an error. This starts from them merged into one.
            </MudAlert>
        }
        @if (_record.AllTerm is { Qualifier: '+' or '?' })
        {
            <MudAlert Severity="Severity.Warning" Dense="true" Class="mb-2">
                The record ends with @_record.AllTerm.Text, which doesn't protect the domain. Pick an ending below.
            </MudAlert>
        }

        <MudText Typo="Typo.subtitle2">Record</MudText>
        <MudPaper Outlined="true" Class="pa-2 mb-2" Style="font-family:monospace; word-break:break-all">@_record.Format()</MudPaper>
        <MudText Typo="Typo.caption" Class="d-block mb-3" Color="@(_proposedCount?.IsOverLimit == true ? Color.Error : Color.Default)">
            @(_proposedCount is null ? "Counting DNS lookups..." : $"{_proposedCount.Total} of {SpfLookupCount.Limit} DNS lookups") · @_record.Format().Length characters
            @if (_record.Format().Length > SpfEditor.LengthWarning)
            {
                <span>(some resolvers truncate TXT answers this long)</span>
            }
        </MudText>

        <MudText Typo="Typo.subtitle2">Includes</MudText>
        @foreach (var include in _record.Includes)
        {
            <div class="d-flex align-center" style="gap:0.5rem">
                <MudText Typo="Typo.body2" Style="font-family:monospace">include:@include</MudText>
                <MudText Typo="Typo.caption" Class="mud-text-secondary">@CostOf(include)</MudText>
                <MudIconButton Icon="@Icons.Material.Filled.Close" Size="Size.Small" title="Remove" OnClick="@(() => RemoveIncludeAsync(include))" />
            </div>
        }
        <div class="d-flex align-center mt-2" style="gap:0.5rem">
            <MudSelect T="string" Label="Add a service" @bind-Value="_serviceToAdd" Dense="true" Style="min-width:220px" Clearable="true">
                @foreach (var service in OrderedCatalog())
                {
                    <MudSelectItem T="string" Value="service.Include">@service.Name (@service.Include)</MudSelectItem>
                }
            </MudSelect>
            <MudTextField T="string" Label="Or an include host" @bind-Value="_typedInclude" Dense="true" />
            <MudButton Variant="Variant.Outlined" OnClick="AddIncludeAsync">Add</MudButton>
        </div>
        @if (_includeError is not null)
        {
            <MudText Typo="Typo.caption" Color="Color.Error">@_includeError</MudText>
        }

        <div class="d-flex align-center mt-3" style="gap:1rem">
            <MudSelect T="char" Label="Ending" Value="@(_record.AllTerm?.Qualifier ?? ' ')" ValueChanged="SetEndingAsync" Dense="true" Style="max-width:260px">
                <MudSelectItem T="char" Value="'~'">~all (softfail)</MudSelectItem>
                <MudSelectItem T="char" Value="'-'">-all (fail)</MudSelectItem>
            </MudSelect>
            <MudButton Variant="Variant.Text" OnClick="SendsNoMailAsync">This domain sends no mail</MudButton>
        </div>

        @if (_proposedCount is { Problems.Count: > 0 })
        {
            <MudText Typo="Typo.subtitle2" Class="mt-3">Problems</MudText>
            @foreach (var problem in _proposedCount.Problems)
            {
                <MudText Typo="Typo.body2" Color="Color.Warning">@problem</MudText>
            }
        }

        @if (!CanPush)
        {
            <MudAlert Severity="Severity.Info" Dense="true" Class="mt-3">@CannotPushReason</MudAlert>
        }
        else if (BlockMessage() is { } blocked)
        {
            <MudAlert Severity="Severity.Warning" Dense="true" Class="mt-3">@blocked</MudAlert>
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="@(() => MudDialog.Cancel())">Cancel</MudButton>
        <MudButton Color="Color.Primary" Variant="Variant.Filled" Disabled="@(!CanPush || _proposedCount is null || BlockMessage() is not null)" OnClick="Push">Push</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public string DomainName { get; set; } = "";
    [Parameter] public List<string> LiveValues { get; set; } = [];
    [Parameter] public List<string> SuggestedIncludes { get; set; } = [];
    [Parameter] public char DefaultEnding { get; set; } = '~';
    [Parameter] public bool CanPush { get; set; }
    [Parameter] public string? CannotPushReason { get; set; }

    private SpfRecord _record = SpfRecord.Parse("v=spf1");
    private bool _isMerge;
    private SpfLookupCounter _counter = default!;
    private SpfLookupCount? _proposedCount;
    private SpfLookupCount? _worstLiveCount;
    private string? _serviceToAdd;
    private string? _typedInclude;
    private string? _includeError;

    protected override async Task OnInitializedAsync()
    {
        // One cache for the dialog's lifetime, so recounting after each edit doesn't re-query includes.
        _counter = new SpfLookupCounter(new CachingTxtRecordLookup(TxtRecordLookup));
        (_record, _isMerge) = SpfEditor.StartingPoint(LiveValues, SuggestedIncludes, DefaultEnding);
        foreach (var liveValue in LiveValues)
        {
            var liveCount = await CountSafelyAsync(SpfRecord.Parse(liveValue));
            if (liveCount is not null && (_worstLiveCount is null || liveCount.Total > _worstLiveCount.Total))
            {
                _worstLiveCount = liveCount;
            }
        }

        await RecountAsync();
    }

    private async Task<SpfLookupCount?> CountSafelyAsync(SpfRecord record)
    {
        try
        {
            return await _counter.CountAsync(DomainName, record, CancellationToken.None);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task RecountAsync()
    {
        _proposedCount = null;
        StateHasChanged();
        _proposedCount = await CountSafelyAsync(_record) ?? new SpfLookupCount(0, [], [], ["DNS lookups couldn't be counted right now. Try again in a moment."]);
    }

    private IEnumerable<(string Name, string Include)> OrderedCatalog() =>
        SpfIncludeCatalog.All.OrderBy(service => SuggestedIncludes.Contains(service.Include) ? 0 : 1);

    private string CostOf(string include) =>
        _proposedCount?.TermCosts.FirstOrDefault(cost => cost.Term.EndsWith($"include:{include}", StringComparison.OrdinalIgnoreCase)) is { } cost
            ? $"{cost.Lookups} lookup{(cost.Lookups == 1 ? "" : "s")}"
            : "";

    private async Task AddIncludeAsync()
    {
        _includeError = null;
        var host = (string.IsNullOrWhiteSpace(_typedInclude) ? _serviceToAdd : _typedInclude)?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(host))
        {
            return;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(host, @"^[a-zA-Z0-9_-]{1,63}(\.[a-zA-Z0-9_-]{1,63})+$"))
        {
            _includeError = "That isn't a host name.";
            return;
        }

        _record = _record.WithInclude(host);
        (_serviceToAdd, _typedInclude) = (null, null);
        await RecountAsync();
    }

    private async Task RemoveIncludeAsync(string include)
    {
        _record = _record.WithoutInclude(include);
        await RecountAsync();
    }

    private async Task SetEndingAsync(char qualifier)
    {
        _record = _record.WithAll(qualifier);
        await RecountAsync();
    }

    private async Task SendsNoMailAsync()
    {
        _record = SpfRecord.Parse("v=spf1 -all");
        await RecountAsync();
    }

    private string? BlockMessage() => _proposedCount is null ? null : SpfEditor.BlockReason(_record.Format(), _proposedCount, LiveValues, _worstLiveCount) switch
    {
        "nothing-to-push" => "This is the same as the live record, so there's nothing to push.",
        "spf-too-many-lookups" => $"This record needs {_proposedCount.Total} DNS lookups. Receivers stop at {SpfLookupCount.Limit}, so it can't be pushed. Remove an include to bring it down.",
        _ => null
    };

    private void Push() =>
        MudDialog.Close(DialogResult.Ok(new SpfPushPayload(_record.Format(), SpfEditor.Fingerprint(LiveValues)).Serialize()));
}
```

- [ ] **Step 2: DKIM records in the selectors dialog**

Replace `ConfigureDkimSelectorsDialog.razor`'s content with a version that keeps the selectors box and adds a record row per selector:

```razor
@* src/DotMarc/Components/Dialogs/ConfigureDkimSelectorsDialog.razor *@
@using DotMarc.Data
@using DotMarc.Dns
<MudDialog>
    <TitleContent>Configure DKIM selectors</TitleContent>
    <DialogContent>
        <MudText Typo="Typo.body2" Class="mb-2">
            One selector per line (e.g. <code>selector1</code>, <code>google</code>). dotMARC checks
            <code>&lt;selector&gt;._domainkey.@DomainName</code> for each one you list here.
        </MudText>
        <MudTextField T="string" Value="_selectorsText" ValueChanged="OnSelectorsChanged" Label="Selectors" Lines="4" Immediate="true" DebounceInterval="300" />

        <MudText Typo="Typo.subtitle2" Class="mt-4">Records (optional)</MudText>
        <MudText Typo="Typo.caption" Class="d-block mb-2 mud-text-secondary">
            Paste the record your mail platform gives you for each selector, and dotMARC can push it and check DNS matches it.
            @HelpText()
        </MudText>
        @foreach (var row in _rows)
        {
            <div class="d-flex align-start mb-2" style="gap:0.5rem">
                <MudText Typo="Typo.body2" Style="min-width:110px; padding-top:1.1rem">@row.Selector</MudText>
                <MudSelect T="DkimRecordType" @bind-Value="row.Type" Dense="true" Style="max-width:110px">
                    <MudSelectItem T="DkimRecordType" Value="DkimRecordType.Cname">CNAME</MudSelectItem>
                    <MudSelectItem T="DkimRecordType" Value="DkimRecordType.Txt">TXT</MudSelectItem>
                </MudSelect>
                <MudTextField T="string" @bind-Value="row.Value" Label="@(row.Type == DkimRecordType.Cname ? "CNAME target" : "TXT value (v=DKIM1; ... p=...)")" Lines="2" Dense="true" />
            </div>
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Color="Color.Primary" Variant="Variant.Filled" OnClick="Save">Save</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public string DomainName { get; set; } = "";
    [Parameter] public List<string> CurrentSelectors { get; set; } = [];
    [Parameter] public List<DomainDkimRecord> CurrentRecords { get; set; } = [];
    [Parameter] public string? DetectedInboxProvider { get; set; }

    private string _selectorsText = "";
    private List<RecordRow> _rows = [];

    protected override void OnInitialized()
    {
        if (CurrentSelectors.Count > 0)
        {
            _selectorsText = string.Join('\n', CurrentSelectors);
        }
        else if (DetectedInboxProvider is not null && DkimSelectorSuggestions.GetSuggestedSelectors(DetectedInboxProvider) is { } suggested)
        {
            _selectorsText = string.Join('\n', suggested);
        }

        RebuildRows();
    }

    private void OnSelectorsChanged(string text)
    {
        _selectorsText = text;
        RebuildRows();
    }

    private List<string> Selectors() =>
        _selectorsText.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>One row per selector, keeping what's been typed for selectors that are still listed.</summary>
    private void RebuildRows()
    {
        _rows = Selectors().Select(selector =>
        {
            if (_rows.FirstOrDefault(row => row.Selector == selector) is { } kept)
            {
                return kept;
            }

            if (CurrentRecords.FirstOrDefault(record => string.Equals(record.Selector, selector, StringComparison.OrdinalIgnoreCase)) is { } stored)
            {
                return new RecordRow(selector) { Type = stored.RecordType, Value = stored.Value };
            }

            return DkimRecordValue.FastmailTarget(selector, DomainName) is { } fastmail
                ? new RecordRow(selector) { Type = DkimRecordType.Cname, Value = fastmail }
                : new RecordRow(selector) { Type = DetectedInboxProvider is "Google Workspace" or "Zoho Mail" ? DkimRecordType.Txt : DkimRecordType.Cname };
        }).ToList();
    }

    private string HelpText() => DetectedInboxProvider switch
    {
        "Microsoft 365" => "Microsoft 365: the Defender portal's DKIM page, or Get-DkimSigningConfig's Selector1CNAME and Selector2CNAME.",
        "Google Workspace" => "Google Workspace: Admin console > Apps > Google Workspace > Gmail > Authenticate email.",
        "Zoho Mail" => "Zoho Mail: Admin console > Domains > Email configuration > DKIM.",
        "ProtonMail" => "Proton Mail: Settings > Domain names > the domain's DKIM step.",
        "Fastmail" => "Fastmail's records are filled in for you.",
        _ => ""
    };

    private void Save() =>
        MudDialog.Close(DialogResult.Ok(new DkimSelectorsDialogResult(
            Selectors(),
            _rows.Select(row => new DkimRecordInput(row.Selector, row.Type, row.Value ?? "")).ToList())));

    private void Cancel() => MudDialog.Cancel();

    private sealed class RecordRow(string selector)
    {
        public string Selector { get; } = selector;
        public DkimRecordType Type { get; set; } = DkimRecordType.Cname;
        public string? Value { get; set; }
    }

    public sealed record DkimSelectorsDialogResult(List<string> Selectors, List<DkimRecordInput> Records);
}
```

- [ ] **Step 3: The domain page**

In `DomainDetail.razor`:
- Add `@inject ITxtRecordLookup TxtRecordLookup` and `@inject IEnumerable<IDnsChangeBuilder> ChangeBuilders`, and `@using DotMarc.Notifications` if missing.
- Load the domain with its DKIM records: add `.Include(d => d.DkimRecords)` to the domain query after `.Include(d => d.Groups)`.
- In the SPF row's `<Authorized>` block, after the recheck button, add `<MudButton Variant="Variant.Outlined" Color="Color.Primary" StartIcon="@Icons.Material.Filled.Edit" OnClick="EditSpfAsync">Edit SPF</MudButton>`.
- In the DKIM row's `<Authorized>` block, before **Configure selectors**, add:

```razor
                                @if (_domain.DkimRecords.Count > 0 && _domain.DkimCheckStatus is DkimCheckStatus.Missing or DkimCheckStatus.Misconfigured)
                                {
                                    <MudButton Variant="Variant.Outlined" Color="Color.Primary" StartIcon="@Icons.Material.Filled.CloudSync" OnClick="PushDkimRecordsAsync">Push DKIM records</MudButton>
                                }
```

- In `OpenDkimSelectorsDialogAsync`, pass `{ x => x.CurrentRecords, _domain!.DkimRecords }`, read `var dialogResult = (ConfigureDkimSelectorsDialog.DkimSelectorsDialogResult)result.Data!;`, and save both:

```csharp
        await using var db = await DbFactory.CreateDbContextAsync();
        var actor = await AuditActorAccessor.GetAsync();
        await DomainManagementService.SetDkimSelectorsAsync(db, actor, _domain!.Id, dialogResult.Selectors, CancellationToken.None);
        try
        {
            await DomainManagementService.SetDkimRecordsAsync(db, actor, _domain.Id, dialogResult.Records, CancellationToken.None);
        }
        catch (ArgumentException refusal)
        {
            Snackbar.Add($"The selectors were saved, but a DKIM record wasn't: {refusal.Message}", Severity.Warning);
        }

        _domain.DkimSelectors = dialogResult.Selectors;
        _domain.DkimRecords = await db.DomainDkimRecords.AsNoTracking().Where(record => record.DomainId == _domain.Id).ToListAsync();

        await RecheckDkimAsync();
```

- Give `OpenDnsPushPopupAsync` an optional payload: `private async Task OpenDnsPushPopupAsync(string providerKey, int domainId, string target, string? payload = null)` building `$"/dns-push/{providerKey}/start?domainId={domainId}&target={target}" + (payload is null ? "" : $"&payload={Uri.EscapeDataString(payload)}")`.
- Add:

```csharp
    private async Task EditSpfAsync()
    {
        var providerKey = _domain!.DnsProvider.ToProviderKey();
        var pushProvider = await DnsPushProviders.FindConfiguredAsync(providerKey);

        List<string> live;
        try
        {
            live = (await TxtRecordLookup.GetTxtValuesAsync(DomainName, CancellationToken.None)).Where(SpfRecord.IsSpf).ToList();
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to look up {DomainName}'s current SPF record. Try again.", Severity.Error);
            return;
        }

        await using var db = await DbFactory.CreateDbContextAsync();
        var settings = await DnsRecordSettingsService.GetAsync(db);
        var parameters = new DialogParameters<SpfEditorDialog>
        {
            { x => x.DomainName, DomainName },
            { x => x.LiveValues, live },
            { x => x.SuggestedIncludes, SpfIncludeCatalog.SuggestedIncludes(_detectedMailServices).ToList() },
            { x => x.DefaultEnding, DnsRecordSettings.ToQualifier(settings.SpfAllQualifier) },
            { x => x.CanPush, pushProvider is not null },
            { x => x.CannotPushReason, "No DNS push option is configured for this domain's DNS provider, so you can preview changes here but not push them." },
        };
        var dialogRef = await DialogService.ShowAsync<SpfEditorDialog>("Edit SPF", parameters, new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        var result = await dialogRef.Result;
        if (result is null || result.Canceled || pushProvider is null)
        {
            return;
        }

        await OpenDnsPushPopupAsync(pushProvider.ProviderKey, _domain.Id, "spf", (string)result.Data!);
    }

    private async Task PushDkimRecordsAsync()
    {
        var pushProvider = await DnsPushProviders.FindConfiguredAsync(_domain!.DnsProvider.ToProviderKey());
        if (pushProvider is null)
        {
            Snackbar.Add("Couldn't find a configured DNS push option for this domain - add the records manually.", Severity.Warning);
            return;
        }

        DnsChangePlan plan;
        try
        {
            plan = await ChangeBuilders.Find("dkim")!.BuildAsync(new DnsPushRequest(_domain, pushProvider.ProviderKey, _domain.DnsZone ?? DomainName, null), CancellationToken.None);
        }
        catch (Exception)
        {
            Snackbar.Add($"Failed to look up {DomainName}'s current DKIM records. Try again.", Severity.Error);
            return;
        }

        if (plan.Refusal == "nothing-to-push")
        {
            Snackbar.Add("DNS already matches the stored DKIM records, so there's nothing to push.", Severity.Info);
            return;
        }

        var overwritten = plan.Changes.Where(change => change.Kind != DnsRecordChangeKind.Create).ToList();
        if (overwritten.Count > 0)
        {
            var lines = string.Join("<br>", overwritten.Select(change =>
                System.Net.WebUtility.HtmlEncode($"{change.Name}: {change.ExistingRecordType ?? change.RecordType} {change.ExistingValue} → {change.RecordType} {change.DesiredValue}")));
            var confirmed = await DialogService.ShowMessageBoxAsync("Push DKIM records",
                (MarkupString)$"These records will be overwritten:<br>{lines}", yesText: "Push", cancelText: "Cancel");
            if (confirmed != true)
            {
                return;
            }
        }

        await OpenDnsPushPopupAsync(pushProvider.ProviderKey, _domain.Id, "dkim");
    }
```

- In `OnDnsPushResult`, add:

```csharp
            case "spf-changed":
                Snackbar.Add("SPF changed since you opened the editor. Reopen it to start from the current record.", Severity.Warning);
                break;
            case "spf-too-many-lookups":
                Snackbar.Add("This record would need more than 10 DNS lookups, so it wasn't pushed.", Severity.Warning);
                break;
            case "nothing-to-push":
                Snackbar.Add("DNS already matches, so there was nothing to push.", Severity.Info);
                break;
```

- [ ] **Step 4: The setting**

In `DnsPushSettings.razor`, add a panel after the Google Cloud DNS one:

```razor
@if (_dnsRecordSettings is not null)
{
    <MudPaper Class="pa-4 mt-4">
        <MudText Typo="Typo.h5" Class="mb-4">DNS records</MudText>
        <MudSelect T="SpfAllQualifier" Label="Ending for new SPF records" @bind-Value="_dnsRecordSettings.SpfAllQualifier" Variant="Variant.Outlined" Style="max-width:320px"
                   HelperText="Used when dotMARC creates an SPF record, or merges several that have no ending.">
            <MudSelectItem T="SpfAllQualifier" Value="SpfAllQualifier.SoftFail">~all (softfail)</MudSelectItem>
            <MudSelectItem T="SpfAllQualifier" Value="SpfAllQualifier.Fail">-all (fail)</MudSelectItem>
        </MudSelect>
        <MudButton Variant="Variant.Filled" Color="Color.Primary" Class="mt-4" OnClick="SaveDnsRecordSettingsAsync">Save</MudButton>
    </MudPaper>
}
```

and in `@code`, a `private DnsRecordSettings? _dnsRecordSettings;` loaded in the existing initialization (`_dnsRecordSettings = await DnsRecordSettingsService.GetAsync(db);` with the other settings), plus:

```csharp
    private async Task SaveDnsRecordSettingsAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        await DnsRecordSettingsService.SaveAsync(db, await AuditActorAccessor.GetAsync(), _dnsRecordSettings!.SpfAllQualifier);
        Snackbar.Add("DNS record settings saved.", Severity.Success);
    }
```

- [ ] **Step 5: Build and check it in the demo**

Run: `dotnet build src/DotMarc/DotMarc.csproj -nologo -v q` → `Build succeeded.` with no new warnings.

Start the demo (`$env:Demo__Enabled='true'; $env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/DotMarc --no-build --no-launch-profile --urls http://localhost:5195`), sign in as Demo Admin, and check:
- A domain's SPF row has **Edit SPF**. The editor opens on "no record" (demo domains have no real DNS) with `v=spf1 ~all`, says pushing isn't configured, and Push is disabled.
- Adding Microsoft 365 adds `include:spf.protection.outlook.com` before `~all` and recounts (the include's real record is counted live); removing it works; switching the ending to `-all` updates the record; "This domain sends no mail" gives `v=spf1 -all`.
- **DNS push settings** has the DNS records panel; switching to `-all` and saving shows "DNS record settings saved." and an audit entry "settings.dns_records.saved".
- **Configure selectors** shows a record row per selector; typing `fm1` pre-fills the Fastmail CNAME; saving a TXT value without `p=` shows the warning snackbar; saving a valid one records `domain.dkim_records_changed`.

Stop the app.

- [ ] **Step 6: Run everything and commit**

Run: `dotnet test test/DotMarc.Tests -nologo -v q` → PASS.

```powershell
git add src/DotMarc/Components
git commit -m "Add the SPF editor, DKIM records and their push buttons"
```

---

### Task 10: Docs and roadmap

**Files:**
- Modify: `website/docs/dns-provider-push.mdx`, `website/docs/dmarc-and-mta-sts.mdx`, `website/scripts/canny-roadmap.json`

- [ ] **Step 1: Document the pushes**

In `website/docs/dns-provider-push.mdx`, change the intro's first sentence to "If a domain's DNS is hosted on Cloudflare, Azure DNS, or Google Cloud DNS, dotMARC can push its MTA-STS, DMARC, TLS reporting, SPF and DKIM records straight there instead of you copying them in by hand." Then add, after the intro (before `## Cloudflare`):

```mdx
## Editing SPF

**Edit SPF** on a domain's SPF check opens the SPF editor. It starts from the live record. If the domain has more
than one SPF record (an error, since receivers allow only one), it starts from them merged into one. With no record,
it starts with an include for each mail service dotMARC detected.

In the editor you can:

* add an include, from a list of common mail services or by typing a host name;
* remove an include;
* set the ending: `~all` (softfail) or `-all` (fail);
* choose **This domain sends no mail**, which publishes `v=spf1 -all`.

Every other part of the record is kept as it is.

The editor counts the DNS lookups the record needs, following each include. Receivers stop at 10 and SPF then fails,
so a record over 10 can't be pushed. If the live record is already over 10, any edit that lowers the count can be,
so you can work your way back under.

The domain's apex usually holds other TXT records too, such as site verifications. The push changes only the SPF
record and leaves the others alone. If someone changes SPF in the DNS provider after you open the editor, the push
stops and asks you to reopen it.

The ending for new records is set under **Manage → DNS push settings → DNS records** (`~all` by default).

## DKIM records

**Configure selectors** on a domain's DKIM check takes, for each selector, the record your mail platform gives you:
a CNAME target or a TXT value with the key. Where to find them:

| Mail platform | Where |
| --- | --- |
| Microsoft 365 | The Defender portal's DKIM page, or `Get-DkimSigningConfig` (`Selector1CNAME` and `Selector2CNAME`). |
| Google Workspace | Admin console > Apps > Google Workspace > Gmail > Authenticate email. |
| Zoho Mail | Admin console > Domains > Email configuration > DKIM. |
| Proton Mail | Settings > Domain names, the domain's DKIM step. |
| Fastmail | Filled in for you. |

Paste them as the console shows them: quotes and line breaks are tidied away. Once a selector has a record, the DKIM
check also checks DNS matches it, and says so if a CNAME is in place but the mail platform hasn't published the key
yet (turn on DKIM signing there). **Push DKIM records** then creates or updates the records that don't match,
showing anything it would overwrite first.
```

In `website/docs/dmarc-and-mta-sts.mdx`'s SPF bullet, add after the sentence about exactly one record: "It also counts the DNS lookups the record needs, following its includes, and flags a record that needs more than 10 (**Too many DNS lookups**) or includes a domain with no SPF record." In the DKIM bullet, add: "If you've stored the record your mail platform gives you for a selector, the check also confirms DNS matches it."

- [ ] **Step 2: Mark the roadmap idea complete**

In `website/scripts/canny-roadmap.json`, on `"Extend DNS Provider push to cover SPF and DKIM record pushes"`, change `"status": "planned"` to `"status": "complete"`.

- [ ] **Step 3: Check everything**

Run: `dotnet test test/DotMarc.Tests -nologo -v q` → PASS.
Run: `cd website; yarn build; cd ..` → builds with no broken links.

- [ ] **Step 4: Commit**

```powershell
git add website/docs website/scripts/canny-roadmap.json
git commit -m "Document the SPF editor and DKIM records"
```
