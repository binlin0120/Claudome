#requires -Version 7.0
param([Parameter(Mandatory)][string]$PayloadPath)
$ErrorActionPreference='Stop'
$isAdmin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $isAdmin) {
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'));$start.UseShellExecute=$true;$start.Verb='runas'
    foreach($arg in @('-NoLogo','-NoProfile','-File',$PSCommandPath,'-PayloadPath',$PayloadPath)){$start.ArgumentList.Add($arg)}
    try{$process=[Diagnostics.Process]::Start($start);$process.WaitForExit();exit $process.ExitCode}catch{exit 20}
}
$result=[ordered]@{schema=2;runId='';vmId='';workspaceId='';rollbackApplied=$false;code='NOT_COMPLETED'}
$reportPath=Join-Path (Split-Path $PayloadPath) 'rollback-result.json'
try {
    $payload=Get-Content -LiteralPath $PayloadPath -Raw | ConvertFrom-Json
    $model=Get-CimInstance Win32_ComputerSystem
    if($payload.platformId -eq 'hyperv') {
        $identity=Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
        if($model.Manufacturer -ne 'Microsoft Corporation' -or $model.Model -ne 'Virtual Machine' -or $identity.vmId -ne $payload.vmId -or $identity.workspaceId -ne $payload.workspaceId){throw 'TARGET_VM_IDENTITY_MISMATCH'}
    }elseif($payload.platformId -eq 'workstation') {
        $identity=Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
        if($model.Manufacturer -notmatch 'VMware' -or $model.Model -notmatch 'VMware' -or $identity.vmId -ne $payload.vmId -or $identity.workspaceId -ne $payload.workspaceId){throw 'TARGET_VM_IDENTITY_MISMATCH'}
    }elseif($payload.platformId -eq 'virtualbox') {
        if($model.Model -notmatch 'VirtualBox' -or ([guid](Get-CimInstance Win32_ComputerSystemProduct).UUID) -ne ([guid]$payload.vmId)){throw 'TARGET_VM_IDENTITY_MISMATCH'}
    }else{throw 'TARGET_VM_IDENTITY_MISMATCH'}
    $result.runId=[string]$payload.runId;$result.vmId=[string]$payload.vmId;$result.workspaceId=[string]$payload.workspaceId
    $root='C:\ProgramData\ClaudeEnvironment'
    $profilePath=Join-Path $root 'firewall-profiles-before.json';$rulesPath=Join-Path $root 'disabled-outbound-rules.json'
    if(-not(Test-Path -LiteralPath $profilePath) -or -not(Test-Path -LiteralPath $rulesPath)){throw 'ROLLBACK_RECORD_MISSING'}
    $profiles=@(Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json)
    if($profiles.Count -ne 3 -or @($profiles | Where-Object {$_.Name -notin @('Domain','Private','Public')}).Count){throw 'ROLLBACK_RECORD_INVALID'}
    $clashRecord=Join-Path $root 'clash-before.json'
    if(Test-Path -LiteralPath $clashRecord) {
        $clash=Get-Content -LiteralPath $clashRecord -Raw | ConvertFrom-Json
        $expectedHome=Join-Path $env:APPDATA 'io.github.clash-verge-rev.clash-verge-rev'
        if($clash.home -ne $expectedHome){throw 'ROLLBACK_RECORD_INVALID'}
        Get-Process -Name 'clash-verge','Clash Verge' -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $clash.executable} | Stop-Process -Force
        Stop-Service -Name 'clash_verge_service' -ErrorAction SilentlyContinue
        Set-Service -Name 'clash_verge_service' -StartupType Manual -ErrorAction SilentlyContinue
        foreach($name in @('profiles.yaml','verge.yaml','config.yaml')) {
            $file=Join-Path $expectedHome $name
            if($file -in @($clash.existingFiles)) {
                if(-not(Test-Path -LiteralPath ($file+'.before-claude'))){throw 'ROLLBACK_RECORD_MISSING'}
                Copy-Item -LiteralPath ($file+'.before-claude') -Destination $file -Force
            }elseif(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file -Force}
        }
        $owned=Join-Path $expectedHome 'profiles\claude-environment-setup.yaml'
        if(Test-Path -LiteralPath $owned){Remove-Item -LiteralPath $owned -Force}
    }
    $startupRecord=Join-Path $root 'clash-startup-before.json'
    if(Test-Path -LiteralPath $startupRecord){
        $startup=Get-Content -LiteralPath $startupRecord -Raw | ConvertFrom-Json
        $runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        if($startup.existed){
            $kind=if($startup.valueKind){[string]$startup.valueKind}else{'String'}
            if($kind -notin @('String','ExpandString')){throw 'ROLLBACK_RECORD_INVALID'}
            if(-not(Test-Path -LiteralPath $runKey)){New-Item -Path $runKey | Out-Null}
            New-ItemProperty -LiteralPath $runKey -Name 'ClaudomeClash' -Value ([string]$startup.value) -PropertyType $kind -Force | Out-Null
        }
        elseif((Test-Path -LiteralPath $runKey) -and (Get-Item -LiteralPath $runKey).Property -contains 'ClaudomeClash'){
            Remove-ItemProperty -LiteralPath $runKey -Name 'ClaudomeClash'
        }
    }
    Stop-ScheduledTask -TaskName 'ClaudeEnvironmentProxy'  -ErrorAction SilentlyContinue
    $core=Join-Path $root 'network\mihomo.exe'
    Get-Process -Name 'mihomo' -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $core} | Stop-Process -Force
    Get-ScheduledTask -TaskName 'ClaudeEnvironmentProxy' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false
    if($payload.platformId -in @('hyperv','workstation')){
        $task='ClaudomeNetwork-'+[string]$payload.vmId
        Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
        Disable-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue | Out-Null
        Get-Process -Name 'ClaudomeGuestNetwork' -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq 'C:\ProgramData\ClaudomeNetwork\ClaudomeGuestNetwork.exe'} | Stop-Process -Force
    }
    Get-NetFirewallRule -Group 'ClaudeEnvironmentSetup' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    foreach($name in @(Get-Content -LiteralPath $rulesPath -Raw | ConvertFrom-Json)) {
        Get-NetFirewallRule -Name ([string]$name) -ErrorAction SilentlyContinue | Enable-NetFirewallRule
    }
    foreach($profile in $profiles){Set-NetFirewallProfile -Profile $profile.Name -Enabled $profile.Enabled -DefaultOutboundAction $profile.DefaultOutboundAction}
    $result.rollbackApplied=$true;$result.code='APPLIED'
} catch {
    if($_.Exception.Message -in @('TARGET_VM_IDENTITY_MISMATCH','ROLLBACK_RECORD_MISSING','ROLLBACK_RECORD_INVALID')){$result.code=$_.Exception.Message}else{$result.code='ROLLBACK_STEP_FAILED'}
} finally {
    $result | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
    if(Test-Path -LiteralPath $PayloadPath){Remove-Item -LiteralPath $PayloadPath -Force}
}
if(-not $result.rollbackApplied){exit 1}
