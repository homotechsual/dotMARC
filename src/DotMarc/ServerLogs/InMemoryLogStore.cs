using Microsoft.Extensions.Logging;

namespace DotMarc.ServerLogs;

public sealed record LogEntry(long Sequence, DateTimeOffset TimestampUtc, LogLevel Level, string Category, string Message, string? Exception);

/// <summary>A fixed-size, in-memory ring of recent log entries, read by the Server logs page. It is
/// deliberately not a durable log: it holds only this instance's own recent entries, and empties on
/// every restart or redeploy. Its job is to make "what just went wrong" answerable from inside the
/// app; history and multi-instance views belong to the host's own log tooling.</summary>
public sealed class InMemoryLogStore
{
    public const int DefaultCapacity = 2000;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();
    private long _lastSequence;

    public InMemoryLogStore(bool isCapturing, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        IsCapturing = isCapturing;
        Capacity = capacity;
    }

    /// <summary>False when nothing feeds this store (a demo instance), so the page can say so
    /// instead of showing an empty list that looks like "no problems".</summary>
    public bool IsCapturing { get; }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public DateTimeOffset? OldestTimestampUtc
    {
        get
        {
            lock (_gate)
            {
                return _entries.TryPeek(out var oldest) ? oldest.TimestampUtc : null;
            }
        }
    }

    public void Add(DateTimeOffset timestampUtc, LogLevel level, string category, string message, string? exception)
    {
        lock (_gate)
        {
            _entries.Enqueue(new LogEntry(++_lastSequence, timestampUtc, level, category, message, exception));
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    /// <summary>Newest first. <paramref name="search"/> matches the message, category or exception
    /// text, ignoring case.</summary>
    public IReadOnlyList<LogEntry> Query(LogLevel minimumLevel, string? search, int take)
    {
        LogEntry[] snapshot;
        lock (_gate)
        {
            snapshot = _entries.ToArray();
        }

        var results = new List<LogEntry>();
        for (var index = snapshot.Length - 1; index >= 0 && results.Count < take; index--)
        {
            var entry = snapshot[index];
            if (entry.Level >= minimumLevel && (string.IsNullOrWhiteSpace(search) || Matches(entry, search.Trim())))
            {
                results.Add(entry);
            }
        }

        return results;
    }

    private static bool Matches(LogEntry entry, string search) =>
        entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
        || entry.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (entry.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
}
