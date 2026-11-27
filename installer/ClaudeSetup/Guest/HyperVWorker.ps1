#requires -Version 7.0
param([string]$PipeName='',[switch]$ProbeGuestStatus,[string]$SpawnerPipeName='',[string]$RecoveryPipeName='',[switch]$SpawnerOnly)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$script:supervisedChildren=[Collections.Concurrent.ConcurrentDictionary[int,Diagnostics.Process]]::new()
$script:sharedState=[Collections.Concurrent.ConcurrentDictionary[string,string]]::new()
$script:recoveryStop=[ref]$false
$script:recoveryPs=$null
$script:recoveryHandle=$null
$script:workerScriptPath=$PSCommandPath
function Register-SupervisedChild([Diagnostics.Process]$Process) {
    if($null -eq $Process){return}
    $script:supervisedChildren[$Process.Id]=$Process
}
function Unregister-SupervisedChild([int]$ProcessId) {
    $removed=$null
    [void]$script:supervisedChildren.TryRemove($ProcessId,[ref]$removed)
}
function Stop-SupervisedChildren {
    foreach($childPid in @($script:supervisedChildren.Keys)) {
        $proc=$null
        if($script:supervisedChildren.TryRemove($childPid,[ref]$proc) -and $proc) {
            try { if(-not $proc.HasExited){ $proc.Kill($true); $null=$proc.WaitForExit(1500) } } catch { }
            try { $proc.Dispose() } catch { }
        }
    }
}
if(-not $ProbeGuestStatus) {
    if($PipeName -notmatch '^ClaudeSetup-[a-f0-9]{32}$'){exit 1}
    if($SpawnerPipeName) {
        if($SpawnerOnly -or $SpawnerPipeName -notmatch '^ClaudeSetup-[a-f0-9]{32}$'){exit 1}
        try {
            $spStart=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
            $spStart.UseShellExecute=$false;$spStart.CreateNoWindow=$true
            foreach($a in @('-NoLogo','-NoProfile','-File',$PSCommandPath,'-PipeName',$SpawnerPipeName,'-SpawnerOnly')){$spStart.ArgumentList.Add($a)}
            $spProc=[Diagnostics.Process]::Start($spStart)
            if($spProc){Register-SupervisedChild $spProc}
        } catch { }
    }
    if($RecoveryPipeName) {
        if($SpawnerOnly -or $RecoveryPipeName -notmatch '^ClaudeSetup-[a-f0-9]{32}$'){exit 1}
        try {
            $script:recoveryPs=[powershell]::Create()
            $null=$script:recoveryPs.AddScript({
                param($recPipeName,$workerPath,$psHomePath,$childMap,$shared,$stopRef)
                $ErrorActionPreference='Stop'
                while(-not $stopRef.Value) {
                    $rp=$null;$rr=$null;$rw=$null
                    try {
                        $rp=[IO.Pipes.NamedPipeClientStream]::new('.',$recPipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::None,[Security.Principal.TokenImpersonationLevel]::Identification)
                        $rp.Connect(500)
                        $rr=[IO.StreamReader]::new($rp,[Text.Encoding]::UTF8,$false,4096,$true)
                        $rw=[IO.StreamWriter]::new($rp,[Text.UTF8Encoding]::new($false),4096,$true);$rw.AutoFlush=$true
                        while(-not $stopRef.Value -and $null -ne ($rl=$rr.ReadLine())) {
                            $req=$null
                            $resp=@{ok=$false;value='';code='HYPERV_STEP_FAILED'}
                            try {
                                $req=$rl | ConvertFrom-Json
                                $rd=$req.data
                                if($req.operation -eq 'exit'){break}
                                if($req.operation -eq 'init') {
                                    $resp=@{ok=$true;value='initialized';code='OK'}
                                } elseif($req.operation -eq 'launch-verifier') {
                                    $rootVal=''
                                    if(-not $shared.TryGetValue('root',[ref]$rootVal) -or -not $rootVal){throw 'VM_SCOPE'}
                                    if(([string]$rd.pipeName) -notmatch '^ClaudeSetup-[a-f0-9]{32}$'){throw 'VM_SCOPE'}
                                    $parentDir=[IO.Path]::GetFullPath((Join-Path $rootVal 'private')).TrimEnd('\')+'\'
                                    $fullScript=[IO.Path]::GetFullPath([string]$rd.scriptPath)
                                    if(-not $fullScript.StartsWith($parentDir,[StringComparison]::OrdinalIgnoreCase)){throw 'VM_SCOPE'}
                                    $part=$fullScript
                                    while($part){if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'VM_SCOPE'};$part=[IO.Path]::GetDirectoryName($part)}
                                    if((Get-FileHash -LiteralPath $fullScript -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $workerPath -Algorithm SHA256).Hash){throw 'VM_SCOPE'}
                                    $si=[Diagnostics.ProcessStartInfo]::new((Join-Path $psHomePath 'pwsh.exe'))
                                    $si.UseShellExecute=$false;$si.CreateNoWindow=$true
                                    $childArgs=@('-NoLogo','-NoProfile','-File',$fullScript,'-PipeName',[string]$rd.pipeName,'-SpawnerOnly')
                                    foreach($ca in $childArgs){$si.ArgumentList.Add($ca)}
                                    $cp=[Diagnostics.Process]::Start($si)
                                    if($cp){$childMap[$cp.Id]=$cp}
                                    $resp=@{ok=$true;value=[string]$cp.Id;code='OK'}
                                } else {
                                    throw 'UNSUPPORTED_OPERATION'
                                }
                            } catch {
                                $code=$_.Exception.Message
                                if($code -notin @('VM_SCOPE','UNSUPPORTED_OPERATION')){$code='HYPERV_STEP_FAILED'}
                                $resp=@{ok=$false;value='';code=$code}
                            }
                            if($null -ne $req -and $null -ne $req.id -and [string]$req.id -ne ''){$resp['id']=[string]$req.id}
                            $rw.WriteLine(($resp | ConvertTo-Json -Compress -Depth 4))
                        }
                    } catch {
                        if(-not $stopRef.Value){Start-Sleep -Milliseconds 120}
                    } finally {
                        if($rw){try{$rw.Dispose()}catch{}}
                        if($rr){try{$rr.Dispose()}catch{}}
                        if($rp){try{$rp.Dispose()}catch{}}
                    }
                }
            }).AddArgument($RecoveryPipeName).AddArgument($PSCommandPath).AddArgument($PSHOME).AddArgument($script:supervisedChildren).AddArgument($script:sharedState).AddArgument($script:recoveryStop)
            $script:recoveryHandle=$script:recoveryPs.BeginInvoke()
        } catch { }
    }
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$PipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::None,[Security.Principal.TokenImpersonationLevel]::Identification)
    $pipe.Connect(180000)
    $reader=[IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8,$false,4096,$true)
    $writer=[IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true);$writer.AutoFlush=$true
}
$o=$null;$verificationOnly=$false;$displayOnly=$false;$spawnerOnly=[bool]$SpawnerOnly
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
function Test-OwnedVmMarker([string]$MarkerPath,[string]$WorkspaceId,[string]$VmName) {
    if(-not [IO.File]::Exists($MarkerPath)){return $false}
    try {
        $marker=[IO.File]::ReadAllText($MarkerPath) | ConvertFrom-Json -AsHashtable
        return ($marker.schema -eq 1 -and [string]$marker.workspaceId -eq $WorkspaceId -and [string]$marker.vmName -eq $VmName)
    } catch { return $false }
}
function Save-OwnedVmMarker([string]$MarkerPath,[string]$WorkspaceId,[string]$VmName,[string]$VmId='',[hashtable]$VideoState=$null) {
    $parent=[IO.Path]::GetDirectoryName($MarkerPath)
    $null=Assert-LocalPath $MarkerPath $parent
    $tmp=$MarkerPath+'.new'
    $doc=[ordered]@{schema=1;workspaceId=$WorkspaceId;vmName=$VmName;vmId=$VmId}
    if($VideoState){
        $doc['defaultVideo']=$VideoState
    }
    $json=$doc | ConvertTo-Json -Compress -Depth 4
    [IO.File]::WriteAllText($tmp,$json,[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($tmp,$MarkerPath,$true)
}
function Set-NewTaskVmDefaultVideo($Vm,[int]$Width=1920,[int]$Height=1080) {
    $result=[ordered]@{requested="${Width}x${Height}";applied='';resolutionType='';verified=$false}
    if(-not $Vm -or -not (Get-Command Set-VMVideo -ErrorAction SilentlyContinue)){return $result}
    foreach($mode in @('Single','Maximum')) {
        try {
            Set-VMVideo -VM $Vm -HorizontalResolution $Width -VerticalResolution $Height -ResolutionType $mode -ErrorAction Stop
            $video=Get-VMVideo -VM $Vm -ErrorAction Stop
            $actualW=[int]$video.HorizontalResolution
            $actualH=[int]$video.VerticalResolution
            $actualType=[string]$video.ResolutionType
            $result['applied']="${actualW}x${actualH}"
            $result['resolutionType']=$actualType
            if($actualW -eq $Width -and $actualH -eq $Height){
                $result['verified']=$true
                return $result
            }
        } catch {
            continue
        }
    }
    return $result
}
function Resolve-OrCreateTaskVm($Data) {
    if($Data.vmId){return [string](Target-VM $Data).Id}
    $path=Assert-LocalPath (Join-Path $o.Root ('machines\'+$o.VmName)) $o.Root
    $marker=Assert-LocalPath (Join-Path $path '.claude-hyperv-owner.json') $path
    $existingVm=Get-VM -Name $o.VmName -ErrorAction SilentlyContinue
    $pathExists=[IO.Directory]::Exists($path) -or [IO.File]::Exists($path)
    if($existingVm) {
        $existingPath=[IO.Path]::GetFullPath([string]$existingVm.Path).TrimEnd('\')
        $expectedPath=[IO.Path]::GetFullPath($path).TrimEnd('\')
        if(($existingPath -eq $expectedPath -or $existingPath.StartsWith($expectedPath+'\',[StringComparison]::OrdinalIgnoreCase)) -and
            (Test-OwnedVmMarker $marker ([string]$o.WorkspaceId) ([string]$o.VmName))) {
            Get-VMNetworkAdapter -VM $existingVm | Remove-VMNetworkAdapter
            Save-OwnedVmMarker $marker ([string]$o.WorkspaceId) ([string]$o.VmName) ([string]$existingVm.Id)
            return [string]$existingVm.Id
        }
        throw 'VM_COLLISION'
    }
    if($pathExists) {
        $owned=([IO.Directory]::Exists($path)) -and (Test-OwnedVmMarker $marker ([string]$o.WorkspaceId) ([string]$o.VmName))
        $others=if([IO.Directory]::Exists($path)){@([IO.Directory]::GetFileSystemEntries($path) | Where-Object { [IO.Path]::GetFileName($_) -notin @('.claude-hyperv-owner.json','.claude-hyperv-owner.json.new') })}else{@('file')}
        if(-not $owned -or $others.Count -gt 0){throw 'VM_COLLISION'}
    } else {
        [void][IO.Directory]::CreateDirectory($path)
    }
    Save-OwnedVmMarker $marker ([string]$o.WorkspaceId) ([string]$o.VmName) ''
    $vm=New-VM -Name $o.VmName -Path $path -Generation 2 -MemoryStartupBytes ([long]$o.MemoryMb*1MB) -NoVHD
    Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter
    # Apply 16:9 1920x1080 default video mode ONLY for newly created VMs before first boot.
    $videoState=Set-NewTaskVmDefaultVideo $vm 1920 1080
    Save-OwnedVmMarker $marker ([string]$o.WorkspaceId) ([string]$o.VmName) ([string]$vm.Id) $videoState
    return [string]$vm.Id
}
function Test-VMKeyProtectorPresent($Vm) {
    $protector=Get-VMKeyProtector -VM $Vm
    if($null -eq $protector){return $false}
    if($protector -is [byte[]]){
        if($protector.Length -eq 0){return $false}
        # Real Hyper-V VMs without a key protector emit a 4-byte empty header (00-00-00-04).
        if($protector.Length -eq 4 -and $protector[0] -eq 0 -and $protector[1] -eq 0 -and $protector[2] -eq 0 -and $protector[3] -in @(0, 4)){return $false}
        return $true
    }
    if($protector -is [System.Collections.ICollection]){
        if($protector.Count -eq 0){return $false}
        if($protector.Count -eq 1 -and $protector[0] -is [byte[]]){
            $inner=[byte[]]$protector[0]
            if($inner.Length -eq 0){return $false}
            if($inner.Length -eq 4 -and $inner[0] -eq 0 -and $inner[1] -eq 0 -and $inner[2] -eq 0 -and $inner[3] -in @(0, 4)){return $false}
        }
        return $true
    }
    return $true
}
function Enable-TaskVirtualTpm($Vm) {
    if((Get-VMSecurity -VM $Vm).TpmEnabled){return}
    try{$present=Test-VMKeyProtectorPresent $Vm}catch{Save-HyperVErrorContext $_;throw 'VM_KEY_PROTECTOR_READ_FAILED'}
    if(-not $present){
        try{
            Set-VMKeyProtector -VM $Vm -NewLocalKeyProtector
            if(-not(Test-VMKeyProtectorPresent $Vm)){throw 'VM_KEY_PROTECTOR_SETUP_FAILED'}
        }catch{Save-HyperVErrorContext $_;throw 'VM_KEY_PROTECTOR_SETUP_FAILED'}
    }
    $script:tpmErrorHResult = $null
    try { Enable-VMTPM -VM $Vm }
    catch { Save-HyperVErrorContext $_;$script:tpmErrorHResult = [int]$_.Exception.GetBaseException().HResult; throw 'VM_TPM_ENABLE_FAILED' }
}
function Guest-Session($Data,[switch]$Default) {
    $vm=Target-VM $Data
    $cred=[pscredential]::new([string]$o.UserName,(ConvertTo-SecureString -String ([string]$Data.password) -AsPlainText -Force))
    if ($Default) { return New-PSSession -VMId $vm.Id -Credential $cred -ErrorAction Stop }
    New-PSSession -VMId $vm.Id -Credential $cred -ConfigurationName 'PowerShell.7' -ErrorAction Stop
}
function Read-GuestBootstrap($Session, $VmId, $WorkspaceId, [bool]$NamedSession) {
    Invoke-Command -Session $Session -ScriptBlock {
        param($vmId, $workspaceId, $namedSession)
        $setup = Get-ItemProperty 'HKLM:\SYSTEM\Setup'
        if ([int]$setup.SystemSetupInProgress -ne 0 -or [int]$setup.OOBEInProgress -ne 0) { return 'WindowsSetup' }
        $root = 'C:\ProgramData\ClaudeBootstrap'
        $identityPath = Join-Path $root 'identity.json'
        if (!(Test-Path -LiteralPath $identityPath -PathType Leaf)) { return 'BootstrapMissing' }
        $identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
        if ($identity.vmId -ne $vmId -or $identity.workspaceId -ne $workspaceId) { throw 'VM_SCOPE' }

        $lockPath = Join-Path $root 'initialize.lock'
        $running = $false
        if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
            try { $handle = [IO.File]::Open($lockPath, 'Open', 'Read', 'ReadWrite'); $handle.Dispose() }
            catch [IO.IOException] { $running = $true }
        }
        $statusPath = Join-Path $root 'initialize-status.json'
        if (Test-Path -LiteralPath $statusPath -PathType Leaf) {
            $stream = [IO.File]::Open($statusPath, 'Open', 'Read', ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            try {
                if ($stream.Length -gt 4096) { return 'StatusUnavailable' }
                $statusReader = [IO.StreamReader]::new($stream)
                try { $status = $statusReader.ReadToEnd() | ConvertFrom-Json }
                finally { $statusReader.Dispose() }
            } finally { $stream.Dispose() }
            if ($status.vmId -ne $vmId -or $status.workspaceId -ne $workspaceId) { throw 'VM_SCOPE' }
            $phase = switch ($status.phase) {
                'prepare' { 'PreparingBootstrap' }
                'remoting' { 'PreparingRemoting' }
                'loopback' { 'PreparingLoopback' }
                'endpoint' { 'VerifyingEndpoint' }
                default { 'Initializing' }
            }
            if ($running) { return $phase }
            if ($status.state -ne 'ready') { return ('Failed' + $phase) }
        } elseif ($running) { return 'Initializing' }
        else {
            # A legacy first-logon script has no lock or progress record.
            $legacyFile = Join-Path $root 'Initialize.ps1'
            $legacyExe = Join-Path $root 'pwsh\pwsh.exe'
            $legacy = @(Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" -ErrorAction SilentlyContinue |
                Where-Object { $_.ExecutablePath -eq $legacyExe -and
                    $_.CommandLine -and $_.CommandLine.IndexOf($legacyFile, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
            if ($legacy.Count) { return 'Initializing' }
        }
        if ($namedSession -and $PSVersionTable.PSVersion.Major -ge 7) { return 'Ready' }
        return 'EndpointUnavailable'
    } -ArgumentList $VmId, $WorkspaceId, $NamedSession
}

function Get-GuestSessionFailure($ErrorRecord) {
    $cause = $ErrorRecord.Exception
    while ($cause) {
        if (($cause -is [ComponentModel.Win32Exception] -and $cause.NativeErrorCode -eq 1326) -or
            $cause.HResult -in @(1326, -2147023570) -or
            ($cause.PSObject.Properties['ErrorCode'] -and $cause.ErrorCode -in @(1326, -2147023570))) { return 'CredentialsRejected' }
        $cause = $cause.InnerException
    }
    if ($ErrorRecord.CategoryInfo.Category -eq 'AuthenticationError') { return 'CredentialsRejected' }
    if ($ErrorRecord.CategoryInfo.Category -eq 'PermissionDenied') { return 'AccessDenied' }
    return 'SessionUnavailable'
}

function Invoke-GuestStatusProbeDirect($Data) {
    $vm = Target-VM $Data
    $named = $null
    try { $named = Guest-Session $Data }
    catch {
        $failure = Get-GuestSessionFailure $_
        if ($failure -ne 'SessionUnavailable') { return $failure }
    }
    if ($named) {
        try { return Read-GuestBootstrap $named ([string]$vm.Id) ([string]$o.WorkspaceId) $true }
        finally { Remove-PSSession $named }
    }

    try { $fallback = Guest-Session $Data -Default }
    catch { return Get-GuestSessionFailure $_ }
    try { return Read-GuestBootstrap $fallback ([string]$vm.Id) ([string]$o.WorkspaceId) $false }
    finally { Remove-PSSession $fallback }
}

function Get-HyperVGuestStatus($Data, [int]$TimeoutMs = 12000) {
    $null = Target-VM $Data
    if (-not $script:workerScriptPath -or -not [IO.File]::Exists($script:workerScriptPath)) {
        return Invoke-GuestStatusProbeDirect $Data
    }
    $pwshExe = Join-Path $PSHOME 'pwsh.exe'
    if (-not [IO.File]::Exists($pwshExe)) {
        return Invoke-GuestStatusProbeDirect $Data
    }
    $start = [Diagnostics.ProcessStartInfo]::new($pwshExe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($a in @('-NoLogo','-NoProfile','-NonInteractive','-File',$script:workerScriptPath,'-ProbeGuestStatus')) {
        $start.ArgumentList.Add($a)
    }
    $child = [Diagnostics.Process]::Start($start)
    if (-not $child) { return 'SessionUnavailable' }
    Register-SupervisedChild $child
    try {
        $payload = @{
            options = $o
            data = @{ vmId = [string]$Data.vmId; password = [string]$Data.password }
        } | ConvertTo-Json -Compress -Depth 4
        $child.StandardInput.WriteLine($payload)
        $child.StandardInput.Close()
        $outTask = $child.StandardOutput.ReadToEndAsync()
        $errTask = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit($TimeoutMs)) {
            try { $child.Kill($true); $child.WaitForExit(2000) | Out-Null } catch { }
            return 'SessionUnavailable'
        }
        $child.WaitForExit()
        $stdout = $outTask.GetAwaiter().GetResult()
        $null = $errTask.Wait(500)
        foreach ($line in ($stdout -split "`r?`n")) {
            if ($line -match '^STATUS:([A-Za-z0-9]+)$') {
                return $Matches[1]
            }
        }
        return 'SessionUnavailable'
    } finally {
        Unregister-SupervisedChild $child.Id
        $child.Dispose()
    }
}

function Copy-VerifiedGuestResource($Session, $Data, $Source, [string]$UserName, [string]$Temporary) {
    if ($Data.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$Data.size -le 0) { throw 'GUEST_COPY_INVALID' }
    $destination = [string]$Data.destination
    $script:copyStage = 'destination'
    $alreadyPresent = Invoke-Command -Session $Session -ScriptBlock {
        param($path, $user, $hash, $size)
        $root = [IO.Path]::GetFullPath("C:\Users\$user\AppData\Local\ClaudeEnvironmentSetup")
        if (![IO.Path]::IsPathFullyQualified($path) -or [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ne $root) { throw 'VM_SCOPE' }
        if (!(Test-Path -LiteralPath $root -PathType Container)) { throw 'GUEST_COPY_DIRECTORY' }
        for ($part = $root; $part; $part = [IO.Path]::GetDirectoryName($part)) {
            if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'VM_SCOPE' }
        }
        if (Test-Path -LiteralPath $path) {
            $item = Get-Item -LiteralPath $path -Force
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'VM_SCOPE' }
            if ($item.Length -eq $size -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $hash) { return $true }
        }
        if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($path)).AvailableFreeSpace -lt $size) { throw 'GUEST_COPY_DISK_FULL' }
        return $false
    } -ArgumentList $destination, $UserName, ([string]$Data.sha256), ([long]$Data.size)
    try {
        if ($alreadyPresent) { return }
        $script:copyStage = 'transfer'
        Copy-Item -LiteralPath $Source -Destination $Temporary -ToSession $Session -Force
        $script:copyStage = 'verify'
        Invoke-Command -Session $Session -ScriptBlock {
            param($temp, $path, $hash, $size)
            $item = Get-Item -LiteralPath $temp -Force
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                $item.Length -ne $size -or (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash -ne $hash) { throw 'GUEST_COPY_HASH' }
            [IO.File]::Move($temp, $path, $true)
        } -ArgumentList $temporary, $destination, ([string]$Data.sha256), ([long]$Data.size)
    } finally {
        if ($Session.State -eq 'Opened') {
            try {
                Invoke-Command -Session $Session -ScriptBlock {
                    param($path)
                    if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
                } -ArgumentList $temporary
            } catch { }
        }
    }
}

function Send-VerifiedGuestResource($Data, $Source) {
    $temporary = [string]$Data.destination + '.claudome-transfer-' + [guid]::NewGuid().ToString('N') + '.tmp'
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $session = $null
        try {
            $script:copyStage = 'session'
            $session = Guest-Session $Data
            Copy-VerifiedGuestResource $session $Data $Source ([string]$o.UserName) $temporary
            return
        } catch {
            $transport = $_.Exception
            while ($transport -and $transport -isnot [Management.Automation.Remoting.PSRemotingTransportException]) {
                $transport = $transport.InnerException
            }
            if ($attempt -ne 0 -or !$session -or (!$transport -and $session.State -notin 'Broken','Disconnected')) { throw }
        } finally { if ($session) { Remove-PSSession $session } }
    }
}

function Disable-HostMetadata($Vm) {
    $service=@(Get-VMIntegrationService -VM $Vm | Where-Object {$_.Id.ToString() -match '2a34b1c2-fd73-4043-8a5b-dd2159bc743f' -or $_.Name -match 'Key-Value|Data Exchange|数据交换|键值'})
    if($service.Count -ne 1){throw 'BOUNDARY_METADATA'}
    $service | Disable-VMIntegrationService
    if(@(Get-VMIntegrationService -VM $Vm | Where-Object {$_.Id -eq $service[0].Id -and $_.Enabled}).Count){throw 'BOUNDARY_METADATA'}
}
function Console-Owner($Vm){return 'HKLM:\SOFTWARE\Claudome\HyperVConsolePolicy\Owners\'+[string]$Vm.Id}
function Use-BasicConsolePolicy {
    if([bool]$o.HyperVBasicConsoleConfirmed){return $true}
    if($null -ne $o.PSObject.Properties['SharedClipboardEnabled']){return -not [bool]$o.SharedClipboardEnabled}
    return $true
}
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
    $basic=Use-BasicConsolePolicy
    if($basic -and $original -and -not $o.HyperVBasicConsoleConfirmed){throw 'HYPERV_CONSOLE_CONSENT'}
    if($basic -and $current -and $Vm.State -ne 'Off'){throw 'HYPERV_CONSOLE_SHUTDOWN'}
    if(-not(Test-Path -LiteralPath $root)){New-Item -Path $root -Force | Out-Null;New-ItemProperty -LiteralPath $root -Name OriginalEnabled -PropertyType DWord -Value ([int]$current) | Out-Null}
    $owner=Console-Owner $Vm
    if(Test-Path -LiteralPath $owner){if((Get-ItemPropertyValue -LiteralPath $owner -Name WorkspaceId) -ne $o.WorkspaceId){throw 'VM_SCOPE'}}
    else{New-Item -Path $owner -Force | Out-Null;New-ItemProperty -LiteralPath $owner -Name WorkspaceId -Value ([string]$o.WorkspaceId) -PropertyType String | Out-Null}
    $target=(-not $basic)
    if($current -ne $target){Set-VMHost -EnableEnhancedSessionMode $target}
    if([bool](Get-VMHost).EnableEnhancedSessionMode -ne $target){throw 'HYPERV_CONSOLE_POLICY'}
    if($target -and (Get-Command Set-VM -ErrorAction SilentlyContinue)){Set-VM -VM $Vm -EnhancedSessionTransportType HvSocket}
    }
}
function Verify-Console($Vm){
    $owner=Console-Owner $Vm
    $target=(-not (Use-BasicConsolePolicy))
    if([bool](Get-VMHost).EnableEnhancedSessionMode -ne $target -or -not(Test-Path -LiteralPath $owner) -or (Get-ItemPropertyValue -LiteralPath $owner -Name WorkspaceId) -ne $o.WorkspaceId){throw 'HYPERV_CONSOLE_POLICY'}
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
function Reset-HyperVRequestDiagnostics {
    $script:windowsInstallStage = ''
    $script:windowsNativeCode = $null
    $script:hypervSubstage = ''
    $script:capturedErrorHResult = $null
    $script:capturedErrorNativeCode = $null
    $script:capturedErrorCategory = $null
    $script:capturedNativeExitCode = $null
    $script:tpmErrorHResult = $null
    $script:copyStage = ''
    $global:LASTEXITCODE = 0
}
function Save-HyperVErrorContext($ErrorRecord) {
    if ($null -eq $ErrorRecord) { return }
    $ex = if ($ErrorRecord.Exception) { $ErrorRecord.Exception.GetBaseException() } else { $null }
    if ($null -ne $ex -and $ex.HResult -ne 0) {
        $script:capturedErrorHResult = [int]$ex.HResult
    }
    if ($ex -is [ComponentModel.Win32Exception]) {
        $script:capturedErrorNativeCode = [int]$ex.NativeErrorCode
    }
    if ($ErrorRecord.CategoryInfo -and $ErrorRecord.CategoryInfo.Category) {
        $script:capturedErrorCategory = [string]$ErrorRecord.CategoryInfo.Category
    }
}
function Format-HyperVErrorResponse($Request, $ErrorRecord) {
    $code = $ErrorRecord.Exception.Message
    if ($code -notmatch '^(GUEST_BRIDGE_(2[0-9]|3[01])|GUEST_COPY_(INVALID|DIRECTORY|DISK_FULL|HASH|SOURCE)|HYPERV_(MODULE_UNAVAILABLE|VMMS_MISSING|VMMS_DISABLED|VMMS_STOPPED|FEATURE_NOT_FOUND|FEATURE_QUERY_FAILED))$' -and $code -notin @('HYPERV_RESTART','HYPERV_EDITION','HYPERV_SWITCH','HYPERV_NOT_READY','VM_SCOPE','VM_COLLISION','DISK_SCOPE','HYPERV_DISK_RECOVERY','HYPERV_DISK_MOUNTED','WINDOWS_MEDIA_UNSUPPORTED','WINDOWS_IMAGE_APPLY_FAILED','WINDOWS_BOOT_FILES_FAILED','GUEST_COMMAND_FAILED','GUEST_SHUTDOWN_PENDING','BOUNDARY_NICS','BOUNDARY_SERVICE','BOUNDARY_METADATA','HYPERV_CONSOLE_CONSENT','HYPERV_CONSOLE_SHUTDOWN','HYPERV_CONSOLE_POLICY','GUEST_LOGIN_REQUIRED','VM_SECURITY','VM_KEY_PROTECTOR_READ_FAILED','VM_KEY_PROTECTOR_SETUP_FAILED','VM_TPM_ENABLE_FAILED','UNSUPPORTED_OPERATION')) { $code = 'HYPERV_STEP_FAILED' }
    $resp = @{ ok = $false; value = ''; code = $code }
    $cause = $ErrorRecord.Exception.GetBaseException()
    $resp.errorOperation = [string]$Request.operation
    $substage = if ($script:windowsInstallStage) { $script:windowsInstallStage } elseif ($script:hypervSubstage) { $script:hypervSubstage } else { '' }
    if ($substage) { $resp.errorSubstage = [string]$substage }
    if ($null -ne $script:capturedErrorHResult) {
        $resp.errorHResult = [int]$script:capturedErrorHResult
    } elseif ($null -ne $cause -and -not ($cause -is [Management.Automation.RuntimeException] -and $cause.HResult -eq -2146233087)) {
        $resp.errorHResult = [int]$cause.HResult
    }
    if ($null -ne $script:capturedErrorNativeCode) {
        $resp.errorNativeCode = [int]$script:capturedErrorNativeCode
    } elseif ($cause -is [ComponentModel.Win32Exception]) {
        $resp.errorNativeCode = [int]$cause.NativeErrorCode
    } elseif ($null -ne $script:capturedNativeExitCode -and $script:capturedNativeExitCode -ne 0) {
        $resp.errorNativeCode = [int]$script:capturedNativeExitCode
    } elseif ($null -ne $script:windowsNativeCode -and $script:windowsNativeCode -ne 0) {
        $resp.errorNativeCode = [int]$script:windowsNativeCode
    }
    if ($script:capturedErrorCategory) {
        $resp.errorCategory = [string]$script:capturedErrorCategory
    } elseif ($ErrorRecord.CategoryInfo -and $ErrorRecord.CategoryInfo.Category) {
        $resp.errorCategory = [string]$ErrorRecord.CategoryInfo.Category
    }
    if ($code -eq 'HYPERV_STEP_FAILED' -and $Request.operation -in @('copy-to','copy-from')) {
        $resp.copyErrorStage = $script:copyStage
        $resp.copyErrorCategory = [string]$ErrorRecord.CategoryInfo.Category
        $resp.copyErrorHResult = [int]$cause.HResult
    }
    if ($code -eq 'VM_TPM_ENABLE_FAILED' -and $null -ne $script:tpmErrorHResult) {
        $resp.tpmErrorHResult = $script:tpmErrorHResult
    }
    if ($Request.operation -eq 'windows' -and $script:windowsInstallStage) {
        $resp.windowsStage = $script:windowsInstallStage
        if ($null -ne $script:windowsNativeCode) {
            $resp.windowsNativeCode = [int]$script:windowsNativeCode
        } elseif ($code -eq 'HYPERV_STEP_FAILED') {
            if ($cause -is [ComponentModel.Win32Exception]) { $resp.windowsNativeCode = $cause.NativeErrorCode }
            elseif ($cause -isnot [Management.Automation.RuntimeException]) { $resp.windowsNativeCode = $cause.HResult }
        }
    }
    return $resp
}
function Resolve-AttachedVhdChain($Vm, [string]$AttachedPath, [int]$MaxDepth = 16) {
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $current = Assert-LocalPath $AttachedPath $Vm.Path
    $depth = 0
    $baseVhd = $null
    while ($current) {
        if ($depth -ge $MaxDepth) { return @{ chainValid = $false; reason = 'chain-too-deep'; chainDepth = $depth; basePath = $current; baseVhd = $null } }
        if (-not $visited.Add($current)) { return @{ chainValid = $false; reason = 'chain-cycle'; chainDepth = $depth; basePath = $current; baseVhd = $null } }
        $vhd = Get-VHD -Path $current
        $resolved = Assert-LocalPath ([string]$vhd.Path) $Vm.Path
        if ($resolved -ne $current) { return @{ chainValid = $false; reason = 'path-mismatch'; chainDepth = $depth; basePath = $current; baseVhd = $null } }
        $parent = [string]$vhd.ParentPath
        if ([string]::IsNullOrWhiteSpace($parent)) {
            $baseVhd = $vhd
            return @{ chainValid = $true; reason = 'ok'; chainDepth = $depth; basePath = $resolved; baseVhd = $baseVhd }
        }
        $current = Assert-LocalPath $parent $Vm.Path
        $depth++
    }
    return @{ chainValid = $false; reason = 'empty-chain'; chainDepth = $depth; basePath = ''; baseVhd = $null }
}
function Get-WindowsInstallDiagnostics($Vm) {
    $snaps = @(Get-VMSnapshot -VM $Vm -ErrorAction SilentlyContinue)
    $path = Assert-LocalPath (Join-Path $Vm.Path '.windows-install.json') $Vm.Path
    if([IO.File]::Exists($path)) {
        try {
            if(([IO.FileInfo]::new($path)).Length -gt 65536){return @{state='needs-recovery';reason='invalid-log';snapshotCount=$snaps.Count;chainMatchesBase=$false;baseDiskPath=''}}
            $c=[IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable
            if($c.schema -ne 1 -or $c.vmId -ne [string]$Vm.Id -or $c.workspaceId -ne $o.WorkspaceId -or $c.isoSha256 -ne $o.IsoSha256 -or $c.imageIndex -ne $o.WindowsImageIndex -or $c.userName -ne $o.UserName -or $c.diskGb -ne $o.DiskGb -or $c.phase -notin @('planned','applying','image-ready','ready') -or @($c.preservedDisks).Count -gt 128){return @{state='needs-recovery';reason='identity-mismatch';snapshotCount=$snaps.Count;chainMatchesBase=$false;baseDiskPath=''}}
            $disk=Assert-InstallDiskPath ([string]$c.diskPath) $Vm
            foreach($old in @($c.preservedDisks)){$null=Assert-InstallDiskPath ([string]$old) $Vm}
            $allDrives = @(Get-VMHardDiskDrive -VM $Vm)
            if ($allDrives.Count -eq 1) {
                $attachedFull = [IO.Path]::GetFullPath([string]$allDrives[0].Path)
                if ($attachedFull -ne $disk) {
                    try {
                        $chain = Resolve-AttachedVhdChain $Vm $attachedFull
                        $matchesBase = $chain.chainValid -and ($chain.basePath -eq $disk) -and $chain.baseVhd -and
                            ([string]$chain.baseVhd.DiskIdentifier -eq $c.diskId) -and $c.diskId -and
                            ($chain.baseVhd.Size -eq ([long]$o.DiskGb*1GB)) -and ($chain.baseVhd.VhdFormat -eq 'VHDX') -and ($chain.baseVhd.VhdType -eq 'Dynamic')
                        $rs = if ($matchesBase) { 'checkpoint-chain-matched' } else { 'checkpoint-chain-invalid' }
                        return @{ state = 'needs-recovery'; reason = $rs; snapshotCount = $snaps.Count; chainMatchesBase = [bool]$matchesBase; baseDiskPath = $disk }
                    } catch {
                        return @{ state = 'needs-recovery'; reason = 'checkpoint-chain-invalid'; snapshotCount = $snaps.Count; chainMatchesBase = $false; baseDiskPath = $disk }
                    }
                }
            }
            $drives=@(Assert-InstallDrives $Vm $disk)
            if ($snaps.Count -gt 0) {
                return @{ state = 'needs-recovery'; reason = 'checkpoint-active'; snapshotCount = $snaps.Count; chainMatchesBase = $true; baseDiskPath = $disk }
            }
            if($c.phase -eq 'ready') {
                if($drives.Count -ne 1){return @{state='needs-recovery';reason='drive-missing';snapshotCount=$snaps.Count;chainMatchesBase=$false;baseDiskPath=$disk}}
                $null=Confirm-WindowsDisk $c $Vm -AllowAttached:$true
                return @{state='ready';reason='ok';snapshotCount=0;chainMatchesBase=$true;baseDiskPath=$disk}
            }
            if($c.phase -eq 'image-ready') {
                if($drives.Count -ne 0){return @{state='needs-recovery';reason='drive-unexpected';snapshotCount=$snaps.Count;chainMatchesBase=$false;baseDiskPath=$disk}}
                $null=Confirm-WindowsDisk $c $Vm
                return @{state='image-ready';reason='ok';snapshotCount=0;chainMatchesBase=$true;baseDiskPath=$disk}
            }
            if($c.phase -eq 'applying'){return @{state='needs-recovery';reason='incomplete-apply';snapshotCount=$snaps.Count;chainMatchesBase=$false;baseDiskPath=$disk}}
            if($drives.Count -gt 0 -or [IO.File]::Exists($disk)){return @{state='needs-recovery';reason='unexpected-disk';snapshotCount=$snaps.Count;chainMatchesBase=$false;baseDiskPath=$disk}}
            return @{state='empty';reason='ok';snapshotCount=0;chainMatchesBase=$true;baseDiskPath=$disk}
        } catch { return @{state='needs-recovery';reason='probe-error';snapshotCount=$snaps.Count;chainMatchesBase=$false;baseDiskPath=''} }
    }
    $defaultDisk=Assert-InstallDiskPath (Join-Path $Vm.Path 'Windows11.vhdx') $Vm
    try { $drives=@(Assert-InstallDrives $Vm $defaultDisk) } catch { return @{state='needs-recovery';reason='drive-mismatch';snapshotCount=$snaps.Count;chainMatchesBase=$false;baseDiskPath=$defaultDisk} }
    if ($snaps.Count -gt 0) {
        return @{ state = 'needs-recovery'; reason = 'checkpoint-active'; snapshotCount = $snaps.Count; chainMatchesBase = $false; baseDiskPath = $defaultDisk }
    }
    $ready=Assert-LocalPath (Join-Path $Vm.Path '.windows-applied') $Vm.Path
    $legacy=([IO.File]::Exists($ready)) -and ([IO.File]::ReadAllText($ready)).Trim() -eq 'applied'
    if($legacy) {
        try {
            if(-not [IO.File]::Exists($defaultDisk)){return @{state='needs-recovery';reason='missing-disk';snapshotCount=0;chainMatchesBase=$false;baseDiskPath=$defaultDisk}}
            $vhd=Get-VHD -Path $defaultDisk
            if($vhd.Size -ne ([long]$o.DiskGb*1GB) -or $vhd.VhdFormat -ne 'VHDX' -or $vhd.VhdType -ne 'Dynamic' -or $vhd.ParentPath){return @{state='needs-recovery';reason='vhd-mismatch';snapshotCount=0;chainMatchesBase=$false;baseDiskPath=$defaultDisk}}
            if($drives.Count -eq 1){return @{state='ready';reason='ok';snapshotCount=0;chainMatchesBase=$true;baseDiskPath=$defaultDisk}}
            if($drives.Count -eq 0 -and -not $vhd.Attached){return @{state='image-ready';reason='ok';snapshotCount=0;chainMatchesBase=$true;baseDiskPath=$defaultDisk}}
            return @{state='needs-recovery';reason='vhd-attached';snapshotCount=0;chainMatchesBase=$false;baseDiskPath=$defaultDisk}
        } catch { return @{state='needs-recovery';reason='legacy-error';snapshotCount=0;chainMatchesBase=$false;baseDiskPath=$defaultDisk} }
    }
    if($drives.Count -eq 0 -and -not [IO.File]::Exists($defaultDisk)){return @{state='empty';reason='ok';snapshotCount=0;chainMatchesBase=$true;baseDiskPath=$defaultDisk}}
    return @{state='needs-recovery';reason='orphan-disk';snapshotCount=0;chainMatchesBase=$false;baseDiskPath=$defaultDisk}
}
function Get-WindowsInstallProbe($Vm) {
    return (Get-WindowsInstallDiagnostics $Vm).state
}
function Disable-TaskVmAutomaticCheckpoints($Vm) {
    $current = Get-VM -Id $Vm.Id
    if (-not $current.AutomaticCheckpointsEnabled) { return 'already-disabled' }
    if ($current.State -ne 'Off') { throw 'HYPERV_CHECKPOINT_ACTIVE' }
    $snaps = @(Get-VMSnapshot -VM $current -ErrorAction SilentlyContinue)
    if ($snaps.Count -gt 0) { throw 'HYPERV_CHECKPOINT_ACTIVE' }
    $ownerMarker = Join-Path (Split-Path -Parent $current.Path) '.claude-hyperv-owner.json'
    $ownerAlt = Join-Path $current.Path '.claude-hyperv-owner.json'
    if ([IO.File]::Exists($ownerMarker)) {
        if (-not (Test-OwnedVmMarker $ownerMarker ([string]$o.WorkspaceId) ([string]$o.VmName))) { throw 'VM_SCOPE' }
    } elseif ([IO.File]::Exists($ownerAlt)) {
        if (-not (Test-OwnedVmMarker $ownerAlt ([string]$o.WorkspaceId) ([string]$o.VmName))) { throw 'VM_SCOPE' }
    }
    $probe = Get-WindowsInstallProbe $current
    if ($probe -notin @('empty','image-ready','ready')) { throw 'HYPERV_CHECKPOINT_ACTIVE' }
    Set-VM -VM $current -AutomaticCheckpointsEnabled $false
    $after = Get-VM -Id $current.Id
    if ($after.AutomaticCheckpointsEnabled) { throw 'VM_CHECKPOINT_POLICY' }
    return 'disabled'
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
    if ((Get-VM -Id $Vm.Id).AutomaticCheckpointsEnabled) {
        Set-VM -VM $Vm -AutomaticCheckpointsEnabled $false
        if ((Get-VM -Id $Vm.Id).AutomaticCheckpointsEnabled) { throw 'VM_CHECKPOINT_POLICY' }
    }
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
function New-HyperVUnattendXml([string]$UserName, [string]$Password, [string]$NetworkSource, [string]$TimeZoneId, [string]$UiLanguage = '') {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Password + 'Password'))
    $name = [Security.SecurityElement]::Escape($UserName)
    $tz = if ($NetworkSource -ne '稍后配置' -and $TimeZoneId) { '<TimeZone>' + [Security.SecurityElement]::Escape($TimeZoneId) + '</TimeZone>' } else { '' }
    $langBlock = ''
    if ($UiLanguage -and $UiLanguage -match '^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})+$') {
        $langEsc = [Security.SecurityElement]::Escape($UiLanguage)
        $langBlock = "<component name=`"Microsoft-Windows-International-Core`" processorArchitecture=`"amd64`" publicKeyToken=`"31bf3856ad364e35`" language=`"neutral`" versionScope=`"nonSxS`"><InputLocale>$langEsc</InputLocale><SystemLocale>$langEsc</SystemLocale><UILanguage>$langEsc</UILanguage><UserLocale>$langEsc</UserLocale></component>"
    }
    return @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
 <settings pass="specialize"><component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><ComputerName>ClaudeWorkspace</ComputerName>$tz</component></settings>
 <settings pass="oobeSystem">$langBlock<component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
  <OOBE><HideOnlineAccountScreens>true</HideOnlineAccountScreens><HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE></OOBE>
  <UserAccounts><LocalAccounts><LocalAccount wcm:action="add"><Password><Value>$encoded</Value><PlainText>false</PlainText></Password><Name>$name</Name><Group>Administrators</Group></LocalAccount></LocalAccounts></UserAccounts>
  <AutoLogon><Password><Value>$encoded</Value><PlainText>false</PlainText></Password><Enabled>true</Enabled><LogonCount>1</LogonCount><Username>$name</Username></AutoLogon>
  <FirstLogonCommands><SynchronousCommand wcm:action="add"><Order>1</Order><Description>Prepare local VM management</Description><CommandLine>C:\ProgramData\ClaudeBootstrap\pwsh\pwsh.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File C:\ProgramData\ClaudeBootstrap\Initialize.ps1</CommandLine></SynchronousCommand></FirstLogonCommands>
 </component></settings>
</unattend>
"@
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
        $uiLang = @($image.Languages | Where-Object { $_ -match '^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})+$' }) | Select-Object -First 1
        if (-not $uiLang -and [string]$o.IsoPath -match '(?i)zh[-_]cn') { $uiLang = 'zh-CN' }
        $script:windowsInstallStage='apply-image'
        Apply-WindowsImageWithProgress $source $image.ImageIndex $target
        $script:windowsInstallStage='bootstrap'
        $bootstrap=Join-Path $target 'ProgramData\ClaudeBootstrap';New-Item -ItemType Directory -Path $bootstrap -Force | Out-Null
        $psArchive=Join-Path $o.Root ('downloads\'+[string]$Data.pwshFile)
        Expand-Archive -LiteralPath $psArchive -DestinationPath (Join-Path $bootstrap 'pwsh') -Force
        @{vmId=[string]$vm.Id;workspaceId=[string]$o.WorkspaceId} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bootstrap 'identity.json') -Encoding utf8NoBOM
        Copy-Item -LiteralPath ([string]$Data.bootstrapScript) -Destination (Join-Path $bootstrap 'Initialize.ps1')
        $xml = New-HyperVUnattendXml ([string]$o.UserName) ([string]$Data.password) ([string]$o.NetworkSource) ([string]$o.TimeZoneId) ([string]$uiLang)
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
if ($ProbeGuestStatus) {
    try {
        Import-Module Hyper-V -ErrorAction Stop
        $probeInput = [Console]::In.ReadLine() | ConvertFrom-Json
        $o = $probeInput.options
        $d = $probeInput.data
        if (-not $o -or -not $d) { Write-Output 'STATUS:StatusUnavailable'; exit 0 }
        $status = Invoke-GuestStatusProbeDirect $d
        Write-Output ('STATUS:' + [string]$status)
        exit 0
    } catch {
        Write-Output 'STATUS:SessionUnavailable'
        exit 0
    }
}
try {
    while($null -ne ($line=$reader.ReadLine())) {
        Reset-HyperVRequestDiagnostics
        $response=@{ok=$false;value='';code='HYPERV_STEP_FAILED'}
        try {
            $request=$line | ConvertFrom-Json;$d=$request.data
            if($request.operation -eq 'exit'){break}
            if($request.operation -eq 'init') {
                $script:hypervSubstage='init-options'
                $o=$d.options
                $verificationOnly=[bool]$d.verificationOnly
                $displayOnly=[bool]$d.displayOnly
                $spawnerOnly=[bool]$SpawnerOnly -or [bool]$d.spawnerOnly
                if(-not $o -or $o.PlatformId -ne 'hyperv' -or $o.VmName -notmatch '^[a-zA-Z0-9_-]{1,50}$' -or -not [IO.Path]::IsPathFullyQualified($o.Root) -or $o.Root.StartsWith('\\') -or ([IO.Path]::GetPathRoot($o.Root).TrimEnd('\') -eq $o.Root.TrimEnd('\'))){throw 'VM_SCOPE'}
                $null=Assert-LocalPath (Join-Path $o.Root 'machines') $o.Root
                $script:sharedState['root']=[string]$o.Root
                $value='initialized'
            } else {
                if(-not $o){throw 'VM_SCOPE'}
                if($verificationOnly -and $request.operation -ne 'boundary-verify'){throw 'UNSUPPORTED_OPERATION'}
                if($displayOnly -and $request.operation -ne 'sync-display'){throw 'UNSUPPORTED_OPERATION'}
                if($spawnerOnly -and $request.operation -ne 'launch-verifier'){throw 'UNSUPPORTED_OPERATION'}
                if($request.operation -eq 'prepare') {
                    $script:hypervSubstage='edition-check'
                    $edition=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
                    if($edition -notmatch '^(Professional|Enterprise|Education)'){throw 'HYPERV_EDITION'}
                    $script:hypervSubstage='query-feature'
                    try {
                        $feature=Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -ErrorAction Stop
                    } catch {
                        Save-HyperVErrorContext $_
                        $hr=[int]$_.Exception.GetBaseException().HResult
                        if($hr -eq -2146498548){throw 'HYPERV_FEATURE_NOT_FOUND'}
                        throw 'HYPERV_FEATURE_QUERY_FAILED'
                    }
                    if(-not $feature){throw 'HYPERV_FEATURE_NOT_FOUND'}
                    if($feature.State -in @('EnablePending','DisablePending')){throw 'HYPERV_RESTART'}
                    if($feature.State -ne 'Enabled') {
                        $script:hypervSubstage='enable-feature'
                        try {
                            Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All -NoRestart -ErrorAction Stop | Out-Null
                        } catch {
                            Save-HyperVErrorContext $_
                            $hr=[int]$_.Exception.GetBaseException().HResult
                            if($hr -eq -2146498548){throw 'HYPERV_FEATURE_NOT_FOUND'}
                            throw
                        }
                        throw 'HYPERV_RESTART'
                    }
                    $script:hypervSubstage='import-module'
                    try { Import-Module Hyper-V -ErrorAction Stop } catch { Save-HyperVErrorContext $_; throw 'HYPERV_MODULE_UNAVAILABLE' }
                    $script:hypervSubstage='check-vmms'
                    $service = Get-Service vmms -ErrorAction SilentlyContinue
                    if (!$service) { throw 'HYPERV_VMMS_MISSING' }
                    if ($service.StartType -eq 'Disabled') { throw 'HYPERV_VMMS_DISABLED' }
                    if ($service.Status -ne 'Running') { throw 'HYPERV_VMMS_STOPPED' }
                    $value='ready'
                } else {
                    $script:hypervSubstage='import-module'
                    Import-Module Hyper-V -ErrorAction Stop
                    switch($request.operation) {
                        'ensure' {
                            $script:hypervSubstage='resolve-vm'
                            $value=Resolve-OrCreateTaskVm $d
                        }
                        'windows-install-state' {
                            $vm=Target-VM $d
                            $value=Get-WindowsInstallProbe $vm
                        }
                        'windows-install-diagnose' {
                            $vm=Target-VM $d
                            $value=(Get-WindowsInstallDiagnostics $vm) | ConvertTo-Json -Compress
                        }
                        'ensure-checkpoint-policy' {
                            $vm=Target-VM $d
                            $value=Disable-TaskVmAutomaticCheckpoints $vm
                        }
                        'configure' {
                            $script:hypervSubstage='target-vm'
                            $vm=Target-VM $d;if($vm.State -ne 'Off'){throw 'VM_SCOPE'}
                            $script:hypervSubstage='remove-nics'
                            Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter
                            $script:hypervSubstage='guard-console'
                            Guard-Console $vm
                            $script:hypervSubstage='disable-metadata'
                            Disable-HostMetadata $vm
                            $script:hypervSubstage='set-processor'
                            Set-VMProcessor -VM $vm -Count ([int]$o.CpuCount)
                            $script:hypervSubstage='set-memory'
                            Set-VMMemory -VM $vm -DynamicMemoryEnabled $false -StartupBytes ([long]$o.MemoryMb*1MB)
                            $script:hypervSubstage='set-firmware'
                            Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
                            $script:hypervSubstage='enable-vtpm'
                            Enable-TaskVirtualTpm $vm
                            $script:hypervSubstage='verify-security'
                            if(-not (Get-VMSecurity -VM $vm).TpmEnabled -or [string](Get-VMFirmware -VM $vm).SecureBoot -ne 'On'){throw 'VM_SECURITY'}
                            $script:hypervSubstage='set-vm-options'
                            Set-VM -VM $vm -AutomaticStartAction Nothing -AutomaticStopAction ShutDown -AutomaticCheckpointsEnabled $false
                            if((Get-VM -Id $vm.Id).AutomaticCheckpointsEnabled){throw 'VM_CHECKPOINT_POLICY'}
                            try{Set-VM -VM $vm -EnhancedSessionTransportType HvSocket -ErrorAction SilentlyContinue}catch{}
                            $value='configured'
                        }
                        'launch-verifier' {
                            if(([string]$d.pipeName) -notmatch '^ClaudeSetup-[a-f0-9]{32}$'){throw 'VM_SCOPE'}
                            $scriptFile=Assert-LocalPath ([string]$d.scriptPath) (Join-Path $o.Root 'private')
                            if((Get-FileHash -LiteralPath $scriptFile -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash){throw 'VM_SCOPE'}
                            $launchAsSpawner=(-not $spawnerOnly) -and [bool]$d.spawnerOnly
                            $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'));$start.UseShellExecute=$false;$start.CreateNoWindow=$true
                            $childArgs=@('-NoLogo','-NoProfile','-File',$scriptFile,'-PipeName',[string]$d.pipeName)
                            if($launchAsSpawner){$childArgs+='-SpawnerOnly'}
                            foreach($a in $childArgs){$start.ArgumentList.Add($a)}
                            $process=[Diagnostics.Process]::Start($start);if($process){Register-SupervisedChild $process};$value=[string]$process.Id
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
                        'set-clipboard' {
                            $vm=Target-VM $d
                            $want=[bool]$d.enabled
                            if($null -eq $o.PSObject.Properties['SharedClipboardEnabled']){Add-Member -InputObject $o -MemberType NoteProperty -Name SharedClipboardEnabled -Value $want -Force}
                            else{$o.SharedClipboardEnabled=$want}
                            $o.HyperVBasicConsoleConfirmed=(-not $want)
                            $value=Invoke-ConsolePolicyLock {
                                $root='HKLM:\SOFTWARE\Claudome\HyperVConsolePolicy';$current=[bool](Get-VMHost).EnableEnhancedSessionMode
                                if(-not(Test-Path -LiteralPath $root)){New-Item -Path $root -Force | Out-Null;New-ItemProperty -Path $root -Name OriginalEnabled -PropertyType DWord -Value ([int]$current) | Out-Null}
                                $owner=Console-Owner $vm
                                if(Test-Path -LiteralPath $owner){if((Get-ItemPropertyValue -LiteralPath $owner -Name WorkspaceId) -ne $o.WorkspaceId){throw 'VM_SCOPE'}}
                                else{New-Item -Path $owner -Force | Out-Null;New-ItemProperty -Path $owner -Name WorkspaceId -Value ([string]$o.WorkspaceId) -PropertyType String | Out-Null}
                                if($want){Set-VM -VM $vm -EnhancedSessionTransportType HvSocket}
                                $vmRead=Get-VM -Id $vm.Id
                                $hvSocket=([string]$vmRead.EnhancedSessionTransportType -eq 'HvSocket')
                                if($want -and -not $hvSocket){throw 'HYPERV_CONSOLE_POLICY'}
                                if($current -ne $want){Set-VMHost -EnableEnhancedSessionMode $want}
                                $hostEnhanced=[bool](Get-VMHost).EnableEnhancedSessionMode
                                if($hostEnhanced -ne $want){throw 'HYPERV_CONSOLE_POLICY'}
                                @{hostEnhanced=$hostEnhanced;hvSocket=$hvSocket} | ConvertTo-Json -Compress
                            }
                        }
                        'clipboard-status' {
                            $vm=Target-VM $d
                            $hostEnhanced=[bool](Get-VMHost).EnableEnhancedSessionMode
                            $hvSocket=([string]$vm.EnhancedSessionTransportType -eq 'HvSocket')
                            $value=@{'hostEnhanced'=$hostEnhanced;'hvSocket'=$hvSocket} | ConvertTo-Json -Compress
                        }
                        'sync-display' {
                            $vm=Target-VM $d
                            $targetW=[Math]::Clamp([int]$d.width,320,7680)
                            $targetH=[Math]::Clamp([int]$d.height,240,4320)
                            $session=$null
                            try {
                                $session=Guest-Session $d
                                $value=[string](Invoke-Command -Session $session -ScriptBlock {
                                    param($user,$vmId,$workspaceId,$tw,$th)
                                    $ErrorActionPreference='Stop'
                                    if($user -notmatch '^[a-zA-Z][a-zA-Z0-9_-]{0,19}$' -or $vmId -notmatch '^[a-fA-F0-9-]{36}$' -or $workspaceId -notmatch '^[a-fA-F0-9]{32}$'){throw 'VM_SCOPE'}
                                    $sys=Get-CimInstance Win32_ComputerSystem
                                    $idDoc=Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
                                    if($sys.Manufacturer -ne 'Microsoft Corporation' -or $sys.Model -ne 'Virtual Machine' -or $idDoc.vmId -ne $vmId -or $idDoc.workspaceId -ne $workspaceId){throw 'VM_SCOPE'}
                                    $pwshExe='C:\Users\'+$user+'\AppData\Local\ClaudeEnvironmentSetup\pwsh\pwsh.exe'
                                    if(-not [IO.File]::Exists($pwshExe)){$pwshExe=Join-Path $PSHOME 'pwsh.exe'}
                                    $runId=[guid]::NewGuid().ToString('N')
                                    $bundle='C:\ProgramData\ClaudeBootstrap\display-'+$runId
                                    $task='ClaudomeDisplay-'+$runId
                                    # Reclaim any stale display tasks/bundles from previously interrupted syncs.
                                    Get-ScheduledTask -TaskName 'ClaudomeDisplay-*' -ErrorAction SilentlyContinue | ForEach-Object {
                                        if($_.State -ne 'Running'){Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue}
                                    }
                                    Get-ChildItem -LiteralPath 'C:\ProgramData\ClaudeBootstrap' -Directory -Filter 'display-*' -ErrorAction SilentlyContinue | ForEach-Object {
                                        if($_.LastWriteTimeUtc -lt [datetime]::UtcNow.AddMinutes(-2)){Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue}
                                    }
                                    New-Item -ItemType Directory -Path $bundle -Force | Out-Null
                                    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
                                    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
                                    Set-Acl -LiteralPath $bundle -AclObject $acl
                                    $scriptFile=Join-Path $bundle 'sync.ps1'
                                    $resultFile=Join-Path $bundle 'result.json'
                                    $inFile=Join-Path $bundle 'req.json'
                                    @{width=$tw;height=$th} | ConvertTo-Json -Compress | Set-Content -LiteralPath $inFile -Encoding utf8NoBOM
                                    @'
$ErrorActionPreference='Stop'
$req=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'req.json') -Raw | ConvertFrom-Json
$cs = "using System; using System.Runtime.InteropServices; namespace Claudome { public static class GuestDisplaySync { [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct DEVMODE { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName; public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public uint dmFields; public int dmPositionX, dmPositionY; public uint dmDisplayOrientation, dmDisplayFixedOutput; public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName; public ushort dmLogPixels; public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency; public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight; } [DllImport(""user32.dll"", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode); [DllImport(""user32.dll"", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettingsExW(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam); } }"
if (-not ('Claudome.GuestDisplaySync' -as [type])) { Add-Type -TypeDefinition $cs }
$dm=[Claudome.GuestDisplaySync+DEVMODE]::new()
$dm.dmSize=[uint16][Runtime.InteropServices.Marshal]::SizeOf([type][Claudome.GuestDisplaySync+DEVMODE])
$before=''
if([Claudome.GuestDisplaySync]::EnumDisplaySettingsW($null,-1,[ref]$dm)){$before="$($dm.dmPelsWidth)x$($dm.dmPelsHeight)"}
$modes=[Collections.Generic.List[object]]::new()
$seen=[Collections.Generic.HashSet[string]]::new()
for($i=0;$i -lt 512;$i++){
    $iter=[Claudome.GuestDisplaySync+DEVMODE]::new();$iter.dmSize=$dm.dmSize
    if(-not [Claudome.GuestDisplaySync]::EnumDisplaySettingsW($null,$i,[ref]$iter)){break}
    if($iter.dmBitsPerPel -ne 32 -or $iter.dmPelsWidth -lt 800){continue}
    $k="$($iter.dmPelsWidth)x$($iter.dmPelsHeight)"
    if($seen.Add($k)){$modes.Add([pscustomobject]@{width=[int]$iter.dmPelsWidth;height=[int]$iter.dmPelsHeight})}
}
$best=$null;$bestScore=-1.0
foreach($m in $modes){
    foreach($z in @(1.0,0.75,0.5,0.25)){
        $sw=[double]$m.width*$z;$sh=[double]$m.height*$z
        if($sw -le [double]$req.width -and $sh -le [double]$req.height){
            $fill=($sw*$sh)/([double]$req.width*[double]$req.height)
            $wide=if($m.width/[double]$m.height -ge 1.5){0.12}else{0.0}
            $score=$fill+$wide+($z*0.04)
            if($score -gt $bestScore){$bestScore=$score;$best=[pscustomobject]@{width=$m.width;height=$m.height;zoom=$z}}
        }
    }
}
$applied='';$selectedZoom=if($best){[double]$best.zoom}else{0.5}
if($best -and $before -ne "$($best.width)x$($best.height)"){
    for($i=0;$i -lt 512;$i++){
        $cand=[Claudome.GuestDisplaySync+DEVMODE]::new();$cand.dmSize=$dm.dmSize
        if(-not [Claudome.GuestDisplaySync]::EnumDisplaySettingsW($null,$i,[ref]$cand)){break}
        if($cand.dmBitsPerPel -eq 32 -and $cand.dmPelsWidth -eq $best.width -and $cand.dmPelsHeight -eq $best.height){
            $cand.dmFields=0x180000
            if([Claudome.GuestDisplaySync]::ChangeDisplaySettingsExW($null,[ref]$cand,[IntPtr]::Zero,2,[IntPtr]::Zero) -eq 0){
                if([Claudome.GuestDisplaySync]::ChangeDisplaySettingsExW($null,[ref]$cand,[IntPtr]::Zero,1,[IntPtr]::Zero) -eq 0){$applied="$($best.width)x$($best.height)"}
            }
            break
        }
    }
}
$after=$before;$adm=[Claudome.GuestDisplaySync+DEVMODE]::new();$adm.dmSize=$dm.dmSize
if([Claudome.GuestDisplaySync]::EnumDisplaySettingsW($null,-1,[ref]$adm)){$after="$($adm.dmPelsWidth)x$($adm.dmPelsHeight)"}
@{before=$before;after=$after;applied=$applied;selectedZoom=$selectedZoom;modes=@($modes | ForEach-Object {"$($_.width)x$($_.height)"})} | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'result.json') -Encoding utf8NoBOM
'@ | Set-Content -LiteralPath $scriptFile -Encoding utf8NoBOM
                                    $registered=$false
                                    try {
                                        $action=New-ScheduledTaskAction -Execute $pwshExe -Argument ('-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File "'+$scriptFile+'"') -WorkingDirectory $bundle
                                        $principal=New-ScheduledTaskPrincipal -UserId ($env:COMPUTERNAME+'\'+$user) -LogonType Interactive -RunLevel Highest
                                        $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromSeconds(15)) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
                                        Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null;$registered=$true
                                        Start-ScheduledTask -TaskName $task
                                        $deadline=[datetime]::UtcNow.AddSeconds(12)
                                        while([datetime]::UtcNow -lt $deadline){
                                            if([IO.File]::Exists($resultFile)){return [IO.File]::ReadAllText($resultFile)}
                                            Start-Sleep -Milliseconds 200
                                        }
                                        throw 'GUEST_LOGIN_REQUIRED'
                                    } finally {
                                        if($registered){
                                            Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
                                            Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
                                        }
                                        Remove-Item -LiteralPath $bundle -Recurse -Force -ErrorAction SilentlyContinue
                                    }
                                } -ArgumentList ([string]$o.UserName),([string]$vm.Id),([string]$o.WorkspaceId),$targetW,$targetH)
                            } finally { if($session){Remove-PSSession $session} }
                        }
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
                        'ready' { $value = Get-HyperVGuestStatus $d }
                        'repair-guest-session' {
                            $vm = Target-VM $d
                            Assert-NoNetworkAdapter $vm
                            try { $session = Guest-Session $d -Default }
                            catch { $value = Get-GuestSessionFailure $_; break }
                            try {
                                $value = Read-GuestBootstrap $session ([string]$vm.Id) ([string]$o.WorkspaceId) $false
                                if ($value -ne 'EndpointUnavailable' -and $value -notlike 'Failed*') { break }
                                $value = [string](Invoke-Command -Session $session -ScriptBlock {
                                    param($bootstrap)
                                    $pwsh = 'C:\ProgramData\ClaudeBootstrap\pwsh\pwsh.exe'
                                    if (!(Test-Path -LiteralPath $pwsh -PathType Leaf)) { return 'BootstrapMissing' }
                                    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))
                                    & $pwsh -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand $encoded *> $null
                                    if ($LASTEXITCODE -eq 0) { return 'EndpointUnavailable' }
                                    if ($LASTEXITCODE -eq 2) { return 'Initializing' }
                                    return 'RepairFailed'
                                } -ArgumentList ([string]$d.bootstrap))
                                if ($value -eq 'RepairFailed') {
                                    $status = Read-GuestBootstrap $session ([string]$vm.Id) ([string]$o.WorkspaceId) $false
                                    if ($status -like 'Failed*') { $value = $status }
                                }
                            } finally { Remove-PSSession $session }
                        }
                        {$_ -in @('guest','copy-to','copy-from','file-exists')} {
                            $session=$null
                            $script:copyStage = 'session'
                            try {
                                if ($_ -eq 'copy-to' -and $d.sha256) {
                                    $script:copyStage = 'source'
                                    $source = Assert-LocalPath ([string]$d.source) $o.Root
                                    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw 'GUEST_COPY_SOURCE' }
                                    Send-VerifiedGuestResource $d $source
                                    $value = 'copied'
                                    break
                                }
                                $session=Guest-Session $d
                                if($_ -eq 'file-exists'){
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
                                    $script:copyStage = 'source'
                                    $source=Assert-LocalPath ([string]$d.source) $o.Root
                                    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw 'GUEST_COPY_SOURCE' }
                                    $script:copyStage = 'transfer'
                                    Copy-Item -LiteralPath $source -Destination ([string]$d.destination) -ToSession $session -Force
                                    $value='copied'
                                }else {
                                    $script:copyStage = 'destination'
                                    $dest=Assert-LocalPath ([string]$d.destination) $o.Root
                                    $script:copyStage = 'transfer'
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
            $response = Format-HyperVErrorResponse $request $_
        }
        if($null -ne $request -and $null -ne $request.id -and [string]$request.id -ne ''){$response['id']=[string]$request.id}
        $writer.WriteLine(($response | ConvertTo-Json -Compress -Depth 4))
        $request=$null;$d=$null
    }
}finally{$script:recoveryStop.Value=$true;if($script:recoveryPs){try{$script:recoveryPs.Stop();$script:recoveryPs.Dispose()}catch{}};Stop-SupervisedChildren;if($writer){$writer.Dispose()};if($reader){$reader.Dispose()};if($pipe){$pipe.Dispose()}}
