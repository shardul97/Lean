using System.Globalization;
using System.Text.Json;
using Trading.Core;
namespace Trading.Analytics;

public sealed record Fill(DateTimeOffset Time, DateTimeOffset SignalTime, int OrderId, decimal Quantity, decimal Price, decimal Fee);
public sealed record EquityPoint(DateTimeOffset Time, decimal Equity);
public sealed record CandlePoint(Candle Candle, decimal Pressure, string Signal);
public sealed record ClosedTrade(DateTimeOffset EntryTime, DateTimeOffset ExitTime, decimal Quantity, decimal EntryPrice, decimal ExitPrice, decimal Fees, decimal NetProfit);
public sealed record Performance(decimal InitialEquity, decimal FinalEquity, decimal TotalReturn,
    decimal MaxDrawdown, int ClosedTrades, decimal WinRate, decimal TotalFees, decimal OpenQuantity);

public static class RunReport
{
    public static List<ClosedTrade> MatchTrades(IReadOnlyList<Fill> fills)
    {
        var trades = new List<ClosedTrade>();
        Fill? entry = null;
        foreach (var fill in fills)
        {
            if (fill.Quantity > 0)
            {
                if (entry is not null) throw new InvalidOperationException("POC expects one full position at a time.");
                entry = fill;
            }
            else
            {
                if (entry is null || entry.Quantity != -fill.Quantity) throw new InvalidOperationException("Unmatched exit quantity.");
                var fees = entry.Fee + fill.Fee;
                trades.Add(new(entry.Time, fill.Time, entry.Quantity, entry.Price, fill.Price, fees,
                    entry.Quantity * (fill.Price - entry.Price) - fees));
                entry = null;
            }
        }
        return trades;
    }
    public static Performance Calculate(IReadOnlyList<EquityPoint> equity, IReadOnlyList<Fill> fills)
    {
        if (equity.Count < 2 || equity[0].Equity <= 0) throw new ArgumentException("Need initial and final equity.");
        var peak = equity[0].Equity;
        decimal drawdown = 0;
        foreach (var point in equity)
        {
            peak = Math.Max(peak, point.Equity);
            drawdown = Math.Max(drawdown, (peak - point.Equity) / peak);
        }
        var trades = MatchTrades(fills);
        return new(equity[0].Equity, equity[^1].Equity, equity[^1].Equity / equity[0].Equity - 1,
            drawdown, trades.Count, trades.Count == 0 ? 0 : (decimal)trades.Count(x => x.NetProfit > 0) / trades.Count,
            fills.Sum(x => x.Fee), fills.Sum(x => x.Quantity));
    }

    public static void Export(string folder, IReadOnlyList<Fill> fills, IReadOnlyList<EquityPoint> equity,
        IReadOnlyList<CandlePoint> candles, object metadata)
    {
        Directory.CreateDirectory(folder);
        string Row(params object[] values) => string.Join(',', values.Select(x => x is DateTimeOffset time ? time.ToString("O") : Convert.ToString(x, CultureInfo.InvariantCulture)));
        File.WriteAllLines(Path.Combine(folder, "fills.csv"), new[] { "time_utc,signal_time_utc,order_id,quantity,price,fee_usdt" }
            .Concat(fills.Select(x => Row(x.Time, x.SignalTime, x.OrderId, x.Quantity, x.Price, x.Fee))));
        File.WriteAllLines(Path.Combine(folder, "trades.csv"), new[] { "entry_utc,exit_utc,quantity,entry_price,exit_price,fees_usdt,net_profit_usdt" }
            .Concat(MatchTrades(fills).Select(x => Row(x.EntryTime, x.ExitTime, x.Quantity, x.EntryPrice, x.ExitPrice, x.Fees, x.NetProfit))));
        File.WriteAllLines(Path.Combine(folder, "equity.csv"), new[] { "time_utc,equity_usdt" }.Concat(equity.Select(x => Row(x.Time, x.Equity))));
        File.WriteAllLines(Path.Combine(folder, "indicators.csv"), new[] { "open_utc,end_utc,open,high,low,close,volume,pressure,signal" }
            .Concat(candles.Select(x => Row(x.Candle.OpenTime, x.Candle.EndTime, x.Candle.Open, x.Candle.High, x.Candle.Low, x.Candle.Close, x.Candle.Volume, x.Pressure, x.Signal))));
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(folder, "statistics.json"), JsonSerializer.Serialize(Calculate(equity, fills), options));
        File.WriteAllText(Path.Combine(folder, "run.json"), JsonSerializer.Serialize(metadata, options));
        var payload = JsonSerializer.Serialize(new { candles, fills, equity = equity.Where((_, i) => i % 5 == 0 || i == equity.Count - 1) });
        File.WriteAllText(Path.Combine(folder, "chart.html"), Html.Replace("/*DATA*/", payload));
    }
    private const string Html = """
<!doctype html><meta charset="utf-8"><title>Pressure breakout backtest</title>
<style>body{background:#111827;color:#e5e7eb;font:15px system-ui;margin:24px}canvas{width:100%;height:660px;background:#182235}input{width:70%}p{color:#b5c4dc}</style>
<h1>Pressure breakout · UTC</h1><p>Five-minute candles, signed body pressure, equity. Green/red triangles: buy/sell decisions. Blue/orange dots: fills. Hover for UTC time and values. Use the slider to inspect windows.</p>
<label>Window start <input id="offset" type="range" min="0" value="0"></label> <select id="window"><option>144</option><option>288</option><option>576</option><option value="0">All</option></select>
<canvas id="chart"></canvas><p id="detail">Hover over a candle.</p>
<script>
const d=/*DATA*/,c=document.querySelector('canvas'),ctx=c.getContext('2d'),slider=document.querySelector('input'),select=document.querySelector('select');let shown=[];
function draw(){const ratio=devicePixelRatio||1,w=c.clientWidth,h=660;c.width=w*ratio;c.height=h*ratio;ctx.scale(ratio,ratio);ctx.clearRect(0,0,w,h);
const n=+select.value||d.candles.length;slider.max=Math.max(0,d.candles.length-n);shown=d.candles.slice(+slider.value,+slider.value+n);if(!shown.length)return;
const lo=Math.min(...shown.map(x=>x.Candle.Low)),hi=Math.max(...shown.map(x=>x.Candle.High)),step=(w-100)/shown.length;
const y=p=>35+(hi-p)/(hi-lo||1)*305,x=i=>80+(i+.5)*step;
ctx.font='12px system-ui';ctx.fillStyle='#cbd5e1';ctx.fillText('Price (USDT) '+lo.toFixed(2)+' – '+hi.toFixed(2),8,18);ctx.fillText('Pressure [-1, 1]',8,385);ctx.fillText('Equity (USDT)',8,510);
ctx.strokeStyle='#637083';[340,430,625].forEach(v=>{ctx.beginPath();ctx.moveTo(80,v);ctx.lineTo(w-20,v);ctx.stroke()});
shown.forEach((p,i)=>{const b=p.Candle;ctx.strokeStyle=ctx.fillStyle=b.Close>=b.Open?'#34d399':'#fb7185';ctx.beginPath();ctx.moveTo(x(i),y(b.High));ctx.lineTo(x(i),y(b.Low));ctx.stroke();ctx.fillRect(x(i)-Math.max(1,step*.3),y(Math.max(b.Open,b.Close)),Math.max(1,step*.6),Math.max(1,Math.abs(y(b.Open)-y(b.Close))));
ctx.fillStyle='#a78bfa';ctx.fillRect(x(i),430,Math.max(1,step*.7),-p.Pressure*38);
if(p.Signal!=='Hold'){ctx.fillStyle=p.Signal==='Buy'?'#34d399':'#fb7185';const v=y(p.Signal==='Buy'?b.Low:b.High)+(p.Signal==='Buy'?10:-10);ctx.beginPath();ctx.moveTo(x(i),v);ctx.lineTo(x(i)-4,v+(p.Signal==='Buy'?7:-7));ctx.lineTo(x(i)+4,v+(p.Signal==='Buy'?7:-7));ctx.fill()}});
const t0=Date.parse(shown[0].Candle.OpenTime),t1=Date.parse(shown.at(-1).Candle.EndTime),tx=t=>80+(Date.parse(t)-t0)/(t1-t0)*(w-100);
d.fills.filter(f=>Date.parse(f.Time)>=t0&&Date.parse(f.Time)<=t1).forEach(f=>{ctx.fillStyle=f.Quantity>0?'#38bdf8':'#fbbf24';ctx.beginPath();ctx.arc(tx(f.Time),y(f.Price),3,0,7);ctx.fill()});
const eq=d.equity.filter(e=>Date.parse(e.Time)>=t0&&Date.parse(e.Time)<=t1);if(eq.length){const a=Math.min(...eq.map(e=>e.Equity)),b=Math.max(...eq.map(e=>e.Equity));ctx.fillStyle='#cbd5e1';ctx.fillText(a.toFixed(2)+' – '+b.toFixed(2),8,530);ctx.strokeStyle='#60a5fa';ctx.beginPath();eq.forEach((e,i)=>{const v=615-(e.Equity-a)/(b-a||1)*75;i?ctx.lineTo(tx(e.Time),v):ctx.moveTo(tx(e.Time),v)});ctx.stroke()}
ctx.fillStyle='#cbd5e1';ctx.fillText(shown[0].Candle.OpenTime,80,650);ctx.fillText(shown.at(-1).Candle.EndTime,Math.max(80,w-250),650)}
c.onmousemove=e=>{const i=Math.floor((e.offsetX-80)/(c.clientWidth-100)*shown.length),p=shown[i];if(p)document.querySelector('#detail').textContent=p.Candle.EndTime+' | O '+p.Candle.Open+' H '+p.Candle.High+' L '+p.Candle.Low+' C '+p.Candle.Close+' | Pressure '+p.Pressure.toFixed(4)+' | '+p.Signal};slider.oninput=select.onchange=draw;window.onresize=draw;draw();
</script>
""";
}
