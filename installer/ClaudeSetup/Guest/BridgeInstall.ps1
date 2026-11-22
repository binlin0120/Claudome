#requires -Version 7.0
param([Parameter(Mandatory)][string]$InputPath)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $admin){
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'));$start.UseShellExecute=$true;$start.Verb='runas'
    foreach($a in @('-NoProfile','-File',$PSCommandPath,'-InputPath',$InputPath)){$start.ArgumentList.Add($a)}
    try{$p=[Diagnostics.Process]::Start($start);$p.WaitForExit();exit $p.ExitCode}catch{exit 1}
}
$scopeVerified=$false
try{
    $inputData=Get-Content -LiteralPath $InputPath -Raw -Encoding utf8 | ConvertFrom-Json;$spec=$inputData.spec
    $identity=Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
    $system=Get-CimInstance Win32_ComputerSystem
    if($identity.vmId -ne $spec.VmId -or $identity.workspaceId -ne $spec.WorkspaceId){throw 'BRIDGE_SCOPE'}
    if($spec.Platform -eq 'hyperv'){if($system.Manufacturer -ne 'Microsoft Corporation' -or $system.Model -ne 'Virtual Machine'){throw 'BRIDGE_SCOPE'}}
    elseif($spec.Platform -eq 'workstation'){if($system.Manufacturer -notmatch 'VMware' -or $system.Model -notmatch 'VMware'){throw 'BRIDGE_SCOPE'}}
    else{throw 'BRIDGE_SCOPE'}
    $scopeVerified=$true
    $source=Join-Path (Split-Path $InputPath) 'ClaudomeGuestNetwork.exe'
    if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputData.executableSha256){throw 'BRIDGE_HASH'}
    $root='C:\ProgramData\ClaudomeNetwork'
    $walk=$root;while($walk){if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'BRIDGE_SCOPE'};$walk=[IO.Path]::GetDirectoryName($walk)}
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    if(@(Get-ChildItem -LiteralPath $root -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'BRIDGE_SCOPE'}
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    Set-Acl -LiteralPath $root -AclObject $acl
    $task='ClaudomeNetwork-'+[string]$spec.VmId
    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
    Get-Process -Name 'ClaudomeGuestNetwork' -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $root 'ClaudomeGuestNetwork.exe')} | Stop-Process -Force
    Copy-Item -LiteralPath $source -Destination (Join-Path $root 'ClaudomeGuestNetwork.exe') -Force
    $spec | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'bridge.json') -Encoding utf8NoBOM
    $action=New-ScheduledTaskAction -Execute (Join-Path $root 'ClaudomeGuestNetwork.exe') -WorkingDirectory $root
    $trigger=New-ScheduledTaskTrigger -AtStartup
    $principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 99 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    Remove-Item -LiteralPath (Join-Path $root 'status.json') -Force -ErrorAction SilentlyContinue
    $started=[datetime]::UtcNow
    Start-ScheduledTask -TaskName $task
    $ok=$false
    for($i=0;$i -lt 45;$i++){
        Start-Sleep -Seconds 1;$status=Join-Path $root 'status.json'
        if(Test-Path -LiteralPath $status){$value=Get-Content -LiteralPath $status -Raw | ConvertFrom-Json;if($value.vmId -eq $spec.VmId -and $value.workspaceId -eq $spec.WorkspaceId -and $value.connected -and [datetime]$value.timeUtc -ge $started.AddSeconds(-1)){$ok=$true;break}}
    }
    if(-not $ok){throw 'BRIDGE_NOT_CONNECTED'}
}catch{exit 1}finally{if($scopeVerified){Remove-Item -LiteralPath $InputPath -Force -ErrorAction SilentlyContinue}}
