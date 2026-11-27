using System.Text.RegularExpressions;
using Xunit;

namespace DotMarc.Tests.Components;

/// <summary>The app links to its own documentation (https://dotmarc.app/docs/...). The repo has no component test host, so this reads the
/// Razor source: every link has to point at a docs page that exists, and at a heading that exists on it, so moving or renaming a
/// section can't leave a link in the app pointing at nothing.</summary>
public sealed class DocsLinkTests
{
    private static readonly Regex DocsLink = new(@"https://dotmarc\.app/docs/(?<path>[a-z0-9/_-]+)(?:#(?<anchor>[a-z0-9_-]+))?", RegexOptions.IgnoreCase);
    private static readonly Regex PartialImport = new(@"^import\s+\w+\s+from\s+'(?<path>\./[^']+\.mdx?)'", RegexOptions.Multiline);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dotMARC.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Couldn't find dotMARC.sln above the test output.");
    }

    private static string? DocsPageFile(string root, string path)
    {
        var docs = Path.Combine(root, "website", "docs");
        return new[] { $"{path}.mdx", $"{path}.md", Path.Combine(path, "index.mdx") }
            .Select(candidate => Path.Combine(docs, candidate))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>The headings of a docs page as Docusaurus anchors them, including the headings of any partial the page imports.</summary>
    private static HashSet<string> Anchors(string pageFile)
    {
        var text = File.ReadAllText(pageFile);
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var inFence = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            if (!inFence && Regex.Match(line, @"^#{1,6}\s+(?<heading>.+?)\s*$") is { Success: true } heading)
            {
                anchors.Add(Slug(heading.Groups["heading"].Value));
            }
        }

        foreach (Match import in PartialImport.Matches(text))
        {
            var partial = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pageFile)!, import.Groups["path"].Value));
            if (File.Exists(partial))
            {
                anchors.UnionWith(Anchors(partial));
            }
        }

        return anchors;
    }

    private static string Slug(string heading)
    {
        var plain = Regex.Replace(heading, @"\[([^\]]+)\]\([^)]*\)", "$1").Replace("`", "").Replace("**", "").Replace("__", "");
        var kept = new string(plain.ToLowerInvariant().Where(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '_').ToArray());
        return kept.Replace(' ', '-');
    }

    [Fact]
    public void EveryDocsLinkInTheApp_PointsAtAPageAndAHeadingThatExist()
    {
        var root = RepositoryRoot();
        var problems = new List<string>();
        var checkedLinks = 0;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "DotMarc"), "*.razor", SearchOption.AllDirectories))
        {
            foreach (Match link in DocsLink.Matches(File.ReadAllText(file)))
            {
                checkedLinks++;
                var path = link.Groups["path"].Value.TrimEnd('/');
                var relativeFile = Path.GetRelativePath(root, file);
                if (DocsPageFile(root, path) is not { } pageFile)
                {
                    problems.Add($"{relativeFile} links to /docs/{path}, which is not a docs page.");
                }
                else if (link.Groups["anchor"].Success && !Anchors(pageFile).Contains(link.Groups["anchor"].Value))
                {
                    problems.Add($"{relativeFile} links to /docs/{path}#{link.Groups["anchor"].Value}, but that page has no such heading.");
                }
            }
        }

        Assert.True(checkedLinks >= 10, "Found almost no docs links, so the search is probably looking in the wrong place.");
        Assert.Empty(problems);
    }

    [Fact]
    public void TheAlertRulesPage_LinksToTheAlertRulesDocsPage_NotToASectionOfTheAlertsPage()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "DotMarc", "Components", "Pages", "AlertRules.razor"));

        var link = Assert.Single(DocsLink.Matches(source));

        Assert.Equal("alert-rules", link.Groups["path"].Value);
        Assert.False(link.Groups["anchor"].Success);
    }
}
