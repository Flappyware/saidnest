$ErrorActionPreference = 'Stop'
$timelineRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'background-common.ps1')
$timelinePaths=Get-SaidNestBackgroundPaths $timelineRoot
$timelineOwned=Get-SaidNestOwnedService $timelinePaths
[IO.File]::WriteAllText($timelinePaths.Stop,'Stopped by the owner; automatic sign-in startup remains configured.')
if($timelineOwned){Stop-SaidNestOwnedService $timelinePaths}
if(Test-Path -LiteralPath $timelinePaths.Marker){Remove-Item -LiteralPath $timelinePaths.Marker}
Write-Host 'The background local timeline is stopped. Saved recordings are unchanged.'
