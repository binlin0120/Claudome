#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
# Evaluate only selected production function ASTs. Every external operation in
# those functions is replaced below by in-memory fakes. No Hyper-V module, real
# registry provider, VM operation or elevation is used.
$worker=Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\HyperVWorker.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($worker,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'PRODUCTION_PARSE_FAILED'}
$names=@('Invoke-ConsolePolicyLock','Console-Owner','Use-BasicConsolePolicy','Guard-Console','Verify-Console','Restore-Console','Assert-NoNetworkAdapter','Disable-HostMetadata','Bus-ServicePath','Verify-BusService','Test-VMKeyProtectorPresent','Enable-TaskVirtualTpm','Assert-LocalPath','Test-OwnedVmMarker','Save-OwnedVmMarker','Resolve-OrCreateTaskVm','Assert-InstallDiskPath','Assert-InstallDrives','Confirm-WindowsDisk','Resolve-AttachedVhdChain','Get-WindowsInstallDiagnostics','Get-WindowsInstallProbe','Disable-TaskVmAutomaticCheckpoints','New-HyperVUnattendXml','Reset-HyperVRequestDiagnostics','Save-HyperVErrorContext','Format-HyperVErrorResponse')
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
    $savedGetSnap = ${function:Get-VMSnapshot}
    try {
        function script:Get-VMSnapshot($VM) { @() }
        [IO.Directory]::CreateDirectory($vmDir) | Out-Null
        $vmId = [guid]'77777777-7777-7777-7777-777777777777'
        $vm = [pscustomobject]@{ Id = $vmId; Path = $vmDir; Name = 'ProbeVm' }
        $script:o = [pscustomobject]@{ Root = $tempRoot; WorkspaceId = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; IsoSha256 = ('a' * 64); WindowsImageIndex = 4; UserName = 'ClaudeUser'; DiskGb = 80 }
        $script:probeDrives = @()
        $defaultDisk = Join-Path $vmDir 'Windows11.vhdx'
        $retryDisk = Join-Path $vmDir 'Windows11-retry-0123456789abcdef0123456789abcdef.vhdx'
        $defaultGuid = [guid]'88888888-8888-8888-8888-888888888888'
        $retryGuid = [guid]'99999999-9999-9999-9999-999999999999'
        $script:vhdReadCount = 0
        $script:activeGuidMap = @{
            $defaultDisk = $defaultGuid
            $retryDisk = $retryGuid
        }
        function script:Get-VMHardDiskDrive($VM) { $script:probeDrives }
        function script:Get-VHD([string]$Path) {
            $script:vhdReadCount++
            $id = $script:activeGuidMap[$Path]
            if (-not $id) { throw 'WRONG_VHD_PATH' }
            [pscustomobject]@{ Path = $Path; DiskIdentifier = $id; Size = 80L * 1GB; VhdFormat = 'VHDX'; VhdType = 'Dynamic'; ParentPath = $null; Attached = $true }
        }

        Assert-Case ((Get-WindowsInstallProbe $vm) -eq 'empty' -and $script:vhdReadCount -eq 0)
        $script:probeDrives = @([pscustomobject]@{ Path = $defaultDisk })
        Assert-Case ((Get-WindowsInstallProbe $vm) -eq 'needs-recovery')

        # 1. Default disk with valid checkpoint reads VHD once -> ready
        $cpPath = Join-Path $vmDir '.windows-install.json'
        $script:vhdReadCount = 0
        @{ schema = 1; vmId = [string]$vmId; workspaceId = $script:o.WorkspaceId; isoSha256 = $script:o.IsoSha256; imageIndex = 4; userName = 'ClaudeUser'; diskGb = 80; diskPath = $defaultDisk; diskId = [string]$defaultGuid; phase = 'ready'; preservedDisks = @() } |
            ConvertTo-Json | Set-Content -LiteralPath $cpPath -Encoding utf8NoBOM
        Assert-Case ((Get-WindowsInstallProbe $vm) -eq 'ready' -and $script:vhdReadCount -eq 1)

        # 2. Legitimate retry disk (with preserved incomplete default disk present) reads VHD once -> ready
        [IO.File]::WriteAllBytes($defaultDisk, [byte[]]@(1, 2, 3))
        $script:probeDrives = @([pscustomobject]@{ Path = $retryDisk })
        $script:vhdReadCount = 0
        @{ schema = 1; vmId = [string]$vmId; workspaceId = $script:o.WorkspaceId; isoSha256 = $script:o.IsoSha256; imageIndex = 4; userName = 'ClaudeUser'; diskGb = 80; diskPath = $retryDisk; diskId = [string]$retryGuid; phase = 'ready'; preservedDisks = @($defaultDisk) } |
            ConvertTo-Json | Set-Content -LiteralPath $cpPath -Encoding utf8NoBOM
        Assert-Case ((Get-WindowsInstallProbe $vm) -eq 'ready' -and $script:vhdReadCount -eq 1)

        # 3. Wrong disk identity (VHD DiskIdentifier mismatch or wrong attached drive) -> needs-recovery
        $script:activeGuidMap[$retryDisk] = [guid]'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
        Assert-Case ((Get-WindowsInstallProbe $vm) -eq 'needs-recovery')
        $script:activeGuidMap[$retryDisk] = $retryGuid
        $script:probeDrives = @([pscustomobject]@{ Path = $defaultDisk })
        Assert-Case ((Get-WindowsInstallProbe $vm) -eq 'needs-recovery')

        # 4. Wrong task binding (workspaceId / vmId / isoSha256 mismatch) -> needs-recovery
        $script:probeDrives = @([pscustomobject]@{ Path = $retryDisk })
        @{ schema = 1; vmId = [string]$vmId; workspaceId = 'wrong-workspace'; isoSha256 = $script:o.IsoSha256; imageIndex = 4; userName = 'ClaudeUser'; diskGb = 80; diskPath = $retryDisk; diskId = [string]$retryGuid; phase = 'ready'; preservedDisks = @($defaultDisk) } |
            ConvertTo-Json | Set-Content -LiteralPath $cpPath -Encoding utf8NoBOM
        Assert-Case ((Get-WindowsInstallProbe $vm) -eq 'needs-recovery')
    } finally {
        $script:o = $savedO
        if ($savedGetDrives) { Set-Item function:script:Get-VMHardDiskDrive -Value $savedGetDrives } else { Remove-Item function:script:Get-VMHardDiskDrive -ErrorAction SilentlyContinue }
        if ($savedGetVhd) { Set-Item function:script:Get-VHD -Value $savedGetVhd } else { Remove-Item function:script:Get-VHD -ErrorAction SilentlyContinue }
        if ($savedGetSnap) { Set-Item function:script:Get-VMSnapshot -Value $savedGetSnap } else { Remove-Item function:script:Get-VMSnapshot -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
Check 'hyperv-avhdx-parent-chain-diagnostics-checkpoint-policy-and-unattend-xml' {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('hv-avhdx-' + [guid]::NewGuid().ToString('N'))
    $vmDir = Join-Path $tempRoot 'machines\CpVm'
    $savedO = $script:o
    $savedGetDrives = ${function:Get-VMHardDiskDrive}
    $savedGetVhd = ${function:Get-VHD}
    $savedGetSnap = ${function:Get-VMSnapshot}
    $savedGetVm = ${function:Get-VM}
    $savedSetVm = ${function:Set-VM}
    try {
        [IO.Directory]::CreateDirectory($vmDir) | Out-Null
        $vmId = [guid]'12121212-1212-1212-1212-121212121212'
        $script:cpVmState = 'Off'
        $script:cpAutoEnabled = $true
        $script:cpSnaps = @()
        $script:cpSetCalls = 0
        $vm = [pscustomobject]@{ Id = $vmId; Path = $vmDir; Name = 'CpVm'; State = 'Off'; AutomaticCheckpointsEnabled = $true }
        $script:o = [pscustomobject]@{ Root = $tempRoot; VmName = 'CpVm'; WorkspaceId = 'cccccccccccccccccccccccccccccccc'; IsoSha256 = ('b' * 64); WindowsImageIndex = 4; UserName = 'ClaudeUser'; DiskGb = 80 }
        $baseDisk = Join-Path $vmDir 'Windows11.vhdx'
        $avhdxDisk = Join-Path $vmDir 'Windows11_B8944BAA-D3B5-4427-A09B-547378F9A6C2.avhdx'
        $foreignDisk = Join-Path $vmDir 'Foreign.avhdx'
        [IO.File]::WriteAllBytes($baseDisk, [byte[]]@(1))
        [IO.File]::WriteAllBytes($avhdxDisk, [byte[]]@(2))
        $baseGuid = [guid]'34343434-3434-3434-3434-343434343434'
        $cpPath = Join-Path $vmDir '.windows-install.json'
        @{ schema = 1; vmId = [string]$vmId; workspaceId = $script:o.WorkspaceId; isoSha256 = $script:o.IsoSha256; imageIndex = 4; userName = 'ClaudeUser'; diskGb = 80; diskPath = $baseDisk; diskId = [string]$baseGuid; phase = 'ready'; preservedDisks = @() } |
            ConvertTo-Json | Set-Content -LiteralPath $cpPath -Encoding utf8NoBOM

        function script:Get-VMSnapshot($VM) { $script:cpSnaps }
        function script:Get-VM([guid]$Id) { [pscustomobject]@{ Id = $vmId; Path = $vmDir; Name = 'CpVm'; State = $script:cpVmState; AutomaticCheckpointsEnabled = $script:cpAutoEnabled } }
        function script:Set-VM { param($VM, [bool]$AutomaticCheckpointsEnabled) $script:cpSetCalls++; $script:cpAutoEnabled = $AutomaticCheckpointsEnabled }
        function script:Get-VMHardDiskDrive($VM) { $script:cpDrives }
        function script:Get-VHD([string]$Path) {
            if ($Path -eq $baseDisk) { return [pscustomobject]@{ Path = $baseDisk; DiskIdentifier = $baseGuid; Size = 80L * 1GB; VhdFormat = 'VHDX'; VhdType = 'Dynamic'; ParentPath = $null; Attached = $true } }
            if ($Path -eq $avhdxDisk) { return [pscustomobject]@{ Path = $avhdxDisk; DiskIdentifier = [guid]::NewGuid(); Size = 80L * 1GB; VhdFormat = 'VHDX'; VhdType = 'Differencing'; ParentPath = $baseDisk; Attached = $true } }
            if ($Path -eq $foreignDisk) { return [pscustomobject]@{ Path = $foreignDisk; DiskIdentifier = [guid]::NewGuid(); Size = 80L * 1GB; VhdFormat = 'VHDX'; VhdType = 'Differencing'; ParentPath = $foreignDisk; Attached = $true } }
            throw 'UNKNOWN_VHD'
        }

        # 1. Active .avhdx matching base chain diagnoses checkpoint-chain-matched cleanly and stays needs-recovery (no auto-delete/merge)
        $script:cpSnaps = @([pscustomobject]@{ Name = 'AutoSnap' })
        $script:cpDrives = @([pscustomobject]@{ Path = $avhdxDisk })
        $diag1 = Get-WindowsInstallDiagnostics $vm
        Assert-Case ($diag1.state -eq 'needs-recovery' -and $diag1.reason -eq 'checkpoint-chain-matched' -and $diag1.chainMatchesBase -eq $true -and $diag1.snapshotCount -eq 1)
        $cpBlocked = $false
        try { $null = Disable-TaskVmAutomaticCheckpoints $vm } catch { if ($_.Exception.Message -eq 'HYPERV_CHECKPOINT_ACTIVE') { $cpBlocked = $true } }
        Assert-Case ($cpBlocked -and $script:cpSetCalls -eq 0 -and [IO.File]::Exists($avhdxDisk))

        # 2. Cycle / foreign chain diagnoses checkpoint-chain-invalid
        $script:cpDrives = @([pscustomobject]@{ Path = $foreignDisk })
        $diag2 = Get-WindowsInstallDiagnostics $vm
        Assert-Case ($diag2.state -eq 'needs-recovery' -and $diag2.reason -eq 'checkpoint-chain-invalid' -and $diag2.chainMatchesBase -eq $false)

        # 3. Clean Off VM with merged disk disables automatic checkpoints and reads back
        $script:cpSnaps = @()
        $script:cpDrives = @([pscustomobject]@{ Path = $baseDisk })
        Assert-Case ((Disable-TaskVmAutomaticCheckpoints $vm) -eq 'disabled' -and -not $script:cpAutoEnabled -and $script:cpSetCalls -eq 1)
        Assert-Case ((Disable-TaskVmAutomaticCheckpoints $vm) -eq 'already-disabled' -and $script:cpSetCalls -eq 1)

        # 4. Unattend XML emits International-Core without unsupported OEM/Server switches
        $xml = New-HyperVUnattendXml 'ClaudeUser' 'StrongPass!12' '已有订阅链接' 'Korea Standard Time' 'zh-CN'
        $doc = [xml]$xml
        Assert-Case ($null -ne $doc -and $xml.Contains('Microsoft-Windows-International-Core') -and $xml.Contains('<UILanguage>zh-CN</UILanguage>') -and -not $xml.Contains('HideEULAPage') -and -not $xml.Contains('HideLocalAccountScreen') -and -not $xml.Contains('BypassNRO') -and -not $xml.Contains('ProtectYourPC'))
    } finally {
        $script:o = $savedO
        if ($savedGetDrives) { Set-Item function:script:Get-VMHardDiskDrive -Value $savedGetDrives } else { Remove-Item function:script:Get-VMHardDiskDrive -ErrorAction SilentlyContinue }
        if ($savedGetVhd) { Set-Item function:script:Get-VHD -Value $savedGetVhd } else { Remove-Item function:script:Get-VHD -ErrorAction SilentlyContinue }
        if ($savedGetSnap) { Set-Item function:script:Get-VMSnapshot -Value $savedGetSnap } else { Remove-Item function:script:Get-VMSnapshot -ErrorAction SilentlyContinue }
        if ($savedGetVm) { Set-Item function:script:Get-VM -Value $savedGetVm } else { Remove-Item function:script:Get-VM -ErrorAction SilentlyContinue }
        if ($savedSetVm) { Set-Item function:script:Set-VM -Value $savedSetVm } else { Remove-Item function:script:Set-VM -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
Check 'hyperv-prepare-preserves-dism-hresult-and-resets-last-exit-code' {
    $ifPrepared = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -match "operation\s*-eq\s*'prepare'" }, $true) | Select-Object -First 1
    Assert-Case ($null -ne $ifPrepared)
    $prepareBlockText = $ifPrepared.Clauses[0].Item2.Extent.Text
    $prepareBlock = [scriptblock]::Create($prepareBlockText.Substring(1, $prepareBlockText.Length - 2))
    $savedO = $script:o
    $savedGetItemProp = ${function:Get-ItemProperty}
    $savedGetFeature = ${function:Get-WindowsOptionalFeature}
    $savedEnableFeature = ${function:Enable-WindowsOptionalFeature}
    try {
        function script:Get-ItemProperty([string]$Path) { [pscustomobject]@{ EditionID = 'Professional' } }
        $script:featureThrowHr = -2146498548
        $script:featureState = 'Disabled'
        function script:Get-WindowsOptionalFeature([switch]$Online, [string]$FeatureName, $ErrorAction) {
            if ($script:featureThrowHr -ne 0) {
                $ex = [System.Runtime.InteropServices.COMException]::new('D:\secret\path\should-not-leak: feature missing', [int]$script:featureThrowHr)
                throw $ex
            }
            [pscustomobject]@{ State = $script:featureState }
        }
        function script:Enable-WindowsOptionalFeature([switch]$Online, [string]$FeatureName, [switch]$All, [switch]$NoRestart, $ErrorAction) {
            throw ([System.Runtime.InteropServices.COMException]::new('enable failure', -2146498548))
        }

        # Case 1: HYPERV_FEATURE_NOT_FOUND preserves 0x800F080C (-2146498548), not 0x80131501
        Reset-HyperVRequestDiagnostics
        $req1 = [pscustomobject]@{ operation = 'prepare'; data = @{} }
        $resp1 = $null
        try { & $prepareBlock } catch { $resp1 = Format-HyperVErrorResponse $req1 $_ }
        $json1 = $resp1 | ConvertTo-Json -Compress
        Assert-Case ($resp1.code -eq 'HYPERV_FEATURE_NOT_FOUND' -and $resp1.errorHResult -eq -2146498548 -and $resp1.errorSubstage -eq 'query-feature' -and -not $json1.Contains('should-not-leak'))

        # Case 2: HYPERV_FEATURE_QUERY_FAILED preserves 0x80070005 (-2147024891), and previous LASTEXITCODE does not leak across requests
        $global:LASTEXITCODE = 77
        $script:windowsNativeCode = 99
        Reset-HyperVRequestDiagnostics
        $script:featureThrowHr = -2147024891
        $resp2 = $null
        try { & $prepareBlock } catch { $resp2 = Format-HyperVErrorResponse $req1 $_ }
        Assert-Case ($resp2.code -eq 'HYPERV_FEATURE_QUERY_FAILED' -and $resp2.errorHResult -eq -2147024891 -and (-not $resp2.ContainsKey('errorNativeCode')) -and $global:LASTEXITCODE -eq 0)
    } finally {
        $script:o = $savedO
        if ($savedGetItemProp) { Set-Item function:script:Get-ItemProperty -Value $savedGetItemProp } else { Remove-Item function:script:Get-ItemProperty -ErrorAction SilentlyContinue }
        if ($savedGetFeature) { Set-Item function:script:Get-WindowsOptionalFeature -Value $savedGetFeature } else { Remove-Item function:script:Get-WindowsOptionalFeature -ErrorAction SilentlyContinue }
        if ($savedEnableFeature) { Set-Item function:script:Enable-WindowsOptionalFeature -Value $savedEnableFeature } else { Remove-Item function:script:Enable-WindowsOptionalFeature -ErrorAction SilentlyContinue }
    }
}
Check 'guest-doh-fallback-and-hyperv-display-mode-selection' {
    $auditTokens = $null; $auditErrors = $null
    $auditAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\NetworkAudit.ps1'), [ref]$auditTokens, [ref]$auditErrors)
    if ($auditErrors.Count) { throw 'NETWORK_AUDIT_PARSE_FAILED' }
    $fnDns = $auditAst.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Test-ObservedDns' }, $true)
    if (-not $fnDns) { throw 'MISSING_TEST_OBSERVED_DNS' }
    . ([scriptblock]::Create($fnDns.Extent.Text))

    $intTokens = $null; $intErrors = $null
    $intAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\Integrations.ps1'), [ref]$intTokens, [ref]$intErrors)
    if ($intErrors.Count) { throw 'INTEGRATIONS_PARSE_FAILED' }
    $fnMode = $intAst.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Select-PreferredHyperVDisplayMode' }, $true)
    if (-not $fnMode) { throw 'MISSING_DISPLAY_SELECTOR' }
    . ([scriptblock]::Create($fnMode.Extent.Text))

    $savedGetTaskUplink = ${function:Get-TaskUplink}
    $savedGetNetAdapter = ${function:Get-NetAdapter}
    $savedGetNetRoute = ${function:Get-NetRoute}
    $savedInvokeRest = ${function:Invoke-RestMethod}
    $savedTestPhysical = ${function:Test-PhysicalProbe}
    $savedGetBlockAudit = ${function:Get-GuestBlockAuditStatus}
    try {
        function script:Get-TaskUplink { [pscustomobject]@{ ifIndex = 5 } }
        function script:Get-NetAdapter { @([pscustomobject]@{ ifIndex = 5; Status = 'Up'; Name = 'ClaudomeBridge' }, [pscustomobject]@{ ifIndex = 9; Status = 'Up'; Name = 'ClaudeTun' }) }
        function script:Get-NetRoute { @() }
        function script:Get-GuestBlockAuditStatus { 'ENABLED' }
        $script:physicalStub = 'DENIED'
        function script:Test-PhysicalProbe([switch]$Dns) { $script:physicalStub }

        # Case 1: Cloudflare fails, Google JSON with matching Question (A) + TC=false + DENIED => PASSED
        function script:Invoke-RestMethod {
            param([string]$Uri, [switch]$NoProxy, $MaximumRedirection, $ConnectionTimeoutSeconds, $OperationTimeoutSeconds, $Headers)
            if ($Uri -like '*cloudflare-dns.com*') { throw 'cloudflare blocked' }
            $qName = if ($Uri -match 'name=([^&]+)') { [Uri]::UnescapeDataString($Matches[1]) } else { '' }
            [pscustomobject]@{ Status = 3; TC = $false; Question = @([pscustomobject]@{ name = ($qName + '.'); type = 1 }) }
        }
        Assert-Case ((Test-ObservedDns) -eq 'PASSED' -and $script:LastObservedDnsDetail.dohProvider -eq 'Google' -and $script:LastObservedDnsDetail.queryMatched -and $script:LastObservedDnsDetail.physicalProbe -eq 'DENIED')

        # Case 1b: Status=0 WITHOUT Question (bare stub), truncated TC=true, or wrong type => DOH_UNVERIFIED (never PASSED)
        function script:Invoke-RestMethod {
            param([string]$Uri, [switch]$NoProxy, $MaximumRedirection, $ConnectionTimeoutSeconds, $OperationTimeoutSeconds, $Headers)
            [pscustomobject]@{ Status = 0 }
        }
        Assert-Case ((Test-ObservedDns) -eq 'DOH_UNVERIFIED' -and $script:LastObservedDnsDetail.dohReason -eq 'QUESTION_MISMATCH')

        function script:Invoke-RestMethod {
            param([string]$Uri, [switch]$NoProxy, $MaximumRedirection, $ConnectionTimeoutSeconds, $OperationTimeoutSeconds, $Headers)
            $qName = if ($Uri -match 'name=([^&]+)') { [Uri]::UnescapeDataString($Matches[1]) } else { '' }
            [pscustomobject]@{ Status = 0; TC = $true; Question = @([pscustomobject]@{ name = $qName; type = 1 }) }
        }
        Assert-Case ((Test-ObservedDns) -eq 'DOH_UNVERIFIED' -and $script:LastObservedDnsDetail.dohReason -eq 'TRUNCATED')

        function script:Invoke-RestMethod {
            param([string]$Uri, [switch]$NoProxy, $MaximumRedirection, $ConnectionTimeoutSeconds, $OperationTimeoutSeconds, $Headers)
            $qName = if ($Uri -match 'name=([^&]+)') { [Uri]::UnescapeDataString($Matches[1]) } else { '' }
            [pscustomobject]@{ Status = 0; TC = $false; Question = @([pscustomobject]@{ name = $qName; type = 28 }) }
        }
        Assert-Case ((Test-ObservedDns) -eq 'DOH_UNVERIFIED')

        # Case 2: Both DoH fail, BUT real physical probe shows BYPASS => BYPASS wins over DOH_UNVERIFIED
        function script:Invoke-RestMethod {
            param([string]$Uri, [switch]$NoProxy, $MaximumRedirection, $ConnectionTimeoutSeconds, $OperationTimeoutSeconds, $Headers)
            throw 'all doh failed'
        }
        $script:physicalStub = 'BYPASS'
        Assert-Case ((Test-ObservedDns) -eq 'BYPASS')

        # Case 3: Both DoH fail + DENIED => DOH_UNVERIFIED
        $script:physicalStub = 'DENIED'
        Assert-Case ((Test-ObservedDns) -eq 'DOH_UNVERIFIED')

        # Case 4: Display mode picks fitting widescreen resolution for embedded viewport without exceeding height
        $modes = @(
            [pscustomobject]@{ width = 1024; height = 768 },
            [pscustomobject]@{ width = 1280; height = 720 },
            [pscustomobject]@{ width = 1920; height = 1080 }
        )
        $picked = Select-PreferredHyperVDisplayMode $modes 1000 500
        Assert-Case ($null -ne $picked -and $picked.width -eq 1280 -and $picked.height -eq 720 -and ($picked.height * $picked.zoom) -le 500)
    } finally {
        if ($savedGetTaskUplink) { Set-Item function:script:Get-TaskUplink -Value $savedGetTaskUplink } else { Remove-Item function:script:Get-TaskUplink -ErrorAction SilentlyContinue }
        if ($savedGetNetAdapter) { Set-Item function:script:Get-NetAdapter -Value $savedGetNetAdapter } else { Remove-Item function:script:Get-NetAdapter -ErrorAction SilentlyContinue }
        if ($savedGetNetRoute) { Set-Item function:script:Get-NetRoute -Value $savedGetNetRoute } else { Remove-Item function:script:Get-NetRoute -ErrorAction SilentlyContinue }
        if ($savedInvokeRest) { Set-Item function:script:Invoke-RestMethod -Value $savedInvokeRest } else { Remove-Item function:script:Invoke-RestMethod -ErrorAction SilentlyContinue }
        if ($savedTestPhysical) { Set-Item function:script:Test-PhysicalProbe -Value $savedTestPhysical } else { Remove-Item function:script:Test-PhysicalProbe -ErrorAction SilentlyContinue }
        if ($savedGetBlockAudit) { Set-Item function:script:Get-GuestBlockAuditStatus -Value $savedGetBlockAudit } else { Remove-Item function:script:Get-GuestBlockAuditStatus -ErrorAction SilentlyContinue }
    }
}

Check 'hyperv-worker-display-only-isolation-and-task-cleanup' {
    $workerPath = Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\HyperVWorker.ps1'
    $workerText = Get-Content -LiteralPath $workerPath -Raw -Encoding UTF8
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($workerPath, [ref]$null, [ref]$parseErrors)
    Assert-Case ($parseErrors.Count -eq 0)
    Assert-Case ($workerText -match '\$displayOnly\s*=\s*\$false')
    Assert-Case ($workerText -match '\$spawnerOnly\s*=\s*\[bool\]\$SpawnerOnly')
    Assert-Case ($workerText -match 'if\s*\(\$displayOnly\s*-and\s*\$request\.operation\s*-ne\s*''sync-display''\)\s*\{\s*throw\s+''UNSUPPORTED_OPERATION''\s*\}')
    Assert-Case ($workerText -match 'if\s*\(\$spawnerOnly\s*-and\s*\$request\.operation\s*-ne\s*''launch-verifier''\)\s*\{\s*throw\s+''UNSUPPORTED_OPERATION''\s*\}')
    Assert-Case ($workerText -match 'if\s*\(\$verificationOnly\s*-and\s*\$request\.operation\s*-ne\s*''boundary-verify''\)\s*\{\s*throw\s+''UNSUPPORTED_OPERATION''\s*\}')
    Assert-Case ($workerText -match 'ClaudomeDisplay-\*')
    Assert-Case ($workerText -match 'Stop-ScheduledTask' -and $workerText -match 'Unregister-ScheduledTask' -and $workerText -match 'Remove-Item\s+-LiteralPath\s+\$bundle')
}

Check 'hyperv-worker-spawner-only-live-process-isolation' {
    # Production HyperVWorker.ps1 process test (no VM touched): real pwsh worker auto-spawns isolated -SpawnerOnly
    # child, spawner launches displayOnly child without touching main pipe, and exiting main cleans up children.
    $workerSource = Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\HyperVWorker.ps1'
    $pwshExe = Join-Path $PSHOME 'pwsh.exe'
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('claudome-spawner-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $privateDir = Join-Path $tempRoot 'private'
    $mainPipeName = 'ClaudeSetup-' + [guid]::NewGuid().ToString('N')
    $spawnerPipeName = 'ClaudeSetup-' + [guid]::NewGuid().ToString('N')
    $recoveryPipeName = 'ClaudeSetup-' + [guid]::NewGuid().ToString('N')
    $displayPipeName = 'ClaudeSetup-' + [guid]::NewGuid().ToString('N')
    $mainPipe = $null; $spawnerPipe = $null; $displayPipe = $null; $mainProc = $null; $displayPid = 0
    try {
        [IO.Directory]::CreateDirectory($privateDir) | Out-Null
        $workerCopy = Join-Path $privateDir 'HyperVWorker-live.ps1'
        $displayCopy = Join-Path $privateDir 'HyperVWorker-display.ps1'
        Copy-Item -LiteralPath $workerSource -Destination $workerCopy -Force
        Copy-Item -LiteralPath $workerSource -Destination $displayCopy -Force

        $mainPipe = [IO.Pipes.NamedPipeServerStream]::new($mainPipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
        $spawnerPipe = [IO.Pipes.NamedPipeServerStream]::new($spawnerPipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
        $displayPipe = [IO.Pipes.NamedPipeServerStream]::new($displayPipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)

        $psi = [Diagnostics.ProcessStartInfo]::new($pwshExe)
        $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
        foreach ($arg in @('-NoLogo','-NoProfile','-File',$workerCopy,'-PipeName',$mainPipeName,'-SpawnerPipeName',$spawnerPipeName,'-RecoveryPipeName',$recoveryPipeName)) { $psi.ArgumentList.Add($arg) }
        $mainProc = [Diagnostics.Process]::Start($psi)
        Assert-Case ($null -ne $mainProc)

        $cts = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
        $mainPipe.WaitForConnectionAsync($cts.Token).GetAwaiter().GetResult()
        $spawnerPipe.WaitForConnectionAsync($cts.Token).GetAwaiter().GetResult()

        $mainR = [IO.StreamReader]::new($mainPipe,[Text.Encoding]::UTF8,$false,4096,$true)
        $mainW = [IO.StreamWriter]::new($mainPipe,[Text.UTF8Encoding]::new($false),4096,$true); $mainW.AutoFlush = $true
        $spR = [IO.StreamReader]::new($spawnerPipe,[Text.Encoding]::UTF8,$false,4096,$true)
        $spW = [IO.StreamWriter]::new($spawnerPipe,[Text.UTF8Encoding]::new($false),4096,$true); $spW.AutoFlush = $true

        $opts = @{ PlatformId = 'hyperv'; VmName = 'SpawnerLiveTest'; Root = $tempRoot }
        $mainW.WriteLine((@{ operation = 'init'; data = @{ options = $opts; verificationOnly = $false; displayOnly = $false } } | ConvertTo-Json -Compress -Depth 5))
        $mainInit = ($mainR.ReadLine() | ConvertFrom-Json)
        Assert-Case ($mainInit.ok -and $mainInit.value -eq 'initialized')

        $spW.WriteLine((@{ operation = 'init'; data = @{ options = $opts; verificationOnly = $false; displayOnly = $false; spawnerOnly = $true } } | ConvertTo-Json -Compress -Depth 5))
        $spInit = ($spR.ReadLine() | ConvertFrom-Json)
        Assert-Case ($spInit.ok -and $spInit.value -eq 'initialized')

        # Spawner-only process MUST reject non-launch-verifier operations
        $spW.WriteLine((@{ operation = 'sync-display'; data = @{ vmId = '00000000-0000-0000-0000-000000000000' } } | ConvertTo-Json -Compress -Depth 5))
        $spRejected = ($spR.ReadLine() | ConvertFrom-Json)
        Assert-Case (-not $spRejected.ok -and $spRejected.code -eq 'UNSUPPORTED_OPERATION')

        # Spawner-only launches real displayOnly child pwsh.exe without touching main pipe
        $spW.WriteLine((@{ operation = 'launch-verifier'; data = @{ pipeName = $displayPipeName; scriptPath = $displayCopy } } | ConvertTo-Json -Compress -Depth 5))
        $spLaunch = ($spR.ReadLine() | ConvertFrom-Json)
        Assert-Case ($spLaunch.ok -and [int]::TryParse([string]$spLaunch.value, [ref]$displayPid) -and $displayPid -gt 0)

        $displayPipe.WaitForConnectionAsync($cts.Token).GetAwaiter().GetResult()
        $dispR = [IO.StreamReader]::new($displayPipe,[Text.Encoding]::UTF8,$false,4096,$true)
        $dispW = [IO.StreamWriter]::new($displayPipe,[Text.UTF8Encoding]::new($false),4096,$true); $dispW.AutoFlush = $true
        $dispW.WriteLine((@{ operation = 'init'; data = @{ options = $opts; verificationOnly = $false; displayOnly = $true } } | ConvertTo-Json -Compress -Depth 5))
        $dispInit = ($dispR.ReadLine() | ConvertFrom-Json)
        Assert-Case ($dispInit.ok -and $dispInit.value -eq 'initialized')

        # Display-only child rejects non-sync-display operations
        $dispW.WriteLine((@{ operation = 'running'; data = @{ vmId = '00000000-0000-0000-0000-000000000000' } } | ConvertTo-Json -Compress -Depth 5))
        $dispRejected = ($dispR.ReadLine() | ConvertFrom-Json)
        Assert-Case (-not $dispRejected.ok -and $dispRejected.code -eq 'UNSUPPORTED_OPERATION')

        # Even if spawner & display pipes are closed abruptly, main pipe remains alive AND the isolated
        # -RecoveryPipeName background Runspace rebuilds a fresh -SpawnerOnly child WITHOUT touching main pipe
        # (and supports reconnecting after an abrupt recovery pipe close)!
        $dispW.Dispose(); $dispR.Dispose(); $displayPipe.Dispose(); $displayPipe = $null
        $spW.Dispose(); $spR.Dispose(); $spawnerPipe.Dispose(); $spawnerPipe = $null
        $mainW.WriteLine((@{ id = 'main-req-1'; operation = 'init'; data = @{ options = $opts; verificationOnly = $false; displayOnly = $false } } | ConvertTo-Json -Compress -Depth 5))
        $mainStillAlive = ($mainR.ReadLine() | ConvertFrom-Json)
        Assert-Case ($mainStillAlive.ok -and $mainStillAlive.value -eq 'initialized' -and $mainStillAlive.id -eq 'main-req-1' -and -not $mainProc.HasExited)

        $spawner2PipeName = 'ClaudeSetup-' + [guid]::NewGuid().ToString('N')
        $display2PipeName = 'ClaudeSetup-' + [guid]::NewGuid().ToString('N')
        $recPipe1 = [IO.Pipes.NamedPipeServerStream]::new($recoveryPipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
        $spawner2Pipe = [IO.Pipes.NamedPipeServerStream]::new($spawner2PipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
        $display2Pipe = [IO.Pipes.NamedPipeServerStream]::new($display2PipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
        try {
            $recPipe1.WaitForConnectionAsync($cts.Token).GetAwaiter().GetResult()
            $recR = [IO.StreamReader]::new($recPipe1,[Text.Encoding]::UTF8,$false,4096,$true)
            $recW = [IO.StreamWriter]::new($recPipe1,[Text.UTF8Encoding]::new($false),4096,$true); $recW.AutoFlush = $true
            $recW.WriteLine((@{ id = 'rec-bad-1'; operation = 'running'; data = @{} } | ConvertTo-Json -Compress -Depth 5))
            $recBad = ($recR.ReadLine() | ConvertFrom-Json)
            Assert-Case (-not $recBad.ok -and $recBad.code -eq 'UNSUPPORTED_OPERATION' -and $recBad.id -eq 'rec-bad-1')

            $recW.WriteLine((@{ id = 'rec-launch-1'; operation = 'launch-verifier'; data = @{ pipeName = $spawner2PipeName; scriptPath = $workerCopy; spawnerOnly = $true } } | ConvertTo-Json -Compress -Depth 5))
            $sp2Launch = ($recR.ReadLine() | ConvertFrom-Json)
            $sp2Pid = 0
            Assert-Case ($sp2Launch.ok -and $sp2Launch.id -eq 'rec-launch-1' -and [int]::TryParse([string]$sp2Launch.value, [ref]$sp2Pid) -and $sp2Pid -gt 0)
            $recW.Dispose(); $recR.Dispose(); $recPipe1.Dispose(); $recPipe1 = $null

            $spawner2Pipe.WaitForConnectionAsync($cts.Token).GetAwaiter().GetResult()
            $sp2R = [IO.StreamReader]::new($spawner2Pipe,[Text.Encoding]::UTF8,$false,4096,$true)
            $sp2W = [IO.StreamWriter]::new($spawner2Pipe,[Text.UTF8Encoding]::new($false),4096,$true); $sp2W.AutoFlush = $true
            $sp2W.WriteLine((@{ id = 'sp2-init'; operation = 'init'; data = @{ options = $opts; verificationOnly = $false; displayOnly = $false; spawnerOnly = $true } } | ConvertTo-Json -Compress -Depth 5))
            $sp2Init = ($sp2R.ReadLine() | ConvertFrom-Json)
            Assert-Case ($sp2Init.ok -and $sp2Init.value -eq 'initialized' -and $sp2Init.id -eq 'sp2-init')
            $sp2W.WriteLine((@{ id = 'sp2-launch'; operation = 'launch-verifier'; data = @{ pipeName = $display2PipeName; scriptPath = $displayCopy } } | ConvertTo-Json -Compress -Depth 5))
            $d2Launch = ($sp2R.ReadLine() | ConvertFrom-Json)
            $d2Pid = 0
            Assert-Case ($d2Launch.ok -and $d2Launch.id -eq 'sp2-launch' -and [int]::TryParse([string]$d2Launch.value, [ref]$d2Pid) -and $d2Pid -gt 0)
            $display2Pipe.WaitForConnectionAsync($cts.Token).GetAwaiter().GetResult()
            $d2R = [IO.StreamReader]::new($display2Pipe,[Text.Encoding]::UTF8,$false,4096,$true)
            $d2W = [IO.StreamWriter]::new($display2Pipe,[Text.UTF8Encoding]::new($false),4096,$true); $d2W.AutoFlush = $true
            $d2W.WriteLine((@{ id = 'd2-init'; operation = 'init'; data = @{ options = $opts; verificationOnly = $false; displayOnly = $true } } | ConvertTo-Json -Compress -Depth 5))
            $d2Init = ($d2R.ReadLine() | ConvertFrom-Json)
            Assert-Case ($d2Init.ok -and $d2Init.value -eq 'initialized' -and $d2Init.id -eq 'd2-init')
            $d2W.WriteLine('{"operation":"exit"}'); $sp2W.WriteLine('{"operation":"exit"}')
            $d2W.Dispose(); $d2R.Dispose(); $sp2W.Dispose(); $sp2R.Dispose()

            # Verify recovery channel cleanly reconnects on a second recovery pipe after abrupt disconnect
            $recPipe2 = [IO.Pipes.NamedPipeServerStream]::new($recoveryPipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
            try {
                $recPipe2.WaitForConnectionAsync($cts.Token).GetAwaiter().GetResult()
                $rec2R = [IO.StreamReader]::new($recPipe2,[Text.Encoding]::UTF8,$false,4096,$true)
                $rec2W = [IO.StreamWriter]::new($recPipe2,[Text.UTF8Encoding]::new($false),4096,$true); $rec2W.AutoFlush = $true
                $rec2W.WriteLine((@{ id = 'rec-reconn-init'; operation = 'init'; data = @{} } | ConvertTo-Json -Compress -Depth 5))
                $rec2Ok = ($rec2R.ReadLine() | ConvertFrom-Json)
                Assert-Case ($rec2Ok.ok -and $rec2Ok.value -eq 'initialized' -and $rec2Ok.id -eq 'rec-reconn-init')
                $rec2W.Dispose(); $rec2R.Dispose()
            } finally { try { $recPipe2.Dispose() } catch { } }
        } finally {
            if ($recPipe1) { try { $recPipe1.Dispose() } catch { } }
            try { $display2Pipe.Dispose() } catch { }
            try { $spawner2Pipe.Dispose() } catch { }
        }

        # Clean exit tears down main worker cleanly
        $mainW.WriteLine('{"operation":"exit"}')
        Assert-Case ($mainProc.WaitForExit(5000))
    } finally {
        if ($displayPid -gt 0) { try { $dp = Get-Process -Id $displayPid -ErrorAction SilentlyContinue; if ($dp -and -not $dp.HasExited) { $dp.Kill($true) } } catch { } }
        if ($mainProc -and -not $mainProc.HasExited) { try { $mainProc.Kill($true) } catch { } }
        if ($displayPipe) { try { $displayPipe.Dispose() } catch { } }
        if ($spawnerPipe) { try { $spawnerPipe.Dispose() } catch { } }
        if ($mainPipe) { try { $mainPipe.Dispose() } catch { } }
        if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Check 'hyperv-guest-status-probe-returns-ready-unregisters-and-handles-timeout-and-exit' {
    # Load production Register-SupervisedChild, Unregister-SupervisedChild, Stop-SupervisedChildren, and Get-HyperVGuestStatus
    $workerPath = Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\HyperVWorker.ps1'
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($workerPath, [ref]$null, [ref]$null)
    $fnNames = @('Register-SupervisedChild', 'Unregister-SupervisedChild', 'Stop-SupervisedChildren', 'Get-HyperVGuestStatus')
    foreach ($fnName in $fnNames) {
        $fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fnName }, $true) | Select-Object -First 1
        Assert-Case ($null -ne $fn)
        . ([scriptblock]::Create($fn.Extent.Text))
    }

    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('claudome-guest-status-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $savedTargetVm = ${function:Target-VM}
    $savedWorkerPath = $script:workerScriptPath
    $savedO = $script:o
    $savedChildren = $script:supervisedChildren
    $longChild = $null
    try {
        [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
        $script:supervisedChildren = [Collections.Concurrent.ConcurrentDictionary[int,Diagnostics.Process]]::new()
        $script:o = [pscustomobject]@{ Root = $tempRoot; VmName = 'GuestProbeVm'; WorkspaceId = '11111111111111111111111111111111' }
        function Target-VM($Data) { [pscustomobject]@{ Id = [guid]'55555555-5555-5555-5555-555555555555' } }

        # 1. Probe worker returns STATUS:Ready -> Get-HyperVGuestStatus returns 'Ready' cleanly and unregisters PID
        $readyProbe = Join-Path $tempRoot 'probe-ready.ps1'
        [IO.File]::WriteAllText($readyProbe, "param([switch]`$ProbeGuestStatus)`n`$null=[Console]::In.ReadLine()`nWrite-Output 'STATUS:Ready'`n", [Text.UTF8Encoding]::new($false))
        $script:workerScriptPath = $readyProbe
        $statusReady = Get-HyperVGuestStatus ([pscustomobject]@{ vmId = '55555555-5555-5555-5555-555555555555'; password = 'pw' }) 5000
        Assert-Case ($statusReady -eq 'Ready' -and $script:supervisedChildren.Count -eq 0)

        # 2. Probe worker hangs beyond timeout -> Get-HyperVGuestStatus kills probe, returns SessionUnavailable, and unregisters PID
        $hangProbe = Join-Path $tempRoot 'probe-hang.ps1'
        [IO.File]::WriteAllText($hangProbe, "param([switch]`$ProbeGuestStatus)`nStart-Sleep -Seconds 30`n", [Text.UTF8Encoding]::new($false))
        $script:workerScriptPath = $hangProbe
        $statusTimeout = Get-HyperVGuestStatus ([pscustomobject]@{ vmId = '55555555-5555-5555-5555-555555555555'; password = 'pw' }) 350
        Assert-Case ($statusTimeout -eq 'SessionUnavailable' -and $script:supervisedChildren.Count -eq 0)

        # 3. Worker exit cleanup kills registered child by held Process handle and clears registry
        $si = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
        $si.UseShellExecute = $false; $si.CreateNoWindow = $true
        foreach ($a in @('-NoLogo','-NoProfile','-Command','Start-Sleep -Seconds 30')) { $si.ArgumentList.Add($a) }
        $longChild = [Diagnostics.Process]::Start($si)
        Assert-Case ($null -ne $longChild -and -not $longChild.HasExited)
        $longChildPid = $longChild.Id
        Register-SupervisedChild $longChild
        Assert-Case ($script:supervisedChildren.Count -eq 1)
        Stop-SupervisedChildren
        $longChild = $null
        Assert-Case ($script:supervisedChildren.Count -eq 0 -and $null -eq (Get-Process -Id $longChildPid -ErrorAction SilentlyContinue))
    } finally {
        if ($longChild) { try { if (-not $longChild.HasExited) { $longChild.Kill($true) }; $longChild.Dispose() } catch { } }
        if ($savedTargetVm) { Set-Item function:Target-VM -Value $savedTargetVm } else { Remove-Item function:Target-VM -ErrorAction SilentlyContinue }
        $script:workerScriptPath = $savedWorkerPath
        $script:o = $savedO
        $script:supervisedChildren = $savedChildren
        if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Check 'new-vm-default-video-sets-1920x1080-with-maximum-fallback-and-preserves-existing-vm' {
    $workerPath = Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\HyperVWorker.ps1'
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($workerPath, [ref]$null, [ref]$null)
    foreach ($fnName in @('Assert-LocalPath', 'Test-OwnedVmMarker', 'Save-OwnedVmMarker', 'Set-NewTaskVmDefaultVideo', 'Resolve-OrCreateTaskVm')) {
        $fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fnName }, $true) | Select-Object -First 1
        Assert-Case ($null -ne $fn)
        . ([scriptblock]::Create($fn.Extent.Text))
    }

    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('claudome-vmvideo-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $savedO = $script:o
    $videoCalls = [Collections.Generic.List[string]]::new()
    $script:failSingle = $false
    $script:mockVideo = @{ w = 1024; h = 768; t = 'Single' }
    $script:mockExistingVm = $null
    try {
        [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
        $script:o = [pscustomobject]@{ Root = $tempRoot; VmName = 'NewVideoVm'; WorkspaceId = '22222222222222222222222222222222'; MemoryMb = 4096 }
        function Get-VM([string]$Name = '', $Id = $null) { return $script:mockExistingVm }
        function New-VM([string]$Name, [string]$Path, [int]$Generation, [long]$MemoryStartupBytes, [switch]$NoVHD) {
            return [pscustomobject]@{ Id = [guid]'66666666-6666-6666-6666-666666666666'; Name = $Name; Path = $Path }
        }
        function Get-VMNetworkAdapter($VM) { @() }
        function Remove-VMNetworkAdapter { }
        function Set-VMVideo($VM, [int]$HorizontalResolution, [int]$VerticalResolution, [string]$ResolutionType) {
            $videoCalls.Add("${HorizontalResolution}x${VerticalResolution}:${ResolutionType}")
            if ($script:failSingle -and $ResolutionType -eq 'Single') { throw 'Single mode unsupported' }
            $script:mockVideo = @{ w = $HorizontalResolution; h = $VerticalResolution; t = $ResolutionType }
        }
        function Get-VMVideo($VM) {
            return [pscustomobject]@{ HorizontalResolution = $script:mockVideo.w; VerticalResolution = $script:mockVideo.h; ResolutionType = $script:mockVideo.t }
        }

        # 1. Newly created VM gets 1920x1080 Single and records verified video state in owner marker.
        $createdId = Resolve-OrCreateTaskVm ([pscustomobject]@{ vmId = '' })
        Assert-Case ($createdId -eq '66666666-6666-6666-6666-666666666666')
        Assert-Case ($videoCalls.Count -eq 1 -and $videoCalls[0] -eq '1920x1080:Single')
        $markerPath = Join-Path $tempRoot 'machines\NewVideoVm\.claude-hyperv-owner.json'
        $marker = [IO.File]::ReadAllText($markerPath) | ConvertFrom-Json
        Assert-Case ($marker.defaultVideo.verified -eq $true -and $marker.defaultVideo.applied -eq '1920x1080' -and $marker.defaultVideo.resolutionType -eq 'Single')

        # 2. Existing VM reuse NEVER calls Set-VMVideo (preserves existing VM config).
        $videoCalls.Clear()
        $script:mockExistingVm = [pscustomobject]@{ Id = [guid]'66666666-6666-6666-6666-666666666666'; Name = 'NewVideoVm'; Path = (Join-Path $tempRoot 'machines\NewVideoVm') }
        $reusedId = Resolve-OrCreateTaskVm ([pscustomobject]@{ vmId = '' })
        Assert-Case ($reusedId -eq '66666666-6666-6666-6666-666666666666' -and $videoCalls.Count -eq 0)

        # 3. Single mode failure falls back cleanly to Maximum and verifies 1920x1080 readback.
        $videoCalls.Clear()
        $script:failSingle = $true
        $fallbackRes = Set-NewTaskVmDefaultVideo ([pscustomobject]@{ Name = 'FallbackVm' }) 1920 1080
        Assert-Case ($fallbackRes.verified -eq $true -and $fallbackRes.resolutionType -eq 'Maximum' -and $videoCalls.Count -eq 2 -and $videoCalls[1] -eq '1920x1080:Maximum')
    } finally {
        $script:o = $savedO
        foreach ($mockFn in @('Get-VM','New-VM','Get-VMNetworkAdapter','Remove-VMNetworkAdapter','Set-VMVideo','Get-VMVideo')) {
            Remove-Item ("function:$mockFn") -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Check 'clash-runtime-settings-mismatch-reports-sanitized-8-field-diff-without-secrets' {
    $auditPath = Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\NetworkAudit.ps1'
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($auditPath, [ref]$null, [ref]$null)
    foreach ($fnName in @('Get-ClashAuditRawValue', 'Test-ClashStrictBool', 'Test-ClashStrictInt', 'Test-ClashApprovedEnum', 'Get-ClashRuntimeSettingMismatches')) {
        $fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fnName }, $true) | Select-Object -First 1
        Assert-Case ($null -ne $fn)
        . ([scriptblock]::Create($fn.Extent.Text))
    }

    $validActive = [pscustomobject]@{
        'mixed-port' = 17897
        'allow-lan' = $false
        ipv6 = $false
        mode = 'Rule'
        secret = 'SUPER-SECRET-TOKEN-999'
        tun = [pscustomobject]@{
            enable = $true
            device = 'ClaudeTun'
            'auto-route' = $true
            'strict-route' = $true
        }
    }
    $okDiff = @(Get-ClashRuntimeSettingMismatches $validActive)
    Assert-Case ($okDiff.Count -eq 0)

    # Probe 1: Missing allow-lan / ipv6 MUST be reported as missing (never zero mismatches).
    $missingFalseActive = [pscustomobject]@{
        'mixed-port' = 17897
        mode = 'rule'
        tun = [pscustomobject]@{ enable = $true; device = 'ClaudeTun'; 'auto-route' = $true; 'strict-route' = $true }
    }
    $missingFalseDiff = @(Get-ClashRuntimeSettingMismatches $missingFalseActive)
    $missingFalseText = $missingFalseDiff -join '；'
    Assert-Case ($missingFalseDiff.Count -eq 2 -and $missingFalseText.Contains('allow-lan: 期望 false，实际 缺失') -and $missingFalseText.Contains('ipv6: 期望 false，实际 缺失'))

    # Probe 2: String "false" on required-true TUN booleans MUST be rejected as type anomaly (never truthy).
    $stringBoolActive = [pscustomobject]@{
        'mixed-port' = '17897'
        'allow-lan' = 'false'
        ipv6 = $false
        mode = 'rule'
        tun = [pscustomobject]@{ enable = 'false'; device = 'ClaudeTun'; 'auto-route' = 'false'; 'strict-route' = 'false' }
    }
    $stringBoolDiff = @(Get-ClashRuntimeSettingMismatches $stringBoolActive)
    $stringBoolText = $stringBoolDiff -join '；'
    Assert-Case ($stringBoolDiff.Count -eq 5)
    Assert-Case ($stringBoolText.Contains('mixed-port: 期望 17897，实际 类型异常'))
    Assert-Case ($stringBoolText.Contains('allow-lan: 期望 false，实际 类型异常'))
    Assert-Case ($stringBoolText.Contains('tun.enable: 期望 true，实际 类型异常'))
    Assert-Case ($stringBoolText.Contains('tun.auto-route: 期望 true，实际 类型异常'))
    Assert-Case ($stringBoolText.Contains('tun.strict-route: 期望 true，实际 类型异常'))

    # Probe 3: Untrusted/unknown scalar strings MUST collapse to 未知值 (never printed or truncated raw).
    $untrustedActive = [pscustomobject]@{
        'mixed-port' = 7897
        'allow-lan' = $true
        ipv6 = $true
        mode = 'SECRET_MODE_TOKEN_XYZ'
        secret = 'SUPER-SECRET-TOKEN-999'
        subscription = 'https://secret.sub.example/token=abc123'
        tun = [pscustomobject]@{
            enable = $false
            device = 'SENSITIVE_CUSTOM_ADAPTER_NAME'
            'auto-route' = $false
        }
    }
    $badDiff = @(Get-ClashRuntimeSettingMismatches $untrustedActive)
    $joined = $badDiff -join '；'
    Assert-Case ($badDiff.Count -eq 8)
    Assert-Case ($joined.Contains('mixed-port: 期望 17897，实际 7897'))
    Assert-Case ($joined.Contains('allow-lan: 期望 false，实际 true'))
    Assert-Case ($joined.Contains('ipv6: 期望 false，实际 true'))
    Assert-Case ($joined.Contains('mode: 期望 rule，实际 未知值'))
    Assert-Case ($joined.Contains('tun.enable: 期望 true，实际 false'))
    Assert-Case ($joined.Contains('tun.device: 期望 ClaudeTun，实际 未知值'))
    Assert-Case ($joined.Contains('tun.auto-route: 期望 true，实际 false'))
    Assert-Case ($joined.Contains('tun.strict-route: 期望 true，实际 缺失'))
    Assert-Case (-not $joined.Contains('SECRET_MODE_TOKEN') -and -not $joined.Contains('SENSITIVE_CUSTOM') -and -not $joined.Contains('SUPER-SECRET') -and -not $joined.Contains('secret.sub.example'))
}

Check 'outage-test-retries-transient-recovery-and-requires-both-block-and-recovery-segments' {
    $auditPath = Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\NetworkAudit.ps1'
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($auditPath, [ref]$null, [ref]$null)
    foreach ($fnName in @('Wait-ClashRecoveryReady', 'Test-NetworkOutage')) {
        $fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fnName }, $true) | Select-Object -First 1
        Assert-Case ($null -ne $fn)
        . ([scriptblock]::Create($fn.Extent.Text))
    }

    $script:coreStopped = $false
    $script:runtimeCalls = 0
    $script:mockDirectProbe = 'DENIED'
    try {
        function Start-Sleep([int]$Seconds = 0, [int]$Milliseconds = 0) { }
        function Trace-Request { if ($script:coreStopped) { throw 'BLOCKED' }; return @{ ip = '1.2.3.4'; loc = 'KR' } }
        function Read-GuestTrace { return Trace-Request }
        function Assert-ExpectedTrace($t) { if ($t.loc -ne 'KR') { throw 'EXIT_MISMATCH' } }
        function Stop-Core { $script:coreStopped = $true }
        function Start-Core { $script:coreStopped = $false; $script:runtimeCalls = 0 }
        function Assert-CoreStopped { if (-not $script:coreStopped) { throw 'CORE_RESTARTED_DURING_OUTAGE' } }
        function Test-PhysicalProbe { return $script:mockDirectProbe }
        function Assert-ClashRuntime {
            $script:runtimeCalls++
            if ($script:runtimeCalls -lt 3) { throw 'CLASH_CONTROLLER_UNAVAILABLE' }
        }

        # 1. Transient controller unavailability after Start-Core recovers via bounded retry -> both segments pass.
        $passed = Test-NetworkOutage ''
        Assert-Case ($passed -eq $true -and $script:runtimeCalls -eq 3 -and $script:LastOutageStage -eq 'passed' -and $script:LastOutageBlockedVerified -eq $true -and $script:LastOutageRecoveryVerified -eq $true)

        # 2. Physical probe BYPASS during outage fails strictly with stage=outage-block-check even if recovery passes.
        $script:mockDirectProbe = 'BYPASS'
        $threwBypass = $false
        try { $null = Test-NetworkOutage '' } catch { $threwBypass = ($_.Exception.Message -eq 'OUTAGE_PROTECTION_FAILED') }
        Assert-Case ($threwBypass -and $script:LastOutageStage -eq 'outage-block-check' -and $script:LastOutageBlockedVerified -eq $false)

        # 3. Physical probe UNVERIFIED throws OUTAGE_UNVERIFIED with stage=outage-unverified.
        $script:mockDirectProbe = 'UNVERIFIED'
        $threwUnverified = $false
        try { $null = Test-NetworkOutage '' } catch { $threwUnverified = ($_.Exception.Message -eq 'OUTAGE_UNVERIFIED') }
        Assert-Case ($threwUnverified -and $script:LastOutageStage -eq 'outage-unverified' -and $script:LastOutageBlockedVerified -eq $false)
    } finally {
        foreach ($mockFn in @('Start-Sleep','Trace-Request','Read-GuestTrace','Assert-ExpectedTrace','Stop-Core','Start-Core','Assert-CoreStopped','Test-PhysicalProbe','Assert-ClashRuntime')) {
            Remove-Item ("function:$mockFn") -ErrorAction SilentlyContinue
        }
    }
}

Check 'clipboard-sync-agent-parses-and-enforces-unicode-only-isolation' {
    $agentPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\ClipboardSyncAgent.ps1'))
    Assert-Case ([IO.File]::Exists($agentPath))
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($agentPath, [ref]$null, [ref]$parseErrors)
    Assert-Case ($parseErrors.Count -eq 0)
    $raw = [IO.File]::ReadAllText($agentPath, [Text.UTF8Encoding]::new($false))
    Assert-Case ($raw.Contains('CF_UNICODETEXT = 13'))
    Assert-Case ($raw.Contains('CF_HDROP = 15'))
    Assert-Case ($raw.Contains('IsClipboardFormatAvailable(CF_HDROP)'))
    Assert-Case ($raw.Contains('AddClipboardFormatListener'))
    Assert-Case ($raw.Contains('GetClipboardSequenceNumber'))
    Assert-Case ($raw.Contains('CLDCLP01'))
    Assert-Case ($raw.Contains('a42e7cda-d03f-480c-9cc2-a4de20abb878'))
    Assert-Case ($raw.Contains('[IO.File]::Delete($bootstrapFile)'))
    Assert-Case (-not $raw.Contains('System.Windows.Forms'))
    # Compile the embedded C# bridge directly in pwsh 7 to guarantee zero assembly/type errors on guest.
    $m = [regex]::Match($raw, "(?s)Add-Type -Language CSharp -TypeDefinition @'\r?\n(.*?)\r?\n'@")
    Assert-Case ($m.Success)
    $uniqueCode = $m.Groups[1].Value.Replace('ClaudomeGuestClipboardBridge', 'ClaudomeGuestClipboardBridgeTest_' + [guid]::NewGuid().ToString('N'))
    Add-Type -Language CSharp -TypeDefinition $uniqueCode
}

Check 'hyperv-worker-clipboard-service-path-verifies-task-scoped-element-name' {
    $workerPath = Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\HyperVWorker.ps1'
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($workerPath, [ref]$null, [ref]$null)
    $clipFn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Clipboard-ServicePath' }, $true) | Select-Object -First 1
    Assert-Case ($null -ne $clipFn)
    . ([scriptblock]::Create($clipFn.Extent.Text))
    $savedO = $script:o
    try {
        $script:o = [pscustomobject]@{ WorkspaceId = 'ws-clip-123' }
        $good = Clipboard-ServicePath ([pscustomobject]@{ workspaceId = 'ws-clip-123'; clipboardServiceId = '11111111-2222-3333-4444-555555555555' })
        Assert-Case ($good.EndsWith('\11111111-2222-3333-4444-555555555555'))
        $threwScope = $false
        try { $null = Clipboard-ServicePath ([pscustomobject]@{ workspaceId = 'other-ws'; clipboardServiceId = '11111111-2222-3333-4444-555555555555' }) }
        catch { $threwScope = ($_.Exception.Message -eq 'VM_SCOPE') }
        Assert-Case $threwScope
    } finally {
        $script:o = $savedO
    }
}

# Call the real .NET file API directly; the mock registry provider remains scoped
# to this test process, not the user's PowerShell session or machine.
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$failed=@($rows | Where-Object {-not $_.passed}).Count
@{type='production function AST with in-memory registry and Hyper-V fakes';passed=$rows.Count-$failed;failed=$failed;tests=@($rows)} | ConvertTo-Json -Depth 6 | ForEach-Object {[IO.File]::WriteAllText((Join-Path $OutputDirectory 'hyperv-policy-tests.json'),$_,[Text.UTF8Encoding]::new($false))}
Write-Output ('Hyper-V policy fixtures passed='+($rows.Count-$failed)+' failed='+$failed)
if($failed){$rows | Where-Object {-not $_.passed} | ConvertTo-Json -Depth 5;exit 1}
