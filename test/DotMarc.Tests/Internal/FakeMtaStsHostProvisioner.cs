using DotMarc.MtaSts;

namespace DotMarc.Tests.Internal;

internal sealed class FakeMtaStsHostProvisioner : IMtaStsHostProvisioner
{
    public bool ShouldThrowOnEnsure { get; set; }

    /// <summary>Thrown by EnsureProvisionedAsync instead of the generic failure, when set.</summary>
    public Exception? ExceptionOnEnsure { get; set; }
    public List<string> ProvisionedDomains { get; } = [];
    public List<string> TornDownDomains { get; } = [];

    public Task EnsureProvisionedAsync(string domainName, CancellationToken cancellationToken)
    {
        ProvisionedDomains.Add(domainName);
        if (ExceptionOnEnsure is not null)
        {
            throw ExceptionOnEnsure;
        }

        if (ShouldThrowOnEnsure)
        {
            throw new InvalidOperationException("Simulated provisioning failure.");
        }
        return Task.CompletedTask;
    }

    public Task TeardownAsync(string domainName, CancellationToken cancellationToken)
    {
        TornDownDomains.Add(domainName);
        return Task.CompletedTask;
    }

    public Task<string?> GetDomainVerificationIdAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
