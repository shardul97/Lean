using System.Globalization;
using System.Text.RegularExpressions;
using Trading.Analytics;

namespace Trading.Api;

public sealed class TradingPaths
{
    public string Root { get; }
    public string Artifacts => Path.Combine(Root, "artifacts");
    public string Jobs => Path.Combine(Artifacts, ".jobs");
    public string Script => Path.Combine(Root, "scripts", "run.ps1");
    public bool BacktestReady => File.Exists(Path.Combine(Root, "..", "Launcher", "bin", "Debug", "QuantConnect.Lean.Launcher.dll")) &&
        File.Exists(Path.Combine(Root, "src", "Trading.Backtest.Lean", "bin", "Debug", "net10.0", "Trading.Backtest.Lean.dll")) &&
        File.Exists(Path.Combine(Root, "src", "Trading.Data.Binance", "bin", "Debug", "net10.0", "Trading.Data.Binance.dll"));
    public TradingPaths(string root)
    {
        Root = Path.GetFullPath(root);
        if (!File.Exists(Script)) throw new ArgumentException("Trading:Root must point to the Trading directory containing scripts/run.ps1.");
        Directory.CreateDirectory(Jobs);
    }
}
public sealed record BacktestRequest(string StrategyId, string Symbol, string Start, string End,
    int Lookback = 6, decimal EntryPressure = 0.15m, decimal StopLoss = 0.02m,
    int MaxHoldingBars = 24, bool DownloadData = false)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        void Error(string key, string message) => errors[key] = [message];
        if (StrategyId != "pressure-breakout") Error("strategyId", "Select a registered strategy.");
        if (Symbol is null || !Regex.IsMatch(Symbol, "^[A-Z0-9]{1,20}USDT$")) Error("symbol", "Use an uppercase Binance USDT spot pair, for example BTCUSDT.");
        var first = DateOnly.TryParseExact(Start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start);
        var last = DateOnly.TryParseExact(End, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end);
        if (!first || !last || end < start || end >= DateOnly.FromDateTime(DateTime.UtcNow) || end.DayNumber - start.DayNumber >= 90)
            Error("dates", "Use 1–90 complete historical UTC days, with the end on or after the start.");
        if (Lookback is < 2 or > 500) Error("lookback", "Lookback must be between 2 and 500 five-minute candles.");
        if (EntryPressure < 0 || EntryPressure > 1) Error("entryPressure", "Entry pressure must be between 0 and 1.");
        if (StopLoss <= 0 || StopLoss >= 1) Error("stopLoss", "Stop loss must be between 0 and 1, exclusive.");
        if (MaxHoldingBars is < 1 or > 10000) Error("maxHoldingBars", "Holding bars must be between 1 and 10000.");
        return errors;
    }
}
public sealed record JobState(string Id, string Status, DateTimeOffset CreatedAt, BacktestRequest Request,
    DateTimeOffset? FinishedAt = null, string? Error = null);
public sealed record RunSummary(string Id, string Status, string StrategyId, string Symbol, string Start, string End,
    DateTimeOffset CreatedAt, Performance? Statistics, string? Error, object? Parameters);
public sealed record MarketCandle(DateTimeOffset Time, DateTimeOffset EndTime, decimal Open, decimal High, decimal Low,
    decimal Close, decimal Volume, decimal Pressure, string Signal);
public sealed record OrderView(int OrderId, DateTimeOffset Time, string Symbol, string Side, string Type,
    string Status, decimal Quantity, decimal Price, decimal FillQuantity, decimal Fee, string Reason);
public sealed record RunDetail(RunSummary Run, IReadOnlyList<MarketCandle> Candles, IReadOnlyList<EquityPoint> Equity,
    IReadOnlyList<Fill> Fills, IReadOnlyList<ClosedTrade> Trades, IReadOnlyList<OrderView> Orders);
