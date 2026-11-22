#requires -Version 7.0
param([string]$Executable,[string[]]$Arguments,[string]$UserName,[string]$VmId,[string]$WorkspaceId,[int]$TimeoutSeconds=1800)
$ErrorActionPreference='Stop'
# Executed only inside the identity-checked guest. InteractiveToken uses an
# existing Windows desktop without storing a password or enabling remoting.
function Get-GuestInteractivePlan($Executable,$Arguments,$UserName,$VmId,$WorkspaceId){
    if($UserName -notmatch '^[a-zA-Z][a-zA-Z0-9_-]{0,19}$' -or $VmId -notmatch '^[a-fA-F0-9-]{36}$' -or $WorkspaceId -notmatch '^[a-fA-F0-9]{32}$'){throw 'VM_SCOPE'}
    $root='C:\Users\'+$UserName+'\AppData\Local\ClaudeEnvironmentSetup'
    if([IO.Path]::GetFullPath($Executable) -ne ($root+'\pwsh\pwsh.exe')){throw 'VM_SCOPE'}
    $index=[array]::IndexOf($Arguments,'-File')
    if($index -notin @(1,2) -or $Arguments.Count -ne $index+4){throw 'VM_SCOPE'}
    foreach($prefix in $Arguments[0..($index-1)]){if($prefix -notin @('-NoProfile','-NoLogo')){throw 'VM_SCOPE'}}
    $script=[IO.Path]::GetFullPath([string]$Arguments[$index+1]);$name=[IO.Path]::GetFileName($script)
    $files=@{'Configure.ps1'=@('-PayloadPath','');'BridgeInstall.ps1'=@('-InputPath','bridge-input.json');'Rollback.ps1'=@('-PayloadPath','rollback-input.json');'Validate.ps1'=@('-PayloadPath','validation-input.json')}
    if(-not $files.ContainsKey($name) -or $script -ne ($root+'\'+$name) -or $Arguments[$index+2] -ne $files[$name][0]){throw 'VM_SCOPE'}
    $inputPath=[IO.Path]::GetFullPath([string]$Arguments[$index+3])
    $inputName=[IO.Path]::GetFileName($inputPath)
    if($inputPath -ne ($root+'\'+$inputName)){throw 'VM_SCOPE'}
    if($name -eq 'Configure.ps1'){
        if($inputName -cnotmatch '^payload-[a-f0-9]{32}\.json$'){throw 'VM_SCOPE'}
    }elseif($inputName -ne $files[$name][1]){throw 'VM_SCOPE'}
    [pscustomobject]@{executable=$Executable;arguments=@($Arguments);root=$root;vmId=$VmId;workspaceId=$WorkspaceId}
}
$plan=Get-GuestInteractivePlan $Executable $Arguments $UserName $VmId $WorkspaceId
$system=Get-CimInstance Win32_ComputerSystem
$identity=Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
if($system.Manufacturer -ne 'Microsoft Corporation' -or $system.Model -ne 'Virtual Machine' -or $identity.vmId -ne $VmId -or $identity.workspaceId -ne $WorkspaceId){throw 'VM_SCOPE'}
$timeout=[Math]::Clamp($TimeoutSeconds,30,1800)
$id=[guid]::NewGuid().ToString('N');$bundle='C:\ProgramData\ClaudeBootstrap\interactive\'+$id;$task='ClaudomeInteractive-'+$id
$walk=$bundle;while($walk){if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'VM_SCOPE'};$walk=[IO.Path]::GetDirectoryName($walk)}
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
Set-Acl -LiteralPath $bundle -AclObject $acl
$inputFile=Join-Path $bundle 'invocation.json';$resultFile=Join-Path $bundle 'result.json';$runnerFile=Join-Path $bundle 'run.ps1'
$plan | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $inputFile -Encoding utf8NoBOM
@'
$ErrorActionPreference='Stop';$code=1
try{
    $p=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'invocation.json') -Raw | ConvertFrom-Json
    Set-Location -LiteralPath $p.root
    $arguments=@($p.arguments)
    & $p.executable @arguments *> $null
    $code=$LASTEXITCODE
}catch{$code=1}
@{exitCode=[int]$code;vmId=$p.vmId;workspaceId=$p.workspaceId} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'result.json') -Encoding utf8NoBOM
exit $code
'@ | Set-Content -LiteralPath $runnerFile -Encoding utf8NoBOM
$registered=$false
try{
    $action=New-ScheduledTaskAction -Execute $Executable -Argument ('-NoLogo -NoProfile -File "'+$runnerFile+'"') -WorkingDirectory $plan.root
    $principal=New-ScheduledTaskPrincipal -UserId ($env:COMPUTERNAME+'\'+$UserName) -LogonType Interactive -RunLevel Highest
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromSeconds($timeout+30)) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null;$registered=$true
    Start-ScheduledTask -TaskName $task
    $started=$false;$end=[datetime]::UtcNow.AddSeconds($timeout)
    while([datetime]::UtcNow -lt $end){
        if(Test-Path -LiteralPath $resultFile){
            $result=Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
            if($result.vmId -ne $VmId -or $result.workspaceId -ne $WorkspaceId -or $result.exitCode -ne 0){throw 'GUEST_COMMAND_FAILED'}
            return
        }
        if((Get-ScheduledTask -TaskName $task).State -eq 'Running'){$started=$true}
        if(-not $started -and [datetime]::UtcNow -gt $end.AddSeconds(-$timeout+10)){throw 'GUEST_LOGIN_REQUIRED'}
        Start-Sleep -Milliseconds 500
    }
    throw 'GUEST_COMMAND_FAILED'
}finally{
    $running=$registered -and (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue).State -eq 'Running'
    if($registered -and -not $running){Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue}
    if(-not $running){foreach($file in @($inputFile,$runnerFile,$resultFile)){if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file -Force}};Remove-Item -LiteralPath $bundle}
}
