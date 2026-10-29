$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'background-common.ps1')
function Assert-Background([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
$testWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testRoot=Join-Path $testWorkspace ('.saidnest/background-tests/'+[guid]::NewGuid().ToString('N'))
$testPaths=Get-SaidNestBackgroundPaths $testRoot
New-Item -ItemType Directory -Path (Join-Path $testRoot '.saidnest/vault') -Force | Out-Null
$ownProcess=Get-Process -Id $PID
$testPaths.Exe=$ownProcess.Path
Write-SaidNestProcessMarker $testPaths.Marker $ownProcess
Assert-Background ((Get-SaidNestOwnedService $testPaths).Id -eq $PID) 'Owned service identity must match process and start time.'
$stale=@{pid=$PID;startTicks=($ownProcess.StartTime.ToUniversalTime().Ticks-1).ToString()} | ConvertTo-Json
[IO.File]::WriteAllText($testPaths.Marker,$stale)
Assert-Background ($null -eq (Get-SaidNestOwnedService $testPaths)) 'A reused PID must be ignored.'
Write-SaidNestProcessMarker $testPaths.NativeMarker $ownProcess
Assert-Background ((Get-SaidNestOwnedService $testPaths).Id -eq $PID) 'Native service metadata must recover an older supervisor marker.'
$testPaths.Exe='C:\not-this-service.exe'
$rejected=$false
try {Get-SaidNestOwnedService $testPaths | Out-Null}catch {$rejected=$true}
Assert-Background $rejected 'An unrelated executable must not be adopted.'
$quotedCommand=Get-SaidNestStartupCommand $testWorkspace
Assert-Background ($quotedCommand.StartsWith('"') -and $quotedCommand.EndsWith('"') -and $quotedCommand.Contains('-WindowStyle Hidden -File "')) 'Startup command must quote executable/script paths and hide its window.'
Assert-Background ($quotedCommand.Length -le 260) 'Startup command must fit the Windows Run limit.'
$lock=[IO.FileStream]::new($testPaths.Lock,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    $blocked=$false
    try {$duplicate=[IO.FileStream]::new($testPaths.Lock,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$duplicate.Dispose()}catch [IO.IOException]{$blocked=$true}
    Assert-Background $blocked 'Only one supervisor may own the installation.'
}finally{$lock.Dispose()}
Write-Host 'Passed: background process identity, stale PID recovery, executable mismatch rejection, startup quoting, and singleton supervisor lock. No registry changes or service stops were performed.'
