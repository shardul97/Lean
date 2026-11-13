using System.Globalization;
using Trading.Data.Binance;
if (args.Length != 5)
{
    Console.Error.WriteLine("Usage: SYMBOL START(yyyy-MM-dd) END(inclusive) LEAN_DATA_ROOT CACHE_ROOT");
    return 2;
}
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
await BinanceArchive.Download(http, args[0], DateOnly.ParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture),
    DateOnly.ParseExact(args[2], "yyyy-MM-dd", CultureInfo.InvariantCulture), Path.GetFullPath(args[3]), Path.GetFullPath(args[4]));
return 0;
