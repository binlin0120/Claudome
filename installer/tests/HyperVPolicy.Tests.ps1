#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
# Evaluate only selected production function ASTs. Every external operation in
# those functions is replaced below by in-memory fakes. No Hyper-V module, real
# registry provider, VM operation or elevation is used.
$worker=Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\HyperVWorker.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($worker,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'PRODUCTION_PARSE_FAILED'}
$names=@('Invoke-ConsolePolicyLock','Console-Owner','Use-BasicConsolePolicy','Guard-Console','Verify-Console','Restore-Console','Assert-NoNetworkAdapter','Disable-HostMetadata','Bus-ServicePath','Verify-BusService','Test-VMKeyProtectorPresent','Enable-TaskVirtualTpm')
foreach($name in $names){$definition=$ast.FindAll({param($a)$a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $name},$true) | Select-Object -First 1;if(-not $definition){throw 'FUNCTION_MISSING'};Invoke-Expression -Command $definition.Extent.Text}
$script:store=@{};$script:policyEnabled=$true;$script:nicCount=0;$script:metadataEnabled=$true;$script:changes=0
$script:o=[pscustomobject]@{WorkspaceId='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';HyperVBasicConsoleConfirmed=$false}
function Get-VMHost {[pscustomobject]@{EnableEnhancedSessionMode=$script:policyEnabled}}
function Set-VMHost([bool]$EnableEnhancedSessionMode){$script:policyEnabled=$EnableEnhancedSessionMode;$script:changes++}
function Test-Path([string]$LiteralPath){$script:store.ContainsKey($LiteralPath)}
function New-Item([string]$Path,[switch]$Force){$part=$Path;while($part.Contains('\')){if(-not $script:store.ContainsKey($part)){$script:store[$part]=@{}};$part=$part.Substring(0,$part.LastIndexOf('\'))}}
function New-ItemProperty([string]$LiteralPath,[string]$Name,$Value,[string]$PropertyType){$script:store[$LiteralPath][$Name]=$Value}
function Get-ItemPropertyValue([string]$LiteralPath,[string]$Name){if(-not $script:store.ContainsKey($LiteralPath) -or -not $script:store[$LiteralPath].ContainsKey($Name)){throw 'FAKE_VALUE_MISSING'};$script:store[$LiteralPath][$Name]}
function Remove-Item([string]$LiteralPath){$script:store.Remove($LiteralPath)}
function Remove-ItemProperty([string]$LiteralPath,[string]$Name){$script:store[$LiteralPath].Remove($Name)}
function Get-ChildItem {
    [CmdletBinding()]param([string]$LiteralPath)
    foreach($key in @($script:store.Keys)){if($key.StartsWith($LiteralPath+'\') -and -not $key.Substring($LiteralPath.Length+1).Contains('\')){[pscustomobject]@{PSPath=$key}}}
}
function Get-VMNetworkAdapter($VM){for($i=0;$i -lt $script:nicCount;$i++){[pscustomobject]@{Name='fake'}}}
function Get-VMIntegrationService($VM){[pscustomobject]@{Id=[guid]'2a34b1c2-fd73-4043-8a5b-dd2159bc743f';Name='Key-Value Pair Exchange';Enabled=$script:metadataEnabled}}
function Disable-VMIntegrationService {param([Parameter(ValueFromPipeline)]$InputObject)process{$script:metadataEnabled=$false}}
$first=[pscustomobject]@{Id=[guid]'11111111-1111-1111-1111-111111111111';State='Off'}
$second=[pscustomobject]@{Id=[guid]'22222222-2222-2222-2222-222222222222';State='Off'}
$rows=[Collections.Generic.List[object]]::new()
function Assert-Case([bool]$Condition){if(-not $Condition){throw 'ASSERTION_FAILED'}}
function Check([string]$Name,[scriptblock]$Body){try{& $Body;$rows.Add(@{name=$Name;passed=$true})}catch{$rows.Add(@{name=$Name;passed=$false;error=$_.Exception.Message})}}
Check 'console-change-needs-explicit-consent' {
    $blocked=$false;try{Guard-Console $first}catch{if($_.Exception.Message -eq 'HYPERV_CONSOLE_CONSENT'){$blocked=$true}else{throw}}
    Assert-Case ($blocked -and $script:policyEnabled -and $script:changes -eq 0 -and $script:store.Count -eq 0)
}
Check 'console-owner-protection-and-original-value' {
    $script:o.HyperVBasicConsoleConfirmed=$true;Guard-Console $first;Verify-Console $first
    Assert-Case (-not $script:policyEnabled -and $script:changes -eq 1)
}
Check 'console-verifier-rejects-policy-changes' {
    $script:policyEnabled=$true;$blocked=$false;try{Verify-Console $first}catch{if($_.Exception.Message -eq 'HYPERV_CONSOLE_POLICY'){$blocked=$true}else{throw}}
    Assert-Case $blocked;$script:policyEnabled=$false
}
Check 'console-does-not-restore-while-guest-running' {
    $first.State='Running';$blocked=$false;try{Restore-Console $first}catch{if($_.Exception.Message -eq 'HYPERV_CONSOLE_SHUTDOWN'){$blocked=$true}else{throw}}
    Assert-Case ($blocked -and -not $script:policyEnabled);$first.State='Off'
}
Check 'console-multiple-environments-retain-protection' {
    Guard-Console $second;$result=Restore-Console $first;Assert-Case ($result -eq 'kept-for-other-environments' -and -not $script:policyEnabled)
}
Check 'console-last-owner-restores-recorded-original' {
    $result=Restore-Console $second;Assert-Case ($result -eq 'restored' -and $script:policyEnabled -and -not $script:store.ContainsKey('HKLM:\SOFTWARE\Claudome\HyperVConsolePolicy'))
}
Check 'shared-clipboard-mode-allows-enhanced-session-without-consent-stop' {
    $savedO=$script:o
    try {
        $script:policyEnabled=$false
        $script:o=[pscustomobject]@{WorkspaceId='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';HyperVBasicConsoleConfirmed=$false;SharedClipboardEnabled=$true}
        Guard-Console $first
        Verify-Console $first
        Assert-Case ($script:policyEnabled)
        $script:policyEnabled=$false
        $blocked=$false;try{Verify-Console $first}catch{if($_.Exception.Message -eq 'HYPERV_CONSOLE_POLICY'){$blocked=$true}else{throw}}
        Assert-Case $blocked
        $script:policyEnabled=$true
        Assert-Case ((Restore-Console $first) -eq 'restored' -and -not $script:policyEnabled)
    } finally {
        $script:o=$savedO
        $script:policyEnabled=$true
    }
}
Check 'zero-nic-rejects-any-additional-adapter' {
    $script:nicCount=0;Assert-NoNetworkAdapter $first;$script:nicCount=1;$blocked=$false;try{Assert-NoNetworkAdapter $first}catch{if($_.Exception.Message -eq 'BOUNDARY_NICS'){$blocked=$true}else{throw}}
    Assert-Case $blocked;$script:nicCount=0
}
Check 'host-metadata-disabled-by-actual-service-id' {
    Disable-HostMetadata $first;Assert-Case (-not $script:metadataEnabled)
}
# Extract only the shutdown case. Its VM and job commands below are in-memory fakes.
$shutdownClause=$ast.FindAll({param($node)$node -is [Management.Automation.Language.SwitchStatementAst]},$true) |
    ForEach-Object {$_.Clauses} | Where-Object {$_.Item1.Value -ceq 'shutdown'}
if(@($shutdownClause).Count -ne 1){throw 'SHUTDOWN_CASE_MISSING'}
$script:shutdown=[scriptblock]::Create("switch ('shutdown') {'shutdown' "+$shutdownClause.Item2.Extent.Text+'};$value')
$script:shutdownTarget=$first
$script:shutdownCalls=0
$script:shutdownTimesOut=$false
function Target-VM($Data){
    if($Data.vmId -ne $script:shutdownTarget.Id){throw 'VM_SCOPE'}
    $script:shutdownTarget
}
function Stop-VM {
    param($VM,[switch]$AsJob,[switch]$Confirm)
    Assert-Case ($VM.Id -eq $script:shutdownTarget.Id -and $AsJob -and !$Confirm -and $args.Count -eq 0)
    $script:shutdownCalls++
    [pscustomobject]@{State='Running'}
}
function Wait-Job($Job,$Timeout){
    Assert-Case ($Timeout -eq 75)
    if(!$script:shutdownTimesOut){$Job.State='Completed';$Job}
}
function Receive-Job {[CmdletBinding()]param($Job);$script:shutdownTarget.State='Off'}
function Stop-Job {[CmdletBinding()]param($Job);$Job.State='Stopped'}
function Remove-Job {[CmdletBinding()]param($Job);Assert-Case ($Job.State -in @('Completed','Stopped'))}
Check 'hyperv-shutdown-requests-soft-stop-and-checks-off-state' {
    $d=@{vmId=$first.Id}
    $first.State='Running'
    Assert-Case ((& $script:shutdown) -eq 'off' -and $script:shutdownCalls -eq 1)
    Assert-Case ((& $script:shutdown) -eq 'off' -and $script:shutdownCalls -eq 1)
    $first.State='Running'
    $script:shutdownTimesOut=$true
    $blocked=$false
    try{& $script:shutdown}catch{if($_.Exception.Message -eq 'GUEST_SHUTDOWN_PENDING'){$blocked=$true}else{throw}}
    Assert-Case ($blocked -and $first.State -eq 'Running' -and $script:shutdownCalls -eq 2 -and $second.State -eq 'Off')
}
$script:protector=$null
$script:tpm=$false
$script:protectorWrites=0
$script:tpmEnables=0
$script:badProtectorWrite=$false
$script:badProtectorRead=$false
$script:badTpmEnable=$false
function Get-VMSecurity($VM){[pscustomobject]@{TpmEnabled=$script:tpm}}
function Get-VMKeyProtector($VM){
    if($script:badProtectorRead){throw 'fixture read failed'}
    return ,$script:protector
}
function Set-VMKeyProtector($VM,[switch]$NewLocalKeyProtector){
    Assert-Case ($VM.Id -eq $first.Id -and $NewLocalKeyProtector)
    $script:protectorWrites++
    if(-not $script:badProtectorWrite){$script:protector=[byte[]]@(5,6,7)}
}
function Enable-VMTPM($VM){
    Assert-Case ($VM.Id -eq $first.Id)
    $script:tpmEnables++
    if($script:badTpmEnable){throw 'fixture provider rejected protector'}
    Assert-Case ($script:protector.Length -gt 0)
    $script:tpm=$true
}
Check 'hyperv-empty-key-protector-is-provisioned-and-read-back-before-vtpm' {
    $script:protector=[byte[]]::new(0)
    Assert-Case (@(Get-VMKeyProtector -VM $first).Count -eq 1)
    Enable-TaskVirtualTpm $first
    Enable-TaskVirtualTpm $first
    Assert-Case ($script:tpm -and $script:protectorWrites -eq 1 -and $script:tpmEnables -eq 1)
}
Check 'hyperv-existing-protector-is-preserved-when-tpm-provider-rejects-it' {
    $script:tpm=$false
    $script:protector=[byte[]]@(8,9,10)
    $script:badTpmEnable=$true
    $stopped=$false
    try{Enable-TaskVirtualTpm $first}catch{if($_.Exception.Message -eq 'VM_TPM_ENABLE_FAILED'){$stopped=$true}else{throw}}
    $script:badTpmEnable=$false
    Assert-Case ($stopped -and !$script:tpm -and $script:protectorWrites -eq 1 -and $script:protector[0] -eq 8)
}
Check 'hyperv-protector-setup-and-read-failures-stop-before-vtpm' {
    $script:protector=$null
    $before=$script:tpmEnables
    $script:badProtectorRead=$true
    $readFailed=$false
    try{Enable-TaskVirtualTpm $first}catch{if($_.Exception.Message -eq 'VM_KEY_PROTECTOR_READ_FAILED'){$readFailed=$true}else{throw}}
    $script:badProtectorRead=$false
    $script:badProtectorWrite=$true
    $setupFailed=$false
    try{Enable-TaskVirtualTpm $first}catch{if($_.Exception.Message -eq 'VM_KEY_PROTECTOR_SETUP_FAILED'){$setupFailed=$true}else{throw}}
    Assert-Case ($readFailed -and $setupFailed -and $script:tpmEnables -eq $before -and $script:protectorWrites -eq 2)
}
# Call the real .NET file API directly; the mock registry provider remains scoped
# to this test process, not the user's PowerShell session or machine.
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$failed=@($rows | Where-Object {-not $_.passed}).Count
@{type='production function AST with in-memory registry and Hyper-V fakes';passed=$rows.Count-$failed;failed=$failed;tests=@($rows)} | ConvertTo-Json -Depth 6 | ForEach-Object {[IO.File]::WriteAllText((Join-Path $OutputDirectory 'hyperv-policy-tests.json'),$_,[Text.UTF8Encoding]::new($false))}
Write-Output ('Hyper-V policy fixtures passed='+($rows.Count-$failed)+' failed='+$failed)
if($failed){$rows | Where-Object {-not $_.passed} | ConvertTo-Json -Depth 5;exit 1}
