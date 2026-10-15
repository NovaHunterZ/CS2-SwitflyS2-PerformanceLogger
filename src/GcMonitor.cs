using System.Diagnostics.Tracing;

namespace PerformanceLogger;

/// <summary>
/// Listens to the .NET runtime's own GC events, in process. A collection stops every managed
/// thread, the game thread included, so its pause shows up as a slow frame wherever it lands.
/// This counts them per generation and reason, times each pause, and (if asked) tallies which
/// types get allocated the most from the runtime's allocation samples.
///
/// Events arrive on whatever thread raised them, so everything here is under one small lock.
/// </summary>
internal sealed class GcMonitor : EventListener
{
    public readonly record struct Window(
        int Gen0, int Gen1, int Gen2, int Induced, double PauseMs, double WorstPauseMs,
        long AllocatedBytes, long HeapBytes, IReadOnlyList<(string Type, double Share)> TopTypes);

    private const int GcKeyword = 0x1;

    private readonly object _gate = new();
    private readonly bool _allocations;
    private EventSource? _runtime;

    private int _gen0, _gen1, _gen2, _induced;
    private long _pauseTicks, _worstPause, _suspendAt;
    private readonly Dictionary<string, long> _types = new();
    private long _sampled;
    private long _allocatedAtStart = GC.GetTotalAllocatedBytes();

    public GcMonitor(bool allocations)
    {
        _allocations = allocations;
        // The runtime source can show up before the constructor body runs; enable it now if so.
        if (_runtime is { } source) Enable(source);
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name != "Microsoft-Windows-DotNETRuntime") return;
        _runtime = source;
        Enable(source);
    }

    private void Enable(EventSource source)
        => EnableEvents(source, _allocations ? EventLevel.Verbose : EventLevel.Informational, (EventKeywords)GcKeyword);

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        switch (e.EventName)
        {
            case "GCStart_V2":
            {
                var gen = Payload<uint>(e, "Depth");
                var reason = (int)Payload<uint>(e, "Reason");
                lock (_gate)
                {
                    if (gen == 0) _gen0++;
                    else if (gen == 1) _gen1++;
                    else _gen2++;
                    // Someone called GC.Collect: worth knowing, a healthy server shouldn't do that.
                    if (reason is 1 or 7 or 9 or 10) _induced++;
                }
                break;
            }
            // Events reach us a little later than they happen, so the pause comes from their own
            // timestamps, not from when we see them.
            case "GCSuspendEEBegin_V1":
                lock (_gate) _suspendAt = e.TimeStamp.Ticks;
                break;
            case "GCRestartEEEnd_V1":
                lock (_gate)
                {
                    if (_suspendAt == 0) break;
                    var pause = e.TimeStamp.Ticks - _suspendAt;
                    _suspendAt = 0;
                    if (pause < 0) break;
                    _pauseTicks += pause;
                    if (pause > _worstPause) _worstPause = pause;
                }
                break;
            case "GCAllocationTick_V4" or "GCAllocationTick_V3":
            {
                if (!_allocations) break;
                var type = PayloadString(e, "TypeName");
                var amount = (long)Payload<ulong>(e, "AllocationAmount64");
                if (amount == 0) amount = Payload<uint>(e, "AllocationAmount");
                lock (_gate)
                {
                    _types[type] = _types.GetValueOrDefault(type) + amount;
                    _sampled += amount;
                }
                break;
            }
        }
    }

    public Window Take(int topTypes)
    {
        lock (_gate)
        {
            var top = _types.OrderByDescending(kv => kv.Value).Take(Math.Max(0, topTypes))
                .Select(kv => (Shorten(kv.Key), _sampled > 0 ? 100.0 * kv.Value / _sampled : 0.0)).ToList();
            var allocated = GC.GetTotalAllocatedBytes();
            var w = new Window(_gen0, _gen1, _gen2, _induced, Ms(_pauseTicks), Ms(_worstPause),
                allocated - _allocatedAtStart, GC.GetTotalMemory(false), top);

            _gen0 = _gen1 = _gen2 = _induced = 0;
            _pauseTicks = _worstPause = 0;
            _types.Clear();
            _sampled = 0;
            _allocatedAtStart = allocated;
            return w;
        }
    }

    private static double Ms(long ticks) => ticks / (double)TimeSpan.TicksPerMillisecond;

    /// <summary>Keeps the namespace short enough to read: "Some.Long.Namespace.Type" becomes "S.L.Namespace.Type".</summary>
    private static string Shorten(string type)
    {
        var parts = type.Split('.');
        if (parts.Length <= 3) return type;
        for (var i = 0; i < parts.Length - 2; i++) parts[i] = parts[i].Length > 0 ? parts[i][..1] : parts[i];
        return string.Join('.', parts);
    }

    private static T Payload<T>(EventWrittenEventArgs e, string name) where T : struct
    {
        if (e.PayloadNames is null || e.Payload is null) return default;
        var i = e.PayloadNames.IndexOf(name);
        return i >= 0 && e.Payload[i] is T value ? value : default;
    }

    private static string PayloadString(EventWrittenEventArgs e, string name)
    {
        if (e.PayloadNames is null || e.Payload is null) return "?";
        var i = e.PayloadNames.IndexOf(name);
        return i >= 0 && e.Payload[i] is string s && s.Length > 0 ? s : "?";
    }
}
