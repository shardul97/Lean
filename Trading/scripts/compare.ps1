param(
    [string]$Symbol = 'BTCUSDT',
    [string]$TrainStart = '2025-01-01',
    [string]$TrainEnd = '2025-01-04',
    [string]$TestStart = '2025-01-05',
    [string]$TestEnd = '2025-01-07',
    [string]$Name = 'comparison'
)
$ErrorActionPreference = 'Stop'
if ([DateOnly]::Parse($TrainStart) -gt [DateOnly]::Parse($TrainEnd) -or
    [DateOnly]::Parse($TestStart) -le [DateOnly]::Parse($TrainEnd) -or
    [DateOnly]::Parse($TestEnd) -lt [DateOnly]::Parse($TestStart)) { throw 'Use chronological, disjoint train and holdout ranges.' }
$tradingRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rows = @()
$first = $true
foreach ($lookback in @(4, 6, 12)) {
    foreach ($threshold in @([decimal]0.1, [decimal]0.2)) {
        $run = "$Name-train-$lookback-$($threshold.ToString([Globalization.CultureInfo]::InvariantCulture).Replace('.', '_'))"
        & "$PSScriptRoot/run.ps1" -Symbol $Symbol -Start $TrainStart -End $TrainEnd -Lookback $lookback -EntryPressure $threshold -RunName $run -SkipBuild:(!$first)
        $first = $false
        $stats = Get-Content (Join-Path $tradingRoot "artifacts/$run/statistics.json") -Raw | ConvertFrom-Json
        $rows += [pscustomobject]@{ Lookback = $lookback; EntryPressure = $threshold; TrainReturn = $stats.TotalReturn; TrainDrawdown = $stats.MaxDrawdown; ClosedTrades = $stats.ClosedTrades }
    }
}
$ranked = @($rows | Sort-Object -Property @{ Expression = 'TrainReturn'; Descending = $true }, Lookback, EntryPressure)
$ranked | Export-Csv (Join-Path $tradingRoot "artifacts/$Name-training.csv") -NoTypeInformation
$best = $ranked[0]
# Lock the winner before touching the holdout. Fresh process and fresh state per run.
$best | ConvertTo-Json | Set-Content (Join-Path $tradingRoot "artifacts/$Name-selected.json")
& "$PSScriptRoot/run.ps1" -Symbol $Symbol -Start $TestStart -End $TestEnd -Lookback $best.Lookback -EntryPressure $best.EntryPressure -RunName "$Name-holdout" -SkipBuild
Write-Host "Training ranking: artifacts/$Name-training.csv; single selected holdout: artifacts/$Name-holdout"
