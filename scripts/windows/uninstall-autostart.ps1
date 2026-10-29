$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'background-common.ps1')
$startupRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$expected=Get-SaidNestStartupCommand $startupRoot
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$registry=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
try {$current=if($registry){$registry.GetValue('SaidNest')}else{$null}}finally{if($registry){$registry.Dispose()}}
if($current -and $current -ne $expected){throw 'The startup entry belongs to another installation. It was not removed.'}
if($current){Remove-ItemProperty -LiteralPath $runKey -Name SaidNest}
$marker=Join-Path $startupRoot '.saidnest/autostart.json'
if(Test-Path -LiteralPath $marker){Remove-Item -LiteralPath $marker}
Write-Host 'SaidNest auto-start is disabled. The running local service is unchanged; use Stop-Local-SaidNest.cmd to stop it.'
