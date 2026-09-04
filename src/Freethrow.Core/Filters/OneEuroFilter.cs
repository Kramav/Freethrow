using System.Numerics;

namespace Freethrow.Core.Filters;

/// <summary>
/// The 1€ filter: an adaptive low-pass filter that trades jitter against lag based on
/// how fast the signal is moving.
/// </summary>
/// <remarks>
/// <para>
/// Landmark output is noisy at rest and must be smoothed, but a fixed low-pass filter
/// forces an unwinnable choice: smooth enough to stop the jitter, and the hand visibly
/// lags behind the real one; responsive enough to keep up, and it shakes when still.
/// </para>
/// <para>
/// This filter varies its cutoff with the signal's own speed — heavy smoothing when
/// nearly still, almost none when moving fast. That matches how the error is actually
/// perceived: jitter is objectionable only when stationary, and lag only when moving.
/// </para>
/// <para>
/// Tuning: raise <c>minCutoff</c> to cut lag at the cost of jitter; raise <c>beta</c> to
/// cut lag specifically during fast motion. Tune <c>minCutoff</c> first with the hand
/// held still, then <c>beta</c> while moving.
/// </para>
/// <para>Casiez, Roussel and Vogel, CHI 2012.</para>
/// </remarks>
public sealed class OneEuroFilter(double minCutoff = 1.0, double beta = 0.0, double derivativeCutoff = 1.0)
{
    private readonly double _minCutoff = minCutoff;
    private readonly double _beta = beta;
    private readonly double _derivativeCutoff = derivativeCutoff;

    private LowPass _value;
    private LowPass _derivative;
    private double _lastTimestamp;
    private bool _initialised;

    /// <summary>Most recent filtered value.</summary>
    public double Value => _value.Value;

    /// <summary>Feeds a sample and returns the filtered value.</summary>
    /// <param name="value">Raw sample.</param>
    /// <param name="timestampSeconds">Monotonic timestamp, in seconds.</param>
    public double Filter(double value, double timestampSeconds)
    {
        if (!_initialised)
        {
            _initialised = true;
            _lastTimestamp = timestampSeconds;
            _value.Reset(value);
            _derivative.Reset(0);
            return value;
        }

        double elapsed = timestampSeconds - _lastTimestamp;
        if (elapsed <= 0)
        {
            // Duplicate or out-of-order timestamp; assume a nominal 30 fps step rather
            // than dividing by zero.
            elapsed = 1.0 / 30;
        }

        _lastTimestamp = timestampSeconds;
        double rate = 1.0 / elapsed;

        double rawDerivative = (value - _value.Value) * rate;
        double smoothedDerivative = _derivative.Filter(rawDerivative, Alpha(_derivativeCutoff, rate));

        double cutoff = _minCutoff + (_beta * Math.Abs(smoothedDerivative));
        return _value.Filter(value, Alpha(cutoff, rate));
    }

    /// <summary>Clears state so the next sample is taken as a fresh start.</summary>
    public void Reset()
    {
        _initialised = false;
    }

    private static double Alpha(double cutoff, double rate)
    {
        double tau = 1.0 / (2 * Math.PI * cutoff);
        double period = 1.0 / rate;
        return 1.0 / (1.0 + (tau / period));
    }

    private struct LowPass
    {
        public double Value { get; private set; }

        public void Reset(double value) => Value = value;

        public double Filter(double value, double alpha)
        {
            Value = (alpha * value) + ((1 - alpha) * Value);
            return Value;
        }
    }
}

/// <summary>A 1€ filter applied independently to each axis of a 2D point.</summary>
public sealed class OneEuroFilter2D(double minCutoff = 1.0, double beta = 0.0, double derivativeCutoff = 1.0)
{
    private readonly OneEuroFilter _x = new(minCutoff, beta, derivativeCutoff);
    private readonly OneEuroFilter _y = new(minCutoff, beta, derivativeCutoff);

    /// <summary>Feeds a sample and returns the filtered point.</summary>
    public Vector2 Filter(Vector2 value, double timestampSeconds) => new(
        (float)_x.Filter(value.X, timestampSeconds),
        (float)_y.Filter(value.Y, timestampSeconds));

    /// <summary>Clears state so the next sample is taken as a fresh start.</summary>
    public void Reset()
    {
        _x.Reset();
        _y.Reset();
    }
}
