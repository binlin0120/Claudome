#requires -Version 7.0
param([Parameter(Mandatory)][string]$PipeName)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
if($PipeName -notmatch '^ClaudeSetup-[a-f0-9]{32}$'){exit 1}
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$PipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::None,[Security.Principal.TokenImpersonationLevel]::Identification)
$pipe.Connect(180000)
$reader=[IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8,$false,4096,$true)
$writer=[IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true);$writer.AutoFlush=$true
$o=$null;$verificationOnly=$false
function Assert-LocalPath([string]$Path,[string]$Parent) {
    $full=[IO.Path]::GetFullPath($Path);$base=[IO.Path]::GetFullPath($Parent).TrimEnd('\')+'\'
    if(-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)){throw 'VM_SCOPE'}
    $part=$full
    while($part){if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'VM_SCOPE'};$part=[IO.Path]::GetDirectoryName($part)}
    return $full
}
function Target-VM($Data) {
    if(-not $Data.vmId){throw 'VM_SCOPE'}
    $vm=Get-VM -Id ([guid]$Data.vmId)
    $expected=Join-Path $o.Root ('machines\'+$o.VmName)
    $actual=[IO.Path]::GetFullPath($vm.Path).TrimEnd('\');$base=[IO.Path]::GetFullPath($expected).TrimEnd('\')
    if($vm.Name -ne $o.VmName -or ($actual -ne $base -and -not $actual.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase))){throw 'VM_SCOPE'}
    $null=Assert-LocalPath (Join-Path $actual 'scope-check') $expected
    return $vm
}
function Test-VMKeyProtectorPresent($Vm) {
    $protector=Get-VMKeyProtector -VM $Vm
    # A cmdlet can emit one object containing a zero-length byte array.
    # Counting pipeline records would mistake that empty array for a key.
    return ($null -ne $protector -and ($protector -isnot [System.Collections.ICollection] -or $protector.Count -gt 0))
}
function Enable-TaskVirtualTpm($Vm) {
    if((Get-VMSecurity -VM $Vm).TpmEnabled){return}
    try{$present=Test-VMKeyProtectorPresent $Vm}catch{throw 'VM_KEY_PROTECTOR_READ_FAILED'}
    if(-not $present){
        try{
            Set-VMKeyProtector -VM $Vm -NewLocalKeyProtector
            if(-not(Test-VMKeyProtectorPresent $Vm)){throw 'VM_KEY_PROTECTOR_SETUP_FAILED'}
        }catch{throw 'VM_KEY_PROTECTOR_SETUP_FAILED'}
    }
    try{Enable-VMTPM -VM $Vm}catch{throw 'VM_TPM_ENABLE_FAILED'}
}
function Guest-Session($Data,[switch]$Default) {
    $vm=Target-VM $Data
    $cred=[pscredential]::new([string]$o.UserName,(ConvertTo-SecureString -String ([string]$Data.password) -AsPlainText -Force))
    if ($Default) { return New-PSSession -VMId $vm.Id -Credential $cred -ErrorAction Stop }
    New-PSSession -VMId $vm.Id -Credential $cred -ConfigurationName 'PowerShell.7' -ErrorAction Stop
}
function Disable-HostMetadata($Vm) {
    $service=@(Get-VMIntegrationService -VM $Vm | Where-Object {$_.Id.ToString() -match '2a34b1c2-fd73-4043-8a5b-dd2159bc743f' -or $_.Name -match 'Key-Value|Data Exchange|数据交换|键值'})
    if($service.Count -ne 1){throw 'BOUNDARY_METADATA'}
    $service | Disable-VMIntegrationService
    if(@(Get-VMIntegrationService -VM $Vm | Where-Object {$_.Id -eq $service[0].Id -and $_.Enabled}).Count){throw 'BOUNDARY_METADATA'}
}
function Console-Owner($Vm){return 'HKLM:\SOFTWARE\Claudome\HyperVConsolePolicy\Owners\'+[string]$Vm.Id}
function Invoke-ConsolePolicyLock([scriptblock]$Action){
    $mutex=[Threading.Mutex]::new($false,'Global\Claudome-HyperVConsolePolicy');$held=$false
    try{
        try{$held=$mutex.WaitOne([TimeSpan]::FromSeconds(15))}catch [Threading.AbandonedMutexException]{$held=$true}
        if(-not $held){throw 'HYPERV_CONSOLE_POLICY'}
        & $Action
    }finally{if($held){$mutex.ReleaseMutex()};$mutex.Dispose()}
}
function Guard-Console($Vm){
    Invoke-ConsolePolicyLock {
    $root='HKLM:\SOFTWARE\Claudome\HyperVConsolePolicy';$current=[bool](Get-VMHost).EnableEnhancedSessionMode
    $original=$current;if(Test-Path -LiteralPath $root){$original=[bool](Get-ItemPropertyValue -LiteralPath $root -Name OriginalEnabled)}
    if($original -and -not $o.HyperVBasicConsoleConfirmed){throw 'HYPERV_CONSOLE_CONSENT'}
    if($current -and $Vm.State -ne 'Off'){throw 'HYPERV_CONSOLE_SHUTDOWN'}
    if(-not(Test-Path -LiteralPath $root)){New-Item -Path $root -Force | Out-Null;New-ItemProperty -LiteralPath $root -Name OriginalEnabled -PropertyType DWord -Value ([int]$current) | Out-Null}
    $owner=Console-Owner $Vm
    if(Test-Path -LiteralPath $owner){if((Get-ItemPropertyValue -LiteralPath $owner -Name WorkspaceId) -ne $o.WorkspaceId){throw 'VM_SCOPE'}}
    else{New-Item -Path $owner -Force | Out-Null;New-ItemProperty -LiteralPath $owner -Name WorkspaceId -Value ([string]$o.WorkspaceId) -PropertyType String | Out-Null}
    if($current){Set-VMHost -EnableEnhancedSessionMode $false}
    if((Get-VMHost).EnableEnhancedSessionMode){throw 'HYPERV_CONSOLE_POLICY'}
    }
}
function Verify-Console($Vm){
    $owner=Console-Owner $Vm
    if((Get-VMHost).EnableEnhancedSessionMode -or -not(Test-Path -LiteralPath $owner) -or (Get-ItemPropertyValue -LiteralPath $owner -Name WorkspaceId) -ne $o.WorkspaceId){throw 'HYPERV_CONSOLE_POLICY'}
}
function Restore-Console($Vm){
    Invoke-ConsolePolicyLock {
    if($Vm.State -ne 'Off'){throw 'HYPERV_CONSOLE_SHUTDOWN'}
    $root='HKLM:\SOFTWARE\Claudome\HyperVConsolePolicy';$owner=Console-Owner $Vm
    if(-not(Test-Path -LiteralPath $root)){return 'nothing-to-restore'}
    if(Test-Path -LiteralPath $owner){if((Get-ItemPropertyValue -LiteralPath $owner -Name WorkspaceId) -ne $o.WorkspaceId){throw 'VM_SCOPE'};Remove-Item -LiteralPath $owner}
    $others=@(Get-ChildItem -LiteralPath (Join-Path $root 'Owners') -ErrorAction SilentlyContinue)
    if($others.Count){return 'kept-for-other-environments'}
    $original=[bool](Get-ItemPropertyValue -LiteralPath $root -Name OriginalEnabled);Set-VMHost -EnableEnhancedSessionMode $original
    if([bool](Get-VMHost).EnableEnhancedSessionMode -ne $original){throw 'HYPERV_CONSOLE_POLICY'}
    if(Test-Path -LiteralPath (Join-Path $root 'Owners')){Remove-Item -LiteralPath (Join-Path $root 'Owners')}
    Remove-Item -LiteralPath $root
    return 'restored'
    }
}
function Assert-NoNetworkAdapter($Vm) {
    if(@(Get-VMNetworkAdapter -VM $Vm).Count -ne 0){throw 'BOUNDARY_NICS'}
}
function Bus-ServicePath($Data) {
    if($Data.workspaceId -ne $o.WorkspaceId -or ([string]$Data.serviceId) -notmatch '^[a-fA-F0-9-]{36}$'){throw 'VM_SCOPE'}
    return 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\'+([guid]$Data.serviceId).ToString()
}
function Verify-BusService($Data,$Vm) {
    Assert-NoNetworkAdapter $Vm
    Verify-Console $Vm
    $path=Bus-ServicePath $Data;$expected='Claudome '+[string]$Vm.Id+' '+[string]$o.WorkspaceId
    if(-not(Test-Path -LiteralPath $path) -or (Get-ItemPropertyValue -LiteralPath $path -Name ElementName) -ne $expected){throw 'BOUNDARY_SERVICE'}
    $metadata=@(Get-VMIntegrationService -VM $Vm | Where-Object {$_.Id.ToString() -match '2a34b1c2-fd73-4043-8a5b-dd2159bc743f' -or $_.Name -match 'Key-Value|Data Exchange|数据交换|键值'})
    if($metadata.Count -ne 1 -or $metadata[0].Enabled){throw 'BOUNDARY_METADATA'}
}
function Send-InstallProgress([string]$Step,$Percent=$null) {
    if($Step -notin @('recovery','disk','apply-image','boot','cleanup')){throw 'HYPERV_STEP_FAILED'}
    if($null -ne $Percent -and ($Percent -lt 0 -or $Percent -gt 100)){throw 'HYPERV_STEP_FAILED'}
    if($writer){$writer.WriteLine((@{event='install-progress';step=$Step;percent=$Percent} | ConvertTo-Json -Compress))}
}
function Convert-DismPercent([string]$Line) {
    foreach($m in [regex]::Matches($Line,'(?<![0-9])([0-9]{1,3}(?:\.[0-9]+)?)%')) {
        $number=[double]::Parse($m.Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture)
        if($number -ge 0 -and $number -le 100){return $number}
    }
    return $null
}
function Apply-WindowsImageWithProgress([string]$Source,[int]$Index,[string]$Target) {
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $env:WINDIR 'System32\dism.exe'))
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($arg in @('/English','/Apply-Image',('/ImageFile:'+$Source),('/Index:'+$Index),('/ApplyDir:'+$Target),'/CheckIntegrity','/Verify')){$start.ArgumentList.Add($arg)}
    $process=[Diagnostics.Process]::Start($start)
    if(-not $process){throw 'WINDOWS_IMAGE_APPLY_FAILED'}
    $errorTask=$process.StandardError.ReadToEndAsync();$line=[Text.StringBuilder]::new();$last=-1
    try {
        Send-InstallProgress 'apply-image' 0
        while(($value=$process.StandardOutput.Read()) -ne -1) {
            if($value -in @(10,13)) {
                $percent=Convert-DismPercent $line.ToString();$null=$line.Clear()
                if($null -ne $percent -and [int]$percent -ne $last){$last=[int]$percent;Send-InstallProgress 'apply-image' $percent}
            }elseif($line.Length -lt 4096){$null=$line.Append([char]$value)}
        }
        $process.WaitForExit();$null=$errorTask.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0){
            $script:windowsNativeCode=$process.ExitCode
            throw 'WINDOWS_IMAGE_APPLY_FAILED'
        }
        Send-InstallProgress 'apply-image' 100
    }finally{
        # If the UI pipe closes, keep draining until DISM exits before the VHD
        # is unmounted. A pause never leaves a detached installer writing a disk.
        $drain=$process.StandardOutput.ReadToEndAsync();$process.WaitForExit()
        $null=$drain.GetAwaiter().GetResult();$null=$errorTask.GetAwaiter().GetResult();$process.Dispose()
    }
}
function Clear-FailedWindowsDisks($Vm) {
    $journal=Assert-LocalPath (Join-Path $Vm.Path '.windows-install.json') $Vm.Path
    if(-not(Test-Path -LiteralPath $journal)){return @{deleted=0;remaining=0}}
    $c=Get-WindowsCheckpoint $Vm -AllowAttached
    if($c.phase -ne 'ready'){throw 'DISK_SCOPE'}
    $active=Assert-InstallDiskPath ([string]$c.diskPath) $Vm
    $references=@()
    foreach($other in @(Get-VM)) {
        $references+=@(Get-VMHardDiskDrive -VM $other | ForEach-Object {[IO.Path]::GetFullPath($_.Path)})
        foreach($snapshot in @(Get-VMSnapshot -VM $other)){$references+=@(Get-VMHardDiskDrive -VMSnapshot $snapshot | ForEach-Object {[IO.Path]::GetFullPath($_.Path)})}
    }
    $allReferences=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($reference in $references) {
        $walk=$reference;$depth=0
        while($walk) {
            $full=[IO.Path]::GetFullPath($walk)
            if(-not $allReferences.Add($full)){break}
            if(++$depth -gt 128){throw 'DISK_SCOPE'}
            $diskInfo=Get-VHD -Path $full;$walk=[string]$diskInfo.ParentPath
        }
    }
    $references=@($allReferences)
    # Check every target first. Deletion is limited to checkpoint-listed files
    # directly within this task's VM directory; no recursive directory cleanup.
    $plan=@()
    foreach($old in @($c.preservedDisks | Sort-Object -Unique)) {
        $path=Assert-InstallDiskPath ([string]$old) $Vm
        if([IO.Path]::GetDirectoryName($path) -ne [IO.Path]::GetFullPath($Vm.Path).TrimEnd('\') -or $path -eq $active -or $path -in $references){throw 'DISK_SCOPE'}
        if(Test-Path -LiteralPath $path){$info=Get-Item -LiteralPath $path;$plan+=@(@{path=$path;length=$info.Length;time=$info.LastWriteTimeUtc})}
    }
    $deleted=0;Send-InstallProgress 'cleanup'
    foreach($item in $plan) {
        try {
            $null=Assert-InstallDiskPath $item.path $Vm;$info=Get-Item -LiteralPath $item.path
            if($info.Length -ne $item.length -or $info.LastWriteTimeUtc -ne $item.time){continue}
            # An in-use/mounted VHD cannot be deleted; no Force or dismount here.
            Remove-Item -LiteralPath $item.path -ErrorAction Stop;$deleted++
            $c.preservedDisks=@($c.preservedDisks | Where-Object {$_ -ne $item.path})
            Save-WindowsCheckpoint $journal $c
        }catch [IO.IOException] {}catch [UnauthorizedAccessException] {}
    }
    $c.preservedDisks=@($c.preservedDisks | Where-Object {Test-Path -LiteralPath $_})
    Save-WindowsCheckpoint $journal $c
    return @{deleted=$deleted;remaining=@($c.preservedDisks).Count}
}

function Save-WindowsCheckpoint([string]$Path,$Checkpoint) {
    $null=Assert-LocalPath $Path $o.Root
    $temporary=Assert-LocalPath ($Path+'.new') $o.Root
    $Checkpoint | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
function Assert-InstallDiskPath([string]$Path,$Vm) {
    $full=Assert-LocalPath $Path $Vm.Path
    if([IO.Path]::GetFileName($full) -notmatch '^Windows11(?:-retry-[a-f0-9]{32})?\.vhdx$'){throw 'DISK_SCOPE'}
    return $full
}
function Assert-InstallDrives($Vm,[string]$Path) {
    $drives=@(Get-VMHardDiskDrive -VM $Vm)
    if($drives.Count -gt 1 -or @($drives | Where-Object {[IO.Path]::GetFullPath($_.Path) -ne $Path}).Count){throw 'DISK_SCOPE'}
    return $drives
}
function Get-WindowsCheckpoint($Vm,[switch]$AllowAttached) {
    $path=Assert-LocalPath (Join-Path $Vm.Path '.windows-install.json') $Vm.Path
    $disk=Assert-InstallDiskPath (Join-Path $Vm.Path 'Windows11.vhdx') $Vm
    if(Test-Path -LiteralPath $path) {
        if((Get-Item -LiteralPath $path).Length -gt 65536){throw 'DISK_SCOPE'}
        $c=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
        if($c.schema -ne 1 -or $c.vmId -ne [string]$Vm.Id -or $c.workspaceId -ne $o.WorkspaceId -or $c.isoSha256 -ne $o.IsoSha256 -or $c.imageIndex -ne $o.WindowsImageIndex -or $c.userName -ne $o.UserName -or $c.diskGb -ne $o.DiskGb -or $c.phase -notin @('planned','applying','image-ready','ready') -or @($c.preservedDisks).Count -gt 128){throw 'DISK_SCOPE'}
        $disk=Assert-InstallDiskPath ([string]$c.diskPath) $Vm
        foreach($old in @($c.preservedDisks)){$null=Assert-InstallDiskPath ([string]$old) $Vm}
    } else {
        # Import only the old installer's completed, task-scoped marker. An
        # incomplete disk is never formatted again or inferred to be bootable.
        $ready=Assert-LocalPath (Join-Path $Vm.Path '.windows-applied') $Vm.Path
        $legacy=(Test-Path -LiteralPath $ready) -and (Get-Content -LiteralPath $ready -Raw).Trim() -eq 'applied'
        $c=@{schema=1;vmId=[string]$Vm.Id;workspaceId=[string]$o.WorkspaceId;isoSha256=[string]$o.IsoSha256;imageIndex=[int]$o.WindowsImageIndex;userName=[string]$o.UserName;diskGb=[int]$o.DiskGb;diskPath=$disk;diskId='';phase='planned';preservedDisks=@()}
        if($legacy){$vhd=Get-VHD -Path $disk;$c.diskId=[string]$vhd.DiskIdentifier;$c.phase='image-ready'}
    }
    if($AllowAttached -and $c.phase -ne 'ready'){throw 'DISK_SCOPE'}
    $drives=@(Assert-InstallDrives $Vm $disk)
    if($c.phase -notin @('image-ready','ready') -and $drives.Count){throw 'HYPERV_DISK_RECOVERY'}
    if($c.phase -in @('image-ready','ready')) {
        $null=Confirm-WindowsDisk $c $Vm -AllowAttached:$AllowAttached
    } elseif(Test-Path -LiteralPath $disk) {
        # A partially applied Windows image cannot be resumed with DISM safely.
        # Keep it byte-for-byte; a new generation uses a different filename.
        if($drives.Count -or @($c.preservedDisks).Count -ge 128){throw 'HYPERV_DISK_RECOVERY'}
        $c.preservedDisks=@($c.preservedDisks)+@($disk)
        $c.diskPath=Assert-InstallDiskPath (Join-Path $Vm.Path ('Windows11-retry-'+[guid]::NewGuid().ToString('N')+'.vhdx')) $Vm
        $c.diskId='';$c.phase='planned'
    } elseif($c.phase -ne 'planned') {throw 'HYPERV_DISK_RECOVERY'}
    Save-WindowsCheckpoint $path $c
    return $c
}
function Confirm-WindowsDisk($Checkpoint,$Vm,[switch]$AllowAttached) {
    $path=Assert-InstallDiskPath ([string]$Checkpoint.diskPath) $Vm
    $vhd=Get-VHD -Path $path
    if([IO.Path]::GetFullPath($vhd.Path) -ne $path -or [string]$vhd.DiskIdentifier -ne $Checkpoint.diskId -or -not $Checkpoint.diskId -or $vhd.Size -ne ([long]$o.DiskGb*1GB) -or $vhd.VhdFormat -ne 'VHDX' -or $vhd.VhdType -ne 'Dynamic' -or $vhd.ParentPath){throw 'DISK_SCOPE'}
    if($vhd.Attached -and -not $AllowAttached){throw 'HYPERV_DISK_MOUNTED'}
    return $vhd
}
function Complete-WindowsDisk($Vm,$Checkpoint) {
    $script:windowsInstallStage='attach-vhd'
    $path=Assert-InstallDiskPath ([string]$Checkpoint.diskPath) $Vm
    $null=Confirm-WindowsDisk $Checkpoint $Vm
    $drives=@(Assert-InstallDrives $Vm $path)
    if(-not $drives.Count){Add-VMHardDiskDrive -VM $Vm -Path $path}
    $boot=@(Assert-InstallDrives $Vm $path)
    if($boot.Count -ne 1){throw 'DISK_SCOPE'}
    Send-InstallProgress 'boot'
    $script:windowsInstallStage='boot-config'
    Set-VMFirmware -VM $Vm -FirstBootDevice $boot[0]
    $Checkpoint.phase='ready'
    Save-WindowsCheckpoint (Join-Path $Vm.Path '.windows-install.json') $Checkpoint
    # Retained for compatibility. The bound checkpoint, not this text flag,
    # determines whether the disk can be started on subsequent attempts.
    'applied' | Set-Content -LiteralPath (Assert-LocalPath (Join-Path $Vm.Path '.windows-applied') $Vm.Path) -Encoding utf8NoBOM
    $script:windowsInstallStage='start-vm'
    Start-VM -VM $Vm | Out-Null
}

function Install-WindowsDisk($Data) {
    $script:windowsInstallStage='checkpoint'
    $vm=Target-VM $Data
    if($vm.State -ne 'Off'){throw 'VM_SCOPE'}
    Send-InstallProgress 'recovery'
    $checkpoint=Get-WindowsCheckpoint $vm
    if($checkpoint.phase -in @('image-ready','ready')){Complete-WindowsDisk $vm $checkpoint;return}
    Send-InstallProgress 'disk'
    Apply-WindowsImageToNewDisk $vm $checkpoint $Data
    $script:windowsInstallStage='checkpoint'
    $checkpoint.phase='image-ready';Save-WindowsCheckpoint (Join-Path $vm.Path '.windows-install.json') $checkpoint
    Complete-WindowsDisk $vm $checkpoint
}
function Resolve-WindowsInstallDisk($MountedVhd) {
    $disks=@($MountedVhd | Get-Disk)
    if($disks.Count -ne 1 -or $disks[0] -isnot [Microsoft.Management.Infrastructure.CimInstance]){throw 'DISK_SCOPE'}
    $disk=$disks[0]
    # The Storage module may format BusType as "File Backed Virtual".
    # Use the provider's UInt16 value, without converting display text.
    $bus=$disk.CimInstanceProperties['BusType'].Value
    if($disk.CimSystemProperties.ClassName -ne 'MSFT_Disk' -or $bus -isnot [uint16] -or $bus -ne 15 -or
       $disk.PartitionStyle -ne 'RAW' -or $disk.IsBoot -or $disk.IsSystem){throw 'DISK_SCOPE'}
    return $disk
}
function Apply-WindowsImageToNewDisk($vm,$checkpoint,$Data) {
    $script:windowsInstallStage='new-vhd'
    $diskPath=Assert-InstallDiskPath ([string]$checkpoint.diskPath) $vm
    $iso=$null;$mounted=$null
    try {
        New-VHD -Path $diskPath -SizeBytes ([long]$o.DiskGb*1GB) -Dynamic | Out-Null
        $vhd=Get-VHD -Path $diskPath
        $checkpoint.diskId=[string]$vhd.DiskIdentifier
        $null=Confirm-WindowsDisk $checkpoint $vm
        $checkpoint.phase='applying';Save-WindowsCheckpoint (Join-Path $vm.Path '.windows-install.json') $checkpoint
        $script:windowsInstallStage='mount-vhd'
        $mounted=Mount-VHD -Path $diskPath -Passthru
        $disk=Resolve-WindowsInstallDisk $mounted
        $script:windowsInstallStage='partitions'
        $disk | Initialize-Disk -PartitionStyle GPT | Out-Null
        $efi=New-Partition -DiskNumber $disk.Number -Size 260MB -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' -AssignDriveLetter
        $efi | Format-Volume -FileSystem FAT32 -NewFileSystemLabel 'SYSTEM' -Confirm:$false | Out-Null
        New-Partition -DiskNumber $disk.Number -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' | Out-Null
        $windows=New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
        $windows | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'Windows' -Confirm:$false | Out-Null
        $target=([string]$windows.DriveLetter)+':\'
        $script:windowsInstallStage='mount-iso'
        $imageBefore=Get-DiskImage -ImagePath ([string]$o.IsoPath)
        if(-not $imageBefore.Attached){$iso=Mount-DiskImage -ImagePath ([string]$o.IsoPath) -PassThru}else{$iso=$imageBefore}
        $volume=$iso | Get-Volume
        $source=([string]$volume.DriveLetter)+':\sources\install.wim'
        if(-not(Test-Path -LiteralPath $source)){$source=([string]$volume.DriveLetter)+':\sources\install.esd'}
        if(-not(Test-Path -LiteralPath $source)){throw 'WINDOWS_MEDIA_UNSUPPORTED'}
        $script:windowsInstallStage='read-image'
        $images=@(Get-WindowsImage -ImagePath $source)
        $image=$images | Where-Object {$_.ImageIndex -eq [int]$o.WindowsImageIndex} | Select-Object -First 1
        if(-not $image){throw 'WINDOWS_MEDIA_UNSUPPORTED'}
        $script:windowsInstallStage='apply-image'
        Apply-WindowsImageWithProgress $source $image.ImageIndex $target
        $script:windowsInstallStage='bootstrap'
        $bootstrap=Join-Path $target 'ProgramData\ClaudeBootstrap';New-Item -ItemType Directory -Path $bootstrap -Force | Out-Null
        $psArchive=Join-Path $o.Root ('downloads\'+[string]$Data.pwshFile)
        Expand-Archive -LiteralPath $psArchive -DestinationPath (Join-Path $bootstrap 'pwsh') -Force
        @{vmId=[string]$vm.Id;workspaceId=[string]$o.WorkspaceId} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bootstrap 'identity.json') -Encoding utf8NoBOM
        Copy-Item -LiteralPath ([string]$Data.bootstrapScript) -Destination (Join-Path $bootstrap 'Initialize.ps1')
        $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(([string]$Data.password)+'Password'))
        $name=[Security.SecurityElement]::Escape([string]$o.UserName)
        $tz=if($o.NetworkSource -ne '稍后配置'){'<TimeZone>'+[Security.SecurityElement]::Escape([string]$o.TimeZoneId)+'</TimeZone>'}else{''}
        $xml=@"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
 <settings pass="specialize"><component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><ComputerName>ClaudeWorkspace</ComputerName>$tz</component></settings>
 <settings pass="oobeSystem"><component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
  <OOBE><HideOnlineAccountScreens>true</HideOnlineAccountScreens><HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE></OOBE>
  <UserAccounts><LocalAccounts><LocalAccount wcm:action="add"><Password><Value>$encoded</Value><PlainText>false</PlainText></Password><Name>$name</Name><Group>Administrators</Group></LocalAccount></LocalAccounts></UserAccounts>
  <AutoLogon><Password><Value>$encoded</Value><PlainText>false</PlainText></Password><Enabled>true</Enabled><LogonCount>1</LogonCount><Username>$name</Username></AutoLogon>
  <FirstLogonCommands><SynchronousCommand wcm:action="add"><Order>1</Order><Description>Prepare local VM management</Description><CommandLine>C:\ProgramData\ClaudeBootstrap\pwsh\pwsh.exe -NoLogo -NoProfile -File C:\ProgramData\ClaudeBootstrap\Initialize.ps1</CommandLine></SynchronousCommand></FirstLogonCommands>
 </component></settings>
</unattend>
"@
        $panther=Join-Path $target 'Windows\Panther';New-Item -ItemType Directory -Path $panther -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $panther 'unattend.xml'),$xml,[Text.UTF8Encoding]::new($false))
        $script:windowsInstallStage='bcdboot'
        & (Join-Path $env:WINDIR 'System32\bcdboot.exe') (Join-Path $target 'Windows') /s (([string]$efi.DriveLetter)+':') /f UEFI *> $null
        if($LASTEXITCODE -ne 0){
            $script:windowsNativeCode=$LASTEXITCODE
            throw 'WINDOWS_BOOT_FILES_FAILED'
        }
    } finally {
        if($mounted){Dismount-VHD -Path $diskPath -ErrorAction SilentlyContinue}
        if($iso -and -not $imageBefore.Attached){Dismount-DiskImage -ImagePath ([string]$o.IsoPath) -ErrorAction SilentlyContinue | Out-Null}
    }

}
try {
    while($null -ne ($line=$reader.ReadLine())) {
        $script:windowsInstallStage=''
        $script:windowsNativeCode=$null
        $response=@{ok=$false;value='';code='HYPERV_STEP_FAILED'}
        try {
            $request=$line | ConvertFrom-Json;$d=$request.data
            if($request.operation -eq 'exit'){break}
            if($request.operation -eq 'init') {
                $o=$d.options
                $verificationOnly=[bool]$d.verificationOnly
                if(-not $o -or $o.PlatformId -ne 'hyperv' -or $o.VmName -notmatch '^[a-zA-Z0-9_-]{1,50}$' -or -not [IO.Path]::IsPathFullyQualified($o.Root) -or $o.Root.StartsWith('\\') -or ([IO.Path]::GetPathRoot($o.Root).TrimEnd('\') -eq $o.Root.TrimEnd('\'))){throw 'VM_SCOPE'}
                $null=Assert-LocalPath (Join-Path $o.Root 'machines') $o.Root
                $value='initialized'
            } else {
                if(-not $o){throw 'VM_SCOPE'}
                if($verificationOnly -and $request.operation -ne 'boundary-verify'){throw 'UNSUPPORTED_OPERATION'}
                if($request.operation -eq 'prepare') {
                    $edition=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
                    if($edition -notmatch '^(Professional|Enterprise|Education)'){throw 'HYPERV_EDITION'}
                    $feature=Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All
                    if($feature.State -ne 'Enabled') {Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All -NoRestart | Out-Null;throw 'HYPERV_RESTART'}
                    Import-Module Hyper-V -ErrorAction Stop
                    if((Get-Service vmms).Status -ne 'Running'){throw 'HYPERV_NOT_READY'}
                    $value='ready'
                } else {
                    Import-Module Hyper-V -ErrorAction Stop
                    switch($request.operation) {
                        'ensure' {
                            if($d.vmId){$value=[string](Target-VM $d).Id;break}
                            $path=Assert-LocalPath (Join-Path $o.Root ('machines\'+$o.VmName)) $o.Root
                            if((Get-VM -Name $o.VmName -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $path)){throw 'VM_COLLISION'}
                            $vm=New-VM -Name $o.VmName -Path $path -Generation 2 -MemoryStartupBytes ([long]$o.MemoryMb*1MB) -NoVHD
                            Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter
                            $value=[string]$vm.Id
                        }
                        'configure' {
                            $vm=Target-VM $d;if($vm.State -ne 'Off'){throw 'VM_SCOPE'}
                            Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter
                            Guard-Console $vm
                            Disable-HostMetadata $vm
                            Set-VMProcessor -VM $vm -Count ([int]$o.CpuCount)
                            Set-VMMemory -VM $vm -DynamicMemoryEnabled $false -StartupBytes ([long]$o.MemoryMb*1MB)
                            Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
                            Enable-TaskVirtualTpm $vm
                            if(-not (Get-VMSecurity -VM $vm).TpmEnabled -or [string](Get-VMFirmware -VM $vm).SecureBoot -ne 'On'){throw 'VM_SECURITY'}
                            Set-VM -VM $vm -AutomaticStartAction Nothing -AutomaticStopAction ShutDown
                            $value='configured'
                        }
                        'launch-verifier' {
                            if(([string]$d.pipeName) -notmatch '^ClaudeSetup-[a-f0-9]{32}$'){throw 'VM_SCOPE'}
                            $scriptFile=Assert-LocalPath ([string]$d.scriptPath) (Join-Path $o.Root 'private')
                            if((Get-FileHash -LiteralPath $scriptFile -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash){throw 'VM_SCOPE'}
                            $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'));$start.UseShellExecute=$false;$start.CreateNoWindow=$true
                            foreach($a in @('-NoLogo','-NoProfile','-File',$scriptFile,'-PipeName',[string]$d.pipeName)){$start.ArgumentList.Add($a)}
                            $process=[Diagnostics.Process]::Start($start);$value=[string]$process.Id
                        }
                        'boundary-prepare' {
                            $vm=Target-VM $d
                            if(@(Get-VMNetworkAdapter -VM $vm).Count){if($vm.State -ne 'Off'){throw 'BOUNDARY_NICS'};Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter}
                            Guard-Console $vm
                            Disable-HostMetadata $vm
                            $path=Bus-ServicePath $d;$expected='Claudome '+[string]$vm.Id+' '+[string]$o.WorkspaceId
                            if(Test-Path -LiteralPath $path){if((Get-ItemPropertyValue -LiteralPath $path -Name ElementName) -ne $expected){throw 'BOUNDARY_SERVICE'}}
                            else{New-Item -Path $path -Force | Out-Null;New-ItemProperty -LiteralPath $path -Name ElementName -Value $expected -PropertyType String | Out-Null}
                            Verify-BusService $d $vm;$value='verified'
                        }
                        'boundary-verify' {$vm=Target-VM $d;Verify-BusService $d $vm;$value='verified'}
                        'boundary-offline' {$vm=Target-VM $d;Guard-Console $vm;Disable-HostMetadata $vm;Assert-NoNetworkAdapter $vm;Verify-Console $vm;$value='verified'}
                        'console-restore' {$value=Restore-Console (Target-VM $d)}
                        'windows' {Install-WindowsDisk $d;$value='started'}
                        'shutdown' {
                            $vm=Target-VM $d
                            if($vm.State -eq 'Off'){$value='off';break}
                            if($vm.State -ne 'Running'){throw 'GUEST_SHUTDOWN_PENDING'}
                            $job=Stop-VM -VM $vm -AsJob -Confirm:$false
                            try{
                                $completed=Wait-Job -Job $job -Timeout 75
                                if(!$completed -or $job.State -ne 'Completed'){throw 'GUEST_SHUTDOWN_PENDING'}
                                Receive-Job -Job $job -ErrorAction Stop | Out-Null
                            }finally{
                                if($job.State -in @('Running','NotStarted','Blocked')){Stop-Job -Job $job -ErrorAction SilentlyContinue}
                                Remove-Job -Job $job -ErrorAction SilentlyContinue
                            }
                            if((Target-VM $d).State -ne 'Off'){throw 'GUEST_SHUTDOWN_PENDING'}
                            $value='off'
                        }
                        'running' {$value=[string]((Target-VM $d).State -eq 'Running')}
                        'open' {$vm=Target-VM $d;if($vm.State -eq 'Paused'){Resume-VM -VM $vm | Out-Null}elseif($vm.State -in @('Off','Saved')){Start-VM -VM $vm | Out-Null};if((Get-VM -Id $vm.Id).State -ne 'Running'){throw 'HYPERV_NOT_READY'};$value='opened'}
                        'cleanup-recovery' {
                            $vm=Target-VM $d;$session=$null
                            try {
                                $session=Guest-Session $d
                                $valid=Invoke-Command -Session $session -ScriptBlock {param($vmId,$workspaceId)
                                    $identity=Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
                                    $identity.vmId -eq $vmId -and $identity.workspaceId -eq $workspaceId -and (Get-ItemProperty 'HKLM:\SYSTEM\Setup').SystemSetupInProgress -eq 0
                                } -ArgumentList ([string]$vm.Id),([string]$o.WorkspaceId)
                                if(-not $valid){throw 'VM_SCOPE'}
                                $value=Clear-FailedWindowsDisks $vm | ConvertTo-Json -Compress
                            }finally{if($session){Remove-PSSession $session}}
                        }
                        'cleanup' {$null=Target-VM $d;$value='clean'}
                        'repair-guest-session' {
                            $vm = Target-VM $d
                            Assert-NoNetworkAdapter $vm
                            $session = Guest-Session $d -Default
                            try {
                                $value = [string](Invoke-Command -Session $session -ScriptBlock {
                                    param($vmId,$workspaceId,$bootstrap)
                                    $root = 'C:\ProgramData\ClaudeBootstrap'
                                    $identity = Get-Content -LiteralPath (Join-Path $root 'identity.json') -Raw | ConvertFrom-Json
                                    if ($identity.vmId -ne $vmId -or $identity.workspaceId -ne $workspaceId) { throw 'VM_SCOPE' }
                                    if ((Get-ItemProperty 'HKLM:\SYSTEM\Setup').SystemSetupInProgress -ne 0) { return $false }
                                    $pwsh = Join-Path $root 'pwsh\pwsh.exe'
                                    if (-not (Test-Path -LiteralPath $pwsh -PathType Leaf)) { return $false }
                                    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))
                                    & $pwsh -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded *> $null
                                    return ($LASTEXITCODE -eq 0)
                                } -ArgumentList ([string]$vm.Id),([string]$o.WorkspaceId),([string]$d.bootstrap))
                            } finally {
                                Remove-PSSession $session
                            }
                        }
                        {$_ -in @('guest','ready','copy-to','copy-from','file-exists')} {
                            $session=$null
                            try {
                                $session=Guest-Session $d
                                if($_ -eq 'ready'){$value=[string](Invoke-Command -Session $session -ScriptBlock {$PSVersionTable.PSVersion.Major -ge 7 -and (Get-ItemProperty 'HKLM:\SYSTEM\Setup').SystemSetupInProgress -eq 0})}
                                elseif($_ -eq 'file-exists'){
                                    $value=[string](Invoke-Command -Session $session -ScriptBlock {
                                        param($path)
                                        Test-Path -LiteralPath $path -PathType Leaf
                                    } -ArgumentList ([string]$d.file))
                                }
                                elseif($_ -eq 'guest') {
                                    $result=Invoke-Command -Session $session -ScriptBlock {
                                        param($exe,$arguments,$interactiveScript,$user,$vmId,$workspaceId,$seconds)
                                        if($interactiveScript){& ([scriptblock]::Create($interactiveScript)) -Executable $exe -Arguments $arguments -UserName $user -VmId $vmId -WorkspaceId $workspaceId -TimeoutSeconds $seconds;return 'completed'}
                                        $output=& $exe @arguments 2>$null;if($LASTEXITCODE -ne 0){throw 'GUEST_COMMAND_FAILED'};return ($output | Out-String)
                                    } -ArgumentList ([string]$d.executable),@($d.arguments),([string]$d.interactiveScript),([string]$o.UserName),([string]$d.vmId),([string]$o.WorkspaceId),([int]$d.seconds)
                                    $value=[string]$result
                                }elseif($_ -eq 'copy-to') {
                                    $source=Assert-LocalPath ([string]$d.source) $o.Root
                                    Copy-Item -LiteralPath $source -Destination ([string]$d.destination) -ToSession $session -Force
                                    $value='copied'
                                }else {
                                    $dest=Assert-LocalPath ([string]$d.destination) $o.Root
                                    Copy-Item -LiteralPath ([string]$d.source) -Destination $dest -FromSession $session -Force
                                    $value='copied'
                                }
                            }finally{if($session){Remove-PSSession $session}}
                        }
                        default {throw 'UNSUPPORTED_OPERATION'}
                    }
                }
            }
            $response=@{ok=$true;value=[string]$value;code='OK'}
        }catch {
            $code=$_.Exception.Message
            if($code -notin @('HYPERV_RESTART','HYPERV_EDITION','HYPERV_SWITCH','HYPERV_NOT_READY','VM_SCOPE','VM_COLLISION','DISK_SCOPE','HYPERV_DISK_RECOVERY','HYPERV_DISK_MOUNTED','WINDOWS_MEDIA_UNSUPPORTED','WINDOWS_IMAGE_APPLY_FAILED','WINDOWS_BOOT_FILES_FAILED','GUEST_COMMAND_FAILED','GUEST_SHUTDOWN_PENDING','BOUNDARY_NICS','BOUNDARY_SERVICE','BOUNDARY_METADATA','HYPERV_CONSOLE_CONSENT','HYPERV_CONSOLE_SHUTDOWN','HYPERV_CONSOLE_POLICY','GUEST_LOGIN_REQUIRED','VM_SECURITY','VM_KEY_PROTECTOR_READ_FAILED','VM_KEY_PROTECTOR_SETUP_FAILED','VM_TPM_ENABLE_FAILED','UNSUPPORTED_OPERATION')){$code='HYPERV_STEP_FAILED'}
            $response=@{ok=$false;value='';code=$code}
            if($request.operation -eq 'windows' -and $script:windowsInstallStage){
                $response.windowsStage=$script:windowsInstallStage
                if($null -ne $script:windowsNativeCode){
                    $response.windowsNativeCode=[int]$script:windowsNativeCode
                }elseif($code -eq 'HYPERV_STEP_FAILED'){
                    $cause=$_.Exception.GetBaseException()
                    if($cause -is [ComponentModel.Win32Exception]){$response.windowsNativeCode=$cause.NativeErrorCode}
                    elseif($cause -isnot [Management.Automation.RuntimeException]){$response.windowsNativeCode=$cause.HResult}
                }
            }
        }
        $writer.WriteLine(($response | ConvertTo-Json -Compress -Depth 4))
        $request=$null;$d=$null
    }
}finally{$writer.Dispose();$reader.Dispose();$pipe.Dispose()}
