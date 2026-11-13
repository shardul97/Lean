import {
  Component,
  computed,
  ElementRef,
  inject,
  OnDestroy,
  OnInit,
  signal,
  ViewChild,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { BacktestRequest, RunDetail, RunSummary, Strategy } from './models';
import { ResultChart } from './result-chart';
import { executionDelay } from './chart-data';

@Component({
  selector: 'app-root',
  imports: [CommonModule, FormsModule, ResultChart],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit, OnDestroy {
  @ViewChild('formDialog') formDialog!: ElementRef<HTMLDialogElement>;
  private http = inject(HttpClient);
  private timer?: ReturnType<typeof setInterval>;
  private pollBusy = false;
  private selection = 0;
  runs = signal<RunSummary[]>([]);
  strategies = signal<Strategy[]>([]);
  detail = signal<RunDetail | null>(null);
  selectedId = signal('');
  loading = signal(true);
  error = signal('');
  apiOnline = signal(false);
  backtestReady = signal(false);
  submitting = signal(false);
  formError = signal('');
  section = signal<'workspace' | 'compare'>('workspace');
  readonly frames = [
    { label: '5m', minutes: 5 },
    { label: '15m', minutes: 15 },
    { label: '1h', minutes: 60 },
    { label: '4h', minutes: 240 },
    { label: '1D', minutes: 1440 },
    { label: '1W', minutes: 10080 },
  ];
  tab = signal<'orders' | 'fills' | 'trades' | 'log'>('orders');
  log = signal<string[]>([]);
  minutes = signal(5);
  showSignals = signal(true);
  showFills = signal(true);
  filter = signal('');
  page = signal(0);
  pageSize = 12;
  stats = computed(() => this.detail()?.run.statistics ?? null);
  run = computed(() => this.detail()?.run ?? null);
  active = computed(() => this.run()?.status === 'running' || this.run()?.status === 'queued');
  tableRows = computed(() => {
    const detail = this.detail();
    if (!detail) return [];
    const source =
      this.tab() === 'orders'
        ? detail.orders
        : this.tab() === 'fills'
          ? detail.fills
          : detail.trades;
    return source.filter((x) => this.matches(x));
  });
  pages = computed(() => Math.max(1, Math.ceil(this.tableRows().length / this.pageSize)));
  // Templates use the separately typed collections, keeping strict Angular template checking enabled.
  orders = computed(
    () =>
      this.detail()
        ?.orders.filter((x) => this.matches(x))
        .slice(this.page() * this.pageSize, (this.page() + 1) * this.pageSize) ?? [],
  );
  fills = computed(
    () =>
      this.detail()
        ?.fills.filter((x) => this.matches(x))
        .slice(this.page() * this.pageSize, (this.page() + 1) * this.pageSize) ?? [],
  );
  trades = computed(
    () =>
      this.detail()
        ?.trades.filter((x) => this.matches(x))
        .slice(this.page() * this.pageSize, (this.page() + 1) * this.pageSize) ?? [],
  );
  request: BacktestRequest = {
    strategyId: 'pressure-breakout',
    symbol: 'BTCUSDT',
    start: '2025-01-01',
    end: '2025-01-07',
    lookback: 6,
    entryPressure: 0.15,
    stopLoss: 0.02,
    maxHoldingBars: 24,
    downloadData: true,
  };
  readonly today = new Date(Date.now() - 86400000).toISOString().slice(0, 10);
  async ngOnInit() {
    await this.refresh();
    if (this.runs().length)
      await this.selectRun(this.runs().find((x) => x.id === 'sample')?.id ?? this.runs()[0].id);
    else this.loading.set(false);
    this.timer = setInterval(() => void this.poll(), 3000);
  }
  ngOnDestroy() {
    clearInterval(this.timer);
  }
  async refresh() {
    try {
      const [health, strategies, runs] = await Promise.all([
        firstValueFrom(this.http.get<{ backtestReady: boolean }>('/api/health')),
        firstValueFrom(this.http.get<Strategy[]>('/api/strategies')),
        firstValueFrom(this.http.get<RunSummary[]>('/api/runs')),
      ]);
      this.apiOnline.set(true);
      this.backtestReady.set(health.backtestReady);
      this.strategies.set(strategies);
      this.runs.set(runs);
      this.error.set('');
    } catch (error) {
      this.apiOnline.set(false);
      this.error.set(this.message(error));
      this.loading.set(false);
    }
  }
  async selectRun(id: string, quiet = false) {
    const selection = ++this.selection;
    if (!quiet) {
      this.loading.set(true);
      this.detail.set(null);
      this.page.set(0);
      this.filter.set('');
    }
    this.selectedId.set(id);
    try {
      const detail = await firstValueFrom(
        this.http.get<RunDetail>(`/api/runs/${encodeURIComponent(id)}`),
      );
      if (selection !== this.selection) return;
      this.detail.set(detail);
      this.error.set('');
      if (this.tab() === 'log' || this.active()) await this.loadLog();
    } catch (error) {
      if (selection === this.selection) this.error.set(this.message(error));
    } finally {
      if (selection === this.selection) this.loading.set(false);
    }
  }
  private async poll() {
    if (this.pollBusy) return;
    this.pollBusy = true;
    try {
      const wasActive = this.active();
      await this.refresh();
      if (
        this.selectedId() &&
        (wasActive ||
          this.runs().some(
            (x) => x.id === this.selectedId() && ['queued', 'running'].includes(x.status),
          ))
      )
        await this.selectRun(this.selectedId(), true);
    } finally {
      this.pollBusy = false;
    }
  }
  async submit() {
    this.submitting.set(true);
    this.formError.set('');
    try {
      const job = await firstValueFrom(this.http.post<{ id: string }>('/api/runs', this.request));
      this.closeForm();
      this.section.set('workspace');
      await this.refresh();
      await this.selectRun(job.id);
      this.setTab('log');
    } catch (error) {
      this.formError.set(this.message(error));
    } finally {
      this.submitting.set(false);
    }
  }
  openForm() {
    this.formError.set('');
    this.formDialog.nativeElement.showModal();
  }
  closeForm() {
    this.formDialog.nativeElement.close();
  }
  async loadLog() {
    const id = this.selectedId();
    if (!id) return;
    try {
      const response = await firstValueFrom(
        this.http.get<{ lines: string[] }>(`/api/runs/${encodeURIComponent(id)}/log`),
      );
      if (id === this.selectedId()) this.log.set(response.lines);
    } catch (error) {
      this.error.set(this.message(error));
    }
  }
  setTab(tab: 'orders' | 'fills' | 'trades' | 'log') {
    this.tab.set(tab);
    this.page.set(0);
    this.filter.set('');
    if (tab === 'log') void this.loadLog();
  }
  setFilter(value: string) {
    this.filter.set(value);
    this.page.set(0);
  }
  download(file: string) {
    return `/api/runs/${encodeURIComponent(this.selectedId())}/files/${file}`;
  }
  parameter(key: string): unknown {
    const entries = Object.entries(this.run()?.parameters ?? {});
    return entries.find(([name]) => name.toLowerCase() === key.toLowerCase())?.[1] ?? '—';
  }
  delay = executionDelay;
  private matches(row: object): boolean {
    return JSON.stringify(row).toLowerCase().includes(this.filter().trim().toLowerCase());
  }
  private message(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 0)
        return 'The local API is unavailable. Start Trading.Api and try again.';
      if (error.error?.errors) return Object.values(error.error.errors).flat().join(' ');
      return (
        error.error?.detail ??
        `Request failed (${error.status}). Check the API terminal for details.`
      );
    }
    return error instanceof Error ? error.message : 'An unexpected error occurred.';
  }
}
