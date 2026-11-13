param(
    [string]$Symbol = 'BTCUSDT',
    [string]$Start = '2025-01-01',
    [string]$End = '2025-01-07',
    [int]$Lookback = 6,
    [decimal]$EntryPressure = 0.15,
    [string]$RunName = 'sample',
    [switch]$SkipBuild,
    [switch]$SkipDownload
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tradingRoot = Join-Path $root 'Trading'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName must contain only letters, digits, hyphens or underscores.' }
$startDay = [DateOnly]::ParseExact($Start, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
$endDay = [DateOnly]::ParseExact($End, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
if ($endDay -lt $startDay) { throw 'End must be on or after Start.' }
$output = Join-Path $tradingRoot "artifacts/$RunName"
if (Test-Path -LiteralPath $output) { throw "Run directory already exists: $output. Choose a new RunName to preserve previous results." }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
if (!$SkipBuild) {
    Invoke-Dotnet @('build', (Join-Path $root 'Launcher/QuantConnect.Lean.Launcher.csproj'), '-v:q', '-m:1')
    Invoke-Dotnet @('build', (Join-Path $tradingRoot 'src/Trading.Backtest.Lean/Trading.Backtest.Lean.csproj'), '-v:q', '-m:1')
    Invoke-Dotnet @('build', (Join-Path $tradingRoot 'src/Trading.Data.Binance/Trading.Data.Binance.csproj'), '-v:q', '-m:1')
}
$data = Join-Path $tradingRoot 'data/lean'
if (!$SkipDownload) {
    Invoke-Dotnet @((Join-Path $tradingRoot 'src/Trading.Data.Binance/bin/Debug/net10.0/Trading.Data.Binance.dll'), $Symbol, $Start, $End, $data, (Join-Path $tradingRoot 'data/raw'))
}
foreach ($day in 0..($endDay.DayNumber - $startDay.DayNumber)) {
    $name = $startDay.AddDays($day).ToString('yyyyMMdd') + '_trade.zip'
    $file = Join-Path $data "crypto/binance/minute/$($Symbol.ToLowerInvariant())/$name"
    if (!(Test-Path -LiteralPath $file)) { throw "Missing converted data: $file" }
}
# Copy only LEAN's local market metadata; the downloaded prices stay isolated.
foreach ($folder in @('market-hours', 'symbol-properties')) {
    $destination = Join-Path $data $folder
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $root "Data/$folder") | Copy-Item -Destination $destination -Recurse -Force
}
$launcher = Join-Path $root 'Launcher/bin/Debug'
$algorithm = Join-Path $tradingRoot 'src/Trading.Backtest.Lean/bin/Debug/net10.0'
Get-ChildItem -LiteralPath $algorithm -Filter 'Trading.*.dll' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $launcher -Force
}
New-Item -ItemType Directory -Path $output | Out-Null
$config = Get-Content -LiteralPath (Join-Path $root 'Launcher/config.json') -Raw | ConvertFrom-Json -AsHashtable
$config['environment'] = 'backtesting'
$config['live-mode'] = $false
$config['algorithm-type-name'] = 'PressureBreakoutAlgorithm'
$config['algorithm-id'] = 'PressureBreakoutAlgorithm'
$config['algorithm-language'] = 'CSharp'
$config['algorithm-location'] = Join-Path $launcher 'Trading.Backtest.Lean.dll'
$config['data-folder'] = $data
$config['results-destination-folder'] = $output
$config['object-store-root'] = Join-Path $output 'storage'
$config['close-automatically'] = $true
$config['debugging'] = $false
$config['job-user-id'] = '0'
$config['api-access-token'] = ''
$config['job-organization-id'] = ''
$config['poc-lean-commit'] = (& git -C $root rev-parse HEAD)
$config['parameters'] = @{ symbol = $Symbol; start = $Start; end = $End; lookback = "$Lookback"; 'entry-pressure' = $EntryPressure.ToString([Globalization.CultureInfo]::InvariantCulture) }
$configPath = Join-Path $output 'config.json'
$config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configPath -Encoding utf8
Push-Location $launcher
try {
    Invoke-Dotnet @('QuantConnect.Lean.Launcher.dll', '--config', $configPath)
} finally { Pop-Location }
if (!(Test-Path -LiteralPath (Join-Path $output 'statistics.json'))) { throw 'LEAN did not export statistics.' }
Get-Content -LiteralPath (Join-Path $output 'statistics.json')
Write-Host "Chart and CSV reports: $output"
