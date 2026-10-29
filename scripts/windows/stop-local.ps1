$ErrorActionPreference = 'Stop'
$timelineRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$timelineMarker = Join-Path $timelineRoot '.saidnest/local-service.json'
if (-not (Test-Path -LiteralPath $timelineMarker)) { Write-Host 'No background timeline service was started here. If you launched it in a window, use Ctrl+C in that window.'; return }
$timelineInfo = Get-Content -LiteralPath $timelineMarker -Raw | ConvertFrom-Json
$timelineProcess = Get-Process -Id $timelineInfo.pid -ErrorAction SilentlyContinue
$timelineExpected = Join-Path $timelineRoot 'native/SaidNest.Capture/bin/Release/net10.0-windows10.0.19041.0/SaidNest.Capture.exe'
if ($timelineProcess) {
    if ($timelineProcess.Path -ne $timelineExpected -or $timelineProcess.StartTime.ToUniversalTime().Ticks.ToString() -ne $timelineInfo.startTicks) { throw 'The saved service identity no longer matches. Nothing was stopped.' }
    Stop-Process -InputObject $timelineProcess
}
Remove-Item -LiteralPath $timelineMarker
Write-Host 'The background local timeline is stopped. Saved recordings are unchanged.'
