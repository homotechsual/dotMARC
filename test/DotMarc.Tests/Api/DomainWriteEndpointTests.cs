using System.Net;
using System.Net.Http.Json;
using System.Text;
using DotMarc.Api;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class DomainWriteEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public DomainWriteEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task AddDomain_CreatesItAsTheKey()
    {
        var (keyId, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd], name: "Provisioning");
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains", new { name = "API-Added.example" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var added = await response.Content.ReadFromJsonAsync<ApiDomain>();
        Assert.Equal("api-added.example", added!.Name);
        Assert.Equal($"/api/v1/domains/{added.Id}", response.Headers.Location!.ToString());
        await using var context = _host.CreateContext();
        var entry = await context.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.DomainAdded && candidate.TargetName == "api-added.example");
        Assert.Equal(AuditActorKind.ApiKey, entry.ActorKind);
        Assert.StartsWith("API key 'Provisioning", entry.ActorName);
        Assert.Equal($"api-key:{keyId}", entry.ActorObjectId);
    }

    [Fact]
    public async Task AddDomain_AnExistingDomain_Is409()
    {
        await _host.SeedDomainAsync("api-existing.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains", new { name = "api-existing.example" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AddDomain_AnInvalidName_Is400()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains", new { name = "not a domain" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddDomain_ByAScopedKey_Is403()
    {
        var groupId = await _host.SeedGroupAsync("api-write-scoped-add");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd], scopedGroupIds: [groupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains", new { name = "api-scoped-add.example" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AddDomain_WithoutDomainsAdd_Is403()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains", new { name = "api-no-permission.example" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJson_IsAProblem()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync("/api/v1/domains", new StringContent("{\"name\": ", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SetGroups_ReplacesTheDomainsGroups()
    {
        var oldGroupId = await _host.SeedGroupAsync("api-set-groups-old");
        var newGroupId = await _host.SeedGroupAsync("api-set-groups-new");
        var domainId = await _host.SeedDomainAsync("api-set-groups.example", groupIds: [oldGroupId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/groups", new { groupIds = new[] { newGroupId } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var context = _host.CreateContext();
        var domain = await context.Domains.Include(candidate => candidate.Groups).SingleAsync(candidate => candidate.Id == domainId);
        Assert.Equal([newGroupId], domain.Groups.Select(group => group.Id));
    }

    [Fact]
    public async Task SetGroups_AnUnknownGroup_Is400()
    {
        var domainId = await _host.SeedDomainAsync("api-set-groups-unknown.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/groups", new { groupIds = new[] { 987654 } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AScopedKey_SettingGroups_KeepsGroupsOutsideItsScope()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-scoped-set-own");
        var secondOwnGroupId = await _host.SeedGroupAsync("api-scoped-set-own-2");
        var otherGroupId = await _host.SeedGroupAsync("api-scoped-set-other");
        var domainId = await _host.SeedDomainAsync("api-scoped-set.example", groupIds: [ownGroupId, otherGroupId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit], scopedGroupIds: [ownGroupId, secondOwnGroupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/groups", new { groupIds = new[] { secondOwnGroupId } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var context = _host.CreateContext();
        var domain = await context.Domains.Include(candidate => candidate.Groups).SingleAsync(candidate => candidate.Id == domainId);
        Assert.Equal(new[] { secondOwnGroupId, otherGroupId }.Order(), domain.Groups.Select(group => group.Id).Order());
    }

    [Fact]
    public async Task AScopedKey_NamingAGroupOutsideItsScope_Is403()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-scoped-name-own");
        var otherGroupId = await _host.SeedGroupAsync("api-scoped-name-other");
        var domainId = await _host.SeedDomainAsync("api-scoped-name.example", groupIds: [ownGroupId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit], scopedGroupIds: [ownGroupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/groups", new { groupIds = new[] { ownGroupId, otherGroupId } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AScopedKey_WritingToADomainItCantSee_Is404()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-scoped-hidden-own");
        var domainId = await _host.SeedDomainAsync("api-scoped-hidden.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit], scopedGroupIds: [ownGroupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/monitoring", new { monitored = false });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SetTags_AndSetMonitoring_ChangeTheDomain()
    {
        var tagId = await _host.SeedTagAsync("api-set-tags");
        var domainId = await _host.SeedDomainAsync("api-set-tags.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var tagsResponse = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/tags", new { tagIds = new[] { tagId } });
        var monitoringResponse = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/monitoring", new { monitored = false });

        Assert.Equal(HttpStatusCode.NoContent, tagsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, monitoringResponse.StatusCode);
        await using var context = _host.CreateContext();
        var domain = await context.Domains.Include(candidate => candidate.Tags).SingleAsync(candidate => candidate.Id == domainId);
        Assert.Equal([tagId], domain.Tags.Select(tag => tag.Id));
        Assert.False(domain.IsMonitored);
    }

    [Fact]
    public async Task SetMonitoring_WithoutAValue_Is400()
    {
        var domainId = await _host.SeedDomainAsync("api-monitoring-missing.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/monitoring", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AScopedKey_NamingAGroupIdThatDoesntExist_Is403_SoItCantTellMissingFromHidden()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-scoped-missing-own");
        var domainId = await _host.SeedDomainAsync("api-scoped-missing.example", groupIds: [ownGroupId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit], scopedGroupIds: [ownGroupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PutAsJsonAsync($"/api/v1/domains/{domainId}/groups", new { groupIds = new[] { 987654 } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AWrongMethod_Is405WithTheAllowedMethods()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.DeleteAsync("/api/v1/domains");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["GET", "POST"], response.Content.Headers.Allow.Order());
    }

    [Fact]
    public async Task AnIdThatIsntANumber_IsStill404()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/domains/not-a-number");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJson_SaysWhereTheProblemIs()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync("/api/v1/domains", new StringContent("{\"name\": 42}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = body.RootElement.GetProperty("errors");
        Assert.Contains(errors.EnumerateObject(), error => error.Name.Contains("name"));
    }
}
