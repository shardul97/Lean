using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trading.Analytics;

namespace Trading.Api;

// Imports the CSV/JSON contract already produced by the POC, including pre-dashboard runs.
public sealed class RunStore(TradingPaths paths)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    public static bool ValidId(string id) => Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9_-]{0,119}$");
    private string Folder(string id) => ValidId(id) ? Path.Combine(paths.Artifacts, id) : throw new ArgumentException("Invalid run id.");
    public bool Exists(string id) => ValidId(id) && (File.Exists(Path.Combine(Folder(id), "run.json")) || File.Exists(Path.Combine(paths.Jobs, id + ".json")));
    public List<RunSummary> List()
    {
        var ids = Directory.EnumerateDirectories(paths.Artifacts).Select(Path.GetFileName)
            .Concat(Directory.EnumerateFiles(paths.Jobs, "*.json").Select(Path.GetFileNameWithoutExtension))
            .OfType<string>().Where(ValidId).Distinct();
        return ids.Select(Summary).OfType<RunSummary>().OrderByDescending(x => x.CreatedAt).ToList();
    }
    public JobState? Job(string id) => Read<JobState>(Path.Combine(paths.Jobs, id + ".json"));
    public void Save(JobState job)
    {
        var path = Path.Combine(paths.Jobs, job.Id + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(job, Json));
        File.Move(path + ".tmp", path, true);
    }
    public RunSummary? Summary(string id)
    {
        var job = Job(id);
        var folder = Folder(id);
        var metadata = Read<JsonElement?>(Path.Combine(folder, "run.json"));
        if (metadata is null && job is null) return null;
        var stats = Read<Performance>(Path.Combine(folder, "statistics.json"));
        var status = job?.Status ?? (stats is not null ? "completed" : "incomplete");
        static string Day(string text) => text.Length >= 10 ? text[..10] : text;
        return new(id, status, job?.Request.StrategyId ?? "pressure-breakout",
            job?.Request.Symbol ?? Text(metadata, "symbol"), job?.Request.Start ?? Day(Text(metadata, "start")),
            job?.Request.End ?? Day(Text(metadata, "end")),
            job?.CreatedAt ?? new DateTimeOffset(Directory.GetCreationTimeUtc(folder)), stats, job?.Error,
            metadata is { } value && value.TryGetProperty("parameters", out var parameters) ? parameters.Clone() : job?.Request);
    }
    public RunDetail? Detail(string id)
    {
        var run = Summary(id);
        if (run is null) return null;
        var folder = Folder(id);
        if (run.Status is "queued" or "running") return new(run, [], [], [], [], []);
        var fills = Rows(folder, "fills.csv").Select(x => new Fill(Time(x[0]), Time(x[1]), int.Parse(x[2]), D(x[3]), D(x[4]), D(x[5]))).ToArray();
        var candles = Rows(folder, "indicators.csv").Select(x => new MarketCandle(Time(x[0]), Time(x[1]), D(x[2]), D(x[3]), D(x[4]), D(x[5]), D(x[6]), D(x[7]), x[8])).ToArray();
        var equity = Rows(folder, "equity.csv").Select(x => new EquityPoint(Time(x[0]), D(x[1]))).ToArray();
        var trades = Rows(folder, "trades.csv").Select(x => new ClosedTrade(Time(x[0]), Time(x[1]), D(x[2]), D(x[3]), D(x[4]), D(x[5]), D(x[6]))).ToArray();
        return new(run, candles, equity, fills, trades, Orders(folder, fills));
    }
    private static List<OrderView> Orders(string folder, IReadOnlyList<Fill> fills)
    {
        var eventsFile = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*-order-events.json").FirstOrDefault() : null;
        if (eventsFile is null) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(eventsFile));
        var nativeFile = eventsFile.Replace("-order-events.json", ".json", StringComparison.Ordinal);
        using var native = File.Exists(nativeFile) ? JsonDocument.Parse(File.ReadAllText(nativeFile)) : null;
        var result = new List<OrderView>();
        foreach (var group in doc.RootElement.EnumerateArray().GroupBy(x => x.GetProperty("orderId").GetInt32()))
        {
            var last = group.Last(); var id = group.Key;
            var reason = "";
            if (native is not null && native.RootElement.TryGetProperty("orders", out var orders) &&
                orders.TryGetProperty(id.ToString(CultureInfo.InvariantCulture), out var order) && order.TryGetProperty("tag", out var tag)) reason = tag.GetString() ?? "";
            var matched = fills.Where(x => x.OrderId == id).ToArray();
            result.Add(new(id, DateTimeOffset.FromUnixTimeMilliseconds((long)(last.GetProperty("time").GetDouble() * 1000)),
                last.GetProperty("symbolValue").GetString() ?? "", last.GetProperty("direction").GetString() ?? "",
                last.GetProperty("orderType").GetString() ?? "", last.GetProperty("status").GetString() ?? "",
                last.GetProperty("quantity").GetDecimal(), matched.LastOrDefault()?.Price ?? 0,
                matched.Sum(x => x.Quantity), matched.Sum(x => x.Fee), reason));
        }
        return result.OrderBy(x => x.OrderId).ToList();
    }
    public string[] Log(string id)
    {
        var jobLog = Path.Combine(paths.Jobs, id + ".log");
        var leanLog = Path.Combine(Folder(id), "log.txt");
        var file = File.Exists(jobLog) ? jobLog : leanLog;
        if (!File.Exists(file)) return [];
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new Queue<string>();
        while (reader.ReadLine() is { } line) { lines.Enqueue(line); if (lines.Count > 150) lines.Dequeue(); }
        return lines.ToArray();
    }
    public string? DownloadPath(string id, string file)
    {
        if (!ValidId(id) || file is not ("fills.csv" or "trades.csv" or "equity.csv" or "indicators.csv" or "statistics.json" or "run.json")) return null;
        var path = Path.Combine(Folder(id), file);
        return File.Exists(path) ? path : null;
    }
    private static IEnumerable<string[]> Rows(string folder, string file) => File.Exists(Path.Combine(folder, file))
        ? File.ReadLines(Path.Combine(folder, file)).Skip(1).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Split(',')) : [];
    private static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    private static decimal D(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);
    private static string Text(JsonElement? element, string key) => element is { } value && value.TryGetProperty(key, out var property) ? property.GetString() ?? "" : "";
    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, Json);
        }
        catch (JsonException) { return default; }
    }
}
