#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$rows=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[scriptblock]$Test){try{& $Test;$rows.Add(@{name=$Name;passed=$true})}catch{$rows.Add(@{name=$Name;passed=$false;error=$_.Exception.Message})}}
function Assert($Value,[string]$Message){if(-not $Value){throw $Message}}
function Reject([scriptblock]$Test,[string]$Expected=''){$failed=$false;try{& $Test}catch{if($Expected -and $_.Exception.Message -ne $Expected){throw};$failed=$true};Assert $failed 'Expected failure'}
# Load function definitions only, not the identity gate or deployment scripts.
foreach($name in @('BrowserAudit.ps1','NetworkAudit.ps1','Integrations.ps1')){
    $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot ('..\ClaudeSetup\Guest\'+$name)),[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'SOURCE_PARSE_FAILED'}
    foreach($fn in $ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and
        ($name -ne 'Integrations.ps1' -or $_.Name -in @('ConvertTo-ClashLocalPath','Get-VerifiedClashListener'))
    }){. ([scriptblock]::Create($fn.Extent.Text))}
}
function Snapshot($Languages=@('en-US','en'),$Zone=$true,$Candidate=$false,$Complete=$true,$Ua='Chrome/140.0.0.0'){
    return @{languages=$Languages;timeZoneMatches=$Zone;rtcCandidate=$Candidate;rtcComplete=$Complete;ua=$Ua}
}
Check 'US-request-language-stays-English' {
    $result=Test-BrowserSnapshot (Snapshot) 'en-US,en;q=0.9' 'Chrome/140.0.1.0' @(Get-ExpectedBrowserLanguages 'US')
    Assert $result.runtimePassed 'valid browser rejected'
}
Check 'expanded-22-regions-all-map-to-valid-browser-locales' {
    foreach($code in @('US','GB','CA','AU','NZ','IE','DE','FR','NL','CH','SE','IT','ES','JP','KR','SG','HK','TW','MY','TH','IN','BR')) {
        $langs=@(Get-ExpectedBrowserLanguages $code)
        Assert ($langs.Count -eq 2 -and $langs[0] -match '^[a-z]{2}-[A-Z]{2}$') ('invalid locale mapping for '+$code)
    }
}
Check 'Chinese-request-language-is-not-Chinese-translation' {
    $result=Test-BrowserSnapshot (Snapshot @('zh-CN','zh')) 'zh-CN,zh;q=0.9' 'Chrome/140.0.1.0' @('en-US','en')
    Assert (-not $result.runtimePassed) 'wrong request locale accepted'
}
Check 'actual-request-header-required' {
    Assert (-not (Test-BrowserSnapshot (Snapshot) 'de-DE,de;q=0.9' 'Chrome/140.0.1.0' @('en-US','en')).runtimePassed) 'navigator alone accepted'
}
Check 'kernel-UA-major-mismatch-rejected' {
    Assert (-not (Test-BrowserSnapshot (Snapshot) 'en-US,en' 'Chrome/139.0.0.0' @('en-US','en')).runtimePassed) 'old UA accepted'
}
Check 'timezone-mismatch-rejected' {Assert (-not (Test-BrowserSnapshot (Snapshot @('en-US','en') $false) 'en-US,en' 'Chrome/140.0.0.0' @('en-US','en')).runtimePassed) 'zone accepted'}
Check 'WebRTC-candidate-rejected' {Assert (-not (Test-BrowserSnapshot (Snapshot @('en-US','en') $true $true) 'en-US,en' 'Chrome/140.0.0.0' @('en-US','en')).runtimePassed) 'candidate accepted'}
Check 'unfinished-WebRTC-measurement-not-passed' {Assert (-not (Test-BrowserSnapshot (Snapshot @('en-US','en') $true $false $false) 'en-US,en' 'Chrome/140.0.0.0' @('en-US','en')).runtimePassed) 'timeout accepted'}
Check 'loopback-captures-request-language-without-external-service' {
    $script:probeClient=$null
    function Invoke-Cdp($Connection,$Method,$Parameters,$Session){
        $uri=[Uri]$Parameters.url;$script:probeClient=[Net.Sockets.TcpClient]::new();$script:probeClient.Connect('127.0.0.1',$uri.Port)
        $request=[Text.Encoding]::ASCII.GetBytes("GET /fixture HTTP/1.1`r`nHost: 127.0.0.1`r`nAccept-Language: en-US,en;q=0.9`r`n`r`n")
        $script:probeClient.GetStream().Write($request,0,$request.Length);return @{}
    }
    try{Assert ((Read-RequestLanguage @{} @{session='fixture'}) -eq 'en-US,en;q=0.9') 'request header lost'}finally{if($script:probeClient){$script:probeClient.Dispose()}}
}
Check 'normal-reopen-reuses-same-id-and-no-config-write' {
    $script:calls=[Collections.Generic.List[string]]::new()
    function Ads-Request($Method,$Route,$Body){$script:calls.Add($Method+' '+$Route);return @{}}
    function Get-AdsBrowserStatus($Id){$script:calls.Add('status '+$Id);return 'Inactive'}
    function Measure-AdsBrowser($Id,$ConfigureTranslation){$script:calls.Add('measure '+$Id+' '+$ConfigureTranslation);return @{runtimePassed=$true;translationPassed=$true}}
    $result=Reopen-AdsBrowser 'fixture-owned-id'
    Assert $result.runtimePassed 'reopen failed'
    Assert ($script:calls.Count -eq 3 -and $script:calls[0] -eq 'GET /api/v1/browser/stop?user_id=fixture-owned-id' -and $script:calls[2] -eq 'measure fixture-owned-id False') 'profile replaced or updated'
}
# All OS / controller reads below are synthetic; Assert-ClashRuntime is the real function.
function Set-RuntimeFixture {
    $script:payload=@{coreConfigSha256=('a'*64);timeZoneId='Pacific Standard Time';country='US'}
    $script:runtimeHome=Join-Path $env:APPDATA 'io.github.clash-verge-rev.clash-verge-rev'
    $script:approved=Join-Path (Get-ClashServiceRoot) 'cores\verge-mihomo.exe'
    $script:activePath=Join-Path (Get-ClashServiceRoot) ('users\'+(Get-ClashOwnerKey)+'\runtime\config.yaml')
    $script:pipe='verge-mihomo-production-'+(Get-ClashOwnerKey)
    $script:corePath=$script:approved;$script:coreParent=42;$script:actualPipe='\\.\pipe\'+$script:pipe
    $script:selected=@{proxies=@(@{name='SelectedNode';type='socks5';server='first.example';port=1080});'proxy-groups'=@(@{name='Claude';type='select';proxies=@('SelectedNode')});rules=@('MATCH,Claude');hosts=@{'first.example'=@('1.1.1.1')};dns=@{enable=$true;nameserver=@('https://1.1.1.1/dns-query#Claude')}}
    $script:runtime=$script:selected.Clone();$script:runtime['external-controller']='';$script:runtime['secret']='fictional'
    $script:active=@{'mixed-port'=17897;'allow-lan'=$false;ipv6=$false;mode='rule';tun=@{enable=$true;device='ClaudeTun';'auto-route'=$true;'strict-route'=$true}}
    $script:selectedHash='a'*64;$script:selection='SelectedNode'
}
function Get-FileHash($LiteralPath,$Algorithm){return @{Hash=$script:selectedHash}}
function Get-ClashExecutable {return 'C:\Fixture\Clash\clash-verge.exe'}
function Get-ClashServiceRoot {return 'C:\Program Data\clash-verge-service'}
function Get-ClashOwnerKey {return ('a'*64)}
function Get-NetTCPConnection($State,$LocalPort){return @{LocalAddress='127.0.0.1';OwningProcess=123}}
function Get-Process($Id){return @{Path=$script:corePath;Id=123}}
function Get-CimInstance($ClassName,$Filter){
    if($ClassName -eq 'Win32_Service'){return @{State='Running';ProcessId=42}}
    return @{ParentProcessId=$script:coreParent;CommandLine=('verge-mihomo.exe -f "'+$script:activePath+'" -ext-ctl-pipe "'+$script:actualPipe+'"')}
}
function Read-LocalYaml($Path){
    if($Path.EndsWith('\claude-environment-setup.yaml')){return $script:selected}
    if($Path.EndsWith('\profiles.yaml')){return @{current='claude-environment-setup'}}
    return $script:runtime
}
function Read-ClashController($Pipe,$CorePid,$Secret,$Route){
    Assert ($Pipe -eq $script:pipe -and $CorePid -eq 123 -and $Secret -eq 'fictional') 'unexpected local controller'
    if($Route -eq 'configs'){return $script:active}
    if($Route -eq 'proxies/Claude'){return @{now=$script:selection}}
    if($Route -eq 'rules'){return @{rules=@(@{type='Match';proxy='Claude'})}}
    throw 'UNEXPECTED_CONTROLLER_ROUTE'
}
function Get-NetAdapter {return @{Name='ClaudeTun';Status='Up'}}
function Get-NetFirewallProfile($PolicyStore){return @{Enabled=$true;DefaultOutboundAction='Block'}}
function Get-NetFirewallRule($PolicyStore,$Direction,$Enabled,$Action){return @()}
function Get-TimeZone {return @{Id='Pacific Standard Time'}}
function Get-WinHomeLocation {return @{GeoId=([Globalization.RegionInfo]::new('US')).GeoId}}
Check 'persisted-and-active-core-readback-agree' {Set-RuntimeFixture;Assert-ClashRuntime}
Check 'persisted-profile-hash-change-is-not-repaired-or-passed' {Set-RuntimeFixture;$script:selectedHash='b'*64;Reject {Assert-ClashRuntime} 'PERSISTED_PROFILE_CHANGED'}
Check 'active-core-mode-change-is-rejected' {Set-RuntimeFixture;$script:active.mode='direct';Reject {Assert-ClashRuntime} 'RUNTIME_NETWORK_SETTINGS_MISMATCH'}
Check 'active-group-cannot-select-another-node' {Set-RuntimeFixture;$script:selection='Other';Reject {Assert-ClashRuntime} 'RUNTIME_ROUTE_MISMATCH'}
Check 'effective-DNS-change-is-rejected' {Set-RuntimeFixture;$script:runtime.dns=@{enable=$true;nameserver=@('8.8.8.8')};Reject {Assert-ClashRuntime} 'RUNTIME_ROUTE_MISMATCH'}
Check 'service-readback-rejects-sidecar-and-unowned-runtime' {
    Set-RuntimeFixture;$script:corePath='C:\Fixture\Clash\verge-mihomo.exe'
    Reject {Assert-ClashRuntime} 'CLASH_SERVICE_CORE_REQUIRED'
    Set-RuntimeFixture;$script:coreParent=99
    Reject {Assert-ClashRuntime} 'CLASH_SERVICE_CORE_REQUIRED'
    Set-RuntimeFixture;$script:activePath=Join-Path $script:runtimeHome 'active.yaml'
    Reject {Assert-ClashRuntime} 'RUNTIME_CONFIG_OUT_OF_SCOPE'
    Set-RuntimeFixture;$script:actualPipe='\\.\pipe\fixture-foreign'
    Reject {Assert-ClashRuntime} 'CLASH_CONTROLLER_UNAVAILABLE'
}
Check 'staged-runtime-readback-accepts-extended-drive-paths' {
    Set-RuntimeFixture;$script:activePath='\\?\'+$script:activePath;$script:corePath='\\?\'+$script:corePath
    Assert-ClashRuntime
}
Check 'service-readback-rejects-a-controller-bound-to-the-network' {
    Set-RuntimeFixture;$script:runtime['external-controller']='0.0.0.0:9097'
    Reject {Assert-ClashRuntime} 'RUNTIME_CONTROLLER_NOT_LOCAL'
}
Check 'guest-trace-distinguishes-dns-connect-and-confirmed-exit' {
    function Invoke-WebRequest {throw [Net.Http.HttpRequestException]::new([Net.Http.HttpRequestError]::NameResolutionError,'fixture')}
    Reject {Trace-Request} 'TRACE_DNS_FAILED'
    function Invoke-WebRequest {throw [Net.Http.HttpRequestException]::new([Net.Http.HttpRequestError]::ConnectionError,'fixture')}
    Reject {Trace-Request} 'TRACE_CONNECT_FAILED'
    function Invoke-WebRequest {return @{StatusCode=429;Content=''}}
    Reject {Trace-Request} 'TRACE_HTTP_FAILED'
    function Invoke-WebRequest {return @{StatusCode=200;Content=("ip=192.0.2.1"+[char]10+"loc=US")}}
    Assert ((Trace-Request).loc -eq 'US') 'exit trace was not read back'
}
Check 'tls-failure-compares-only-verified-clash-loopback-without-passing-the-tun-check' {
    function Invoke-WebRequest {
        param([uri]$Uri, [switch]$NoProxy, [uri]$Proxy, [switch]$SkipCertificateCheck)
        Assert (-not $SkipCertificateCheck) 'certificate validation was disabled'
        Assert ($Uri.AbsoluteUri -eq 'https://claude.ai/cdn-cgi/trace') 'paths did not query the same target'
        if($NoProxy){
            Assert ($null -eq $Proxy) 'primary check did not use the TUN path'
            throw [Net.Http.HttpRequestException]::new([Net.Http.HttpRequestError]::SecureConnectionError,'fixture')
        }
        Assert ($Proxy.AbsoluteUri -eq 'http://127.0.0.1:17897/') 'diagnostic did not stay on the verified Clash listener'
        return @{StatusCode=403;Content='fixture'}
    }
    Reject {Trace-Request} 'TRACE_TLS_FAILED'
    Assert ((Test-ClashLoopbackTls) -eq 'LOOPBACK_TLS_PASSED') 'a valid HTTP status did not attest alternate-path TLS'
    Reject {Trace-Request} 'TRACE_TLS_FAILED'
}
Check 'tls-comparison-distinguishes-shared-handshake-from-a-refused-proxy-tunnel' {
    function Invoke-WebRequest {throw [Net.Http.HttpRequestException]::new([Net.Http.HttpRequestError]::SecureConnectionError,'fixture')}
    Assert ((Test-ClashLoopbackTls) -eq 'LOOPBACK_TLS_FAILED') 'shared TLS failure was not identified'
    function Invoke-WebRequest {throw [Net.Http.HttpRequestException]::new([Net.Http.HttpRequestError]::ProxyTunnelError,'fixture')}
    Assert ((Test-ClashLoopbackTls) -eq 'LOOPBACK_TUNNEL_FAILED') 'failed CONNECT was not identified'
}
Check 'guest-trace-retries-transient-failure-on-the-same-path-once' {
    $script:traceAttempts=0
    function Start-Sleep {}
    function Invoke-WebRequest {
        param([uri]$Uri, [switch]$NoProxy, [uri]$Proxy)
        Assert ($NoProxy -and $null -eq $Proxy -and $Uri.Host -eq 'claude.ai') 'retry changed target or network path'
        $script:traceAttempts++
        if($traceAttempts -eq 1){throw [Net.Http.HttpRequestException]::new([Net.Http.HttpRequestError]::SecureConnectionError,'fixture')}
        return @{StatusCode=200;Content=("ip=192.0.2.1"+[char]10+"loc=US")}
    }
    Assert ((Read-GuestTrace).loc -eq 'US' -and $traceAttempts -eq 2) 'transient failure was not retried once'
    $script:traceAttempts=0
    function Trace-Request {$script:traceAttempts++;throw 'TRACE_TLS_FAILED'}
    Reject {Read-GuestTrace} 'TRACE_TLS_FAILED'
    Assert ($traceAttempts -eq 2) 'persistent failure was accepted or retried without bound'
}
Check 'comparison-https-is-diagnostic-and-stays-on-verified-loopback' {
    function Invoke-WebRequest {
        param([uri]$Uri, [switch]$NoProxy, [uri]$Proxy, [switch]$SkipCertificateCheck)
        Assert (-not $SkipCertificateCheck) 'certificate validation was disabled'
        Assert (-not $NoProxy -and $Proxy.AbsoluteUri -eq 'http://127.0.0.1:17897/') 'comparison left the managed proxy'
        Assert ($Uri.Host -eq 'www.cloudflare.com') 'comparison did not query a second host'
        return @{StatusCode=403;Content='fixture'}
    }
    Assert ((Test-ComparisonHttps) -eq 'COMPARISON_HTTPS_RESPONDED') 'HTTP response did not attest HTTPS'
    function Invoke-WebRequest {throw [Net.Http.HttpRequestException]::new([Net.Http.HttpRequestError]::SecureConnectionError,'fixture')}
    Assert ((Test-ComparisonHttps) -eq 'COMPARISON_TLS_FAILED') 'comparison TLS failure was not preserved'
}
function Set-OutageFixture([bool]$Bypass=$false,[bool]$BadBaseline=$false,[string]$Direct='DENIED'){
    $script:direct=$Direct
    $script:stopped=$false;$script:stopCount=0;$script:restoreCount=0;$script:bypass=$Bypass;$script:badBaseline=$BadBaseline
    $script:payload=@{country='US';expectedExitIp=''}
}
function Open-AdsCdp($Id){$x=[pscustomobject]@{};$x | Add-Member ScriptMethod Dispose {};return @{socket=$x;cancel=$x}}
function New-AuditTab($Connection){return @{target='fixture';session='fixture'}}
function Invoke-Cdp($Connection,$Method,$Parameters,$Session){return @{}}
function Trace-Request {if($script:stopped -or $script:badBaseline){throw 'BLOCKED'};return @{loc='US';ip='fixture'}}
function Read-BrowserTrace($Connection,$Tab){if($script:stopped -and -not $script:bypass){throw 'BLOCKED'};return @{loc='US';ip='fixture'}}
function Test-PhysicalProbe {Assert $script:stopped 'probe ran while the core was up';return $script:direct}
function Stop-Core {$script:stopped=$true;$script:stopCount++}
function Start-Core {$script:stopped=$false;$script:restoreCount++}
function Assert-CoreStopped {Assert $script:stopped 'core restarted'}
function Assert-ClashRuntime {Assert (-not $script:stopped) 'core not restored'}
Check 'dns-check-explains-disabled-audit-and-remains-read-only' {
    function Get-TaskUplink {return @{ifIndex=18;Name='fixture-link'}}
    function Get-NetAdapter {return @{ifIndex=18;Name='fixture-link';Status='Up'}}
    function Get-NetRoute {param($InterfaceIndex,$AddressFamily,$DestinationPrefix,$ErrorAction)}
    function Invoke-RestMethod {
        param([string]$Uri)
        $qName=if($Uri -match 'name=([^&]+)'){[Uri]::UnescapeDataString($Matches[1])}else{''}
        return @{Status=0;TC=$false;Question=@(@{name=$qName;type=1})}
    }
    function Test-PhysicalProbe {return 'UNVERIFIED'}
    function Get-GuestBlockAuditStatus {return 'DISABLED'}
    function Set-GuestBlockAuditFailure {throw 'read-only diagnostic tried to change audit policy'}
    Assert ((Test-ObservedDns) -eq 'BLOCK_AUDIT_DISABLED') 'missing audit was hidden or falsely passed'
}
Check 'enable-block-audit-reuses-current-policy-and-verifies-change-before-success' {
    $script:currentBlockAudit='DISABLED'
    $script:writeCount=0
    function Get-GuestBlockAuditStatus {return $script:currentBlockAudit}
    function Set-GuestBlockAuditFailure {$script:writeCount++;return $true}
    Assert ((Enable-GuestBlockAudit) -eq 'ENABLE_FAILED') 'accepted syntax without verifying the actual policy'
    function Set-GuestBlockAuditFailure {$script:writeCount++;$script:currentBlockAudit='ENABLED';return $true}
    Assert ((Enable-GuestBlockAudit) -eq 'ENABLED_NOW') 'failed to verify the new failure audit setting'
    Assert ((Enable-GuestBlockAudit) -eq 'ALREADY_ENABLED' -and $script:writeCount -eq 2) 'changed an enabled audit'
    $script:currentBlockAudit='UNKNOWN'
    Assert ((Enable-GuestBlockAudit) -eq 'STATUS_UNKNOWN' -and $script:writeCount -eq 2) 'wrote audit policy when current state was unavailable'
}
Check 'dynamic-exit-allows-an-ip-change-within-the-expected-region' {
    $payload=@{country='KR';expectedExitIp=''}
    $a=@{loc='KR';ip='203.0.113.10'}
    $b=@{loc='KR';ip='203.0.113.20'}
    Assert (-not (Get-TraceMismatch $a) -and -not (Get-TraceMismatch $b)) 'dynamic region rejected a rotated IPv4'
    $payload.expectedExitIp=$a.ip
    Assert ((Get-TraceMismatch $b) -eq 'IP_MISMATCH') 'fixed IPv4 change was not identified'
    Assert ((Get-TraceMismatch @{loc='US';ip=$a.ip}) -eq 'COUNTRY_MISMATCH') 'country mismatch was not identified separately'
}
Check 'outage-requires-desktop-and-browser-failure-then-restoration' {
    Set-OutageFixture
    Assert (Test-NetworkOutage 'fixture') 'valid outage failed'
    Assert ($script:stopCount -eq 1 -and $script:restoreCount -eq 1 -and -not $script:stopped) 'core not restored'
}
Check 'browser-bypass-fails-outage-and-still-restores-core' {
    Set-OutageFixture $true
    Reject {Test-NetworkOutage 'fixture'}
    Assert ($script:restoreCount -eq 1 -and -not $script:stopped) 'failure left core stopped'
}
Check 'timeout-without-denial-is-unverified-and-restores' {
    Set-OutageFixture $false $false 'UNVERIFIED'
    Reject {Test-NetworkOutage 'fixture'} 'OUTAGE_UNVERIFIED'
    Assert (-not $script:stopped -and $script:restoreCount -eq 1) 'unverified test did not restore the core'
}
Check 'manual-outage-requires-proof-without-opening-ads-api' {
    Set-OutageFixture
    function Open-AdsCdp {throw 'API unexpectedly opened'}
    Assert (Test-NetworkOutage '') 'guest-only outage did not pass'
}
Check 'failed-baseline-is-not-an-outage-pass' {
    Set-OutageFixture $false $true
    Reject {Test-NetworkOutage 'fixture'}
    Assert ($script:stopCount -eq 0) 'core stopped without working baseline'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$failed=@($rows | Where-Object {-not $_.passed}).Count
@{type='synthetic fixtures and loopback only; no VM or installed apps';passed=$rows.Count-$failed;failed=$failed;tests=@($rows)} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'alignment-tests.json') -Encoding utf8NoBOM
Write-Output ('Alignment passed='+($rows.Count-$failed)+' failed='+$failed)
if($failed){$rows | Where-Object {-not $_.passed} | ConvertTo-Json;exit 1}
