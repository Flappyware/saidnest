param([Parameter(Mandatory)][string]$SessionId,[Parameter(Mandatory)][int]$CaptureProcessId,[Parameter(Mandatory)][string]$Executable,[string]$OutFile='.saidnest/live-metrics.jsonl')
$ErrorActionPreference='Stop'
$measureRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $measureRoot
$measurePath=[IO.Path]::GetFullPath($OutFile)
if (-not $measurePath.StartsWith((Join-Path $measureRoot '.saidnest')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Keep local metrics under ignored .saidnest.' }
$deadline=[DateTimeOffset]::UtcNow.AddHours(1)
while([DateTimeOffset]::UtcNow -lt $deadline) {
    $capture=Get-Process -Id $CaptureProcessId -ErrorAction SilentlyContinue
    if(-not $capture) { break }
    $live=& $Executable live-status --session $SessionId | ConvertFrom-Json
    if($LASTEXITCODE -ne 0) { throw 'Local progress could not be read.' }
    $sample=[ordered]@{sampledUtc=[DateTimeOffset]::UtcNow;captureWorkingSetBytes=$capture.WorkingSet64;captureCpuSeconds=$capture.TotalProcessorTime.TotalSeconds;live=$live}
    $sample | ConvertTo-Json -Depth 8 -Compress | Add-Content -LiteralPath $measurePath
    Start-Sleep -Seconds 15
}
