using System.Text;

namespace PerformanceLogger;

/// <summary>
/// Counts the lines each plugin writes to the SwiftlyS2 log, by reading what was added to the
/// newest file in logs/managed since last time. With the default logger, each of those lines is
/// also drawn to the console on the game thread, so a busy one costs frame time.
/// Runs on the summary thread.
/// </summary>
internal sealed class PluginLogMonitor(string directory)
{
    public readonly record struct Window(bool Available, IReadOnlyList<(string Plugin, int Lines, int Warnings)> Plugins);

    private string? _file;
    private long _position;
    private string _carry = "";

    public Window Take(string ownName)
    {
        if (!Directory.Exists(directory)) return default;
        try
        {
            var newest = new DirectoryInfo(directory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null) return default;

            // A new file (server restart, new day): start at its end, we only want fresh lines.
            if (newest.FullName != _file)
            {
                _file = newest.FullName;
                _position = newest.Length;
                _carry = "";
                return new Window(true, []);
            }

            using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position) _position = 0;
            stream.Position = _position;
            var bytes = new byte[Math.Min(stream.Length - _position, 64L * 1024 * 1024)];
            var read = stream.Read(bytes, 0, bytes.Length);
            _position += read;

            var text = _carry + Encoding.UTF8.GetString(bytes, 0, read);
            var lastBreak = text.LastIndexOf('\n');
            _carry = lastBreak >= 0 ? text[(lastBreak + 1)..] : text;
            if (lastBreak < 0) return new Window(true, []);

            var counts = new Dictionary<string, (int Lines, int Warnings)>();
            foreach (var line in text.AsSpan(0, lastBreak).ToString().Split('\n'))
            {
                // "Plugin | 10/15 18:03:12 | Information | Category | message"
                var bar = line.IndexOf(" | ", StringComparison.Ordinal);
                if (bar <= 0) continue;
                var plugin = line[..bar];
                if (plugin == ownName) continue;
                var warning = line.Contains("| Warning |", StringComparison.Ordinal) || line.Contains("| Error |", StringComparison.Ordinal);
                var c = counts.GetValueOrDefault(plugin);
                counts[plugin] = (c.Lines + 1, c.Warnings + (warning ? 1 : 0));
            }
            return new Window(true, counts.OrderByDescending(kv => kv.Value.Lines).Select(kv => (kv.Key, kv.Value.Lines, kv.Value.Warnings)).ToList());
        }
        catch
        {
            return default;
        }
    }
}
