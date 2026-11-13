using Trading.Core;
namespace Trading.Indicators;

public sealed record PressureValue(bool Ready, decimal Pressure, bool Breakout);

// Example original formula, not a claimed trading edge:
// mean signed body/range, with current close above all PRIOR N highs.
public sealed class CandlePressure : ICandleIndicator<PressureValue>
{
    private readonly int _lookback;
    private readonly Queue<Candle> _history = new();
    private DateTimeOffset? _lastEnd;
    public CandlePressure(int lookback)
    {
        if (lookback < 2) throw new ArgumentOutOfRangeException(nameof(lookback));
        _lookback = lookback;
    }
    public PressureValue Update(Candle candle)
    {
        candle.Validate();
        if (_lastEnd is not null && candle.OpenTime < _lastEnd)
            throw new InvalidOperationException("Indicator candles must not overlap or go backwards.");
        _lastEnd = candle.EndTime;
        var ready = _history.Count == _lookback;
        var breakout = ready && candle.Close > _history.Max(x => x.High);
        var bodies = _history.Append(candle).TakeLast(_lookback);
        var pressure = bodies.Average(x => x.High == x.Low ? 0 : (x.Close - x.Open) / (x.High - x.Low));
        _history.Enqueue(candle);
        if (_history.Count > _lookback) _history.Dequeue();
        return new(ready, pressure, breakout);
    }
}
