using Trading.Core;
using Trading.Indicators;
namespace Trading.Strategies;

public sealed record StrategyParameters(int Lookback = 6, decimal EntryPressure = 0.15m,
    decimal StopLoss = 0.02m, int MaxHoldingBars = 24)
{
    public void Validate()
    {
        if (Lookback < 2 || EntryPressure < 0 || EntryPressure > 1 || StopLoss <= 0 || StopLoss >= 1 || MaxHoldingBars < 1)
            throw new ArgumentException("Invalid strategy parameters.");
    }
}

public sealed class PressureBreakoutStrategy : ITradingStrategy
{
    private readonly StrategyParameters _parameters;
    private readonly CandlePressure _indicator;
    private readonly Dictionary<Timeframe, CandleAggregator> _aggregators =
        Enum.GetValues<Timeframe>().ToDictionary(x => x, x => new CandleAggregator(x));
    private readonly Dictionary<Timeframe, Candle> _completed = new();
    private int _holdingBars;
    public IReadOnlyDictionary<Timeframe, Candle> Completed => _completed;
    public PressureBreakoutStrategy(StrategyParameters parameters)
    {
        parameters.Validate(); _parameters = parameters; _indicator = new(parameters.Lookback);
    }
    public Decision OnMinute(Candle minute, bool invested, decimal averageEntryPrice)
    {
        Candle? signalBar = null;
        foreach (var (frame, aggregator) in _aggregators)
        {
            var bar = aggregator.Update(minute);
            if (bar is null) continue;
            _completed[frame] = bar;
            if (frame == Timeframe.M5) signalBar = bar;
        }
        if (signalBar is null) return new(minute.EndTime, TradeAction.Hold, null, "between signal bars");
        var value = _indicator.Update(signalBar);
        _holdingBars = invested ? _holdingBars + 1 : 0;
        if (invested && (signalBar.Close <= averageEntryPrice * (1 - _parameters.StopLoss) ||
            value.Pressure < 0 || _holdingBars >= _parameters.MaxHoldingBars))
            return new(minute.EndTime, TradeAction.Sell, value.Pressure, "close-based risk/pressure/time exit");
        if (!invested && value.Ready && value.Breakout && value.Pressure >= _parameters.EntryPressure &&
            _completed.TryGetValue(Timeframe.H1, out var hour) && hour.Close > hour.Open)
            return new(minute.EndTime, TradeAction.Buy, value.Pressure, "pressure breakout with completed bullish hour");
        return new(minute.EndTime, TradeAction.Hold, value.Pressure, "no entry/exit");
    }
}
