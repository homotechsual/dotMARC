using DotMarc.ServerLogs;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DotMarc.Tests.ServerLogs;

public class InMemoryLogStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Query_ReturnsNewestFirst()
    {
        var store = new InMemoryLogStore(isCapturing: true);
        store.Add(Now, LogLevel.Warning, "A", "first", null);
        store.Add(Now.AddSeconds(1), LogLevel.Warning, "A", "second", null);

        var entries = store.Query(LogLevel.Warning, null, 10);

        Assert.Equal(["second", "first"], entries.Select(e => e.Message));
    }

    [Fact]
    public void Add_DropsTheOldestEntries_OnceCapacityIsReached()
    {
        var store = new InMemoryLogStore(isCapturing: true, capacity: 3);
        for (var index = 1; index <= 5; index++)
        {
            store.Add(Now.AddSeconds(index), LogLevel.Warning, "A", $"entry {index}", null);
        }

        Assert.Equal(3, store.Count);
        Assert.Equal(["entry 5", "entry 4", "entry 3"], store.Query(LogLevel.Warning, null, 10).Select(e => e.Message));
        Assert.Equal(Now.AddSeconds(3), store.OldestTimestampUtc);
    }

    [Fact]
    public void Query_FiltersByMinimumLevel()
    {
        var store = new InMemoryLogStore(isCapturing: true);
        store.Add(Now, LogLevel.Information, "A", "info", null);
        store.Add(Now, LogLevel.Warning, "A", "warn", null);
        store.Add(Now, LogLevel.Error, "A", "error", null);

        Assert.Equal(["error"], store.Query(LogLevel.Error, null, 10).Select(e => e.Message));
        Assert.Equal(["error", "warn"], store.Query(LogLevel.Warning, null, 10).Select(e => e.Message));
        Assert.Equal(3, store.Query(LogLevel.Information, null, 10).Count);
    }

    [Fact]
    public void Query_SearchesMessageCategoryAndExceptionText_IgnoringCase()
    {
        var store = new InMemoryLogStore(isCapturing: true);
        store.Add(Now, LogLevel.Warning, "DotMarc.Halo", "loading failed", null);
        store.Add(Now, LogLevel.Warning, "Other", "unrelated", "System.FormatException: bad Priority id");
        store.Add(Now, LogLevel.Warning, "Other", "nothing to see", null);

        Assert.Single(store.Query(LogLevel.Warning, "PRIORITY", 10));
        Assert.Single(store.Query(LogLevel.Warning, "dotmarc.halo", 10));
        Assert.Single(store.Query(LogLevel.Warning, "loading", 10));
        Assert.Empty(store.Query(LogLevel.Warning, "absent", 10));
    }

    [Fact]
    public void Query_HonoursTheTakeLimit()
    {
        var store = new InMemoryLogStore(isCapturing: true);
        for (var index = 0; index < 10; index++)
        {
            store.Add(Now, LogLevel.Warning, "A", $"entry {index}", null);
        }

        Assert.Equal(4, store.Query(LogLevel.Warning, null, 4).Count);
    }

    [Fact]
    public void Add_IsSafeUnderConcurrentWriters()
    {
        var store = new InMemoryLogStore(isCapturing: true, capacity: 100);

        Parallel.For(0, 8, writer =>
        {
            for (var index = 0; index < 500; index++)
            {
                store.Add(Now, LogLevel.Warning, "A", $"writer {writer}", null);
                Assert.NotNull(store.Query(LogLevel.Warning, null, 5));
            }
        });

        Assert.Equal(100, store.Count);
    }
}

public class InMemoryLoggerProviderTests
{
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Information, false)]
    [InlineData("System.Net.Http.HttpClient.IHaloPsaClient.ClientHandler", LogLevel.Information, false)]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning, true)]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Error, true)]
    [InlineData("DotMarc.Ingestion.PollingService", LogLevel.Information, true)]
    [InlineData("DotMarc.Ingestion.PollingService", LogLevel.Debug, false)]
    [InlineData("Microsoft.Hosting.Lifetime", LogLevel.Information, true)]
    public void ShouldCapture_KeepsWarningsAndDotMarcInformation_ButNotFrameworkChatter(string category, LogLevel level, bool expected)
    {
        Assert.Equal(expected, InMemoryLoggerProvider.ShouldCapture(category, level));
    }

    [Fact]
    public void ALoggedException_IsCapturedWithItsTextAndRedactedSecrets()
    {
        var store = new InMemoryLogStore(isCapturing: true);
        using var provider = new InMemoryLoggerProvider(store);
        var logger = provider.CreateLogger("DotMarc.Test");

        logger.LogWarning(new InvalidOperationException("token=abc123 leaked"), "Call to {Url} failed", "https://x.example/cb?client_secret=hunter2&state=ok");

        var entry = Assert.Single(store.Query(LogLevel.Warning, null, 10));
        Assert.Equal("DotMarc.Test", entry.Category);
        Assert.Contains("failed", entry.Message);
        Assert.DoesNotContain("hunter2", entry.Message);
        Assert.Contains("state=ok", entry.Message);
        Assert.Contains("InvalidOperationException", entry.Exception);
        Assert.DoesNotContain("abc123", entry.Exception);
    }

    [Fact]
    public void ADebugMessage_IsNotCaptured()
    {
        var store = new InMemoryLogStore(isCapturing: true);
        using var provider = new InMemoryLoggerProvider(store);

        provider.CreateLogger("DotMarc.Test").LogDebug("noise");

        Assert.Equal(0, store.Count);
    }
}

public class LogRedactorTests
{
    [Theory]
    [InlineData("POST /integrations/halopsa/webhook/AB12CD34EF56 responded 200", "POST /integrations/halopsa/webhook/[redacted] responded 200")]
    [InlineData("GET https://h/cb?code=xyz789&state=s1", "GET https://h/cb?code=[redacted]&state=s1")]
    [InlineData("grant_type=client_credentials&client_secret=hunter2&scope=x", "grant_type=client_credentials&client_secret=[redacted]&scope=x")]
    [InlineData("Authorization: Bearer eyJhbGciOi.payload.sig", "Authorization: Bearer [redacted]")]
    [InlineData("nothing sensitive here, status_code=200", "nothing sensitive here, status_code=200")]
    public void Redact_MasksKnownSecretShapes_AndLeavesTheRestAlone(string input, string expected)
    {
        Assert.Equal(expected, LogRedactor.Redact(input));
    }

    [Fact]
    public void Redact_PassesEmptyTextThrough()
    {
        Assert.Equal("", LogRedactor.Redact(""));
    }
}
