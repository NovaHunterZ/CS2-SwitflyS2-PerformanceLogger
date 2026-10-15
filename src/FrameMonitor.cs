using System.Diagnostics;

namespace PerformanceLogger;

/// <summary>
/// Times whole server frames. OnTick fires once at the end of every frame, so the gap between two
/// of them is how long that frame really took, sleep included. A server keeping up shows the tick
/// interval (15.6 ms at 64 tick); anything longer is a frame that ran late.
///
/// Runs on the game thread, a timestamp and a few additions per tick.
/// </summary>
internal sealed class FrameMonitor
{
    public readonly record struct Window(
        int Frames, double AvgMs, double WorstMs, DateTime WorstAt,
        int Slow, int VerySlow, int SlowWithGc, double BudgetMs,
        double WorldAvgMs, double WorldWorstMs,
        int UnderBudget, int UpTo2x, int UpTo4x, int Over4x);

    private long _lastTick;
    private long _worldAt;
    private int _lastGcCount;

    private int _frames;
    private long _sum;
    private long _worst;
    private DateTime _worstAt;
    private int _slow, _verySlow, _slowWithGc;
    private int _underBudget, _upTo2x, _upTo4x, _over4x;
    private long _worldSum, _worldWorst;
    private int _worldFrames;

    private double _budgetMs = 15.625;
    private float _slowFactor = 1.5f;

    public void Configure(double budgetMs, float slowFactor)
    {
        if (budgetMs > 1 && budgetMs < 200) _budgetMs = budgetMs;
        _slowFactor = Math.Max(1.05f, slowFactor);
    }

    /// <summary>Map changes stall the server for seconds; don't count that as a frame.</summary>
    public void Reset() => _lastTick = 0;

    /// <summary>The world update starts partway through the frame (entity thinks, physics).</summary>
    public void OnWorldUpdate() => _worldAt = Stopwatch.GetTimestamp();

    public void OnTick()
    {
        var now = Stopwatch.GetTimestamp();
        var gc = GC.CollectionCount(0);

        if (_worldAt > _lastTick && _lastTick != 0)
        {
            var span = now - _worldAt;
            _worldSum += span;
            if (span > _worldWorst) _worldWorst = span;
            _worldFrames++;
        }

        if (_lastTick != 0)
        {
            var frame = now - _lastTick;
            var ms = frame * 1000.0 / Stopwatch.Frequency;
            if (ms < 2000)
            {
                _frames++;
                _sum += frame;
                if (frame > _worst) { _worst = frame; _worstAt = DateTime.Now; }

                var ratio = ms / _budgetMs;
                if (ratio <= 1.1) _underBudget++;
                else if (ratio <= 2) _upTo2x++;
                else if (ratio <= 4) _upTo4x++;
                else _over4x++;

                if (ratio > _slowFactor)
                {
                    _slow++;
                    if (ratio > 2) _verySlow++;
                    if (gc != _lastGcCount) _slowWithGc++;
                }
            }
        }

        _lastTick = now;
        _lastGcCount = gc;
    }

    /// <summary>Hands over this window's numbers and starts a new one.</summary>
    public Window Take()
    {
        static double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;
        var w = new Window(
            _frames, _frames > 0 ? Ms(_sum) / _frames : 0, Ms(_worst), _worstAt,
            _slow, _verySlow, _slowWithGc, _budgetMs,
            _worldFrames > 0 ? Ms(_worldSum) / _worldFrames : 0, Ms(_worldWorst),
            _underBudget, _upTo2x, _upTo4x, _over4x);

        _frames = 0; _sum = 0; _worst = 0;
        _slow = 0; _verySlow = 0; _slowWithGc = 0;
        _underBudget = 0; _upTo2x = 0; _upTo4x = 0; _over4x = 0;
        _worldSum = 0; _worldWorst = 0; _worldFrames = 0;
        return w;
    }
}
