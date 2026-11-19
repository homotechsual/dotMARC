using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;

namespace DotMarc.Api;

/// <summary>The public API's OpenAPI document, served anonymously at /api/v1/openapi.json. Only /api/v1 endpoints are
/// described; servers are left out so the committed copy doesn't depend on where it was generated.</summary>
public static class ApiDocument
{
    public const string DocumentName = "v1";
    public const string DocumentPath = "/api/v1/openapi.json";
    private const string PermissionExtension = "x-dotmarc-permission";

    /// <summary>What each error means, for every operation that can return it. All are problem+json.</summary>
    private static readonly Dictionary<string, string> ErrorDescriptions = new()
    {
        ["400"] = "The request couldn't be used. `errors` names each field that's wrong and why.",
        ["401"] = "No API key was sent, or the key is unknown, expired or revoked.",
        ["403"] = "The key's role doesn't have the permission this operation needs, or the key is limited to certain groups and this would reach outside them. `detail` says which.",
        ["404"] = "There's no such item, or this key can't see it. A key limited to certain groups can't see what's outside them.",
        ["409"] = "The request conflicts with how things are now, such as a domain that's already in dotMARC or an alert that can't be acknowledged. `detail` says what.",
        ["429"] = "This key has made more requests this minute than dotMARC allows (120 by default). Wait for the number of seconds in the Retry-After header, then try again.",
        ["500"] = "Something went wrong in dotMARC that the request didn't cause. It's safe to retry a read; check before retrying a change. If it keeps happening, dotMARC's logs have the details.",
    };

    /// <summary>The project's VersionPrefix, from the informational version without any "+commit" suffix.</summary>
    public static string Version { get; } =
        (typeof(ApiDocument).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static IServiceCollection AddDotMarcOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
            options.ShouldInclude = description => description.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true;

            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "dotMARC API",
                    Version = Version,
                    Description = "Read domains, DNS health, DMARC report summaries, groups, tags and alerts, and add, import and organise domains. Authenticate with an API key from dotMARC's Access page: `Authorization: Bearer dmk_...`. Each operation needs the permission named in its description, and a key limited to certain groups only sees their domains. Each key may make 120 requests a minute.",
                };
                document.Servers = [];
                document.Tags = new HashSet<OpenApiTag>(ApiTags.All.Select(tag => new OpenApiTag { Name = tag.Name, Description = tag.Description }));
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "dmk_ API key",
                        Description = "An API key created on dotMARC's Access page.",
                    },
                };
                document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] }];
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, cancellationToken) =>
            {
                var permission = context.Description.ActionDescriptor.EndpointMetadata.OfType<ApiPermissionMetadata>().FirstOrDefault();
                if (permission is not null)
                {
                    operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                    operation.Extensions[PermissionExtension] = new JsonNodeExtension(JsonValue.Create(permission.Permission.ToString()));
                    operation.Description = $"{operation.Description}\n\nNeeds the {permission.Permission} permission.".TrimStart();
                }

                // ASP.NET describes an error only by its status's name; say when each one happens instead.
                foreach (var (statusCode, response) in operation.Responses ?? [])
                {
                    if (response is OpenApiResponse openApiResponse && ErrorDescriptions.TryGetValue(statusCode, out var description))
                    {
                        openApiResponse.Description = description;
                    }
                }

                // Each filter's description and allowed values; a list filter's values describe its items.
                foreach (var docs in context.Description.ActionDescriptor.EndpointMetadata.OfType<ApiFilterDocs>())
                {
                    if (operation.Parameters?.FirstOrDefault(parameter => parameter.Name == docs.Name) is not OpenApiParameter parameter)
                    {
                        continue;
                    }

                    parameter.Description = docs.Description;
                    if (parameter.Schema is not OpenApiSchema schema)
                    {
                        continue;
                    }

                    var valuesSchema = schema.Items as OpenApiSchema ?? schema;
                    if (docs.Values is { } values)
                    {
                        valuesSchema.Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
                    }

                    if (docs.Type is { } type)
                    {
                        valuesSchema.Type = type;
                    }

                    if (docs.Defaults is { } defaults)
                    {
                        schema.Default = new JsonArray(defaults.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
                    }
                }

                return Task.CompletedTask;
            });

            options.AddSchemaTransformer((schema, context, cancellationToken) =>
            {
                // The API reads numbers sent as strings too, so ASP.NET describes each number as "number or string" with a
                // digits pattern. OpenAPI 3.0 can't say "or", so the type was dropped and doc tools showed `any` and a
                // regex. Describe them as the numbers they are, keeping nullability.
                var numberType = schema.Format switch
                {
                    "int32" or "int64" => JsonSchemaType.Integer,
                    "double" or "float" => JsonSchemaType.Number,
                    _ => (JsonSchemaType?)null,
                };
                if (numberType is { } type)
                {
                    schema.Type = type | (schema.Type ?? 0) & JsonSchemaType.Null;
                    schema.Pattern = null;
                }

                // The send body's period takes a fixed set of values, which the record's string can't say on its own.
                if (context.JsonTypeInfo.Type == typeof(ApiSendReportRequest)
                    && schema.Properties?.TryGetValue("period", out var periodProperty) == true && periodProperty is OpenApiSchema periodSchema)
                {
                    periodSchema.Enum = ["week", "month", "quarter"];
                    periodSchema.Description = "week, month or quarter, with of (default: the period in progress); or leave it out and give start and end.";
                }

                if (ApiExamples.For(context.JsonTypeInfo.Type) is { } example)
                {
                    schema.Examples = [example];
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }

    public static void MapDotMarcOpenApi(this WebApplication app) =>
        app.MapOpenApi("/api/{documentName}/openapi.json").AllowAnonymous();
}

/// <summary>Example values for the API's main types, serialized the way the API serializes them (web defaults, enums as
/// strings).</summary>
internal static class ApiExamples
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly DateTimeOffset ExampleTime = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);

    private static readonly Dictionary<Type, object> Samples = new()
    {
        [typeof(ApiDomain)] = new ApiDomain(42, "contoso.example", true, [new ApiNamedRef(3, "Contoso")], [new ApiNamedRef(7, "Microsoft 365")], ExampleTime, 0.987),
        [typeof(ApiGroup)] = new ApiGroup(3, "Contoso", 12),
        [typeof(ApiTag)] = new ApiTag(7, "Microsoft 365", "Primary", 30),
        [typeof(ApiCheck)] = new ApiCheck("Ok", ExampleTime, null),
        [typeof(ApiSource)] = new ApiSource("203.0.113.25", 1480, "Pass", "Pass", "None"),
        [typeof(ApiAlert)] = new ApiAlert(901, "SpfRecordBroken", "SPF record broken", "contoso.example", new ApiNamedRef(42, "contoso.example"), "Warning",
            "SPF record broken", "contoso.example's SPF record has more than 10 DNS lookups.", ExampleTime, false, null, false,
            [new ApiAlertTicket("HaloPsa", "T-1042", true, ExampleTime, ExampleTime)]),
        [typeof(ApiAddDomainRequest)] = new ApiAddDomainRequest("contoso.example"),
        [typeof(ApiSetGroupsRequest)] = new ApiSetGroupsRequest([3, 5]),
        [typeof(ApiSetTagsRequest)] = new ApiSetTagsRequest([7]),
        [typeof(ApiSetMonitoringRequest)] = new ApiSetMonitoringRequest(true),
        [typeof(ApiImportRequest)] = new ApiImportRequest("skip", "skip",
            [new ApiImportDomain("contoso.example", ["Contoso"], ["Microsoft 365"], true), new ApiImportDomain("fabrikam.example", ["Fabrikam"], null, null)]),
        [typeof(ApiAcknowledgement)] = new ApiAcknowledgement(true, 1, 0),
        [typeof(ApiSendReportRequest)] = new ApiSendReportRequest("month", "2026-09-15", Recipients: ["it@contoso.example"]),
    };

    public static JsonNode? For(Type type) =>
        Samples.TryGetValue(type, out var sample) ? JsonSerializer.SerializeToNode(sample, type, SerializerOptions) : null;
}
