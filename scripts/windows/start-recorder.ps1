param([int]$Seconds = 600, [string]$MicrophoneName, [switch]$UseDefaultOutput, [string]$OutputId, [switch]$Live)
$ErrorActionPreference = 'Stop'
$captureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $captureRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$captureExe = Join-Path $captureRoot 'native/SaidNest.Capture/bin/Release/net10.0-windows10.0.19041.0/SaidNest.Capture.exe'
if (-not (Test-Path -LiteralPath $captureExe)) {
    dotnet restore native/SaidNest.Capture --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'The pinned dependencies could not be restored.' }
    dotnet build native/SaidNest.Capture -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'The recording prototype could not be built.' }
}
$captureDevices = & $captureExe devices | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not list Windows audio sources.' }
function Select-CaptureSource([string]$Kind) {
    $captureChoices = @($captureDevices | Where-Object kind -eq $Kind)
    Write-Host "`nChoose $Kind source (enter X to exclude this source):"
    for ($captureIndex = 0; $captureIndex -lt $captureChoices.Count; $captureIndex++) {
        Write-Host ('  {0}: {1}{2}' -f $captureIndex, $captureChoices[$captureIndex].name, $(if ($captureChoices[$captureIndex].isDefault) { ' (Windows default)' } else { '' }))
    }
    $captureSelection = Read-Host 'Source number or X'
    if ($captureSelection -eq 'X') { return $null }
    $captureNumber = 0
    if (-not [int]::TryParse($captureSelection, [ref]$captureNumber) -or $captureNumber -lt 0 -or $captureNumber -ge $captureChoices.Count) { throw 'Invalid source number.' }
    return $captureChoices[$captureNumber].id
}
if ($MicrophoneName) {
    $preferredMics = @($captureDevices | Where-Object { $_.kind -eq 'mic' -and $_.name.IndexOf($MicrophoneName, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
    if ($preferredMics.Count -ne 1) { throw 'The requested microphone name must match exactly one active device.' }
    $captureMic = $preferredMics[0].id
} else { $captureMic = Select-CaptureSource 'mic' }
if ($OutputId) {
    $selectedOutputs = @($captureDevices | Where-Object { $_.kind -eq 'output' -and $_.id -eq $OutputId })
    if ($selectedOutputs.Count -ne 1) { throw 'The selected output endpoint is not active.' }
    $captureOutput = $selectedOutputs[0].id
} elseif ($UseDefaultOutput) {
    $preferredOutputs = @($captureDevices | Where-Object { $_.kind -eq 'output' -and $_.isDefault })
    if ($preferredOutputs.Count -ne 1) { throw 'No unique default output is available.' }
    $captureOutput = $preferredOutputs[0].id
} else { $captureOutput = Select-CaptureSource 'output' }
if (-not $captureMic -and -not $captureOutput) { throw 'No sources selected.' }
foreach ($captureChoice in $captureDevices | Where-Object { $_.id -eq $captureMic -or $_.id -eq $captureOutput }) { Write-Host ('Selected {0}: {1}' -f $captureChoice.kind, $captureChoice.name) }
if (-not (Test-Path -LiteralPath '.saidnest/vault/vault.json')) {
    & $captureExe init
    if ($LASTEXITCODE -ne 0) { throw 'Vault setup did not finish; recording has not started.' }
}
$captureArguments = @('record', '--seconds', "$Seconds")
if ($Live) {
    if (-not (Test-Path -LiteralPath '.saidnest/inference/install.json')) { throw 'Provision the local model with scripts/windows/provision-whisper.ps1 before choosing live transcription.' }
    $captureArguments += '--live'
}
if ($captureMic) { $captureArguments += @('--mic', $captureMic) }
if ($captureOutput) { $captureArguments += @('--output', $captureOutput) }
Write-Host "`nRecording starts now. P pauses/resumes. Q stops. Audio stays in the local encrypted vault."
& $captureExe @captureArguments
if ($LASTEXITCODE -ne 0) { throw 'Recording was interrupted. Use the sessions command to inspect saved chunks.' }
Write-Host "`nRecording is saved locally. Open Start-Local-SaidNest.cmd to play it and create a local transcript."
