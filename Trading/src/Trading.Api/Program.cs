using System.Net;
using Microsoft.Extensions.FileProviders;
using Trading.Api;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5057");
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(new TradingPaths(builder.Configuration["Trading:Root"] ??
    Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "../.."))));
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<BacktestJobs>();
builder.Services.AddHostedService(services => services.GetRequiredService<BacktestJobs>());
var app = builder.Build();
app.UseExceptionHandler();
// This unauthenticated research tool is restricted to the local computer.
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
    { context.Response.StatusCode = 403; return; }
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Headers.Origin is { Count: > 0 } origins)
    {
        var origin = origins.ToString();
        if (origin != $"{context.Request.Scheme}://{context.Request.Host}" &&
            origin is not "http://127.0.0.1:4200" and not "http://localhost:4200")
        { context.Response.StatusCode = 403; return; }
    }
    await next(context);
});
app.MapGet("/api/health", (TradingPaths paths) => Results.Ok(new
{
    status = "ok", backtestReady = paths.BacktestReady,
    message = paths.BacktestReady ? "Local LEAN binaries found" : "Build LEAN, the algorithm and downloader using Trading/scripts/dashboard.ps1 first."
}));
app.MapGet("/api/strategies", () => Results.Ok(new[] {
    new { id = "pressure-breakout", name = "Candle pressure breakout", description = "5m prior-range breakout with completed bullish 1h confirmation.",
        signalTimeframe = "5m", confirmationTimeframe = "1h", timeframes = new[] { "5m", "15m", "1h", "4h", "daily", "weekly" } }
}));
app.MapGet("/api/runs", (RunStore store) => Results.Ok(store.List()));
app.MapGet("/api/runs/{id}", (string id, RunStore store) =>
{
    if (!RunStore.ValidId(id)) return Results.BadRequest(new { detail = "Invalid run identifier." });
    return store.Detail(id) is { } detail ? Results.Ok(detail) : Results.NotFound();
});
app.MapGet("/api/runs/{id}/log", (string id, RunStore store) =>
    RunStore.ValidId(id) && store.Exists(id) ? Results.Ok(new { lines = store.Log(id) }) : Results.NotFound());
app.MapGet("/api/runs/{id}/files/{file}", (string id, string file, RunStore store) =>
{
    var path = store.DownloadPath(id, file);
    return path is null ? Results.NotFound() : Results.File(path, file.EndsWith(".csv") ? "text/csv" : "application/json", file);
});
app.MapPost("/api/runs", (BacktestRequest request, BacktestJobs jobs, TradingPaths paths) =>
{
    var errors = request.Validate();
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    if (!paths.BacktestReady) return Results.Problem("Build LEAN, the algorithm and downloader with Trading/scripts/dashboard.ps1 before starting a backtest.", statusCode: 409);
    var job = jobs.Enqueue(request);
    return job is null ? Results.Problem("The backtest queue is full. Wait for a run to finish.", statusCode: 429)
        : Results.Accepted($"/api/runs/{job.Id}", job);
});
var dashboard = Path.Combine(app.Services.GetRequiredService<TradingPaths>().Root, "dashboard", "dist", "trading-dashboard", "browser");
if (Directory.Exists(dashboard))
{
    var provider = new PhysicalFileProvider(dashboard);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
    app.MapFallback((HttpContext context) => context.Request.Path.StartsWithSegments("/api")
        ? Results.NotFound() : Results.File(Path.Combine(dashboard, "index.html"), "text/html"));
}
app.Run();
