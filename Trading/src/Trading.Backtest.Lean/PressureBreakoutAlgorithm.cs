using System.Globalization;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.Slippage;
using QuantConnect.Securities;
using Trading.Analytics;
using Trading.Core;
using Trading.Strategies;
namespace Trading.Backtest.Lean;

public sealed class PressureBreakoutAlgorithm : QCAlgorithm
{
    private Symbol _symbol = null!;
    private PressureBreakoutStrategy _strategy = null!;
    private StrategyParameters _parameters = null!;
    private Decision? _pending;
    private DateTimeOffset _activeSignalTime;
    private readonly List<Fill> _fills = new();
    private readonly List<EquityPoint> _equity = new();
    private readonly List<CandlePoint> _candles = new();
    private const decimal InitialCash = 10000;
    public override void Initialize()
    {
        if (LiveMode || Config.GetBool("live-mode")) throw new InvalidOperationException("This adapter is backtest-only.");
        var start = DateTime.ParseExact(GetParameter("start"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateTime.ParseExact(GetParameter("end"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (end < start) throw new ArgumentException("End precedes start.");
        SetTimeZone(TimeZones.Utc);
        SetStartDate(start); SetEndDate(end);
        SetAccountCurrency("USDT"); SetCash(InitialCash);
        SetBrokerageModel(BrokerageName.Binance, AccountType.Cash);
        SetAvailableDataTypes(new Dictionary<SecurityType, List<TickType>> { [SecurityType.Crypto] = new() { TickType.Trade } });
        var security = AddCrypto(GetParameter("symbol", "BTCUSDT"), Resolution.Minute, Market.Binance, fillForward: false);
        _symbol = security.Symbol;
        security.SetFeeModel(new QuoteFeeModel());
        security.SetSlippageModel(new ConstantSlippageModel(0.0002m));
        SetBenchmark(_ => 1m); // no extra symbol or remote benchmark data
        _parameters = new StrategyParameters(GetParameter("lookback", 6), GetParameter("entry-pressure", 0.15m),
            GetParameter("stop-loss", 0.02m), GetParameter("max-holding-bars", 24));
        _strategy = new(_parameters);
        _equity.Add(new(new DateTimeOffset(start, TimeSpan.Zero), InitialCash));
    }
    public override void OnData(Slice slice)
    {
        if (!slice.Bars.TryGetValue(_symbol, out var bar) || bar.IsFillForward) return;
        var minute = new Candle(new DateTimeOffset(DateTime.SpecifyKind(bar.Time, DateTimeKind.Utc)), bar.Period,
            bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
        // Execute an earlier decision before evaluating any newly completed signal candle.
        if (_pending is { } pending && minute.EndTime > pending.Time)
        {
            _activeSignalTime = pending.Time;
            var quantity = pending.Action == TradeAction.Buy
                ? Math.Floor((Portfolio.Cash * 0.25m / bar.Close) / Securities[_symbol].SymbolProperties.LotSize) * Securities[_symbol].SymbolProperties.LotSize
                : -Portfolio[_symbol].Quantity;
            if (quantity != 0) MarketOrder(_symbol, quantity, tag: pending.Reason);
            _pending = null;
        }
        var decision = _strategy.OnMinute(minute, Portfolio[_symbol].Invested, Portfolio[_symbol].AveragePrice);
        if (decision.Indicator.HasValue)
        {
            _candles.Add(new(_strategy.Completed[Timeframe.M5], decision.Indicator.Value, decision.Action.ToString()));
            if (decision.Action != TradeAction.Hold) _pending = decision;
        }
        _equity.Add(new(minute.EndTime, Portfolio.TotalPortfolioValue));
    }
    public override void OnOrderEvent(OrderEvent orderEvent)
    {
        if (orderEvent.Status == OrderStatus.Invalid) throw new InvalidOperationException(orderEvent.Message);
        if (orderEvent.Status != OrderStatus.Filled) return;
        var time = new DateTimeOffset(DateTime.SpecifyKind(orderEvent.UtcTime, DateTimeKind.Utc));
        if (time <= _activeSignalTime) throw new InvalidOperationException("Fill must follow signal time.");
        _fills.Add(new(time, _activeSignalTime, orderEvent.OrderId, orderEvent.FillQuantity, orderEvent.FillPrice, orderEvent.OrderFee.Value.Amount));
    }
    public override void OnEndOfAlgorithm()
    {
        var expected = (EndDate.Date - StartDate.Date).Days * 1440 + 1440;
        if (_equity.Count - 1 != expected) throw new InvalidOperationException($"Expected {expected} minute candles, received {_equity.Count - 1}.");
        RunReport.Export(Config.Get("results-destination-folder"), _fills, _equity, _candles,
            new { symbol = _symbol.Value, start = StartDate, end = EndDate, parameters = _parameters,
                feeRate = 0.001m, feeCurrency = "USDT", slippageRate = 0.0002m,
                execution = "Market order at the next observed minute close after the signal", pendingAtEnd = _pending,
                leanCommit = Config.Get("poc-lean-commit"), openPosition = Portfolio[_symbol].Quantity });
    }
    private sealed class QuoteFeeModel : FeeModel
    {
        public override OrderFee GetOrderFee(OrderFeeParameters parameters) =>
            new(new CashAmount(parameters.Order.AbsoluteQuantity * parameters.Security.Price * 0.001m, "USDT"));
    }
}
