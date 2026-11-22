# Loaded only by Configure.ps1 after target-VM identity verification.
function Clear-StaleClipboardState([string]$BaseDir, [string]$ActiveRunId = '', [int]$MaxAgeSeconds = 120, [DateTime]$NowUtc = [DateTime]::UtcNow) {
    if (-not [IO.Directory]::Exists($BaseDir)) { return }
    $baseInfo = [IO.DirectoryInfo]::new($BaseDir)
    if (($baseInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
    $cutoff = $NowUtc.AddSeconds(-$MaxAgeSeconds)
    foreach ($dir in $baseInfo.EnumerateDirectories('clipboard-*')) {
        if ($dir.Name -notmatch '^clipboard-([a-f0-9]{32})$') { continue }
        $id = $Matches[1]
        if ($id -eq $ActiveRunId -or ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        $latest = $dir.LastWriteTimeUtc
        $unsafeEntry = $false
        $files = @()
        try {
            foreach ($entry in $dir.EnumerateFileSystemInfos()) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $entry -is [IO.DirectoryInfo] -or
                    $entry.Name -notin @('request.json', 'result.json', 'result.json.new', 'ClipboardTransfer.ps1')) {
                    $unsafeEntry = $true
                    break
                }
                if ($entry.LastWriteTimeUtc -gt $latest) { $latest = $entry.LastWriteTimeUtc }
                $files += $entry.FullName
            }
        } catch { continue }
        if ($unsafeEntry -or $latest -gt $cutoff) { continue }
        $staleTask = 'ClaudomeClipboard-' + $id
        Stop-ScheduledTask -TaskName $staleTask -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $staleTask -Confirm:$false -ErrorAction SilentlyContinue
        try {
            foreach ($file in $files) {
                if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
            }
            [IO.Directory]::Delete($dir.FullName, $false)
        } catch { }
    }
    foreach ($task in @(Get-ScheduledTask -TaskName 'ClaudomeClipboard-*' -ErrorAction SilentlyContinue)) {
        if ($task.TaskName -notmatch '^ClaudomeClipboard-([a-f0-9]{32})$') { continue }
        $id = $Matches[1]
        if ($id -eq $ActiveRunId -or $task.State -eq 'Running') { continue }
        $taskFolder = Join-Path $BaseDir ('clipboard-' + $id)
        if (-not [IO.Directory]::Exists($taskFolder) -or ([IO.DirectoryInfo]::new($taskFolder)).LastWriteTimeUtc -le $cutoff) {
            Unregister-ScheduledTask -TaskName $task.TaskName -Confirm:$false -ErrorAction SilentlyContinue
        }
    }
}
function Read-LocalYaml([string]$Path) {
    if(-not(Test-Path -LiteralPath $Path)){return @{}}
    if((Get-Item -LiteralPath $Path).Length -gt 8MB){throw 'CLASH_CONFIG_REVIEW_REQUIRED'}
    $reader=[IO.StringReader]::new([IO.File]::ReadAllText($Path))
    try{
        $parser=[YamlDotNet.Serialization.DeserializerBuilder]::new().WithAttemptingUnquotedStringTypeDeserialization().Build()
        $raw=$parser.Deserialize[object]($reader)
        if($null -ne $raw -and $raw -isnot [Collections.IDictionary]){throw 'CLASH_CONFIG_REVIEW_REQUIRED'}
    }catch{throw 'CLASH_CONFIG_REVIEW_REQUIRED'}
    finally{$reader.Dispose()}
    $result=@{}
    foreach($key in $raw.Keys){$result[[string]$key]=$raw[$key]}
    return $result
}
function Write-ClashConfigText([string]$Path,[string]$Text) {
    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try{
        [IO.Directory]::CreateDirectory((Split-Path $Path)) | Out-Null
        [IO.File]::WriteAllText($temporary,$Text,[Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary,$Path,$true)
    }catch{throw 'CLASH_CONFIG_WRITE_FAILED'}
    finally{
        if([IO.File]::Exists($temporary)){
            try{[IO.File]::Delete($temporary)}catch{Write-Warning '本次配置的临时文件待清理。'}
        }
    }
}
function Get-GuestVcRuntimeVersion {
    $keys=@(
        'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
    )
    Get-ItemProperty -LiteralPath $keys -ErrorAction SilentlyContinue | ForEach-Object {
        $version=$null
        if($_.Installed -eq 1 -and [version]::TryParse(([string]$_.Version).TrimStart('v','V'),[ref]$version)){$version}
    } | Sort-Object -Descending | Select-Object -First 1
}
function Test-GuestVcRuntimeFiles {
    return ((Test-Path -LiteralPath (Join-Path $env:SystemRoot 'System32\vcruntime140.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $env:SystemRoot 'System32\vcruntime140_1.dll') -PathType Leaf))
}
function Ensure-GuestVcRuntime {
    $resources=@($payload.resources | Where-Object id -eq 'vcredist')
    if($resources.Count -ne 1){throw 'VC_RUNTIME_RESOURCE_MISSING'}
    $installer=Join-Path (Split-Path $PayloadPath) $resources[0].fileName
    if(-not(Test-Path -LiteralPath $installer -PathType Leaf)){throw 'VC_RUNTIME_RESOURCE_MISSING'}
    $versionInfo=(Get-Item -LiteralPath $installer).VersionInfo
    $minimum=[version]::new($versionInfo.ProductMajorPart,$versionInfo.ProductMinorPart,$versionInfo.ProductBuildPart,$versionInfo.ProductPrivatePart)
    $installed=Get-GuestVcRuntimeVersion
    if($installed -ge $minimum -and (Test-GuestVcRuntimeFiles)){
        Write-Host '来宾微软 VC++ x64 运行库已就绪。'
        return
    }
    if($installed -gt $minimum){throw 'VC_RUNTIME_READBACK_FAILED'}
    if(Get-Process -Name 'vc_redist.x64' -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $installer}){
        throw 'VC_RUNTIME_INSTALL_WAITING'
    }
    Write-Host '正在安装或修复来宾微软 VC++ x64 运行库…'
    $mode=if($installed -eq $minimum){'/repair'}else{'/install'}
    try{$process=Start-Process -FilePath $installer -ArgumentList $mode,'/quiet','/norestart' -PassThru}
    catch{throw 'VC_RUNTIME_INSTALL_FAILED'}
    try{
        if(-not $process.WaitForExit(300000)){throw 'VC_RUNTIME_INSTALL_WAITING'}
        $code=$process.ExitCode
    }finally{$process.Dispose()}
    if($code -in 1641,3010){
        $report.installerExitCode=$code
        throw 'VC_RUNTIME_RESTART_REQUIRED'
    }
    if($code -notin 0,1638){
        $report.installerExitCode=$code
        throw 'VC_RUNTIME_INSTALL_FAILED'
    }
    if((Get-GuestVcRuntimeVersion) -lt $minimum -or -not(Test-GuestVcRuntimeFiles)){
        $report.installerExitCode=$code
        throw 'VC_RUNTIME_READBACK_FAILED'
    }
    Write-Host '来宾微软 VC++ x64 运行库回读通过。'
}
function Test-GuestAppInstalled([string]$Id,[string]$Pattern) {
    $existing=@(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object {$_.DisplayName -match $Pattern})
    if(-not $existing.Count){return $false}
    if($Id -eq 'clash'){try{$null=Get-ClashExecutable;return $true}catch{return $false}}
    if($Id -eq 'adspower'){
        $paths=@((Join-Path $env:LOCALAPPDATA 'Programs\AdsPower Global\AdsPower Global.exe'),(Join-Path $env:ProgramFiles 'AdsPower Global\AdsPower Global.exe'))
        if(${env:ProgramFiles(x86)}){$paths+=Join-Path ${env:ProgramFiles(x86)} 'AdsPower Global\AdsPower Global.exe'}
        return [bool]($paths | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf} | Select-Object -First 1)
    }
    return $false
}
function Install-GuestApp([string]$Id,[string]$Pattern) {
    if(Test-GuestAppInstalled $Id $Pattern){
        Write-Host "$Pattern 已安装，继续使用现有版本。"
        return
    }
    Write-Host "正在安装 $Pattern…"
    $resource=$payload.resources | Where-Object id -eq $Id
    if(-not $resource){throw 'GUEST_RESOURCE_MISSING'}
    $installer=Join-Path (Split-Path $PayloadPath) $resource.fileName
    $process=Start-Process -FilePath $installer -ArgumentList '/S' -PassThru
    if(-not $process.WaitForExit(300000)){throw 'APPLICATION_INSTALL_WAITING'}
    if($process.ExitCode -ne 0){throw 'APPLICATION_INSTALL_FAILED'}
    if(-not(Test-GuestAppInstalled $Id $Pattern)){throw 'APPLICATION_READBACK_FAILED'}
    Write-Host "$Pattern 安装回读通过。"
}
function Get-ClashExecutable {
    $candidates=@((Join-Path $env:ProgramFiles 'Clash Verge\clash-verge.exe'),(Join-Path $env:ProgramFiles 'Clash Verge\Clash Verge.exe'),(Join-Path $env:LOCALAPPDATA 'Programs\Clash Verge\clash-verge.exe'))
    foreach($path in $candidates){if(Test-Path -LiteralPath $path){return $path}}
    throw 'CLASH_EXECUTABLE_MISSING'
}
function Get-ClashServiceRoot {
    return (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'clash-verge-service')
}
function Test-InstalledClashService([string]$ServicePath,[string]$ServiceHash,[string]$CorePath,[string]$CoreHash) {
    $registered=Get-CimInstance Win32_Service -Filter "Name='clash_verge_service'"
    $expectedCommand='^\s*"?'+[regex]::Escape($ServicePath)+'"?(?:\s|$)'
    return ($registered.PathName -match $expectedCommand -and
        (Test-Path -LiteralPath $ServicePath -PathType Leaf) -and (Get-FileHash -LiteralPath $ServicePath -Algorithm SHA256).Hash -eq $ServiceHash -and
        (Test-Path -LiteralPath $CorePath -PathType Leaf) -and (Get-FileHash -LiteralPath $CorePath -Algorithm SHA256).Hash -eq $CoreHash)
}
function ConvertTo-ClashLocalPath([string]$Path) {
    if($Path.StartsWith('\\?\') -and $Path.Substring(4) -match '^[A-Za-z]:\\'){$Path=$Path.Substring(4)}
    if($Path -notmatch '^[A-Za-z]:\\'){throw 'RUNTIME_CONFIG_OUT_OF_SCOPE'}
    return [IO.Path]::GetFullPath($Path)
}
function Get-VerifiedClashListener {
    $listeners=@(Get-NetTCPConnection -State Listen -LocalPort 17897 -ErrorAction SilentlyContinue)
    if(-not $listeners.Count){throw 'CLASH_CORE_NOT_READY'}
    if(@($listeners | Where-Object {$_.LocalAddress -notin @('127.0.0.1','::1')}).Count){throw 'PROXY_PORT_COLLISION'}
    $owners=@($listeners.OwningProcess | Sort-Object -Unique)
    if($owners.Count -ne 1){throw 'PROXY_PORT_COLLISION'}
    $process=Get-Process -Id $owners[0] -ErrorAction SilentlyContinue
    if(-not $process){throw 'CLASH_CORE_NOT_READY'}
    $expected=Join-Path (Get-ClashServiceRoot) 'cores\verge-mihomo.exe'
    if(-not $process.Path -or (ConvertTo-ClashLocalPath $process.Path) -ne $expected){throw 'CLASH_SERVICE_CORE_REQUIRED'}
    $bundled=Join-Path (Split-Path $script:ClashExe) 'verge-mihomo.exe'
    if((Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $bundled -Algorithm SHA256).Hash){
        throw 'CLASH_CORE_HASH_MISMATCH'
    }
    return $process
}
function Stop-Core {
    if($script:ClashExe){Get-Process -Name 'clash-verge','Clash Verge' -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $script:ClashExe} | Stop-Process -Force}
    Stop-Service -Name 'clash_verge_service' -ErrorAction SilentlyContinue
    if($script:CoreExe){Get-Process -Name 'verge-mihomo','verge-mihomo-alpha' -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $script:CoreExe} | Stop-Process -Force}
    Start-Sleep -Seconds 2
}
function Start-Core {
    try{
        $service=Get-Service -Name 'clash_verge_service' -ErrorAction Stop
        if($service.Status -eq 'Stopped'){Start-Service -InputObject $service -ErrorAction Stop}
        $service.WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
    }catch{throw 'CLASH_SERVICE_START_FAILED'}
    if(-not(Get-Process -Name 'clash-verge','Clash Verge' -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $script:ClashExe})){
        try{Start-Process -FilePath $script:ClashExe -WindowStyle Minimized -ErrorAction Stop | Out-Null}
        catch{throw 'CLASH_APP_LAUNCH_FAILED'}
    }
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do{
        try{
            $process=Get-VerifiedClashListener
            if(@(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {$_.Name -eq 'ClaudeTun' -and $_.Status -eq 'Up'}).Count -eq 1){
                $script:CoreExe=ConvertTo-ClashLocalPath $process.Path
                return
            }
            $failure='CLASH_TUN_REQUIRED'
        }catch{
            $failure=$_.Exception.Message
            if($failure -notin @('CLASH_CORE_NOT_READY','CLASH_SERVICE_CORE_REQUIRED')){throw}
        }
        Start-Sleep -Seconds 1
    }while($clock.Elapsed.TotalSeconds -lt 75)
    throw $failure
}
function Set-ClaudomeClashSettings([hashtable]$Verge,[hashtable]$Base,[string]$CoreConfig) {
    $selected=ConvertFrom-Json -InputObject $CoreConfig -AsHashtable
    if($selected['mixed-port'] -ne 17897 -or $selected['tun']['device'] -ne 'ClaudeTun'){throw 'CORE_CONFIG_INVALID'}
    $Verge['language']='zh';$Verge['enable_tun_mode']=$true;$Verge['enable_auto_launch']=$true;$Verge['enable_silent_start']=$true;$Verge['enable_system_proxy']=$false
    $Verge['verge_mixed_port']=17897;$Verge['clash_core']='verge-mihomo'
    $Verge['enable_external_controller']=$false
    # Clash Verge merges global config over the profile, including individual TUN keys.
    foreach($key in @('mixed-port','allow-lan','mode','ipv6','tun','dns')){$Base[$key]=$selected[$key]}
    if($selected.ContainsKey('interface-name')){$Base['interface-name']=$selected['interface-name']}
    else{$Base.Remove('interface-name')}
}
function Write-ClashConfiguration([string]$ClashHome,[string]$CoreConfig) {
    $profileFile=Join-Path $ClashHome 'profiles.yaml'
    $vergeFile=Join-Path $ClashHome 'verge.yaml'
    $baseFile=Join-Path $ClashHome 'config.yaml'
    $profiles=Read-LocalYaml $profileFile
    $verge=Read-LocalYaml $vergeFile
    $base=Read-LocalYaml $baseFile
    $uid='claude-environment-setup'
    if($null -ne $profiles['items'] -and $profiles['items'] -isnot [Collections.IList]){throw 'CLASH_CONFIG_REVIEW_REQUIRED'}
    $items=@($profiles['items'] | Where-Object { $null -ne $_ })
    if(@($items | Where-Object { $_ -isnot [Collections.IDictionary] }).Count){throw 'CLASH_CONFIG_REVIEW_REQUIRED'}
    $items=@($items | Where-Object {$_['uid'] -ne $uid})
    $items+=@{uid=$uid;type='local';name='Claude 专用';file=($uid+'.yaml');selected=@(@{name='Claude';now='SelectedNode'});updated=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()}
    $profiles['items']=$items
    $profiles['current']=$uid
    Set-ClaudomeClashSettings $verge $base $CoreConfig
    try{
        # JSON is valid YAML. PowerShell's serializer unwraps the values returned by its pipeline.
        $profilesText=ConvertTo-Json -InputObject $profiles -Depth 100 -WarningAction Stop
        $vergeText=ConvertTo-Json -InputObject $verge -Depth 100 -WarningAction Stop
        $baseText=ConvertTo-Json -InputObject $base -Depth 100 -WarningAction Stop
    }catch{throw 'CLASH_CONFIG_REVIEW_REQUIRED'}
    Write-ClashConfigText (Join-Path $ClashHome ('profiles\'+$uid+'.yaml')) $CoreConfig
    Write-ClashConfigText $vergeFile $vergeText
    Write-ClashConfigText $baseFile $baseText
    Write-ClashConfigText $profileFile $profilesText
}
function Set-ClaudomeClashStartup([string]$RecordPath,[string]$Executable) {
    $runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $name='ClaudomeClash'
    if(-not(Test-Path -LiteralPath $RecordPath)){
        $previous=$null
        $valueKind='String'
        if(Test-Path -LiteralPath $runKey){
            $key=Get-Item -LiteralPath $runKey -ErrorAction Stop
            try{
                # An existing Run key with no value for this app is the normal first-run state.
                $previous=$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if($null -ne $previous){
                    $valueKind=[string]$key.GetValueKind($name)
                    if($valueKind -notin @('String','ExpandString')){throw 'CLASH_STARTUP_VALUE_CONFLICT'}
                }
            }finally{$key.Dispose()}
        }
        $record=@{existed=($null -ne $previous);value=$previous;valueKind=$valueKind}
        Write-ClashConfigText $RecordPath (ConvertTo-Json -InputObject $record)
    }
    if(-not(Test-Path -LiteralPath $runKey)){New-Item -Path $runKey -ErrorAction Stop | Out-Null}
    New-ItemProperty -LiteralPath $runKey -Name $name -Value ('"'+$Executable+'"') -PropertyType String -Force -ErrorAction Stop | Out-Null
}
function Save-DisabledOutboundRules([string]$Path,$Rules) {
    $names=@()
    if(Test-Path -LiteralPath $Path){$names+=@(Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)}
    $names+=@($Rules | ForEach-Object {$_.Name})
    $names=@($names | Where-Object {$_} | Sort-Object -Unique)
    $temporary=$Path+'.new'
    ConvertTo-Json -InputObject $names | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
function Get-TaskUplink {
    if($payload.transportKind -eq 'bus'){
        $adapters=@(Get-NetAdapter | Where-Object {$_.Name -eq 'ClaudomeBridge' -and $_.Status -eq 'Up'})
    }else{
        $adapters=@(Get-NetAdapter | Where-Object {$_.InterfaceDescription -notmatch 'Mihomo|Wintun' -and $_.Name -ne 'ClaudeTun' -and $_.Status -eq 'Up'})
    }
    if($adapters.Count -ne 1){throw 'PHYSICAL_INTERFACE_AMBIGUOUS'}
    return $adapters[0]
}

function Set-TaskDhcpRule([string]$InterfaceAlias, [string]$DhcpServer) {
    $name = 'Claude guest DHCP'
    $group = 'ClaudeEnvironmentSetup'
    $rule = @{
        Direction='Outbound'; Action='Allow'; Enabled='True'; Profile='Any'
        Program=(Join-Path $env:SystemRoot 'System32\svchost.exe'); Service='Dhcp'
        Protocol='UDP'; LocalPort=68; RemotePort=67
        RemoteAddress=@($DhcpServer,'255.255.255.255')
        InterfaceAlias=[System.Management.Automation.WildcardPattern]::Escape($InterfaceAlias)
        ErrorAction='Stop'
    }
    $existing=@(Get-NetFirewallRule -PolicyStore PersistentStore -Group $group -ErrorAction SilentlyContinue | Where-Object DisplayName -eq $name)
    if($existing.Count -gt 0){
        $existing | Set-NetFirewallRule @rule | Out-Null
    }else{
        New-NetFirewallRule -Group $group -DisplayName $name @rule | Out-Null
    }
    $active=@(Get-NetFirewallRule -PolicyStore ActiveStore -Group $group -ErrorAction SilentlyContinue | Where-Object DisplayName -eq $name)
    $expected=@($rule.RemoteAddress | Sort-Object -Unique)
    if($active.Count -eq 0){throw 'VBOX_DHCP_RULE_FAILED'}
    foreach($entry in $active){
        $actual=@(($entry | Get-NetFirewallAddressFilter -ErrorAction Stop).RemoteAddress |
            ForEach-Object {$_ -replace '/(?:32|255\.255\.255\.255)$',''} | Sort-Object -Unique)
        $ports=$entry | Get-NetFirewallPortFilter -ErrorAction Stop
        $service=$entry | Get-NetFirewallServiceFilter -ErrorAction Stop
        $program=$entry | Get-NetFirewallApplicationFilter -ErrorAction Stop
        $aliases=@(($entry | Get-NetFirewallInterfaceFilter -ErrorAction Stop).InterfaceAlias)
        if($entry.Enabled -ne 'True' -or $entry.Direction -ne 'Outbound' -or $entry.Action -ne 'Allow' -or
           $ports.Protocol -notin @('UDP','17') -or $ports.LocalPort -ne '68' -or $ports.RemotePort -ne '67' -or
           $service.Service -ne 'Dhcp' -or $aliases.Count -ne 1 -or
           $aliases[0] -notin @($InterfaceAlias,$rule.InterfaceAlias) -or
           [Environment]::ExpandEnvironmentVariables([string]$program.Program) -ine $rule.Program -or
           ($actual -join ',') -cne ($expected -join ',')){
            throw 'VBOX_DHCP_RULE_FAILED'
        }
    }
}

function Test-VirtualBoxUplink([int]$InterfaceIndex) {
    $address = Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -IPAddress '198.19.250.10' -ErrorAction SilentlyContinue |
        Where-Object {$_.PrefixLength -eq 24 -and $_.AddressState -eq 'Preferred'}
    $route = Get-NetRoute -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -NextHop '198.19.250.1' -ErrorAction SilentlyContinue
    return [bool]($address -and $route)
}

function Repair-VirtualBoxUplink {
    $adapter = Get-TaskUplink
    $index = [int]$adapter.ifIndex
    try{Set-TaskDhcpRule $adapter.Name '198.19.250.1'}catch{throw 'VBOX_DHCP_RULE_FAILED'}
    if(Test-VirtualBoxUplink $index){return}

    $dhcp = @(Get-CimInstance Win32_NetworkAdapterConfiguration -Filter "InterfaceIndex=$index" -ErrorAction Stop)
    if($dhcp.Count -ne 1 -or -not $dhcp[0].DHCPEnabled){throw 'VBOX_DHCP_NOT_ENABLED'}
    try{$null=Invoke-CimMethod -InputObject $dhcp[0] -MethodName RenewDHCPLease -OperationTimeoutSec 15 -ErrorAction Stop}
    catch{Write-Host 'DHCP 续租调用未确认完成，正在回读来宾地址和默认路由。'}
    for($i=0; $i -lt 8; $i++){
        if(Test-VirtualBoxUplink $index){return}
        Start-Sleep -Seconds 1
    }
    throw 'VBOX_DHCP_LEASE_NOT_READY'
}

function Configure-Clash {
    $country=[Globalization.RegionInfo]::new([string]$payload.country)
    Set-WinHomeLocation -GeoId $country.GeoId
    $culture=@{
        'US'='en-US';'GB'='en-GB';'CA'='en-CA';'AU'='en-AU';'NZ'='en-NZ';'IE'='en-IE';
        'DE'='de-DE';'FR'='fr-FR';'NL'='nl-NL';'CH'='de-CH';'SE'='sv-SE';'IT'='it-IT';'ES'='es-ES';
        'JP'='ja-JP';'KR'='ko-KR';'SG'='en-SG';'HK'='zh-HK';'TW'='zh-TW';'MY'='en-MY';'TH'='th-TH';'IN'='en-IN';'BR'='pt-BR'
    }[[string]$payload.country]
    if(-not $culture){throw 'REGION_NOT_SUPPORTED'}
    Set-Culture -CultureInfo $culture;Set-TimeZone -Id ([string]$payload.timeZoneId)
    $actualCulture=& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -Command '(Get-Culture).Name'
    if((Get-TimeZone).Id -ne $payload.timeZoneId -or (Get-WinHomeLocation).GeoId -ne $country.GeoId -or $actualCulture.Trim() -ne $culture){throw 'REGION_READBACK_FAILED'}
    $report.regionConfigured=$true
    $script:ClashExe=Get-ClashExecutable
    if((Get-Item -LiteralPath $script:ClashExe).VersionInfo.ProductVersion -notlike '2.5.10*'){throw 'CLASH_VERSION_REVIEW_REQUIRED'}
    $clashDir=Split-Path $script:ClashExe
    $resourcesDir=Join-Path $clashDir 'resources'
    $serviceInstaller=Join-Path $resourcesDir 'clash-verge-service-install.exe'
    $bundledService=Join-Path $resourcesDir 'clash-verge-service.exe'
    $core=Join-Path $clashDir 'verge-mihomo.exe'
    if(@($serviceInstaller,$bundledService,$core | Where-Object {-not(Test-Path -LiteralPath $_ -PathType Leaf)}).Count){throw 'CLASH_SERVICE_INSTALLER_MISSING'}
    Set-GuestStep '核对 Clash 后台服务与已登记核心'
    Stop-Core
    $coreHash=(Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash
    $serviceHash=(Get-FileHash -LiteralPath $bundledService -Algorithm SHA256).Hash
    $serviceRoot=Get-ClashServiceRoot
    $installedCore=Join-Path $serviceRoot 'cores\verge-mihomo.exe'
    $installedService=Join-Path $serviceRoot 'bin\clash-verge-service.exe'
    if(-not(Test-InstalledClashService $installedService $serviceHash $installedCore $coreHash)) {
        if(Get-Process -Name 'clash-verge-service-install' -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $serviceInstaller}){
            throw 'CLASH_SERVICE_INSTALL_WAITING'
        }
        Write-Host '正在登记 Clash 后台服务…'
        $start=[Diagnostics.ProcessStartInfo]::new($serviceInstaller)
        $start.UseShellExecute=$false
        $start.CreateNoWindow=$true
        foreach($argument in @('--install-service','--ensure-service','--install-core',$core,'--sha256',$coreHash.ToLowerInvariant())){
            $start.ArgumentList.Add($argument)
        }
        try{$process=[Diagnostics.Process]::Start($start)}
        catch{throw 'CLASH_SERVICE_INSTALL_REQUIRED'}
        try{
            if(-not $process.WaitForExit(90000)){throw 'CLASH_SERVICE_INSTALL_WAITING'}
            if($process.ExitCode -ne 0){
                $report.installerExitCode=$process.ExitCode
                throw 'CLASH_SERVICE_INSTALL_REQUIRED'
            }
        }finally{$process.Dispose()}
        if(-not(Test-InstalledClashService $installedService $serviceHash $installedCore $coreHash)){
            throw 'CLASH_SERVICE_INSTALL_REQUIRED'
        }
    }
    try{Set-Service -Name 'clash_verge_service' -StartupType Automatic -ErrorAction Stop}
    catch{throw 'CLASH_SERVICE_START_FAILED'}
    Set-GuestStep '备份来宾 Clash 配置'
    $clashHome=Join-Path $env:APPDATA 'io.github.clash-verge-rev.clash-verge-rev'
    $profileFile=Join-Path $clashHome 'profiles.yaml';$vergeFile=Join-Path $clashHome 'verge.yaml';$baseFile=Join-Path $clashHome 'config.yaml'
    $clashRecord=Join-Path $root 'clash-before.json'
    try{
        Lock-Directory $clashHome
        if(-not(Test-Path -LiteralPath $clashRecord)){
            $existingFiles=@(@($profileFile,$vergeFile,$baseFile) | Where-Object {Test-Path -LiteralPath $_})
            $original=@{home=$clashHome;executable=$script:ClashExe;existingFiles=$existingFiles}
            Write-ClashConfigText $clashRecord (ConvertTo-Json -InputObject $original -Depth 4)
        }
        foreach($file in @($profileFile,$vergeFile,$baseFile)){
            if((Test-Path -LiteralPath $file) -and -not(Test-Path -LiteralPath ($file+'.before-claude'))){
                Copy-Item -LiteralPath $file -Destination ($file+'.before-claude') -ErrorAction Stop
            }
        }
    }catch{throw 'CLASH_CONFIG_BACKUP_FAILED'}
    Set-GuestStep '写入专用 Clash 配置'
    Write-ClashConfiguration $clashHome ([string]$payload.coreConfig)
    Set-GuestStep '登记 Clash 开机启动项'
    try{Set-ClaudomeClashStartup (Join-Path $root 'clash-startup-before.json') $script:ClashExe}
    catch{
        if($_.Exception.Message -eq 'CLASH_STARTUP_VALUE_CONFLICT'){throw}
        throw 'CLASH_STARTUP_REGISTRATION_FAILED'
    }
    Set-GuestStep '等待 Clash 服务核心与 TUN 网卡'
    Start-Core
    $core=$script:CoreExe
    $report.clashServiceRunning=((Get-Service -Name 'clash_verge_service').Status -eq 'Running')
    Set-GuestStep '配置来宾出站防火墙'
    $backup=Join-Path $root 'firewall-before.wfw'
    if(-not(Test-Path -LiteralPath $backup)){& netsh.exe advfirewall export $backup *> $null;if($LASTEXITCODE -ne 0){throw 'FIREWALL_EXPORT_FAILED'}}
    $profileBackup=Join-Path $root 'firewall-profiles-before.json'
    if(-not(Test-Path -LiteralPath $profileBackup)){Get-NetFirewallProfile -PolicyStore PersistentStore | Select-Object Name,Enabled,DefaultOutboundAction | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $profileBackup -Encoding utf8NoBOM}
    $old=@(Get-NetFirewallRule -PolicyStore PersistentStore -Direction Outbound -Enabled True -Action Allow | Where-Object {$_.Group -ne 'ClaudeEnvironmentSetup'})
    $names=Join-Path $root 'disabled-outbound-rules.json';Save-DisabledOutboundRules $names $old
    $old | Disable-NetFirewallRule
    Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled True -DefaultOutboundAction Block
    Get-NetFirewallRule -Group 'ClaudeEnvironmentSetup' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    $addresses=@($payload.endpointAddresses | ForEach-Object {[string]$_});if($addresses.Count -eq 0){throw 'NO_VERIFIED_ENDPOINT'}
    foreach($protocol in @('TCP','UDP')){New-NetFirewallRule -DisplayName ('Claude endpoint '+$protocol) -Group 'ClaudeEnvironmentSetup' -Direction Outbound -Action Allow -Program $core -Protocol $protocol -RemoteAddress $addresses -RemotePort ([int]$payload.endpointPort) -Profile Any | Out-Null}
    if($payload.externalGateway){
        New-NetFirewallRule -DisplayName 'Claude pinned node DNS' -Group 'ClaudeEnvironmentSetup' -Direction Outbound -Action Allow -Program $core -Protocol UDP -RemoteAddress '198.19.250.1' -RemotePort 53 -Profile Any | Out-Null
    }else{
        New-NetFirewallRule -DisplayName 'Claude DNS bootstrap' -Group 'ClaudeEnvironmentSetup' -Direction Outbound -Action Allow -Program $core -Protocol TCP -RemoteAddress '1.1.1.1' -RemotePort 443 -Profile Any | Out-Null
    }
    $tun=@(Get-NetAdapter | Where-Object {$_.Name -eq 'ClaudeTun'})
    if($tun.Count -ne 1){throw 'CLASH_TUN_REQUIRED'}
    New-NetFirewallRule -DisplayName 'Claude TUN interface' -Group 'ClaudeEnvironmentSetup' -Direction Outbound -Action Allow -InterfaceAlias $tun[0].Name -Profile Any | Out-Null
    $physical=Get-TaskUplink
    if($payload.platformId -eq 'virtualbox' -and $payload.transportKind -eq 'ethernet' -and $payload.externalGateway){
        try{Set-TaskDhcpRule $physical.Name '198.19.250.1'}catch{throw 'VBOX_DHCP_RULE_FAILED'}
    }elseif($payload.transportKind -ne 'bus'){
        $dhcp=(Get-CimInstance Win32_NetworkAdapterConfiguration -Filter "InterfaceIndex=$([int]$physical.ifIndex)" |
            Where-Object {$_.IPEnabled -and $_.DHCPEnabled} | Select-Object -First 1).DHCPServer
        if(-not $dhcp){throw 'DHCP_SERVER_UNKNOWN'}
        Set-TaskDhcpRule $physical.Name ([string]$dhcp)
    }
    if(@(Get-NetFirewallProfile -PolicyStore ActiveStore | Where-Object {-not $_.Enabled -or $_.DefaultOutboundAction -ne 'Block'}).Count){throw 'FIREWALL_NOT_ENFORCED'}
    $report.networkConfigured=$true
}
function Resume-AdsPower {
    try {
        Start-AdsClient;$id=Read-OwnedBrowserId;$wasActive=(Get-AdsBrowserStatus $id) -eq 'Active'
        $report.browserProfileConfigured=$true
        $measured=Measure-AdsBrowser $id $false
        $report.browserWindowOpened=$true;$report.browserRuntimePassed=[bool]$measured.runtimePassed;$report.translationPassed=[bool]$measured.translationPassed
        $report.browserReopenPassed=[bool]$payload.resumeReopenPassed
        if(-not $report.browserReopenPassed -and -not $wasActive){$second=Reopen-AdsBrowser $id;$report.browserReopenPassed=[bool]($measured.runtimePassed -and $measured.translationPassed -and $second.runtimePassed -and $second.translationPassed)}
        if(-not $report.browserRuntimePassed -or -not $report.translationPassed -or -not $report.browserReopenPassed){Add-Pending '继续原浏览器运行验收' @('原专用环境已保留，本次只做回读，没有重新创建或更新环境。','正常关闭后使用检查已配置环境，核对语言、时区、中文翻译和正常重开。') 'browser-runtime'}
    }catch{
        if(-not (Add-AdsPowerApiHandoff $_.Exception.Message)){
            Add-Pending '继续原 AdsPower 环境检查' @('保持 AdsPower 打开，检查专用环境的运行状态和联网结果。','处理后继续回读，已建立的环境会保留。') 'browser-runtime'
        }
    }
}
function Add-AdsPowerApiHandoff([string]$Code) {
    if($Code -eq 'ADSPOWER_API_UNREACHABLE'){
        Add-Pending 'AdsPower 本机 API 未连接' @('保持虚拟机内 AdsPower 打开。到「自动化 → API」查看接口是否已启用，默认端口为 50325。','接口就绪后可继续自动配置，也可以在助手左侧选择「不用 API，手动使用 AdsPower」。') 'adspower'
        return $true
    }
    if($Code -eq 'ADSPOWER_API_PERMISSION_OR_LOGIN'){
        Add-Pending 'AdsPower 本机 API 拒绝请求' @('已登录时，请核对账号的本机 API 权限、API 开关及所需的 API Key。API Key 填在助手「本机资料」。','可继续自动配置，或在助手左侧选择「不用 API，手动使用 AdsPower」。') 'adspower'
        return $true
    }
    return $false
}
function Configure-AdsPower {
    Start-AdsClient
    $name='Claude-'+([string]$payload.workspaceId).Substring(0,8)
    $ownership='ClaudeEnvironmentSetup:'+([string]$payload.workspaceId)
    $recordPath=Join-Path $root 'adspower-profile.json'
    try {
        $matches=@()
        if(Test-Path -LiteralPath $recordPath) {
            $record=Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
            if($record.workspaceId -ne $payload.workspaceId){throw 'ADSPOWER_PROFILE_REVIEW_REQUIRED'}
            $existing=Ads-Request 'GET' ('/api/v1/user/list?user_id='+[Uri]::EscapeDataString([string]$record.userId)+'&page_size=100')
            $matches=@($existing.list | Where-Object {$_.user_id -eq $record.userId -and $_.remark -eq $ownership})
            if($matches.Count -ne 1){throw 'ADSPOWER_PROFILE_REVIEW_REQUIRED'}
        } else {
            for($page=1;$page -le 50;$page++) {
                $existing=Ads-Request 'GET' ('/api/v1/user/list?page='+$page+'&page_size=100')
                $matches+=@($existing.list | Where-Object {$_.remark -eq $ownership})
                if(@($existing.list).Count -lt 100){break}
                if($page -eq 50){throw 'ADSPOWER_PROFILE_REVIEW_REQUIRED'}
            }
        }
        if($matches.Count -gt 1){throw 'ADSPOWER_PROFILE_REVIEW_REQUIRED'}
        $id=''
        if($matches.Count -eq 1){$id=[string]$matches[0].user_id}
        $fingerprint=@{webrtc='disabled';language_switch='0';language=@(Get-ExpectedBrowserLanguages $payload.country);automatic_timezone='1';browser_kernel_config=@{type='chrome';version='ua_auto'}}
        if($payload.ianaTimeZone){$fingerprint['automatic_timezone']='0';$fingerprint['timezone']=[string]$payload.ianaTimeZone}
        $settings=@{name=$name;domain_name='';remark=$ownership;open_urls=@();ipchecker='ipapi';country=([string]$payload.country).ToLowerInvariant();fingerprint_config=$fingerprint;user_proxy_config=@{proxy_soft='other';proxy_type='socks5';proxy_host='127.0.0.1';proxy_port='17897'}}
        if($id -and (Get-AdsBrowserStatus $id) -eq 'Active'){throw 'ADSPOWER_CLOSE_BEFORE_UPDATE'}
        if(-not $id) {
            # Pin Windows only during initial creation; never regenerate UA when resuming an existing profile.
            $fingerprint['random_ua']=@{ua_browser=@('chrome');ua_system_version=@('Windows 11')}
            $settings['group_id']='0'
            $created=Ads-Request 'POST' '/api/v1/user/create' $settings
            $id=[string]$created.id
            if(-not $id){throw 'ADSPOWER_PROFILE_CREATE_FAILED'}
        } else {
            $settings['user_id']=$id
            $null=Ads-Request 'POST' '/api/v1/user/update' $settings
        }
        @{workspaceId=[string]$payload.workspaceId;userId=$id} | ConvertTo-Json | Set-Content -LiteralPath $recordPath -Encoding utf8NoBOM
        $report.browserProfileConfigured=$true
        $first=Measure-AdsBrowser $id $true
        $report.browserWindowOpened=$true
        $report.browserRuntimePassed=[bool]$first.runtimePassed;$report.translationPassed=[bool]$first.translationPassed
        # Only this newly started owned window is closed; API stop preserves its data.
        $second=Reopen-AdsBrowser $id
        $report.browserRuntimePassed=[bool]($first.runtimePassed -and $second.runtimePassed)
        $report.translationPassed=[bool]($first.translationPassed -and $second.translationPassed)
        $report.browserReopenPassed=[bool]($report.browserRuntimePassed -and $report.translationPassed)
        if(-not $report.browserRuntimePassed -or -not $report.browserReopenPassed -or -not $report.translationPassed){
            Add-Pending '核对浏览器运行参数' @('专用环境已保留。打开浏览器设置 → 语言，开启网页翻译，并把翻译目标设为简体中文；美国出口的请求语言应为 en-US、en。','回到助手点击检查已配置环境，读取实际语言、时区、WebRTC 与内核。需要正常重开验证时，先自行关闭该专用窗口。') 'browser-runtime'
        }
    }catch {
        if($_.Exception.Message -eq 'ADSPOWER_CLOSE_BEFORE_UPDATE'){Add-Pending '先保存并关闭专用浏览器' @('该专用环境正在使用。请保存工作并正常关闭它，再点继续部署；不会强制关闭或清理资料。') 'browser-active'}
        elseif(Add-AdsPowerApiHandoff $_.Exception.Message){return}
        elseif($_.Exception.Message -eq 'ADSPOWER_PROFILE_REVIEW_REQUIRED'){Add-Pending 'AdsPower 环境需确认' @('打开 AdsPower 环境列表，确认同名 Claude 环境，不要删除已有环境。','回到安装助手，点击继续自动配置。') 'adspower'}
        else{Add-Pending '完成 AdsPower 登录与环境配置' @('在虚拟机中打开 AdsPower，用你自己的账号登录。','如支持本机 API，在 AdsPower 的自动化 → API 中确认已开启；需要 API Key 时填到安装助手的本机资料页。','回到助手点继续。程序会尝试新建 Claude 专用环境，并配置本地 SOCKS5 代理、WebRTC 与时区。','如需自行配置，选择助手左侧「不用 API，手动使用 AdsPower」，按当次指引检查原环境。','打开这个专用环境，先访问 https://ip.net.coffee/claude/，确认结果后再登录 Claude。') 'adspower'}
    }
}
function Set-HyperVGuestRedirectionPolicy([bool]$AllowClipboard) {
    $tsPolicy='HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
    if(-not(Test-Path -LiteralPath $tsPolicy)){New-Item -Path $tsPolicy -Force | Out-Null}
    $locked=@{fDisableClip=$(if($AllowClipboard){0}else{1});fDisableCdm=1;fDisableLPT=1;fDisableCam=1;fDisablePNPRedir=1;fDisableAudio=1;fDisableAudioCapture=1;fDisableCcm=1}
    foreach($entry in $locked.GetEnumerator()){
        New-ItemProperty -Path $tsPolicy -Name $entry.Key -PropertyType DWord -Value ([int]$entry.Value) -Force | Out-Null
    }
    if(-not $AllowClipboard){
        Get-Process -Name 'rdpclip' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}
