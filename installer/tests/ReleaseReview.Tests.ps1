#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$rows=[Collections.Generic.List[object]]::new()
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\Integrations.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Integration source parse failed'}
# Import pure functions only. No app, service, firewall or guest script runs.
foreach($name in @('Set-ClaudomeClashSettings','Save-DisabledOutboundRules','Clear-StaleClipboardState')) {
    $function=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$false)
    if(-not $function){throw ('Missing production function: '+$name)}
    . ([scriptblock]::Create($function.Extent.Text))
}
function Check([string]$Name,[scriptblock]$Test){
    try{& $Test;$rows.Add(@{name=$Name;passed=$true})}
    catch{$rows.Add(@{name=$Name;passed=$false;detail=$_.Exception.Message})}
}
function Assert($Value,[string]$Message){if(-not $Value){throw $Message}}
$fixture=@{'mixed-port'=17897;'allow-lan'=$false;mode='rule';ipv6=$false;tun=@{device='ClaudeTun';'auto-detect-interface'=$false;'strict-route'=$true;enable=$true};dns=@{nameserver=@('https://1.1.1.1/dns-query#Claude')};'interface-name'='ClaudomeBridge'}
Check 'global-clash-config-cannot-override-controlled-tun' {
    $verge=@{verge_mixed_port=7897};$base=@{tun=@{'auto-detect-interface'=$true;'strict-route'=$false};mode='direct';ipv6=$true}
    Set-ClaudomeClashSettings $verge $base ($fixture | ConvertTo-Json -Depth 8)
    Assert ($verge.verge_mixed_port -eq 17897 -and $base.tun.device -eq 'ClaudeTun' -and -not $base.tun['auto-detect-interface'] -and $base.tun['strict-route']) 'global TUN settings overrode selected bridge'
    Assert ($base['interface-name'] -eq 'ClaudomeBridge' -and $base.mode -eq 'rule' -and -not $base.ipv6 -and $base.dns.nameserver[0].EndsWith('#Claude')) 'selected DNS/interface policy was lost'
}
Check 'ethernet-task-removes-stale-bus-interface' {
    $ethernet=$fixture.Clone();$ethernet.Remove('interface-name');$verge=@{};$base=@{'interface-name'='ClaudomeBridge'}
    Set-ClaudomeClashSettings $verge $base ($ethernet | ConvertTo-Json -Depth 8)
    Assert (-not $base.ContainsKey('interface-name')) 'stale physical interface survived'
}
Check 'invalid-core-profile-is-rejected-before-modifying-settings' {
    $bad=$fixture.Clone();$bad['mixed-port']=7897;$verge=@{};$base=@{};$rejected=$false
    try{Set-ClaudomeClashSettings $verge $base ($bad | ConvertTo-Json -Depth 8)}catch{$rejected=$true}
    Assert ($rejected -and $base.Count -eq 0 -and $verge.Count -eq 0) 'invalid profile altered settings'
}
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$fixtureRoot=Join-Path $OutputDirectory ('review-fixtures-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
Check 'firewall-rollback-record-includes-rules-from-retries' {
    $path=Join-Path $fixtureRoot 'disabled.json'
    Save-DisabledOutboundRules $path @([pscustomobject]@{Name='first-rule'})
    Save-DisabledOutboundRules $path @([pscustomobject]@{Name='later-installed-rule'},[pscustomobject]@{Name='first-rule'})
    Save-DisabledOutboundRules $path @()
    $names=@(Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
    Assert ($names.Count -eq 2 -and 'first-rule' -in $names -and 'later-installed-rule' -in $names) 'retry discarded rules needed by rollback'
}
Check 'empty-firewall-snapshot-remains-an-array' {
    $path=Join-Path $fixtureRoot 'empty.json';Save-DisabledOutboundRules $path @()
    Assert ((Get-Content -LiteralPath $path -Raw).Trim() -eq '[]') 'empty snapshot cannot round-trip'
}
Check 'stale-clipboard-sweep-removes-expired-plaintext-and-tasks-only' {
    $clipBase=Join-Path $fixtureRoot 'clipboard-sweep'
    [IO.Directory]::CreateDirectory($clipBase) | Out-Null
    $now=[DateTime]::UtcNow
    $expiredId='11111111111111111111111111111111'
    $activeId='22222222222222222222222222222222'
    $freshId='33333333333333333333333333333333'
    $foreignId='44444444444444444444444444444444'
    $orphanTaskId='55555555555555555555555555555555'
    foreach($entry in @(
        @{id=$expiredId;age=-300;file='result.json'},
        @{id=$activeId;age=-300;file='result.json'},
        @{id=$freshId;age=-20;file='result.json'},
        @{id=$foreignId;age=-300;file='keep-unknown.txt'}
    )){
        $dir=Join-Path $clipBase ('clipboard-'+$entry.id)
        [IO.Directory]::CreateDirectory($dir) | Out-Null
        $f=Join-Path $dir $entry.file
        [IO.File]::WriteAllText($f,'{"text":"fictional"}')
        [IO.File]::SetLastWriteTimeUtc($f,$now.AddSeconds($entry.age))
        [IO.Directory]::SetLastWriteTimeUtc($dir,$now.AddSeconds($entry.age))
    }
    $script:stoppedTasks=[Collections.Generic.List[string]]::new()
    $script:unregisteredTasks=[Collections.Generic.List[string]]::new()
    function Stop-ScheduledTask([string]$TaskName,$ErrorAction){$script:stoppedTasks.Add($TaskName)}
    function Unregister-ScheduledTask([string]$TaskName,[bool]$Confirm,$ErrorAction){$script:unregisteredTasks.Add($TaskName)}
    function Get-ScheduledTask([string]$TaskName,$ErrorAction){
        return @(
            [pscustomobject]@{TaskName=('ClaudomeClipboard-'+$orphanTaskId);State='Ready'},
            [pscustomobject]@{TaskName=('ClaudomeClipboard-'+$activeId);State='Ready'},
            [pscustomobject]@{TaskName=('ClaudomeClipboard-'+$freshId);State='Running'}
        )
    }
    Clear-StaleClipboardState -BaseDir $clipBase -ActiveRunId $activeId -MaxAgeSeconds 120 -NowUtc $now
    Assert (-not [IO.Directory]::Exists((Join-Path $clipBase ('clipboard-'+$expiredId)))) 'expired plaintext clipboard folder remained'
    Assert ([IO.Directory]::Exists((Join-Path $clipBase ('clipboard-'+$activeId)))) 'active clipboard folder was deleted'
    Assert ([IO.Directory]::Exists((Join-Path $clipBase ('clipboard-'+$freshId)))) 'fresh clipboard folder was deleted'
    Assert ([IO.Directory]::Exists((Join-Path $clipBase ('clipboard-'+$foreignId)))) 'folder with unknown file was deleted'
    Assert (('ClaudomeClipboard-'+$expiredId) -in $script:unregisteredTasks -and ('ClaudomeClipboard-'+$orphanTaskId) -in $script:unregisteredTasks) 'stale clipboard scheduled tasks remained'
    Assert (('ClaudomeClipboard-'+$activeId) -notin $script:unregisteredTasks -and ('ClaudomeClipboard-'+$freshId) -notin $script:unregisteredTasks) 'active or running clipboard task was unregistered'
}
$failed=@($rows | Where-Object {-not $_.passed}).Count
@{type='pure production functions and isolated JSON fixtures; no host firewall changes';passed=$rows.Count-$failed;failed=$failed;tests=@($rows)} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-review-tests.json') -Encoding utf8NoBOM
Write-Output ('Release review passed='+($rows.Count-$failed)+' failed='+$failed)
if($failed){exit 1}
