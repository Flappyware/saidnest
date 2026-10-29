function Get-SaidNestBackgroundPaths([string]$Root) {
    $fullRoot=[IO.Path]::GetFullPath($Root)
    [pscustomobject]@{
        Root=$fullRoot
        Exe=Join-Path $fullRoot 'native/SaidNest.Capture/bin/Release/net10.0-windows10.0.19041.0/SaidNest.Capture.exe'
        Marker=Join-Path $fullRoot '.saidnest/local-service.json'
        NativeMarker=Join-Path $fullRoot '.saidnest/vault/service-process.json'
        NativeStop=Join-Path $fullRoot '.saidnest/vault/service-stop.json'
        Stop=Join-Path $fullRoot '.saidnest/local-service.stop'
        Supervisor=Join-Path $fullRoot '.saidnest/local-supervisor.json'
        Lock=Join-Path $fullRoot '.saidnest/local-supervisor.lock'
    }
}
function Get-SaidNestOwnedService($Paths) {
    foreach($marker in @($Paths.Marker,$Paths.NativeMarker)) {
        if(-not (Test-Path -LiteralPath $marker)){continue}
        $stream=[IO.File]::Open($marker,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $reader=[IO.StreamReader]::new($stream)
        try {$identity=$reader.ReadToEnd() | ConvertFrom-Json}finally{$reader.Dispose()}
        $candidate=Get-Process -Id $identity.pid -ErrorAction SilentlyContinue
        # A reused PID after reboot is stale metadata, never authority to stop that process.
        if(-not $candidate){continue}
        try {
            if($candidate.StartTime.ToUniversalTime().Ticks.ToString() -ne $identity.startTicks){$candidate.Dispose();continue}
            if($candidate.Path -ne $Paths.Exe){throw 'The saved service identity belongs to another executable. Nothing was stopped or replaced.'}
        }catch [System.ComponentModel.Win32Exception] {$candidate.Dispose();continue}catch [System.InvalidOperationException] {$candidate.Dispose();continue}
        return $candidate
    }
    return $null
}
function Write-SaidNestProcessMarker([string]$Path,$Process) {
    $Process.Refresh()
    $value=@{pid=$Process.Id;startTicks=$Process.StartTime.ToUniversalTime().Ticks.ToString()} | ConvertTo-Json
    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.partial'
    [IO.File]::WriteAllText($temporary,$value)
    [IO.File]::Move($temporary,$Path,$true)
}
function Stop-SaidNestOwnedService($Paths) {
    $service=Get-SaidNestOwnedService $Paths
    if(-not $service){return}
    Write-SaidNestProcessMarker $Paths.NativeStop $service
    # Let the service cancel its decoder and persist deferral before any forced stop.
    if(-not $service.WaitForExit(10000)) {
        $current=Get-SaidNestOwnedService $Paths
        if($current -and $current.Id -eq $service.Id){$current.Kill($true);$current.WaitForExit()}
    }
}
function Get-SaidNestStartupCommand([string]$Root) {
    $shellPath=(Get-Command pwsh -CommandType Application -ErrorAction Stop).Source
    $scriptPath=Join-Path ([IO.Path]::GetFullPath($Root)) 'scripts/windows/start-background.ps1'
    if($shellPath.Contains('"') -or $scriptPath.Contains('"')){throw 'Unsupported quote in installation path.'}
    $command='"'+$shellPath+'" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File "'+$scriptPath+'"'
    if($command.Length -gt 260){throw 'The startup command exceeds the Windows Run-entry limit. Choose a shorter installation path.'}
    return $command
}
