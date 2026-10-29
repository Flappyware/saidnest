param([string]$OutFile = '.saidnest/baseline/hardware.json')
$ErrorActionPreference = 'Stop'
$baselineReport = [ordered]@{
    schemaVersion = 1
    recordedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber
    cpu = Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors
    memoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion)
    nvidia = @(& nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader)
    dotnetSdks = @(& dotnet --list-sdks)
    constraints = 'Hardware inventory only; no transcription or capture performance conclusions. Hostnames, usernames, serial numbers, and audio are excluded.'
}
$baselineParent = Split-Path -Parent ([IO.Path]::GetFullPath($OutFile))
New-Item -ItemType Directory -Force -Path $baselineParent | Out-Null
$baselineReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutFile -Encoding utf8
Write-Output "Baseline saved to $OutFile"
