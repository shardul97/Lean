param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$tradingRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $tradingRoot '..'))
$env:MSBuildEnableWorkloadResolver = 'false'
$env:NG_CLI_ANALYTICS = 'false'
function Build-Project([string]$Path) {
    & dotnet build $Path -m:1 -v:q
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $Path" }
}
if (!$SkipBuild) {
    Build-Project (Join-Path $repoRoot 'Launcher/QuantConnect.Lean.Launcher.csproj')
    Build-Project (Join-Path $tradingRoot 'src/Trading.Backtest.Lean/Trading.Backtest.Lean.csproj')
    Build-Project (Join-Path $tradingRoot 'src/Trading.Data.Binance/Trading.Data.Binance.csproj')
    Build-Project (Join-Path $tradingRoot 'src/Trading.Api/Trading.Api.csproj')
    Push-Location (Join-Path $tradingRoot 'dashboard')
    try {
        & npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Angular build failed.' }
    } finally { Pop-Location }
}
Write-Host 'Open http://127.0.0.1:5057 for the Angular dashboard. Ctrl+C stops the local API and its backtest worker.'
& dotnet run --project (Join-Path $tradingRoot 'src/Trading.Api/Trading.Api.csproj') --no-build --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw "Trading.Api stopped with exit code $LASTEXITCODE" }
