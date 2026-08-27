using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Tests.Internal;

/// <summary>A minimal IDbContextFactory<DotMarcDbContext> that always points at the same test
/// connection string — used where a real class under test (like
/// UserAccessClaimsTransformation) needs to create its own short-lived contexts rather than
/// being handed one directly.</summary>
internal sealed class FakeDbContextFactory(string connectionString) : IDbContextFactory<DotMarcDbContext>
{
    public DotMarcDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);

    public Task<DotMarcDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
