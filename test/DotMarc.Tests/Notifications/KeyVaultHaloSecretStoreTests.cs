using System.Net;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Secrets;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class KeyVaultHaloSecretStoreTests
{
    private static (KeyVaultHaloSecretStore store, FakeHttpMessageHandler handler) CreateStore()
    {
        var handler = new FakeHttpMessageHandler();
        var options = new SecretClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        var client = new SecretClient(new Uri("https://fake-vault.vault.azure.net/"), new FakeTokenCredential(), options);
        return (new KeyVaultHaloSecretStore(client), handler);
    }

    [Fact]
    public async Task SetClientSecretAsync_PutsToTheSecretsEndpoint()
    {
        var (store, handler) = CreateStore();
        handler.ResponseBody = """{"value":"x","id":"https://fake-vault.vault.azure.net/secrets/HaloPsa-ClientSecret/v1"}""";

        await store.SetClientSecretAsync("super-secret-value");

        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("/secrets/HaloPsa-ClientSecret"));
    }

    [Fact]
    public async Task GetClientSecretAsync_ReturnsNull_WhenTheSecretDoesNotExist()
    {
        var (store, handler) = CreateStore();
        handler.StatusCode = HttpStatusCode.NotFound;
        handler.ResponseBody = """{"error":{"code":"SecretNotFound","message":"not found"}}""";

        Assert.Null(await store.GetClientSecretAsync());
    }
}
