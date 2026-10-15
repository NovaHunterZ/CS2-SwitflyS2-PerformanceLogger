using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace PerformanceLogger;

/// <summary>
/// Counts what gets printed to the server console. Every line costs the game thread something,
/// and some sources print thousands an hour (the game's damage log, engine warnings, a chatty
/// plugin). Lines are only queued here; grouping them into patterns happens on the summary thread.
/// </summary>
internal sealed partial class ConsoleMonitor
{
    public readonly record struct Window(int Lines, int Dropped, IReadOnlyList<(string Pattern, int Count)> Top);

    private const int MaxQueued = 20000;

    private readonly ConcurrentQueue<string> _queue = new();
    private int _queued;
    private int _dropped;

    /// <summary>From the console output event: as cheap as possible, whatever thread it is.</summary>
    public void OnLine(string message)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _queue.Enqueue(message);
    }

    public Window Take(int top, string ownPrefix)
    {
        var counts = new Dictionary<string, int>();
        var lines = 0;
        while (_queue.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _queued);
            if (line.Contains(ownPrefix, StringComparison.Ordinal)) continue;
            lines++;
            var pattern = Pattern(line);
            if (pattern.Length == 0) continue;
            counts[pattern] = counts.GetValueOrDefault(pattern) + 1;
        }
        var list = counts.OrderByDescending(kv => kv.Value).Take(Math.Max(0, top)).Select(kv => (kv.Key, kv.Value)).ToList();
        return new Window(lines, Interlocked.Exchange(ref _dropped, 0), list);
    }

    /// <summary>"L 10/12/2026 - 20:01:13: "Bob<3><BOT>" attacked..." becomes ""…" attacked…": names, numbers and colour codes go.</summary>
    private static string Pattern(string line)
    {
        var s = Ansi().Replace(line, "");
        s = LogDate().Replace(s, "");
        s = Quoted().Replace(s, "\"…\"");
        s = Numbers().Replace(s, "#");
        s = s.Trim();
        return s.Length > 90 ? s[..90] + "…" : s;
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*m")] private static partial Regex Ansi();
    [GeneratedRegex(@"^L \d+/\d+/\d+ - \d+:\d+:\d+: ")] private static partial Regex LogDate();
    [GeneratedRegex("\"[^\"]*\"")] private static partial Regex Quoted();
    [GeneratedRegex(@"-?\d+(\.\d+)?")] private static partial Regex Numbers();
}
