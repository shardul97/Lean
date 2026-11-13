using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trading.Core;
namespace Trading.Data.Binance;

public static class BinanceArchive
{
    public static Candle Parse(string line)
    {
        var columns = line.Split(',');
        if (columns.Length != 12) throw new FormatException("Expected 12 Binance spot kline columns.");
        var timestamp = long.Parse(columns[0], CultureInfo.InvariantCulture);
        // Binance spot archives use microseconds from 2025-01-01, milliseconds earlier.
        var micros = timestamp >= 100_000_000_000_000L;
        var open = micros ? DateTimeOffset.UnixEpoch.AddTicks(checked(timestamp * 10)) : DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        decimal D(int i) => decimal.Parse(columns[i], CultureInfo.InvariantCulture);
        var candle = new Candle(open, TimeSpan.FromMinutes(1), D(1), D(2), D(3), D(4), D(5));
        candle.Validate();
        var closeStamp = long.Parse(columns[6], CultureInfo.InvariantCulture);
        if (open.Ticks % TimeSpan.TicksPerMinute != 0 || closeStamp != timestamp + (micros ? 60_000_000 : 60_000) - 1)
            throw new FormatException("Expected an aligned, closed one-minute kline.");
        return candle;
    }

    public static IReadOnlyList<Candle> Read(byte[] zip, DateOnly day)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        if (archive.Entries.Count != 1 || !archive.Entries[0].Name.EndsWith(".csv", StringComparison.Ordinal))
            throw new InvalidDataException("Expected one CSV archive entry.");
        using var reader = new StreamReader(archive.Entries[0].Open());
        var candles = new List<Candle>();
        while (reader.ReadLine() is { } line)
        {
            var candle = Parse(line);
            var expected = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddMinutes(candles.Count);
            if (candle.OpenTime != expected) throw new InvalidDataException("Duplicate, missing, out-of-day or unordered kline.");
            candles.Add(candle);
        }
        if (candles.Count != 1440) throw new InvalidDataException($"Expected 1440 minutes, got {candles.Count}.");
        return candles;
    }

    public static string LeanLine(Candle c) => string.Join(',',
        ((long)c.OpenTime.TimeOfDay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
        c.Open.ToString(CultureInfo.InvariantCulture), c.High.ToString(CultureInfo.InvariantCulture),
        c.Low.ToString(CultureInfo.InvariantCulture), c.Close.ToString(CultureInfo.InvariantCulture), c.Volume.ToString(CultureInfo.InvariantCulture));

    public static void WriteLean(string dataRoot, string symbol, DateOnly day, IReadOnlyList<Candle> candles)
    {
        var folder = Path.Combine(dataRoot, "crypto", "binance", "minute", symbol.ToLowerInvariant());
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{day:yyyyMMdd}_trade.zip");
        var temporary = path + ".tmp";
        using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry($"{day:yyyyMMdd}_{symbol.ToLowerInvariant()}_minute_trade.csv").Open()))
            foreach (var candle in candles) writer.WriteLine(LeanLine(candle));
        File.Move(temporary, path, true);
    }

    public static async Task Download(HttpClient http, string symbol, DateOnly start, DateOnly end, string dataRoot, string cache)
    {
        if (!Regex.IsMatch(symbol, "^[A-Z0-9]{5,24}$") || !symbol.EndsWith("USDT", StringComparison.Ordinal))
            throw new ArgumentException("POC supports alphanumeric Binance USDT spot symbols.");
        if (end < start || end >= DateOnly.FromDateTime(DateTime.UtcNow)) throw new ArgumentException("Use complete historical UTC days.");
        Directory.CreateDirectory(cache);
        var manifest = new List<object>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var name = $"{symbol}-1m-{day:yyyy-MM-dd}.zip";
            var url = $"https://data.binance.vision/data/spot/daily/klines/{symbol}/1m/{name}";
            var path = Path.Combine(cache, name);
            var checksumPath = path + ".CHECKSUM";
            var zip = File.Exists(path) ? await File.ReadAllBytesAsync(path) : await http.GetByteArrayAsync(url);
            var checksum = File.Exists(checksumPath) ? await File.ReadAllTextAsync(checksumPath) : await http.GetStringAsync(url + ".CHECKSUM");
            var expected = checksum.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            var actual = Convert.ToHexString(SHA256.HashData(zip));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"SHA256 mismatch: {name}");
            var candles = Read(zip, day);
            await File.WriteAllBytesAsync(path, zip);
            await File.WriteAllTextAsync(checksumPath, checksum);
            WriteLean(dataRoot, symbol, day, candles);
            manifest.Add(new { url, sha256 = actual, rows = candles.Count, day });
            Console.WriteLine($"Verified and converted {name}: {candles.Count} candles");
        }
        await File.WriteAllTextAsync(Path.Combine(cache, $"manifest-{symbol}-{start:yyyyMMdd}-{end:yyyyMMdd}.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }
}
