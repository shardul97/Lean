using System.Globalization;
using System.IO.Compression;
using NUnit.Framework;
using QuantConnect;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using Trading.Analytics;
using Trading.Core;
using Trading.Data.Binance;
using Trading.Indicators;
using Trading.Strategies;
using Candle = Trading.Core.Candle;
namespace Trading.Tests;

public class PipelineTests
{
    private static readonly DateTimeOffset Monday = new(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
    private static Candle Minute(int i, decimal price = 100) =>
        new(Monday.AddMinutes(i), TimeSpan.FromMinutes(1), price, price + 2, price - 1, price + 1, 3);

    [TestCase(Timeframe.M5, 5)] [TestCase(Timeframe.M15, 15)]
    [TestCase(Timeframe.H1, 60)] [TestCase(Timeframe.H4, 240)]
    [TestCase(Timeframe.Daily, 1440)] [TestCase(Timeframe.Weekly, 10080)]
    public void AggregatorPublishesOnlyCompleteUtcBuckets(Timeframe frame, int minutes)
    {
        var aggregator = new CandleAggregator(frame);
        for (var i = 0; i < minutes - 1; i++) Assert.That(aggregator.Update(Minute(i)), Is.Null);
        var candle = aggregator.Update(Minute(minutes - 1))!;
        Assert.Multiple(() => {
            Assert.That(candle.OpenTime, Is.EqualTo(Monday));
            Assert.That(candle.EndTime, Is.EqualTo(Monday.AddMinutes(minutes)));
            Assert.That(candle.Volume, Is.EqualTo(3 * minutes));
            Assert.That(candle.High, Is.EqualTo(102)); Assert.That(candle.Low, Is.EqualTo(99));
        });
    }
    [Test]
    public void LeadingPartialBucketIsDiscarded()
    {
        var aggregator = new CandleAggregator(Timeframe.M5);
        for (var i = 2; i < 9; i++) Assert.That(aggregator.Update(Minute(i)), Is.Null);
        Assert.That(aggregator.Update(Minute(9))!.OpenTime, Is.EqualTo(Monday.AddMinutes(5)));
    }
    [TestCase(0)] [TestCase(2)]
    public void DuplicateOrMissingMinutesFail(int next)
    {
        var aggregator = new CandleAggregator(Timeframe.M5);
        aggregator.Update(Minute(0));
        Assert.Throws<InvalidOperationException>(() => aggregator.Update(Minute(next)));
    }
    [Test]
    public void BreakoutComparesAgainstPriorCandlesOnly()
    {
        var indicator = new CandlePressure(2);
        Assert.That(indicator.Update(Minute(0)).Ready, Is.False);
        Assert.That(indicator.Update(Minute(1)).Ready, Is.False);
        var result = indicator.Update(Minute(2, 103));
        Assert.That(result.Ready && result.Breakout, Is.True);
        Assert.That(result.Pressure, Is.EqualTo(1m / 3));
    }
    [Test]
    public void FlatCandleHasZeroPressureAndNoDivisionByZero()
    {
        var result = new CandlePressure(2).Update(new(Monday, TimeSpan.FromMinutes(5), 100, 100, 100, 100, 0));
        Assert.That(result.Pressure, Is.Zero);
    }
    [Test]
    public void StrategyOnlySeesCompletedHigherTimeframes()
    {
        var strategy = new PressureBreakoutStrategy(new());
        for (var i = 0; i < 59; i++) strategy.OnMinute(Minute(i), false, 0);
        Assert.That(strategy.Completed.ContainsKey(Timeframe.H1), Is.False);
        strategy.OnMinute(Minute(59), false, 0);
        Assert.That(strategy.Completed[Timeframe.H1].EndTime, Is.EqualTo(Monday.AddHours(1)));
        for (var i = 60; i < 119; i++) strategy.OnMinute(Minute(i), false, 0);
        Assert.That(strategy.Completed[Timeframe.H1].EndTime, Is.EqualTo(Monday.AddHours(1)));
    }
    [Test]
    public void FuturePricesCannotChangeEarlierDecisions()
    {
        var a = new PressureBreakoutStrategy(new()); var b = new PressureBreakoutStrategy(new());
        var prefix = Enumerable.Range(0, 120).Select(i => Minute(i, 100 + 3 * i)).ToArray();
        var before = prefix.Select(x => a.OnMinute(x, false, 0)).ToArray();
        var replay = prefix.Select(x => b.OnMinute(x, false, 0)).ToArray();
        for (var i = 120; i < 300; i++) b.OnMinute(Minute(i, 100000), false, 0);
        Assert.That(replay, Is.EqualTo(before));
        Assert.That(before.Any(x => x.Action == TradeAction.Buy), Is.True);
    }
    [Test]
    public void CloseBasedRiskExitUsesEntryPrice()
    {
        var strategy = new PressureBreakoutStrategy(new()); Decision? decision = null;
        for (var i = 0; i < 5; i++) decision = strategy.OnMinute(Minute(i), true, 200);
        Assert.That(decision!.Action, Is.EqualTo(TradeAction.Sell));
    }

    private static string Kline(long timestamp, long duration) =>
        $"{timestamp},100.25,103,99,102.5,7.123,{timestamp + duration - 1},0,1,0,0,0";
    [TestCase(false)] [TestCase(true)]
    public void MillisecondAndMicrosecondArchivesMatchLeanReader(bool microseconds)
    {
        var timestamp = Monday.ToUnixTimeMilliseconds() * (microseconds ? 1000 : 1);
        var candle = BinanceArchive.Parse(Kline(timestamp, microseconds ? 60_000_000 : 60_000));
        var symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, QuantConnect.Market.Binance);
        var config = new SubscriptionDataConfig(typeof(TradeBar), symbol, Resolution.Minute, TimeZones.Utc, TimeZones.Utc, false, true, false);
        var parsed = TradeBar.ParseCrypto(config, BinanceArchive.LeanLine(candle), Monday.UtcDateTime);
        Assert.Multiple(() => {
            Assert.That(candle.OpenTime, Is.EqualTo(Monday));
            Assert.That(parsed.Time, Is.EqualTo(Monday.UtcDateTime));
            Assert.That(parsed.EndTime, Is.EqualTo(Monday.UtcDateTime.AddMinutes(1)));
            Assert.That(parsed.Open, Is.EqualTo(candle.Open)); Assert.That(parsed.High, Is.EqualTo(candle.High));
            Assert.That(parsed.Low, Is.EqualTo(candle.Low)); Assert.That(parsed.Close, Is.EqualTo(candle.Close));
            Assert.That(parsed.Volume, Is.EqualTo(candle.Volume));
        });
    }
    [Test]
    public void ConversionUsesInvariantCultureAndCorrectZipLayout()
    {
        var original = CultureInfo.CurrentCulture;
        var path = Path.Combine(Path.GetTempPath(), "trading-test-" + Guid.NewGuid());
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            BinanceArchive.WriteLean(path, "BTCUSDT", new DateOnly(2025, 1, 6), new[] { Minute(0, 100.25m) });
            using var zip = ZipFile.OpenRead(Path.Combine(path, "crypto/binance/minute/btcusdt/20250106_trade.zip"));
            Assert.That(zip.Entries.Single().Name, Is.EqualTo("20250106_btcusdt_minute_trade.csv"));
            using var reader = new StreamReader(zip.Entries.Single().Open());
            Assert.That(reader.ReadLine(), Is.EqualTo("0,100.25,102.25,99.25,101.25,3"));
        }
        finally { CultureInfo.CurrentCulture = original; if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    private static byte[] Archive(IEnumerable<string> lines)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("data.csv").Open()))
            foreach (var line in lines) writer.WriteLine(line);
        return stream.ToArray();
    }
    [Test]
    public void FullDayArchiveIsValidated()
    {
        var lines = Enumerable.Range(0, 1440).Select(i => Kline(Monday.AddMinutes(i).ToUnixTimeMilliseconds(), 60000));
        Assert.That(BinanceArchive.Read(Archive(lines), new(2025, 1, 6)).Count, Is.EqualTo(1440));
    }
    [Test]
    public void IncompleteOrDuplicatedArchiveFails()
    {
        var line = Kline(Monday.ToUnixTimeMilliseconds(), 60000);
        Assert.Throws<InvalidDataException>(() => BinanceArchive.Read(Archive(new[] { line }), new(2025, 1, 6)));
        Assert.Throws<InvalidDataException>(() => BinanceArchive.Read(Archive(new[] { line, line }), new(2025, 1, 6)));
    }
    [Test]
    public void InvalidOhlcFails() => Assert.Throws<ArgumentException>(() => BinanceArchive.Parse(
        Kline(Monday.ToUnixTimeMilliseconds(), 60000).Replace(",103,", ",101,")));
    [Test]
    public void FeesDrawdownAndOpenInventoryAreAccountedFor()
    {
        Fill[] fills = { new(Monday, Monday.AddMinutes(-1), 1, 2, 100, 0.2m),
            new(Monday.AddHours(1), Monday, 2, -2, 110, 0.22m),
            new(Monday.AddHours(2), Monday, 3, 1, 100, 0.1m) };
        EquityPoint[] equity = { new(Monday, 1000), new(Monday.AddHours(1), 1100), new(Monday.AddHours(2), 990) };
        var performance = RunReport.Calculate(equity, fills);
        Assert.Multiple(() => {
            Assert.That(performance.TotalReturn, Is.EqualTo(-0.01m)); Assert.That(performance.MaxDrawdown, Is.EqualTo(0.1m));
            Assert.That(performance.ClosedTrades, Is.EqualTo(1)); Assert.That(performance.OpenQuantity, Is.EqualTo(1));
            Assert.That(performance.TotalFees, Is.EqualTo(0.52m)); Assert.That(performance.WinRate, Is.EqualTo(1));
            Assert.That(RunReport.MatchTrades(fills)[0].NetProfit, Is.EqualTo(19.58m));
        });
    }
}
