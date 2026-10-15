using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PerformanceLogger;

/// <summary>
/// Writes log lines from a background thread. The SwiftlyS2 logger draws every line to the console
/// right away, which costs the game thread around a millisecond a line, and a performance logger
/// shouldn't be the thing causing slow frames.
/// </summary>
internal sealed class QueuedLog : IDisposable
{
    private readonly ILogger _inner;
    private readonly BlockingCollection<string> _lines = new(512);
    private readonly Thread _writer;

    public QueuedLog(ILogger inner)
    {
        _inner = inner;
        _writer = new Thread(Run) { IsBackground = true, Name = "PerfLogger.Log" };
        _writer.Start();
    }

    public void Info(string line)
    {
        if (_lines.IsAddingCompleted) { _inner.LogInformation("{Line}", line); return; }
        try { _lines.TryAdd(line); } catch (InvalidOperationException) { }
    }

    private void Run()
    {
        foreach (var line in _lines.GetConsumingEnumerable())
        {
            try { _inner.LogInformation("{Line}", line); } catch { }
        }
    }

    public void Dispose()
    {
        if (_lines.IsAddingCompleted) return;
        _lines.CompleteAdding();
        _writer.Join(2000);
    }
}
