import {
  AfterViewInit,
  Component,
  ElementRef,
  OnDestroy,
  ViewChild,
  effect,
  input,
  signal,
} from '@angular/core';
import {
  AreaSeries,
  CandlestickSeries,
  ColorType,
  HistogramSeries,
  LineSeries,
  createChart,
  createSeriesMarkers,
} from 'lightweight-charts';
import type { IChartApi, Time, UTCTimestamp, SeriesMarker } from 'lightweight-charts';
import type { EquityPoint, Fill, MarketCandle } from './models';
import { aggregateCandles, drawdownSeries, fillBucket, seconds } from './chart-data';

@Component({
  selector: 'app-result-chart',
  template: `<div
      #host
      class="chart-host"
      [style.height.px]="kind() === 'market' ? 420 : 235"
    ></div>
    @if (empty()) {
      <p class="chart-empty">No complete candles for this timeframe in the selected run.</p>
    }
    <div class="chart-caption">
      {{ caption() || 'Scroll to zoom · drag to pan · times in UTC' }}
    </div>`,
  styles: [
    `
      :host {
        display: block;
        position: relative;
      }
      .chart-host {
        width: 100%;
        min-width: 0;
      }
      .chart-caption {
        padding: 9px 18px;
        color: #788392;
        font:
          11px ui-monospace,
          monospace;
        border-top: 1px solid #edf0f3;
        min-height: 32px;
      }
      .chart-empty {
        text-align: center;
        color: #6b7785;
        margin: 0;
        position: absolute;
        top: 45%;
        inset-inline: 16px;
        z-index: 5;
        pointer-events: none;
      }
    `,
  ],
})
export class ResultChart implements AfterViewInit, OnDestroy {
  @ViewChild('host') host!: ElementRef<HTMLDivElement>;
  candles = input<MarketCandle[]>([]);
  equity = input<EquityPoint[]>([]);
  fills = input<Fill[]>([]);
  kind = input<'market' | 'equity'>('market');
  minutes = input(5);
  showSignals = input(true);
  showFills = input(true);
  caption = signal('');
  empty = signal(false);
  private ready = signal(false);
  private chart?: IChartApi;
  constructor() {
    effect(() => {
      const candles = this.candles(),
        equity = this.equity(),
        fills = this.fills(),
        kind = this.kind();
      const minutes = this.minutes(),
        signals = this.showSignals(),
        executions = this.showFills();
      if (this.ready()) this.render(candles, equity, fills, kind, minutes, signals, executions);
    });
  }
  ngAfterViewInit() {
    this.ready.set(true);
  }
  ngOnDestroy() {
    this.chart?.remove();
  }
  private render(
    input: MarketCandle[],
    equity: EquityPoint[],
    fills: Fill[],
    kind: string,
    minutes: number,
    signals: boolean,
    executions: boolean,
  ) {
    this.chart?.remove();
    this.caption.set('');
    const chart = (this.chart = createChart(this.host.nativeElement, {
      autoSize: true,
      layout: {
        background: { type: ColorType.Solid, color: '#ffffff' },
        textColor: '#7a8695',
        fontSize: 11,
        attributionLogo: true,
      },
      grid: { vertLines: { color: '#f1f3f5' }, horzLines: { color: '#f1f3f5' } },
      rightPriceScale: { borderColor: '#e9edf1' },
      // Minute equity can contain >100k points; allow the full run to fit even in a narrow viewport.
      timeScale: { borderColor: '#e9edf1', timeVisible: true, secondsVisible: false, minBarSpacing: 0.001 },
      crosshair: {
        vertLine: { color: '#98a4b1', labelBackgroundColor: '#23364a' },
        horzLine: { color: '#98a4b1', labelBackgroundColor: '#23364a' },
      },
    }));
    const time = (value: string) => seconds(value) as UTCTimestamp;
    if (kind === 'equity') {
      this.empty.set(false);
      chart
        .addSeries(AreaSeries, {
          lineColor: '#178264',
          topColor: '#17826430',
          bottomColor: '#17826402',
          lineWidth: 2,
          priceLineVisible: false,
        })
        .setData(equity.map((x) => ({ time: time(x.time), value: x.equity })));
      const dd = chart.addSeries(
        LineSeries,
        {
          color: '#c46a6a',
          lineWidth: 1,
          priceFormat: { type: 'percent' },
          priceLineVisible: false,
        },
        1,
      );
      dd.setData(drawdownSeries(equity).map((x) => ({ ...x, time: x.time as UTCTimestamp })));
      chart.panes()[0]?.setHeight(145);
      chart.panes()[1]?.setHeight(65);
    } else {
      const candles = aggregateCandles(input, minutes);
      this.empty.set(candles.length === 0);
      const series = chart.addSeries(CandlestickSeries, {
        upColor: '#1b9775',
        downColor: '#d56a72',
        borderVisible: false,
        wickUpColor: '#1b9775',
        wickDownColor: '#d56a72',
        priceLineVisible: false,
      });
      series.setData(
        candles.map((x) => ({
          time: time(x.time),
          open: x.open,
          high: x.high,
          low: x.low,
          close: x.close,
        })),
      );
      chart
        .addSeries(
          HistogramSeries,
          {
            priceLineVisible: false,
            lastValueVisible: false,
            priceFormat: { type: 'custom', formatter: (v: number) => v.toFixed(2) },
          },
          1,
        )
        .setData(
          candles.map((x) => ({
            time: time(x.time),
            value: x.pressure,
            color: x.pressure >= 0 ? '#1b977570' : '#d56a7270',
          })),
        );
      const markers: SeriesMarker<Time>[] = [];
      if (signals)
        for (const candle of input.filter((x) => x.signal !== 'Hold')) {
          const bucket = fillBucket(candle.time, candles);
          if (bucket !== null)
            markers.push({
              time: bucket as UTCTimestamp,
              position: candle.signal === 'Buy' ? 'belowBar' : 'aboveBar',
              color: candle.signal === 'Buy' ? '#178264' : '#c75360',
              shape: candle.signal === 'Buy' ? 'arrowUp' : 'arrowDown',
              text: '',
              size: 0.7,
            });
        }
      if (executions)
        for (const fill of fills) {
          const bucket = fillBucket(fill.time, candles);
          if (bucket !== null)
            markers.push({
              time: bucket as UTCTimestamp,
              position: fill.quantity > 0 ? 'belowBar' : 'aboveBar',
              color: fill.quantity > 0 ? '#477bd3' : '#b98a32',
              shape: 'circle',
              text: '',
              size: 0.5,
            });
        }
      markers.sort((a, b) => Number(a.time) - Number(b.time));
      createSeriesMarkers(series, markers);
      chart.panes()[0]?.setHeight(320);
      chart.panes()[1]?.setHeight(80);
      chart.subscribeCrosshairMove((param) => {
        const row = param.seriesData.get(series);
        if (row && 'open' in row)
          this.caption.set(
            `${new Date(Number(param.time) * 1000).toISOString().slice(0, 16).replace('T', ' ')} UTC · O ${row.open.toFixed(2)}  H ${row.high.toFixed(2)}  L ${row.low.toFixed(2)}  C ${row.close.toFixed(2)}`,
          );
      });
    }
    chart.timeScale().fitContent();
  }
}
