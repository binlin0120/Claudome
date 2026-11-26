#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
# Evaluate only selected production function ASTs. Every external operation in
# those functions is replaced below by in-memory fakes. No Hyper-V module, real
# registry provider, VM operation or elevation is used.
$worker=Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\HyperVWorker.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($worker,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'PRODUCTION_PARSE_FAILED'}
$names=@('Invoke-ConsolePolicyLock','Console-Owner','Use-BasicConsolePolicy','Guard-Console','Verify-Console','Restore-Console','Assert-NoNetworkAdapter','Disable-HostMetadata','Bus-ServicePath','Verify-BusService','Test-VMKeyProtectorPresent','Enable-TaskVirtualTpm','Assert-LocalPath','Test-OwnedVmMarker','Save-OwnedVmMarker','Resolve-OrCreateTaskVm','Assert-InstallDiskPath','Assert-InstallDrives','Confirm-WindowsDisk','Get-WindowsInstallProbe')
foreach($name in $names){$definition=$ast.FindAll({param($a)$a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $name},$true) | Select-Object -First 1;if(-not $definition){throw 'FUNCTION_MISSING'};Invoke-Expression -Command $definition.Extent.Text}
$script:store=@{};$script:policyEnabled=$true;$script:nicCount=0;$script:metadataEnabled=$true;$script:changes=0
$script:o=[pscustomobject]@{WorkspaceId='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';HyperVBasicConsoleConfirmed=$false}
function Get-VMHost {[pscustomobject]@{EnableEnhancedSessionMode=$script:policyEnabled}}
function Set-VMHost([bool]$EnableEnhancedSessionMode){$script:policyEnabled=$EnableEnhancedSessionMode;$script:changes++}
function Test-Path([string]$LiteralPath){$script:store.ContainsKey($LiteralPath)}
function New-Item([string]$Path,[switch]$Force){$part=$Path;while($part.Contains('\')){if(-not $script:store.ContainsKey($part)){$script:store[$part]=@{}};$part=$part.Substring(0,$part.LastIndexOf('\'))}}
function New-ItemProperty([Alias('Path')][string]$LiteralPath,[string]$Name,$Value,[string]$PropertyType,[switch]$Force){$script:store[$LiteralPath][$Name]=$Value}
function Get-ItemPropertyValue([string]$LiteralPath,[string]$Name){if(-not $script:store.ContainsKey($LiteralPath) -or -not $script:store[$LiteralPath].ContainsKey($Name)){throw 'FAKE_VALUE_MISSING'};$script:store[$LiteralPath][$Name]}
function Remove-Item([string]$LiteralPath){$script:store.Remove($LiteralPath)}
function Remove-ItemProperty([string]$LiteralPath,[string]$Name){$script:store[$LiteralPath].Remove($Name)}
function Get-ChildItem {
    [CmdletBinding()]param([string]$LiteralPath)
    foreach($key in @($script:store.Keys)){if($key.StartsWith($LiteralPath+'\') -and -not $key.Substring($LiteralPath.Length+1).Contains('\')){[pscustomobject]@{PSPath=$key}}}
}
function Get-VMNetworkAdapter($VM){for($i=0;$i -lt $script:nicCount;$i++){[pscustomobject]@{Name='fake'}}}
function Set-VM($VM,[string]$EnhancedSessionTransportType){}
function Get-VMIntegrationService($VM){[pscustomobject]@{Id=[guid]'2a34b1c2-fd73-4043-8a5b-dd2159bc743f';Name='Key-Value Pair Exchange';Enabled=$script:metadataEnabled}}
function Disable-VMIntegrationService {param([Parameter(ValueFromPipeline)]$InputObject)process{$script:metadataEnabled=$false}}
$first=[pscustomobject]@{Id=[guid]'11111111-1111-1111-1111-111111111111';State='Off'}
$second=[pscustomobject]@{Id=[guid]'22222222-2222-2222-2222-222222222222';State='Off'}
$rows=[Collections.Generic.List[object]]::new()
function Assert-Case([bool]$Condition){if(-not $Condition){throw 'ASSERTION_FAILED'}}
function Check([string]$Name,[scriptblock]$Body){try{& $Body;$rows.Add(@{name=$Name;passed=$true})}catch{$rows.Add(@{name=$Name;passed=$false;error=$_.Exception.Message})}}
Check 'console-change-needs-explicit-consent' {
    $blocked=$false;try{Guard-Console $first}catch{if($_.Exception.Message -eq 'HYPERV_CONSOLE_CONSENT'){$blocked=$true}else{throw}}
    Assert-Case ($blocked -and $script:policyEnabled -and $script:changes -eq 0 -and $script:store.Count -eq 0)
}
Check 'console-owner-protection-and-original-value' {
    $script:o.HyperVBasicConsoleConfirmed=$true;Guard-Console $first;Verify-Console $first
    Assert-Case (-not $script:policyEnabled -and $script:changes -eq 1)
}
Check 'console-verifier-rejects-policy-changes' {
    $script:policyEnabled=$true;$blocked=$false;try{Verify-Console $first}catch{if($_.Exception.Message -eq 'HYPERV_CONSOLE_POLICY'){$blocked=$true}else{throw}}
    Assert-Case $blocked;$script:policyEnabled=$false
}
Check 'console-does-not-restore-while-guest-running' {
    $first.State='Running';$blocked=$false;try{Restore-Console $first}catch{if($_.Exception.Message -eq 'HYPERV_CONSOLE_SHUTDOWN'){$blocked=$true}else{throw}}
    Assert-Case ($blocked -and -not $script:policyEnabled);$first.State='Off'
}
Check 'console-multiple-environments-retain-protection' {
    Guard-Console $second;$result=Restore-Console $first;Assert-Case ($result -eq 'kept-for-other-environments' -and -not $script:policyEnabled)
}
Check 'console-last-owner-restores-recorded-original' {
    $result=Restore-Console $second;Assert-Case ($result -eq 'restored' -and $script:policyEnabled -and -not $script:store.ContainsKey('HKLM:\SOFTWARE\Claudome\HyperVConsolePolicy'))
}
Check 'shared-clipboard-mode-allows-enhanced-session-without-consent-stop' {
    $savedO=$script:o
    try {
        $script:policyEnabled=$false
        $script:o=[pscustomobject]@{WorkspaceId='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';HyperVBasicConsoleConfirmed=$false;SharedClipboardEnabled=$true}
        Guard-Console $first
        Verify-Console $first
        Assert-Case ($script:policyEnabled)
        $script:policyEnabled=$false
        $blocked=$false;try{Verify-Console $first}catch{if($_.Exception.Message -eq 'HYPERV_CONSOLE_POLICY'){$blocked=$true}else{throw}}
        Assert-Case $blocked
        $script:policyEnabled=$true
        Assert-Case ((Restore-Console $first) -eq 'restored' -and -not $script:policyEnabled)
    } finally {
        $script:o=$savedO
        $script:policyEnabled=$true
    }
}
Check 'zero-nic-rejects-any-additional-adapter' {
    $script:nicCount=0;Assert-NoNetworkAdapter $first;$script:nicCount=1;$blocked=$false;try{Assert-NoNetworkAdapter $first}catch{if($_.Exception.Message -eq 'BOUNDARY_NICS'){$blocked=$true}else{throw}}
    Assert-Case $blocked;$script:nicCount=0
}
Check 'host-metadata-disabled-by-actual-service-id' {
    Disable-HostMetadata $first;Assert-Case (-not $script:metadataEnabled)
}
# Extract only the shutdown case. Its VM and job commands below are in-memory fakes.
$shutdownClause=$ast.FindAll({param($node)$node -is [Management.Automation.Language.SwitchStatementAst]},$true) |
    ForEach-Object {$_.Clauses} | Where-Object {$_.Item1.Value -ceq 'shutdown'}
if(@($shutdownClause).Count -ne 1){throw 'SHUTDOWN_CASE_MISSING'}
$script:shutdown=[scriptblock]::Create("switch ('shutdown') {'shutdown' "+$shutdownClause.Item2.Extent.Text+'};$value')
$script:shutdownTarget=$first
$script:shutdownCalls=0
$script:shutdownTimesOut=$false
function Target-VM($Data){
    if($Data.vmId -ne $script:shutdownTarget.Id){throw 'VM_SCOPE'}
    $script:shutdownTarget
}
function Stop-VM {
    param($VM,[switch]$AsJob,[switch]$Confirm)
    Assert-Case ($VM.Id -eq $script:shutdownTarget.Id -and $AsJob -and !$Confirm -and $args.Count -eq 0)
    $script:shutdownCalls++
    [pscustomobject]@{State='Running'}
}
function Wait-Job($Job,$Timeout){
    Assert-Case ($Timeout -eq 75)
    if(!$script:shutdownTimesOut){$Job.State='Completed';$Job}
}
function Receive-Job {[CmdletBinding()]param($Job);$script:shutdownTarget.State='Off'}
function Stop-Job {[CmdletBinding()]param($Job);$Job.State='Stopped'}
function Remove-Job {[CmdletBinding()]param($Job);Assert-Case ($Job.State -in @('Completed','Stopped'))}
Check 'hyperv-shutdown-requests-soft-stop-and-checks-off-state' {
    $d=@{vmId=$first.Id}
    $first.State='Running'
    Assert-Case ((& $script:shutdown) -eq 'off' -and $script:shutdownCalls -eq 1)
    Assert-Case ((& $script:shutdown) -eq 'off' -and $script:shutdownCalls -eq 1)
    $first.State='Running'
    $script:shutdownTimesOut=$true
    $blocked=$false
    try{& $script:shutdown}catch{if($_.Exception.Message -eq 'GUEST_SHUTDOWN_PENDING'){$blocked=$true}else{throw}}
    Assert-Case ($blocked -and $first.State -eq 'Running' -and $script:shutdownCalls -eq 2 -and $second.State -eq 'Off')
}
$script:protector=$null
$script:tpm=$false
$script:protectorWrites=0
$script:tpmEnables=0
$script:badProtectorWrite=$false
$script:badProtectorRead=$false
$script:badTpmEnable=$false
function Get-VMSecurity($VM){[pscustomobject]@{TpmEnabled=$script:tpm}}
function Get-VMKeyProtector($VM){
    if($script:badProtectorRead){throw 'fixture read failed'}
    return ,$script:protector
}
function Set-VMKeyProtector($VM,[switch]$NewLocalKeyProtector){
    Assert-Case ($VM.Id -eq $first.Id -and $NewLocalKeyProtector)
    $script:protectorWrites++
    if(-not $script:badProtectorWrite){$script:protector=[byte[]]@(5,6,7)}
}
function Enable-VMTPM($VM){
    Assert-Case ($VM.Id -eq $first.Id)
    $script:tpmEnables++
    if($script:badTpmEnable){throw 'fixture provider rejected protector'}
    Assert-Case ($script:protector.Length -gt 0)
    $script:tpm=$true
}
Check 'hyperv-empty-key-protector-is-provisioned-and-read-back-before-vtpm' {
    $script:protector=[byte[]]::new(0)
    Assert-Case (@(Get-VMKeyProtector -VM $first).Count -eq 1)
    Enable-TaskVirtualTpm $first
    Enable-TaskVirtualTpm $first
    Assert-Case ($script:tpm -and $script:protectorWrites -eq 1 -and $script:tpmEnables -eq 1)
}
Check 'hyperv-existing-protector-is-preserved-when-tpm-provider-rejects-it' {
    $script:tpm=$false
    $script:protector=[byte[]]@(8,9,10)
    $script:badTpmEnable=$true
    $stopped=$false
    try{Enable-TaskVirtualTpm $first}catch{if($_.Exception.Message -eq 'VM_TPM_ENABLE_FAILED'){$stopped=$true}else{throw}}
    $script:badTpmEnable=$false
    Assert-Case ($stopped -and !$script:tpm -and $script:protectorWrites -eq 1 -and $script:protector[0] -eq 8)
}
Check 'hyperv-protector-setup-and-read-failures-stop-before-vtpm' {
    $script:protector=$null
    $before=$script:tpmEnables
    $script:badProtectorRead=$true
    $readFailed=$false
    try{Enable-TaskVirtualTpm $first}catch{if($_.Exception.Message -eq 'VM_KEY_PROTECTOR_READ_FAILED'){$readFailed=$true}else{throw}}
    $script:badProtectorRead=$false
    $script:badProtectorWrite=$true
    $setupFailed=$false
    try{Enable-TaskVirtualTpm $first}catch{if($_.Exception.Message -eq 'VM_KEY_PROTECTOR_SETUP_FAILED'){$setupFailed=$true}else{throw}}
    Assert-Case ($readFailed -and $setupFailed -and $script:tpmEnables -eq $before -and $script:protectorWrites -eq 2)
}
Check 'hyperv-four-byte-empty-header-key-protector-is-provisioned-before-vtpm' {
    $script:tpm=$false
    $script:badProtectorWrite=$false
    $script:badProtectorRead=$false
    $script:badTpmEnable=$false
    $beforeWrites=$script:protectorWrites
    $beforeTpm=$script:tpmEnables
    $script:protector=[byte[]]@(0,0,0,4)
    Assert-Case (-not (Test-VMKeyProtectorPresent $first))
    Enable-TaskVirtualTpm $first
    Assert-Case ($script:tpm -and $script:protectorWrites -eq ($beforeWrites + 1) -and $script:tpmEnables -eq ($beforeTpm + 1))
}
Check 'hyperv-ensure-recovers-owned-vm-after-crash-before-state-save' {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('hv-ensure-recover-' + [guid]::NewGuid().ToString('N'))
    $savedO = $script:o
    $savedGetVm = ${function:Get-VM}
    $savedNewVm = ${function:New-VM}
    $savedGetNic = ${function:Get-VMNetworkAdapter}
    $savedRemoveNic = ${function:Remove-VMNetworkAdapter}
    try {
        [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
        $script:o = [pscustomobject]@{ Root = $tempRoot; VmName = 'CrashRecoverVm'; WorkspaceId = '11111111111111111111111111111111'; MemoryMb = 4096 }
        $ownedId = [guid]'44444444-4444-4444-4444-444444444444'
        $ownedPath = Join-Path $tempRoot 'machines\CrashRecoverVm'
        [IO.Directory]::CreateDirectory($ownedPath) | Out-Null
        Save-OwnedVmMarker (Join-Path $ownedPath '.claude-hyperv-owner.json') $script:o.WorkspaceId $script:o.VmName ([string]$ownedId)
        $newVmCalls = 0
        $removedNics = 0
        function Get-VM { param([string]$Name) if ($Name -eq 'CrashRecoverVm') { [pscustomobject]@{ Id = $ownedId; Name = $Name; Path = $ownedPath } } }
        function New-VM { param($Name,$Path,$Generation,$MemoryStartupBytes,[switch]$NoVHD) $script:newVmCalls++; [pscustomobject]@{ Id = [guid]::NewGuid(); Name = $Name; Path = $Path } }
        function Get-VMNetworkAdapter { param($VM) @([pscustomobject]@{ Name = 'DefaultNIC'; VM = $VM }) }
        function Remove-VMNetworkAdapter { param([Parameter(ValueFromPipeline)]$InputObject) process { $script:removedNics++ } }
        $script:newVmCalls = 0; $script:removedNics = 0
        $resolved = Resolve-OrCreateTaskVm ([pscustomobject]@{ vmId = '' })
        Assert-Case ($resolved -eq [string]$ownedId -and $script:newVmCalls -eq 0 -and $script:removedNics -eq 1)
    } finally {
        $script:o = $savedO
        Set-Item function:Get-VM -Value $savedGetVm -ErrorAction SilentlyContinue
        Set-Item function:New-VM -Value $savedNewVm -ErrorAction SilentlyContinue
        Set-Item function:Get-VMNetworkAdapter -Value $savedGetNic -ErrorAction SilentlyContinue
        Set-Item function:Remove-VMNetworkAdapter -Value $savedRemoveNic -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
Check 'hyperv-ensure-rejects-same-name-foreign-vm' {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('hv-ensure-foreign-' + [guid]::NewGuid().ToString('N'))
    $savedO = $script:o
    $savedGetVm = ${function:Get-VM}
    $savedNewVm = ${function:New-VM}
    $savedGetNic = ${function:Get-VMNetworkAdapter}
    $savedRemoveNic = ${function:Remove-VMNetworkAdapter}
    try {
        [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
        $script:o = [pscustomobject]@{ Root = $tempRoot; VmName = 'ExistingForeignVm'; WorkspaceId = '22222222222222222222222222222222'; MemoryMb = 4096 }
        $foreignPath = Join-Path $tempRoot 'other-dir\ExistingForeignVm'
        function Get-VM { param([string]$Name) if ($Name -eq 'ExistingForeignVm') { [pscustomobject]@{ Id = [guid]'55555555-5555-5555-5555-555555555555'; Name = $Name; Path = $foreignPath } } }
        function New-VM { param($Name,$Path,$Generation,$MemoryStartupBytes,[switch]$NoVHD) throw 'UNEXPECTED_NEW_VM' }
        function Get-VMNetworkAdapter { param($VM) @([pscustomobject]@{ Name = 'ForeignNIC'; VM = $VM }) }
        function Remove-VMNetworkAdapter { param([Parameter(ValueFromPipeline)]$InputObject) process { throw 'UNEXPECTED_NIC_CHANGE' } }
        $blocked = $false
        try { $null = Resolve-OrCreateTaskVm ([pscustomobject]@{ vmId = '' }) } catch { if ($_.Exception.Message -eq 'VM_COLLISION') { $blocked = $true } else { throw } }
        Assert-Case $blocked
    } finally {
        $script:o = $savedO
        Set-Item function:Get-VM -Value $savedGetVm -ErrorAction SilentlyContinue
        Set-Item function:New-VM -Value $savedNewVm -ErrorAction SilentlyContinue
        Set-Item function:Get-VMNetworkAdapter -Value $savedGetNic -ErrorAction SilentlyContinue
        Set-Item function:Remove-VMNetworkAdapter -Value $savedRemoveNic -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
Check 'clipboard-worker-returns-host-readback-from-inside-lock' {
    $clause = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.SwitchStatementAst]}, $true) |
        ForEach-Object {$_.Clauses} | Where-Object {$_.Item1.Value -ceq 'set-clipboard'})
    Assert-Case ($clause.Count -eq 1)
    $run = [scriptblock]::Create("switch ('set-clipboard') {'set-clipboard' " + $clause[0].Item2.Extent.Text + '};$value')
    $savedO = $script:o
    $script:o = [pscustomobject]@{WorkspaceId='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';HyperVBasicConsoleConfirmed=$false;SharedClipboardEnabled=$true}
    $script:policyEnabled = $false
    $script:transportType = 'VMBus'
    $script:shutdownTarget = $first
    function Get-VM([guid]$Id) { Assert-Case ($Id -eq $first.Id); [pscustomobject]@{EnhancedSessionTransportType=$script:transportType} }
    function Set-VM($VM, [string]$EnhancedSessionTransportType) {
        Assert-Case ($VM.Id -eq $first.Id -and $EnhancedSessionTransportType -eq 'HvSocket')
        $script:transportType = $EnhancedSessionTransportType
    }
    try {
        $d = @{vmId=$first.Id;enabled=$true}
        $enabled = & $run | ConvertFrom-Json
        Assert-Case ($enabled.hostEnhanced -eq $true -and $enabled.hvSocket -eq $true)
        $d.enabled = $false
        $disabled = & $run | ConvertFrom-Json
        Assert-Case ($disabled.hostEnhanced -eq $false -and $disabled.hvSocket -eq $true)
    } finally {
        $script:o = $savedO
    }
}
Check 'clipboard-guest-script-stops-sync-and-verifies-policy-with-read-only-refresh' {
    $guestScript = [scriptblock]::Create([IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\HyperVClipboardPolicy.ps1')))
    $script:clipboardFixtureWrites = 0
    $script:clipboardFixtureRunning = $true
    $script:clipboardFixturePolicy = @{}
    function Test-Path([string]$LiteralPath) { $true }
    function New-ItemProperty([string]$LiteralPath,[string]$Name,[string]$PropertyType,$Value,[switch]$Force) {
        Assert-Case ($LiteralPath -like 'HKLM:\*\Terminal Services' -and $PropertyType -eq 'DWord')
        $script:clipboardFixturePolicy[$Name] = $Value
        $script:clipboardFixtureWrites++
    }
    function Get-ItemProperty([string]$LiteralPath) { [pscustomobject]$script:clipboardFixturePolicy }
    function Get-Process([string]$Name,$ErrorAction) {
        Assert-Case ($Name -eq 'rdpclip')
        if ($script:clipboardFixtureRunning) { [pscustomobject]@{Name=$Name} }
    }
    function Stop-Process($InputObject,[switch]$Force) {
        Assert-Case ($InputObject.Name -eq 'rdpclip' -and $Force)
        $script:clipboardFixtureRunning = $false
    }

    Assert-Case ((& $guestScript -Mode Disable) -ceq 'CLIP_POLICY_OK')
    Assert-Case ($script:clipboardFixturePolicy.fDisableClip -eq 1 -and -not $script:clipboardFixtureRunning)
    Assert-Case ((& $guestScript -Mode ReadEnabled) -ceq 'CLIP_POLICY_MISMATCH')
    Assert-Case ($script:clipboardFixtureWrites -eq 8)
    Assert-Case ((& $guestScript -Mode Enable) -ceq 'CLIP_POLICY_OK')
    Assert-Case ((& $guestScript -Mode ReadEnabled) -ceq 'CLIP_POLICY_OK')
    $script:clipboardFixturePolicy['fDisableCdm'] = 0
    Assert-Case ((& $guestScript -Mode ReadEnabled) -ceq 'CLIP_POLICY_MISMATCH')
    Assert-Case ($script:clipboardFixtureWrites -eq 16)
}
Check 'hyperv-windows-install-probe-distinguishes-empty-ready-and-needs-recovery' {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('hv-probe-' + [guid]::NewGuid().ToString('N'))
    $vmDir = Join-Path $tempRoot 'machines\ProbeVm'
    $savedO = $script:o
    $savedGetDrives = ${function:Get-VMHardDiskDrive}
    $savedGetVhd = ${function:Get-VHD}
    try {
        [IO.Directory]::CreateDirectory($vmDir) | Out-Null
        $vmId = [guid]'77777777-7777-7777-7777-777777777777'
        $vm = [pscustomobject]@{ Id = $vmId; Path = $vmDir; Name = 'ProbeVm' }
        $script:o = [pscustomobject]@{ Root = $tempRoot; WorkspaceId = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; IsoSha256 = ('a' * 64); WindowsImageIndex = 4; UserName = 'ClaudeUser'; DiskGb = 80 }
        $script:probeDrives = @()
        $diskPath = Join-Path $vmDir 'Windows11.vhdx'
        $script:probeDiskGuid = [guid]'88888888-8888-8888-8888-888888888888'
        function script:Get-VMHardDiskDrive($VM) { $script:probeDrives }
        function script:Get-VHD([string]$Path) { [pscustomobject]@{ Path = $Path; DiskIdentifier = $script:probeDiskGuid; Size = 80L * 1GB; VhdFormat = 'VHDX'; VhdType = 'Dynamic'; ParentPath = $null; Attached = $true } }

        Assert-Case ((Get-WindowsInstallProbe $vm) -eq 'empty')
        $script:probeDrives = @([pscustomobject]@{ Path = $diskPath })
        Assert-Case ((Get-WindowsInstallProbe $vm) -eq 'needs-recovery')
        $cpPath = Join-Path $vmDir '.windows-install.json'
        @{ schema = 1; vmId = [string]$vmId; workspaceId = $script:o.WorkspaceId; isoSha256 = $script:o.IsoSha256; imageIndex = 4; userName = 'ClaudeUser'; diskGb = 80; diskPath = $diskPath; diskId = [string]$script:probeDiskGuid; phase = 'ready'; preservedDisks = @() } |
            ConvertTo-Json | Set-Content -LiteralPath $cpPath -Encoding utf8NoBOM
        Assert-Case ((Get-WindowsInstallProbe $vm) -eq 'ready')
        @{ schema = 1; vmId = [string]$vmId; workspaceId = 'wrong-workspace'; isoSha256 = $script:o.IsoSha256; imageIndex = 4; userName = 'ClaudeUser'; diskGb = 80; diskPath = $diskPath; diskId = [string]$script:probeDiskGuid; phase = 'ready'; preservedDisks = @() } |
            ConvertTo-Json | Set-Content -LiteralPath $cpPath -Encoding utf8NoBOM
        Assert-Case ((Get-WindowsInstallProbe $vm) -eq 'needs-recovery')
    } finally {
        $script:o = $savedO
        if ($savedGetDrives) { Set-Item function:script:Get-VMHardDiskDrive -Value $savedGetDrives } else { Remove-Item function:script:Get-VMHardDiskDrive -ErrorAction SilentlyContinue }
        if ($savedGetVhd) { Set-Item function:script:Get-VHD -Value $savedGetVhd } else { Remove-Item function:script:Get-VHD -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# Call the real .NET file API directly; the mock registry provider remains scoped
# to this test process, not the user's PowerShell session or machine.
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$failed=@($rows | Where-Object {-not $_.passed}).Count
@{type='production function AST with in-memory registry and Hyper-V fakes';passed=$rows.Count-$failed;failed=$failed;tests=@($rows)} | ConvertTo-Json -Depth 6 | ForEach-Object {[IO.File]::WriteAllText((Join-Path $OutputDirectory 'hyperv-policy-tests.json'),$_,[Text.UTF8Encoding]::new($false))}
Write-Output ('Hyper-V policy fixtures passed='+($rows.Count-$failed)+' failed='+$failed)
if($failed){$rows | Where-Object {-not $_.passed} | ConvertTo-Json -Depth 5;exit 1}
