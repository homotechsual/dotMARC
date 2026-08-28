// src/DotMarc/Demo/DemoDataResetService.cs
using DotMarc.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotMarc.Demo;

/// <summary>Resets the demo dataset once a day at DemoOptions.ResetHourUtc. Registered only when
/// Demo:Enabled is true (see Program.cs). The very first seed happens synchronously in
/// Program.cs's own startup block, not here — this service only ever handles the recurring
/// reset, so there's no window where a visitor could sign in before any data exists.</summary>
public sealed class DemoDataResetService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DemoOptions _options;
    private readonly ILogger<DemoDataResetService> _logger;

    public DemoDataResetService(IServiceScopeFactory scopeFactory, IOptions<DemoOptions> options, ILogger<DemoDataResetService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextReset(DateTimeOffset.UtcNow, _options.ResetHourUtc);
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DotMarcDbContext>();
                var nowUtc = DateTimeOffset.UtcNow;
                var dataset = DemoDataGenerator.Generate(new Random(SeedFor(nowUtc)), nowUtc);
                await DemoDataSeeder.ResetAsync(context, dataset, stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Demo dataset reset completed at {NowUtc}.", nowUtc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Demo dataset reset failed; will retry at the next scheduled reset.");
            }
        }
    }

    /// <summary>Deterministic per-UTC-day seed: a container restart between resets reproduces
    /// the same dataset instead of drawing a new random one, while each calendar day still gets
    /// its own variation. internal so tests can verify it directly.</summary>
    internal static int SeedFor(DateTimeOffset nowUtc) => nowUtc.UtcDateTime.Date.GetHashCode();

    /// <summary>internal so tests can verify the scheduling math without waiting on real time —
    /// the only production caller is ExecuteAsync above.</summary>
    internal static TimeSpan GetDelayUntilNextReset(DateTimeOffset nowUtc, int resetHourUtc)
    {
        var todayReset = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, resetHourUtc, 0, 0, TimeSpan.Zero);
        var nextReset = nowUtc < todayReset ? todayReset : todayReset.AddDays(1);
        return nextReset - nowUtc;
    }
}
