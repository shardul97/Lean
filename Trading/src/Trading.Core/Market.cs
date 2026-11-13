namespace Trading.Core;

// All timestamps are UTC. A candle becomes available only at EndTime.
public sealed record Candle(DateTimeOffset OpenTime, TimeSpan Period,
    decimal Open, decimal High, decimal Low, decimal Close, decimal Volume)
{
    public DateTimeOffset EndTime => OpenTime + Period;
    public void Validate()
    {
        if (OpenTime.Offset != TimeSpan.Zero || Period <= TimeSpan.Zero ||
            Open <= 0 || Close <= 0 || Low <= 0 || High < Math.Max(Open, Close) ||
            Low > Math.Min(Open, Close) || High < Low || Volume < 0)
            throw new ArgumentException("Invalid UTC OHLCV candle.");
    }
}

public enum Timeframe { M5, M15, H1, H4, Daily, Weekly }
public static class Timeframes
{
    public static TimeSpan Period(this Timeframe frame) => frame switch
    {
        Timeframe.M5 => TimeSpan.FromMinutes(5), Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.H1 => TimeSpan.FromHours(1), Timeframe.H4 => TimeSpan.FromHours(4),
        Timeframe.Daily => TimeSpan.FromDays(1), Timeframe.Weekly => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(frame))
    };
    public static DateTimeOffset Bucket(this Timeframe frame, DateTimeOffset time)
    {
        var anchor = new DateTimeOffset(1970, 1, frame == Timeframe.Weekly ? 5 : 1, 0, 0, 0, TimeSpan.Zero);
        var ticks = (time - anchor).Ticks;
        return anchor.AddTicks((long)Math.Floor((decimal)ticks / frame.Period().Ticks) * frame.Period().Ticks);
    }
}

// Reject gaps instead of silently constructing partial or synthetic candles.
public sealed class CandleAggregator(Timeframe timeframe)
{
    private Candle? _pending;
    private DateTimeOffset? _lastEnd;
    public Candle? Update(Candle minute)
    {
        minute.Validate();
        if (minute.Period != TimeSpan.FromMinutes(1)) throw new ArgumentException("Expected 1-minute input.");
        if (_lastEnd is not null && minute.OpenTime != _lastEnd)
            throw new InvalidOperationException("Input must be contiguous and strictly ordered.");
        _lastEnd = minute.EndTime;
        var bucket = timeframe.Bucket(minute.OpenTime);
        if (_pending is null)
        {
            if (minute.OpenTime != bucket) return null; // discard incomplete leading bucket
            _pending = minute with { Period = timeframe.Period() };
        }
        else
        {
            _pending = _pending with { High = Math.Max(_pending.High, minute.High),
                Low = Math.Min(_pending.Low, minute.Low), Close = minute.Close, Volume = _pending.Volume + minute.Volume };
        }
        if (minute.EndTime != bucket + timeframe.Period()) return null;
        var completed = _pending;
        _pending = null;
        return completed;
    }
}

public interface ICandleIndicator<out T> { T Update(Candle candle); }
public enum TradeAction { Hold, Buy, Sell }
public sealed record Decision(DateTimeOffset Time, TradeAction Action, decimal? Indicator, string Reason);
public interface ITradingStrategy
{
    Decision OnMinute(Candle candle, bool invested, decimal averageEntryPrice);
}
