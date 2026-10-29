param([string]$Root = '.saidnest/inference')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$inferenceRoot = [IO.Path]::GetFullPath($Root)
New-Item -ItemType Directory -Force -Path $inferenceRoot | Out-Null
function Get-VerifiedAsset([string]$Url, [string]$Hash, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Destination)) {
        Invoke-WebRequest -Uri $Url -OutFile ($Destination + '.download') -TimeoutSec 300
        if ((Get-FileHash -LiteralPath ($Destination + '.download') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Hash) { throw "Checksum mismatch for $Url; file will not be used." }
        Move-Item -LiteralPath ($Destination + '.download') -Destination $Destination
    }
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Hash) { throw "Existing asset does not match expected checksum: $Destination" }
}
$runtimeUrl = 'https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-win-cuda-12.4.0-x64.zip'
$runtimeHash = 'afef0b881c500958921c3f5523b50e59ee2ec9b6f5cbd25b324c51ed308a957a'
$modelRevision = '5359861c739e955e79d9a303bcbc70fb988958b1'
$modelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/$modelRevision/ggml-tiny.en.bin"
$modelHash = '921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f'
$runtimeZip = Join-Path $inferenceRoot 'whisper-b5454-cuda12.zip'
$runtimeFolder = Join-Path $inferenceRoot 'whisper-b5454-cuda12'
$modelFile = Join-Path $inferenceRoot 'ggml-tiny.en.bin'
Write-Output 'Downloading and verifying the official pinned CUDA runtime (~653 MiB) and tiny English model (~74 MiB).'
Get-VerifiedAsset $runtimeUrl $runtimeHash $runtimeZip
if (-not (Test-Path -LiteralPath $runtimeFolder)) { Expand-Archive -LiteralPath $runtimeZip -DestinationPath $runtimeFolder }
Get-VerifiedAsset $modelUrl $modelHash $modelFile
$runtimeExe = @(Get-ChildItem -LiteralPath $runtimeFolder -Recurse -File -Filter 'whisper-cli.exe')
if ($runtimeExe.Count -ne 1) { throw 'Expected exactly one whisper-cli executable in the verified distribution.' }
[ordered]@{
    schemaVersion = 1; runtime = 'whisper.cpp'; runtimeBuild = 'b5454'; runtimeUrl = $runtimeUrl; runtimeSha256 = $runtimeHash
    executable = $runtimeExe[0].FullName; model = $modelFile; modelUrl = $modelUrl; modelRevision = $modelRevision; modelSha256 = $modelHash
    sourceLicenses = @('https://github.com/ggml-org/whisper.cpp/blob/master/LICENSE', 'https://huggingface.co/ggerganov/whisper.cpp')
    distributionNote = 'Local evaluation only. Preserve upstream licenses and CUDA redistribution notices before packaging. Not a selected production model.'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $inferenceRoot 'install.json') -Encoding utf8
Write-Output 'Pinned inference assets verified. Provisioning does not upload audio or run transcription.'
