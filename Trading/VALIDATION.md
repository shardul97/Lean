# Executed validation — 2026-11-12

Environment: Windows x64, .NET SDK 10.0.403, PowerShell 7, LEAN source commit `2d3a3d2436ce99be531f932fc452f63504a2331f`.

## Build and tests

- Source-built `Launcher/QuantConnect.Lean.Launcher.csproj`: succeeded, zero errors. Upstream analyzer/runtime compatibility warnings remain.
- `Trading.Backtest.Lean` and `Trading.Data.Binance`: built successfully.
- NUnit: **21 passed, 0 failed**, including all six aggregation periods and converted-row parsing by LEAN's actual `TradeBar` reader.
- Test result: `Trading/artifacts/tests/tests.trx`. The restricted sandbox stalled testhost startup; the same compiled tests passed outside that sandbox in about seven seconds. NuGet restore and initial public-data downloads needed network access. Neither step used account credentials.

## Real Binance data and real LEAN execution

Executed the `run.ps1` pipeline for BTCUSDT spot, 2025-01-01–2025-01-07 UTC inclusive. All seven public daily ZIP archives matched Binance's SHA256 checksums. Each contained exactly 1,440 valid, contiguous candles: **10,080 input minutes** in total. The archives use microsecond timestamps.

| Metric | Result |
| --- | ---: |
| Initial equity | 10,000 USDT |
| Final equity | 9,681.14534159372 USDT |
| Net return | -3.18855% |
| Maximum drawdown, sampled every minute | 3.21401% |
| Executed fills | 98 |
| Closed trades | 49 |
| Net win rate | 12.2449% |
| Modeled fees | 241.0437014219 USDT |
| Final open BTC quantity | 0 |

All 98 fills occurred exactly **60 seconds after their signal**. The 10,081 exported equity rows comprise initial equity plus every input minute. Summed closed-trade net P&L is `-318.85465840628 USDT`, exactly equal to final equity minus initial equity. LEAN's native summary agrees with equity and fees to its displayed precision (`9681.15`, `241.04`).

LEAN also probes the prior day's minute file and a daily file; these are listed in its failed-data-requests diagnostic. Neither is required by this strategy. The adapter explicitly checked that all 10,080 requested minutes were received. There is no history-based warmup or remote data fallback.

Artifacts: `Trading/artifacts/sample/`, including raw LEAN JSON, fill/trade/equity/indicator CSV, statistics, parameters and self-contained `chart.html`.

## Chronological comparison

Executed `compare.ps1`: six configurations trained on January 1–4; the selected lookback **12**, entry pressure **0.2** was then evaluated once on January 5–7 with fresh state.

- Selected training return: **-0.626744%**; 12 closed trades.
- Selected holdout return: **-1.165846%**; 15 closed trades; maximum minute-sampled drawdown **1.215024%**.
- Ranking and frozen selection: `Trading/artifacts/comparison-training.csv` and `comparison-selected.json`.
- Holdout: `Trading/artifacts/comparison-holdout/`.

Every tested configuration lost money on the training sample. These runs demonstrate working research plumbing, not a validated trading edge. The dates are too short for statistical conclusions.

## Chart verification

The exported HTML's JavaScript parsed and executed against a local canvas/DOM stub for 144, 288, 576 and all-candle windows. Drawing coordinates were finite, and the hover handler returned the expected candle values. No third-party chart library or network resource is required.

Visual inspection in the browser was **not completed**: the browser tool's security policy disallows `file://` URLs. Open `Trading/artifacts/sample/chart.html` manually in a local browser to review appearance. No browser-policy workaround was attempted.

## Angular dashboard/API validation — 2026-11-13

Added the Angular 21 dashboard and ASP.NET Core 10 local API. This is a new HTTP-served application; the earlier limitation above applies to the standalone file-URL chart.

- Angular production build passed (about 453 kB raw initial bundle, 124 kB estimated transfer); API build passed without warnings/errors.
- Five frontend tests, eleven API tests and all twenty-one original strategy tests passed (37 total). Coverage includes aggregation/partial periods/weekly boundaries, fill mapping and delay, drawdown, validation, download/path restrictions, queue capacity, restart handling and process arguments.
- Real browser form submission completed a new BTCUSDT Jan 1–7 backtest: 2,016 five-minute chart candles, 10,081 equity rows, 98 orders/fills, 49 closed trades. Final equity and fees exactly reproduced the original sample.
- A separate Jan 1 run with entry pressure 1 completed with zero orders/fills/trades and unchanged 10,000 USDT equity. The browser displayed an explicit empty orders state. A weekly view correctly had no complete candles.
- A request for missing converted Dec 1 data was recorded as failed with a readable execution log. Invalid strategy requests returned HTTP 400. Unknown runs/routes and attempts to download config.json returned HTTP 404.
- Browser checks confirmed chart rendering, timeframe switching, form submission, automatic status updates, fill filtering/delay, comparison table and zero-trade/partial-period states. Testing used the browser's existing narrow viewport.
- Status/logs persist under artifacts/.jobs; smoke-run identifiers are recorded in artifacts/dashboard-smoke.json. Generated results remain excluded from Git.
