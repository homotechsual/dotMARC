# Bulk Domain Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let someone add and update many domains at once from a pasted list, a CSV file or an `.xlsx` file, with a
preview they confirm before anything is saved.

**Architecture:** A new `DotMarc.DomainImport` namespace turns input into rows (`CsvImportReader`,
`XlsxImportReader`), rows into typed values (`ImportTable`), and those into a plan (`DomainImportPlanner`, a pure
function over an `ImportSnapshot` loaded once from the database). `DomainImportService.ApplyAsync` carries a plan out in
one transaction through the existing domain, group and tag services, so the audit log records every change as if made
by hand. A new `/domains/import` page drives it.

**Tech Stack:** .NET 10, Blazor Server with MudBlazor 9.8, EF Core 10 on PostgreSQL, xUnit 2.9 with the Testcontainers
Postgres fixture (Docker must be running), ExcelDataReader 3.9.0 (new).

**Spec:** `docs/superpowers/specs/2026-09-30-bulk-domain-import-design.md`

## Global Constraints

- Limits: CSV and pasted text at most 1 MB; `.xlsx` at most 5 MB; at most 1,000 data rows.
- Only these refuse the whole input: a size or row limit, text that isn't valid UTF-8, an unreadable spreadsheet, or an
  `.xls` file. Everything else is per row or per value.
- A blank cell leaves that value as it is, except Groups and Tags in Match mode, where blank means none.
- New MTA-STS max age default: 604,800 seconds. Allowed max age: 1 to 31,557,600.
- Permissions: page `DomainsAdd`; Groups, Tags, Halo client, Monitored, DKIM selectors `DomainsEdit`; MTA-STS columns
  `MtaStsManage`; creating groups `GroupsAdd`, tags `TagsAdd`. Halo clients can never be created.
- Changes to domains, groups and tags go only through the existing services
  (`DomainManagementService`, `GroupManagementService`, `TagManagementService`), which take the `AuditActor` straight
  after the context.
- The sample CSV and `.xlsx` are generated from one definition and served by two endpoints, rather than being two
  checked-in files that could drift apart (the spec says static files; same result for the person).
- User-facing text uses no em dashes. Meaningful variable names everywhere, including tests and scripts.
- Commit after each task with a plain sentence message, like the repository's history.

Commands (from the repository root):

- Build: `dotnet build src/DotMarc/DotMarc.csproj -nologo -v q`
- One test class: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ClassName" -nologo -v q`
- Everything: `dotnet test test/DotMarc.Tests -nologo -v q`

## Review Focus

1. **A CSV saved by Excel** (byte order mark, `\r\n` line endings, cells padded with spaces) must import exactly as a
   clean file would. Pinned in Task 3 (`ExcelStyleCsv_ReadsLikeACleanFile`).
2. **A domain pasted as a URL or email address** must be refused with a reason saying what it looks like, not saved as
   `https://contoso.com/`. Pinned in Task 1 (`TryNormalize_ExplainsWhyUrlsAndEmailAddressesAreRefused`).
3. **The same domain written two ways** (`Contoso.com` and `contoso.com.`) must be one domain, merged, not two rows that
   both try to add it. Pinned in Task 8 (`TheSameDomainWrittenTwoWays_IsOneDomain`).
4. **Match mode** must clear groups for a blank cell only when the Groups column exists; a file without the column must
   leave groups alone. Pinned in Task 8 (`MatchMode_ClearsGroupsForABlankCell_OnlyWhenTheColumnExists`).
5. **A group deleted between the preview and the import** must make the import fail with nothing saved, never a half
   import. Pinned in Task 9 (`Apply_SavesNothing_WhenItFailsPartway`).

---

### Task 1: Stricter domain names

**Files:**
- Modify: `src/DotMarc/Data/DomainNameValidator.cs`
- Test: `test/DotMarc.Tests/Data/DomainNameValidatorTests.cs`

**Interfaces:**
- Produces: `DomainNameValidator.TryNormalize(string? input, out string normalized)` (unchanged signature, stricter) and
  a new overload `TryNormalize(string? input, out string normalized, out string? reason)` where `reason` is a sentence
  for the preview when it returns false.

- [ ] **Step 1: Write the failing tests**

Add to `DomainNameValidatorTests`:

```csharp
    [Theory]
    [InlineData("sub.domain.co.uk", "sub.domain.co.uk")]
    [InlineData("Contoso.com.", "contoso.com")]
    [InlineData("a-b.example", "a-b.example")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    [InlineData("xn--bcher-kva.example", "xn--bcher-kva.example")]
    public void TryNormalize_AcceptsRealHostnames(string input, string expected)
    {
        Assert.True(DomainNameValidator.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("https://contoso.com/", "web address")]
    [InlineData("http://contoso.com", "web address")]
    [InlineData("sam@contoso.com", "email address")]
    public void TryNormalize_ExplainsWhyUrlsAndEmailAddressesAreRefused(string input, string reasonMentions)
    {
        Assert.False(DomainNameValidator.TryNormalize(input, out _, out var reason));
        Assert.Contains(reasonMentions, reason);
    }

    [Theory]
    [InlineData("192.168.0.1")]
    [InlineData("-bad.com")]
    [InlineData("bad-.com")]
    [InlineData("contoso..com")]
    [InlineData("under_score.com")]
    [InlineData(".com")]
    public void TryNormalize_RefusesNamesThatArentHostnames(string input)
    {
        Assert.False(DomainNameValidator.TryNormalize(input, out _, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void TryNormalize_RefusesALabelOver63Characters_AndANameOver253()
    {
        Assert.False(DomainNameValidator.TryNormalize(new string('a', 64) + ".com", out _));
        Assert.True(DomainNameValidator.TryNormalize(new string('a', 63) + ".com", out _));

        var tooLong = string.Join('.', Enumerable.Repeat(new string('a', 63), 4)) + ".com";
        Assert.False(DomainNameValidator.TryNormalize(tooLong, out _));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainNameValidatorTests" -nologo -v q`
Expected: build FAILS, no overload of `TryNormalize` takes three arguments.

- [ ] **Step 3: Write the stricter validator**

Replace the body of `src/DotMarc/Data/DomainNameValidator.cs` (keep the existing class summary, and add the sentence
"Names must be real hostnames, so URLs, email addresses and IP addresses are refused." to it):

```csharp
using System.Globalization;

namespace DotMarc.Data;

/// (keep the existing summary, plus the sentence above)
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
```

- [ ] **Step 4: Run the tests, and check other callers still pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainNameValidatorTests|FullyQualifiedName~DomainManagementServiceTests|FullyQualifiedName~PollingServiceTests" -nologo -v q`
Expected: PASS. If a caller other than `DomainManagementService` uses `TryNormalize` (search:
`grep -rn "TryNormalize" src`), confirm its tests pass too.

- [ ] **Step 5: Commit**

```powershell
git add src/DotMarc/Data/DomainNameValidator.cs test/DotMarc.Tests/Data/DomainNameValidatorTests.cs
git commit -m "Require domain names to be real hostnames"
```

---

### Task 2: Imports can run the audited services inside one transaction

**Files:**
- Modify: `src/DotMarc/Audit/AuditLog.cs` (`SaveAndRecordAsync`)
- Modify: `src/DotMarc/Audit/AuditActions.cs` (add `DomainsImported`)
- Test: `test/DotMarc.Tests/Audit/AuditLogTests.cs`

**Interfaces:**
- Produces: `AuditActions.DomainsImported = "domains.imported"` (in `All` as "Domains imported"); `SaveAndRecordAsync`
  joins the caller's open transaction.

- [ ] **Step 1: Write the failing test**

Add to `AuditLogTests`:

```csharp
    [Fact]
    public async Task SaveAndRecordAsync_JoinsTheCallersTransaction()
    {
        await using (var context = CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var group = new Group { Name = "Client A" };
            context.Groups.Add(group);
            await AuditLog.SaveAndRecordAsync(context, () => AuditLog.Record(context, TestActors.Admin, AuditActions.GroupAdded, AuditTarget.For(group), "Added group Client A"), CancellationToken.None);
            await transaction.RollbackAsync();
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.Groups);
        Assert.Empty(verify.AuditEntries);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SaveAndRecordAsync_JoinsTheCallersTransaction" -nologo -v q`
Expected: FAIL with `InvalidOperationException`: the connection is already in a transaction.

- [ ] **Step 3: Join an open transaction**

In `AuditLog.SaveAndRecordAsync`, before `BeginTransactionAsync`, add:

```csharp
        // Inside a caller's transaction (a bulk import), join it: the caller commits or rolls back everything.
        if (context.Database.CurrentTransaction is not null)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            recordEntries();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
```

and add to its summary: "Inside a transaction the caller already opened, it joins that one instead."

In `AuditActions`, add `public const string DomainsImported = "domains.imported";` after `DomainsReordered`, and
`(DomainsImported, "Domains imported"),` after `(DomainsReordered, "Domains reordered"),` in `All`.

- [ ] **Step 4: Run the audit tests**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DotMarc.Tests.Audit" -nologo -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/DotMarc/Audit test/DotMarc.Tests/Audit/AuditLogTests.cs
git commit -m "Let audited saves join a transaction the caller already opened"
```

---

### Task 3: Reading CSV and pasted text

**Files:**
- Create: `src/DotMarc/DomainImport/ImportRows.cs`
- Create: `src/DotMarc/DomainImport/CsvImportReader.cs`
- Test: `test/DotMarc.Tests/DomainImport/CsvImportReaderTests.cs`

**Interfaces:**
- Produces:
  - `record ImportRow(int LineNumber, IReadOnlyList<string> Cells)`: cells trimmed; blank and `#` rows already dropped.
  - `class ImportInputException(string message) : Exception`: refuses the whole input, message shown as is.
  - `static class ImportRows` with `void AddIfData(List<ImportRow> rows, int lineNumber, IEnumerable<string?> rawCells)`
    and `Task<MemoryStream> ReadLimitedAsync(Stream stream, int maximumBytes, string tooLargeMessage, CancellationToken cancellationToken)`.
  - `static class CsvImportReader` with `const int MaximumBytes = 1_048_576`, `IReadOnlyList<ImportRow> Read(string text)`,
    `Task<IReadOnlyList<ImportRow>> ReadAsync(Stream stream, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/DomainImport/CsvImportReaderTests.cs`:

```csharp
using System.Text;
using DotMarc.DomainImport;
using Xunit;

namespace DotMarc.Tests.DomainImport;

public sealed class CsvImportReaderTests
{
    [Fact]
    public void OneDomainPerLine_IsOneCellPerRow()
    {
        var rows = CsvImportReader.Read("contoso.com\nfabrikam.com");

        Assert.Equal([(1, "contoso.com"), (2, "fabrikam.com")], rows.Select(row => (row.LineNumber, row.Cells.Single())));
    }

    [Fact]
    public void QuotedCells_KeepCommasQuotesAndLineBreaks()
    {
        var rows = CsvImportReader.Read("domain,groups\n\"contoso.com\",\"Client A, Europe;\"\"Quoted\"\" Ltd\"\nfabrikam.com,\"two\nlines\"\nnext.com");

        Assert.Equal(["contoso.com", "Client A, Europe;\"Quoted\" Ltd"], rows[1].Cells);
        Assert.Equal((3, "two\nlines"), (rows[2].LineNumber, rows[2].Cells[1]));
        Assert.Equal(5, rows[3].LineNumber);
    }

    [Fact]
    public void BlankLinesCommentsAndEmptyRows_AreSkipped_AndKeepTheirLineNumbers()
    {
        var rows = CsvImportReader.Read("contoso.com\n\n   \n# a comment\n , , \nfabrikam.com");

        Assert.Equal([(1, "contoso.com"), (6, "fabrikam.com")], rows.Select(row => (row.LineNumber, row.Cells[0])));
    }

    [Fact]
    public void ExcelStyleCsv_ReadsLikeACleanFile()
    {
        var excelStyle = CsvImportReader.Read("﻿domain , groups \r\n contoso.com , Client A \r\nfabrikam.com,\r\n");
        var clean = CsvImportReader.Read("domain,groups\ncontoso.com,Client A\nfabrikam.com,");

        Assert.Equal(clean.Select(row => string.Join('|', row.Cells)), excelStyle.Select(row => string.Join('|', row.Cells)));
    }

    [Fact]
    public void OldMacLineEndings_EndARow()
    {
        var rows = CsvImportReader.Read("a.com\rb.com\r\nc.com");

        Assert.Equal(["a.com", "b.com", "c.com"], rows.Select(row => row.Cells[0]));
    }

    [Fact]
    public async Task ReadAsync_RefusesAFileOverOneMegabyte()
    {
        var tooBig = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', CsvImportReader.MaximumBytes + 1)));

        var refusal = await Assert.ThrowsAsync<ImportInputException>(() => CsvImportReader.ReadAsync(tooBig, CancellationToken.None));

        Assert.Contains("1 MB", refusal.Message);
    }

    [Fact]
    public async Task ReadAsync_RefusesTextThatIsntUtf8()
    {
        var utf16 = new MemoryStream(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("contoso.com")).ToArray());

        var refusal = await Assert.ThrowsAsync<ImportInputException>(() => CsvImportReader.ReadAsync(utf16, CancellationToken.None));

        Assert.Contains("UTF-8", refusal.Message);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~CsvImportReaderTests" -nologo -v q`
Expected: build FAILS, namespace `DotMarc.DomainImport` not found.

- [ ] **Step 3: Write the rows helpers and the reader**

Create `src/DotMarc/DomainImport/ImportRows.cs`:

```csharp
namespace DotMarc.DomainImport;

/// <summary>One row of input, with its line (CSV) or row (Excel) number so the preview can point at it. Cells are
/// trimmed.</summary>
public sealed record ImportRow(int LineNumber, IReadOnlyList<string> Cells);

/// <summary>Refuses the whole input: over a size or row limit, not UTF-8, or an unreadable spreadsheet. The message is
/// shown to the person as it is.</summary>
public sealed class ImportInputException(string message) : Exception(message);

public static class ImportRows
{
    /// <summary>Adds a row unless it's blank or a comment (its first cell starts with #).</summary>
    public static void AddIfData(List<ImportRow> rows, int lineNumber, IEnumerable<string?> rawCells)
    {
        var cells = rawCells.Select(cell => (cell ?? "").Trim()).ToList();
        if (cells.All(cell => cell.Length == 0) || cells[0].StartsWith('#'))
        {
            return;
        }

        rows.Add(new ImportRow(lineNumber, cells));
    }

    /// <summary>Copies a stream into memory, refusing it as soon as it passes <paramref name="maximumBytes"/>.</summary>
    public static async Task<MemoryStream> ReadLimitedAsync(Stream stream, int maximumBytes, string tooLargeMessage, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximumBytes)
            {
                throw new ImportInputException(tooLargeMessage);
            }
        }

        buffer.Position = 0;
        return buffer;
    }
}
```

Create `src/DotMarc/DomainImport/CsvImportReader.cs`:

```csharp
using System.Text;

namespace DotMarc.DomainImport;

/// <summary>Reads CSV (RFC 4180) and pasted text into rows. A pasted list of domains is just CSV with one column.</summary>
public static class CsvImportReader
{
    public const int MaximumBytes = 1_048_576;

    public static async Task<IReadOnlyList<ImportRow>> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = await ImportRows.ReadLimitedAsync(stream, MaximumBytes,
            "The file is larger than 1 MB. Split it into smaller files.", cancellationToken).ConfigureAwait(false);

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (DecoderFallbackException)
        {
            throw new ImportInputException("The file isn't UTF-8 text. In Excel, save it as \"CSV UTF-8\" and try again.");
        }

        return Read(text);
    }

    public static IReadOnlyList<ImportRow> Read(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var rows = new List<ImportRow>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var lineNumber = 1;
        var rowStartLine = 1;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (character == '\n')
                    {
                        lineNumber++;
                    }

                    cell.Append(character);
                }

                continue;
            }

            switch (character)
            {
                // A quote opens a quoted cell only at the start of a cell (spaces before it are allowed).
                case '"' when cell.ToString().Trim().Length == 0:
                    cell.Clear();
                    inQuotes = true;
                    break;
                case ',':
                    cells.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r' when index + 1 < text.Length && text[index + 1] == '\n':
                    break;
                case '\r':
                case '\n':
                    EndRow();
                    lineNumber++;
                    rowStartLine = lineNumber;
                    break;
                default:
                    cell.Append(character);
                    break;
            }
        }

        EndRow();
        return rows;

        void EndRow()
        {
            cells.Add(cell.ToString());
            cell.Clear();
            ImportRows.AddIfData(rows, rowStartLine, cells);
            cells.Clear();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~CsvImportReaderTests" -nologo -v q`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```powershell
git add src/DotMarc/DomainImport test/DotMarc.Tests/DomainImport
git commit -m "Read pasted text and CSV files for domain imports"
```

---

### Task 4: Reading Excel files, and the samples

**Files:**
- Modify: `src/DotMarc/DotMarc.csproj` (add ExcelDataReader)
- Create: `src/DotMarc/DomainImport/XlsxImportReader.cs`
- Create: `src/DotMarc/DomainImport/SimpleXlsxWriter.cs`
- Create: `src/DotMarc/DomainImport/DomainImportSamples.cs`
- Test: `test/DotMarc.Tests/DomainImport/XlsxImportReaderTests.cs`

**Interfaces:**
- Consumes: Task 3's `ImportRow`, `ImportRows`, `ImportInputException`, `CsvImportReader`.
- Produces:
  - `static class XlsxImportReader` with `const int MaximumBytes = 5 * 1_048_576` and
    `Task<IReadOnlyList<ImportRow>> ReadAsync(Stream stream, string fileName, CancellationToken cancellationToken)`.
  - `static class SimpleXlsxWriter` with `record Formula(string Expression, double SavedValue)` and
    `byte[] Build(IReadOnlyList<IReadOnlyList<object?>> rows)`; a cell is `null`, a `string`, a `double` or a `Formula`.
  - `static class DomainImportSamples` with `string Csv` and `byte[] Xlsx()`.

- [ ] **Step 1: Add the package**

In `src/DotMarc/DotMarc.csproj`, next to the other `PackageReference` lines, add:

```xml
    <PackageReference Include="ExcelDataReader" Version="3.9.0" />
```

- [ ] **Step 2: Write the failing tests**

Create `test/DotMarc.Tests/DomainImport/XlsxImportReaderTests.cs`:

```csharp
using System.Text;
using DotMarc.DomainImport;
using Xunit;

namespace DotMarc.Tests.DomainImport;

public sealed class XlsxImportReaderTests
{
    private static Task<IReadOnlyList<ImportRow>> ReadAsync(byte[] bytes, string fileName = "domains.xlsx") =>
        XlsxImportReader.ReadAsync(new MemoryStream(bytes), fileName, CancellationToken.None);

    [Fact]
    public async Task ReadsTextNumbersAndAFormulasSavedValue()
    {
        var workbook = SimpleXlsxWriter.Build(
        [
            ["domain", "mta-sts max age"],
            ["contoso.com", 604800.0],
            ["fabrikam.com", new SimpleXlsxWriter.Formula("86400*2", 172800)],
        ]);

        var rows = await ReadAsync(workbook);

        Assert.Equal(["contoso.com", "604800"], rows[1].Cells);
        Assert.Equal("172800", rows[2].Cells[1]);
        Assert.Equal([1, 2, 3], rows.Select(row => row.LineNumber));
    }

    [Fact]
    public async Task BlankAndCommentRows_AreSkipped()
    {
        var workbook = SimpleXlsxWriter.Build([["contoso.com"], [null], ["# note"], ["fabrikam.com"]]);

        var rows = await ReadAsync(workbook);

        Assert.Equal(["contoso.com", "fabrikam.com"], rows.Select(row => row.Cells[0]));
    }

    [Fact]
    public async Task AnXlsFile_IsRefusedWithAWayForward()
    {
        var refusal = await Assert.ThrowsAsync<ImportInputException>(() => ReadAsync([1, 2, 3], "domains.xls"));

        Assert.Contains(".xlsx or CSV", refusal.Message);
    }

    [Fact]
    public async Task AFileThatIsntASpreadsheet_IsRefused()
    {
        var refusal = await Assert.ThrowsAsync<ImportInputException>(() => ReadAsync(Encoding.UTF8.GetBytes("not a spreadsheet")));

        Assert.Contains("couldn't be read", refusal.Message);
    }

    [Fact]
    public async Task AFileOverFiveMegabytes_IsRefused()
    {
        var refusal = await Assert.ThrowsAsync<ImportInputException>(() => ReadAsync(new byte[XlsxImportReader.MaximumBytes + 1]));

        Assert.Contains("5 MB", refusal.Message);
    }

    [Fact]
    public async Task TheSampleFiles_ReadTheSameRows()
    {
        var fromCsv = CsvImportReader.Read(DomainImportSamples.Csv);
        var fromXlsx = await ReadAsync(DomainImportSamples.Xlsx());

        Assert.Equal(fromCsv.Select(row => string.Join('|', row.Cells).TrimEnd('|')), fromXlsx.Select(row => string.Join('|', row.Cells).TrimEnd('|')));
    }
}
```

(The last assertion trims trailing empty cells on both sides, because a spreadsheet row can report more or fewer columns than the CSV line has.)

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~XlsxImportReaderTests" -nologo -v q`
Expected: build FAILS, `SimpleXlsxWriter`, `XlsxImportReader`, `DomainImportSamples` not found.

- [ ] **Step 4: Write the writer, the reader and the samples**

Create `src/DotMarc/DomainImport/SimpleXlsxWriter.cs`:

```csharp
using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace DotMarc.DomainImport;

/// <summary>Writes a minimal one-sheet .xlsx: enough for the import sample, and for tests to build spreadsheets
/// without a writing library. Up to 26 columns.</summary>
public static class SimpleXlsxWriter
{
    /// <summary>A formula cell, with the value Excel would have saved for it.</summary>
    public sealed record Formula(string Expression, double SavedValue);

    public static byte[] Build(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Write(archive, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Write(archive, "xl/workbook.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Domains" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Write(archive, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Write(archive, "xl/worksheets/sheet1.xml", SheetXml(rows));
        }

        return output.ToArray();
    }

    private static string SheetXml(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var sheet = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var rowNumber = rowIndex + 1;
            sheet.Append(CultureInfo.InvariantCulture, $"""<row r="{rowNumber}">""");
            for (var columnIndex = 0; columnIndex < rows[rowIndex].Count; columnIndex++)
            {
                var reference = $"{(char)('A' + columnIndex)}{rowNumber}";
                sheet.Append(rows[rowIndex][columnIndex] switch
                {
                    null => "",
                    string text => $"""<c r="{reference}" t="inlineStr"><is><t xml:space="preserve">{SecurityElement.Escape(text)}</t></is></c>""",
                    double number => $"""<c r="{reference}"><v>{number.ToString(CultureInfo.InvariantCulture)}</v></c>""",
                    Formula formula => $"""<c r="{reference}"><f>{SecurityElement.Escape(formula.Expression)}</f><v>{formula.SavedValue.ToString(CultureInfo.InvariantCulture)}</v></c>""",
                    var other => throw new ArgumentException($"A cell can't hold a {other.GetType().Name}.", nameof(rows))
                });
            }

            sheet.Append("</row>");
        }

        return sheet.Append("</sheetData></worksheet>").ToString();
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
```

Create `src/DotMarc/DomainImport/XlsxImportReader.cs`:

```csharp
using System.Globalization;
using ExcelDataReader;

namespace DotMarc.DomainImport;

/// <summary>Reads the first worksheet of an .xlsx file into rows. Each cell becomes text: numbers and dates in invariant
/// culture, formulas as their saved value.</summary>
public static class XlsxImportReader
{
    public const int MaximumBytes = 5 * 1_048_576;

    public static async Task<IReadOnlyList<ImportRow>> ReadAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        if (fileName.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportInputException("Old Excel .xls files can't be read. Save it as .xlsx or CSV and try again.");
        }

        using var buffer = await ImportRows.ReadLimitedAsync(stream, MaximumBytes,
            "The file is larger than 5 MB. Split it into smaller files.", cancellationToken).ConfigureAwait(false);

        try
        {
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(buffer);
            var rows = new List<ImportRow>();
            var rowNumber = 0;
            while (reader.Read())
            {
                rowNumber++;
                var cells = new List<string>(reader.FieldCount);
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    cells.Add(CellText(reader.GetValue(column)));
                }

                ImportRows.AddIfData(rows, rowNumber, cells);
            }

            return rows;
        }
        catch (Exception exception) when (exception is not ImportInputException and not OperationCanceledException)
        {
            throw new ImportInputException("The spreadsheet couldn't be read. Check it's an .xlsx file, or save it as CSV and try again.");
        }
    }

    private static string CellText(object? value) => value switch
    {
        null => "",
        string text => text,
        double number => number.ToString("0.###############", CultureInfo.InvariantCulture),
        DateTime date when date.TimeOfDay == TimeSpan.Zero => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool flag => flag ? "TRUE" : "FALSE",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
```

Create `src/DotMarc/DomainImport/DomainImportSamples.cs`:

```csharp
using System.Text;

namespace DotMarc.DomainImport;

/// <summary>The sample files offered on the Import domains page, built from one set of rows so the CSV and the .xlsx
/// always say the same thing.</summary>
public static class DomainImportSamples
{
    private static readonly string[][] Rows =
    [
        ["domain", "groups", "tags", "halo client", "monitored", "dkim selectors", "mta-sts mode", "mta-sts mx hosts", "mta-sts max age"],
        ["contoso.com", "Contoso", "primary", "Contoso Ltd", "yes", "selector1;selector2", "testing", "mail.contoso.com", "604800"],
        ["fabrikam.com", "Fabrikam;Europe", "", "", "yes", "", "", "", ""],
        ["old.contoso.com", "Contoso", "-primary", "", "no", "", "off", "", ""],
    ];

    public static string Csv { get; } = string.Join("\r\n", Rows.Select(row => string.Join(',', row.Select(QuoteIfNeeded)))) + "\r\n";

    public static byte[] Xlsx() =>
        SimpleXlsxWriter.Build(Rows.Select(row => (IReadOnlyList<object?>)row.Select(cell => cell.Length == 0 ? null : (object?)cell).ToList()).ToList());

    private static string QuoteIfNeeded(string cell) =>
        cell.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{cell.Replace("\"", "\"\"")}\"" : cell;
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~XlsxImportReaderTests" -nologo -v q`
Expected: PASS, 6 tests. If ExcelDataReader reports that an encoding provider is needed, add
`System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);` in a static constructor of
`XlsxImportReader` and record a ruling.

- [ ] **Step 6: Commit**

```powershell
git add src/DotMarc/DotMarc.csproj src/DotMarc/DomainImport test/DotMarc.Tests/DomainImport
git commit -m "Read Excel files for domain imports, and build the sample files"
```

---

### Task 5: Columns and values

**Files:**
- Create: `src/DotMarc/DomainImport/ImportTable.cs`
- Create: `src/DotMarc/DomainImport/ImportValueParser.cs`
- Test: `test/DotMarc.Tests/DomainImport/ImportTableTests.cs`

**Interfaces:**
- Consumes: Task 3's `ImportRow`, `ImportInputException`; Task 1's `DomainNameValidator`.
- Produces:
  - `enum ImportColumn { Domain, Groups, Tags, HaloClient, Monitored, DkimSelectors, MtaStsMode, MtaStsMxHosts, MtaStsMaxAge }`
  - `enum MtaStsImportMode { Off, None, Testing, Enforce }`
  - `record NameListCell(IReadOnlyList<string> Names, IReadOnlyList<string> Removals)` with
    `static NameListCell? Combine(NameListCell? first, NameListCell? second)`.
  - `record ImportTableRow(int LineNumber, string RawDomain, NameListCell? Groups, NameListCell? Tags, string? HaloClient, bool? Monitored, IReadOnlyList<string>? DkimSelectors, MtaStsImportMode? MtaStsMode, IReadOnlyList<string>? MtaStsMxHosts, int? MtaStsMaxAgeSeconds, IReadOnlyList<string> Problems)`
    (a `null` value means the cell was blank, the column is missing, or the value was refused, in which case
    `Problems` says why).
  - `record ImportTable(IReadOnlySet<ImportColumn> Columns, IReadOnlyList<ImportTableRow> Rows, IReadOnlyList<string> Warnings)`
    with `const int MaximumDataRows = 1000` and `static ImportTable FromRows(IReadOnlyList<ImportRow> rows)`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/DomainImport/ImportTableTests.cs`:

```csharp
using DotMarc.DomainImport;
using Xunit;

namespace DotMarc.Tests.DomainImport;

public sealed class ImportTableTests
{
    private static ImportTable Table(string csv) => ImportTable.FromRows(CsvImportReader.Read(csv));

    [Fact]
    public void APlainList_IsDomainsOnly()
    {
        var table = Table("contoso.com\nfabrikam.com");

        Assert.Equal([ImportColumn.Domain], table.Columns);
        Assert.Equal(["contoso.com", "fabrikam.com"], table.Rows.Select(row => row.RawDomain));
        Assert.All(table.Rows, row => Assert.Null(row.Groups));
    }

    [Fact]
    public void WithoutAHeader_ColumnsAreReadInOrder()
    {
        var row = Table("contoso.com,Client A,primary,Contoso Ltd,no").Rows.Single();

        Assert.Equal(["Client A"], row.Groups!.Names);
        Assert.Equal(["primary"], row.Tags!.Names);
        Assert.Equal("Contoso Ltd", row.HaloClient);
        Assert.False(row.Monitored);
    }

    [Fact]
    public void AHeader_MapsColumnsByNameInAnyOrder_AndWarnsAboutUnknownOnes()
    {
        var table = Table("Monitored,Colour,Domain,MTA_STS Max-Age\nyes,red,contoso.com,86400");

        Assert.Equal(new HashSet<ImportColumn> { ImportColumn.Monitored, ImportColumn.Domain, ImportColumn.MtaStsMaxAge }, table.Columns);
        var row = table.Rows.Single();
        Assert.Equal(("contoso.com", true, 86400), (row.RawDomain, row.Monitored, row.MtaStsMaxAgeSeconds));
        Assert.Contains(table.Warnings, warning => warning.Contains("Colour"));
    }

    [Fact]
    public void NameLists_TrimEntries_DropEmptyOnes_AndSplitOutRemovals()
    {
        var groups = Table("contoso.com,Client A; ;-Old Client ; Client B").Rows.Single().Groups!;

        Assert.Equal(["Client A", "Client B"], groups.Names);
        Assert.Equal(["Old Client"], groups.Removals);
    }

    [Fact]
    public void BlankCells_AreNull()
    {
        var row = Table("domain,groups,monitored,mta-sts mode\ncontoso.com,,,").Rows.Single();

        Assert.Null(row.Groups);
        Assert.Null(row.Monitored);
        Assert.Null(row.MtaStsMode);
        Assert.Empty(row.Problems);
    }

    [Fact]
    public void ValuesThatDontMakeSense_AreLeftOut_WithTheReason()
    {
        var row = Table("domain,monitored,mta-sts mode,mta-sts max age,dkim selectors,mta-sts mx hosts\ncontoso.com,maybe,sometimes,0,bad selector!,not a host").Rows.Single();

        Assert.Null(row.Monitored);
        Assert.Null(row.MtaStsMode);
        Assert.Null(row.MtaStsMaxAgeSeconds);
        Assert.Null(row.DkimSelectors);
        Assert.Null(row.MtaStsMxHosts);
        Assert.Equal(5, row.Problems.Count);
        Assert.Contains(row.Problems, problem => problem.Contains("maybe"));
    }

    [Theory]
    [InlineData("YES", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("No", false)]
    public void Monitored_AcceptsYesNoTrueFalseAndOneZero(string cell, bool expected)
    {
        Assert.Equal(expected, Table($"domain,monitored\ncontoso.com,{cell}").Rows.Single().Monitored);
    }

    [Fact]
    public void MtaStsValues_AreParsed()
    {
        var row = Table("domain,mta-sts mode,mx hosts,max age\ncontoso.com,Enforce,mail.contoso.com;*.mx.contoso.com,31557600").Rows.Single();

        Assert.Equal(MtaStsImportMode.Enforce, row.MtaStsMode);
        Assert.Equal(["mail.contoso.com", "*.mx.contoso.com"], row.MtaStsMxHosts);
        Assert.Equal(31_557_600, row.MtaStsMaxAgeSeconds);
    }

    [Fact]
    public void MoreThanAThousandRows_IsRefused()
    {
        var csv = string.Join('\n', Enumerable.Range(1, ImportTable.MaximumDataRows + 1).Select(number => $"d{number}.com"));

        Assert.Throws<ImportInputException>(() => Table(csv));
    }

    [Fact]
    public void NothingToImport_IsRefused()
    {
        Assert.Throws<ImportInputException>(() => Table("# only a comment\n\n"));
        Assert.Throws<ImportInputException>(() => Table("domain,groups"));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ImportTableTests" -nologo -v q`
Expected: build FAILS, `ImportTable` not found.

- [ ] **Step 3: Write the value parser**

Create `src/DotMarc/DomainImport/ImportValueParser.cs`:

```csharp
using System.Globalization;
using DotMarc.Data;

namespace DotMarc.DomainImport;

/// <summary>Turns one cell into a value. A blank cell is null. A value that doesn't make sense is also null, with a
/// problem saying why, so the row still imports without it.</summary>
internal static class ImportValueParser
{
    public const int MaximumMaxAgeSeconds = 31_557_600;

    public static NameListCell? NameList(string cell)
    {
        var entries = SplitList(cell);
        if (entries.Count == 0)
        {
            return null;
        }

        var removals = entries.Where(entry => entry.StartsWith('-')).Select(entry => entry[1..].Trim()).Where(name => name.Length > 0).ToList();
        var names = entries.Where(entry => !entry.StartsWith('-')).ToList();
        return new NameListCell(names, removals);
    }

    public static string? Text(string cell) => cell.Length == 0 ? null : cell;

    public static bool? Monitored(string cell, List<string> problems) => cell.ToLowerInvariant() switch
    {
        "" => null,
        "yes" or "true" or "1" => true,
        "no" or "false" or "0" => false,
        _ => Refuse<bool?>(problems, $"Monitored \"{cell}\" should be yes or no, so it was left as it is.")
    };

    public static IReadOnlyList<string>? DkimSelectors(string cell, List<string> problems)
    {
        var selectors = SplitList(cell).Select(selector => selector.ToLowerInvariant()).ToList();
        if (selectors.Count == 0)
        {
            return null;
        }

        var invalid = selectors.FirstOrDefault(selector => !selector.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
        return invalid is null
            ? selectors
            : Refuse<IReadOnlyList<string>?>(problems, $"DKIM selector \"{invalid}\" can only contain letters, digits, hyphens, underscores and dots, so the selectors were left as they are.");
    }

    public static MtaStsImportMode? MtaStsMode(string cell, List<string> problems) => cell.ToLowerInvariant() switch
    {
        "" => null,
        "off" => MtaStsImportMode.Off,
        "none" => MtaStsImportMode.None,
        "testing" => MtaStsImportMode.Testing,
        "enforce" => MtaStsImportMode.Enforce,
        _ => Refuse<MtaStsImportMode?>(problems, $"MTA-STS mode \"{cell}\" should be off, none, testing or enforce, so it was left as it is.")
    };

    public static IReadOnlyList<string>? MxHosts(string cell, List<string> problems)
    {
        var hosts = SplitList(cell);
        if (hosts.Count == 0)
        {
            return null;
        }

        var normalized = new List<string>();
        foreach (var host in hosts)
        {
            // MTA-STS allows a leading wildcard label, such as *.mail.contoso.com.
            var wildcard = host.StartsWith("*.", StringComparison.Ordinal);
            if (!DomainNameValidator.TryNormalize(wildcard ? host[2..] : host, out var hostName))
            {
                return Refuse<IReadOnlyList<string>?>(problems, $"MX host \"{host}\" isn't a host name, so the MX hosts were left as they are.");
            }

            normalized.Add(wildcard ? "*." + hostName : hostName);
        }

        return normalized;
    }

    public static int? MaxAge(string cell, List<string> problems)
    {
        if (cell.Length == 0)
        {
            return null;
        }

        return int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= MaximumMaxAgeSeconds
            ? seconds
            : Refuse<int?>(problems, $"MTA-STS max age \"{cell}\" should be a number of seconds from 1 to {MaximumMaxAgeSeconds:N0}, so it was left as it is.");
    }

    private static List<string> SplitList(string cell) =>
        cell.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static T Refuse<T>(List<string> problems, string problem)
    {
        problems.Add(problem);
        return default!;
    }
}
```

- [ ] **Step 4: Write the table**

Create `src/DotMarc/DomainImport/ImportTable.cs`:

```csharp
namespace DotMarc.DomainImport;

public enum ImportColumn { Domain, Groups, Tags, HaloClient, Monitored, DkimSelectors, MtaStsMode, MtaStsMxHosts, MtaStsMaxAge }

public enum MtaStsImportMode { Off, None, Testing, Enforce }

/// <summary>A groups or tags cell: names to add, and names written as <c>-Name</c> to remove.</summary>
public sealed record NameListCell(IReadOnlyList<string> Names, IReadOnlyList<string> Removals)
{
    /// <summary>Two rows for the same domain: their names and removals combined.</summary>
    public static NameListCell? Combine(NameListCell? first, NameListCell? second) =>
        (first, second) switch
        {
            (null, _) => second,
            (_, null) => first,
            _ => new NameListCell(
                first.Names.Concat(second.Names).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                first.Removals.Concat(second.Removals).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        };
}

/// <summary>One row's values. Null means the cell was blank, the column is missing, or the value was refused, in which
/// case <see cref="Problems"/> says why.</summary>
public sealed record ImportTableRow(
    int LineNumber,
    string RawDomain,
    NameListCell? Groups,
    NameListCell? Tags,
    string? HaloClient,
    bool? Monitored,
    IReadOnlyList<string>? DkimSelectors,
    MtaStsImportMode? MtaStsMode,
    IReadOnlyList<string>? MtaStsMxHosts,
    int? MtaStsMaxAgeSeconds,
    IReadOnlyList<string> Problems);

/// <summary>The input as typed rows: which columns it has, the rows, and warnings about the columns.</summary>
public sealed record ImportTable(IReadOnlySet<ImportColumn> Columns, IReadOnlyList<ImportTableRow> Rows, IReadOnlyList<string> Warnings)
{
    public const int MaximumDataRows = 1000;

    private static readonly ImportColumn[] PositionalOrder = Enum.GetValues<ImportColumn>();

    private static readonly Dictionary<string, ImportColumn> HeaderNames = new(StringComparer.Ordinal)
    {
        ["domain"] = ImportColumn.Domain,
        ["groups"] = ImportColumn.Groups, ["group"] = ImportColumn.Groups,
        ["tags"] = ImportColumn.Tags, ["tag"] = ImportColumn.Tags,
        ["haloclient"] = ImportColumn.HaloClient, ["halo"] = ImportColumn.HaloClient,
        ["monitored"] = ImportColumn.Monitored,
        ["dkimselectors"] = ImportColumn.DkimSelectors, ["dkim"] = ImportColumn.DkimSelectors,
        ["mtastsmode"] = ImportColumn.MtaStsMode, ["mtasts"] = ImportColumn.MtaStsMode,
        ["mtastsmxhosts"] = ImportColumn.MtaStsMxHosts, ["mxhosts"] = ImportColumn.MtaStsMxHosts,
        ["mtastsmaxage"] = ImportColumn.MtaStsMaxAge, ["maxage"] = ImportColumn.MtaStsMaxAge,
    };

    public static ImportTable FromRows(IReadOnlyList<ImportRow> rows)
    {
        var warnings = new List<string>();
        // A header row names its columns, one of which is "domain". Data never holds that, since a domain has a dot.
        var hasHeader = rows.Count > 0 && rows[0].Cells.Any(cell => HeaderKey(cell) == "domain");
        var dataRows = hasHeader ? rows.Skip(1).ToList() : rows.ToList();

        if (dataRows.Count == 0)
        {
            throw new ImportInputException("There's nothing to import. Paste a list of domains, or choose a file.");
        }

        if (dataRows.Count > MaximumDataRows)
        {
            throw new ImportInputException($"There are {dataRows.Count:N0} rows, and an import can have at most {MaximumDataRows:N0}. Split it into smaller imports.");
        }

        // Which input column holds each ImportColumn.
        var columnIndexes = new Dictionary<ImportColumn, int>();
        if (hasHeader)
        {
            for (var index = 0; index < rows[0].Cells.Count; index++)
            {
                var header = rows[0].Cells[index];
                if (header.Length == 0)
                {
                    continue;
                }

                if (!HeaderNames.TryGetValue(HeaderKey(header), out var column))
                {
                    warnings.Add($"The column \"{header}\" isn't one dotMARC knows, so it was ignored.");
                }
                else if (!columnIndexes.TryAdd(column, index))
                {
                    warnings.Add($"The column \"{header}\" appears more than once, so only the first was used.");
                }
            }
        }
        else
        {
            var widest = dataRows.Max(row => row.Cells.Count);
            for (var index = 0; index < Math.Min(widest, PositionalOrder.Length); index++)
            {
                columnIndexes[PositionalOrder[index]] = index;
            }

            if (widest > PositionalOrder.Length)
            {
                warnings.Add($"Without a header row only the first {PositionalOrder.Length} columns are read, so the rest were ignored.");
            }
        }

        var typedRows = dataRows.Select(row => ToTableRow(row, columnIndexes)).ToList();
        return new ImportTable(columnIndexes.Keys.ToHashSet(), typedRows, warnings);
    }

    private static ImportTableRow ToTableRow(ImportRow row, Dictionary<ImportColumn, int> columnIndexes)
    {
        var problems = new List<string>();
        string Cell(ImportColumn column) =>
            columnIndexes.TryGetValue(column, out var index) && index < row.Cells.Count ? row.Cells[index] : "";

        return new ImportTableRow(
            row.LineNumber,
            Cell(ImportColumn.Domain),
            ImportValueParser.NameList(Cell(ImportColumn.Groups)),
            ImportValueParser.NameList(Cell(ImportColumn.Tags)),
            ImportValueParser.Text(Cell(ImportColumn.HaloClient)),
            ImportValueParser.Monitored(Cell(ImportColumn.Monitored), problems),
            ImportValueParser.DkimSelectors(Cell(ImportColumn.DkimSelectors), problems),
            ImportValueParser.MtaStsMode(Cell(ImportColumn.MtaStsMode), problems),
            ImportValueParser.MxHosts(Cell(ImportColumn.MtaStsMxHosts), problems),
            ImportValueParser.MaxAge(Cell(ImportColumn.MtaStsMaxAge), problems),
            problems);
    }

    /// <summary>A header compared ignoring case, spaces, hyphens and underscores, so "MTA-STS Max Age" matches.</summary>
    private static string HeaderKey(string header) =>
        new(header.ToLowerInvariant().Where(character => character is not (' ' or '-' or '_')).ToArray());
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ImportTableTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/DotMarc/DomainImport test/DotMarc.Tests/DomainImport
git commit -m "Map import columns and read each value, explaining any that don't make sense"
```

---

### Task 6: Suggesting existing names for typos

**Files:**
- Modify: `src/DotMarc/Notifications/HaloGroupSuggestions.cs` (expose `LooseKey`)
- Create: `src/DotMarc/DomainImport/NameMatcher.cs`
- Test: `test/DotMarc.Tests/DomainImport/NameMatcherTests.cs`, `test/DotMarc.Tests/Notifications/HaloGroupSuggestionsTests.cs`

**Interfaces:**
- Produces: `HaloGroupSuggestions.LooseKey(string name)`; `NameMatcher.FindExisting(string name, IEnumerable<string> existingNames)`
  returning the existing name ignoring case, or null; `NameMatcher.IsSameName(string first, string second)`;
  `NameMatcher.Suggest(string name, IEnumerable<string> existingNames, int maximum = 3)`.

- [ ] **Step 1: Write the failing tests**

Add to `HaloGroupSuggestionsTests`:

```csharp
    [Fact]
    public void LooseKey_IgnoresCasePunctuationAndCompanySuffixes()
    {
        Assert.Equal("compute bridgend", HaloGroupSuggestions.LooseKey("Compute (Bridgend) Limited"));
        Assert.Equal(HaloGroupSuggestions.LooseKey("Contoso Ltd."), HaloGroupSuggestions.LooseKey("contoso"));
    }
```

Create `test/DotMarc.Tests/DomainImport/NameMatcherTests.cs`:

```csharp
using DotMarc.DomainImport;
using Xunit;

namespace DotMarc.Tests.DomainImport;

public sealed class NameMatcherTests
{
    [Fact]
    public void FindExisting_IgnoresCase()
    {
        Assert.Equal("Client A", NameMatcher.FindExisting("client a", ["Client A", "Client B"]));
        Assert.Null(NameMatcher.FindExisting("Client C", ["Client A"]));
    }

    [Fact]
    public void IsSameName_IgnoresCasePunctuationAndSuffixes_ButNotTypos()
    {
        Assert.True(NameMatcher.IsSameName("contoso ltd.", "Contoso Limited"));
        Assert.False(NameMatcher.IsSameName("Client C", "Client A"));
    }

    [Fact]
    public void ALooseMatch_IsSuggestedFirst()
    {
        Assert.Equal(["Contoso Limited"], NameMatcher.Suggest("contoso", ["Fabrikam", "Contoso Limited", "Contoso Europe"]));
    }

    [Theory]
    [InlineData("Nortwind Traders", "Northwind Traders")]
    [InlineData("Fabrikan", "Fabrikam")]
    [InlineData("Northwnd Tradrs", "Northwind Traders")]
    public void OneOrTwoLetterTypos_AreSuggested(string typed, string expected)
    {
        Assert.Equal([expected], NameMatcher.Suggest(typed, ["Northwind Traders", "Fabrikam"]));
    }

    [Fact]
    public void UnrelatedAndShortNames_AreNotSuggested()
    {
        Assert.Empty(NameMatcher.Suggest("Woodgrove", ["Northwind Traders", "Fabrikam"]));
        Assert.Empty(NameMatcher.Suggest("Ops", ["Dev", "QA"]));
    }

    [Fact]
    public void AtMostThree_BestFirst()
    {
        var suggestions = NameMatcher.Suggest("Client", ["Clients", "Client Ltd", "Clien", "Cliant", "Clxxnt"]);

        Assert.Equal(3, suggestions.Count);
        Assert.Equal("Client Ltd", suggestions[0]);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~NameMatcherTests|FullyQualifiedName~HaloGroupSuggestionsTests" -nologo -v q`
Expected: build FAILS, `NameMatcher` and `LooseKey` not found.

- [ ] **Step 3: Write it**

In `HaloGroupSuggestions`, after `NamesMatch`, add:

```csharp
    /// <summary>A name reduced for loose comparison: lower-cased words, punctuation dropped, trailing company suffixes
    /// such as "Limited" removed. Two names with the same key are the same name written differently.</summary>
    public static string LooseKey(string name) => string.Join(' ', Tokenise(name));
```

Create `src/DotMarc/DomainImport/NameMatcher.cs`:

```csharp
using DotMarc.Notifications;

namespace DotMarc.DomainImport;

/// <summary>Finds which existing group, tag or Halo client a name in an import means, and suggests the closest ones when
/// it's unknown, to catch typos.</summary>
public static class NameMatcher
{
    private const int MaximumEdits = 2;
    private const int ShortestNameForTypos = 4;

    public static string? FindExisting(string name, IEnumerable<string> existingNames) =>
        existingNames.FirstOrDefault(existing => string.Equals(existing, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>True when two names are the same name written differently: case, punctuation, spacing, or a company
    /// suffix such as "Ltd".</summary>
    public static bool IsSameName(string first, string second)
    {
        var firstKey = HaloGroupSuggestions.LooseKey(first);
        return firstKey.Length > 0 && firstKey == HaloGroupSuggestions.LooseKey(second);
    }

    /// <summary>Close existing names, best first: the same name written differently (case, punctuation, "Ltd"), then
    /// names within one or two letters of it. Names shorter than four letters only match the first way, or every short
    /// name would look like a typo of every other.</summary>
    public static IReadOnlyList<string> Suggest(string name, IEnumerable<string> existingNames, int maximum = 3)
    {
        var key = HaloGroupSuggestions.LooseKey(name);
        var lowered = name.Trim().ToLowerInvariant();

        return existingNames
            .Select(existing => (Name: existing, Score: Score(key, lowered, existing)))
            .Where(candidate => candidate.Score is not null)
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
            .Select(candidate => candidate.Name)
            .ToList();
    }

    private static int? Score(string key, string lowered, string existing)
    {
        if (key.Length > 0 && key == HaloGroupSuggestions.LooseKey(existing))
        {
            return 0;
        }

        var existingLowered = existing.ToLowerInvariant();
        if (Math.Min(lowered.Length, existingLowered.Length) < ShortestNameForTypos)
        {
            return null;
        }

        var edits = EditDistance(lowered, existingLowered);
        return edits <= MaximumEdits ? edits : null;
    }

    /// <summary>The number of single-letter inserts, deletes or changes that turn one name into the other.</summary>
    internal static int EditDistance(string first, string second)
    {
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        var current = new int[second.Length + 1];
        for (var firstIndex = 1; firstIndex <= first.Length; firstIndex++)
        {
            current[0] = firstIndex;
            for (var secondIndex = 1; secondIndex <= second.Length; secondIndex++)
            {
                var substitution = previous[secondIndex - 1] + (first[firstIndex - 1] == second[secondIndex - 1] ? 0 : 1);
                current[secondIndex] = Math.Min(substitution, Math.Min(previous[secondIndex] + 1, current[secondIndex - 1] + 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[second.Length];
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~NameMatcherTests|FullyQualifiedName~HaloGroupSuggestionsTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/DotMarc/Notifications/HaloGroupSuggestions.cs src/DotMarc/DomainImport/NameMatcher.cs test/DotMarc.Tests
git commit -m "Suggest existing names for unknown or mistyped ones in an import"
```

---

### Task 7: Loading what the import needs from the database

**Files:**
- Create: `src/DotMarc/DomainImport/ImportSnapshot.cs`
- Create: `test/DotMarc.Tests/Internal/FakeMxHostsLookup.cs`
- Test: `test/DotMarc.Tests/DomainImport/ImportSnapshotLoaderTests.cs`

**Interfaces:**
- Consumes: Task 5's `ImportTable`; Task 1's `DomainNameValidator`; `IMxHostsLookup` (`DotMarc.MtaSts`); `HaloClient`
  (`DotMarc.Notifications`, `record HaloClient(int Id, string Name)`).
- Produces:
  - `record ExistingDomain(int Id, string Name, IReadOnlyList<string> Groups, IReadOnlyList<string> Tags, int? HaloClientId, bool IsMonitored, IReadOnlyList<string> DkimSelectors, bool MtaStsEnabled, MtaStsMode MtaStsMode, IReadOnlyList<string> MtaStsMxHosts, int MtaStsMaxAgeSeconds)`
  - `record ImportSnapshot(IReadOnlyDictionary<string, ExistingDomain> DomainsByName, IReadOnlyList<string> GroupNames, IReadOnlyList<string> TagNames, IReadOnlyList<HaloClient>? HaloClients, string? HaloUnavailableReason, IReadOnlyDictionary<string, IReadOnlyList<string>> LookedUpMxHosts)`
    (`HaloClients` null means Halo isn't usable, with the reason).
  - `static class ImportSnapshotLoader` with
    `Task<ImportSnapshot> LoadAsync(DotMarcDbContext context, ImportTable table, IReadOnlyList<HaloClient>? haloClients, string? haloUnavailableReason, IMxHostsLookup mxHostsLookup, CancellationToken cancellationToken)`.
  - Test helper `FakeMxHostsLookup` with a `Dictionary<string, List<string>> HostsByDomain` and a `List<string> LookedUp`.

- [ ] **Step 1: Write the fake and the failing tests**

Create `test/DotMarc.Tests/Internal/FakeMxHostsLookup.cs`:

```csharp
using DotMarc.MtaSts;

namespace DotMarc.Tests.Internal;

internal sealed class FakeMxHostsLookup : IMxHostsLookup
{
    public Dictionary<string, List<string>> HostsByDomain { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> LookedUp { get; } = [];

    public Task<List<string>> LookupAsync(string domainName, CancellationToken cancellationToken)
    {
        lock (LookedUp)
        {
            LookedUp.Add(domainName);
        }

        return Task.FromResult(HostsByDomain.TryGetValue(domainName, out var hosts) ? hosts : []);
    }
}
```

Create `test/DotMarc.Tests/DomainImport/ImportSnapshotLoaderTests.cs` with the usual Postgres boilerplate (as in
`test/DotMarc.Tests/Audit/AuditLogTests.cs`: `[Collection("Postgres")]`, `IAsyncLifetime`, a fresh migrated database,
`CreateContext()`), then:

```csharp
    private static ImportTable Table(string csv) => ImportTable.FromRows(CsvImportReader.Read(csv));

    [Fact]
    public async Task LoadsTheImportsDomains_WithTheirGroupsTagsAndSettings()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "unrelated.com");
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Client A", CancellationToken.None);
            await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", MudBlazor.Color.Primary);
            var domainId = (await context.Domains.SingleAsync(domain => domain.Name == "contoso.com")).Id;
            await GroupManagementService.SetDomainGroupsAsync(context, TestActors.Admin, domainId, [(await context.Groups.SingleAsync()).Id]);
            await DomainManagementService.SetDkimSelectorsAsync(context, TestActors.Admin, domainId, ["selector1"]);
        }

        await using var loadContext = CreateContext();
        var snapshot = await ImportSnapshotLoader.LoadAsync(loadContext, Table("Contoso.com\nnew.com"), null, "HaloPSA isn't connected.", new FakeMxHostsLookup(), CancellationToken.None);

        var contoso = Assert.Single(snapshot.DomainsByName).Value;
        Assert.Equal("contoso.com", contoso.Name);
        Assert.Equal(["Client A"], contoso.Groups);
        Assert.Equal(["selector1"], contoso.DkimSelectors);
        Assert.Equal(["Client A"], snapshot.GroupNames);
        Assert.Equal(["primary"], snapshot.TagNames);
        Assert.Null(snapshot.HaloClients);
        Assert.Equal("HaloPSA isn't connected.", snapshot.HaloUnavailableReason);
    }

    [Fact]
    public async Task LooksUpMxHosts_OnlyForDomainsTurningMtaStsOnWithoutAny()
    {
        var lookup = new FakeMxHostsLookup();
        lookup.HostsByDomain["needs.com"] = ["mail.needs.com"];

        await using var context = CreateContext();
        var snapshot = await ImportSnapshotLoader.LoadAsync(context,
            Table("domain,mta-sts mode,mx hosts\nneeds.com,testing,\ngiven.com,enforce,mail.given.com\noff.com,off,\nnothing.com,,"),
            null, null, lookup, CancellationToken.None);

        Assert.Equal(["needs.com"], lookup.LookedUp);
        Assert.Equal(["mail.needs.com"], snapshot.LookedUpMxHosts["needs.com"]);
    }
```

(Needed usings: `DotMarc.Data`, `DotMarc.DomainImport`, `DotMarc.Tests.Internal`, `Microsoft.EntityFrameworkCore`, `Xunit`.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ImportSnapshotLoaderTests" -nologo -v q`
Expected: build FAILS, `ImportSnapshotLoader` not found.

- [ ] **Step 3: Write the snapshot and loader**

Create `src/DotMarc/DomainImport/ImportSnapshot.cs`:

```csharp
using DotMarc.Data;
using DotMarc.MtaSts;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DomainImport;

/// <summary>A domain the import names that is already monitored, as it was when the preview was built.</summary>
public sealed record ExistingDomain(
    int Id,
    string Name,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Tags,
    int? HaloClientId,
    bool IsMonitored,
    IReadOnlyList<string> DkimSelectors,
    bool MtaStsEnabled,
    MtaStsMode MtaStsMode,
    IReadOnlyList<string> MtaStsMxHosts,
    int MtaStsMaxAgeSeconds);

/// <summary>Everything the planner needs from outside the input, loaded once, so re-planning when the person changes a
/// choice is instant and needs no database.</summary>
public sealed record ImportSnapshot(
    IReadOnlyDictionary<string, ExistingDomain> DomainsByName,
    IReadOnlyList<string> GroupNames,
    IReadOnlyList<string> TagNames,
    IReadOnlyList<HaloClient>? HaloClients,
    string? HaloUnavailableReason,
    IReadOnlyDictionary<string, IReadOnlyList<string>> LookedUpMxHosts);

public static class ImportSnapshotLoader
{
    private const int ParallelMxLookups = 8;

    public static async Task<ImportSnapshot> LoadAsync(DotMarcDbContext context, ImportTable table, IReadOnlyList<HaloClient>? haloClients,
        string? haloUnavailableReason, IMxHostsLookup mxHostsLookup, CancellationToken cancellationToken)
    {
        var validNames = table.Rows
            .Select(row => DomainNameValidator.TryNormalize(row.RawDomain, out var name) ? name : null)
            .OfType<string>()
            .Distinct()
            .ToList();

        var domains = await context.Domains
            .AsNoTracking()
            .Where(domain => validNames.Contains(domain.Name))
            .Include(domain => domain.Groups)
            .Include(domain => domain.Tags)
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var domainsByName = domains.ToDictionary(
            domain => domain.Name,
            domain => new ExistingDomain(domain.Id, domain.Name,
                domain.Groups.Select(group => group.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
                domain.Tags.Select(tag => tag.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
                domain.HaloClientId, domain.IsMonitored, domain.DkimSelectors, domain.MtaStsEnabled, domain.MtaStsMode,
                domain.MtaStsMxHosts, domain.MtaStsMaxAgeSeconds));

        var groupNames = await context.Groups.AsNoTracking().OrderBy(group => group.Name).Select(group => group.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var tagNames = await context.Tags.AsNoTracking().OrderBy(tag => tag.Name).Select(tag => tag.Name).ToListAsync(cancellationToken).ConfigureAwait(false);

        var lookedUp = await LookUpMxHostsAsync(table, domainsByName, mxHostsLookup, cancellationToken).ConfigureAwait(false);
        return new ImportSnapshot(domainsByName, groupNames, tagNames, haloClients, haloUnavailableReason, lookedUp);
    }

    /// <summary>MX hosts from DNS for the domains that turn MTA-STS on without any, as the domain page's Enable button
    /// does. A failed lookup counts as none found.</summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> LookUpMxHostsAsync(ImportTable table,
        IReadOnlyDictionary<string, ExistingDomain> domainsByName, IMxHostsLookup mxHostsLookup, CancellationToken cancellationToken)
    {
        var needed = table.Rows
            .Where(row => row.MtaStsMode is MtaStsImportMode.None or MtaStsImportMode.Testing or MtaStsImportMode.Enforce && row.MtaStsMxHosts is null)
            .Select(row => DomainNameValidator.TryNormalize(row.RawDomain, out var name) ? name : null)
            .OfType<string>()
            .Where(name => !domainsByName.TryGetValue(name, out var existing) || existing.MtaStsMxHosts.Count == 0)
            .Distinct()
            .ToList();

        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>>();
        await Parallel.ForEachAsync(needed, new ParallelOptions { MaxDegreeOfParallelism = ParallelMxLookups, CancellationToken = cancellationToken },
            async (domainName, token) =>
            {
                try
                {
                    found[domainName] = await mxHostsLookup.LookupAsync(domainName, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    found[domainName] = [];
                }
            }).ConfigureAwait(false);

        return found;
    }
}
```

(The `&&` in the first `Where` binds after the `is` pattern, so it reads as "(mode is one of those) and (no MX hosts
given)"; wrap it in brackets if that is clearer to you.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ImportSnapshotLoaderTests" -nologo -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/DotMarc/DomainImport/ImportSnapshot.cs test/DotMarc.Tests
git commit -m "Load the domains, groups, tags and MX hosts an import needs"
```

---

### Task 8: The planner

**Files:**
- Create: `src/DotMarc/DomainImport/ImportPlan.cs`
- Create: `src/DotMarc/DomainImport/DomainImportPlanner.cs`
- Test: `test/DotMarc.Tests/DomainImport/DomainImportPlannerTests.cs`

**Interfaces:**
- Consumes: Tasks 5 to 7 (`ImportTable`, `ImportTableRow`, `NameListCell`, `MtaStsImportMode`, `NameMatcher`,
  `ImportSnapshot`, `ExistingDomain`); `AuditChanges` and `AuditFieldChange` from `DotMarc.Audit`; `MtaStsMode` from
  `DotMarc.Data`.
- Produces (all in `ImportPlan.cs`):
  - `enum ExistingDomainMode { Skip, Add, Match }`, `enum ImportRowStatus { New, AlreadyMonitored, Duplicate, Invalid }`,
    `enum ImportNameKind { Group, Tag, HaloClient }`, `enum NameChoice { Create, MapTo, LeaveOut }`.
  - `record NameKey(ImportNameKind Kind, string LoweredName)`, `record NameResolution(NameChoice Choice, string? MapTo = null)`.
  - `record UnknownName(ImportNameKind Kind, string Name, IReadOnlyList<int> LineNumbers, IReadOnlyList<string> Suggestions, bool CanCreate, NameResolution Resolution)`
    with `NameKey Key` (Resolution is the one in effect).
  - `record ImportPermissions(bool CanEditDomains, bool CanManageMtaSts, bool CanAddGroups, bool CanAddTags)` with
    `static ImportPermissions All`.
  - `record NameSetChange(IReadOnlyList<string> Add, IReadOnlyList<string> Remove, bool ReplaceAll)` with
    `IReadOnlyList<string> ApplyTo(IEnumerable<string> current)`.
  - `record MtaStsTarget(bool Enabled, MtaStsMode Mode, IReadOnlyList<string> MxHosts, int MaxAgeSeconds)`.
  - `record DomainTarget(NameSetChange? Groups, NameSetChange? Tags, bool SetHaloClient, int? HaloClientId, bool? Monitored, IReadOnlyList<string>? DkimSelectors, MtaStsTarget? MtaSts)`.
  - `record PlannedRow(int LineNumber, string RawDomain, string? Domain, ImportRowStatus Status, string? InvalidReason, int? ExistingDomainId, int? MergedIntoLine, DomainTarget? Target, IReadOnlyList<AuditFieldChange> Changes, IReadOnlyList<string> Removals, IReadOnlyList<string> Notes)`.
  - `record ImportPlan(ExistingDomainMode Mode, IReadOnlyList<PlannedRow> Rows, IReadOnlyList<UnknownName> UnknownNames, IReadOnlyList<string> GroupsToCreate, IReadOnlyList<string> TagsToCreate, IReadOnlyList<string> Notices)`
    with counts `NewCount`, `UpdateCount`, `UnchangedCount`, `SkippedExistingCount`, `DuplicateCount`, `InvalidCount`.
  - `DomainImportPlanner.Plan(ImportTable table, ImportSnapshot snapshot, ExistingDomainMode mode, ImportPermissions permissions, IReadOnlyDictionary<NameKey, NameResolution>? resolutions = null)`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/DomainImport/DomainImportPlannerTests.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.DomainImport;
using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.DomainImport;

public sealed class DomainImportPlannerTests
{
    private static ImportTable Table(string csv) => ImportTable.FromRows(CsvImportReader.Read(csv));

    private static ExistingDomain Existing(string name, IReadOnlyList<string>? groups = null, bool monitored = true,
        bool mtaStsEnabled = false, IReadOnlyList<string>? mxHosts = null) =>
        new(Math.Abs(name.GetHashCode()) % 10_000 + 1, name, groups ?? [], [], null, monitored, [], mtaStsEnabled, MtaStsMode.Testing, mxHosts ?? [], 604_800);

    private static ImportSnapshot Snapshot(IReadOnlyList<ExistingDomain>? domains = null, IReadOnlyList<HaloClient>? haloClients = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? lookedUpMx = null) =>
        new((domains ?? []).ToDictionary(domain => domain.Name),
            ["Client A", "Client B", "Contoso Limited"], ["primary", "europe"],
            haloClients, haloClients is null ? "HaloPSA isn't connected." : null,
            lookedUpMx ?? new Dictionary<string, IReadOnlyList<string>>());

    private static ImportPlan Plan(string csv, ImportSnapshot? snapshot = null, ExistingDomainMode mode = ExistingDomainMode.Add,
        ImportPermissions? permissions = null, Dictionary<NameKey, NameResolution>? resolutions = null) =>
        DomainImportPlanner.Plan(Table(csv), snapshot ?? Snapshot(), mode, permissions ?? ImportPermissions.All, resolutions);

    private static PlannedRow Row(ImportPlan plan, string domain) => plan.Rows.Single(row => row.Domain == domain && row.Status != ImportRowStatus.Duplicate);

    [Fact]
    public void Rows_GetNewExistingAndInvalidStatuses()
    {
        var plan = Plan("new.com\nold.com\nhttps://bad.com/", Snapshot([Existing("old.com")]));

        Assert.Equal([ImportRowStatus.New, ImportRowStatus.AlreadyMonitored, ImportRowStatus.Invalid], plan.Rows.Select(row => row.Status));
        Assert.Contains("web address", plan.Rows[2].InvalidReason);
        Assert.Equal((1, 1), (plan.NewCount, plan.InvalidCount));
    }

    [Fact]
    public void TheSameDomainWrittenTwoWays_IsOneDomain()
    {
        var plan = Plan("domain,groups,monitored\nContoso.com,Client A,yes\ncontoso.com.,Client B,no");

        Assert.Equal([ImportRowStatus.New, ImportRowStatus.Duplicate], plan.Rows.Select(row => row.Status));
        Assert.Equal(2, plan.Rows[1].MergedIntoLine);
        var target = plan.Rows[0].Target!;
        Assert.Equal(["Client A", "Client B"], target.Groups!.Add);
        Assert.False(target.Monitored);
    }

    [Fact]
    public void SkipMode_LeavesExistingDomainsAlone()
    {
        var plan = Plan("domain,groups\nold.com,Client A", Snapshot([Existing("old.com")]), ExistingDomainMode.Skip);

        var row = plan.Rows.Single();
        Assert.Null(row.Target);
        Assert.Empty(row.Changes);
        Assert.Equal(1, plan.SkippedExistingCount);
    }

    [Fact]
    public void AddMode_AddsGroups_AndRemovesDashEntries_ShowingTheRemoval()
    {
        var plan = Plan("domain,groups\nold.com,Client B;-Client A", Snapshot([Existing("old.com", groups: ["Client A"])]), ExistingDomainMode.Add);

        var row = plan.Rows.Single();
        Assert.Equal([new AuditFieldChange("Groups", "Client A", "Client B")], row.Changes);
        Assert.Equal(["Group \"Client A\" removed"], row.Removals);
        Assert.Equal(1, plan.UpdateCount);
    }

    [Fact]
    public void MatchMode_ClearsGroupsForABlankCell_OnlyWhenTheColumnExists()
    {
        var existing = Snapshot([Existing("old.com", groups: ["Client A"])]);

        var withColumn = Plan("domain,groups\nold.com,", existing, ExistingDomainMode.Match).Rows.Single();
        var withoutColumn = Plan("domain,monitored\nold.com,yes", existing, ExistingDomainMode.Match).Rows.Single();

        Assert.Equal(["Group \"Client A\" removed"], withColumn.Removals);
        Assert.True(withColumn.Target!.Groups!.ReplaceAll);
        Assert.Empty(withoutColumn.Removals);
        Assert.Null(withoutColumn.Target);
    }

    [Fact]
    public void UnknownNames_AreListedOnce_AndACaseOnlyDifferenceIsKnown()
    {
        var plan = Plan("domain,groups,tags\na.com,client a;Client C,Primary\nb.com,Client C,");

        var unknown = Assert.Single(plan.UnknownNames);
        Assert.Equal((ImportNameKind.Group, "Client C", new[] { 2, 3 }), (unknown.Kind, unknown.Name, unknown.LineNumbers.ToArray()));
        Assert.Equal(["Client A"], plan.Rows[0].Target!.Groups!.Add.Where(name => name == "Client A"));
    }

    [Fact]
    public void AnUnknownName_DefaultsToItsClosestMatch_ThenCreate_ThenLeaveOut()
    {
        var plan = Plan("domain,groups\na.com,Contoso Ltd;Brand New", permissions: ImportPermissions.All);
        var withoutCreate = Plan("domain,groups\na.com,Brand New", permissions: ImportPermissions.All with { CanAddGroups = false });

        Assert.Equal(new NameResolution(NameChoice.MapTo, "Contoso Limited"), plan.UnknownNames.Single(name => name.Name == "Contoso Ltd").Resolution);
        Assert.Equal(NameChoice.Create, plan.UnknownNames.Single(name => name.Name == "Brand New").Resolution.Choice);
        Assert.Equal(["Brand New"], plan.GroupsToCreate);
        Assert.Equal(NameChoice.LeaveOut, withoutCreate.UnknownNames.Single().Resolution.Choice);
        Assert.False(withoutCreate.UnknownNames.Single().CanCreate);
    }

    [Fact]
    public void ANearMissTypo_IsSuggested_ButNotChosenForYou()
    {
        // "Client C" is one letter from "Client A", but they are different clients: suggest, don't map.
        var plan = Plan("domain,groups\na.com,Client C");

        var unknown = plan.UnknownNames.Single();
        Assert.Contains("Client A", unknown.Suggestions);
        Assert.Equal(NameChoice.Create, unknown.Resolution.Choice);
    }

    [Fact]
    public void AResolution_OverridesTheDefault()
    {
        var resolutions = new Dictionary<NameKey, NameResolution>
        {
            [new NameKey(ImportNameKind.Group, "contoso ltd")] = new(NameChoice.MapTo, "Client B"),
            [new NameKey(ImportNameKind.Group, "brand new")] = new(NameChoice.LeaveOut),
        };

        var plan = Plan("domain,groups\na.com,Contoso Ltd;Brand New", resolutions: resolutions);

        Assert.Equal(["Client B"], plan.Rows.Single().Target!.Groups!.Add);
        Assert.Empty(plan.GroupsToCreate);
    }

    [Fact]
    public void HaloClients_CantBeCreated_AndTheColumnIsIgnoredWithoutHalo()
    {
        var clients = new[] { new HaloClient(7, "Contoso Limited"), new HaloClient(8, "Fabrikam") };

        var connected = Plan("domain,halo client\na.com,Contoso Ltd\nb.com,Nobody", Snapshot(haloClients: clients));
        var notConnected = Plan("domain,halo client\na.com,Fabrikam");

        Assert.Equal(7, connected.Rows[0].Target!.HaloClientId);
        var unknownClient = connected.UnknownNames.Single(name => name.Name == "Nobody");
        Assert.False(unknownClient.CanCreate);
        Assert.Equal(NameChoice.LeaveOut, unknownClient.Resolution.Choice);
        Assert.Contains("HaloPSA isn't connected.", notConnected.Notices);
        Assert.False(notConnected.Rows.Single().Target!.SetHaloClient);
    }

    [Fact]
    public void Columns_AreIgnoredWithoutTheirPermission()
    {
        var plan = Plan("domain,groups,mta-sts mode,mx hosts\na.com,Client A,testing,mail.a.com",
            permissions: new ImportPermissions(CanEditDomains: false, CanManageMtaSts: false, CanAddGroups: true, CanAddTags: true));

        var target = plan.Rows.Single().Target!;
        Assert.Null(target.Groups);
        Assert.Null(target.MtaSts);
        Assert.Equal(2, plan.Notices.Count);
        Assert.Empty(plan.UnknownNames);
    }

    [Fact]
    public void MtaSts_TurnsOnWithLookedUpMxHosts_AndNotWhenNoneAreFound()
    {
        var snapshot = Snapshot(lookedUpMx: new Dictionary<string, IReadOnlyList<string>> { ["found.com"] = ["mail.found.com"], ["empty.com"] = [] });

        var plan = Plan("domain,mta-sts mode\nfound.com,enforce\nempty.com,testing", snapshot);

        Assert.Equal(new MtaStsTarget(true, MtaStsMode.Enforce, ["mail.found.com"], 604_800), Row(plan, "found.com").Target!.MtaSts, new MtaStsTargetComparer());
        Assert.Null(Row(plan, "empty.com").Target!.MtaSts);
        Assert.Contains(Row(plan, "empty.com").Notes, note => note.Contains("wasn't turned on"));
    }

    [Fact]
    public void TurningMonitoringAndMtaStsOff_OnAnExistingDomain_AreRemovals()
    {
        var plan = Plan("domain,monitored,mta-sts mode\nold.com,no,off",
            Snapshot([Existing("old.com", monitored: true, mtaStsEnabled: true, mxHosts: ["mail.old.com"])]));

        Assert.Equal(["Monitoring turned off", "MTA-STS turned off"], plan.Rows.Single().Removals);
    }

    [Fact]
    public void RemovingANameTheDomainDoesntHave_IsIgnoredWithANote()
    {
        var plan = Plan("domain,tags\nold.com,-europe", Snapshot([Existing("old.com")]));

        var row = plan.Rows.Single();
        Assert.Contains(row.Notes, note => note.Contains("nothing to remove"));
        Assert.Empty(row.Removals);
    }

    /// <summary>Records holding lists compare the lists by reference, so compare MTA-STS targets by value.</summary>
    private sealed class MtaStsTargetComparer : IEqualityComparer<MtaStsTarget?>
    {
        public bool Equals(MtaStsTarget? first, MtaStsTarget? second) =>
            first is not null && second is not null
            && (first.Enabled, first.Mode, first.MaxAgeSeconds) == (second.Enabled, second.Mode, second.MaxAgeSeconds)
            && first.MxHosts.SequenceEqual(second.MxHosts);

        public int GetHashCode(MtaStsTarget? target) => 0;
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainImportPlannerTests" -nologo -v q`
Expected: build FAILS, `DomainImportPlanner` and the plan types not found.

- [ ] **Step 3: Write the plan types**

Create `src/DotMarc/DomainImport/ImportPlan.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;

namespace DotMarc.DomainImport;

public enum ExistingDomainMode { Skip, Add, Match }

public enum ImportRowStatus { New, AlreadyMonitored, Duplicate, Invalid }

public enum ImportNameKind { Group, Tag, HaloClient }

public enum NameChoice { Create, MapTo, LeaveOut }

public sealed record NameKey(ImportNameKind Kind, string LoweredName);

public sealed record NameResolution(NameChoice Choice, string? MapTo = null);

/// <summary>A group, tag or Halo client name in the input that doesn't exist, with the choice in effect for it.</summary>
public sealed record UnknownName(ImportNameKind Kind, string Name, IReadOnlyList<int> LineNumbers, IReadOnlyList<string> Suggestions, bool CanCreate, NameResolution Resolution)
{
    public NameKey Key => new(Kind, Name.ToLowerInvariant());
}

public sealed record ImportPermissions(bool CanEditDomains, bool CanManageMtaSts, bool CanAddGroups, bool CanAddTags)
{
    public static ImportPermissions All { get; } = new(true, true, true, true);
}

/// <summary>How a domain's groups or tags change: names to add and remove, or, with <see cref="ReplaceAll"/>, the
/// complete list it should end up with.</summary>
public sealed record NameSetChange(IReadOnlyList<string> Add, IReadOnlyList<string> Remove, bool ReplaceAll)
{
    public IReadOnlyList<string> ApplyTo(IEnumerable<string> current) =>
        ReplaceAll
            ? Add.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : current.Where(name => !Remove.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Concat(Add)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
}

public sealed record MtaStsTarget(bool Enabled, MtaStsMode Mode, IReadOnlyList<string> MxHosts, int MaxAgeSeconds);

/// <summary>What importing does to one domain. A null part leaves that part alone.</summary>
public sealed record DomainTarget(
    NameSetChange? Groups,
    NameSetChange? Tags,
    bool SetHaloClient,
    int? HaloClientId,
    bool? Monitored,
    IReadOnlyList<string>? DkimSelectors,
    MtaStsTarget? MtaSts);

/// <summary>One input row in the preview: its status, what it will change, what it will remove (shown in red), and
/// notes such as values left out.</summary>
public sealed record PlannedRow(
    int LineNumber,
    string RawDomain,
    string? Domain,
    ImportRowStatus Status,
    string? InvalidReason,
    int? ExistingDomainId,
    int? MergedIntoLine,
    DomainTarget? Target,
    IReadOnlyList<AuditFieldChange> Changes,
    IReadOnlyList<string> Removals,
    IReadOnlyList<string> Notes);

public sealed record ImportPlan(
    ExistingDomainMode Mode,
    IReadOnlyList<PlannedRow> Rows,
    IReadOnlyList<UnknownName> UnknownNames,
    IReadOnlyList<string> GroupsToCreate,
    IReadOnlyList<string> TagsToCreate,
    IReadOnlyList<string> Notices)
{
    public int NewCount => Rows.Count(row => row.Status == ImportRowStatus.New);
    public int UpdateCount => Rows.Count(row => row.Status == ImportRowStatus.AlreadyMonitored && row.Target is not null);
    public int UnchangedCount => Rows.Count(row => row.Status == ImportRowStatus.AlreadyMonitored && row.Target is null && Mode != ExistingDomainMode.Skip);
    public int SkippedExistingCount => Rows.Count(row => row.Status == ImportRowStatus.AlreadyMonitored && Mode == ExistingDomainMode.Skip);
    public int DuplicateCount => Rows.Count(row => row.Status == ImportRowStatus.Duplicate);
    public int InvalidCount => Rows.Count(row => row.Status == ImportRowStatus.Invalid);
}
```

- [ ] **Step 4: Write the planner**

Create `src/DotMarc/DomainImport/DomainImportPlanner.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;

namespace DotMarc.DomainImport;

/// <summary>Works out what an import will do, row by row, without touching the database: a pure function of the input,
/// the snapshot, the existing-domains mode, the person's permissions and their choices for unknown names.</summary>
public static class DomainImportPlanner
{
    private const int DefaultMaxAgeSeconds = 604_800;

    public static ImportPlan Plan(ImportTable table, ImportSnapshot snapshot, ExistingDomainMode mode, ImportPermissions permissions,
        IReadOnlyDictionary<NameKey, NameResolution>? resolutions = null)
    {
        var notices = new List<string>();
        var usable = UsableColumns.For(table.Columns, snapshot, permissions, notices);

        // Normalise every row, and merge rows naming the same domain into the first one.
        var entries = new List<(ImportTableRow Row, string? Domain, string? InvalidReason, MergedRow? Primary)>();
        var primaries = new Dictionary<string, MergedRow>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            if (!DomainNameValidator.TryNormalize(row.RawDomain, out var domain, out var reason))
            {
                entries.Add((row, null, reason, null));
            }
            else if (primaries.TryGetValue(domain, out var primary))
            {
                primary.Merge(row);
                entries.Add((row, domain, null, primary));
            }
            else
            {
                var merged = new MergedRow(row, domain);
                primaries[domain] = merged;
                entries.Add((row, domain, null, null));
            }
        }

        var names = new NameResolver(snapshot, permissions, resolutions ?? new Dictionary<NameKey, NameResolution>());
        foreach (var primary in primaries.Values)
        {
            if (usable.Groups)
            {
                foreach (var name in primary.Groups?.Names ?? [])
                {
                    names.Consider(ImportNameKind.Group, name, primary.LineNumber);
                }
            }

            if (usable.Tags)
            {
                foreach (var name in primary.Tags?.Names ?? [])
                {
                    names.Consider(ImportNameKind.Tag, name, primary.LineNumber);
                }
            }

            if (usable.HaloClient && primary.HaloClient is { } haloClientName)
            {
                names.Consider(ImportNameKind.HaloClient, haloClientName, primary.LineNumber);
            }
        }

        var plannedRows = entries.Select(entry =>
        {
            if (entry.Domain is null)
            {
                return new PlannedRow(entry.Row.LineNumber, entry.Row.RawDomain, null, ImportRowStatus.Invalid, entry.InvalidReason,
                    null, null, null, [], [], entry.Row.Problems);
            }

            if (entry.Primary is { } mergedInto)
            {
                return new PlannedRow(entry.Row.LineNumber, entry.Row.RawDomain, entry.Domain, ImportRowStatus.Duplicate, null,
                    null, mergedInto.LineNumber, null, [], [], [$"The same domain as line {mergedInto.LineNumber}, so it was merged into that row."]);
            }

            var primaryRow = primaries[entry.Domain];
            snapshot.DomainsByName.TryGetValue(entry.Domain, out var existing);
            return new RowPlanner(primaryRow, existing, snapshot, mode, usable, table.Columns, names).Plan();
        }).ToList();

        return new ImportPlan(mode, plannedRows, names.UnknownNames(), names.ToCreate(ImportNameKind.Group), names.ToCreate(ImportNameKind.Tag), notices);
    }

    /// <summary>Which columns this import can use, given the input, the person's permissions and whether Halo is
    /// connected. Adds a notice for each column it has to ignore.</summary>
    private sealed record UsableColumns(bool Groups, bool Tags, bool HaloClient, bool Monitored, bool DkimSelectors, bool MtaSts)
    {
        public static UsableColumns For(IReadOnlySet<ImportColumn> columns, ImportSnapshot snapshot, ImportPermissions permissions, List<string> notices)
        {
            var editColumns = new[]
            {
                (Column: ImportColumn.Groups, Label: "Groups"), (ImportColumn.Tags, "Tags"), (ImportColumn.HaloClient, "Halo client"),
                (ImportColumn.Monitored, "Monitored"), (ImportColumn.DkimSelectors, "DKIM selectors"),
            }.Where(column => columns.Contains(column.Column)).Select(column => column.Label).ToList();

            if (!permissions.CanEditDomains && editColumns.Count > 0)
            {
                notices.Add($"The {JoinLabels(editColumns)} {(editColumns.Count == 1 ? "column was" : "columns were")} ignored: you don't have permission to change a domain's groups, tags and settings.");
            }

            var hasMtaStsColumns = columns.Overlaps([ImportColumn.MtaStsMode, ImportColumn.MtaStsMxHosts, ImportColumn.MtaStsMaxAge]);
            if (!permissions.CanManageMtaSts && hasMtaStsColumns)
            {
                notices.Add("The MTA-STS columns were ignored: you don't have permission to manage MTA-STS.");
            }

            var haloColumnUsable = permissions.CanEditDomains && columns.Contains(ImportColumn.HaloClient);
            if (haloColumnUsable && snapshot.HaloClients is null)
            {
                notices.Add(snapshot.HaloUnavailableReason ?? "HaloPSA isn't connected, so the Halo client column was ignored.");
            }

            return new UsableColumns(
                permissions.CanEditDomains && columns.Contains(ImportColumn.Groups),
                permissions.CanEditDomains && columns.Contains(ImportColumn.Tags),
                haloColumnUsable && snapshot.HaloClients is not null,
                permissions.CanEditDomains && columns.Contains(ImportColumn.Monitored),
                permissions.CanEditDomains && columns.Contains(ImportColumn.DkimSelectors),
                permissions.CanManageMtaSts && hasMtaStsColumns);
        }

        private static string JoinLabels(IReadOnlyList<string> labels) =>
            labels.Count == 1 ? labels[0] : string.Join(", ", labels.Take(labels.Count - 1)) + " and " + labels[^1];
    }

    /// <summary>The first row for a domain, with any later rows for it merged in: names combined, and for other values
    /// the later non-blank one wins.</summary>
    private sealed class MergedRow(ImportTableRow first, string domain)
    {
        public int LineNumber { get; } = first.LineNumber;
        public string RawDomain { get; } = first.RawDomain;
        public string Domain { get; } = domain;
        public NameListCell? Groups { get; private set; } = first.Groups;
        public NameListCell? Tags { get; private set; } = first.Tags;
        public string? HaloClient { get; private set; } = first.HaloClient;
        public bool? Monitored { get; private set; } = first.Monitored;
        public IReadOnlyList<string>? DkimSelectors { get; private set; } = first.DkimSelectors;
        public MtaStsImportMode? MtaStsMode { get; private set; } = first.MtaStsMode;
        public IReadOnlyList<string>? MtaStsMxHosts { get; private set; } = first.MtaStsMxHosts;
        public int? MtaStsMaxAgeSeconds { get; private set; } = first.MtaStsMaxAgeSeconds;
        public List<string> Notes { get; } = [.. first.Problems];

        public void Merge(ImportTableRow later)
        {
            Groups = NameListCell.Combine(Groups, later.Groups);
            Tags = NameListCell.Combine(Tags, later.Tags);
            HaloClient = later.HaloClient ?? HaloClient;
            Monitored = later.Monitored ?? Monitored;
            DkimSelectors = later.DkimSelectors ?? DkimSelectors;
            MtaStsMode = later.MtaStsMode ?? MtaStsMode;
            MtaStsMxHosts = later.MtaStsMxHosts ?? MtaStsMxHosts;
            MtaStsMaxAgeSeconds = later.MtaStsMaxAgeSeconds ?? MtaStsMaxAgeSeconds;
            Notes.AddRange(later.Problems.Select(problem => $"Line {later.LineNumber}: {problem}"));
        }
    }

    /// <summary>Tracks the unknown group, tag and Halo client names, and resolves any name to the one to use.</summary>
    private sealed class NameResolver(ImportSnapshot snapshot, ImportPermissions permissions, IReadOnlyDictionary<NameKey, NameResolution> chosen)
    {
        private readonly Dictionary<NameKey, (string Name, List<int> Lines)> _unknown = [];

        public IReadOnlyList<string> Existing(ImportNameKind kind) => kind switch
        {
            ImportNameKind.Group => snapshot.GroupNames,
            ImportNameKind.Tag => snapshot.TagNames,
            _ => snapshot.HaloClients?.Select(client => client.Name).ToList() ?? []
        };

        private bool CanCreate(ImportNameKind kind) => kind switch
        {
            ImportNameKind.Group => permissions.CanAddGroups,
            ImportNameKind.Tag => permissions.CanAddTags,
            _ => false
        };

        public void Consider(ImportNameKind kind, string name, int lineNumber)
        {
            if (NameMatcher.FindExisting(name, Existing(kind)) is not null)
            {
                return;
            }

            var key = new NameKey(kind, name.ToLowerInvariant());
            if (!_unknown.TryGetValue(key, out var entry))
            {
                entry = (name, []);
                _unknown[key] = entry;
            }

            if (!entry.Lines.Contains(lineNumber))
            {
                entry.Lines.Add(lineNumber);
            }
        }

        public IReadOnlyList<UnknownName> UnknownNames() =>
            _unknown.Select(pair => new UnknownName(pair.Key.Kind, pair.Value.Name, pair.Value.Lines,
                    NameMatcher.Suggest(pair.Value.Name, Existing(pair.Key.Kind)), CanCreate(pair.Key.Kind), Effective(pair.Key, pair.Value.Name)))
                .OrderBy(name => name.Kind).ThenBy(name => name.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public IReadOnlyList<string> ToCreate(ImportNameKind kind) =>
            _unknown.Where(pair => pair.Key.Kind == kind && Effective(pair.Key, pair.Value.Name).Choice == NameChoice.Create)
                .Select(pair => pair.Value.Name.Trim())
                .ToList();

        /// <summary>The name to use: the existing one (ignoring case), the mapped one, the name itself if it will be
        /// created, or null if it's left out.</summary>
        public string? Resolve(ImportNameKind kind, string name)
        {
            var existing = Existing(kind);
            if (NameMatcher.FindExisting(name, existing) is { } known)
            {
                return known;
            }

            var resolution = Effective(new NameKey(kind, name.ToLowerInvariant()), name);
            return resolution.Choice switch
            {
                NameChoice.Create => name.Trim(),
                NameChoice.MapTo => NameMatcher.FindExisting(resolution.MapTo!, existing),
                _ => null
            };
        }

        private NameResolution Effective(NameKey key, string name)
        {
            var existing = Existing(key.Kind);
            if (chosen.TryGetValue(key, out var choice)
                && (choice.Choice == NameChoice.LeaveOut
                    || (choice.Choice == NameChoice.Create && CanCreate(key.Kind))
                    || (choice.Choice == NameChoice.MapTo && choice.MapTo is not null && NameMatcher.FindExisting(choice.MapTo, existing) is not null)))
            {
                return choice;
            }

            // Only the same name written differently is mapped for you. A near-miss typo might be a genuinely different
            // name ("Client C" and "Client A"), so it is suggested and the person picks it.
            var sameName = existing.FirstOrDefault(candidate => NameMatcher.IsSameName(name, candidate));
            return sameName is not null ? new NameResolution(NameChoice.MapTo, sameName)
                : CanCreate(key.Kind) ? new NameResolution(NameChoice.Create)
                : new NameResolution(NameChoice.LeaveOut);
        }
    }

    /// <summary>Plans one domain: its target, and the changes, removals and notes the preview shows.</summary>
    private sealed class RowPlanner(MergedRow row, ExistingDomain? existing, ImportSnapshot snapshot, ExistingDomainMode mode,
        UsableColumns usable, IReadOnlySet<ImportColumn> columns, NameResolver names)
    {
        private readonly AuditChanges _changes = new();
        private readonly List<string> _removals = [];
        private readonly List<string> _notes = [.. row.Notes];

        public PlannedRow Plan()
        {
            var status = existing is null ? ImportRowStatus.New : ImportRowStatus.AlreadyMonitored;
            if (existing is not null && mode == ExistingDomainMode.Skip)
            {
                _notes.Add("Already monitored, so it's skipped.");
                return Result(status, null);
            }

            var groups = usable.Groups ? PlanNames(ImportNameKind.Group, "Groups", "group", row.Groups, existing?.Groups, columns.Contains(ImportColumn.Groups)) : null;
            var tags = usable.Tags ? PlanNames(ImportNameKind.Tag, "Tags", "tag", row.Tags, existing?.Tags, columns.Contains(ImportColumn.Tags)) : null;
            var (setHaloClient, haloClientId) = PlanHaloClient();
            var monitored = PlanMonitored();
            var dkimSelectors = PlanDkimSelectors();
            var mtaSts = PlanMtaSts();

            var target = new DomainTarget(groups, tags, setHaloClient, haloClientId, monitored, dkimSelectors, mtaSts);
            if (existing is not null && !_changes.Any)
            {
                _notes.Add("Already monitored, and there's nothing to change.");
                return Result(status, null);
            }

            return Result(status, target);
        }

        private PlannedRow Result(ImportRowStatus status, DomainTarget? target) =>
            new(row.LineNumber, row.RawDomain, row.Domain, status, null, existing?.Id, null, target, _changes.Items, _removals, _notes);

        private NameSetChange? PlanNames(ImportNameKind kind, string label, string noun, NameListCell? cell, IReadOnlyList<string>? current, bool columnPresent)
        {
            var isMatch = mode == ExistingDomainMode.Match && current is not null;
            if (cell is null && !(isMatch && columnPresent))
            {
                return null;
            }

            var add = (cell?.Names ?? []).Select(name => names.Resolve(kind, name)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var remove = new List<string>();
            if (cell is { Removals.Count: > 0 })
            {
                if (current is null)
                {
                    _notes.Add($"Entries starting with - are ignored for a new domain: it has no {noun}s to remove.");
                }
                else if (isMatch)
                {
                    _notes.Add("Entries starting with - are ignored when matching the file.");
                }
                else
                {
                    foreach (var removal in cell.Removals)
                    {
                        if (NameMatcher.FindExisting(removal, current) is { } found)
                        {
                            remove.Add(found);
                        }
                        else
                        {
                            _notes.Add($"\"{removal}\" isn't one of this domain's {noun}s, so there was nothing to remove.");
                        }
                    }
                }
            }

            var change = new NameSetChange(add, remove, isMatch);
            var before = current ?? [];
            var after = change.ApplyTo(before);
            _changes.Set(label, before, after);
            _removals.AddRange(before.Where(name => !after.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(name => $"{char.ToUpperInvariant(noun[0])}{noun[1..]} \"{name}\" removed"));
            return change;
        }

        private (bool SetHaloClient, int? HaloClientId) PlanHaloClient()
        {
            if (!usable.HaloClient || row.HaloClient is not { } requestedName)
            {
                return (false, null);
            }

            if (names.Resolve(ImportNameKind.HaloClient, requestedName) is not { } resolvedName)
            {
                _notes.Add($"Halo client \"{requestedName}\" was left out.");
                return (false, null);
            }

            var client = snapshot.HaloClients!.First(candidate => string.Equals(candidate.Name, resolvedName, StringComparison.OrdinalIgnoreCase));
            _changes.Field("Halo client", ClientName(existing?.HaloClientId), client.Name);
            return (true, client.Id);
        }

        private string? ClientName(int? clientId) =>
            clientId is null ? null : snapshot.HaloClients?.FirstOrDefault(client => client.Id == clientId)?.Name ?? $"Halo client {clientId}";

        private bool? PlanMonitored()
        {
            if (!usable.Monitored || row.Monitored is not { } monitored)
            {
                return null;
            }

            // A domain added by the import starts monitored.
            var before = existing?.IsMonitored ?? true;
            _changes.Field("Monitored", before, monitored);
            if (existing is not null && before && !monitored)
            {
                _removals.Add("Monitoring turned off");
            }

            return monitored;
        }

        private IReadOnlyList<string>? PlanDkimSelectors()
        {
            if (!usable.DkimSelectors || row.DkimSelectors is not { } selectors)
            {
                return null;
            }

            var before = existing?.DkimSelectors ?? [];
            _changes.Set("DKIM selectors", before, selectors);
            _removals.AddRange(before.Where(selector => !selectors.Contains(selector, StringComparer.OrdinalIgnoreCase)).Select(selector => $"DKIM selector \"{selector}\" removed"));
            return selectors;
        }

        private MtaStsTarget? PlanMtaSts()
        {
            if (!usable.MtaSts || (row.MtaStsMode is null && row.MtaStsMxHosts is null && row.MtaStsMaxAgeSeconds is null))
            {
                return null;
            }

            var currentEnabled = existing?.MtaStsEnabled ?? false;
            var currentMode = existing?.MtaStsMode ?? MtaStsMode.Testing;
            var currentMxHosts = existing?.MtaStsMxHosts ?? [];
            var currentMaxAge = existing?.MtaStsMaxAgeSeconds ?? DefaultMaxAgeSeconds;

            var enabled = row.MtaStsMode is { } requested ? requested != MtaStsImportMode.Off : currentEnabled;
            var modeAfter = row.MtaStsMode switch
            {
                MtaStsImportMode.None => MtaStsMode.None,
                MtaStsImportMode.Testing => MtaStsMode.Testing,
                MtaStsImportMode.Enforce => MtaStsMode.Enforce,
                _ => currentMode
            };

            var lookedUp = row.MtaStsMxHosts is null && currentMxHosts.Count == 0
                ? snapshot.LookedUpMxHosts.GetValueOrDefault(row.Domain) ?? []
                : [];
            var mxHosts = row.MtaStsMxHosts ?? (currentMxHosts.Count > 0 ? currentMxHosts : lookedUp);
            var maxAge = row.MtaStsMaxAgeSeconds ?? currentMaxAge;

            if (enabled && !currentEnabled && mxHosts.Count == 0)
            {
                _notes.Add("MTA-STS wasn't turned on: no MX hosts were given, and none were found in DNS.");
                return null;
            }

            _changes
                .Field("MTA-STS", Describe(currentEnabled, currentMode), Describe(enabled, modeAfter))
                .Set("MTA-STS MX hosts", currentMxHosts, mxHosts)
                .Field("MTA-STS max age (seconds)", currentMaxAge, maxAge);
            if (currentEnabled && !enabled)
            {
                _removals.Add("MTA-STS turned off");
            }

            if (lookedUp.Count > 0 && ReferenceEquals(mxHosts, lookedUp))
            {
                _notes.Add($"MX hosts found in DNS: {string.Join(", ", mxHosts)}.");
            }

            return new MtaStsTarget(enabled, modeAfter, mxHosts, maxAge);
        }

        private static string Describe(bool enabled, MtaStsMode mode) => enabled ? mode.ToString() : "Off";
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainImportPlannerTests" -nologo -v q`
Expected: PASS, 13 tests.

- [ ] **Step 6: Commit**

```powershell
git add src/DotMarc/DomainImport test/DotMarc.Tests/DomainImport
git commit -m "Plan a domain import row by row, with unknown names, modes and permissions"
```

---

### Task 9: Carrying out an import

**Files:**
- Create: `src/DotMarc/DomainImport/DomainImportService.cs`
- Test: `test/DotMarc.Tests/DomainImport/DomainImportServiceTests.cs`

**Interfaces:**
- Consumes: Task 8's `ImportPlan` and parts; Task 2's transaction joining and `AuditActions.DomainsImported`; the
  existing services.
- Produces: `record ImportRowOutcome(int LineNumber, string Domain, string Outcome)`;
  `record ImportResult(int Added, int Updated, int Unchanged, int SkippedExisting, int Invalid, int Duplicates, IReadOnlyList<ImportRowOutcome> Rows)`;
  `DomainImportService.ApplyAsync(DotMarcDbContext context, AuditActor actor, ImportPlan plan, CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Write the failing tests**

Create `test/DotMarc.Tests/DomainImport/DomainImportServiceTests.cs` with the usual Postgres boilerplate, then:

```csharp
    private async Task<ImportPlan> PlanAsync(string csv, ExistingDomainMode mode = ExistingDomainMode.Add)
    {
        var table = ImportTable.FromRows(CsvImportReader.Read(csv));
        await using var context = CreateContext();
        var snapshot = await ImportSnapshotLoader.LoadAsync(context, table, null, "HaloPSA isn't connected.", new FakeMxHostsLookup(), CancellationToken.None);
        return DomainImportPlanner.Plan(table, snapshot, mode, ImportPermissions.All);
    }

    private async Task<ImportResult> ApplyAsync(ImportPlan plan)
    {
        await using var context = CreateContext();
        return await DomainImportService.ApplyAsync(context, TestActors.Admin, plan);
    }

    private async Task<List<string>> GroupsOfAsync(string domainName)
    {
        await using var context = CreateContext();
        return await context.Domains.Where(domain => domain.Name == domainName).SelectMany(domain => domain.Groups.Select(group => group.Name)).OrderBy(name => name).ToListAsync();
    }

    private async Task SeedAsync(string domainName, params string[] groupNames)
    {
        await using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, domainName);
        foreach (var groupName in groupNames)
        {
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, groupName, CancellationToken.None);
        }

        var domainId = (await context.Domains.SingleAsync(domain => domain.Name == domainName)).Id;
        var groupIds = await context.Groups.Where(group => groupNames.Contains(group.Name)).Select(group => group.Id).ToListAsync();
        await GroupManagementService.SetDomainGroupsAsync(context, TestActors.Admin, domainId, groupIds);
    }

    [Fact]
    public async Task Apply_AddsDomainsWithTheirGroupsTagsAndSettings_AndAuditsEachChange()
    {
        var plan = await PlanAsync("domain,groups,tags,monitored,dkim selectors\na.com,New Group,new-tag,no,s1\nb.com,,,,\nnot a domain,,,,");

        var result = await ApplyAsync(plan);

        Assert.Equal((2, 1), (result.Added, result.Invalid));
        Assert.Equal(["New Group"], await GroupsOfAsync("a.com"));
        await using var verify = CreateContext();
        var domainA = await verify.Domains.Include(domain => domain.Tags).SingleAsync(domain => domain.Name == "a.com");
        Assert.False(domainA.IsMonitored);
        Assert.Equal(["s1"], domainA.DkimSelectors);
        Assert.Equal("new-tag", domainA.Tags.Single().Name);
        var actions = await verify.AuditEntries.Select(entry => entry.Action).ToListAsync();
        Assert.Contains(AuditActions.GroupAdded, actions);
        Assert.Contains(AuditActions.TagAdded, actions);
        Assert.Equal(2, actions.Count(action => action == AuditActions.DomainAdded));
        Assert.Contains(AuditActions.DomainGroupsChanged, actions);
        var summary = await verify.AuditEntries.SingleAsync(entry => entry.Action == AuditActions.DomainsImported);
        Assert.Equal("Imported 2 domains, updated 0, skipped 1 invalid and 0 already monitored", summary.Summary);
    }

    [Fact]
    public async Task Apply_AddMode_KeepsExistingGroups_AndRemovesDashEntries()
    {
        await SeedAsync("old.com", "Client A", "Client B");
        var plan = await PlanAsync("domain,groups\nold.com,Client C;-Client B");

        var result = await ApplyAsync(plan);

        Assert.Equal(1, result.Updated);
        Assert.Equal(["Client A", "Client C"], await GroupsOfAsync("old.com"));
    }

    [Fact]
    public async Task Apply_MatchMode_ReplacesGroups()
    {
        await SeedAsync("old.com", "Client A", "Client B");
        var plan = await PlanAsync("domain,groups\nold.com,Client B", ExistingDomainMode.Match);

        await ApplyAsync(plan);

        Assert.Equal(["Client B"], await GroupsOfAsync("old.com"));
    }

    [Fact]
    public async Task Apply_SkipMode_LeavesExistingDomainsUntouched()
    {
        await SeedAsync("old.com", "Client A");
        var plan = await PlanAsync("domain,groups\nold.com,Client B", ExistingDomainMode.Skip);

        var result = await ApplyAsync(plan);

        Assert.Equal(1, result.SkippedExisting);
        Assert.Equal(["Client A"], await GroupsOfAsync("old.com"));
    }

    [Fact]
    public async Task Apply_UsesAGroupCreatedSinceThePreview_RatherThanADuplicate()
    {
        var plan = await PlanAsync("domain,groups\na.com,Late Group");
        await using (var context = CreateContext())
        {
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Late Group", CancellationToken.None);
        }

        await ApplyAsync(plan);

        await using var verify = CreateContext();
        Assert.Single(verify.Groups, group => group.Name == "Late Group");
        Assert.Equal(["Late Group"], await GroupsOfAsync("a.com"));
    }

    [Fact]
    public async Task Apply_SavesNothing_WhenItFailsPartway()
    {
        await SeedAsync("old.com");
        await using (var context = CreateContext())
        {
            await GroupManagementService.AddGroupAsync(context, TestActors.Admin, "Doomed", CancellationToken.None);
        }

        var plan = await PlanAsync("domain,groups\nnew.com,\nold.com,Doomed");
        await using (var context = CreateContext())
        {
            var doomedId = (await context.Groups.SingleAsync(group => group.Name == "Doomed")).Id;
            await GroupManagementService.RemoveGroupAsync(context, TestActors.Admin, doomedId);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => ApplyAsync(plan));

        await using var verify = CreateContext();
        Assert.DoesNotContain(verify.Domains, domain => domain.Name == "new.com");
        Assert.DoesNotContain(verify.AuditEntries, entry => entry.Action == AuditActions.DomainsImported);
    }
```

(Needed usings: `DotMarc.Audit`, `DotMarc.Data`, `DotMarc.DomainImport`, `DotMarc.Tests.Internal`,
`Microsoft.EntityFrameworkCore`, `Xunit`.) In `Apply_SavesNothing_WhenItFailsPartway` the plan adds the existing group
`Doomed` to `old.com`, and `Doomed` is deleted before the import runs, so setting the groups can't find it after
`new.com` has already been added.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainImportServiceTests" -nologo -v q`
Expected: build FAILS, `DomainImportService` not found.

- [ ] **Step 3: Write the service**

Create `src/DotMarc/DomainImport/DomainImportService.cs`:

```csharp
using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DomainImport;

public sealed record ImportRowOutcome(int LineNumber, string Domain, string Outcome);

public sealed record ImportResult(int Added, int Updated, int Unchanged, int SkippedExisting, int Invalid, int Duplicates, IReadOnlyList<ImportRowOutcome> Rows);

/// <summary>Carries out a confirmed import plan in one transaction, through the existing domain, group and tag services,
/// so every change is audited exactly as if made by hand. Any failure rolls the whole import back.</summary>
public static class DomainImportService
{
    public static async Task<ImportResult> ApplyAsync(DotMarcDbContext context, AuditActor actor, ImportPlan plan, CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // A result other than Added means a group or tag of that name appeared since the preview; it's used below.
        foreach (var groupName in plan.GroupsToCreate)
        {
            await GroupManagementService.AddGroupAsync(context, actor, groupName, cancellationToken).ConfigureAwait(false);
        }

        foreach (var tagName in plan.TagsToCreate)
        {
            await TagManagementService.AddTagAsync(context, actor, tagName, TagManagementService.AllowedColors[0], cancellationToken).ConfigureAwait(false);
        }

        var groupIds = await context.Groups.ToDictionaryAsync(group => group.Name, group => group.Id, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
        var tagIds = await context.Tags.ToDictionaryAsync(tag => tag.Name, tag => tag.Id, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);

        var outcomes = new List<ImportRowOutcome>();
        var (added, updated, unchanged, skipped) = (0, 0, 0, 0);
        foreach (var row in plan.Rows)
        {
            var domainLabel = row.Domain ?? row.RawDomain;
            switch (row.Status)
            {
                case ImportRowStatus.Invalid:
                    outcomes.Add(new(row.LineNumber, domainLabel, $"Not imported: {row.InvalidReason}"));
                    continue;
                case ImportRowStatus.Duplicate:
                    outcomes.Add(new(row.LineNumber, domainLabel, $"Merged into line {row.MergedIntoLine}"));
                    continue;
                case ImportRowStatus.AlreadyMonitored when row.Target is null:
                    if (plan.Mode == ExistingDomainMode.Skip)
                    {
                        skipped++;
                        outcomes.Add(new(row.LineNumber, domainLabel, "Already monitored, skipped"));
                    }
                    else
                    {
                        unchanged++;
                        outcomes.Add(new(row.LineNumber, domainLabel, "Already monitored, nothing to change"));
                    }

                    continue;
            }

            int domainId;
            if (row.Status == ImportRowStatus.New)
            {
                var addResult = await DomainManagementService.AddDomainAsync(context, actor, row.Domain!, cancellationToken).ConfigureAwait(false);
                domainId = await context.Domains.Where(domain => domain.Name == row.Domain).Select(domain => domain.Id).SingleAsync(cancellationToken).ConfigureAwait(false);
                if (addResult == DomainManagementService.AddDomainResult.Added)
                {
                    added++;
                    outcomes.Add(new(row.LineNumber, domainLabel, "Added"));
                }
                else if (plan.Mode == ExistingDomainMode.Skip)
                {
                    skipped++;
                    outcomes.Add(new(row.LineNumber, domainLabel, "Added by someone else since the preview, so it was skipped"));
                    continue;
                }
                else
                {
                    updated++;
                    outcomes.Add(new(row.LineNumber, domainLabel, "Already existed by the time of the import, so it was updated"));
                }
            }
            else
            {
                domainId = row.ExistingDomainId!.Value;
                updated++;
                outcomes.Add(new(row.LineNumber, domainLabel, "Updated"));
            }

            await ApplyTargetAsync(context, actor, domainId, row.Target!, groupIds, tagIds, cancellationToken).ConfigureAwait(false);
        }

        var invalid = plan.InvalidCount;
        AuditLog.Record(context, actor, AuditActions.DomainsImported, null,
            $"Imported {added} {(added == 1 ? "domain" : "domains")}, updated {updated}, skipped {invalid} invalid and {skipped} already monitored");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ImportResult(added, updated, unchanged, skipped, invalid, plan.DuplicateCount, outcomes);
    }

    private static async Task ApplyTargetAsync(DotMarcDbContext context, AuditActor actor, int domainId, DomainTarget target,
        IReadOnlyDictionary<string, int> groupIds, IReadOnlyDictionary<string, int> tagIds, CancellationToken cancellationToken)
    {
        if (target.Groups is { } groupChange)
        {
            // Worked out against the domain's groups now, not at preview time, so a change made in between isn't undone.
            var current = await context.Domains.Where(domain => domain.Id == domainId).SelectMany(domain => domain.Groups.Select(group => group.Name)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var ids = groupChange.ApplyTo(current).Select(name => groupIds[name]).ToList();
            await GroupManagementService.SetDomainGroupsAsync(context, actor, domainId, ids, cancellationToken).ConfigureAwait(false);
        }

        if (target.Tags is { } tagChange)
        {
            var current = await context.Domains.Where(domain => domain.Id == domainId).SelectMany(domain => domain.Tags.Select(tag => tag.Name)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var ids = tagChange.ApplyTo(current).Select(name => tagIds[name]).ToList();
            await TagManagementService.SetDomainTagsAsync(context, actor, domainId, ids, cancellationToken).ConfigureAwait(false);
        }

        if (target.SetHaloClient)
        {
            await DomainManagementService.SetHaloClientIdAsync(context, actor, domainId, target.HaloClientId, cancellationToken).ConfigureAwait(false);
        }

        if (target.Monitored is { } monitored)
        {
            await DomainManagementService.SetMonitoredAsync(context, actor, domainId, monitored, cancellationToken).ConfigureAwait(false);
        }

        if (target.DkimSelectors is { } selectors)
        {
            await DomainManagementService.SetDkimSelectorsAsync(context, actor, domainId, [.. selectors], cancellationToken).ConfigureAwait(false);
        }

        if (target.MtaSts is { } mtaSts)
        {
            await DomainManagementService.SetMtaStsConfigAsync(context, actor, domainId, mtaSts.Enabled, mtaSts.Mode, [.. mtaSts.MxHosts], mtaSts.MaxAgeSeconds, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

(`groupIds[name]` throws `KeyNotFoundException` for a group deleted since the preview, which rolls back the
transaction; that is `Apply_SavesNothing_WhenItFailsPartway`.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainImportServiceTests" -nologo -v q`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```powershell
git add src/DotMarc/DomainImport/DomainImportService.cs test/DotMarc.Tests/DomainImport/DomainImportServiceTests.cs
git commit -m "Carry out a domain import in one transaction through the audited services"
```

---

### Task 10: The Import domains page

**Files:**
- Create: `src/DotMarc/Components/Pages/ImportDomains.razor`
- Modify: `src/DotMarc/Components/Pages/ManageDomains.razor` (Import domains button)
- Modify: `src/DotMarc/Program.cs` (sample endpoints)

**Interfaces:**
- Consumes: every earlier task; `HaloPsaSettingsService.GetAsync`, `IHaloPsaClient.ListClientsAsync`, `IMxHostsLookup`,
  `AuditActorAccessor`, `SearchableSelect`/`SelectOption` (`DotMarc.Components.Shared`).

- [ ] **Step 1: Serve the samples**

In `src/DotMarc/Program.cs`, before the `/audit/export` endpoint, add:

```csharp
app.MapGet("/domains/import/sample.csv", () =>
        Results.File(System.Text.Encoding.UTF8.GetBytes(DotMarc.DomainImport.DomainImportSamples.Csv), "text/csv", "dotmarc-domain-import-sample.csv"))
    .RequireAuthorization(nameof(Permission.DomainsAdd));
app.MapGet("/domains/import/sample.xlsx", () =>
        Results.File(DotMarc.DomainImport.DomainImportSamples.Xlsx(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "dotmarc-domain-import-sample.xlsx"))
    .RequireAuthorization(nameof(Permission.DomainsAdd));
```

- [ ] **Step 2: Write the page**

Create `src/DotMarc/Components/Pages/ImportDomains.razor`:

```razor
@page "/domains/import"
@attribute [Authorize(Policy = "DomainsAdd")]
@using DotMarc.Components.Shared
@using DotMarc.Data
@using DotMarc.DomainImport
@using DotMarc.MtaSts
@using DotMarc.Notifications
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.EntityFrameworkCore
@inject AuditActorAccessor AuditActorAccessor
@inject IDbContextFactory<DotMarcDbContext> DbFactory
@inject IAuthorizationService AuthorizationService
@inject AuthenticationStateProvider AuthenticationStateProvider
@inject IHaloPsaClient HaloPsaClient
@inject IMxHostsLookup MxHostsLookup
@inject ISnackbar Snackbar
@inject ILogger<ImportDomains> Logger

<PageTitle>dotMARC - Import domains</PageTitle>
<MudButton Href="/domains" StartIcon="@Icons.Material.Filled.ArrowBack" Class="mb-2">Back</MudButton>
<div class="d-flex align-center mb-1">
    <MudText Typo="Typo.h4">Import domains</MudText>
    <DocsLink Href="https://dotmarc.app/docs/import-domains" Text="Columns, modes and examples" Class="ml-2" />
</div>

@if (_result is not null)
{
    <MudAlert Severity="Severity.Success" Class="mb-3">
        Added @_result.Added, updated @_result.Updated, @_result.Unchanged unchanged, skipped @_result.SkippedExisting already monitored
        and @_result.Invalid invalid. Health checks run for the new domains within the next polling cycle.
    </MudAlert>
    <MudTable Items="_result.Rows" Dense="true" Hover="true" RowsPerPage="50">
        <HeaderContent><MudTh>Line</MudTh><MudTh>Domain</MudTh><MudTh>What happened</MudTh></HeaderContent>
        <RowTemplate><MudTd>@context.LineNumber</MudTd><MudTd>@context.Domain</MudTd><MudTd>@context.Outcome</MudTd></RowTemplate>
        <PagerContent><MudTablePager /></PagerContent>
    </MudTable>
    <div class="d-flex mt-3" style="gap:0.5rem">
        <MudButton Variant="Variant.Filled" Color="Color.Primary" Href="/domains">Go to Manage domains</MudButton>
        <MudButton Variant="Variant.Outlined" OnClick="StartOver">Import more</MudButton>
    </div>
}
else if (_plan is null)
{
    <MudText Typo="Typo.body2" Class="mb-3 mud-text-secondary">
        Paste domains, one per line, or choose a CSV or Excel file. Optional columns set each domain's groups, tags, Halo
        client, monitoring, DKIM selectors and MTA-STS. Nothing is saved until you check the preview and confirm.
        Download a sample: <MudLink Href="/domains/import/sample.csv" UserAttributes="@DownloadAttribute">CSV</MudLink> or
        <MudLink Href="/domains/import/sample.xlsx" UserAttributes="@DownloadAttribute">Excel</MudLink>.
    </MudText>
    <MudPaper Class="pa-4" Elevation="1">
        @if (_uploadedFileName is null)
        {
            <MudTextField T="string" @bind-Value="_pasteText" Label="Domains" Lines="10" Variant="Variant.Outlined"
                          Placeholder="contoso.com&#10;fabrikam.com,Client A,primary" />
        }
        else
        {
            <MudChip T="string" Color="Color.Primary" OnClose="@(() => { _uploadedFileName = null; _uploadedRows = null; })">@_uploadedFileName</MudChip>
        }
        <div class="d-flex align-center mt-3" style="gap:0.5rem">
            <MudFileUpload T="IBrowserFile" FilesChanged="OnFileChosenAsync" Accept=".csv,.txt,.xlsx,.xls">
                <CustomContent>
                    <MudButton Variant="Variant.Outlined" StartIcon="@Icons.Material.Filled.UploadFile" OnClick="@context.OpenFilePickerAsync">Choose a CSV or Excel file</MudButton>
                </CustomContent>
            </MudFileUpload>
            <MudSpacer />
            <MudButton Variant="Variant.Filled" Color="Color.Primary" Disabled="_busy" OnClick="PreviewAsync">@(_busy ? "Reading..." : "Preview")</MudButton>
        </div>
        @if (_inputError is not null)
        {
            <MudAlert Severity="Severity.Error" Class="mt-3">@_inputError</MudAlert>
        }
    </MudPaper>
}
else
{
    @foreach (var warning in _table!.Warnings)
    {
        <MudAlert Severity="Severity.Warning" Dense="true" Class="mb-2">@warning</MudAlert>
    }
    @foreach (var notice in _plan.Notices)
    {
        <MudAlert Severity="Severity.Info" Dense="true" Class="mb-2">@notice</MudAlert>
    }

    <div class="d-flex flex-wrap mb-3" style="gap:0.5rem">
        <MudChip T="string" Color="Color.Success">@_plan.NewCount new</MudChip>
        <MudChip T="string" Color="Color.Info">@(_plan.UpdateCount + _plan.UnchangedCount + _plan.SkippedExistingCount) already monitored</MudChip>
        <MudChip T="string">@_plan.DuplicateCount duplicate</MudChip>
        <MudChip T="string" Color="Color.Error">@_plan.InvalidCount invalid</MudChip>
    </div>

    @if (_plan.Rows.Any(row => row.Status == ImportRowStatus.AlreadyMonitored))
    {
        <MudPaper Class="pa-4 mb-3" Elevation="1">
            <MudText Typo="Typo.subtitle1">Domains that are already monitored</MudText>
            <MudRadioGroup T="ExistingDomainMode" Value="_mode" ValueChanged="@(mode => { _mode = mode; Replan(); })">
                <MudRadio T="ExistingDomainMode" Value="ExistingDomainMode.Skip">Skip them</MudRadio>
                <MudRadio T="ExistingDomainMode" Value="ExistingDomainMode.Add">Add groups and tags (-Name removes one), and apply settings</MudRadio>
                <MudRadio T="ExistingDomainMode" Value="ExistingDomainMode.Match">Make groups and tags match the file, and apply settings</MudRadio>
            </MudRadioGroup>
        </MudPaper>
    }

    @if (_plan.UnknownNames.Count > 0)
    {
        <MudPaper Class="pa-4 mb-3" Elevation="1">
            <MudText Typo="Typo.subtitle1" Class="mb-2">Names that don't exist yet</MudText>
            <MudSimpleTable Dense="true" Elevation="0">
                <thead><tr><th>Name</th><th>Used on lines</th><th>What to do</th></tr></thead>
                <tbody>
                    @foreach (var unknown in _plan.UnknownNames)
                    {
                        <tr>
                            <td><MudChip T="string" Size="Size.Small">@KindLabel(unknown.Kind)</MudChip> @unknown.Name</td>
                            <td>@string.Join(", ", unknown.LineNumbers)</td>
                            <td>
                                <div class="d-flex align-center" style="gap:0.5rem">
                                    <MudSelect T="NameChoice" Value="unknown.Resolution.Choice" Margin="Margin.Dense" Style="min-width:160px"
                                               ValueChanged="@(choice => Resolve(unknown, choice, choice == NameChoice.MapTo ? unknown.Suggestions.FirstOrDefault() ?? ExistingNames(unknown.Kind).FirstOrDefault() : null))">
                                        @if (unknown.CanCreate)
                                        {
                                            <MudSelectItem T="NameChoice" Value="NameChoice.Create">Create it</MudSelectItem>
                                        }
                                        <MudSelectItem T="NameChoice" Value="NameChoice.MapTo">Map to</MudSelectItem>
                                        <MudSelectItem T="NameChoice" Value="NameChoice.LeaveOut">Leave it out</MudSelectItem>
                                    </MudSelect>
                                    @if (unknown.Resolution.Choice == NameChoice.MapTo)
                                    {
                                        <SearchableSelect TValue="string" Options="@(ExistingNames(unknown.Kind).Select(name => new SelectOption<string>(name, name)).ToList())"
                                                          Value="unknown.Resolution.MapTo" ValueChanged="@(name => Resolve(unknown, NameChoice.MapTo, name))" Margin="Margin.Dense" />
                                    }
                                </div>
                                @if (unknown.Suggestions.Count > 0)
                                {
                                    <MudText Typo="Typo.caption" Class="mud-text-secondary">Closest: @string.Join(", ", unknown.Suggestions)</MudText>
                                }
                            </td>
                        </tr>
                    }
                </tbody>
            </MudSimpleTable>
        </MudPaper>
    }

    <MudTable Items="_plan.Rows" Dense="true" Hover="true" RowsPerPage="50">
        <HeaderContent><MudTh>Line</MudTh><MudTh>Domain</MudTh><MudTh>Status</MudTh><MudTh>What will happen</MudTh></HeaderContent>
        <RowTemplate>
            <MudTd>@context.LineNumber</MudTd>
            <MudTd>@(context.Domain ?? context.RawDomain)</MudTd>
            <MudTd><MudChip T="string" Size="Size.Small" Color="@StatusColor(context.Status)">@StatusLabel(context.Status)</MudChip></MudTd>
            <MudTd>
                @if (context.InvalidReason is not null)
                {
                    <MudText Typo="Typo.body2" Color="Color.Error">@context.InvalidReason</MudText>
                }
                @foreach (var change in context.Changes)
                {
                    <MudText Typo="Typo.body2">@change.Field: @(change.Old ?? "(none)") → @(change.New ?? "(none)")</MudText>
                }
                @foreach (var removal in context.Removals)
                {
                    <MudText Typo="Typo.body2" Color="Color.Error">@removal</MudText>
                }
                @foreach (var note in context.Notes)
                {
                    <MudText Typo="Typo.caption" Class="mud-text-secondary d-block">@note</MudText>
                }
            </MudTd>
        </RowTemplate>
        <PagerContent><MudTablePager /></PagerContent>
    </MudTable>

    <div class="d-flex mt-3" style="gap:0.5rem">
        <MudButton Variant="Variant.Outlined" OnClick="@(() => { _plan = null; _table = null; })">Back</MudButton>
        <MudSpacer />
        <MudButton Variant="Variant.Filled" Color="Color.Primary" Disabled="@(_busy || (_plan.NewCount + _plan.UpdateCount) == 0)" OnClick="ImportAsync">
            @(_busy ? "Importing..." : $"Import: add {_plan.NewCount}, update {_plan.UpdateCount}")
        </MudButton>
    </div>
}

@code {
    private static readonly Dictionary<string, object?> DownloadAttribute = new() { ["download"] = "" };

    private string _pasteText = "";
    private string? _uploadedFileName;
    private IReadOnlyList<ImportRow>? _uploadedRows;
    private string? _inputError;
    private bool _busy;

    private ImportTable? _table;
    private ImportSnapshot? _snapshot;
    private ImportPermissions _permissions = ImportPermissions.All;
    private ExistingDomainMode _mode = ExistingDomainMode.Skip;
    private readonly Dictionary<NameKey, NameResolution> _resolutions = [];
    private ImportPlan? _plan;
    private ImportResult? _result;

    private async Task OnFileChosenAsync(IBrowserFile file)
    {
        _inputError = null;
        try
        {
            await using var stream = file.OpenReadStream(maxAllowedSize: XlsxImportReader.MaximumBytes + 1);
            _uploadedRows = file.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".xls", StringComparison.OrdinalIgnoreCase)
                ? await XlsxImportReader.ReadAsync(stream, file.Name, CancellationToken.None)
                : await CsvImportReader.ReadAsync(stream, CancellationToken.None);
            _uploadedFileName = file.Name;
            _pasteText = "";
        }
        catch (ImportInputException refusal)
        {
            _inputError = refusal.Message;
        }
        catch (IOException)
        {
            _inputError = "The file is too large. CSV files can be up to 1 MB and Excel files up to 5 MB.";
        }
    }

    private async Task PreviewAsync()
    {
        _inputError = null;
        _busy = true;
        try
        {
            if (_uploadedRows is null && System.Text.Encoding.UTF8.GetByteCount(_pasteText) > CsvImportReader.MaximumBytes)
            {
                throw new ImportInputException("The pasted text is larger than 1 MB. Split it into smaller imports.");
            }

            _table = ImportTable.FromRows(_uploadedRows ?? CsvImportReader.Read(_pasteText));
            _permissions = await LoadPermissionsAsync();
            var (haloClients, haloUnavailableReason) = await LoadHaloClientsAsync();
            await using var context = await DbFactory.CreateDbContextAsync();
            _snapshot = await ImportSnapshotLoader.LoadAsync(context, _table, haloClients, haloUnavailableReason, MxHostsLookup, CancellationToken.None);
            _resolutions.Clear();
            Replan();
        }
        catch (ImportInputException refusal)
        {
            _inputError = refusal.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<ImportPermissions> LoadPermissionsAsync()
    {
        var user = (await AuthenticationStateProvider.GetAuthenticationStateAsync()).User;
        async Task<bool> CanAsync(string policy) => (await AuthorizationService.AuthorizeAsync(user, policy)).Succeeded;
        return new ImportPermissions(await CanAsync("DomainsEdit"), await CanAsync("MtaStsManage"), await CanAsync("GroupsAdd"), await CanAsync("TagsAdd"));
    }

    private async Task<(IReadOnlyList<HaloClient>? Clients, string? UnavailableReason)> LoadHaloClientsAsync()
    {
        if (!_table!.Columns.Contains(ImportColumn.HaloClient))
        {
            return (null, null);
        }

        await using var context = await DbFactory.CreateDbContextAsync();
        var settings = await HaloPsaSettingsService.GetAsync(context);
        if (!settings.Enabled || !settings.ClientSecretConfigured)
        {
            return (null, "HaloPSA isn't connected, so the Halo client column was ignored.");
        }

        try
        {
            return ((await HaloPsaClient.ListClientsAsync(settings)).ToList(), null);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Loading Halo clients for a domain import failed");
            return (null, "The Halo client list couldn't be loaded, so the Halo client column was ignored.");
        }
    }

    private void Replan() => _plan = DomainImportPlanner.Plan(_table!, _snapshot!, _mode, _permissions, _resolutions);

    private void Resolve(UnknownName unknown, NameChoice choice, string? mapTo)
    {
        _resolutions[unknown.Key] = new NameResolution(choice, mapTo);
        Replan();
    }

    private IReadOnlyList<string> ExistingNames(ImportNameKind kind) => kind switch
    {
        ImportNameKind.Group => _snapshot!.GroupNames,
        ImportNameKind.Tag => _snapshot!.TagNames,
        _ => _snapshot!.HaloClients?.Select(client => client.Name).ToList() ?? []
    };

    private async Task ImportAsync()
    {
        _busy = true;
        try
        {
            await using var context = await DbFactory.CreateDbContextAsync();
            _result = await DomainImportService.ApplyAsync(context, await AuditActorAccessor.GetAsync(), _plan!);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "A domain import failed and was rolled back");
            Snackbar.Add("The import failed, and nothing was saved. Check the preview and try again.", Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void StartOver()
    {
        (_result, _plan, _table, _snapshot, _uploadedRows, _uploadedFileName, _pasteText) = (null, null, null, null, null, null, "");
        _resolutions.Clear();
    }

    private static string KindLabel(ImportNameKind kind) => kind switch
    {
        ImportNameKind.Group => "Group",
        ImportNameKind.Tag => "Tag",
        _ => "Halo client"
    };

    private static string StatusLabel(ImportRowStatus status) => status switch
    {
        ImportRowStatus.New => "New",
        ImportRowStatus.AlreadyMonitored => "Already monitored",
        ImportRowStatus.Duplicate => "Duplicate",
        _ => "Invalid"
    };

    private static Color StatusColor(ImportRowStatus status) => status switch
    {
        ImportRowStatus.New => Color.Success,
        ImportRowStatus.AlreadyMonitored => Color.Info,
        ImportRowStatus.Duplicate => Color.Default,
        _ => Color.Error
    };
}
```

If the build names a different member than `OpenFilePickerAsync` on the `CustomContent` context, use the one it names.

- [ ] **Step 3: Link it from Manage domains**

In `src/DotMarc/Components/Pages/ManageDomains.razor`, inside the `DomainsAdd` `AuthorizeView`, change the button
`MudItem` to hold both buttons:

```razor
            <MudItem xs="3" Class="d-flex align-start" Style="gap:0.5rem">
                <MudButton Variant="Variant.Filled" Color="Color.Primary" Class="button-beside-input" StartIcon="@Icons.Material.Filled.Add"
                           OnClick="AddDomainAsync">Add domain</MudButton>
                <MudButton Variant="Variant.Outlined" Color="Color.Primary" Class="button-beside-input" StartIcon="@Icons.Material.Filled.UploadFile"
                           Href="/domains/import">Import domains</MudButton>
            </MudItem>
```

- [ ] **Step 4: Build and check it on the demo**

Run: `dotnet build src/DotMarc/DotMarc.csproj -nologo -v q` → `Build succeeded.`

Start the demo (`$env:Demo__Enabled='true'; $env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/DotMarc --no-build --no-launch-profile --urls http://localhost:5195`),
sign in as Demo Admin, and on **Manage domains > Import domains** check:
- Pasting `contoso-new.example` and `not a domain` previews one New and one Invalid row with its reason.
- Pasting a CSV with a groups column naming `Aurora Retale` (a typo of a demo group) lists it under "Names that don't
  exist yet", mapped to `Aurora Retail`; switching it to Create it lists it for creation.
- A row for an existing demo domain with `-Aurora Retail` in Add mode shows `Group "Aurora Retail" removed` in red,
  and Match mode with a blank groups cell does the same.
- Downloading the sample CSV and `.xlsx`, then uploading each, previews the same rows.
- Importing shows the result page, the domains appear on Manage domains with their groups, and the audit log lists the
  changes and a `domains.imported` entry.

Stop the app.

- [ ] **Step 5: Run everything and commit**

Run: `dotnet test test/DotMarc.Tests -nologo -v q` → PASS.

```powershell
git add src/DotMarc/Components/Pages/ImportDomains.razor src/DotMarc/Components/Pages/ManageDomains.razor src/DotMarc/Program.cs
git commit -m "Add the Import domains page"
```

---

### Task 11: Docs and roadmap

**Files:**
- Create: `website/docs/import-domains.mdx`
- Modify: `website/sidebars.ts`, `website/docs/psa-integration.mdx`, `website/scripts/canny-roadmap.json`

(There is no Manage domains docs page, so the new page goes in the Monitor category and is linked from the PSA page's
"add the domains to them afterwards" sentence.)

- [ ] **Step 1: Write the docs page**

Create `website/docs/import-domains.mdx`:

```mdx
---
description: Add and update many domains at once from a pasted list, a CSV file or an Excel file, with a preview before anything is saved.
---

# Import domains

**Manage domains > Import domains** adds many domains at once, and can update the groups, tags and settings of domains
you already monitor. Paste a list, or choose a CSV or Excel (`.xlsx`) file. Nothing is saved until you check the
preview and confirm, and the whole import is saved at once or not at all.

The page has a sample CSV and a sample Excel file to start from.

## Columns

Only the domain is required, so a list of domains, one per line, is a complete import.

| Column | What it holds |
| --- | --- |
| domain | The domain, for example `contoso.com`. |
| groups | Groups, separated by `;`. |
| tags | Tags, separated by `;`. |
| halo client | The HaloPSA client's name. Used only when HaloPSA is connected. |
| monitored | `yes` or `no`. |
| dkim selectors | DKIM selectors, separated by `;`. They replace the domain's list. |
| mta-sts mode | `off`, `none`, `testing` or `enforce`. |
| mta-sts mx hosts | MX hosts, separated by `;`. |
| mta-sts max age | Seconds, from 1 to 31,557,600. |

Without a header row, columns are read in that order. With a header row (one of its cells is `domain`), columns can be
in any order and any can be left out.

A blank cell leaves that value as it is. A value that doesn't make sense, such as `monitored` set to `maybe`, is left
out and the preview says why; the rest of the row still imports.

Turning MTA-STS on needs MX hosts. If a row doesn't give them and the domain has none saved, dotMARC looks them up in
DNS, as the Enable button on a domain's page does.

## Domains you already monitor

Choose what happens to them on the preview:

* **Skip them** (the default) leaves them untouched.
* **Add** adds the listed groups and tags, and applies any settings given. Write `-Name` to remove a group or tag, for
  example `Client A;-Old Client`.
* **Match the file** makes each domain's groups and tags exactly what its row lists, and applies any settings given. A
  blank groups or tags cell removes them all. A column that isn't in the file is left alone.

Everything that will be removed is shown in red on the preview.

## Names that don't exist yet

If a group, tag or Halo client in the import doesn't exist, the preview lists it. For each one, choose to create it,
map it to an existing one, or leave it out. A name that's the same as an existing one apart from punctuation or an
ending like "Ltd" is mapped to it for you; close spellings are suggested, to catch typos, but you choose them. Halo clients can't be
created from dotMARC. A name that only differs in capital letters is treated as the existing one.

## Permissions

Importing needs the `DomainsAdd` permission. Groups, tags, Halo client, monitoring and DKIM selectors also need
`DomainsEdit`, and the MTA-STS columns need `MtaStsManage`. Creating groups or tags needs `GroupsAdd` or `TagsAdd`.
Without a permission, that column is ignored and the preview says so. See [Permissions and access](./permissions-and-access.mdx).

## Limits

An import can have up to 1,000 rows. CSV files and pasted text can be up to 1 MB, and Excel files up to 5 MB. Old
`.xls` files can't be read: save them as `.xlsx` or CSV.

## After importing

Every change is recorded in the [audit log](./audit-log.mdx), as if you'd made it by hand, with one extra entry
summarising the import. New domains get their DNS health checks within the next polling cycle.
```

- [ ] **Step 2: Sidebar and links**

In `website/sidebars.ts`, change `items: ['dmarc-and-mta-sts', 'dns-provider-push', 'mta-sts'],` to
`items: ['import-domains', 'dmarc-and-mta-sts', 'dns-provider-push', 'mta-sts'],`.

In `website/docs/psa-integration.mdx`, change "Only the Groups are created. Add the domains to them afterwards, from
**Manage domains**." to "Only the Groups are created. Add the domains to them afterwards, from **Manage domains** or in
bulk with an [import](./import-domains.mdx)."

- [ ] **Step 3: Mark the roadmap idea complete**

In `website/scripts/canny-roadmap.json`, on `"Allow bulk domain onboarding with CSV or paste-a-list imports"`, change
`"status": "planned"` to `"status": "complete"`.

- [ ] **Step 4: Check everything**

Run: `dotnet test test/DotMarc.Tests -nologo -v q` → PASS.
Run: `cd website; yarn build; cd ..` → builds with no broken links.

- [ ] **Step 5: Commit**

```powershell
git add website/docs/import-domains.mdx website/sidebars.ts website/docs/psa-integration.mdx website/scripts/canny-roadmap.json
git commit -m "Document domain imports"
```
