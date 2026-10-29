param([int]$Port = 8788)
$ErrorActionPreference = 'Stop'
$timelineRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $timelineRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$timelineExe = Join-Path $timelineRoot 'native/SaidNest.Capture/bin/Release/net10.0-windows10.0.19041.0/SaidNest.Capture.exe'
. (Join-Path $PSScriptRoot 'background-common.ps1')
$timelinePaths=Get-SaidNestBackgroundPaths $timelineRoot
if($Port -eq 8788 -and (Get-SaidNestOwnedService $timelinePaths)) {& $timelineExe open;return}
if(Test-Path -LiteralPath $timelinePaths.Stop){Remove-Item -LiteralPath $timelinePaths.Stop}
if (-not (Test-Path -LiteralPath '.saidnest/vault/vault.json')) { throw 'Record locally with Start-SaidNest.cmd first to create your encrypted archive.' }
if (-not (Test-Path -LiteralPath $timelineExe)) {
    dotnet restore native/SaidNest.Capture --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'The pinned dependencies could not be restored.' }
    dotnet build native/SaidNest.Capture -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'The local timeline could not be built.' }
}
if($Port -eq 8788 -and (Test-Path -LiteralPath (Join-Path $timelineRoot '.saidnest/autostart.json'))) {
    $timelineShell=(Get-Command pwsh -CommandType Application).Source
    $timelineBackground=Join-Path $PSScriptRoot 'start-background.ps1'
    Start-Process -FilePath $timelineShell -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-WindowStyle','Hidden','-File',('"'+$timelineBackground+'"')) -WindowStyle Hidden | Out-Null
    $timelineDeadline=[DateTime]::UtcNow.AddSeconds(15)
    while(-not (Get-SaidNestOwnedService $timelinePaths)) {if([DateTime]::UtcNow -ge $timelineDeadline){throw 'The background service did not become ready.'};Start-Sleep -Milliseconds 200}
    # Native metadata is published after its encrypted browser bootstrap, not just process creation.
    while($true){
        if(Test-Path -LiteralPath $timelinePaths.NativeMarker){
            $ready=Get-Content -LiteralPath $timelinePaths.NativeMarker -Raw | ConvertFrom-Json
            $running=Get-SaidNestOwnedService $timelinePaths
            if($running -and $ready.pid -eq $running.Id -and $ready.startTicks -eq $running.StartTime.ToUniversalTime().Ticks.ToString()){break}
        }
        if([DateTime]::UtcNow -ge $timelineDeadline){throw 'The background service did not become ready.'};Start-Sleep -Milliseconds 200
    }
    & $timelineExe open;return
}
Write-Host 'Opening your local archive. Keep this window open; Ctrl+C stops the service.'
& $timelineExe serve --port $Port
if ($LASTEXITCODE -ne 0) { throw 'The local timeline could not start. Check that its port is available and your Windows account can unlock the vault.' }
