# Shared readback / outage checks. Loaded only in the verified task VM.
function ConvertTo-CanonicalConfig($Value) {
    if($null -eq $Value){return $null}
    if($Value -is [Collections.IDictionary]){
        $ordered=[ordered]@{};foreach($key in @($Value.Keys | Sort-Object)){$ordered[[string]$key]=ConvertTo-CanonicalConfig $Value[$key]};return ,$ordered
    }
    if($Value -is [Collections.IEnumerable] -and $Value -isnot [string]){return ,@($Value | ForEach-Object {ConvertTo-CanonicalConfig $_})}
    return $Value
}
function Get-ClashOwnerKey {
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    return [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sid)))
}
function Read-ClashController([string]$Pipe,[uint32]$CorePid,[string]$Secret,[ValidateSet('configs','proxies/Claude','rules')][string]$Route) {
    if(-not ('Claudome.ClashController' -as [type])){
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Claudome
{
    public static class ClashController
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

        public static string Read(string name, uint processId, string secret, string route)
        {
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            handler.ConnectCallback = async (_, token) =>
            {
                var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                try
                {
                    await pipe.ConnectAsync(3000, token).ConfigureAwait(false);
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverId) || serverId != processId)
                        throw new IOException("Unexpected Clash controller process.");
                    return pipe;
                }
                catch { pipe.Dispose(); throw; }
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8),
                MaxResponseContentBufferSize = 2 * 1024 * 1024
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/" + route);
            if (!string.IsNullOrEmpty(secret))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
    }
}
'@
    }
    try{return ([Claudome.ClashController]::Read($Pipe,$CorePid,$Secret,$Route) | ConvertFrom-Json -ErrorAction Stop)}
    catch{throw 'CLASH_CONTROLLER_UNAVAILABLE'}
}
function Assert-ClashServiceCommand([string]$Command,[string]$RuntimePath,[string]$Pipe) {
    if($Command -notmatch '(?:^|\s)-f\s+(?:"([^"]+)"|([^\s"]+))'){throw 'RUNTIME_CONFIG_NOT_LOCATED'}
    $file=if($Matches[1]){$Matches[1]}else{$Matches[2]}
    if((ConvertTo-ClashLocalPath $file) -ne $RuntimePath){throw 'RUNTIME_CONFIG_OUT_OF_SCOPE'}
    if($Command -notmatch '(?:^|\s)-ext-ctl-pipe\s+(?:"([^"]+)"|([^\s"]+))'){throw 'CLASH_CONTROLLER_UNAVAILABLE'}
    $actualPipe=if($Matches[1]){$Matches[1]}else{$Matches[2]}
    if($actualPipe -ne ('\\.\pipe\'+$Pipe)){throw 'CLASH_CONTROLLER_UNAVAILABLE'}
}
function Get-ClashAuditRawValue($Container, [string]$PropertyName) {
    if ($null -eq $Container) { return @{ present = $false; value = $null } }
    if ($Container -is [Collections.IDictionary]) {
        if ($Container.Contains($PropertyName) -and $null -ne $Container[$PropertyName]) {
            return @{ present = $true; value = $Container[$PropertyName] }
        }
        return @{ present = $false; value = $null }
    }
    $p = $Container.PSObject.Properties[$PropertyName]
    if ($null -ne $p -and $null -ne $p.Value) {
        return @{ present = $true; value = $p.Value }
    }
    return @{ present = $false; value = $null }
}
function Test-ClashStrictBool($Entry, [bool]$Expected) {
    if (-not $Entry.present) { return @{ ok = $false; actual = '缺失' } }
    if ($Entry.value -isnot [bool]) { return @{ ok = $false; actual = '类型异常' } }
    $b = [bool]$Entry.value
    if ($b -ne $Expected) { return @{ ok = $false; actual = $(if ($b) { 'true' } else { 'false' }) } }
    return @{ ok = $true; actual = $(if ($b) { 'true' } else { 'false' }) }
}
function Test-ClashStrictInt($Entry, [int]$Expected) {
    if (-not $Entry.present) { return @{ ok = $false; actual = '缺失' } }
    if ($Entry.value -is [bool] -or ($Entry.value -isnot [int] -and $Entry.value -isnot [long] -and $Entry.value -isnot [int16] -and $Entry.value -isnot [byte])) {
        return @{ ok = $false; actual = '类型异常' }
    }
    $num = [long]$Entry.value
    if ($num -ne [long]$Expected) { return @{ ok = $false; actual = [string]$num } }
    return @{ ok = $true; actual = [string]$num }
}
function Test-ClashApprovedEnum($Entry, [string]$Expected, [string[]]$ApprovedValues, [switch]$CaseSensitive) {
    if (-not $Entry.present) { return @{ ok = $false; actual = '缺失' } }
    if ($Entry.value -isnot [string]) { return @{ ok = $false; actual = '类型异常' } }
    $raw = [string]$Entry.value
    $matched = if ($CaseSensitive) { $raw -ceq $Expected } else { $raw -ieq $Expected }
    if ($matched) { return @{ ok = $true; actual = $Expected } }
    foreach ($allowed in $ApprovedValues) {
        $isKnown = if ($CaseSensitive) { $raw -ceq $allowed } else { $raw -ieq $allowed }
        if ($isKnown) { return @{ ok = $false; actual = $allowed } }
    }
    return @{ ok = $false; actual = '未知值' }
}
function Get-ClashRuntimeSettingMismatches($Active) {
    $mismatches = [Collections.Generic.List[string]]::new()
    if ($null -eq $Active) {
        $mismatches.Add('/configs: 期望 对象，实际 缺失')
        return @($mismatches)
    }
    $mixed = Test-ClashStrictInt (Get-ClashAuditRawValue $Active 'mixed-port') 17897
    if (-not $mixed.ok) { $mismatches.Add("mixed-port: 期望 17897，实际 $($mixed.actual)") }
    $allowLan = Test-ClashStrictBool (Get-ClashAuditRawValue $Active 'allow-lan') $false
    if (-not $allowLan.ok) { $mismatches.Add("allow-lan: 期望 false，实际 $($allowLan.actual)") }
    $ipv6 = Test-ClashStrictBool (Get-ClashAuditRawValue $Active 'ipv6') $false
    if (-not $ipv6.ok) { $mismatches.Add("ipv6: 期望 false，实际 $($ipv6.actual)") }
    $mode = Test-ClashApprovedEnum (Get-ClashAuditRawValue $Active 'mode') 'rule' @('rule','global','direct','script')
    if (-not $mode.ok) { $mismatches.Add("mode: 期望 rule，实际 $($mode.actual)") }

    $tunEntry = Get-ClashAuditRawValue $Active 'tun'
    $tun = if ($tunEntry.present -and ($tunEntry.value -is [Collections.IDictionary] -or $tunEntry.value -is [pscustomobject])) { $tunEntry.value } else { $null }
    $tunEnable = Test-ClashStrictBool (Get-ClashAuditRawValue $tun 'enable') $true
    if (-not $tunEnable.ok) { $mismatches.Add("tun.enable: 期望 true，实际 $($tunEnable.actual)") }
    $tunDevice = Test-ClashApprovedEnum (Get-ClashAuditRawValue $tun 'device') 'ClaudeTun' @('ClaudeTun','Meta','utun','Clash') -CaseSensitive
    if (-not $tunDevice.ok) { $mismatches.Add("tun.device: 期望 ClaudeTun，实际 $($tunDevice.actual)") }
    $tunAutoRoute = Test-ClashStrictBool (Get-ClashAuditRawValue $tun 'auto-route') $true
    if (-not $tunAutoRoute.ok) { $mismatches.Add("tun.auto-route: 期望 true，实际 $($tunAutoRoute.actual)") }
    $tunStrictRoute = Test-ClashStrictBool (Get-ClashAuditRawValue $tun 'strict-route') $true
    if (-not $tunStrictRoute.ok) { $mismatches.Add("tun.strict-route: 期望 true，实际 $($tunStrictRoute.actual)") }
    return @($mismatches)
}
function Assert-ClashRuntime {
    $script:LastRuntimeMismatchDetail=''
    $clashHome=Join-Path $env:APPDATA 'io.github.clash-verge-rev.clash-verge-rev'
    $profile=Join-Path $clashHome 'profiles\claude-environment-setup.yaml'
    $wantedHash=if($payload.coreConfigSha256){[string]$payload.coreConfigSha256}else{[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([string]$payload.coreConfig))).ToLowerInvariant()}
    if((Get-FileHash -LiteralPath $profile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $wantedHash){$script:LastRuntimeMismatchDetail='profiles/claude-environment-setup.yaml 哈希与任务记录不一致';throw 'PERSISTED_PROFILE_CHANGED'}
    $selected=Read-LocalYaml $profile
    $profiles=Read-LocalYaml (Join-Path $clashHome 'profiles.yaml')
    if($profiles['current'] -ne 'claude-environment-setup'){$script:LastRuntimeMismatchDetail="profiles.current: 期望 claude-environment-setup，实际 $(if($profiles['current']){'未知值'}else{'缺失'})";throw 'PERSISTED_PROFILE_NOT_SELECTED'}
    $service=Get-CimInstance Win32_Service -Filter "Name='clash_verge_service'"
    if($service.State -ne 'Running' -or $service.ProcessId -le 0){$script:LastRuntimeMismatchDetail="clash_verge_service: 期望 Running，实际 $($service.State)";throw 'CLASH_SERVICE_NOT_RUNNING'}
    $script:ClashExe=Get-ClashExecutable
    $process=Get-VerifiedClashListener
    $script:CoreExe=ConvertTo-ClashLocalPath $process.Path
    $core=Get-CimInstance Win32_Process -Filter ('ProcessId='+$process.Id)
    if($core.ParentProcessId -ne $service.ProcessId){$script:LastRuntimeMismatchDetail='mihomo 核心进程非 clash_verge_service 子进程';throw 'CLASH_SERVICE_CORE_REQUIRED'}
    $ownerKey=Get-ClashOwnerKey
    $runtimePath=Join-Path (Get-ClashServiceRoot) ('users\'+$ownerKey+'\runtime\config.yaml')
    $pipe='verge-mihomo-production-'+$ownerKey
    Assert-ClashServiceCommand $core.CommandLine $runtimePath $pipe
    $runtime=Read-LocalYaml $runtimePath
    if(($runtime['external-controller'] -and $runtime['external-controller'] -notmatch '^(?:127\.0\.0\.1|\[::1\]|localhost):[0-9]{1,5}$') -or
        $runtime['external-controller-tls']){$script:LastRuntimeMismatchDetail='external-controller 非本机监听或开启了 TLS';throw 'RUNTIME_CONTROLLER_NOT_LOCAL'}
    foreach($key in @('proxies','proxy-groups','rules','hosts','dns')){
        $a=ConvertTo-Json -InputObject (ConvertTo-CanonicalConfig $runtime[$key]) -Depth 30 -Compress
        $b=ConvertTo-Json -InputObject (ConvertTo-CanonicalConfig $selected[$key]) -Depth 30 -Compress
        if($a -cne $b){$script:LastRuntimeMismatchDetail="运行配置段 $key 与任务专用配置不一致";throw 'RUNTIME_ROUTE_MISMATCH'}
    }
    $secret=[string]$runtime['secret']
    $active=Read-ClashController $pipe $process.Id $secret 'configs'
    $settingMismatches=@(Get-ClashRuntimeSettingMismatches $active)
    if($settingMismatches.Count -gt 0){
        $script:LastRuntimeMismatchDetail=($settingMismatches -join '；')
        throw 'RUNTIME_NETWORK_SETTINGS_MISMATCH'
    }
    $group=Read-ClashController $pipe $process.Id $secret 'proxies/Claude'
    if($group.now -ne 'SelectedNode'){$script:LastRuntimeMismatchDetail='proxies/Claude.now 非 SelectedNode';throw 'RUNTIME_ROUTE_MISMATCH'}
    $rules=Read-ClashController $pipe $process.Id $secret 'rules'
    if(@($rules.rules).Count -ne 1 -or $rules.rules[0].type -ne 'Match' -or $rules.rules[0].proxy -ne 'Claude'){$script:LastRuntimeMismatchDetail="rules 数量=$(@($rules.rules).Count)，期望仅 1 条 MATCH,Claude";throw 'RUNTIME_ROUTE_MISMATCH'}
    if(@(Get-NetAdapter | Where-Object {$_.Name -eq 'ClaudeTun' -and $_.Status -eq 'Up'}).Count -ne 1){$script:LastRuntimeMismatchDetail='未检测到唯一状态为 Up 的 ClaudeTun 网卡';throw 'CLASH_TUN_REQUIRED'}
    if(@(Get-NetFirewallProfile -PolicyStore ActiveStore | Where-Object {-not $_.Enabled -or $_.DefaultOutboundAction -ne 'Block'}).Count){$script:LastRuntimeMismatchDetail='Windows 防火墙出站默认策略未处于全 Profile Block';throw 'FIREWALL_NOT_ENFORCED'}
    if(@(Get-NetFirewallRule -PolicyStore ActiveStore -Direction Outbound -Enabled True -Action Allow | Where-Object {$_.Group -ne 'ClaudeEnvironmentSetup'}).Count){$script:LastRuntimeMismatchDetail='存在非本任务组（ClaudeEnvironmentSetup）的启用出站放行规则';throw 'UNREVIEWED_OUTBOUND_ALLOW'}
    if((Get-TimeZone).Id -ne $payload.timeZoneId -or (Get-WinHomeLocation).GeoId -ne ([Globalization.RegionInfo]::new([string]$payload.country)).GeoId){$script:LastRuntimeMismatchDetail="区域或时区与任务不一致（当前时区=$((Get-TimeZone).Id)）";throw 'REGION_READBACK_FAILED'}
}
function Get-TraceMismatch($Trace) {
    if($Trace.loc -ne $payload.country){return 'COUNTRY_MISMATCH'}
    if($payload.expectedExitIp -and $Trace.ip -ne $payload.expectedExitIp){return 'IP_MISMATCH'}
    return ''
}
function Assert-ExpectedTrace($Trace) {
    if(Get-TraceMismatch $Trace){throw 'EXIT_MISMATCH'}
}
function Trace-Request([switch]$ViaClashLoopback, [switch]$ComparisonTarget) {
    $request = @{
        Uri = if($ComparisonTarget){'https://www.cloudflare.com/cdn-cgi/trace'}else{'https://claude.ai/cdn-cgi/trace'}
        ConnectionTimeoutSeconds = 15
        OperationTimeoutSeconds = 15
        MaximumRedirection = 0
        SkipHttpErrorCheck = $true
        ErrorAction = 'Stop'
        Headers = @{'Cache-Control'='no-cache'}
    }
    if($ViaClashLoopback){
        $request.Proxy = 'http://127.0.0.1:17897'
        $request.ConnectionTimeoutSeconds = 8
        $request.OperationTimeoutSeconds = 8
    }else{
        $request.NoProxy = $true
    }
    try{
        $response = Invoke-WebRequest @request
    }catch{
        for($errorType=$_.Exception;$null -ne $errorType;$errorType=$errorType.InnerException){
            if($errorType -isnot [Net.Http.HttpRequestException]){continue}
            switch([string]$errorType.HttpRequestError){
                'NameResolutionError' {throw 'TRACE_DNS_FAILED'}
                'SecureConnectionError' {throw 'TRACE_TLS_FAILED'}
                'ProxyTunnelError' {throw 'TRACE_PROXY_TUNNEL_FAILED'}
                'HttpProtocolError' {throw 'TRACE_HTTP_PROTOCOL_FAILED'}
            }
        }
        throw 'TRACE_CONNECT_FAILED'
    }
    if($response.StatusCode -ne 200){throw 'TRACE_HTTP_FAILED'}
    $fields=@{}
    foreach($line in ($response.Content -split "`n")){if($line -match '^([a-z]+)=(.*)$'){$fields[$Matches[1]]=$Matches[2].Trim()}}
    $exitAddress=$null
    if(-not [Net.IPAddress]::TryParse([string]$fields.ip,[ref]$exitAddress) -or $fields.loc -cnotmatch '^[A-Z]{2}$'){throw 'TRACE_INCOMPLETE'}
    return $fields
}
function Read-GuestTrace {
    try { return (Trace-Request) }
    catch {
        $primaryError = $_.Exception.Message
        if ($primaryError -in @('TRACE_TLS_FAILED','TRACE_CONNECT_FAILED')) {
            Start-Sleep -Seconds 2
            try { return (Trace-Request) }
            catch {
                $primaryError = $_.Exception.Message
                if ($primaryError -ne 'TRACE_CONNECT_FAILED') { throw }
            }
        }
        elseif ($primaryError -notin @('TRACE_HTTP_FAILED','TRACE_INCOMPLETE')) { throw }

        # Fallback probe only on HTTP/edge instability so transient single-endpoint failures recover cleanly.
        try { return (Trace-Request -ComparisonTarget) }
        catch { throw $primaryError }
    }
}
# Called only after the guarded TUN check fails. An HTTP response over the same
# verified Clash instance proves the alternate path reached the HTTPS layer.
function Test-ClashLoopbackTls {
    try{
        $null = Trace-Request -ViaClashLoopback
        return 'LOOPBACK_TLS_PASSED'
    }catch{
        switch($_.Exception.Message){
            'TRACE_TLS_FAILED' { return 'LOOPBACK_TLS_FAILED' }
            'TRACE_HTTP_FAILED' { return 'LOOPBACK_TLS_PASSED' }
            'TRACE_INCOMPLETE' { return 'LOOPBACK_TLS_PASSED' }
            'TRACE_PROXY_TUNNEL_FAILED' { return 'LOOPBACK_TUNNEL_FAILED' }
            default { return 'LOOPBACK_TEST_INCONCLUSIVE' }
        }
    }
}
function Test-ComparisonHttps {
    try {
        $null = Trace-Request -ViaClashLoopback -ComparisonTarget
        return 'COMPARISON_HTTPS_RESPONDED'
    }catch{
        switch($_.Exception.Message){
            'TRACE_HTTP_FAILED' { return 'COMPARISON_HTTPS_RESPONDED' }
            'TRACE_INCOMPLETE' { return 'COMPARISON_HTTPS_RESPONDED' }
            'TRACE_TLS_FAILED' { return 'COMPARISON_TLS_FAILED' }
            default { return 'COMPARISON_INCONCLUSIVE' }
        }
    }
}
function Assert-CoreStopped {
    if((Get-Service 'clash_verge_service').Status -eq 'Running' -or (Get-Process -Name 'verge-mihomo','verge-mihomo-alpha' -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $script:CoreExe}) -or (Get-NetTCPConnection -State Listen -LocalPort 17897 -ErrorAction SilentlyContinue)){throw 'CORE_RESTARTED_DURING_OUTAGE'}
}
function Read-BrowserTrace($Connection,$Tab) {
    $null=Invoke-Cdp $Connection 'Network.enable' @{} $Tab.session
    $null=Invoke-Cdp $Connection 'Network.setCacheDisabled' @{cacheDisabled=$true} $Tab.session
    $null=Invoke-Cdp $Connection 'Network.setBypassServiceWorker' @{bypass=$true} $Tab.session
    $url='https://claude.ai/cdn-cgi/trace?claudome_check='+[guid]::NewGuid().ToString('N')
    $nav=Invoke-Cdp $Connection 'Page.navigate' @{url=$url} $Tab.session
    if($nav.errorText){throw 'BROWSER_TRACE_FAILED'}
    for($i=0;$i -lt 30;$i++){
        Start-Sleep -Milliseconds 250
        $body=Evaluate-Cdp $Connection $Tab.session 'document.body?.innerText||""'
        $fields=@{};foreach($line in ($body -split "`n")){if($line -match '^([a-z]+)=(.*)$'){$fields[$Matches[1]]=$Matches[2].Trim()}}
        if($fields.ContainsKey('ip') -and $fields.ContainsKey('loc')){return $fields}
    }
    throw 'BROWSER_TRACE_FAILED'
}
function Assert-TaskFirewallSurface {
    $enabled=@(Get-NetFirewallRule -PolicyStore ActiveStore -Direction Outbound -Enabled True -Action Allow)
    $uplink=Get-TaskUplink
    $dnsRule=if($payload.externalGateway -eq $false){'Claude DNS bootstrap'}else{'Claude pinned node DNS'}
    $expectedNames=@('Claude endpoint TCP','Claude endpoint UDP',$dnsRule,'Claude TUN interface')
    if($payload.platformId -eq 'virtualbox' -or ($payload.transportKind -ne 'bus' -and $payload.externalGateway -eq $false)){$expectedNames+='Claude guest DHCP'}
    if((($enabled.DisplayName | Sort-Object) -join ',') -ne (($expectedNames | Sort-Object) -join ',')){throw 'FIREWALL_RULE_SET'}
    foreach($rule in $enabled) {
        $ports=$rule | Get-NetFirewallPortFilter
        $address=$rule | Get-NetFirewallAddressFilter
        $program=$rule | Get-NetFirewallApplicationFilter
        $alias=@($rule | Get-NetFirewallInterfaceFilter | ForEach-Object InterfaceAlias) -join ','
        $remote=@($address.RemoteAddress | ForEach-Object {([string]$_) -replace '/32$',''} | Sort-Object) -join ','
        switch -Regex ($rule.DisplayName) {
            '^Claude endpoint (TCP|UDP)$' {
                $expectedProto=$Matches[1]
                $acceptableProto=if($expectedProto -eq 'TCP'){@('TCP','6')}else{@('UDP','17')}
                if([string]$ports.Protocol -notin $acceptableProto -or
                    (@($ports.RemotePort) -join ',') -ne [string]$payload.endpointPort -or
                    $remote -ne (($payload.endpointAddresses | Sort-Object) -join ',') -or
                    (ConvertTo-ClashLocalPath ([string]$program.Program)) -ne $script:CoreExe){throw 'FIREWALL_ENDPOINT_SCOPE'}
            }
            '^Claude pinned node DNS$' {
                if([string]$ports.Protocol -notin @('UDP','17') -or (@($ports.RemotePort) -join ',') -ne '53' -or
                    $remote -ne '198.19.250.1' -or (ConvertTo-ClashLocalPath ([string]$program.Program)) -ne $script:CoreExe){throw 'FIREWALL_NODE_DNS_SCOPE'}
            }
            '^Claude DNS bootstrap$' {
                if([string]$ports.Protocol -notin @('TCP','6') -or (@($ports.RemotePort) -join ',') -ne '443' -or
                    $remote -ne '1.1.1.1' -or (ConvertTo-ClashLocalPath ([string]$program.Program)) -ne $script:CoreExe){throw 'FIREWALL_NODE_DNS_SCOPE'}
            }
            '^Claude guest DHCP$' {
                $service=$rule | Get-NetFirewallServiceFilter
                $validRemote=if($payload.platformId -eq 'virtualbox' -and $payload.externalGateway){$remote -eq '198.19.250.1,255.255.255.255'}else{$remote -match '^(?:\d{1,3}\.){3}\d{1,3},255\.255\.255\.255$'}
                if([string]$ports.Protocol -notin @('UDP','17') -or (@($ports.LocalPort) -join ',') -ne '68' -or
                    (@($ports.RemotePort) -join ',') -ne '67' -or $alias -ne $uplink.Name -or
                    -not $validRemote -or $service.Service -ne 'Dhcp' -or
                    [Environment]::ExpandEnvironmentVariables([string]$program.Program) -ne (Join-Path $env:SystemRoot 'System32\svchost.exe')){throw 'FIREWALL_DHCP_SCOPE'}
            }
            '^Claude TUN interface$' {if($alias -ne 'ClaudeTun'){throw 'FIREWALL_TUN_SCOPE'}}
        }
    }
}
function Find-PhysicalDenial([datetime]$Since,[string]$Source,[int]$SourcePort,[string]$Target,[int]$Port,[int]$Protocol) {
    try {
        $events=Get-WinEvent -FilterHashtable @{LogName='Security';Id=5157;StartTime=$Since} -MaxEvents 120 -ErrorAction Stop
        foreach($event in $events){
            $data=@{}
            foreach($item in ([xml]$event.ToXml()).Event.EventData.Data){$data[$item.Name]=$item.'#text'}
            if($data.SourceAddress -eq $Source -and [string]$data.SourcePort -eq [string]$SourcePort -and
                $data.DestAddress -eq $Target -and [string]$data.DestPort -eq [string]$Port -and
                [string]$data.Protocol -eq [string]$Protocol){return $true}
        }
    }catch{}
    return $false
}
function Test-PhysicalProbe([switch]$Dns) {
    $adapter=Get-TaskUplink
    $source=@(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -AddressState Preferred |
        Where-Object {$_.IPAddress -notlike '169.254.*'} | ForEach-Object IPAddress)
    if($source.Count -ne 1){return 'UNVERIFIED'}
    $target=if(@($payload.endpointAddresses) -contains '1.1.1.1'){'1.0.0.1'}else{'1.1.1.1'}
    $port=if($Dns){53}else{443}
    $protocol=if($Dns){17}else{6}
    $type=if($Dns){[Net.Sockets.SocketType]::Dgram}else{[Net.Sockets.SocketType]::Stream}
    $socket=[Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::InterNetwork,$type,[Net.Sockets.ProtocolType]$protocol)
    $started=Get-Date
    try{
        # Winsock IP_UNICAST_IF selects the task uplink even while TUN is running.
        $socket.SetSocketOption([Net.Sockets.SocketOptionLevel]::IP,[Net.Sockets.SocketOptionName]31,
            [Net.IPAddress]::HostToNetworkOrder([int]$adapter.ifIndex))
        $socket.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Parse($source[0]),0))
        $localPort=$socket.LocalEndPoint.Port
        $dest=[Net.IPEndPoint]::new([Net.IPAddress]::Parse($target),$port)
        try{
            if($Dns){
                [byte[]]$query=@(173,81,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1)
                $socket.Connect($dest)
                $null=$socket.Send($query)
                $socket.ReceiveTimeout=900
                [byte[]]$response=[byte[]]::new(512)
                $size=$socket.Receive($response)
                if($size -ge 12 -and $response[0] -eq 173 -and $response[1] -eq 81){return 'BYPASS'}
            }else{
                $ct=[Threading.CancellationTokenSource]::new(1200)
                try{$socket.ConnectAsync($dest,$ct.Token).AsTask().GetAwaiter().GetResult();return 'BYPASS'}
                finally{$ct.Dispose()}
            }
        }catch{
            for($errorType=$_.Exception;$null -ne $errorType;$errorType=$errorType.InnerException){
                if($errorType -is [Net.Sockets.SocketException] -and $errorType.SocketErrorCode -eq 'AccessDenied'){return 'DENIED'}
            }
        }
        if(Find-PhysicalDenial $started $source[0] $localPort $target $port $protocol){return 'DENIED'}
        return 'UNVERIFIED'
    }finally{$socket.Dispose()}
}
function Get-GuestBlockAuditStatus {
    try {
        if(-not ('Claudome.Native.BlockAudit' -as [type])){
            Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace Claudome.Native
{
    public static class BlockAudit
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Policy
        {
            public Guid Subcategory;
            public uint Flags;
            public Guid Category;
        }

        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool AuditQuerySystemPolicy(in Guid subcategory, uint count, out IntPtr result);

        [DllImport("advapi32.dll")]
        private static extern void AuditFree(IntPtr address);

        public static int IsFailureEnabled()
        {
            var filterConnection = new Guid("0CCE9226-69AE-11D9-BED3-505054503030");
            if (!AuditQuerySystemPolicy(in filterConnection, 1, out var address) || address == IntPtr.Zero)
                return -1;
            try
            {
                var policy = Marshal.PtrToStructure<Policy>(address);
                return policy.Subcategory != filterConnection ? -1 : (policy.Flags & 2u) != 0 ? 1 : 0;
            }
            finally { AuditFree(address); }
        }
    }
}
"@
        }
        switch([Claudome.Native.BlockAudit]::IsFailureEnabled()) {
            1 {return 'ENABLED'}
            0 {return 'DISABLED'}
            default {return 'UNKNOWN'}
        }
    }catch{return 'UNKNOWN'}
}

function Set-GuestBlockAuditFailure {
    $auditTool=Join-Path $env:SystemRoot 'System32\auditpol.exe'
    & $auditTool '/set' '/subcategory:{0CCE9226-69AE-11D9-BED3-505054503030}' '/failure:enable' *> $null
    return ($LASTEXITCODE -eq 0)
}

function Enable-GuestBlockAudit {
    $before=Get-GuestBlockAuditStatus
    if($before -eq 'ENABLED'){return 'ALREADY_ENABLED'}
    if($before -ne 'DISABLED'){return 'STATUS_UNKNOWN'}
    try{
        if(-not(Set-GuestBlockAuditFailure) -or (Get-GuestBlockAuditStatus) -ne 'ENABLED'){return 'ENABLE_FAILED'}
        return 'ENABLED_NOW'
    }catch{return 'ENABLE_FAILED'}
}

function Test-ObservedDns {
    $adapter=Get-TaskUplink
    $other=@(Get-NetAdapter | Where-Object {$_.Status -eq 'Up' -and $_.ifIndex -ne $adapter.ifIndex -and $_.Name -ne 'ClaudeTun'})
    $v6=@(Get-NetRoute -InterfaceIndex $adapter.ifIndex -AddressFamily IPv6 -DestinationPrefix '::/0' -ErrorAction SilentlyContinue)
    if($other.Count -or $v6.Count){
        $script:LastObservedDnsDetail=@{dohOk=$false;dohProvider='';dohReason='UPLINK_BYPASS';queryMatched=$false;physicalProbe='BYPASS';blockAudit=''}
        return 'BYPASS'
    }
    $nonce='claudome-'+[guid]::NewGuid().ToString('N')+'.example.com'
    $dohProvider=''
    $dohReason='NO_RESPONSE'
    foreach($provider in @(
        @{Name='Cloudflare';Url=('https://cloudflare-dns.com/dns-query?type=A&name='+$nonce)},
        @{Name='Google';Url=('https://dns.google/resolve?type=A&name='+$nonce)}
    )) {
        try {
            $result=Invoke-RestMethod -Uri $provider.Url -NoProxy -MaximumRedirection 0 -ConnectionTimeoutSeconds 6 -OperationTimeoutSeconds 8 -Headers @{Accept='application/dns-json';'Cache-Control'='no-cache'}
            if($null -eq $result -or $result -is [Array] -or $result -is [string] -or $result -is [ValueType]){$dohReason='INVALID_SHAPE';continue}
            $getField={param($obj,[string]$key) if($obj -is [Collections.IDictionary]){if($obj.Contains($key)){return $obj[$key]};return $null};$p=$obj.PSObject.Properties[$key];if($null -ne $p){return $p.Value};return $null}
            $statusVal=& $getField $result 'Status'
            if($null -eq $statusVal -or $statusVal -notin @(0,3)){$dohReason='BAD_STATUS';continue}
            $tcVal=& $getField $result 'TC'
            if($tcVal -eq $true){$dohReason='TRUNCATED';continue}
            $qVal=& $getField $result 'Question'
            $questions=if($null -ne $qVal){@($qVal)}else{@()}
            $matched=$false
            foreach($q in $questions) {
                if($null -eq $q -or $q -is [string] -or $q -is [ValueType]){continue}
                $qNameRaw=& $getField $q 'name';$qTypeRaw=& $getField $q 'type'
                if($null -eq $qNameRaw -or $null -eq $qTypeRaw){continue}
                $qName=([string]$qNameRaw).TrimEnd('.')
                $qType=0
                try{$qType=[int]$qTypeRaw}catch{continue}
                if($qName.Equals($nonce,[StringComparison]::OrdinalIgnoreCase) -and $qType -eq 1){$matched=$true;break}
            }
            if(-not $matched){$dohReason='QUESTION_MISMATCH';continue}
            $dohProvider=$provider.Name
            $dohReason='VERIFIED'
            break
        }catch{$dohReason='REQUEST_FAILED'}
    }
    $probeStatus='PROBE_ERROR'
    try {
        $probeStatus=Test-PhysicalProbe -Dns
    }catch{$probeStatus='PROBE_ERROR'}
    $auditStatus=if($probeStatus -in @('BYPASS','DENIED','PROBE_ERROR')){''}else{Get-GuestBlockAuditStatus}
    $script:LastObservedDnsDetail=@{
        dohOk=[bool]$dohProvider
        dohProvider=$dohProvider
        dohReason=$dohReason
        queryMatched=[bool]$dohProvider
        physicalProbe=$probeStatus
        blockAudit=$auditStatus
    }
    if($probeStatus -eq 'BYPASS'){return 'BYPASS'}
    if(-not $dohProvider){return 'DOH_UNVERIFIED'}
    if($probeStatus -eq 'DENIED'){return 'PASSED'}
    if($probeStatus -eq 'PROBE_ERROR'){return 'PHYSICAL_PROBE_UNVERIFIED'}
    if($auditStatus -eq 'DISABLED'){return 'BLOCK_AUDIT_DISABLED'}
    return 'NO_DENIAL_EVIDENCE'
}
function Measure-GuestNetwork {
    $h=[Net.Http.SocketsHttpHandler]::new()
    $h.UseProxy=$false;$h.AllowAutoRedirect=$false
    $client=[Net.Http.HttpClient]::new($h)
    $samples=@()
    try {
        foreach($attempt in 1..2) {
            $response=$null;$ct=[Threading.CancellationTokenSource]::new(10000)
            try {
                $url='https://speed.cloudflare.com/__down?bytes=262144&nonce='+[guid]::NewGuid().ToString('N')
                $timer=[Diagnostics.Stopwatch]::StartNew()
                $response=$client.GetAsync($url,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$ct.Token).GetAwaiter().GetResult()
                $null=$response.EnsureSuccessStatusCode()
                $latency=$timer.ElapsedMilliseconds
                $stream=$response.Content.ReadAsStream($ct.Token)
                [byte[]]$buffer=[byte[]]::new(16384)
                $bytes=0;$timer.Restart()
                while($bytes -lt 262144){
                    $length=$stream.ReadAsync($buffer,0,[Math]::Min($buffer.Length,262144-$bytes),$ct.Token).GetAwaiter().GetResult()
                    if($length -eq 0){break}
                    $bytes+=$length
                }
                if($bytes -eq 262144){$samples+=@{latencyMs=$latency;mbps=[Math]::Round($bytes*8.0/[Math]::Max(0.001,$timer.Elapsed.TotalSeconds)/1e6,2)}}
            }catch{}finally{if($response){$response.Dispose()};$ct.Dispose()}
        }
    }finally{$client.Dispose()}
    return @{attempts=2;samples=$samples}
}
function Test-AdsBrowserExit([string]$Id) {
    $connection=Open-AdsCdp $Id
    $tab=$null
    try{
        $tab=New-AuditTab $connection
        Assert-ExpectedTrace (Read-BrowserTrace $connection $tab)
        return $true
    }finally{
        if($tab){try{$null=Invoke-Cdp $connection 'Target.closeTarget' @{targetId=$tab.target}}catch{}}
        $connection.socket.Dispose();$connection.cancel.Dispose()
    }
}

function Wait-ClashRecoveryReady([int]$TimeoutSeconds = 30) {
    $transient = @(
        'CLASH_CORE_NOT_READY',
        'CLASH_SERVICE_CORE_REQUIRED',
        'CLASH_CONTROLLER_UNAVAILABLE',
        'CLASH_TUN_REQUIRED',
        'RUNTIME_ROUTE_MISMATCH',
        'RUNTIME_NETWORK_SETTINGS_MISMATCH'
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            Assert-ClashRuntime
            return
        } catch {
            if ([DateTime]::UtcNow -ge $deadline -or $_.Exception.Message -notin $transient) { throw }
            Start-Sleep -Seconds 2
        }
    } while ($true)
}

function Test-NetworkOutage([string]$Id) {
    $script:LastOutageStage = ''
    $script:LastOutageBlockedVerified = $false
    $script:LastOutageRecoveryVerified = $false
    $connection=$null;$tab=$null
    try {
        $script:LastOutageStage = 'precheck-trace'
        if($Id){$connection=Open-AdsCdp $Id;$tab=New-AuditTab $connection}
        Assert-ExpectedTrace (Trace-Request)
        if($tab){Assert-ExpectedTrace (Read-BrowserTrace $connection $tab)}
        $desktopBlocked=$false;$browserBlocked=-not $tab;$direct='UNVERIFIED'
        try {
            $script:LastOutageStage = 'stop-core'
            Stop-Core
            $stopDeadline = [DateTime]::UtcNow.AddSeconds(6)
            do {
                try { Assert-CoreStopped; break }
                catch {
                    if ([DateTime]::UtcNow -ge $stopDeadline) { throw }
                    Start-Sleep -Milliseconds 500
                }
            } while ($true)
            $script:LastOutageStage = 'outage-block-check'
            try{$null=Trace-Request}catch{$desktopBlocked=$true}
            if($tab){try{$null=Read-BrowserTrace $connection $tab}catch{$browserBlocked=$true}}
            $direct=Test-PhysicalProbe
            Assert-CoreStopped
            if($desktopBlocked -and $browserBlocked -and $direct -eq 'DENIED'){
                $script:LastOutageBlockedVerified = $true
            }
        }finally{
            $script:LastOutageStage = 'restart-core'
            Start-Core
        }
        $script:LastOutageStage = 'recovery-runtime'
        Wait-ClashRecoveryReady 30
        $script:LastOutageStage = 'recovery-trace'
        $traceOk = $false
        for($attempt = 0; $attempt -lt 3; $attempt++) {
            try {
                Assert-ExpectedTrace (Read-GuestTrace)
                if($tab){Assert-ExpectedTrace (Read-BrowserTrace $connection $tab)}
                $traceOk = $true
                break
            } catch {
                if($attempt -ge 2){throw}
                Start-Sleep -Seconds 2
            }
        }
        $script:LastOutageRecoveryVerified = $traceOk
        if(-not $desktopBlocked -or -not $browserBlocked -or $direct -eq 'BYPASS'){
            $script:LastOutageStage = 'outage-block-check'
            throw 'OUTAGE_PROTECTION_FAILED'
        }
        if($direct -ne 'DENIED'){
            $script:LastOutageStage = 'outage-unverified'
            throw 'OUTAGE_UNVERIFIED'
        }
        if(-not $script:LastOutageBlockedVerified -or -not $script:LastOutageRecoveryVerified){
            throw 'OUTAGE_PROTECTION_FAILED'
        }
        $script:LastOutageStage = 'passed'
        return $true
    }finally {
        if($tab){try{$null=Invoke-Cdp $connection 'Target.closeTarget' @{targetId=$tab.target}}catch{}}
        if($connection){$connection.socket.Dispose();$connection.cancel.Dispose()}
    }
}
