param([switch]$NoStart)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'background-common.ps1')
$startupRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$paths=Get-SaidNestBackgroundPaths $startupRoot
if(-not (Test-Path -LiteralPath $paths.Exe) -or -not (Test-Path -LiteralPath (Join-Path $startupRoot '.saidnest/vault/vault.json'))){throw 'Build SaidNest and create its vault before enabling auto-start.'}
$startupCommand=Get-SaidNestStartupCommand $startupRoot
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$registry=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
try {$previous=if($registry){$registry.GetValue('SaidNest')}else{$null}}finally{if($registry){$registry.Dispose()}}
if($previous -and $previous -ne $startupCommand){throw 'Another SaidNest startup entry exists. Inspect it before replacing it.'}
if(-not (Test-Path -LiteralPath $runKey)){New-Item -Path $runKey -Force | Out-Null}
New-ItemProperty -LiteralPath $runKey -Name SaidNest -Value $startupCommand -PropertyType String -Force | Out-Null
@{enabled=$true;scope='current-user sign-in';installedUtc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $startupRoot '.saidnest/autostart.json')
if(-not $NoStart){
    $startupShell=(Get-Command pwsh -CommandType Application).Source
    $startupScript=Join-Path $PSScriptRoot 'start-background.ps1'
    Start-Process -FilePath $startupShell -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-WindowStyle','Hidden','-File',('"'+$startupScript+'"')) -WindowStyle Hidden | Out-Null
}
Write-Host 'SaidNest will start quietly when this Windows account signs in. It does not start recording.'
