using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class TagManagementServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public TagManagementServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task AddTagAsync_AddsATrimmedTagWithColor()
    {
        using var context = CreateContext();

        var result = await TagManagementService.AddTagAsync(context, TestActors.Admin, "  primary  ", Color.Info, CancellationToken.None);

        Assert.Equal(TagManagementService.AddTagResult.Added, result);
        using var verify = CreateContext();
        var tag = verify.Tags.Single();
        Assert.Equal("primary", tag.Name);
        Assert.Equal(Color.Info, tag.Color);
    }

    [Fact]
    public async Task AddTagAsync_RejectsEmptyName()
    {
        using var context = CreateContext();

        var result = await TagManagementService.AddTagAsync(context, TestActors.Admin, "   ", Color.Primary, CancellationToken.None);

        Assert.Equal(TagManagementService.AddTagResult.InvalidName, result);
        Assert.Empty(context.Tags);
    }

    [Fact]
    public async Task AddTagAsync_RejectsCaseInsensitiveDuplicate()
    {
        using var context = CreateContext();
        await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", Color.Primary, CancellationToken.None);

        var result = await TagManagementService.AddTagAsync(context, TestActors.Admin, "PRIMARY", Color.Info, CancellationToken.None);

        Assert.Equal(TagManagementService.AddTagResult.AlreadyExists, result);
    }

    [Fact]
    public async Task AddTagAsync_RejectsColorOutsideTheAllowedPalette()
    {
        using var context = CreateContext();

        var result = await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", Color.Success, CancellationToken.None);

        Assert.Equal(TagManagementService.AddTagResult.InvalidColor, result);
        Assert.Empty(context.Tags);
    }

    [Theory]
    [InlineData(Color.Primary)]
    [InlineData(Color.Secondary)]
    [InlineData(Color.Tertiary)]
    [InlineData(Color.Info)]
    [InlineData(Color.Dark)]
    public async Task AddTagAsync_AcceptsEveryAllowedColor(Color color)
    {
        using var context = CreateContext();

        var result = await TagManagementService.AddTagAsync(context, TestActors.Admin, $"tag-{color}", color, CancellationToken.None);

        Assert.Equal(TagManagementService.AddTagResult.Added, result);
        using var verify = CreateContext();
        var tag = verify.Tags.Single(t => t.Name == $"tag-{color}");
        Assert.Equal(color, tag.Color);
    }

    [Fact]
    public async Task UpdateTagAsync_RejectsColorOutsideTheAllowedPalette()
    {
        using var context = CreateContext();
        await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", Color.Primary, CancellationToken.None);
        var tagId = context.Tags.Single().Id;

        var result = await TagManagementService.UpdateTagAsync(context, TestActors.Admin, tagId, "primary", Color.Warning, CancellationToken.None);

        Assert.Equal(TagManagementService.AddTagResult.InvalidColor, result);
        using var verify = CreateContext();
        Assert.Equal(Color.Primary, verify.Tags.Single().Color);
    }

    [Fact]
    public async Task UpdateTagAsync_UpdatesNameAndColorTogether()
    {
        using var context = CreateContext();
        await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", Color.Primary, CancellationToken.None);
        var tagId = context.Tags.Single().Id;

        var result = await TagManagementService.UpdateTagAsync(context, TestActors.Admin, tagId, "renamed", Color.Dark, CancellationToken.None);

        Assert.Equal(TagManagementService.AddTagResult.Added, result);
        using var verify = CreateContext();
        var tag = verify.Tags.Single();
        Assert.Equal("renamed", tag.Name);
        Assert.Equal(Color.Dark, tag.Color);
    }

    [Fact]
    public async Task UpdateTagAsync_RejectsRenamingToAnExistingName()
    {
        using var context = CreateContext();
        await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", Color.Primary, CancellationToken.None);
        await TagManagementService.AddTagAsync(context, TestActors.Admin, "secondary", Color.Secondary, CancellationToken.None);
        var secondaryId = context.Tags.Single(t => t.Name == "secondary").Id;

        var result = await TagManagementService.UpdateTagAsync(context, TestActors.Admin, secondaryId, "primary", Color.Secondary, CancellationToken.None);

        Assert.Equal(TagManagementService.AddTagResult.AlreadyExists, result);
    }

    [Fact]
    public async Task RemoveTagAsync_RemovesTheTag_ButNotItsMemberDomain()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.io", CancellationToken.None);
        await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", Color.Primary, CancellationToken.None);
        var domainId = context.Domains.Single().Id;
        var tagId = context.Tags.Single().Id;
        await TagManagementService.SetDomainTagsAsync(context, TestActors.Admin, domainId, [tagId], CancellationToken.None);

        await TagManagementService.RemoveTagAsync(context, TestActors.Admin, tagId, CancellationToken.None);

        using var verify = CreateContext();
        Assert.Empty(verify.Tags);
        Assert.NotNull(verify.Domains.SingleOrDefault(d => d.Id == domainId));
    }

    [Fact]
    public async Task SetDomainTagsAsync_ReplacesTheFullMembershipSet()
    {
        using var context = CreateContext();
        await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.io", CancellationToken.None);
        await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", Color.Primary, CancellationToken.None);
        await TagManagementService.AddTagAsync(context, TestActors.Admin, "secondary", Color.Secondary, CancellationToken.None);
        var domainId = context.Domains.Single().Id;
        var primaryId = context.Tags.Single(t => t.Name == "primary").Id;
        var secondaryId = context.Tags.Single(t => t.Name == "secondary").Id;

        await TagManagementService.SetDomainTagsAsync(context, TestActors.Admin, domainId, [primaryId, secondaryId], CancellationToken.None);
        using (var verify1 = CreateContext())
        {
            Assert.Equal(2, verify1.Domains.Include(d => d.Tags).Single().Tags.Count);
        }

        await TagManagementService.SetDomainTagsAsync(context, TestActors.Admin, domainId, [primaryId], CancellationToken.None);

        using var verify2 = CreateContext();
        var updated = verify2.Domains.Include(d => d.Tags).Single();
        Assert.Single(updated.Tags);
        Assert.Equal("primary", updated.Tags[0].Name);
    }

    [Fact]
    public async Task UpdateTagAsync_RecordsNameAndColorChanges()
    {
        await using (var context = CreateContext())
        {
            await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", Color.Primary);
            var tagId = (await context.Tags.SingleAsync()).Id;
            await TagManagementService.UpdateTagAsync(context, TestActors.Admin, tagId, "main", Color.Info);
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync(auditEntry => auditEntry.Action == AuditActions.TagUpdated);
        Assert.Equal([new AuditFieldChange("Name", "primary", "main"), new AuditFieldChange("Color", "Primary", "Info")], entry.Changes);
    }

    [Fact]
    public async Task SetDomainTagsAsync_RecordsNothing_WhenTheTagsAreUnchanged()
    {
        await using (var context = CreateContext())
        {
            await DomainManagementService.AddDomainAsync(context, TestActors.Admin, "contoso.com");
            var domainId = (await context.Domains.SingleAsync()).Id;
            await TagManagementService.SetDomainTagsAsync(context, TestActors.Admin, domainId, []);
        }

        await using var verify = CreateContext();
        Assert.DoesNotContain(verify.AuditEntries, entry => entry.Action == AuditActions.DomainTagsChanged);
    }

    [Fact]
    public async Task RemoveTagAsync_RecordsTheRemovedTag()
    {
        await using (var context = CreateContext())
        {
            await TagManagementService.AddTagAsync(context, TestActors.Admin, "primary", Color.Primary);
            var tagId = (await context.Tags.SingleAsync()).Id;
            await TagManagementService.RemoveTagAsync(context, TestActors.Admin, tagId);
        }

        await using var verify = CreateContext();
        var entry = await verify.AuditEntries.SingleAsync(auditEntry => auditEntry.Action == AuditActions.TagRemoved);
        Assert.Equal(("primary", "Removed tag primary"), (entry.TargetName, entry.Summary));
    }
}