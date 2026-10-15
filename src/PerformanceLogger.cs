using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Plugins;

namespace PerformanceLogger;

[PluginMetadata(
    Id          = "PerformanceLogger",
    Version     = "1.0.0",
    Name        = "Performance Logger",
    Author      = "Denied",
    Website     = "https://novazombie.com",
    Description = "Finds what slows your server down: frame times, CPU, garbage collection, console spam, plugin logs and entities."
)]
public sealed class PerformanceLogger : BasePlugin
{
    private const string Tag = "[PerfLogger]";

    private readonly PerfConfig _defaults = new();
    private IOptionsMonitor<PerfConfig>? _config;
    private PerfConfig Config => _config?.CurrentValue ?? _defaults;

    private QueuedLog? _log;
    private readonly FrameMonitor _frames = new();
    private readonly CpuMonitor _cpu = new();
    private readonly ConsoleMonitor _console = new();
    private GcMonitor? _gc;
    private PluginLogMonitor? _pluginLogs;
    private EntityMonitor? _entities;

    private bool _started;
    private long _nextSummaryAt;
    private int _summaryCount;
    private int _summaryRunning;
    private string _mapName = "";

    private readonly object _historyGate = new();
    private readonly List<string[]> _history = new();
    private const int HistorySize = 60;

    public PerformanceLogger(ISwiftlyCore core) : base(core) { }

    public override void Load(bool hotReload)
    {
        Core.Configuration
            .InitializeJsonWithModel<PerfConfig>("config.jsonc", "PerformanceLogger")
            .Configure(builder => builder.AddJsonFile("config.jsonc", optional: false, reloadOnChange: true));

        var services = new ServiceCollection();
        services.AddSwiftly(Core)
                .AddOptionsWithValidateOnStart<PerfConfig>()
                .BindConfiguration("PerformanceLogger");
        _config = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<PerfConfig>>();

        _log = new QueuedLog(Core.Logger);
        _entities = new EntityMonitor(Core);
        _pluginLogs = new PluginLogMonitor(FindManagedLogs());
        if (Config.TrackGc) _gc = new GcMonitor(Config.TrackAllocations);

        _log.Info($"{Tag} Loaded. A summary every {Config.IntervalSeconds} s; type perflog for the latest.");
    }

    public override void Unload()
    {
        _gc?.Dispose();
        _gc = null;
        _log?.Dispose();
        _log = null;
    }

    // ------------------------------------------------------------------
    // Game thread
    // ------------------------------------------------------------------

    [EventListener<EventDelegates.OnTick>]
    public void OnTick()
    {
        var cfg = Config;
        if (!cfg.Enabled) return;

        if (!_started)
        {
            _started = true;
            _cpu.CaptureMainThread();
            _frames.Configure(Core.Engine.GlobalVars.IntervalPerTick * 1000.0, cfg.SlowFrameFactor);
            _nextSummaryAt = Stopwatch.GetTimestamp() + Seconds(cfg.IntervalSeconds);
        }

        _frames.OnTick();
        _entities?.Step(cfg.TopEntityClasses);

        if (Stopwatch.GetTimestamp() < _nextSummaryAt) return;
        _nextSummaryAt = Stopwatch.GetTimestamp() + Seconds(cfg.IntervalSeconds);
        Summarize(cfg);
    }

    [EventListener<EventDelegates.OnWorldUpdate>]
    public void OnWorldUpdate()
    {
        if (Config.Enabled) _frames.OnWorldUpdate();
    }

    [EventListener<EventDelegates.OnConsoleOutput>]
    public void OnConsoleOutput(IOnConsoleOutputEvent @event)
    {
        if (Config is { Enabled: true, TrackConsole: true }) _console.OnLine(@event.Message);
    }

    [EventListener<EventDelegates.OnMapLoad>]
    public void OnMapLoad(IOnMapLoadEvent @event)
    {
        _mapName = @event.MapName;
        _frames.Reset();
        _entities?.Reset();
    }

    [EventListener<EventDelegates.OnMapUnload>]
    public void OnMapUnload(IOnMapUnloadEvent @event) => _frames.Reset();

    /// <summary>Grabs what has to be read on the game thread, then leaves the rest to a worker.</summary>
    private void Summarize(PerfConfig cfg)
    {
        var frames = _frames.Take();
        var entities = _entities?.TakeDone();
        _summaryCount++;
        if (cfg.TrackEntities && _entities is { Busy: false } counter)
            counter.Start(_summaryCount % Math.Max(1, cfg.EntityClassesEvery) == 0, cfg.TopEntityClasses);

        int players = 0, bots = 0;
        foreach (var p in Core.PlayerManager.GetAllPlayers())
        {
            if (p is not { IsValid: true }) continue;
            players++;
            if (p.IsFakeClient) bots++;
        }
        if (_mapName.Length == 0) _mapName = Core.Engine.GlobalVars.MapName.Value;
        var map = _mapName;

        // One summary at a time; if the last one is still writing, this one is skipped.
        if (Interlocked.Exchange(ref _summaryRunning, 1) == 1) return;
        Task.Run(() =>
        {
            try { Report(cfg, frames, entities, players, bots, map); }
            catch (Exception ex) { _log?.Info($"{Tag} Summary failed: {ex.Message}"); }
            finally { Volatile.Write(ref _summaryRunning, 0); }
        });
    }

    // ------------------------------------------------------------------
    // Summary thread
    // ------------------------------------------------------------------

    private void Report(PerfConfig cfg, FrameMonitor.Window f, EntityMonitor.Window? entities, int players, int bots, string map)
    {
        var lines = new List<string>();
        var ci = CultureInfo.InvariantCulture;
        string F(double v, int d = 1) => v.ToString("F" + d, ci);

        var tick = f.BudgetMs > 0 ? 1000.0 / f.BudgetMs : 0;
        lines.Add($"{Tag} Frames (last {cfg.IntervalSeconds} s, {F(tick, 0)} tick, {F(f.BudgetMs)} ms each): {f.Frames} frames, " +
                  $"average {F(f.AvgMs, 2)} ms, worst {F(f.WorstMs)} ms{(f.Frames > 0 && f.WorstMs > 0 ? $" at {f.WorstAt:HH:mm:ss}" : "")}. " +
                  $"Slow (over {F(f.BudgetMs * cfg.SlowFrameFactor)} ms): {f.Slow}, {f.VerySlow} of them over twice the budget, {f.SlowWithGc} during a GC. " +
                  $"On time {f.UnderBudget} / up to 2x {f.UpTo2x} / up to 4x {f.UpTo4x} / worse {f.Over4x}.");
        if (f.WorldAvgMs > 0)
            lines.Add($"{Tag} World update to end of frame: average {F(f.WorldAvgMs, 2)} ms, worst {F(f.WorldWorstMs)} ms.");

        var cpu = cfg.TrackCpu ? _cpu.Take() : default;
        if (cpu.Available)
            lines.Add($"{Tag} CPU: game thread busy {F(cpu.MainThreadPercent, 0)}%{(cpu.Core >= 0 ? $" (on core {cpu.Core}{(cpu.CoreGhz > 0 ? $" at {F(cpu.CoreGhz, 2)} GHz" : "")})" : "")}, " +
                      $"whole process {F(cpu.ProcessPercent, 0)}% of one core ({cpu.CpuCount} cores), steal {F(cpu.StealPercent)}%.");

        GcMonitor.Window? gc = _gc?.Take(cfg.TrackAllocations ? cfg.TopAllocationTypes : 0);
        if (gc is { } g)
        {
            var total = g.Gen0 + g.Gen1 + g.Gen2;
            var types = g.TopTypes.Count > 0 ? " Most allocated: " + string.Join(", ", g.TopTypes.Select(t => $"{t.Type} {F(t.Share, 0)}%")) + "." : "";
            lines.Add($"{Tag} GC: {total} collections (gen 0: {g.Gen0}, gen 1: {g.Gen1}, gen 2: {g.Gen2}{(g.Induced > 0 ? $", {g.Induced} forced by code" : "")}), " +
                      $"paused {F(g.PauseMs)} ms in total, longest {F(g.WorstPauseMs)} ms. Allocated {F(g.AllocatedBytes / 1048576.0)} MB, heap {F(g.HeapBytes / 1048576.0, 0)} MB.{types}");
        }

        ConsoleMonitor.Window? console = cfg.TrackConsole ? _console.Take(cfg.TopConsolePatterns, Tag) : null;
        if (console is { } c)
        {
            var top = c.Top.Count > 0 ? " Most: " + string.Join(" | ", c.Top.Select(t => $"{t.Count}x {t.Pattern}")) : "";
            lines.Add($"{Tag} Console: {c.Lines} lines{(c.Dropped > 0 ? $" (and {c.Dropped} more not looked at)" : "")}.{top}");
        }

        if (cfg.TrackPluginLogs && _pluginLogs?.Take("PerformanceLogger") is { Available: true } logs)
        {
            var list = logs.Plugins.Count == 0 ? "none" : string.Join(", ", logs.Plugins.Take(10).Select(p => $"{p.Plugin} {p.Lines}{(p.Warnings > 0 ? $" ({p.Warnings} warnings)" : "")}"));
            lines.Add($"{Tag} Plugin log lines: {list}.");
        }

        if (entities is { } e)
        {
            var top = e.Top.Count > 0 ? " Most: " + string.Join(", ", e.Top.Select(t => $"{t.Class} {t.Count}")) + "." : "";
            lines.Add($"{Tag} Entities: {e.Count} ({(e.Change >= 0 ? "+" : "")}{e.Change}).{top}");
        }

        lines.Add($"{Tag} Players: {players} ({bots} bots) on {map}.");

        var hints = Hints(f, cpu, gc, console, cfg);
        if (hints.Count > 0) lines.Add($"{Tag} Worth a look: {string.Join(" ", hints)}");

        foreach (var line in lines) _log?.Info(line);
        lock (_historyGate)
        {
            _history.Add([DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", ci), .. lines]);
            if (_history.Count > HistorySize) _history.RemoveAt(0);
        }

        if (cfg.WriteCsv) WriteCsv(f, cpu, gc, console, entities, players, bots, map);
    }

    /// <summary>Plain-language pointers when a number stands out.</summary>
    private static List<string> Hints(FrameMonitor.Window f, CpuMonitor.Window cpu, GcMonitor.Window? gc, ConsoleMonitor.Window? console, PerfConfig cfg)
    {
        var hints = new List<string>();
        if (cpu.Available && cpu.MainThreadPercent >= 90)
            hints.Add("The game thread is nearly maxed out: the server is CPU bound (fewer bots, lighter plugins, or a faster core).");
        if (cpu.Available && cpu.StealPercent >= 5)
            hints.Add("High steal time: the host is giving your CPU to someone else.");
        if (f.Slow > 0 && f.SlowWithGc * 2 >= f.Slow)
            hints.Add("Most slow frames had a GC in them: something allocates a lot (see the GC line).");
        if (gc is { Induced: > 0 })
            hints.Add("Some code calls GC.Collect; that pauses the whole server.");
        if (console is { } c && c.Lines >= 300 * cfg.IntervalSeconds / 60)
            hints.Add("A lot of console output; every line costs a little (see the Console line).");
        return hints;
    }

    private void WriteCsv(FrameMonitor.Window f, CpuMonitor.Window cpu, GcMonitor.Window? gc, ConsoleMonitor.Window? console,
        EntityMonitor.Window? entities, int players, int bots, string map)
    {
        try
        {
            Directory.CreateDirectory(Core.PluginDataDirectory);
            var path = Path.Combine(Core.PluginDataDirectory, $"perf-{DateTime.Now:yyyy-MM-dd}.csv");
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            if (!File.Exists(path))
                sb.AppendLine("time,map,players,bots,frames,avg_ms,worst_ms,slow,very_slow,slow_with_gc,world_avg_ms,world_worst_ms," +
                              "game_thread_cpu,process_cpu,steal,core_ghz,gc0,gc1,gc2,gc_pause_ms,gc_worst_ms,alloc_mb,heap_mb,console_lines,entities");
            string N(double v) => v.ToString("0.###", ci);
            var g = gc ?? default;
            sb.AppendLine(string.Join(',',
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", ci), map.Replace(",", " "), players, bots, f.Frames, N(f.AvgMs), N(f.WorstMs),
                f.Slow, f.VerySlow, f.SlowWithGc, N(f.WorldAvgMs), N(f.WorldWorstMs),
                cpu.Available ? N(cpu.MainThreadPercent) : "", cpu.Available ? N(cpu.ProcessPercent) : "", cpu.Available ? N(cpu.StealPercent) : "",
                cpu.Available ? N(cpu.CoreGhz) : "", g.Gen0, g.Gen1, g.Gen2, N(g.PauseMs), N(g.WorstPauseMs),
                N(g.AllocatedBytes / 1048576.0), N(g.HeapBytes / 1048576.0), console?.Lines ?? 0, entities?.Count.ToString(ci) ?? ""));
            File.AppendAllText(path, sb.ToString());
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // Command
    // ------------------------------------------------------------------

    [Command("perflog", helpText: "Shows the latest performance summary. 'perflog report' writes the last hour to a file.")]
    public void OnPerfLog(ICommandContext ctx)
    {
        if (ctx.IsSentByPlayer && ctx.Sender is { } sender
            && !Core.Permission.PlayerHasPermission(sender.SteamID, Config.AdminPermission))
        {
            ctx.Reply($"{Tag} You don't have access to this command.");
            return;
        }

        if (ctx.Args.Length > 0 && ctx.Args[0].Equals("report", StringComparison.OrdinalIgnoreCase))
        {
            var path = WriteReport();
            ctx.Reply(path is null ? $"{Tag} Nothing to report yet." : $"{Tag} Report written to {path}");
            return;
        }

        string[]? last;
        lock (_historyGate) last = _history.Count > 0 ? _history[^1] : null;
        if (last is null)
        {
            ctx.Reply($"{Tag} No summary yet; the first one comes {Config.IntervalSeconds} s after the plugin starts.");
            return;
        }
        ctx.Reply($"{Tag} Summary from {last[0]}:");
        foreach (var line in last.Skip(1)) ctx.Reply(line);
    }

    private string? WriteReport()
    {
        List<string[]> history;
        lock (_historyGate) history = _history.ToList();
        if (history.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"Performance report, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Map: {_mapName}   Cores: {Environment.ProcessorCount}   .NET: {Environment.Version}   OS: {Environment.OSVersion}");
        var plugins = InstalledPlugins();
        if (plugins.Count > 0) sb.AppendLine($"Plugins installed ({plugins.Count}): {string.Join(", ", plugins)}");
        sb.AppendLine();
        foreach (var entry in history)
        {
            sb.AppendLine($"== {entry[0]}");
            foreach (var line in entry.Skip(1)) sb.AppendLine(line);
            sb.AppendLine();
        }

        try
        {
            Directory.CreateDirectory(Core.PluginDataDirectory);
            var path = Path.Combine(Core.PluginDataDirectory, $"report-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception ex)
        {
            _log?.Info($"{Tag} Couldn't write the report: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------

    private static long Seconds(int seconds) => Math.Max(5, seconds) * Stopwatch.Frequency;

    /// <summary>The plugin folders next to ours, for the report.</summary>
    private List<string> InstalledPlugins()
    {
        try
        {
            var dir = Directory.GetParent(Core.PluginPath)?.FullName;
            return dir is null ? [] : Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToList();
        }
        catch { return []; }
    }

    /// <summary>SwiftlyS2 keeps its managed log in logs/managed, next to the plugins folder.</summary>
    private string FindManagedLogs()
    {
        try
        {
            var plugins = Directory.GetParent(Core.PluginPath);
            var root = plugins?.Parent;
            if (root is not null)
            {
                var dir = Path.Combine(root.FullName, "logs", "managed");
                if (Directory.Exists(dir)) return dir;
            }
        }
        catch { }
#pragma warning disable CS0618 // the folder we want is game/csgo, which is what this one is
        return Path.Combine(Core.CSGODirectory, "addons", "swiftlys2", "logs", "managed");
#pragma warning restore CS0618
    }
}
