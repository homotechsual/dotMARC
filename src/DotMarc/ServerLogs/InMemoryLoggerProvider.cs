using Microsoft.Extensions.Logging;

namespace DotMarc.ServerLogs;

/// <summary>Feeds <see cref="InMemoryLogStore"/> from the normal logging pipeline. It keeps
/// everything at Warning and above, plus Information from dotMARC's own categories and the host
/// lifecycle (so restarts are visible). Framework chatter such as every EF Core SQL command is left
/// out: at this app's polling rate it would push the entries worth reading out of the buffer within
/// minutes.</summary>
public sealed class InMemoryLoggerProvider(InMemoryLogStore store) : ILoggerProvider
{
    private const int MaxExceptionLength = 8000;

    public ILogger CreateLogger(string categoryName) => new StoreLogger(store, categoryName);

    public void Dispose()
    {
    }

    public static bool ShouldCapture(string category, LogLevel level) =>
        level >= LogLevel.Warning
        || (level >= LogLevel.Information
            && (category.StartsWith("DotMarc.", StringComparison.Ordinal) || category == "Microsoft.Hosting.Lifetime"));

    private sealed class StoreLogger(InMemoryLogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => ShouldCapture(category, logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = LogRedactor.Redact(formatter(state, exception));
            var exceptionText = exception is null ? null : LogRedactor.Redact(Truncate(exception.ToString()));
            store.Add(DateTimeOffset.UtcNow, logLevel, category, message, exceptionText);
        }

        private static string Truncate(string text) =>
            text.Length <= MaxExceptionLength ? text : text[..MaxExceptionLength] + "... (truncated)";
    }
}
