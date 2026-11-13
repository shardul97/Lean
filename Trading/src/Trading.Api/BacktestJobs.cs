using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace Trading.Api;

// One child process at a time: LEAN's current runner shares a build/output directory.
public sealed class BacktestJobs(TradingPaths paths, RunStore store, ILogger<BacktestJobs> logger) : BackgroundService
{
    private readonly Channel<JobState> _queue = Channel.CreateBounded<JobState>(new BoundedChannelOptions(3)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly object _gate = new();
    public JobState? Enqueue(BacktestRequest request)
    {
        if (request.Validate().Count > 0) throw new ArgumentException("Invalid backtest request.");
        lock (_gate)
        {
            var id = $"dashboard-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            var job = new JobState(id, "queued", DateTimeOffset.UtcNow, request);
            store.Save(job);
            if (_queue.Writer.TryWrite(job)) return job;
            store.Save(job with { Status = "failed", FinishedAt = DateTimeOffset.UtcNow, Error = "Queue capacity exceeded." });
            return null;
        }
    }
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var run in store.List().Where(x => x.Status is "queued" or "running"))
            if (store.Job(run.Id) is { } previous) store.Save(previous with { Status = "failed", FinishedAt = DateTimeOffset.UtcNow, Error = "API restarted before this run completed. Submit a new run." });
        return base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var queued in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            store.Save(queued with { Status = "running" });
            try
            {
                using var process = new Process { StartInfo = BuildStartInfo(paths, queued) };
                process.Start();
                using var cancellation = stoppingToken.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
                await using var output = new StreamWriter(Path.Combine(paths.Jobs, queued.Id + ".log")) { AutoFlush = true };
                using var logGate = new SemaphoreSlim(1);
                async Task Pump(StreamReader reader)
                {
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        await logGate.WaitAsync();
                        try { await output.WriteLineAsync(line); } finally { logGate.Release(); }
                    }
                }
                await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError), process.WaitForExitAsync());
                var completed = process.ExitCode == 0 && File.Exists(Path.Combine(paths.Artifacts, queued.Id, "statistics.json"));
                store.Save(queued with { Status = completed ? "completed" : "failed", FinishedAt = DateTimeOffset.UtcNow,
                    Error = completed ? null : "Backtest failed. Open the execution log for details." });
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error, "Backtest {Id} failed", queued.Id);
                store.Save(queued with { Status = "failed", FinishedAt = DateTimeOffset.UtcNow, Error = error.Message });
            }
        }
    }
    public static ProcessStartInfo BuildStartInfo(TradingPaths paths, JobState job)
    {
        var request = job.Request;
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetFullPath(Path.Combine(paths.Root, "..")), RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", paths.Script,
            "-Symbol", request.Symbol, "-Start", request.Start, "-End", request.End, "-Lookback", request.Lookback.ToString(CultureInfo.InvariantCulture),
            "-EntryPressure", request.EntryPressure.ToString(CultureInfo.InvariantCulture), "-StopLoss", request.StopLoss.ToString(CultureInfo.InvariantCulture),
            "-MaxHoldingBars", request.MaxHoldingBars.ToString(CultureInfo.InvariantCulture), "-RunName", job.Id, "-SkipBuild" }) start.ArgumentList.Add(arg);
        if (!request.DownloadData) start.ArgumentList.Add("-SkipDownload");
        return start;
    }
}
