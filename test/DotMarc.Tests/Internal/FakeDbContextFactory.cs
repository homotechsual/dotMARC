using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DotMarc.Tests.Internal;

/// <summary>A minimal IDbContextFactory<DotMarcDbContext> that always points at the same test
/// connection string - used where a real class under test (like
/// UserAccessClaimsTransformation) needs to create its own short-lived contexts rather than
/// being handed one directly.</summary>
internal sealed class FakeDbContextFactory(string connectionString) : IDbContextFactory<DotMarcDbContext>
{
    // Turns EF's "more than one collection Include without a split query" performance warning into
    // a failure, so an unsplit multi-collection query in code under test can't quietly come back
    // (the warning is raised when the query is compiled, so simply running it trips this).
    public DotMarcDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(warnings => warnings.Throw(RelationalEventId.MultipleCollectionIncludeWarning))
            .Options);

    public Task<DotMarcDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
