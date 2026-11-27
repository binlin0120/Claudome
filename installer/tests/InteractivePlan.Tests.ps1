#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
# Import only the pure planner function AST. Never execute the guest task code.
$tokens=$null;$errors=$null;$source=Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\InteractiveRun.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'PRODUCTION_PARSE_FAILED'}
$function=$ast.FindAll({param($a)$a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Get-GuestInteractivePlan'},$true) | Select-Object -First 1
if(-not $function){throw 'FUNCTION_MISSING'}
Invoke-Expression $function.Extent.Text
$rows=[Collections.Generic.List[object]]::new();$user='FixtureUser';$vm='11111111-1111-1111-1111-111111111111';$workspace='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$root='C:\Users\FixtureUser\AppData\Local\ClaudeEnvironmentSetup';$exe=$root+'\pwsh\pwsh.exe'
$currentInput=$root+'\payload-0123456789abcdef0123456789abcdef.json'
function Check($Name,[scriptblock]$Body){try{& $Body;$rows.Add(@{name=$Name;passed=$true})}catch{$rows.Add(@{name=$Name;passed=$false;error=$_.Exception.Message})}}
function Reject-Plan($Executable,$Arguments){$blocked=$false;try{$null=Get-GuestInteractivePlan $Executable $Arguments $user $vm $workspace}catch{if($_.Exception.Message -eq 'VM_SCOPE'){$blocked=$true}else{throw}};if(-not $blocked){throw 'UNSCOPED_PLAN_ACCEPTED'}}
Check 'guest-interactive-accepts-only-owned-configure' {
    $p=Get-GuestInteractivePlan $exe @('-NoLogo','-NoProfile','-File',($root+'\Configure.ps1'),'-PayloadPath',$currentInput) $user $vm $workspace
    if($p.root -ne $root -or $p.arguments.Count -ne 6){throw 'VALID_PLAN_REJECTED'}
}
Check 'guest-interactive-supports-bridge-and-rollback' {
    foreach($pair in @(@('BridgeInstall.ps1','-InputPath','bridge-input.json'),@('Rollback.ps1','-PayloadPath','rollback-input.json'),@('Validate.ps1','-PayloadPath','validation-input.json'))){$p=Get-GuestInteractivePlan $exe @('-NoProfile','-File',($root+'\'+$pair[0]),$pair[1],($root+'\'+$pair[2])) $user $vm $workspace;if($p.arguments.Count -ne 5){throw 'VALID_PLAN_REJECTED'}}
}
Check 'guest-interactive-rejects-unowned-executable' {Reject-Plan 'C:\Windows\System32\cmd.exe' @('-NoProfile','-File',($root+'\Configure.ps1'),'-PayloadPath',$currentInput)}
Check 'guest-interactive-rejects-foreign-script-and-payload' {
    Reject-Plan $exe @('-NoProfile','-File','C:\Other\Configure.ps1','-PayloadPath',$currentInput)
    Reject-Plan $exe @('-NoProfile','-File',($root+'\Configure.ps1'),'-PayloadPath',('C:\Other\'+[IO.Path]::GetFileName($currentInput)))
}
Check 'guest-interactive-rejects-stale-or-misnamed-configure-input' {
    Reject-Plan $exe @('-NoProfile','-File',($root+'\Configure.ps1'),'-PayloadPath',($root+'\payload.json'))
    Reject-Plan $exe @('-NoProfile','-File',($root+'\Configure.ps1'),'-PayloadPath',($root+'\payload-0123456789abcdef0123456789abcdeg.json'))
}
Check 'validator-cannot-reuse-installation-payload-or-foreign-path' {
    Reject-Plan $exe @('-NoProfile','-File',($root+'\Validate.ps1'),'-PayloadPath',$currentInput)
    Reject-Plan $exe @('-NoProfile','-File',($root+'\Validate.ps1'),'-PayloadPath','C:\Other\validation-input.json')
}
Check 'guest-interactive-rejects-command-injection-and-extra-switch' {
    Reject-Plan $exe @('-Command','-File',($root+'\Configure.ps1'),'-PayloadPath',$currentInput)
    Reject-Plan $exe @('-NoProfile','-File',($root+'\Configure.ps1'),'-PayloadPath',$currentInput,'-Command','anything')
    Reject-Plan $exe @('-NoProfile','-File',($root+'\Configure.ps1'),'-PayloadPath',$currentInput,'-Quick')
    Reject-Plan $exe @('-NoProfile','-File',($root+'\Validate.ps1'),'-PayloadPath',($root+'\validation-input.json'),'-Other')
}
Check 'guest-interactive-accepts-validate-quick-switch' {
    $p=Get-GuestInteractivePlan $exe @('-NoProfile','-File',($root+'\Validate.ps1'),'-PayloadPath',($root+'\validation-input.json'),'-Quick') $user $vm $workspace
    if($p.arguments.Count -ne 6 -or $p.arguments[5] -ne '-Quick'){throw 'VALID_QUICK_PLAN_REJECTED'}
}
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$failed=@($rows | Where-Object {-not $_.passed}).Count
@{type='pure production planner AST only; no guest, scheduled task or host mutation';passed=$rows.Count-$failed;failed=$failed;tests=@($rows)} | ConvertTo-Json -Depth 5 | ForEach-Object {[IO.File]::WriteAllText((Join-Path $OutputDirectory 'interactive-plan-tests.json'),$_,[Text.UTF8Encoding]::new($false))}
Write-Output ('Interactive plan fixtures passed='+($rows.Count-$failed)+' failed='+$failed)
if($failed){exit 1}
