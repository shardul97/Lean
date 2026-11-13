import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  aggregateCandles,
  drawdownSeries,
  executionDelay,
  fillBucket,
} from '../src/app/chart-data.ts';

const minute = (i) => new Date(Date.UTC(2025, 0, 6, 0, i)).toISOString();
const candle = (i) => ({
  time: minute(i),
  endTime: minute(i + 5),
  open: 100,
  high: 110 + i,
  low: 95,
  close: 105 + i,
  volume: 2,
  pressure: 0.5,
  signal: 'Hold',
});
test('chart aggregation combines complete UTC candles and drops a partial edge', () => {
  const input = [candle(0), candle(5), candle(10), candle(15)];
  const rows = aggregateCandles(input, 15);
  assert.equal(rows.length, 1);
  assert.equal(rows[0].volume, 6);
  assert.equal(rows[0].high, 120);
  assert.equal(rows[0].close, 115);
  assert.equal(rows[0].endTime, minute(15));
  assert.equal(rows[0].pressure, 0.5);
});
test('an incomplete leading bucket is never displayed as a complete hour', () => {
  assert.equal(aggregateCandles([candle(5), candle(10)], 60).length, 0);
});
test('weekly chart buckets align to Monday UTC', () => {
  const input = Array.from({ length: 2016 }, (_, i) => candle(i * 5));
  const rows = aggregateCandles(input, 10080);
  assert.equal(rows.length, 1);
  assert.equal(rows[0].time, minute(0));
});
test('fill markers map to the containing displayed candle without changing execution time', () => {
  assert.equal(fillBucket(minute(6), [candle(0), candle(5)]), Date.parse(minute(5)) / 1000);
  assert.equal(fillBucket(minute(10), [candle(0), candle(5)]), null);
  assert.equal(executionDelay({ time: minute(6), signalTime: minute(5) }), 60);
});
test('drawdown uses the running peak and preserves all equity timestamps', () => {
  const rows = drawdownSeries([100, 110, 99].map((equity, i) => ({ time: minute(i), equity })));
  assert.equal(rows.length, 3);
  assert.equal(rows[0].value, 0);
  assert.ok(Math.abs(rows[2].value + 10) < 1e-10);
});
