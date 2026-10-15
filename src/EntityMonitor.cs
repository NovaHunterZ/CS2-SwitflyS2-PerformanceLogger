using SwiftlyS2.Shared;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace PerformanceLogger;

/// <summary>
/// Counts entities. A count that keeps climbing usually means something spawns entities and
/// never removes them (particles, lights, props), which slows everything down over a map.
///
/// Taking the list is cheap; reading every entity's class name isn't, so the names are read a
/// slice per tick on the game thread instead of all at once.
/// </summary>
internal sealed class EntityMonitor(ISwiftlyCore core)
{
    public readonly record struct Window(int Count, int Change, IReadOnlyList<(string Class, int Count)> Top);

    private const int NamesPerTick = 128;

    private List<CEntityInstance>? _pending;
    private int _next;
    private int _count;
    private readonly Dictionary<string, int> _classes = new();

    private Window? _done;
    private int _lastCount = -1;

    public bool Busy => _pending is not null;

    /// <summary>Counts now; with classes, their names are read over the next ticks. Game thread.</summary>
    public void Start(bool withClasses, int topClasses)
    {
        if (_pending is not null) return;
        List<CEntityInstance> all;
        try { all = core.EntitySystem.GetAllEntities().ToList(); }
        catch { return; }

        _count = all.Count;
        _classes.Clear();
        if (withClasses)
        {
            _pending = all;
            _next = 0;
        }
        else Finish(topClasses);
    }

    /// <summary>A slice of class names. Game thread, every tick while one is going.</summary>
    public void Step(int topClasses)
    {
        if (_pending is null) return;
        var end = Math.Min(_pending.Count, _next + NamesPerTick);
        for (; _next < end; _next++)
        {
            try
            {
                var entity = _pending[_next];
                if (!entity.IsValid) continue; // removed since the list was taken
                var name = entity.DesignerName;
                _classes[name] = _classes.GetValueOrDefault(name) + 1;
            }
            catch { }
        }
        if (_next >= _pending.Count) Finish(topClasses);
    }

    /// <summary>The last finished count, once.</summary>
    public Window? TakeDone()
    {
        var w = _done;
        _done = null;
        return w;
    }

    public void Reset()
    {
        _pending = null;
        _lastCount = -1;
    }

    private void Finish(int topClasses)
    {
        _pending = null;
        var top = _classes.OrderByDescending(kv => kv.Value).Take(Math.Max(0, topClasses)).Select(kv => (kv.Key, kv.Value)).ToList();
        _done = new Window(_count, _lastCount < 0 ? 0 : _count - _lastCount, top);
        _lastCount = _count;
    }
}
