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
    public async Task EveryOperation_DescribesEachErrorItCanReturn_AsAProblem()
    {
        // Every operation can also answer 401, 403, 429 and 500; these are the errors particular to each.
        int[] everyOperation = [401, 403, 429, 500];
        var particular = new Dictionary<string, int[]>
        {
            ["GET /api/v1/alerts"] = [400],
            ["POST /api/v1/alerts/{id}/acknowledge"] = [404, 409],
            ["GET /api/v1/domains"] = [400],
            ["POST /api/v1/domains"] = [400, 409],
            ["POST /api/v1/domains/import"] = [400],
            ["GET /api/v1/domains/{domain}"] = [404],
            ["GET /api/v1/domains/{domain}/reports/summary"] = [400, 404],
            ["PUT /api/v1/domains/{domain}/groups"] = [400, 404],
            ["PUT /api/v1/domains/{domain}/tags"] = [400, 404],
            ["PUT /api/v1/domains/{domain}/monitoring"] = [400, 404],
            ["GET /api/v1/groups"] = [],
            ["GET /api/v1/tags"] = [],
        };
        using var document = JsonDocument.Parse(await FetchDocumentAsync());
        var operations = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(operation => (Name: $"{operation.Name.ToUpperInvariant()} {path.Name}", Body: operation.Value)))
            .ToList();

        Assert.Equal(particular.Keys.Order(), operations.Select(operation => operation.Name).Order());
        Assert.All(operations, operation =>
        {
            var errors = operation.Body.GetProperty("responses").EnumerateObject().Where(response => int.Parse(response.Name) >= 400).ToList();
            Assert.Equal(particular[operation.Name].Concat(everyOperation).Order(), errors.Select(response => int.Parse(response.Name)).Order());
            Assert.All(errors, response =>
            {
                Assert.True(response.Value.GetProperty("content").TryGetProperty("application/problem+json", out _),
                    $"{operation.Name} {response.Name} isn't described as problem+json");
                var description = response.Value.GetProperty("description").GetString();
                Assert.False(string.IsNullOrWhiteSpace(description) || description == Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(int.Parse(response.Name)),
                    $"{operation.Name} {response.Name} has no description beyond its status's name");
            });
        });
    }

    [Fact]
    public async Task EveryOperation_IsInOneReadableSection_AndEverySectionIsDescribed()
    {
        string[] sections = ["Domains", "Imports", "Groups and tags", "Alerts"];
        using var document = JsonDocument.Parse(await FetchDocumentAsync());
        var operationTags = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Select(operation => operation.Value.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).ToList())
            .ToList();
        var describedTags = document.RootElement.GetProperty("tags").EnumerateArray()
            .Where(tag => tag.TryGetProperty("description", out _))
            .Select(tag => tag.GetProperty("name").GetString())
            .ToList();

        Assert.All(operationTags, tags => Assert.Contains(Assert.Single(tags), sections));
        Assert.Equal(sections.Order(), describedTags.Order());
    }

    [Fact]
    public async Task Numbers_AreDescribedAsNumbers_NotAsPatternedStrings()
    {
        var numberSchemas = new List<JsonObject>();
        void Collect(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject jsonObject:
                    if (jsonObject["format"]?.GetValue<string>() is "int32" or "int64" or "double" or "float")
                    {
                        numberSchemas.Add(jsonObject);
                    }

                    foreach (var property in jsonObject)
                    {
                        Collect(property.Value);
                    }

                    break;
                case JsonArray jsonArray:
                    foreach (var item in jsonArray)
                    {
                        Collect(item);
                    }

                    break;
            }
        }

        Collect(JsonNode.Parse(await FetchDocumentAsync()));

        Assert.NotEmpty(numberSchemas);
        Assert.All(numberSchemas, schema =>
        {
            var expectedType = schema["format"]!.GetValue<string>() is "int32" or "int64" ? "integer" : "number";
            Assert.Equal(expectedType, schema["type"]?.GetValue<string>());
            Assert.Null(schema["pattern"]);
        });
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
