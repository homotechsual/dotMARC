using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotMarc.Tests.Internal;
using Microsoft.OpenApi;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed partial class OpenApiDocumentTests : IAsyncLifetime
{
    private const string UpdateVariable = "DOTMARC_UPDATE_OPENAPI";
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public OpenApiDocumentTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dotMARC.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Couldn't find dotMARC.sln above the test output.");
    }

    private static string CommittedPath() => Path.Combine(RepositoryRoot(), "website", "data", "openapi", "dotmarc-api.json");

    /// <summary>Sorted keys, two-space indentation and \n line endings on every platform, so the committed copy diffs
    /// cleanly.</summary>
    private static string Normalize(string json) =>
        Sort(JsonNode.Parse(json))!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject jsonObject => new JsonObject(jsonObject.OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => KeyValuePair.Create(property.Key, Sort(property.Value?.DeepClone())))),
        JsonArray jsonArray => new JsonArray(jsonArray.Select(item => Sort(item?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    private async Task<string> FetchDocumentAsync()
    {
        using var client = _host.ClientFor(null);
        var response = await client.GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task TheCommittedCopy_MatchesTheApp()
    {
        var current = Normalize(await FetchDocumentAsync());
        var path = CommittedPath();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, current);
            return;
        }

        var committed = File.Exists(path) ? (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n") : "";
        Assert.True(committed == current,
            $"The API changed; run `node scripts/update-openapi.mjs` (or set {UpdateVariable}=1 and run this test) and commit website/data/openapi/dotmarc-api.json.");
    }

    [Fact]
    public async Task EveryOperation_HasASummaryAndItsPermission()
    {
        using var document = JsonDocument.Parse(await FetchDocumentAsync());
        var operations = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(operation => (Name: $"{operation.Name.ToUpperInvariant()} {path.Name}", Body: operation.Value)))
            .ToList();

        Assert.Equal(12, operations.Count);
        Assert.All(operations, operation =>
        {
            Assert.True(operation.Body.TryGetProperty("summary", out _), $"{operation.Name} has no summary");
            Assert.True(operation.Body.TryGetProperty("x-dotmarc-permission", out _), $"{operation.Name} has no permission");
        });
        Assert.All(document.RootElement.GetProperty("paths").EnumerateObject(), path => Assert.StartsWith("/api/v1/", path.Name));
    }

    [Fact]
    public async Task TheDocument_IsValidOpenApi3_WithABearerScheme()
    {
        var json = await FetchDocumentAsync();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        var readResult = await OpenApiDocument.LoadAsync(stream, "json");

        Assert.Empty(readResult.Diagnostic!.Errors);
        using var document = JsonDocument.Parse(json);
        Assert.StartsWith("3.", document.RootElement.GetProperty("openapi").GetString());
        Assert.Equal("bearer", document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("bearer").GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task TheDocumentsVersion_IsTheProjectVersion()
    {
        var props = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "Directory.Build.props"));
        var projectVersion = VersionPrefixPattern().Match(props).Groups[1].Value;
        using var document = JsonDocument.Parse(await FetchDocumentAsync());

        Assert.Equal(projectVersion, document.RootElement.GetProperty("info").GetProperty("version").GetString());
    }

    [GeneratedRegex("<VersionPrefix>([^<]+)</VersionPrefix>")]
    private static partial Regex VersionPrefixPattern();
}
