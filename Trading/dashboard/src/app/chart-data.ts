import type { EquityPoint, Fill, MarketCandle } from './models';

export const seconds = (time: string): number => Math.floor(Date.parse(time) / 1000);

// Aggregate only already-completed 5m outputs. Drop partial edge buckets for larger chart views.
export function aggregateCandles(candles: MarketCandle[], minutes: number): MarketCandle[] {
  if (minutes === 5) return candles;
  const buckets = new Map<number, MarketCandle[]>();
  const anchor = minutes === 10080 ? 4 * 86400 : 0; // Monday relative to Unix epoch Thursday
  for (const candle of candles) {
    const bucket =
      Math.floor((seconds(candle.time) - anchor) / (minutes * 60)) * minutes * 60 + anchor;
    const rows = buckets.get(bucket) ?? [];
    rows.push(candle);
    buckets.set(bucket, rows);
  }
  return [...buckets.entries()].flatMap(([bucket, rows]) => {
    if (
      rows.length !== minutes / 5 ||
      seconds(rows[0].time) !== bucket ||
      seconds(rows.at(-1)!.endTime) !== bucket + minutes * 60
    )
      return [];
    return [
      {
        ...rows[0],
        endTime: rows.at(-1)!.endTime,
        high: Math.max(...rows.map((x) => x.high)),
        low: Math.min(...rows.map((x) => x.low)),
        close: rows.at(-1)!.close,
        volume: rows.reduce((sum, x) => sum + x.volume, 0),
        pressure: rows.reduce((sum, x) => sum + x.pressure, 0) / rows.length,
        signal: rows.some((x) => x.signal === 'Buy')
          ? 'Buy'
          : rows.some((x) => x.signal === 'Sell')
            ? 'Sell'
            : 'Hold',
      },
    ];
  });
}
export function fillBucket(time: string, candles: MarketCandle[]): number | null {
  const t = seconds(time);
  // Fills are known at their timestamp; the marker uses the 5m chart bucket containing it.
  const candle = candles.find((x) => seconds(x.time) <= t && t < seconds(x.endTime));
  return candle ? seconds(candle.time) : null;
}
export function drawdownSeries(equity: EquityPoint[]): { time: number; value: number }[] {
  let peak = 0;
  return equity.map((point) => {
    peak = Math.max(peak, point.equity);
    return { time: seconds(point.time), value: peak > 0 ? (point.equity / peak - 1) * 100 : 0 };
  });
}
export function executionDelay(fill: Fill): number {
  return seconds(fill.time) - seconds(fill.signalTime);
}
