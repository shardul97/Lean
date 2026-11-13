# Stratum Angular dashboard

Local Angular application for the Binance/LEAN research platform. Reads real saved backtests and submits new local LEAN backtests. No brokerage connection, credentials, demo data or live-trading mode.

## Start everything

From the LEAN repository root, with .NET 10 SDK, PowerShell 7.2+, Node.js **24.x** and npm installed:

```powershell
pwsh -File Trading/scripts/dashboard.ps1
```

Open **http://127.0.0.1:5057**. The script builds LEAN and the trading projects, installs the lockfile's npm dependencies with `npm ci`, builds Angular, and starts `Trading.Api`. The API serves Angular and `/api` on the same local origin. Ctrl+C stops the server and its child backtest process.

On later launches when source/dependencies have not changed:

```powershell
pwsh -File Trading/scripts/dashboard.ps1 -SkipBuild
```

This is standalone Angular **21**, with signals, strict templates, HttpClient and TradingView Lightweight Charts 5.3. Angular 21 supports the installed Node 24.4 runtime; Angular 22 requires a newer Node 24 minor release. See [Angular's version matrix](https://angular.dev/reference/versions). Dependencies are pinned by `package-lock.json`.

## Your first backtest

Existing runs in `Trading/artifacts/*/` appear automatically, including the original sample and comparison runs. Generated results and data are ignored by Git: a fresh clone starts with an empty dashboard.

Click **New backtest**, use the default BTCUSDT dates/parameters, leave **Download / verify Binance archives** checked, and submit. Downloads use public archives and SHA256 checks, with no API keys. With the checkbox off, every requested converted daily file must already exist.

The form accepts a registered strategy, Binance USDT spot pair, 1–90 complete historical UTC days, lookback, entry pressure, stop-loss fraction, and maximum holding bars. The current registered strategy is **Candle pressure breakout**, with 5m decisions and completed 1h confirmation. A chart timeframe button changes the visualization, not the strategy's decision schedule.

New runs receive unique IDs. Status updates every three seconds through queued → running → completed/failed. The worker runs one LEAN child process at a time and accepts three queued jobs because the runner shares build outputs. Status and logs persist under `artifacts/.jobs/`. On API restart, unfinished jobs are marked failed rather than replayed. Finished runs remain available.

## Features

- Saved-run selection and a comparison table for return, drawdown, fees, closed trades and win rate.
- Candlestick charts at 5m, 15m, 1h, 4h, daily and Monday-based weekly periods; zoom, pan, crosshair and UTC values.
- Custom pressure pane and separate decision/fill marker toggles.
- Portfolio equity and running-peak drawdown charts.
- Orders with latest LEAN status, side, quantity, fill price, fee and entry/exit reason.
- Fills with decision time, execution time and delay; closed trades with both fees and net P&L.
- Search, pagination, fill/trade CSV exports, statistics JSON, and the last 150 execution-log lines.
- Empty/error states for zero trades, incomplete chart periods, missing data and failed jobs.
- Responsive layout and a native modal form with keyboard focus containment.

Higher chart timeframes combine only complete exported 5m candles. Partial edges are dropped: a seven-day run starting midweek may have **no complete weekly candle**. Higher-frame pressure is the mean of underlying 5m pressure, not a new strategy calculation. Markers use the containing chart bucket; exact event times remain in the tables. Multiple decisions in one larger chart candle are retained.

The Orders tab's download is explicitly labeled **Fill CSV**. The orders table comes from native LEAN order-event JSON plus original order tags; submission alone does not imply a fill.

## Angular development

Terminal 1, from the repository root:

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet run --project Trading/src/Trading.Api --no-launch-profile
```

Terminal 2:

```powershell
cd Trading/dashboard
npm ci
npm start
```

Open **http://127.0.0.1:4200**. The Angular proxy forwards `/api/**` to port 5057. The API can read saved results without built LEAN binaries, but starting a new backtest requires the Launcher, adapter and downloader builds.

`Trading:Root` (environment variable `Trading__Root`) may point the API at another Trading directory with the same layout. This does not yet remove the runner's relative dependency on the LEAN checkout.

## API

| Route | Behavior |
| --- | --- |
| `GET /api/health` | API status and backtest binary readiness |
| `GET /api/strategies` | Registered strategy descriptors |
| `GET /api/runs` | Saved/queued runs and metrics |
| `GET /api/runs/{id}` | Metadata, candles, full minute equity, orders, fills and closed trades |
| `POST /api/runs` | Validate/enqueue; returns HTTP 202 and job ID |
| `GET /api/runs/{id}/log` | Last 150 log lines |
| `GET /api/runs/{id}/files/{file}` | Allowlisted CSV/JSON downloads |

Example POST body:

```json
{
  "strategyId": "pressure-breakout",
  "symbol": "BTCUSDT",
  "start": "2025-01-01",
  "end": "2025-01-07",
  "lookback": 6,
  "entryPressure": 0.15,
  "stopLoss": 0.02,
  "maxHoldingBars": 24,
  "downloadData": true
}
```

The API binds to **127.0.0.1**, restricts hosts and remote addresses, checks browser POST origins, validates inputs, and launches PowerShell using separate process arguments. It is an unauthenticated local research service, not an internet deployment. There is no arbitrary algorithm-upload or shell-command field. Logs may contain local paths.

## Tests

```powershell
cd Trading/dashboard
npm test
npm run build
```

From the repository root:

```powershell
dotnet test Trading/tests/Trading.Api.Tests/Trading.Api.Tests.csproj -m:1
dotnet test Trading/tests/Trading.Tests/Trading.Tests.csproj -m:1
```

Frontend tests cover candle aggregation, partial periods, Monday alignment, fill mapping, delay and drawdown. API tests cover validation, path/download restrictions, zero-trade import, bounded queueing, restart state and culture-invariant process arguments. They need no Binance connection or running LEAN process. Real browser/API backtests were also executed; see [VALIDATION.md](../VALIDATION.md).

## Adding strategies

Implement the indicator and `ITradingStrategy` in C#, register its descriptor/ID in `Trading.Api`, and dispatch the ID in the LEAN adapter. Unregistered IDs are rejected. For additional outputs, extend the export contract, API DTO and Angular chart together. Existing exposed parameters can be changed in the form without changing source.

Execution uses the POC's 10,000 USDT initial equity, 25% cash per entry, 0.10% pre-slippage notional fee and 0.02% slippage. Stops are close-based and orders fill on a later minute. See the [main README](../README.md) for modeling and look-ahead limits. Manual comparison does not replace a chronological holdout workflow.

Charts use [TradingView Lightweight Charts™](https://www.tradingview.com/), with required on-page attribution preserved. See [license and attribution](https://tradingview.github.io/lightweight-charts/docs#license-and-attribution).
