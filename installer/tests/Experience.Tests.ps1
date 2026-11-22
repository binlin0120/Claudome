#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$rows=[Collections.Generic.List[object]]::new()
function Check($Name,[scriptblock]$Test){try{& $Test;$rows.Add(@{name=$Name;passed=$true})}catch{$rows.Add(@{name=$Name;passed=$false;error=$_.Exception.Message})}}
function Assert($Value,$Message){if(-not $Value){throw $Message}}
$tokens=$null;$errors=$null
$integrations=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\Integrations.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'SOURCE_PARSE_FAILED'}
$resume=$integrations.Find({param($a)$a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Resume-AdsPower'},$true)
if(-not $resume){throw 'RESUME_FUNCTION_MISSING'}
. ([scriptblock]::Create($resume.Extent.Text))
$apiHandoff=$integrations.Find({param($a)$a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Add-AdsPowerApiHandoff'},$true)
. ([scriptblock]::Create($apiHandoff.Extent.Text))
function Add-Pending($Title,$Steps,$Id){$script:pending.Add($Id)}
function Start-AdsClient {}
function Read-OwnedBrowserId {return 'fixture-owned-profile'}
function Get-AdsBrowserStatus($Id){return 'Active'}
function Measure-AdsBrowser($Id,$ConfigureTranslation){Assert (-not $ConfigureTranslation) 'resume changed preferences';return @{runtimePassed=$true;translationPassed=$true}}
function Reopen-AdsBrowser($Id){throw 'ACTIVE_USER_WINDOW_WAS_CLOSED'}
function Configure-AdsPower {throw 'PROFILE_WAS_RECREATED'}
Check 'browser-resume-reads-owned-profile-without-recreate-update-or-close' {
    $script:report=@{};$script:payload=@{resumeReopenPassed=$true};$script:pending=[Collections.Generic.List[string]]::new()
    Resume-AdsPower
    Assert ($report.browserProfileConfigured -and $report.browserRuntimePassed -and $report.browserReopenPassed -and $pending.Count -eq 0) 'resume did not preserve completed profile'
}
Check 'browser-resume-preserves-active-window-when-reopen-is-still-pending' {
    $script:report=@{};$script:payload=@{resumeReopenPassed=$false};$script:pending=[Collections.Generic.List[string]]::new()
    Resume-AdsPower
    Assert ($report.browserRuntimePassed -and -not $report.browserReopenPassed -and $pending.Contains('browser-runtime')) 'active profile was closed or falsely accepted'
}
Check 'browser-API-failure-becomes-pending-instead-of-duplicate-profile' {
    function Read-OwnedBrowserId {throw 'API_OFFLINE'}
    $script:report=@{};$script:payload=@{};$script:pending=[Collections.Generic.List[string]]::new()
    Resume-AdsPower
    Assert ($pending.Contains('browser-runtime')) 'failure not preserved as pending'
}
Check 'manual-AdsPower-mode-skips-create-resume-and-automatic-outage' {
    $tokens=$null;$errors=$null
    $configure=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\Configure.ps1'),[ref]$tokens,[ref]$errors)
    Assert ($errors.Count -eq 0) 'configure script did not parse'
    $branch=$configure.Find({param($a)
        $a -is [Management.Automation.Language.IfStatementAst] -and
        $a.Clauses[0].Item1.Extent.Text -eq '-not $payload.manualAdsPower'
    },$true)
    Assert ($null -ne $branch) 'optional mode guard missing'
    $run=[scriptblock]::Create($branch.Extent.Text)
    $script:adsCalls=[Collections.Generic.List[string]]::new()
    function Set-GuestStep {}
    function Configure-AdsPower {$script:adsCalls.Add('configure')}
    function Resume-AdsPower {$script:adsCalls.Add('resume')}
    function Test-NetworkOutage {$script:adsCalls.Add('outage');return $true}
    function Test-AdsBrowserExit {return $true}
    $payload=@{manualAdsPower=$true;resumeBrowser=$false;testOutage=$true}
    $report=@{browserProfileConfigured=$true;outagePassed=$false}
    & $run
    $payload.resumeBrowser=$true
    & $run
    Assert ($adsCalls.Count -eq 0 -and -not $report.outagePassed) 'manual mode called an automatic browser check'
    $payload.Remove('manualAdsPower')
    & $run
    $payload.resumeBrowser=$false
    & $run
    Assert (($adsCalls -join ',') -eq 'resume,outage,configure,outage') 'legacy automatic mode changed'
}
Check 'manual-validation-validates-network-without-using-the-AdsPower-API' {
    $tokens=$null;$errors=$null
    $validate=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\Validate.ps1'),[ref]$tokens,[ref]$errors)
    Assert ($errors.Count -eq 0) 'validation script did not parse'
    $guard=$validate.Find({param($a)
        $a -is [Management.Automation.Language.IfStatementAst] -and
        $a.Clauses[0].Item1.Extent.Text -eq '$payload.manualAdsPower -or $payload.guestOnly'
    },$true)
    Assert ($null -ne $guard) 'validation did not honor manual mode'
    $source=$validate.Extent.Text
    $dailyApiGuard=$validate.Find({param($a)
        $a -is [Management.Automation.Language.IfStatementAst] -and
        $a.Clauses[0].Item1.Extent.Text -eq '$payload.openBrowser -and -not $payload.manualAdsPower'
    },$true)
    Assert ($null -ne $dailyApiGuard -and $dailyApiGuard.Extent.Text.Contains('Start-AdsClient')) 'daily API open ignored manual mode'
    $guestProbe=$source.IndexOf('$trace=Read-GuestTrace')
    Assert ($guestProbe -ge 0 -and $guestProbe -lt $dailyApiGuard.Extent.StartOffset -and
        $guard.Extent.EndOffset -lt $source.LastIndexOf('Start-AdsClient')) 'validation skipped required network checks or reached AdsPower before its guard'
    $payload=@{manualAdsPower=$true};$report=@{networkPassed=$true;browserRuntimePassed=$false;code='NOT_COMPLETED'}
    & ([scriptblock]::Create($guard.Extent.Text))
    Assert ($report.code -eq 'CHECKED' -and $report.networkPassed -and -not $report.browserRuntimePassed) 'manual validation misreported browser evidence'
}
foreach($functionName in @('Set-TaskDhcpRule','Test-VirtualBoxUplink','Repair-VirtualBoxUplink')){
    $definition=$integrations.Find({param($a)$a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $functionName},$true)
    . ([scriptblock]::Create($definition.Extent.Text))
}
Check 'virtualbox-dhcp-repair-updates-only-the-owned-ethernet-rule' {
    $script:written=$null
    function Get-NetFirewallRule($PolicyStore,$Group){
        Assert ($Group -eq 'ClaudeEnvironmentSetup') 'queried unrelated rules'
        @{DisplayName='Claude guest DHCP';Enabled='True';Direction='Outbound';Action='Allow';Name='managed-dhcp'}
        @{DisplayName='Unrelated rule';Enabled='True';Direction='Outbound';Action='Allow';Name='other'}
    }
    function Set-NetFirewallRule {
        [CmdletBinding()]
        param([Parameter(ValueFromPipeline)]$InputObject,$Direction,$Action,$Enabled,$Profile,$Program,$Service,$Protocol,$LocalPort,$RemotePort,$RemoteAddress,$InterfaceAlias)
        process{
            Assert ($InputObject.Name -eq 'managed-dhcp') 'modified an unrelated rule'
            $script:written=@{}+$PSBoundParameters
        }
    }
    function Get-NetFirewallAddressFilter {process{ @{RemoteAddress=@('198.19.250.1/32','255.255.255.255')} }}
    function Get-NetFirewallPortFilter {process{ @{Protocol='UDP';LocalPort='68';RemotePort='67'} }}
    function Get-NetFirewallServiceFilter {process{ @{Service='Dhcp'} }}
    function Get-NetFirewallApplicationFilter {process{ @{Program='%SystemRoot%\System32\svchost.exe'} }}
    function Get-NetFirewallInterfaceFilter {process{ @{InterfaceAlias=@('Ethernet 4')} }}
    Set-TaskDhcpRule 'Ethernet 4' '198.19.250.1'
    Assert ($written.RemoteAddress.Count -eq 2 -and ($written.RemoteAddress -contains '198.19.250.1') -and
        ($written.RemoteAddress -contains '255.255.255.255')) 'rule omitted the controlled gateway or added a wider range'
    Assert ($written.InterfaceAlias -eq 'Ethernet 4' -and $written.Service -eq 'Dhcp' -and $written.Protocol -eq 'UDP' -and
        $written.LocalPort -eq 68 -and $written.RemotePort -eq 67) 'DHCP exception was not restricted to the task uplink'
}
Check 'virtualbox-dhcp-repair-renews-only-the-uplink-and-requires-the-default-route' {
    $script:renewals=0;$script:hasRoute=$false;$script:recover=$true
    function Get-TaskUplink {return @{Name='Ethernet 4';ifIndex=43}}
    function Set-TaskDhcpRule($InterfaceAlias,$DhcpServer){
        Assert ($InterfaceAlias -eq 'Ethernet 4' -and $DhcpServer -eq '198.19.250.1') 'recovery used a different interface or gateway'
    }
    function Get-NetIPAddress($InterfaceIndex,$AddressFamily,$IPAddress){
        Assert ($InterfaceIndex -eq 43 -and $IPAddress -eq '198.19.250.10') 'looked for address on the wrong interface'
        @{PrefixLength=24;AddressState='Preferred'}
    }
    function Get-NetRoute($InterfaceIndex,$AddressFamily,$DestinationPrefix,$NextHop){
        Assert ($InterfaceIndex -eq 43 -and $DestinationPrefix -eq '0.0.0.0/0' -and $NextHop -eq '198.19.250.1') 'did not check for the controlled default route'
        if($script:hasRoute){return @{NextHop=$NextHop}}
    }
    function Get-CimInstance($ClassName,$Filter){
        Assert ($Filter -eq 'InterfaceIndex=43') 'renewed the wrong adapter'
        return @{DHCPEnabled=$true}
    }
    function Invoke-CimMethod($InputObject,$MethodName,$OperationTimeoutSec){
        Assert ($InputObject.DHCPEnabled -and $MethodName -eq 'RenewDHCPLease' -and $OperationTimeoutSec -eq 15) 'renewal was unscoped or unbounded'
        $script:renewals++
        $script:hasRoute=$script:recover
    }
    function Start-Sleep {}
    Repair-VirtualBoxUplink
    Assert ($renewals -eq 1 -and $hasRoute) 'lease recovery did not restore the route'
    Repair-VirtualBoxUplink
    Assert ($renewals -eq 1) 'healthy lease was renewed unnecessarily'
    $script:hasRoute=$false;$script:recover=$false
    $code='';try{Repair-VirtualBoxUplink}catch{$code=$_.Exception.Message}
    Assert ($code -eq 'VBOX_DHCP_LEASE_NOT_READY' -and $renewals -eq 2) 'missing route was not reported at the DHCP stage'
}
Check 'guest-configuration-copies-this-run-payload-before-launch' {
    $source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../ClaudeSetup/Deployment.cs') -Raw
    $transfer='await box.CopyTo(state, secrets, input.FileName, guestPayload, token);'
    $launcher='"-PayloadPath", guestPayload]'
    $copyIndex=$source.IndexOf($transfer,[StringComparison]::Ordinal)
    $launchIndex=$source.IndexOf($launcher,[StringComparison]::Ordinal)
    Assert ($copyIndex -ge 0 -and $launchIndex -gt $copyIndex) 'guest configure launched without transferring the exact current payload'
}
Check 'ads-local-api-separates-unreachable-from-rejected' {
    $audit=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\BrowserAudit.ps1'),[ref]$tokens,[ref]$errors)
    Assert (-not $errors.Count) 'browser audit parse failed'
    $request=$audit.Find({param($a)$a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Ads-Request'},$true)
    . ([scriptblock]::Create($request.Extent.Text))
    $payload=@{adsPowerApiKey='synthetic-not-a-real-key'}
    function Invoke-RestMethod {throw 'synthetic connection failure'}
    $code='';try{$null=Ads-Request GET '/api/v1/user/list'}catch{$code=$_.Exception.Message}
    Assert ($code -eq 'ADSPOWER_API_UNREACHABLE') 'connection failure was reported as login'
    function Invoke-RestMethod {return @{code=-1;msg='synthetic rejected'}}
    $code='';try{$null=Ads-Request GET '/api/v1/user/list'}catch{$code=$_.Exception.Message}
    Assert ($code -eq 'ADSPOWER_API_PERMISSION_OR_LOGIN') 'rejected response was reported as unavailable'
    function Invoke-RestMethod {return @{code=0;data=@{available=$true}}}
    Assert ((Ads-Request GET '/api/v1/user/list').available) 'a valid local API response was not returned'
}
$configure=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\Configure.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'SOURCE_PARSE_FAILED'}
Check 'configure-elevation-cancellation-publishes-only-the-current-run' {
    $fixtureRoot=Join-Path $OutputDirectory ('guest-report-fixture-'+[guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $previous=[guid]::NewGuid().ToString('N')
    $current=[guid]::NewGuid().ToString('N')
    $fixtureVm='11111111-1111-1111-1111-111111111111'
    $fixtureWorkspace='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $PayloadPath=Join-Path $fixtureRoot ('payload-'+$current+'.json')
    $previousReport=Join-Path $fixtureRoot ('guest-result-'+$previous+'.json')
    [IO.File]::WriteAllText($previousReport,'older run')
    [IO.File]::WriteAllText($PayloadPath,(ConvertTo-Json -InputObject @{
        platformId='virtualbox';runId=$current;vmId=$fixtureVm;workspaceId=$fixtureWorkspace
        internalFixtureSecret='SYNTHETIC_VALUE_MUST_NOT_ENTER_REPORT'
    }))
    function Get-CimInstance($ClassName) {
        if($ClassName -eq 'Win32_ComputerSystem'){return @{Model='VirtualBox'}}
        return @{UUID=$fixtureVm}
    }
    function Start-ElevatedGuest($ScriptPath,$InputPath,$ResultPath) {
        Assert ([IO.Path]::GetFileName($InputPath) -eq ('payload-'+$current+'.json')) 'wrong run elevation input'
        Assert ([IO.Path]::GetFileName($ResultPath) -eq ('guest-result-'+$current+'.json')) 'wrong run elevation result'
        $report.installerExitCode=1223
        throw 'GUEST_ELEVATION_CANCELLED'
    }
    $operation=foreach($statement in $configure.EndBlock.Statements){
        if($statement -is [Management.Automation.Language.FunctionDefinitionAst] -and $statement.Name -eq 'Start-ElevatedGuest'){continue}
        if($statement.Extent.Text -like 'if*$report.code*exit 1*'){continue}
        $statement.Extent.Text -replace '(?m)^(\s*)\$isAdmin=.+$', '$1$isAdmin=$false'
    }
    & ([scriptblock]::Create($operation -join [Environment]::NewLine))
    $path=Join-Path $fixtureRoot ('guest-result-'+$current+'.json')
    $text=[IO.File]::ReadAllText($path)
    $result=ConvertFrom-Json $text
    Assert ($result.runId -ceq $current -and $result.vmId -eq $fixtureVm -and $result.workspaceId -eq $fixtureWorkspace) 'current report identity was not preserved'
    Assert ($result.code -eq 'GUEST_ELEVATION_CANCELLED' -and $result.installerExitCode -eq 1223 -and -not $result.networkPassed) 'cancellation evidence lost or falsely passed'
    Assert ($text -notlike '*SYNTHETIC_VALUE_MUST_NOT_ENTER_REPORT*' -and -not [IO.File]::Exists($PayloadPath)) 'installation input was not protected'
    Assert (([IO.File]::ReadAllText($previousReport)) -ceq 'older run' -and -not [IO.File]::Exists($path+'.new')) 'previous report was overwritten or a partial report remained'
}
$network=$configure.Find({param($a)$a -is [Management.Automation.Language.IfStatementAst] -and $a.Clauses[0].Item1.Extent.Text -eq '$payload.resumeNetwork'},$true)
if(-not $network){throw 'NETWORK_RESUME_BRANCH_MISSING'}
$networkBranch=[scriptblock]::Create($network.Extent.Text)
function Configure-Clash {throw 'NETWORK_WAS_REWRITTEN'}
Check 'verified-network-resume-does-not-configure-again' {
    function Assert-ClashRuntime {$script:networkReads++}
    $script:networkReads=0;$script:report=@{};$script:payload=@{resumeNetwork=$true};$script:pending=[Collections.Generic.List[string]]::new()
    & $networkBranch
    Assert ($networkReads -eq 1 -and $report.clashConfigured -and $report.networkConfigured) 'existing runtime was not read back'
}
Check 'failed-network-readback-does-not-hide-failure-with-automatic-rewrite' {
    function Assert-ClashRuntime {throw 'READBACK_MISMATCH'}
    $script:report=@{};$script:payload=@{resumeNetwork=$true};$script:pending=[Collections.Generic.List[string]]::new();$rejected=$false
    try{& $networkBranch}catch{if($_.Exception.Message -ne 'RESUME_NETWORK_REVIEW_REQUIRED'){throw};$rejected=$true}
    Assert ($rejected -and $pending.Contains('resume-network')) 'failure was hidden or wrong configuration written'
}
foreach($name in @(
    'Ensure-GuestVcRuntime','Get-GuestVcRuntimeVersion','Test-GuestVcRuntimeFiles',
    'Read-LocalYaml','Write-ClashConfigText','Set-ClaudomeClashSettings','Write-ClashConfiguration','Set-ClaudomeClashStartup'
)){
    $definition=$integrations.Find({param($a)$a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $name},$true)
    if(-not $definition){throw "MISSING_FUNCTION_$name"}
    . ([scriptblock]::Create($definition.Extent.Text))
}
& {
    $PayloadPath=Join-Path $OutputDirectory 'payload.json'
    $payload=@{resources=@(@{id='vcredist';fileName='vc_redist.x64.exe'})}
    $report=@{}
    function Get-ItemProperty {if($script:runtimeVersion){[pscustomobject]@{Installed=1;Version=$script:runtimeVersion}}}
    function Test-Path($LiteralPath,$PathType){[IO.Path]::GetFileName($LiteralPath) -eq 'vc_redist.x64.exe' -or $script:runtimeFiles}
    function Get-Item { @{VersionInfo=@{ProductMajorPart=14;ProductMinorPart=44;ProductBuildPart=35211;ProductPrivatePart=0}} }
    function Get-Process {}
    function Start-Process {
        param($FilePath,$ArgumentList,[switch]$PassThru)
        $script:runtimeArguments=$ArgumentList
        if($script:runtimeExit -in 0,1638 -and $script:completeRuntime){
            $script:runtimeVersion='v14.44.35211.00'
            $script:runtimeFiles=$true
        }
        $fixture=[pscustomobject]@{ExitCode=$script:runtimeExit}
        $fixture | Add-Member ScriptMethod WaitForExit { param($milliseconds) $milliseconds -gt 0 -and $script:runtimeExit -ne -1 }
        $fixture | Add-Member ScriptMethod Dispose {}
        return $fixture
    }
    Check 'guest-runtime-reuses-a-verified-current-or-newer-install' {
        $script:runtimeVersion='v14.50.100.0';$script:runtimeFiles=$true;$script:runtimeArguments=$null
        Ensure-GuestVcRuntime
        Assert ($null -eq $script:runtimeArguments) 'usable guest runtime was reinstalled'
    }
    Check 'guest-runtime-installs-updates-or-repairs-before-clash' {
        foreach($installed in @('', 'v14.40.10.0', 'v14.44.35211.00')){
            $script:runtimeVersion=$installed;$script:runtimeFiles=$false;$script:runtimeExit=0;$script:completeRuntime=$true
            Ensure-GuestVcRuntime
            $expected=if($installed -eq 'v14.44.35211.00'){'/repair'}else{'/install'}
            Assert (($script:runtimeArguments -join ' ') -eq "$expected /quiet /norestart") 'wrong runtime mode or reboot behavior'
            Assert ($script:runtimeFiles) 'runtime files were not verified'
        }
    }
    Check 'guest-runtime-waits-and-reports-reboot-failure-or-missing-files' {
        foreach($fixture in @(@(-1,'WAITING'),@(3010,'RESTART_REQUIRED'),@(1603,'FAILED'),@(0,'READBACK_FAILED'),@(1638,'READBACK_FAILED'))){
            $script:runtimeVersion='';$script:runtimeFiles=$false;$script:runtimeExit=$fixture[0];$script:completeRuntime=$false
            $report.installerExitCode=$null
            $actual=try{Ensure-GuestVcRuntime}catch{$_.Exception.Message}
            Assert ($actual -eq ('VC_RUNTIME_INSTALL_'+$fixture[1]) -or $actual -eq ('VC_RUNTIME_'+$fixture[1])) "unexpected result $actual"
            if($fixture[0] -ge 0){Assert ($report.installerExitCode -eq $fixture[0]) 'installer exit code not retained'}
        }
    }
}
$assets=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\ClaudeSetup\obj\project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$yamlPath=$assets.packageFolders.Keys | ForEach-Object {Join-Path $_ 'yamldotnet\18.1.0\lib\netstandard2.0\YamlDotNet.dll'} |
    Where-Object {Test-Path -LiteralPath $_ -PathType Leaf} | Select-Object -First 1
if(-not $yamlPath){throw 'Restore the project before testing the production YAML parser.'}
Add-Type -Path $yamlPath
$clashFixture=Join-Path $OutputDirectory ('clash-config-fixture-'+[guid]::NewGuid().ToString('N'))
$fixtureCore='{"mixed-port":17897,"allow-lan":false,"mode":"rule","ipv6":false,"tun":{"device":"ClaudeTun","enable":true,"auto-route":true,"strict-route":true},"dns":{"enable":true},"proxies":[{"name":"SelectedNode","type":"socks5","server":"fixture.invalid","port":1080}],"proxy-groups":[{"name":"Claude","type":"select","proxies":["SelectedNode"]}],"rules":["MATCH,Claude"]}'
Check 'clash-config-preserves-existing-subscriptions-settings-and-types-on-retry' {
    $directory=Join-Path $clashFixture 'existing'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $index=Join-Path $directory 'profiles.yaml'
    [IO.File]::WriteAllText($index,@'
current: old-subscription
items:
  - uid: old-subscription
    type: remote
    name: Existing subscription
    option:
      with_proxy: false
      update_interval: 120
      label: 'false'
'@)
    [IO.File]::WriteAllText((Join-Path $directory 'verge.yaml'),('enable_memory_usage: false'+[char]10+'app_log_max_count: 8'))
    [IO.File]::WriteAllText((Join-Path $directory 'config.yaml'),('log-level: debug'+[char]10+'unified-delay: true'))
    $oldProfile=Join-Path $directory 'profiles\old-subscription.yaml'
    [IO.Directory]::CreateDirectory((Split-Path $oldProfile)) | Out-Null
    [IO.File]::WriteAllText($oldProfile,'proxies: []')
    $before=[IO.File]::ReadAllText($oldProfile)
    Write-ClashConfiguration $directory $fixtureCore
    Write-ClashConfiguration $directory $fixtureCore
    $readback=Read-LocalYaml $index
    $saved=$readback.items[0]
    Assert ($readback.current -eq 'claude-environment-setup' -and $readback.items.Count -eq 2 -and $saved['uid'] -eq 'old-subscription') 'profile selection or existing subscription changed'
    Assert ($saved['option']['with_proxy'] -is [bool] -and -not $saved['option']['with_proxy'] -and $saved['option']['update_interval'] -is [ValueType] -and $saved['option']['label'] -is [string]) 'existing YAML types changed'
    Assert (([IO.File]::ReadAllText($oldProfile)) -ceq $before) 'existing subscription file was rewritten'
    $verge=Read-LocalYaml (Join-Path $directory 'verge.yaml')
    $base=Read-LocalYaml (Join-Path $directory 'config.yaml')
    Assert ($verge['enable_memory_usage'] -is [bool] -and $verge['app_log_max_count'] -is [ValueType] -and $verge['enable_tun_mode'] -and $verge['clash_core'] -eq 'verge-mihomo') 'Clash retained or applied settings are invalid'
    Assert ($base['log-level'] -eq 'debug' -and $base['unified-delay'] -is [bool] -and $base['tun']['strict-route'] -and $base['mixed-port'] -eq 17897) 'runtime configuration fields changed'
    $owned=Join-Path $directory 'profiles\claude-environment-setup.yaml'
    Assert (([IO.File]::ReadAllText($owned)) -ceq $fixtureCore) 'profile received by hash verification differs from payload'
    Assert (@(Get-ChildItem -LiteralPath $directory -Recurse -Filter '*.tmp').Count -eq 0) 'configuration left temporary files'
}
Check 'clash-config-supports-clean-guest-and-preserves-files-on-invalid-input' {
    $clean=Join-Path $clashFixture 'new'
    Write-ClashConfiguration $clean $fixtureCore
    Assert ((Read-LocalYaml (Join-Path $clean 'profiles.yaml')).items.Count -eq 1) 'fresh guest configuration missing'
    $index=Join-Path $clean 'profiles.yaml'
    $before=[IO.File]::ReadAllText($index)
    $broken='items: [{uid: unfinished'
    $base=Join-Path $clean 'config.yaml'
    [IO.File]::WriteAllText($base,$broken)
    $rejected=$false
    try{Write-ClashConfiguration $clean $fixtureCore}catch{
        if($_.Exception.Message -ne 'CLASH_CONFIG_REVIEW_REQUIRED'){throw}
        $rejected=$true
    }
    Assert ($rejected -and ([IO.File]::ReadAllText($index)) -ceq $before -and ([IO.File]::ReadAllText($base)) -ceq $broken) 'invalid config was overwritten or accepted'
}
$rollback=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\Rollback.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'ROLLBACK_SOURCE_PARSE_FAILED'}
$restore=$rollback.Find({param($a)$a -is [Management.Automation.Language.IfStatementAst] -and $a.Clauses[0].Item1.Extent.Text -eq 'Test-Path -LiteralPath $startupRecord'},$true)
if(-not $restore){throw 'STARTUP_ROLLBACK_MISSING'}
$restoreStartup=[scriptblock]::Create($restore.Extent.Text)
& {
    function Test-Path($LiteralPath,$PathType) {
        if($LiteralPath -like 'HKCU:*'){return $script:fixtureKeyExists}
        return (Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath)
    }
    function Get-Item($LiteralPath) {
        Assert ($LiteralPath -like 'HKCU:*' -and $script:fixtureKeyExists) 'read of an unavailable startup key'
        $key=[pscustomobject]@{Property=@($script:fixtureRegistry.Keys)}
        $key | Add-Member ScriptMethod GetValue {
            param($name,$default,$options)
            if($name -ne 'ClaudomeClash' -or $options -ne [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames){throw 'Unexpected startup value read'}
            if($script:fixtureRegistry.ContainsKey($name)){return $script:fixtureRegistry[$name].data}
            return $default
        }
        $key | Add-Member ScriptMethod GetValueKind {param($name) return $script:fixtureRegistry[$name].kind}
        $key | Add-Member ScriptMethod Dispose {}
        return $key
    }
    function New-Item {
        param($Path,[switch]$Force)
        Assert ($Path -like 'HKCU:*' -and -not $script:fixtureKeyExists -and -not $Force) 'existing Run key must not be recreated'
        $script:fixtureKeyExists=$true
    }
    function New-ItemProperty {
        param($LiteralPath,$Name,$Value,$PropertyType,[switch]$Force)
        Assert ($LiteralPath -like 'HKCU:*' -and $Name -eq 'ClaudomeClash') 'write outside owned startup value'
        Assert ($script:fixtureKeyExists) 'write before startup key exists'
        $script:fixtureRegistry[$Name]=@{data=$Value;kind=$PropertyType}
    }
    function Remove-ItemProperty($LiteralPath,$Name) {
        Assert ($LiteralPath -like 'HKCU:*' -and $Name -eq 'ClaudomeClash' -and $script:fixtureRegistry.ContainsKey($Name)) 'remove attempted on an unrelated or absent startup value'
        $script:fixtureRegistry.Remove($Name)
    }
    Check 'clash-first-startup-accepts-existing-run-key-without-our-value-and-rolls-back' {
        $script:fixtureKeyExists=$true
        $script:fixtureRegistry=@{OtherApp=@{data='keep';kind='String'}}
        $startupRecord=Join-Path $clashFixture 'new/registry-before.json'
        $app='C:\Fixture Directory\Clash.exe'
        Set-ClaudomeClashStartup $startupRecord $app
        Assert ($script:fixtureRegistry.ClaudomeClash.data -eq ('"'+$app+'"')) 'missing startup value was not created'
        $saved=Get-Content -LiteralPath $startupRecord -Raw
        Assert (-not (ConvertFrom-Json $saved).existed) 'the newly created startup value was recorded as old'
        Set-ClaudomeClashStartup $startupRecord $app
        Assert ((Get-Content -LiteralPath $startupRecord -Raw) -ceq $saved) 'retry overwrote the original startup record'
        & $restoreStartup
        & $restoreStartup
        Assert ($script:fixtureRegistry.Count -eq 1 -and $script:fixtureRegistry.OtherApp.data -eq 'keep') 'rollback removed unrelated startup data'
    }
    Check 'clash-startup-restores-an-existing-expandable-value' {
        $script:fixtureKeyExists=$true
        $script:fixtureRegistry=@{ClaudomeClash=@{data='%LOCALAPPDATA%\Old\Clash.exe';kind='ExpandString'}}
        $startupRecord=Join-Path $clashFixture 'existing/registry-before.json'
        Set-ClaudomeClashStartup $startupRecord 'C:\Fixture\Clash.exe'
        Assert ($script:fixtureRegistry.ClaudomeClash.kind -eq 'String') 'startup command was not updated'
        & $restoreStartup
        Assert ($script:fixtureRegistry.ClaudomeClash.kind -eq 'ExpandString' -and $script:fixtureRegistry.ClaudomeClash.data -eq '%LOCALAPPDATA%\Old\Clash.exe') 'prior startup type or unexpanded path was lost'
    }
    Check 'clash-startup-creates-missing-key-and-keeps-an-incompatible-value' {
        $script:fixtureKeyExists=$false
        $script:fixtureRegistry=@{}
        $startupRecord=Join-Path $clashFixture 'new/missing-key.json'
        Set-ClaudomeClashStartup $startupRecord 'C:\Fixture\Clash.exe'
        Assert ($script:fixtureKeyExists -and $script:fixtureRegistry.ContainsKey('ClaudomeClash')) 'missing key was not created'
        $script:fixtureRegistry.ClaudomeClash=@{data=7;kind='DWord'}
        $startupRecord=Join-Path $clashFixture 'new/conflict.json'
        $rejected=$false
        try{Set-ClaudomeClashStartup $startupRecord 'C:\Fixture\Clash.exe'}catch{
            if($_.Exception.Message -ne 'CLASH_STARTUP_VALUE_CONFLICT'){throw}
            $rejected=$true
        }
        Assert ($rejected -and $script:fixtureRegistry.ClaudomeClash.kind -eq 'DWord') 'incompatible setting was overwritten'
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$failed=@($rows | Where-Object {-not $_.passed}).Count
@{type='production function AST with synthetic stubs only; no installed software or VM touched';passed=$rows.Count-$failed;failed=$failed;tests=@($rows)} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'experience-tests.json') -Encoding utf8NoBOM
Write-Output ('Experience passed='+($rows.Count-$failed)+' failed='+$failed)
if($failed){$rows | Where-Object {-not $_.passed} | ConvertTo-Json;exit 1}
