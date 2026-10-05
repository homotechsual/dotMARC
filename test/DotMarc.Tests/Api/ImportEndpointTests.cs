using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class ImportEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ImportEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Import_AddsDomainsWithGroupsAndTags()
    {
        await _host.SeedGroupAsync("api-import-group");
        await _host.SeedTagAsync("api-import-tag");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd, Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains/import", new
        {
            domains = new object[]
            {
                new { name = "api-import-1.example", groups = new[] { "api-import-group" }, tags = new[] { "api-import-tag" } },
                new { name = "api-import-2.example", monitored = false },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiImportResponse>();
        Assert.False(result!.DryRun);
        Assert.Equal(2, result.Added);
        await using var context = _host.CreateContext();
        var first = await context.Domains.Include(domain => domain.Groups).Include(domain => domain.Tags).SingleAsync(domain => domain.Name == "api-import-1.example");
        Assert.Equal(["api-import-group"], first.Groups.Select(group => group.Name));
        Assert.Equal(["api-import-tag"], first.Tags.Select(tag => tag.Name));
        Assert.False((await context.Domains.SingleAsync(domain => domain.Name == "api-import-2.example")).IsMonitored);
    }

    [Fact]
    public async Task Import_DryRun_ChangesNothing()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains/import?dryRun=true", new { domains = new[] { new { name = "api-dry-run.example" } } });

        var result = await response.Content.ReadFromJsonAsync<ApiImportResponse>();
        Assert.True(result!.DryRun);
        Assert.Equal(1, result.Added);
        Assert.Equal("add", Assert.Single(result.Rows).Outcome);
        await using var context = _host.CreateContext();
        Assert.False(await context.Domains.AnyAsync(domain => domain.Name == "api-dry-run.example"));
    }

    [Fact]
    public async Task Import_UnknownNamesSkipped_ByDefault()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd, Permission.DomainsEdit, Permission.GroupsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains/import", new { domains = new[] { new { name = "api-unknown-skip.example", groups = new[] { "api-never-made" } } } });

        var result = await response.Content.ReadFromJsonAsync<ApiImportResponse>();
        Assert.Equal("api-never-made", Assert.Single(result!.UnknownNames).Name);
        await using var context = _host.CreateContext();
        Assert.False(await context.Groups.AnyAsync(group => group.Name == "api-never-made"));
        Assert.True(await context.Domains.AnyAsync(domain => domain.Name == "api-unknown-skip.example"));
    }

    [Fact]
    public async Task Import_UnknownNamesCreated_WhenAskedAndAllowed()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd, Permission.DomainsEdit, Permission.GroupsAdd]);
        using var client = _host.ClientFor(secret);

        await client.PostAsJsonAsync("/api/v1/domains/import", new { unknownNames = "create", domains = new[] { new { name = "api-unknown-create.example", groups = new[] { "api-made-by-import" } } } });

        await using var context = _host.CreateContext();
        Assert.True(await context.Groups.AnyAsync(group => group.Name == "api-made-by-import"));
    }

    [Fact]
    public async Task Import_UnknownNamesLeftOut_WhenCreateIsAskedButNotAllowed()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd, Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        await client.PostAsJsonAsync("/api/v1/domains/import", new { unknownNames = "create", domains = new[] { new { name = "api-unknown-denied.example", groups = new[] { "api-denied-group" } } } });

        await using var context = _host.CreateContext();
        Assert.False(await context.Groups.AnyAsync(group => group.Name == "api-denied-group"));
    }

    [Fact]
    public async Task MatchMode_LeavesTagsAlone_WhenNoRowSendsTags()
    {
        var oldGroupId = await _host.SeedGroupAsync("api-match-old");
        await _host.SeedGroupAsync("api-match-new");
        var tagId = await _host.SeedTagAsync("api-match-tag");
        await _host.SeedDomainAsync("api-match.example", groupIds: [oldGroupId], tagIds: [tagId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd, Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        await client.PostAsJsonAsync("/api/v1/domains/import", new { existingDomains = "match", domains = new[] { new { name = "api-match.example", groups = new[] { "api-match-new" } } } });

        await using var context = _host.CreateContext();
        var domain = await context.Domains.Include(candidate => candidate.Groups).Include(candidate => candidate.Tags).SingleAsync(candidate => candidate.Name == "api-match.example");
        Assert.Equal(["api-match-new"], domain.Groups.Select(group => group.Name));
        Assert.Equal([tagId], domain.Tags.Select(tag => tag.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Import_RefusesNoneOrTooManyRows(int rowCount)
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains/import",
            new { domains = Enumerable.Range(0, rowCount).Select(index => new { name = $"api-bulk-{index}.example" }).ToArray() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"existingDomains\":\"replace\",\"domains\":[{\"name\":\"a.example\"}]}", "existingDomains")]
    [InlineData("{\"unknownNames\":\"guess\",\"domains\":[{\"name\":\"a.example\"}]}", "unknownNames")]
    [InlineData("{\"domains\":[{\"name\":\"a.example\",\"groups\":[\"semi;colon\"]}]}", "domains")]
    public async Task Import_RefusesBadOptions(string body, string field)
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsync("/api/v1/domains/import", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Import_ByAScopedKey_Is403()
    {
        var groupId = await _host.SeedGroupAsync("api-import-scoped");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsAdd], scopedGroupIds: [groupId]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync("/api/v1/domains/import", new { domains = new[] { new { name = "api-import-scoped.example" } } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
