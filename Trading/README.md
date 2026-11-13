# Local Binance / LEAN proof of concept

This directory adds a C# trading platform proof of concept to the existing LEAN checkout. It uses the source-built LEAN Launcher, public Binance spot archives, and local files. No QuantConnect CLI, account, paid service, Docker, or Binance credentials are required. No live execution is implemented.

## Validated integration approach

Investigated against LEAN commit `2d3a3d2436ce99be531f932fc452f63504a2331f` (2026-11-12). The projects in this checkout target **net10.0**. Use the checked-out source as the version authority: the web documentation lists older SDKs in some sections.

- [Official source execution instructions](https://www.quantconnect.com/docs/v2/lean-engine/getting-started) describe building LEAN and executing `QuantConnect.Lean.Launcher.dll` directly. The CLI is an optional separate product.
- [Native crypto format](https://www.quantconnect.com/docs/v2/lean-engine/data-format/crypto) stores actual unscaled prices and UTC timestamps. This implementation writes `crypto/binance/minute/btcusdt/YYYYMMDD_trade.zip`, containing `YYYYMMDD_btcusdt_minute_trade.csv`. Rows are `milliseconds_since_midnight,open,high,low,close,base_volume`.
- [Binance public data specification](https://github.com/binance/binance-public-data) documents free spot daily archives, checksums, and the **microsecond timestamp change on 2025-01-01**. Older millisecond archives also work.
- The local source confirms these contracts in `Launcher/Program.cs`, `Queues/JobQueue.cs`, `Common/Data/Market/TradeBar.cs`, and `Launcher/config.json`. The runner uses local backtesting handlers, `DefaultDataProvider`, and `SubscriptionDataReaderHistoryProvider` from that configuration. No API data downloader is selected.
- Native `AddCrypto` preserves LEAN's spot holdings, buying-power checks, lot sizing, fills and portfolio accounting. Trade-only subscriptions avoid manufacturing quote data. The account and reports use USDT so no USD/USDT conversion feed is required. The benchmark is constant and requires no separate market subscription.

The implementation sequence was shared before coding: independent candle/strategy logic; archive conversion and LEAN adapter; exports and chart; tests and chronological parameter comparison. Upstream LEAN source is unchanged.

## Architecture

```text
Trading.Core                 UTC candles, timeframe aggregation, strategy contracts
   ↑
Trading.Indicators           example mathematical candle pressure and prior-range breakout
   ↑
Trading.Strategies           entry, close-based stop, pressure exit, maximum holding duration

Trading.Data.Binance → Core   public archive downloader, SHA256 checks, native data conversion
Trading.Analytics → Core     fills, matched trades, equity, metrics, self-contained HTML chart
Trading.Backtest.Lean        QCAlgorithm adapter → Strategies + Analytics + LEAN
```

Only the adapter depends on LEAN. `ITradingStrategy.OnMinute` consumes a closed UTC candle plus current execution state and produces a decision. A future paper/live host can use the same strategy object and feed contract, substituting an execution adapter. `Trading.Live.Binance` is deliberately deferred until order reconciliation, reconnect handling, persistence, exchange filters, and paper execution have been designed. A project containing empty live classes would not add value here.

The sample is an illustrative formula, not an asserted trading edge:

- On each completed five-minute candle, pressure is the mean of `(close - open) / (high - low)` over the last N candles (zero for a flat candle).
- Entry requires a close above the **previous** N candles' highs, pressure above the parameter threshold, and a bullish most recently completed one-hour candle.
- Exit when five-minute pressure is negative, a five-minute close falls 2% below entry, or 24 five-minute holding bars have elapsed.
- Buy size is 25% of available USDT, rounded down to LEAN's stored lot size. One long spot position at a time; no leverage or shorts.

All 5m, 15m, 1h, 4h, daily, and weekly aggregators use one-minute input. Intraday/daily boundaries use UTC; weeks start **Monday 00:00 UTC**. Leading/trailing partial buckets are not emitted. The sample consumes 5m and 1h; other completed frames are accessible through the strategy's `Completed` dictionary. Each new run starts with fresh state; download enough earlier history or use longer runs for daily/weekly strategies.

## Requirements

- .NET **10 SDK** (not only the runtime).
- PowerShell **7.2+** (`pwsh`; Windows PowerShell 5.1 is not supported).
- This LEAN checkout, including the checked-in `Data/market-hours` and `Data/symbol-properties` directories.
- Internet access for initial NuGet restore and `data.binance.vision` downloads. Subsequent runs can use verified cached archives.

Run from the repository root:

```powershell
pwsh -File Trading/scripts/run.ps1
```

The default backtest downloads BTCUSDT for **2025-01-01 through 2025-01-07 inclusive**, builds the Launcher and POC, and writes `Trading/artifacts/sample`. Each day must contain exactly 1,440 ordered one-minute candles. Downloads are SHA256 verified against Binance's `.CHECKSUM`, cached under `Trading/data/raw`, and described by a source/checksum manifest. Missing or malformed days fail rather than silently backtesting a shorter range.

The runner preserves existing results: choose a new run name for each run. To reuse the build and local converted data:

```powershell
pwsh -File Trading/scripts/run.ps1 -RunName trial2 -SkipBuild -SkipDownload -Lookback 12 -EntryPressure 0.2
```

For another Binance USDT spot pair (provided LEAN's symbol-properties database contains it):

```powershell
pwsh -File Trading/scripts/run.ps1 -Symbol ETHUSDT -Start 2025-01-01 -End 2025-01-07 -RunName eth
```

The script generates an explicit `live-mode: false` backtesting config, runs the Launcher directly, and verifies the statistics export. The algorithm also refuses live mode. Files are isolated under `Trading/data` and `Trading/artifacts`; a few POC DLLs are copied into the Launcher output directory for algorithm dependency discovery. Run source builds sequentially; the script uses one MSBuild node for portability. Initial compilation emits upstream analyzer warnings and can take several minutes.

Individual build/download/test commands:

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build Launcher/QuantConnect.Lean.Launcher.csproj -m:1
dotnet build Trading/src/Trading.Backtest.Lean/Trading.Backtest.Lean.csproj -m:1
dotnet run --project Trading/src/Trading.Data.Binance -- BTCUSDT 2025-01-01 2025-01-07 Trading/data/lean Trading/data/raw
dotnet test Trading/tests/Trading.Tests/Trading.Tests.csproj -m:1
```

The strategy libraries themselves can be built without LEAN: `dotnet build Trading/src/Trading.Strategies/Trading.Strategies.csproj -m:1`.

## Results and visualization

Open `Trading/artifacts/sample/chart.html` locally in a browser. It has no external scripts, CDN, service or server dependency. It displays historical five-minute candlesticks, pressure, decision markers, executed fill markers and equity. Change the window size and use the slider; hover for candle values and UTC times.

Each run exports:

| File | Contents |
| --- | --- |
| `fills.csv` | Executed orders, signal time, fill time, signed quantity, execution price, USDT fee |
| `trades.csv` | Closed round trips, entry/exit, fees and net P&L |
| `equity.csv` | Initial equity and every minute's marked portfolio value |
| `indicators.csv` | Completed five-minute OHLCV, pressure and decisions |
| `statistics.json` | Return, minute-sampled maximum drawdown, closed trade count, net win rate, fees, open quantity |
| `run.json` | Parameters, execution assumptions, dates, symbol, LEAN commit and final open/pending state |
| `config.json` | Exact generated LEAN configuration |
| `PressureBreakoutAlgorithm*.json`, logs | LEAN's native detailed results, statistics, orders and execution diagnostics |

Return is `final equity / initial equity - 1`. Drawdown uses the running peak over the minute equity series. Win rate counts closed trades with positive P&L after both fees. Closed-trade P&L excludes any position still open at the end, while final equity includes it marked to market. Open positions are **not forcibly liquidated**; a pending signal at the end cannot be executed without another bar. Avoid interpreting the sample's constant-benchmark alpha/beta as market-relative performance.

## Parameter comparison and avoiding look-ahead

```powershell
pwsh -File Trading/scripts/compare.ps1
```

This evaluates six parameter combinations on January 1–4 only, ranks them by net training return, saves the winner, then runs **one** selected configuration on the disjoint January 5–7 holdout. A fresh LEAN process and strategy instance are used per run. It writes `comparison-training.csv`, `comparison-selected.json`, and `comparison-holdout/`. Use a new `-Name` on repeats. For meaningful research use substantially longer periods, several chronological folds, sufficient indicator warmup, and a final untouched holdout.

Decisions consume completed candles only. A breakout compares against prior highs before updating the lookback. An hourly candle remains unchanged until its full hour closes. No `History` calls, centered windows, backfilled values, or whole-dataset normalization are used. The adapter submits a decision **on the next observed minute**, before evaluating that minute's new signals, and LEAN fills the market order at that later minute's close plus slippage. `fills.csv` retains both times, and the adapter rejects a fill that does not follow its signal.

The one-minute execution delay is intentional and conservative, not a claim of exact real-time execution. The close-based stop is evaluated only every five minutes and is not an exchange stop. Ranking training results is still subject to selection bias; repeated holdout tuning would invalidate the holdout. These controls address temporal leakage, not profitability or statistical significance.

## Limitations and next steps

- Spot OHLCV has no bid/ask spread, depth, tick path, latency or partial-fill model. The POC assumes full market fills, **0.10% USDT fee** on pre-slippage notional and **0.02% slippage** per side. These are explicit research assumptions, not verified current Binance account fees. Actual Binance buy fees can be taken in the received asset; this simplified quote-currency fee model makes accounting transparent and is not an exact exchange replica.
- Strict complete-day validation intentionally refuses outages, newly listed partial days, gaps and duplicate candles. Production ingestion should record and classify gaps rather than silently fill them. Daily archives may be delayed or revised; preserve raw files/manifests for reproducibility.
- Fresh runs have no pre-start warmup. The sample warms itself causally before entry; weekly outputs require a complete Monday–Sunday bucket. Holdout starts with flat inventory and a fresh warmup, so returns describe independent deployments rather than a continuous portfolio carried across the split.
- LEAN metadata is a current snapshot, not historical exchange-filter data; new/delisted pairs and fee schedules require deliberate handling. Selecting today's surviving assets introduces survivorship bias.
- Export lists are kept in memory and HTML embeds the data; suitable for a small POC, not years of multi-symbol minute history. Streaming exports and a chart data service can be added when needed.
- No optimizer service, database, web app, dependency injection framework, or Binance live client is required for this proof of concept.

Tests cover aggregation of all six timeframes, partial boundaries, gaps/duplicates, prior-only breakout logic, completed higher-frame visibility, causal replay, risk exits, both timestamp units, actual LEAN TradeBar parsing, invariant CSV/ZIP layout, full-day validation and performance accounting.

See [VALIDATION.md](VALIDATION.md) for the executed build, test, backtest and comparison results.
