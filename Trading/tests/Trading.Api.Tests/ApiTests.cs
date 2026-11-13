using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Trading.Api;

namespace Trading.Api.Tests;

public sealed class ApiTests
{
    private string _root = null!;
    private TradingPaths _paths = null!;
    private RunStore _store = null!;
    private static BacktestRequest Request => new("pressure-breakout", "BTCUSDT", "2025-01-01", "2025-01-07");
    [SetUp]
    public void Setup()
    {
        _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trading-api-tests-" + Guid.NewGuid()));
        Directory.CreateDirectory(Path.Combine(_root, "scripts")); File.WriteAllText(Path.Combine(_root, "scripts", "run.ps1"), "# fixture");
        _paths = new(_root); _store = new(_paths);
    }
    [TearDown]
    public void Cleanup()
    {
        if (!_root.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "trading-api-tests-", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected cleanup path.");
        Directory.Delete(_root, true);
    }
    [TestCase("../secret")]
    [TestCase("a/b")]
    [TestCase("a\\b")]
    [TestCase(".jobs")]
    [TestCase("")]
    public void RejectsNonRunPaths(string id)
    {
        Assert.That(RunStore.ValidId(id), Is.False);
        Assert.That(_store.DownloadPath(id, "fills.csv"), Is.Null);
    }
    [Test]
    public void ValidatesStrategySymbolDatesAndRiskBeforeQueueing()
    {
        Assert.That(Request.Validate(), Is.Empty);
        Assert.That((Request with
        {
            Symbol = "BTCUSDT;pwd",
            StrategyId = "unknown",
            Lookback = 0,
            StopLoss = 1,
            Start = "2025-01-08",
            End = "2025-01-07"
        }).Validate().Keys,
            Is.EquivalentTo(new[] { "symbol", "strategyId", "lookback", "stopLoss", "dates" }));
        Assert.That((Request with { End = "2025-12-31" }).Validate(), Contains.Key("dates"));
    }
    [Test]
    public void QueuedRunIsVisibleWithoutAnOutputDirectory()
    {
        using var jobs = new BacktestJobs(_paths, _store, NullLogger<BacktestJobs>.Instance);
        var job = jobs.Enqueue(Request)!;
        Assert.That(_store.List().Single().Status, Is.EqualTo("queued"));
        Assert.That(_store.Detail(job.Id)!.Candles, Is.Empty);
    }
    [Test]
    public void QueueIsBoundedAndRejectsASecondBatch()
    {
        using var jobs = new BacktestJobs(_paths, _store, NullLogger<BacktestJobs>.Instance);
        for (var i = 0; i < 3; i++) Assert.That(jobs.Enqueue(Request), Is.Not.Null);
        Assert.That(jobs.Enqueue(Request), Is.Null);
    }
    [Test]
    public async Task RestartMarksUnfinishedJobsFailed()
    {
        _store.Save(new("interrupted", "running", DateTimeOffset.UtcNow, Request));
        using var jobs = new BacktestJobs(_paths, _store, NullLogger<BacktestJobs>.Instance);
        await jobs.StartAsync(CancellationToken.None); await jobs.StopAsync(CancellationToken.None);
        Assert.That(_store.Job("interrupted")!.Status, Is.EqualTo("failed"));
    }
    [Test]
    public void LaunchArgumentsAreSeparateAndCultureInvariant()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new("de-DE");
            var start = BacktestJobs.BuildStartInfo(_paths, new("run-1", "queued", DateTimeOffset.UtcNow, Request));
            Assert.That(start.ArgumentList, Contains.Item("0.15"));
            Assert.That(start.ArgumentList, Contains.Item("-SkipDownload")); Assert.That(start.UseShellExecute, Is.False);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
    [Test]
    public void ExistingZeroTradeRunImportsWithoutInventingExecutions()
    {
        var folder = Path.Combine(_paths.Artifacts, "empty"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "run.json"), """{"symbol":"BTCUSDT","start":"2025-01-01T00:00:00","end":"2025-01-01T23:59:59","parameters":{"Lookback":6}}""");
        File.WriteAllText(Path.Combine(folder, "statistics.json"), """{"InitialEquity":10000,"FinalEquity":10000,"TotalReturn":0,"MaxDrawdown":0,"ClosedTrades":0,"WinRate":0,"TotalFees":0,"OpenQuantity":0}""");
        File.WriteAllText(Path.Combine(folder, "fills.csv"), "time,signal,id,qty,price,fee\n");
        var detail = _store.Detail("empty")!;
        Assert.That(detail.Run.Status, Is.EqualTo("completed")); Assert.That(detail.Run.Statistics!.FinalEquity, Is.EqualTo(10000));
        Assert.That(detail.Fills, Is.Empty); Assert.That(detail.Trades, Is.Empty); Assert.That(detail.Orders, Is.Empty);
        Assert.That(_store.DownloadPath("empty", "config.json"), Is.Null);
        Assert.That(_store.DownloadPath("empty", "fills.csv"), Is.Not.Null);
    }
}
