using DotMarc.Notifications;

namespace DotMarc.Tests.Internal;

internal sealed class FakeSecretStore : ISecretStore
{
    public Dictionary<string, string> Secrets { get; } = [];

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Secrets[key] = value;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Secrets.TryGetValue(key, out var value) ? value : null);
}
