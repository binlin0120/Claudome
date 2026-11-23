#requires -Version 7.0
param([Parameter(Mandatory)][string]$InputPath)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Stop-Bridge([string]$TaskName, [string]$Executable) {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($task -and $task.State -eq 'Running') { Stop-ScheduledTask -TaskName $TaskName }
    $agents = @(Get-Process -Name ClaudomeGuestNetwork -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $Executable })
    foreach ($agent in $agents) {
        if (!$agent.HasExited) { Stop-Process -InputObject $agent -Force }
        if (!$agent.WaitForExit(5000)) { throw 'BRIDGE_STOP_TIMEOUT' }
    }
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$admin) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute = $true
    $start.Verb = 'runas'
    foreach ($arg in @('-NoProfile','-File',$PSCommandPath,'-InputPath',$InputPath)) { $start.ArgumentList.Add($arg) }
    try { $process = [Diagnostics.Process]::Start($start); $process.WaitForExit(); exit $process.ExitCode }
    catch {
        $cause = $_.Exception.GetBaseException()
        if ($cause -is [ComponentModel.Win32Exception] -and $cause.NativeErrorCode -eq 1223) { exit 20 }
        exit 31
    }
}

$scopeVerified = $false
$authority = $null
$staging = $null
$retainStaging = $false
$exitCode = 21
$oldTouched = $false
$taskChanged = $false
$startAttempted = $false
$applied = [Collections.Generic.List[object]]::new()
try {
    $exitCode = 30
    $inputData = Get-Content -LiteralPath $InputPath -Raw -Encoding utf8 | ConvertFrom-Json
    $spec = $inputData.spec
    $identity = Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
    $system = Get-CimInstance Win32_ComputerSystem
    if ($identity.vmId -ne $spec.VmId -or $identity.workspaceId -ne $spec.WorkspaceId) { throw 'BRIDGE_SCOPE' }
    if ($spec.Platform -eq 'hyperv') {
        if ($system.Manufacturer -ne 'Microsoft Corporation' -or $system.Model -ne 'Virtual Machine') { throw 'BRIDGE_SCOPE' }
    } elseif ($spec.Platform -eq 'workstation') {
        if ($system.Manufacturer -notmatch 'VMware' -or $system.Model -notmatch 'VMware') { throw 'BRIDGE_SCOPE' }
    } else { throw 'BRIDGE_SCOPE' }

    $scopeVerified = $true
    $source = Join-Path (Split-Path $InputPath) 'ClaudomeGuestNetwork.exe'
    $exitCode = 21
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $inputData.executableSha256) { $exitCode = 29; throw 'BRIDGE_HASH' }
    $root = 'C:\ProgramData\ClaudomeNetwork'
    $walk = $root
    while ($walk) {
        if ((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'BRIDGE_SCOPE' }
        $walk = [IO.Path]::GetDirectoryName($walk)
    }
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    if (@(Get-ChildItem -LiteralPath $root -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'BRIDGE_SCOPE' }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sid),
            'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    Set-Acl -LiteralPath $root -AclObject $acl
    try { $authority = [IO.File]::Open((Join-Path $root 'bridge-install.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [IO.IOException] { $scopeVerified = $false; $exitCode = 28; throw }

    $taskName = 'ClaudomeNetwork-' + [string]$spec.VmId
    $executable = Join-Path $root 'ClaudomeGuestNetwork.exe'
    $previousTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    $previousTaskXml = if ($previousTask) { Export-ScheduledTask -TaskName $taskName }
    $previouslyRunning = $previousTask -and (
        $previousTask.State -eq 'Running' -or
        @(Get-Process -Name ClaudomeGuestNetwork -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $executable }).Count -gt 0)

    # Copy and hash-check the replacement, and save the original files, while the old agent is still running.
    $staging = Join-Path $root ('install-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    $stagedExe = Join-Path $staging 'ClaudomeGuestNetwork.exe'
    $stagedConfig = Join-Path $staging 'bridge.json'
    Copy-Item -LiteralPath $source -Destination $stagedExe
    if ((Get-FileHash -LiteralPath $stagedExe -Algorithm SHA256).Hash -ne $inputData.executableSha256) { $exitCode = 29; throw 'BRIDGE_HASH' }
    $spec | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $stagedConfig -Encoding utf8NoBOM
    $files = foreach ($name in @('ClaudomeGuestNetwork.exe','bridge.json')) {
        $target = Join-Path $root $name
        $backup = Join-Path $staging ($name + '.before')
        $existed = Test-Path -LiteralPath $target -PathType Leaf
        if ($existed) { Copy-Item -LiteralPath $target -Destination $backup }
        [pscustomobject]@{ Target = $target; Staged = (Join-Path $staging $name); Backup = $backup; Existed = $existed }
    }
    $action = New-ScheduledTaskAction -Execute $executable -WorkingDirectory $root
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 99 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

    $exitCode = 22
    $oldTouched = $true
    Stop-Bridge $taskName $executable
    $exitCode = 23
    foreach ($file in $files) {
        [IO.File]::Move($file.Staged, $file.Target, $true)
        $applied.Add($file)
    }
    $exitCode = 24
    $taskChanged = $true
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    $exitCode = 25
    $status = Join-Path $root 'status.json'
    if (Test-Path -LiteralPath $status) { Remove-Item -LiteralPath $status -Force }
    $started = [datetime]::UtcNow
    $startAttempted = $true
    Start-ScheduledTask -TaskName $taskName

    $exitCode = 26
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 1
        if (Test-Path -LiteralPath $status) {
            $value = Get-Content -LiteralPath $status -Raw | ConvertFrom-Json
            if ($value.vmId -eq $spec.VmId -and $value.workspaceId -eq $spec.WorkspaceId -and
                $value.connected -and [datetime]$value.timeUtc -ge $started.AddSeconds(-1)) {
                $exitCode = 0
                break
            }
        }
    }
    if ($exitCode -ne 0) { throw 'BRIDGE_NOT_CONNECTED' }
} catch {
    if ($oldTouched) {
        try {
            if ($startAttempted) { Stop-Bridge $taskName $executable }
            foreach ($file in $applied) {
                if ($file.Existed) { [IO.File]::Copy($file.Backup, $file.Target, $true) }
                elseif ([IO.File]::Exists($file.Target)) { [IO.File]::Delete($file.Target) }
            }
            if ($taskChanged) {
                if ($previousTaskXml) {
                    Register-ScheduledTask -TaskName $taskName -Xml $previousTaskXml -Force | Out-Null
                } elseif (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
                    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
                }
            }
            if ($previouslyRunning -and (Get-ScheduledTask -TaskName $taskName).State -ne 'Running') {
                Start-ScheduledTask -TaskName $taskName
            }
        } catch {
            $exitCode = 27
            $retainStaging = $true
        }
    }
} finally {
    if ($staging -and !$retainStaging -and (Test-Path -LiteralPath $staging)) {
        foreach ($name in @('ClaudomeGuestNetwork.exe','bridge.json','ClaudomeGuestNetwork.exe.before','bridge.json.before')) {
            Remove-Item -LiteralPath (Join-Path $staging $name) -Force -ErrorAction SilentlyContinue
        }
        if (!(Get-ChildItem -LiteralPath $staging -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $staging -ErrorAction SilentlyContinue
        }
    }
    if ($scopeVerified) { Remove-Item -LiteralPath $InputPath -Force -ErrorAction SilentlyContinue }
    if ($authority) { $authority.Dispose() }
}
exit $exitCode
