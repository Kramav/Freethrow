using System.Diagnostics;

namespace Freethrow.Core.Diagnostics;

/// <summary>
/// Frequency of an event, averaged over a sliding window. Used for capture and
/// inference rates, which the preview demo displays so the efficiency targets are
/// read off the screen rather than asserted.
/// </summary>
public sealed class RateCounter(double windowSeconds = 1.0)
{
    private readonly double _windowSeconds = windowSeconds;
    private readonly object _gate = new();
    private long _windowStart = Stopwatch.GetTimestamp();
    private int _countInWindow;
    private double _rate;

    /// <summary>Current rate, in events per second.</summary>
    /// <remarks>
    /// Reading decays an open window rather than returning the last closed one. Without
    /// that, a source delivering a one-second burst and then stalling keeps reporting
    /// the burst rate forever — a diagnostic that reads healthy while nothing is
    /// arriving is worse than no diagnostic at all.
    /// </remarks>
    public double PerSecond
    {
        get
        {
            lock (_gate)
            {
                double elapsed = (Stopwatch.GetTimestamp() - _windowStart) / (double)Stopwatch.Frequency;
                return elapsed >= _windowSeconds ? _countInWindow / elapsed : _rate;
            }
        }
    }

    /// <summary>Records one event.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            _countInWindow++;

            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - _windowStart) / (double)Stopwatch.Frequency;
            if (elapsed < _windowSeconds)
            {
                return;
            }

            _rate = _countInWindow / elapsed;
            _countInWindow = 0;
            _windowStart = now;
        }
    }

    /// <summary>Clears the window, for example after a source restart.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _windowStart = Stopwatch.GetTimestamp();
            _countInWindow = 0;
            _rate = 0;
        }
    }
}

/// <summary>
/// Exponential moving average with a running maximum. The maximum matters as much as
/// the mean here: a pipeline that averages 20 ms but spikes to 200 ms feels broken,
/// and an average alone hides that.
/// </summary>
public sealed class MovingAverage(double smoothing = 0.1)
{
    private readonly double _smoothing = smoothing;
    private readonly object _gate = new();
    private double _value;
    private double _max;
    private bool _hasValue;

    /// <summary>Smoothed value, or zero before the first sample.</summary>
    public double Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }
    }

    /// <summary>Largest sample seen since the last <see cref="Reset"/>.</summary>
    public double Max
    {
        get
        {
            lock (_gate)
            {
                return _max;
            }
        }
    }

    /// <summary>Adds a sample.</summary>
    public void Add(double sample)
    {
        lock (_gate)
        {
            _value = _hasValue ? (_value * (1 - _smoothing)) + (sample * _smoothing) : sample;
            _hasValue = true;

            if (sample > _max)
            {
                _max = sample;
            }
        }
    }

    /// <summary>Clears the average and the running maximum.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _value = 0;
            _max = 0;
            _hasValue = false;
        }
    }
}

/// <summary>
/// Times a pipeline stage and feeds a <see cref="MovingAverage"/>.
/// Wrap a stage in a <c>using</c> to record it.
/// </summary>
public readonly struct StageTimer : IDisposable
{
    private readonly MovingAverage _target;
    private readonly long _start;

    private StageTimer(MovingAverage target)
    {
        _target = target;
        _start = Stopwatch.GetTimestamp();
    }

    /// <summary>Starts timing into <paramref name="target"/>.</summary>
    public static StageTimer Start(MovingAverage target) => new(target);

    /// <summary>Records the elapsed milliseconds.</summary>
    public void Dispose() =>
        _target.Add((Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency);
}
