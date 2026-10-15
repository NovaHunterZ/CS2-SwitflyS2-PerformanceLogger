namespace PerformanceLogger;

/// <summary>
/// How busy the CPU is, read from /proc on Linux. The number that matters most is the game
/// thread: a CS2 server runs its simulation on one thread, so when that thread is near 100% the
/// server is CPU bound, no matter how idle the other cores look. Steal time shows a VPS host
/// taking the CPU away, and the core clock shows throttling or a power-saving governor.
///
/// Files are read from the summary thread, never the game thread.
/// </summary>
internal sealed class CpuMonitor
{
    public readonly record struct Window(bool Available, double MainThreadPercent, double ProcessPercent,
        double StealPercent, int Core, double CoreGhz, int CpuCount);

    private const double ClockTicks = 100.0; // USER_HZ, 100 on every common Linux build

    private readonly bool _linux = OperatingSystem.IsLinux();
    private string? _mainThreadStat;
    private long _lastMain, _lastProcess, _lastSteal, _lastTotal;
    private DateTime _lastAt;

    /// <summary>Call once from the game thread, so we know which thread to watch.</summary>
    public void CaptureMainThread()
    {
        if (!_linux || _mainThreadStat is not null) return;
        try
        {
            var target = new FileInfo("/proc/thread-self").LinkTarget; // "<pid>/task/<tid>"
            if (target is null) return;
            var tid = target.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
            _mainThreadStat = $"/proc/self/task/{tid}/stat";
        }
        catch { }
    }

    public Window Take()
    {
        if (!_linux) return default;
        try
        {
            var now = DateTime.UtcNow;
            var (main, core) = _mainThreadStat is { } path ? ReadThread(path) : (0L, -1);
            var (process, _) = ReadThread("/proc/self/stat");
            var (steal, total) = ReadSteal();

            var first = _lastAt == default;
            var seconds = (now - _lastAt).TotalSeconds;
            var w = first || seconds <= 0
                ? new Window(false, 0, 0, 0, core, 0, Environment.ProcessorCount)
                : new Window(true,
                    100.0 * (main - _lastMain) / ClockTicks / seconds,
                    100.0 * (process - _lastProcess) / ClockTicks / seconds,
                    total > _lastTotal ? 100.0 * (steal - _lastSteal) / (total - _lastTotal) : 0,
                    core, CoreGhz(core), Environment.ProcessorCount);

            _lastMain = main;
            _lastProcess = process;
            _lastSteal = steal;
            _lastTotal = total;
            _lastAt = now;
            return w;
        }
        catch
        {
            return default;
        }
    }

    /// <summary>utime + stime and the core it last ran on, from a /proc stat line.</summary>
    private static (long Ticks, int Core) ReadThread(string path)
    {
        var line = File.ReadAllText(path);
        // The name in brackets can hold spaces, so split after the closing bracket.
        var f = line[(line.LastIndexOf(')') + 2)..].Split(' ');
        // f[0] is field 3 (state), so field n is f[n - 3].
        return (long.Parse(f[11]) + long.Parse(f[12]), int.Parse(f[36]));
    }

    private static (long Steal, long Total) ReadSteal()
    {
        using var reader = new StreamReader("/proc/stat");
        var f = reader.ReadLine()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long total = 0;
        for (var i = 1; i < f.Length; i++) total += long.Parse(f[i]);
        return (f.Length > 8 ? long.Parse(f[8]) : 0, total);
    }

    private static double CoreGhz(int core)
    {
        if (core < 0) return 0;
        var path = $"/sys/devices/system/cpu/cpu{core}/cpufreq/scaling_cur_freq";
        return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var khz) ? khz / 1_000_000.0 : 0;
    }
}
