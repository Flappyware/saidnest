param([string]$WorkspaceRoot=([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'background-common.ps1')
$paths=Get-SaidNestBackgroundPaths $WorkspaceRoot
if(-not (Test-Path -LiteralPath $paths.Exe) -or -not (Test-Path -LiteralPath (Join-Path $paths.Root '.saidnest/vault/vault.json'))){throw 'Build SaidNest and create the local vault before starting the background service.'}
try {$supervisorLease=[IO.FileStream]::new($paths.Lock,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}catch [IO.IOException]{return}
try {
    # Clear an earlier explicit stop only after becoming the single supervisor.
    if(Test-Path -LiteralPath $paths.Stop){Remove-Item -LiteralPath $paths.Stop}
    Write-SaidNestProcessMarker $paths.Supervisor (Get-Process -Id $PID)
    $failures=0
    while(-not (Test-Path -LiteralPath $paths.Stop)) {
        $service=Get-SaidNestOwnedService $paths
        if(-not $service){
            $outLog=Join-Path $paths.Root '.saidnest/background-service.log'
            $errorLog=Join-Path $paths.Root '.saidnest/background-service-error.log'
            # Output contains status only. Rotate before starting a new child, never while it writes.
            foreach($log in @($outLog,$errorLog)){if((Test-Path -LiteralPath $log) -and (Get-Item -LiteralPath $log).Length -gt 1048576){Copy-Item -LiteralPath $log -Destination ($log+'.previous') -Force}}
            $service=Start-Process -FilePath $paths.Exe -ArgumentList @('serve','--no-open') -WorkingDirectory $paths.Root -WindowStyle Hidden -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errorLog
            Write-SaidNestProcessMarker $paths.Marker $service
        }
        else {Write-SaidNestProcessMarker $paths.Marker $service}
        $started=[DateTime]::UtcNow
        while(-not $service.HasExited -and -not (Test-Path -LiteralPath $paths.Stop)) {Start-Sleep -Milliseconds 500;$service.Refresh()}
        if(Test-Path -LiteralPath $paths.Stop){
            Stop-SaidNestOwnedService $paths
            break
        }
        $failures=if(([DateTime]::UtcNow-$started).TotalSeconds -lt 30){[Math]::Min($failures+1,5)}else{0}
        Start-Sleep -Seconds ([Math]::Min(30,[Math]::Pow(2,$failures)))
    }
}finally {
    if(Test-Path -LiteralPath $paths.Supervisor){Remove-Item -LiteralPath $paths.Supervisor}
    $supervisorLease.Dispose()
}
