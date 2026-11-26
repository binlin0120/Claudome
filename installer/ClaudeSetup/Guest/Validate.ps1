#requires -Version 7.0
param([Parameter(Mandatory)][string]$PayloadPath,[switch]$Quick)
$ErrorActionPreference='Stop'
$progressPreference='SilentlyContinue'
$isAdmin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $isAdmin -and -not $Quick) {
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute=$true;$start.Verb='runas'
    foreach($arg in @('-NoLogo','-NoProfile','-File',$PSCommandPath,'-PayloadPath',$PayloadPath)){$start.ArgumentList.Add($arg)}
    try{$child=[Diagnostics.Process]::Start($start);$child.WaitForExit();exit $child.ExitCode}catch{exit 20}
}
$report=[ordered]@{schema=2;runId='';vmId='';workspaceId='';bootUtc='';networkPassed=$false;clashRuntimePassed=$false;browserRuntimePassed=$false;browserReopenPassed=$false;browserReopenTested=$false;translationPassed=$false;outagePassed=$false;outageTested=$false;pendingActions=@();code='NOT_COMPLETED';runtimeCode='';exitCode='';dnsCode='';blockAuditCode='';outageCode='';browserExitPassed=$false;browserOpened=$false;measurement=$null}
$root='C:\ProgramData\ClaudeEnvironment'
$reportPath=Join-Path (Split-Path $PayloadPath) 'validation-result.json'
function Add-Pending([string]$Title,[string[]]$Steps,[string]$Id){$report.pendingActions+=@{title=$Title;steps=$Steps;id=$Id}}
function Invoke-ReportedOutage([string]$Id) {
    $report.outageTested=$true
    try{$report.outagePassed=[bool](Test-NetworkOutage $Id)}
    catch{
        $report.outageCode=if($_.Exception.Message -eq 'OUTAGE_UNVERIFIED'){'UNVERIFIED'}else{'FAILED'}
        try{Assert-ClashRuntime;Assert-ExpectedTrace (Read-GuestTrace)}catch{$report.networkPassed=$false;$report.outageCode='FAILED'}
    }
}
try {
    $payload=Get-Content -LiteralPath $PayloadPath -Raw -Encoding utf8 | ConvertFrom-Json
    $model=Get-CimInstance Win32_ComputerSystem
    $product=Get-CimInstance Win32_ComputerSystemProduct
    if($payload.platformId -eq 'hyperv') {
        $identity=Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
        if($model.Manufacturer -ne 'Microsoft Corporation' -or $model.Model -ne 'Virtual Machine' -or $identity.vmId -ne $payload.vmId -or $identity.workspaceId -ne $payload.workspaceId){throw 'TARGET_VM_IDENTITY_MISMATCH'}
    }elseif($payload.platformId -eq 'workstation') {
        $identity=Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
        if($model.Manufacturer -notmatch 'VMware' -or $model.Model -notmatch 'VMware' -or $identity.vmId -ne $payload.vmId -or $identity.workspaceId -ne $payload.workspaceId){throw 'TARGET_VM_IDENTITY_MISMATCH'}
    }elseif($payload.platformId -eq 'virtualbox') {
        if($model.Model -notmatch 'VirtualBox' -or ([guid]$product.UUID) -ne ([guid]$payload.vmId)){throw 'TARGET_VM_IDENTITY_MISMATCH'}
    }else{throw 'TARGET_VM_IDENTITY_MISMATCH'}
    $report.runId=[string]$payload.runId;$report.vmId=[string]$payload.vmId;$report.workspaceId=[string]$payload.workspaceId
    $report.bootUtc=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('O')
    Add-Type -Path (Join-Path (Split-Path $PayloadPath) 'YamlDotNet.dll')
    . (Join-Path (Split-Path $PayloadPath) 'Integrations.ps1')
    Clear-StaleClipboardState (Split-Path $PayloadPath)
    . (Join-Path (Split-Path $PayloadPath) 'BrowserAudit.ps1')
    . (Join-Path (Split-Path $PayloadPath) 'NetworkAudit.ps1')
    # Read first. No Configure-Clash / Configure-AdsPower / Start-Core before persistence checks.
    try {
        if($Quick -or $payload.quick) {
            $profile=Join-Path $env:APPDATA 'io.github.clash-verge-rev.clash-verge-rev\profiles\claude-environment-setup.yaml'
            if((Get-FileHash -LiteralPath $profile -Algorithm SHA256).Hash -ine $payload.coreConfigSha256){throw 'PERSISTED_PROFILE_CHANGED'}
            if((Get-Service clash_verge_service).Status -ne 'Running'){throw 'CLASH_SERVICE_NOT_RUNNING'}
            if(@(Get-NetAdapter -Name ClaudeTun | Where-Object Status -eq 'Up').Count -ne 1){throw 'CLASH_TUN_REQUIRED'}
            $profiles=@(Get-NetFirewallProfile -PolicyStore ActiveStore)
            if($profiles.Count -ne 3 -or @($profiles | Where-Object {-not $_.Enabled -or $_.DefaultOutboundAction -ne 'Block'}).Count){throw 'FIREWALL_NOT_ENFORCED'}
        }else{
            Assert-ClashRuntime
            Assert-TaskFirewallSurface
        }
        $report.clashRuntimePassed=$true
    }catch{
        if($_.Exception.Message -match '^(PERSISTED_PROFILE_|CLASH_(TUN|SERVICE)|RUNTIME_|FIREWALL_|UNREVIEWED_|REGION_READBACK|PHYSICAL_INTERFACE_AMBIGUOUS)'){$report.runtimeCode='MISMATCH'}
        throw
    }
    $trace=Read-GuestTrace
    $report.exitCode=Get-TraceMismatch $trace
    if($report.exitCode){throw 'EXIT_MISMATCH'}
    $report.networkPassed=$true
    if($Quick -or $payload.quick){
        if($payload.openBrowser -and -not $payload.manualAdsPower){
            try{
                Start-AdsClient
                $id=Read-OwnedBrowserId
                if((Get-AdsBrowserStatus $id) -ne 'Active'){
                    $null=Ads-Request 'GET' ('/api/v1/browser/start?open_tabs=1&ip_tab=0&user_id='+[Uri]::EscapeDataString($id))
                }
                $report.browserOpened=$true
            }catch{}
        }
        $report.code='CHECKED';return
    }
    if($payload.measure){$report.measurement=Measure-GuestNetwork;$report.code='CHECKED';return}
    if($payload.enableBlockAudit -eq $true){$report.blockAuditCode=Enable-GuestBlockAudit}
    try{$report.dnsCode=Test-ObservedDns;if($script:LastObservedDnsDetail){$report.dnsEvidence=$script:LastObservedDnsDetail}}catch{$report.dnsCode='UNVERIFIED'}
    if($payload.manualAdsPower -or $payload.guestOnly){
        if($payload.testOutage){Invoke-ReportedOutage ''}
        $report.code='CHECKED';return
    }
    try {
        Start-AdsClient
        $id=Read-OwnedBrowserId;$wasActive=(Get-AdsBrowserStatus $id) -eq 'Active'
        $first=Measure-AdsBrowser $id $false
        $report.browserRuntimePassed=[bool]$first.runtimePassed;$report.translationPassed=[bool]$first.translationPassed
        if(-not $wasActive){
            $report.browserReopenTested=$true;$second=Reopen-AdsBrowser $id
            $report.browserRuntimePassed=[bool]($first.runtimePassed -and $second.runtimePassed)
            $report.translationPassed=[bool]($first.translationPassed -and $second.translationPassed)
            $report.browserReopenPassed=[bool]($report.browserRuntimePassed -and $report.translationPassed)
        }else{Add-Pending '正常关闭后验证重开' @('专用窗口原本已打开，本次没有关闭它。请保存工作并正常关闭，再点击检查已配置环境。') 'browser-reopen'}
        $connection=Open-AdsCdp $id;$tab=$null
        try {
            $tab=New-AuditTab $connection
            Assert-ExpectedTrace (Read-BrowserTrace $connection $tab)
            $report.browserExitPassed=$true
        }finally{
            if($tab){try{$null=Invoke-Cdp $connection 'Target.closeTarget' @{targetId=$tab.target}}catch{}}
            $connection.socket.Dispose();$connection.cancel.Dispose()
        }
        if($payload.testOutage){Invoke-ReportedOutage $id}
        if(-not $report.browserRuntimePassed -or -not $report.translationPassed){throw 'BROWSER_RUNTIME_REVIEW_REQUIRED'}
    }catch{
        try{Assert-ClashRuntime;Assert-ExpectedTrace (Trace-Request)}catch{$report.networkPassed=$false}
        Add-Pending '浏览器运行检查待处理' @('打开原来的专用环境，检查语言、时区、WebRTC、内核与中文翻译。API 不可用时请确认 AdsPower 登录、API 权限和密钥。','保存工作并正常关闭专用窗口后，可重新检查；不会新建或清空环境。') 'browser-runtime'}
    $report.code='CHECKED'
}catch{
    $report.code='VALIDATION_FAILED'
    Add-Pending '持久配置或网络回读未通过' @('本次先检查了当前配置，没有自动重写。保持助手开启，在虚拟机查看服务模式、TUN、所选专用配置、地区和时区。','需要修复时再点继续部署；修复后要重新执行重启验收，旧结果不会计为通过。') 'runtime-validation'
}finally{
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
    if(Test-Path -LiteralPath $PayloadPath){Remove-Item -LiteralPath $PayloadPath -Force}
}
