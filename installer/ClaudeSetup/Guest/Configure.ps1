#requires -Version 7.0
param([Parameter(Mandatory)][string]$PayloadPath)
$ErrorActionPreference='Stop'
$progressPreference='SilentlyContinue'
if([IO.Path]::GetFileName($PayloadPath) -cnotmatch '^payload-([0-9a-f]{32})\.json$'){exit 2}
$runId=$Matches[1]
$report=[ordered]@{schema=2;runId=$runId;vmId='';workspaceId='';pendingActions=@();clashConfigured=$false;clashServiceRunning=$false;browserRuntimePassed=$false;browserReopenPassed=$false;translationPassed=$false;browserProfileConfigured=$false;browserWindowOpened=$false;softwareInstalled=$false;regionConfigured=$false;networkConfigured=$false;networkPassed=$false;outagePassed=$false;browserAcceptance='needs-user-action';code='NOT_COMPLETED';step='核对来宾身份';installerExitCode=$null;traceTlsPath='';traceComparison='';bootUtc='';clashRuntimePassed=$false;runtimeCode='';runtimeMismatchDetail='';exitCode='';dnsCode='';outageCode='';outageTested=$false;browserExitPassed=$false}
$root='C:\ProgramData\ClaudeEnvironment'
$reportPath=Join-Path (Split-Path $PayloadPath) ('guest-result-'+$runId+'.json')
$useChildReport=$false
$runMutex=$null
$ownsMutex=$false
function Add-Pending([string]$Title,[string[]]$Steps,[string]$Id) { $report.pendingActions+=@{title=$Title;steps=$Steps;id=$Id} }
function Set-GuestStep([string]$Step) {
    $report.step=$Step
    Write-Host ('[{0:HH:mm:ss}] {1}' -f (Get-Date),$Step)
}
function Lock-Directory([string]$Path) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Start-ElevatedGuest([string]$ScriptPath,[string]$InputPath,[string]$ResultPath) {
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute=$true;$start.Verb='runas'
    foreach($argument in @('-NoLogo','-NoProfile','-File',$ScriptPath,'-PayloadPath',$InputPath)){$start.ArgumentList.Add($argument)}
    try{$child=[Diagnostics.Process]::Start($start)}
    catch{
        $cause=$_.Exception.GetBaseException()
        if($cause -is [ComponentModel.Win32Exception]){
            $report.installerExitCode=$cause.NativeErrorCode
            if($cause.NativeErrorCode -eq 1223){throw 'GUEST_ELEVATION_CANCELLED'}
        }
        throw 'GUEST_ELEVATION_FAILED'
    }
    try{$child.WaitForExit();$exitCode=$child.ExitCode}
    finally{$child.Dispose()}
    if(-not [IO.File]::Exists($ResultPath)){
        $report.installerExitCode=$exitCode
        throw 'GUEST_ELEVATED_REPORT_MISSING'
    }
    return $exitCode
}
function Publish-GuestReport([string]$Path,$Value) {
    $temporary=$Path+'.new'
    try{
        [IO.File]::WriteAllText($temporary,(ConvertTo-Json -InputObject $Value -Depth 8),[Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary,$Path,$true)
    }finally{
        if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}
    }
}
try {
    try{$payload=Get-Content -LiteralPath $PayloadPath -Raw -Encoding utf8 | ConvertFrom-Json}
    catch{throw 'GUEST_INPUT_UNREADABLE'}
    $report.vmId=[string]$payload.vmId
    $report.workspaceId=[string]$payload.workspaceId
    if([string]$payload.runId -cne $runId -or $report.vmId -notmatch '^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$' -or
        $report.workspaceId -notmatch '^[0-9a-f]{32}$'){throw 'GUEST_INPUT_MISMATCH'}
    $model=Get-CimInstance Win32_ComputerSystem
    $product=Get-CimInstance Win32_ComputerSystemProduct
    if($payload.platformId -eq 'hyperv') {
        $identity=Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
        if($model.Manufacturer -ne 'Microsoft Corporation' -or $model.Model -ne 'Virtual Machine' -or $identity.vmId -ne $payload.vmId -or $identity.workspaceId -ne $payload.workspaceId){throw 'TARGET_VM_IDENTITY_MISMATCH'}
    }elseif($payload.platformId -in @('workstation','fusion')) {
        $identity=Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
        if($model.Manufacturer -notmatch 'VMware' -or $model.Model -notmatch 'VMware' -or $identity.vmId -ne $payload.vmId -or $identity.workspaceId -ne $payload.workspaceId){throw 'TARGET_VM_IDENTITY_MISMATCH'}
    }elseif($payload.platformId -eq 'utm') {
        $identity=Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
        if($model.Manufacturer -notmatch 'QEMU|UTM|Bochs' -or $identity.vmId -ne $payload.vmId -or $identity.workspaceId -ne $payload.workspaceId){throw 'TARGET_VM_IDENTITY_MISMATCH'}
    }elseif($payload.platformId -eq 'virtualbox') {
        if($model.Model -notmatch 'VirtualBox' -or ([guid]$product.UUID) -ne ([guid]$payload.vmId)){throw 'TARGET_VM_IDENTITY_MISMATCH'}
    }else{throw 'TARGET_VM_IDENTITY_MISMATCH'}
    $isAdmin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if(-not $isAdmin){
        Set-GuestStep '等待来宾管理员确认'
        $exitCode=Start-ElevatedGuest $PSCommandPath $PayloadPath $reportPath
        $useChildReport=$true
        exit $exitCode
    }
    $runMutex=[Threading.Mutex]::new($false,('Global\ClaudomeConfigure-'+$report.workspaceId))
    try{$ownsMutex=$runMutex.WaitOne(0)}
    catch [Threading.AbandonedMutexException]{$ownsMutex=$true}
    if(-not $ownsMutex){throw 'GUEST_CONFIG_BUSY'}
    $browserOnly = $payload.browserOnly -eq $true
    if($browserOnly -and (-not $payload.networkEnabled -or -not $payload.resumeNetwork)){throw 'GUEST_INPUT_MISMATCH'}
    if(-not $browserOnly){
        Set-GuestStep '核对来宾安装文件'
        Lock-Directory $root
        foreach($resource in $payload.resources) {
            $path=Join-Path (Split-Path $PayloadPath) $resource.fileName
            if(-not(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $resource.sha256){throw 'GUEST_RESOURCE_HASH_MISMATCH'}
        }
    }
    Add-Type -Path (Join-Path (Split-Path $PayloadPath) 'YamlDotNet.dll')
    . (Join-Path (Split-Path $PayloadPath) 'Integrations.ps1')
    Clear-StaleClipboardState (Split-Path $PayloadPath)
    . (Join-Path (Split-Path $PayloadPath) 'BrowserAudit.ps1')
    . (Join-Path (Split-Path $PayloadPath) 'NetworkAudit.ps1')
    if($payload.platformId -eq 'hyperv'){
        $allowHyperVClipboard=if($null -ne $payload.PSObject.Properties['sharedClipboardEnabled']){[bool]$payload.sharedClipboardEnabled}else{$true}
        Set-HyperVGuestRedirectionPolicy -AllowClipboard:$allowHyperVClipboard
        $prefW=if($null -ne $payload.PSObject.Properties['preferredDesktopWidth']){[int]$payload.preferredDesktopWidth}else{0}
        $prefH=if($null -ne $payload.PSObject.Properties['preferredDesktopHeight']){[int]$payload.preferredDesktopHeight}else{0}
        $displayInfo=Sync-HyperVGuestDisplayMode $prefW $prefH
        if($displayInfo){$report.displayState=$displayInfo}
    }
    if(-not $browserOnly){
        Set-GuestStep '核对来宾微软 VC++ x64 运行库'
        Ensure-GuestVcRuntime
        Set-GuestStep '检查 Clash Verge 与 AdsPower 安装'
        Install-GuestApp 'clash' 'Clash Verge'
        Install-GuestApp 'adspower' 'AdsPower'
    }
    $report.softwareInstalled=$true
    if($payload.networkEnabled) {
        if($payload.platformId -eq 'virtualbox' -and $payload.transportKind -eq 'ethernet' -and $payload.externalGateway){
            Set-GuestStep '核对受控网卡地址并恢复 DHCP'
            Repair-VirtualBoxUplink
        }
        if($browserOnly){
            Set-GuestStep '只读核对已配置的 Clash 网络'
        }else{
            Set-GuestStep '配置或回读 Clash 网络'
        }
        if($payload.resumeNetwork){
            try{Assert-ClashRuntime;$report.clashConfigured=$true;$report.clashServiceRunning=$true;$report.networkConfigured=$true;$report.regionConfigured=$true}
            catch {
                $mismatch=[string]$script:LastRuntimeMismatchDetail
                if($mismatch){$report.runtimeMismatchDetail=$mismatch}
                $detail = switch ($_.Exception.Message) {
                    'CLASH_SERVICE_NOT_RUNNING' { 'Clash 后台服务尚未运行。' }
                    'PERSISTED_PROFILE_CHANGED' { '已保存的专用配置与本任务记录不一致。' }
                    'PERSISTED_PROFILE_NOT_SELECTED' { 'Clash 当前未选中本任务的专用配置。' }
                    'RUNTIME_NETWORK_SETTINGS_MISMATCH' { 'Clash 生效的端口、模式或 TUN 配置与本任务要求不符。' }
                    'RUNTIME_ROUTE_MISMATCH' { 'Clash 生效线路或规则与本任务记录不一致。' }
                    'CLASH_TUN_REQUIRED' { '没有检测到可用的 ClaudeTun 网卡。' }
                    'CLASH_CONTROLLER_UNAVAILABLE' { 'Clash 本地控制接口暂时不可用。' }
                    default { '来宾未能完整核对已保存的网络配置。' }
                }
                if($mismatch){$detail=$detail+"（差异明细：$mismatch）"}
                Add-Pending '已保存网络配置需要处理' @($detail,'处理后可重试检查；也可直接点击「重新配置本任务网络」按当前任务线路重写来宾 Clash 配置并重新核验。') 'resume-network'
                throw 'RESUME_NETWORK_REVIEW_REQUIRED'
            }
        }else{Configure-Clash}
        if(-not $browserOnly){
            # Recheck installer-created outbound permissions after both applications exist.
            $additional=@(Get-NetFirewallRule -PolicyStore PersistentStore -Direction Outbound -Enabled True -Action Allow | Where-Object {$_.Group -ne 'ClaudeEnvironmentSetup'})
            Save-DisabledOutboundRules (Join-Path $root 'disabled-outbound-rules.json') $additional
            $additional | Disable-NetFirewallRule
            if(@(Get-NetFirewallRule -PolicyStore ActiveStore -Direction Outbound -Enabled True -Action Allow | Where-Object {$_.Group -ne 'ClaudeEnvironmentSetup'}).Count){throw 'UNREVIEWED_OUTBOUND_ALLOW'}
            Set-GuestStep '核对 Clash 生效配置与出站规则'
            $deadline=[DateTime]::UtcNow.AddSeconds(30)
            do{
                try{Assert-ClashRuntime;break}
                catch{
                    if([DateTime]::UtcNow -ge $deadline -or $_.Exception.Message -notin @('CLASH_CORE_NOT_READY','CLASH_SERVICE_CORE_REQUIRED','CLASH_CONTROLLER_UNAVAILABLE','CLASH_TUN_REQUIRED','RUNTIME_ROUTE_MISMATCH','RUNTIME_NETWORK_SETTINGS_MISMATCH')){throw}
                    Start-Sleep -Seconds 2
                }
            }while($true)
        }
        $report.clashConfigured=$true
        $report.bootUtc=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('O')
        Assert-ClashRuntime
        Assert-TaskFirewallSurface
        $report.clashRuntimePassed=$true
        Set-GuestStep '检查来宾 HTTPS 并核对代理出口'
        try{$trace=Read-GuestTrace}
        catch{
            if($_.Exception.Message -eq 'TRACE_TLS_FAILED'){
                $report.traceTlsPath = Test-ClashLoopbackTls
                if($report.traceTlsPath -eq 'LOOPBACK_TLS_FAILED'){
                    $report.traceComparison = Test-ComparisonHttps
                }
            }
            throw
        }
        if($trace.loc -ne $payload.country){throw 'EXIT_COUNTRY_MISMATCH'}
        if($payload.expectedExitIp -and $trace.ip -ne $payload.expectedExitIp){throw 'EXIT_IP_MISMATCH'}
        $report.networkPassed=$true
        try{$report.dnsCode=Test-ObservedDns;if($script:LastObservedDnsDetail){$report.dnsEvidence=$script:LastObservedDnsDetail}}catch{$report.dnsCode='UNVERIFIED'}
        if($report.dnsCode -eq 'BYPASS'){throw 'FIREWALL_BYPASS_DETECTED'}
        if(-not $payload.manualAdsPower){
            Set-GuestStep '配置或回读 AdsPower 环境'
            if($payload.resumeBrowser){Resume-AdsPower}else{Configure-AdsPower}
            if($report.browserProfileConfigured){try{$report.browserExitPassed=[bool](Test-AdsBrowserExit (Read-OwnedBrowserId))}catch{}}
            if($payload.testOutage -and $report.browserProfileConfigured){
                $report.outageTested=$true
                try{$report.outagePassed=[bool](Test-NetworkOutage (Read-OwnedBrowserId))}
                catch{
                    $report.outageCode=if($_.Exception.Message -eq 'OUTAGE_UNVERIFIED'){'UNVERIFIED'}else{'FAILED'}
                    try{Assert-ClashRuntime;Assert-ExpectedTrace (Trace-Request)}catch{$report.networkPassed=$false;$report.outageCode='FAILED'}
                    Add-Pending '断线与恢复检查未通过' @('保存虚拟机中的在线工作后，使用助手的断线与恢复检查。桌面请求和同一专用浏览器都必须断网，并在恢复后再次核对出口。') 'outage-validation'}
            }
        }
    } else {
        Add-Pending '选择网络节点后继续' @('回到网络节点页，导入自己的订阅或填写节点。','选定节点和实际地区时区后，点继续部署。程序会接着配置 Clash 和 AdsPower。') 'network'
    }
    if($payload.manualAdsPower -and $report.networkPassed){
        Set-GuestStep '网络已核验，等待手动配置与检查 AdsPower'
    }elseif(-not $payload.manualAdsPower){
        Add-Pending '在专用浏览器完成验收与登录' @('打开 Claude 专用 AdsPower 环境，访问 https://ip.net.coffee/claude/。','核对出口、DNS、WebRTC 和时区后，再由本人登录 Claude 并进行对话。','检测页结果不代表账号已可用；不要在结果未确认时登录。') 'acceptance'
        Set-GuestStep '自动配置完成，等待浏览器验收'
    }
    $report.code='NEEDS_BROWSER_ACCEPTANCE'
} catch {
    $report.networkPassed=$false;$report.outagePassed=$false
    if(-not $report.runtimeMismatchDetail -and $script:LastRuntimeMismatchDetail){$report.runtimeMismatchDetail=[string]$script:LastRuntimeMismatchDetail}
    if($_.Exception.Message -match '^FIREWALL_'){$report.runtimeCode='MISMATCH'}
    if($_.Exception.Message -match '^EXIT_(COUNTRY|IP)_MISMATCH$'){$report.exitCode=$Matches[1]+'_MISMATCH'}
    $known=@('RESUME_NETWORK_REVIEW_REQUIRED','CLASH_CONFIG_REVIEW_REQUIRED','CLASH_CORE_NOT_READY','CLASH_VERSION_REVIEW_REQUIRED','CLASH_SERVICE_INSTALL_REQUIRED','CLASH_TUN_REQUIRED','APPLICATION_INSTALL_WAITING','APPLICATION_INSTALL_FAILED','APPLICATION_READBACK_FAILED','TARGET_VM_IDENTITY_MISMATCH','GUEST_RESOURCE_HASH_MISMATCH','GUEST_RESOURCE_MISSING','REGION_NOT_SUPPORTED','REGION_READBACK_FAILED','CORE_CONFIG_INVALID','FIREWALL_EXPORT_FAILED','FIREWALL_NOT_ENFORCED','UNREVIEWED_OUTBOUND_ALLOW','NO_VERIFIED_ENDPOINT','PHYSICAL_INTERFACE_AMBIGUOUS','DHCP_SERVER_UNKNOWN','CORE_NOT_LISTENING','PROXY_PORT_COLLISION','TRACE_INCOMPLETE','EXIT_COUNTRY_MISMATCH','EXIT_IP_MISMATCH','OUTAGE_PROTECTION_FAILED','RESTORE_EXIT_MISMATCH','ADSPOWER_INSTALL_WAITING','ADSPOWER_INSTALL_FAILED','ADSPOWER_READBACK_FAILED')
    $known+=@('VC_RUNTIME_RESOURCE_MISSING','VC_RUNTIME_INSTALL_WAITING','VC_RUNTIME_RESTART_REQUIRED','VC_RUNTIME_INSTALL_FAILED','VC_RUNTIME_READBACK_FAILED','CLASH_SERVICE_INSTALL_WAITING','CLASH_SERVICE_INSTALLER_MISSING')
    $known+=@('CLASH_CONFIG_WRITE_FAILED','CLASH_CONFIG_BACKUP_FAILED','CLASH_STARTUP_REGISTRATION_FAILED','CLASH_STARTUP_VALUE_CONFLICT')
    $known+=@('GUEST_INPUT_UNREADABLE','GUEST_INPUT_MISMATCH','GUEST_CONFIG_BUSY','GUEST_ELEVATION_CANCELLED','GUEST_ELEVATION_FAILED','GUEST_ELEVATED_REPORT_MISSING')
    $known+=@('CLASH_SERVICE_START_FAILED','CLASH_SERVICE_NOT_RUNNING','CLASH_APP_LAUNCH_FAILED','CLASH_SERVICE_CORE_REQUIRED','CLASH_CORE_HASH_MISMATCH','CLASH_CONTROLLER_UNAVAILABLE','RUNTIME_CONTROLLER_NOT_LOCAL','RUNTIME_CONFIG_NOT_LOCATED','RUNTIME_CONFIG_OUT_OF_SCOPE','RUNTIME_NETWORK_SETTINGS_MISMATCH','RUNTIME_ROUTE_MISMATCH','PERSISTED_PROFILE_CHANGED','PERSISTED_PROFILE_NOT_SELECTED','TRACE_DNS_FAILED','TRACE_TLS_FAILED','TRACE_CONNECT_FAILED','TRACE_HTTP_FAILED','TRACE_HTTP_PROTOCOL_FAILED','TRACE_PROXY_TUNNEL_FAILED')
    $known+=@('VBOX_DHCP_RULE_FAILED','VBOX_DHCP_NOT_ENABLED','VBOX_DHCP_LEASE_NOT_READY')
    if($report.runtimeCode -eq 'MISMATCH' -and $_.Exception.Message -match '^FIREWALL_[A-Z_]+$'){$known+=@($_.Exception.Message)}
    if($_.Exception.Message -in $known){$report.code=$_.Exception.Message}else{$report.code='GUEST_STEP_FAILED'}
    switch -Wildcard ($report.code) {
        'VC_RUNTIME_RESTART_REQUIRED' {
            Add-Pending '在虚拟机中重启 Windows' @('保存来宾中的工作，重启虚拟机里的 Windows，然后继续原任务。') 'guest-runtime'
            break
        }
        'GUEST_ELEVATION_*' {
            Add-Pending '处理来宾管理员确认' @('保持虚拟机的 Windows 桌面打开。继续原任务后，在虚拟机内查看管理员确认。') 'guest-elevation'
            break
        }
        'GUEST_CONFIG_BUSY' {
            Add-Pending '等待上一轮来宾配置结束' @('保持虚拟机运行，待先前的配置窗口结束后继续原任务。') 'guest-step'
            break
        }
        'VC_RUNTIME_*' {
            Add-Pending '处理来宾微软 VC++ x64 运行库' @('如虚拟机里仍有安装提示，请处理并等待安装结束，再继续原任务。','若反复出现回读失败，请在来宾中通过 Windows 已安装的应用修复 Microsoft Visual C++ x64 运行库，然后继续。') 'guest-runtime'
            break
        }
        'CLASH_SERVICE_*' {
            Add-Pending '处理 Clash 后台服务' @('应用已安装。请查看虚拟机中的服务提示，处理后继续原任务。') 'guest-step'
            break
        }
        'CLASH_CONFIG_*' {
            Add-Pending '检查专用 Clash 配置文件' @('现有配置资料已保留。请正常退出虚拟机内的 Clash 窗口，再继续原任务。若仍失败，记录提示中的诊断码。') 'guest-step'
            break
        }
        'CLASH_STARTUP_*' {
            Add-Pending '检查来宾 Clash 开机启动项' @('请使用任务中已同步的 Windows 账号登录虚拟机，再继续原任务。若仍失败，记录提示中的诊断码。') 'guest-step'
            break
        }
        'VBOX_DHCP_*' {
            Add-Pending '检查来宾的受控网卡' @('确认虚拟机的以太网适配器已启用并设为自动获取 IP，再继续原任务。','取得受控地址和默认网关后，助手才会继续验收 HTTPS 和 AdsPower。') 'guest-step'
            break
        }
        'TRACE_*' {
            $nextStep = switch($report.traceTlsPath){
                'LOOPBACK_TLS_PASSED' { '同一节点经 Clash 本机代理可完成握手。复查虚拟网卡的路由与 DNS 后，再按原路径验收。' }
                'LOOPBACK_TLS_FAILED' {
                    switch($report.traceComparison){
                        'COMPARISON_HTTPS_RESPONDED' { '同一 Clash 代理可访问另一 HTTPS 检测地址。请先复测所选节点到原检测站的连接。' }
                        'COMPARISON_TLS_FAILED' { '两个 HTTPS 检测地址都握手失败，请检查来宾时钟、证书和代理节点。' }
                        default { '原检测站经 TUN 和本地代理都握手失败。请检查来宾时钟、证书和代理节点。' }
                    }
                }
                'LOOPBACK_TUNNEL_FAILED' { 'Clash 本机代理也未能建立隧道。先在助手中检查所选节点的连接和出口。' }
                default { 'Clash 已通过本地配置检查。请在助手里检查所选节点及出口，再点一键连接网络。' }
            }
            Add-Pending '复查来宾的代理联网' @($nextStep) 'guest-network-probe'
            break
        }
        default {
            Add-Pending '自动配置需要处理' @('请打开虚拟机查看当前提示；安装器和管理员确认窗口可能仍在等待。','处理后回到助手继续部署；原虚拟机和浏览器资料会保留。') 'guest-step'
        }
    }
    Write-Host ('配置在“{0}”暂停（{1}）。' -f $report.step,$report.code)
} finally {
    try{
        if(-not $useChildReport){Publish-GuestReport $reportPath $report}
    }finally{
        try{
            if(Test-Path -LiteralPath $PayloadPath){Remove-Item -LiteralPath $PayloadPath -Force}
        }finally{
            if($ownsMutex){$runMutex.ReleaseMutex()}
            if($runMutex){$runMutex.Dispose()}
        }
    }
}
if($report.code -ne 'NEEDS_BROWSER_ACCEPTANCE'){exit 1}
