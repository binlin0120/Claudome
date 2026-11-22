#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\HyperVWorker.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'WORKER_PARSE_FAILED'}
foreach($name in @('Assert-LocalPath','Save-WindowsCheckpoint','Assert-InstallDiskPath','Assert-InstallDrives','Get-WindowsCheckpoint','Confirm-WindowsDisk','Complete-WindowsDisk','Install-WindowsDisk','Send-InstallProgress','Resolve-WindowsInstallDisk','Clear-FailedWindowsDisks','Convert-DismPercent')) {
    $definition=$ast.Find({param($a)$a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $name},$true)
    if(-not $definition){throw ('MISSING_PRODUCTION_FUNCTION '+$name)}
    . ([scriptblock]::Create($definition.Extent.Text))
}
# Every storage/hypervisor call below is fake. Files are small text fixtures
# below OutputDirectory, never disk images mounted by Windows.
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$fixtures=Join-Path $OutputDirectory ('recovery-fixtures-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtures) | Out-Null
$script:rows=[Collections.Generic.List[object]]::new()
function Reset-Fixture {
    $path=Join-Path $fixtures ([guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($path) | Out-Null
    $script:vm=[pscustomobject]@{Id=[guid]::NewGuid();Path=$path;State='Off'}
    $script:o=[pscustomobject]@{Root=$fixtures;WorkspaceId=[guid]::NewGuid().ToString('N');IsoSha256=('a'*64);WindowsImageIndex=6;UserName='FixtureUser';DiskGb=80}
    $script:drives=@();$script:disks=@{};$script:applies=0;$script:starts=0;$script:adds=0;$script:fail='';$script:extraVm=$null;$script:externalDisk=''
}
function Target-VM($Data){if($Data.vmId -ne $script:vm.Id){throw 'VM_SCOPE'};return $script:vm}
function Get-VM {if($script:extraVm){return @($script:vm,$script:extraVm)};return $script:vm}
function Get-VMSnapshot($VM){return @()}
function Get-VMHardDiskDrive($VM){if($script:extraVm -and $VM.Id -eq $script:extraVm.Id){return [pscustomobject]@{Path=$script:externalDisk}};return $script:drives}
function Get-VHD([string]$Path){if(-not $script:disks.ContainsKey($Path)){throw 'FAKE_DISK_MISSING'};return $script:disks[$Path]}
function Add-VMHardDiskDrive($VM,[string]$Path){$script:adds++;$script:drives=@([pscustomobject]@{Path=$Path});if($script:fail -eq 'after-attach'){$script:fail='';throw 'INJECTED_AFTER_ATTACH'}}
function Set-VMFirmware($VM,$FirstBootDevice){if($FirstBootDevice.Path -ne $script:drives[0].Path){throw 'WRONG_BOOT_DISK'}}
function Start-VM($VM){$script:starts++;if($script:fail -eq 'before-start'){$script:fail='';throw 'INJECTED_START_FAILURE'}}
function Fake-Disk([string]$Path){
    [IO.File]::WriteAllText($Path,'fictional image generation '+$script:applies)
    $script:disks[$Path]=[pscustomobject]@{Path=$Path;DiskIdentifier=[guid]::NewGuid();Size=80GB;VhdFormat='VHDX';VhdType='Dynamic';ParentPath='';Attached=$false}
}
function Apply-WindowsImageToNewDisk($Vm,$Checkpoint,$Data){
    $script:applies++;$path=[string]$Checkpoint.diskPath
    if(Test-Path -LiteralPath $path){throw 'WOULD_OVERWRITE_DISK'}
    Fake-Disk $path
    if($script:fail -eq 'after-create'){$script:fail='';throw 'INJECTED_AFTER_CREATE'}
    $Checkpoint.diskId=[string]$script:disks[$path].DiskIdentifier;$Checkpoint.phase='applying';Save-WindowsCheckpoint (Join-Path $Vm.Path '.windows-install.json') $Checkpoint
    if($script:fail -eq 'during-image'){$script:fail='';throw 'INJECTED_IMAGE_FAILURE'}
}
function Assert($Condition,[string]$Message='ASSERT_FAILED'){if(-not $Condition){throw $Message}}
function Reject([scriptblock]$Action){$blocked=$false;try{& $Action}catch{$blocked=$true};Assert $blocked 'expected rejection'}
function Install {Install-WindowsDisk @{vmId=[string]$script:vm.Id;password='fictional-only'}}
function Check([string]$Name,[scriptblock]$Test){try{Reset-Fixture;& $Test;$script:rows.Add(@{name=$Name;passed=$true})}catch{$script:rows.Add(@{name=$Name;passed=$false;detail=$_.Exception.Message})}}
function Get-Disk {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)]$MountedVhd)
    process { $script:mountDiskResults }
}
function New-InstallDiskFixture([uint16]$Bus=15,[string]$Partition='RAW',[bool]$Boot=$false,[bool]$System=$false,[switch]$MissingBus) {
    $disk=[Microsoft.Management.Infrastructure.CimInstance]::new('MSFT_Disk','root/microsoft/windows/storage')
    if(-not $MissingBus){
        $property=[Microsoft.Management.Infrastructure.CimProperty]::Create('BusType',$Bus,[Microsoft.Management.Infrastructure.CimType]::UInt16,[Microsoft.Management.Infrastructure.CimFlags]::None)
        $disk.CimInstanceProperties.Add($property)
    }
    $disk | Add-Member -NotePropertyName BusType -NotePropertyValue 'File Backed Virtual' -Force
    $disk | Add-Member -NotePropertyMembers @{PartitionStyle=$Partition;IsBoot=$Boot;IsSystem=$System}
    return $disk
}
Check 'hyperv-vhd-guard-uses-raw-cim-bus-value-and-rejects-other-disks' {
    $approved=New-InstallDiskFixture
    $script:mountDiskResults=@($approved)
    Assert ($approved.BusType -eq 'File Backed Virtual') 'fixture did not emulate the display property'
    $resolved=Resolve-WindowsInstallDisk ([pscustomobject]@{Attached=$true})
    Assert ([object]::ReferenceEquals($resolved,$approved)) 'raw CIM-backed VHD was denied'
    foreach($candidate in @((New-InstallDiskFixture -Bus 17),(New-InstallDiskFixture -Partition 'GPT'),
        (New-InstallDiskFixture -Boot $true),(New-InstallDiskFixture -System $true),(New-InstallDiskFixture -MissingBus))){
        $script:mountDiskResults=@($candidate)
        Reject {Resolve-WindowsInstallDisk ([pscustomobject]@{Attached=$true})}
    }
    $script:mountDiskResults=@($approved,(New-InstallDiskFixture))
    Reject {Resolve-WindowsInstallDisk ([pscustomobject]@{Attached=$true})}
    $script:mountDiskResults=@()
    Reject {Resolve-WindowsInstallDisk ([pscustomobject]@{Attached=$true})}
}
foreach($point in @('after-create','during-image')) {
    Check ('hyperv-resume-'+$point+'-preserves-original') {
        $script:fail=$point;Reject {Install};$old=Join-Path $script:vm.Path 'Windows11.vhdx';$bytes=[IO.File]::ReadAllBytes($old)
        Install;$c=Get-Content -LiteralPath (Join-Path $script:vm.Path '.windows-install.json') -Raw | ConvertFrom-Json
        Assert ($script:applies -eq 2 -and $script:adds -eq 1 -and $script:starts -eq 1 -and $c.phase -eq 'ready' -and $old -in $c.preservedDisks -and $c.diskPath -ne $old) 'recovery did not complete using a new generation'
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($old)) -eq [Convert]::ToBase64String($bytes)) 'old disk bytes changed'
    }
}
foreach($point in @('after-attach','before-start')) {
    Check ('hyperv-resume-'+$point+'-does-not-reapply-image') {
        $script:fail=$point;Reject {Install};Install
        Assert ($script:applies -eq 1 -and $script:adds -eq 1 -and $script:starts -ge 1) 'duplicate disk/image operation after completed image'
    }
}
Check 'hyperv-diagnostics-track-failed-step-on-preserved-disk' {
    $script:fail='before-start'
    Reject {Install}
    Assert ($script:windowsInstallStage -eq 'start-vm') 'start error lost its stage'
    $journal=Join-Path $script:vm.Path '.windows-install.json'
    $checkpoint=Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
    Assert ($checkpoint.phase -eq 'ready' -and $script:applies -eq 1) 'start failure lost the installed image'
}
Check 'hyperv-ready-checkpoint-starts-idempotently' {Install;Install;Assert ($script:applies -eq 1 -and $script:adds -eq 1 -and $script:starts -eq 2)}
Check 'hyperv-legacy-incomplete-unattached-disk-is-preserved' {
    $old=Join-Path $script:vm.Path 'Windows11.vhdx';Fake-Disk $old;$before=[IO.File]::ReadAllText($old);Install
    Assert ([IO.File]::ReadAllText($old) -eq $before -and $script:drives[0].Path -ne $old)
}
Check 'hyperv-legacy-complete-marker-is-scoped-and-resumed' {
    $disk=Join-Path $script:vm.Path 'Windows11.vhdx';Fake-Disk $disk;'applied' | Set-Content -LiteralPath (Join-Path $script:vm.Path '.windows-applied')
    Install;Assert ($script:applies -eq 0 -and $script:adds -eq 1 -and $script:starts -eq 1)
}
foreach($mutation in @('owner','media','path','disk-id','mounted','extra-drive','disk-size','missing-disk')) {
    Check ('hyperv-recovery-rejects-'+$mutation) {
        Install;$path=Join-Path $script:vm.Path '.windows-install.json';$c=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
        switch($mutation){
            'owner' {$c.workspaceId='other-workspace'}
            'media' {$c.isoSha256='b'*64}
            'path' {$c.diskPath=Join-Path $fixtures 'other-project.vhdx'}
            'disk-id' {$c.diskId=[string][guid]::NewGuid()}
            'mounted' {$script:disks[$c.diskPath].Attached=$true}
            'disk-size' {$script:disks[$c.diskPath].Size=160GB}
            'missing-disk' {$script:disks.Remove($c.diskPath)}
            'extra-drive' {$script:drives+=@([pscustomobject]@{Path=(Join-Path $fixtures 'other.vhdx')})}
        }
        $c | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path
        Reject {Install};Assert ($script:applies -eq 1 -and $script:starts -eq 1) 'unsafe state reached storage or start'
    }
}
Check 'hyperv-incomplete-attached-missing-disk-is-not-recreated' {
    $disk=Join-Path $script:vm.Path 'Windows11.vhdx';$script:drives=@([pscustomobject]@{Path=$disk});Reject {Install};Assert ($script:applies -eq 0 -and $script:starts -eq 0)
}
Check 'hyperv-running-machine-cannot-rebuild-disk' {$script:vm.State='Running';Reject {Install};Assert ($script:applies -eq 0 -and $script:starts -eq 0)}
Check 'hyperv-cleanup-deletes-replaced-disk-after-new-system-ready' {
    $script:fail='during-image';Reject {Install};$old=Join-Path $script:vm.Path 'Windows11.vhdx';Install
    $active=$script:drives[0].Path;$before=[IO.File]::ReadAllText($active)
    $result=Clear-FailedWindowsDisks $script:vm
    Assert ($result.deleted -eq 1 -and $result.remaining -eq 0 -and -not(Test-Path -LiteralPath $old) -and [IO.File]::ReadAllText($active) -eq $before)
    $again=Clear-FailedWindowsDisks $script:vm;Assert ($again.deleted -eq 0 -and $again.remaining -eq 0)
}
Check 'hyperv-cleanup-before-ready-keeps-original-disk' {
    $script:fail='during-image';Reject {Install};$old=Join-Path $script:vm.Path 'Windows11.vhdx'
    Reject {Clear-FailedWindowsDisks $script:vm};Assert (Test-Path -LiteralPath $old)
}
Check 'hyperv-cleanup-locked-old-disk-remains-pending' {
    $script:fail='during-image';Reject {Install};$old=Join-Path $script:vm.Path 'Windows11.vhdx';Install
    $held=[IO.File]::Open($old,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{$result=Clear-FailedWindowsDisks $script:vm;Assert ($result.deleted -eq 0 -and $result.remaining -eq 1 -and (Test-Path -LiteralPath $old))}finally{$held.Dispose()}
}
foreach($target in @('active','outside')) {
    Check ('hyperv-cleanup-rejects-'+$target+'-before-any-delete') {
        $script:fail='during-image';Reject {Install};$old=Join-Path $script:vm.Path 'Windows11.vhdx';Install
        $path=Join-Path $script:vm.Path '.windows-install.json';$c=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
        if($target -eq 'active'){$c.preservedDisks+=@($c.diskPath)}else{$c.preservedDisks+=@(Join-Path $fixtures 'other\Windows11.vhdx')}
        $c | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path
        Reject {Clear-FailedWindowsDisks $script:vm};Assert (Test-Path -LiteralPath $old)
    }
}
Check 'hyperv-cleanup-protects-parent-disk-used-by-other-vm' {
    $script:fail='during-image';Reject {Install};$old=Join-Path $script:vm.Path 'Windows11.vhdx';Install
    $script:extraVm=[pscustomobject]@{Id=[guid]::NewGuid()};$script:externalDisk=Join-Path $fixtures 'another-vm-child.vhdx';Fake-Disk $script:externalDisk
    $script:disks[$script:externalDisk].ParentPath=$old
    Reject {Clear-FailedWindowsDisks $script:vm};Assert (Test-Path -LiteralPath $old)
}
Check 'dism-real-percentage-parser' {
    foreach($number in @(0,42.5,100)){$text='[======== '+$number.ToString([Globalization.CultureInfo]::InvariantCulture)+'% ========]';Assert ((Convert-DismPercent $text) -eq $number)}
}
Check 'dism-unavailable-or-invalid-progress-is-not-invented' {
    Assert ($null -eq (Convert-DismPercent 'Preparing image; no percentage available'))
    Assert ($null -eq (Convert-DismPercent '999%'))
}

$failed=@($script:rows | Where-Object {-not $_.passed}).Count
@{type='production orchestration with fake storage and VM commands; no VHD mount or real VM';passed=$script:rows.Count-$failed;failed=$failed;tests=@($script:rows)} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'hyperv-recovery-tests.json') -Encoding utf8NoBOM
Write-Output ('Hyper-V recovery passed='+($script:rows.Count-$failed)+' failed='+$failed)
if($failed){exit 1}
