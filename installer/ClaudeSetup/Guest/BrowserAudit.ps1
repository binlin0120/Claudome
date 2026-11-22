# Loaded only after guest identity verification. No account, cookie or storage reads.
function Get-ExpectedBrowserLanguages([string]$Country) {
    $local=@{
        US='en-US';GB='en-GB';CA='en-CA';AU='en-AU';NZ='en-NZ';IE='en-IE';
        DE='de-DE';FR='fr-FR';NL='nl-NL';CH='de-CH';SE='sv-SE';IT='it-IT';ES='es-ES';
        JP='ja-JP';KR='ko-KR';SG='en-SG';HK='zh-HK';TW='zh-TW';
        MY='en-MY';TH='th-TH';IN='en-IN';BR='pt-BR'
    }[$Country]
    if(-not $local){throw 'REGION_NOT_SUPPORTED'}
    return @($local,$local.Split('-')[0])
}
function Start-AdsClient {
    $adsExe=@((Join-Path $env:LOCALAPPDATA 'Programs\AdsPower Global\AdsPower Global.exe'),(Join-Path $env:ProgramFiles 'AdsPower Global\AdsPower Global.exe')) | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
    if($adsExe -and -not(Get-Process -Name 'AdsPower Global' -ErrorAction SilentlyContinue)){Start-Process -FilePath $adsExe -WindowStyle Minimized | Out-Null;Start-Sleep -Seconds 10}
}
function Ads-Request([string]$Method,[string]$Route,$Body=$null) {
    if(-not $Route.StartsWith('/api/v1/')){throw 'ADSPOWER_ROUTE_INVALID'}
    $headers=@{};if($payload.adsPowerApiKey){$headers['Authorization']='Bearer '+[string]$payload.adsPowerApiKey}
    $parameters=@{Uri=('http://127.0.0.1:50325'+$Route);Method=$Method;Headers=$headers;TimeoutSec=20;NoProxy=$true;MaximumRedirection=0}
    if($null -ne $Body){$parameters['Body']=ConvertTo-Json -InputObject $Body -Depth 15 -Compress;$parameters['ContentType']='application/json'}
    try{$result=Invoke-RestMethod @parameters}
    catch{
        if($_.Exception.Response.StatusCode -in @(401,403)){throw 'ADSPOWER_API_PERMISSION_OR_LOGIN'}
        throw 'ADSPOWER_API_UNREACHABLE'
    }
    if($null -eq $result.code -or $result.code -ne 0){throw 'ADSPOWER_API_PERMISSION_OR_LOGIN'}
    return $result.data
}
function Get-AdsBrowserStatus([string]$Id) {
    $data=Ads-Request 'GET' ('/api/v1/browser/active?user_id='+[Uri]::EscapeDataString($Id))
    if($data.status -notin @('Active','Inactive')){throw 'ADSPOWER_STATUS_UNKNOWN'}
    return [string]$data.status
}
function Open-AdsCdp([string]$Id) {
    $started=Ads-Request 'GET' ('/api/v1/browser/start?open_tabs=1&ip_tab=0&user_id='+[Uri]::EscapeDataString($Id))
    $uri=[Uri]([string]$started.ws.puppeteer)
    if($uri.Scheme -ne 'ws' -or $uri.Host -notin @('127.0.0.1','localhost') -or $uri.UserInfo -or $uri.Port -lt 1){throw 'ADSPOWER_CDP_NOT_LOCAL'}
    $socket=[Net.WebSockets.ClientWebSocket]::new();$cancel=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(300))
    try{$socket.ConnectAsync($uri,$cancel.Token).GetAwaiter().GetResult();return @{socket=$socket;cancel=$cancel;sequence=0}}
    catch{$socket.Dispose();$cancel.Dispose();throw 'ADSPOWER_CDP_UNAVAILABLE'}
}
function Invoke-Cdp($Connection,[string]$Method,$Parameters=@{},[string]$Session='') {
    $deadline=[Threading.CancellationTokenSource]::CreateLinkedTokenSource($Connection.cancel.Token)
    $deadline.CancelAfter([TimeSpan]::FromSeconds(30))
    try {
    $Connection.sequence++
    $request=@{id=$Connection.sequence;method=$Method;params=$Parameters};if($Session){$request['sessionId']=$Session}
    $bytes=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $request -Compress -Depth 12))
    $null=$Connection.socket.SendAsync([ArraySegment[byte]]::new($bytes),[Net.WebSockets.WebSocketMessageType]::Text,$true,$deadline.Token).GetAwaiter().GetResult()
    $buffer=[byte[]]::new(65536)
    do {
        $message=[IO.MemoryStream]::new()
        try {
            do{$count=$Connection.socket.ReceiveAsync([ArraySegment[byte]]::new($buffer),$deadline.Token).GetAwaiter().GetResult();if($count.MessageType -ne 'Text'){throw 'CDP_CLOSED'};$message.Write($buffer,0,$count.Count);if($message.Length -gt 1MB){throw 'CDP_TOO_LARGE'}}until($count.EndOfMessage)
            $response=[Text.Encoding]::UTF8.GetString($message.ToArray()) | ConvertFrom-Json
        }finally{$message.Dispose()}
    }until($response.id -eq $Connection.sequence)
    if($response.error){throw 'CDP_COMMAND_FAILED'}
    return $response.result
    }finally{$deadline.Dispose()}
}
function Evaluate-Cdp($Connection,[string]$Session,[string]$Expression) {
    $result=Invoke-Cdp $Connection 'Runtime.evaluate' @{expression=$Expression;returnByValue=$true;awaitPromise=$true} $Session
    if($result.exceptionDetails){throw 'BROWSER_EVALUATION_FAILED'}
    return $result.result.value
}
function New-AuditTab($Connection,[string]$Url='about:blank') {
    $target=Invoke-Cdp $Connection 'Target.createTarget' @{url=$Url}
    $attached=Invoke-Cdp $Connection 'Target.attachToTarget' @{targetId=$target.targetId;flatten=$true}
    return @{target=[string]$target.targetId;session=[string]$attached.sessionId}
}
function Read-RequestLanguage($Connection,$Tab) {
    # Only a random loopback audit page is read; headers other than Accept-Language are discarded.
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start()
    $client=$null
    try {
        $accept=$listener.AcceptTcpClientAsync()
        $url='http://127.0.0.1:'+([Net.IPEndPoint]$listener.LocalEndpoint).Port+'/'+[guid]::NewGuid().ToString('N')
        $null=Invoke-Cdp $Connection 'Page.navigate' @{url=$url} $Tab.session
        $client=$accept.WaitAsync([TimeSpan]::FromSeconds(8)).GetAwaiter().GetResult();$stream=$client.GetStream()
        $header=[Collections.Generic.List[byte]]::new();$one=[byte[]]::new(1)
        do {
            $n=$stream.ReadAsync($one,0,1).WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
            if($n -ne 1 -or $header.Count -ge 16384){throw 'BROWSER_LANGUAGE_REQUEST_FAILED'}
            $header.Add($one[0])
        }until($header.Count -ge 4 -and [Text.Encoding]::ASCII.GetString($header.ToArray(),$header.Count-4,4) -eq "`r`n`r`n")
        $value='';foreach($line in ([Text.Encoding]::ASCII.GetString($header.ToArray()) -split "`r`n")){if($line -match '^Accept-Language:\s*(.+)$'){$value=$Matches[1]}}
        $body='<html><head><title>Claudome local check</title></head><body>Local browser check</body></html>'
        $reply=[Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nCache-Control: no-store`r`nContent-Length: "+$body.Length+"`r`nConnection: close`r`n`r`n"+$body)
        $stream.Write($reply,0,$reply.Length)
        return $value
    }finally{if($client){$client.Dispose()};$listener.Stop()}
}
function Test-BrowserSnapshot($Snapshot,[string]$RequestLanguage,[string]$Product,[string[]]$ExpectedLanguages) {
    $actual=@($Snapshot.languages);$header=@(($RequestLanguage -split ',') | ForEach-Object {($_ -split ';')[0].Trim()})
    $language=($actual.Count -eq $ExpectedLanguages.Count -and $header.Count -eq $ExpectedLanguages.Count)
    for($i=0;$language -and $i -lt $ExpectedLanguages.Count;$i++){$language=($actual[$i] -ceq $ExpectedLanguages[$i] -and $header[$i] -ceq $ExpectedLanguages[$i])}
    $ua=[regex]::Match([string]$Snapshot.ua,'(?:Chrome|Chromium)/(\d+)\.');$kernel=[regex]::Match($Product,'(?:Chrome|Chromium|HeadlessChrome)/(\d+)\.')
    $kernelMatches=$ua.Success -and $kernel.Success -and $ua.Groups[1].Value -eq $kernel.Groups[1].Value
    return [pscustomobject]@{languagePassed=[bool]$language;timeZonePassed=[bool]$Snapshot.timeZoneMatches;webrtcPassed=[bool]($Snapshot.rtcComplete -and -not $Snapshot.rtcCandidate);kernelPassed=[bool]$kernelMatches;runtimePassed=[bool]($language -and $Snapshot.timeZoneMatches -and $Snapshot.rtcComplete -and -not $Snapshot.rtcCandidate -and $kernelMatches)}
}
function Read-Translation($Connection,[bool]$Configure) {
    $tab=New-AuditTab $Connection 'chrome://settings/languages'
    try {
        for($i=0;$i -lt 10;$i++){
            $ready=Evaluate-Cdp $Connection $tab.session "Boolean(globalThis.chrome?.settingsPrivate)"
            if($ready){break};Start-Sleep -Milliseconds 300
        }
        if(-not $ready){return $false}
        $change=if($Configure){"await set('translate.enabled',true); await set('translate_recent_target','zh-CN');"}else{''}
        $js="(async()=>{const api=chrome.settingsPrivate;const get=k=>new Promise(r=>api.getPref(k,r));const set=(k,v)=>new Promise(r=>api.setPref(k,v,'',r));"+$change+"const a=await get('translate.enabled'),b=await get('translate_recent_target');return a?.value===true && ['zh-CN','zh'].includes(b?.value)})()"
        return [bool](Evaluate-Cdp $Connection $tab.session $js)
    }catch{return $false}finally{$null=Invoke-Cdp $Connection 'Target.closeTarget' @{targetId=$tab.target}}
}
function Measure-AdsBrowser([string]$Id,[bool]$ConfigureTranslation=$false) {
    $connection=Open-AdsCdp $Id;$tab=$null
    try {
        $tab=New-AuditTab $connection
        $requestLanguage=Read-RequestLanguage $connection $tab
        $zone=ConvertTo-Json -InputObject ([string]$payload.ianaTimeZone) -Compress
        $expression=@'
(async()=>{
 const expected=ZONE;
 let rtcCandidate=false,rtcComplete=false,pc;
 try {
  const C=window.RTCPeerConnection||window.webkitRTCPeerConnection;
  if(!C)rtcComplete=true;
  else {pc=new C({iceServers:[]});pc.createDataChannel('local-check');
   await new Promise(async resolve=>{const end=setTimeout(resolve,4000);pc.onicecandidate=e=>{if(e.candidate)rtcCandidate=true;else{rtcComplete=true;clearTimeout(end);resolve()}};
    try{await pc.setLocalDescription(await pc.createOffer())}catch{rtcComplete=true;clearTimeout(end);resolve()}});
  }
 }catch{rtcComplete=true}finally{try{pc?.close()}catch{}}
 let timeZoneMatches=false;try{timeZoneMatches=Intl.DateTimeFormat().resolvedOptions().timeZone===new Intl.DateTimeFormat('en',{timeZone:expected}).resolvedOptions().timeZone}catch{}
 return {languages:[...navigator.languages],ua:navigator.userAgent,timeZoneMatches,rtcCandidate,rtcComplete};
})()
'@
        $snapshot=Evaluate-Cdp $connection $tab.session ($expression.Replace('ZONE',$zone))
        $version=Invoke-Cdp $connection 'Browser.getVersion'
        $result=Test-BrowserSnapshot $snapshot $requestLanguage ([string]$version.product) @(Get-ExpectedBrowserLanguages $payload.country)
        $translation=Read-Translation $connection $ConfigureTranslation
        $result | Add-Member -NotePropertyName translationPassed -NotePropertyValue ([bool]$translation)
        return $result
    }finally {
        if($tab){try{$null=Invoke-Cdp $connection 'Page.navigate' @{url='https://ip.net.coffee/claude/'} $tab.session}catch{}}
        $connection.socket.Dispose();$connection.cancel.Dispose()
    }
}
function Reopen-AdsBrowser([string]$Id) {
    $null=Ads-Request 'GET' ('/api/v1/browser/stop?user_id='+[Uri]::EscapeDataString($Id))
    $closed=$false
    for($i=0;$i -lt 20;$i++){if((Get-AdsBrowserStatus $Id) -eq 'Inactive'){$closed=$true;break};Start-Sleep -Milliseconds 500}
    if(-not $closed){throw 'ADSPOWER_NORMAL_CLOSE_NOT_CONFIRMED'}
    # No update/create or preference writes between close and this measurement.
    return Measure-AdsBrowser $Id $false
}
function Read-OwnedBrowserId {
    $record=Get-Content -LiteralPath (Join-Path $root 'adspower-profile.json') -Raw | ConvertFrom-Json
    if($record.workspaceId -ne $payload.workspaceId -or -not $record.userId){throw 'ADSPOWER_PROFILE_REVIEW_REQUIRED'}
    $existing=Ads-Request 'GET' ('/api/v1/user/list?user_id='+[Uri]::EscapeDataString([string]$record.userId)+'&page_size=100')
    $matches=@($existing.list | Where-Object {$_.user_id -eq $record.userId -and $_.remark -eq ('ClaudeEnvironmentSetup:'+ $payload.workspaceId)})
    if($matches.Count -ne 1){throw 'ADSPOWER_PROFILE_REVIEW_REQUIRED'}
    return [string]$record.userId
}
