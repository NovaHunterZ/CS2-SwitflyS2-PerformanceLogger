namespace PerformanceLogger;

public class PerfConfig
{
    /// <summary>Turn everything off without unloading the plugin.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often a summary is written, in seconds.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>A frame counts as slow when it takes this many times the tick interval (1.5 = 23.4 ms at 64 tick).</summary>
    public float SlowFrameFactor { get; set; } = 1.5f;

    /// <summary>Write every summary to a CSV file in the plugin's data folder, handy for spreadsheets.</summary>
    public bool WriteCsv { get; set; } = true;

    /// <summary>Main thread and process CPU, steal time and core clock. Linux only, skipped elsewhere.</summary>
    public bool TrackCpu { get; set; } = true;

    /// <summary>Garbage collections: how many, why, and how long the server was paused.</summary>
    public bool TrackGc { get; set; } = true;

    /// <summary>Which types are allocated the most (sampled by the runtime, about one sample per 100 KB).</summary>
    public bool TrackAllocations { get; set; } = true;

    /// <summary>How many types to list.</summary>
    public int TopAllocationTypes { get; set; } = 6;

    /// <summary>Lines printed to the server console, grouped by pattern (numbers and names stripped).</summary>
    public bool TrackConsole { get; set; } = true;

    /// <summary>How many console patterns to list.</summary>
    public int TopConsolePatterns { get; set; } = 6;

    /// <summary>Lines each plugin wrote to the SwiftlyS2 log file.</summary>
    public bool TrackPluginLogs { get; set; } = true;

    /// <summary>Entity count, and the most common entity classes every few summaries.</summary>
    public bool TrackEntities { get; set; } = true;

    /// <summary>List entity classes every this many summaries (counting is spread over a few ticks).</summary>
    public int EntityClassesEvery { get; set; } = 5;

    /// <summary>How many entity classes to list.</summary>
    public int TopEntityClasses { get; set; } = 8;

    /// <summary>Permission needed for the perflog command in game. The server console can always use it.</summary>
    public string AdminPermission { get; set; } = "perflogger.admin";
}
