export interface Performance {
  initialEquity: number;
  finalEquity: number;
  totalReturn: number;
  maxDrawdown: number;
  closedTrades: number;
  winRate: number;
  totalFees: number;
  openQuantity: number;
}
export interface RunSummary {
  id: string;
  status: string;
  strategyId: string;
  symbol: string;
  start: string;
  end: string;
  createdAt: string;
  statistics: Performance | null;
  error: string | null;
  parameters: Record<string, unknown> | null;
}
export interface MarketCandle {
  time: string;
  endTime: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  pressure: number;
  signal: string;
}
export interface EquityPoint {
  time: string;
  equity: number;
}
export interface Fill {
  time: string;
  signalTime: string;
  orderId: number;
  quantity: number;
  price: number;
  fee: number;
}
export interface Trade {
  entryTime: string;
  exitTime: string;
  quantity: number;
  entryPrice: number;
  exitPrice: number;
  fees: number;
  netProfit: number;
}
export interface Order {
  orderId: number;
  time: string;
  symbol: string;
  side: string;
  type: string;
  status: string;
  quantity: number;
  price: number;
  fillQuantity: number;
  fee: number;
  reason: string;
}
export interface RunDetail {
  run: RunSummary;
  candles: MarketCandle[];
  equity: EquityPoint[];
  fills: Fill[];
  trades: Trade[];
  orders: Order[];
}
export interface BacktestRequest {
  strategyId: string;
  symbol: string;
  start: string;
  end: string;
  lookback: number;
  entryPressure: number;
  stopLoss: number;
  maxHoldingBars: number;
  downloadData: boolean;
}
export interface Strategy {
  id: string;
  name: string;
  description: string;
  signalTimeframe: string;
  confirmationTimeframe: string;
}
