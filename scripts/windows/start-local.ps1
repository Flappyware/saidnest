param([int]$Port = 8788)
$ErrorActionPreference = 'Stop'
$timelineRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $timelineRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$timelineExe = Join-Path $timelineRoot 'native/SaidNest.Capture/bin/Release/net10.0-windows10.0.19041.0/SaidNest.Capture.exe'
if (-not (Test-Path -LiteralPath '.saidnest/vault/vault.json')) { throw 'Record locally with Start-SaidNest.cmd first to create your encrypted archive.' }
if (-not (Test-Path -LiteralPath $timelineExe)) {
    dotnet restore native/SaidNest.Capture --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'The pinned dependencies could not be restored.' }
    dotnet build native/SaidNest.Capture -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'The local timeline could not be built.' }
}
Write-Host 'Opening your local archive. Keep this window open; Ctrl+C stops the service.'
& $timelineExe serve --port $Port
if ($LASTEXITCODE -ne 0) { throw 'The local timeline could not start. Check that its port is available and your Windows account can unlock the vault.' }
