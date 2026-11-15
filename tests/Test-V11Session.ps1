#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
$pkg=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $pkg 'tools\V11\Deployment.psm1') -Force -DisableNameChecking
$area=Join-Path $OutputRoot ('v11-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $area 'work') -Force | Out-Null
$results=New-Object 'Collections.Generic.List[object]'
function Check([string]$Name,[scriptblock]$Test){
    try{& $Test;$results.Add([pscustomobject]@{name=$Name;status='PASS'})}
    catch{$results.Add([pscustomobject]@{name=$Name;status='FAIL';error=$_.Exception.Message})}
}
function Assert($Condition,[string]$Why){if(-not $Condition){throw $Why}}
function Reject([scriptblock]$Code,[string]$Expected){
    $caught=$false
    try{& $Code | Out-Null}catch{$caught=$true;Assert ($_.Exception.Message -like ('*'+$Expected+'*')) 'unexpected_error_code'}
    Assert $caught 'operation_unexpectedly_allowed'
}
$inputFile=Join-Path $area 'input.txt'
$fake='V11-test-only-83#=$abc'
$base="EXECUTION_PLACE=GuestLocal`nTARGET_VM_NAME=FixtureVM`nVM_USERNAME=fixture-user`nVM_PASSWORD=$fake`nPROXY_SUBSCRIPTION_URL=https://example.invalid/sub?token=fixture-only`nEXPECTED_EXIT_IPV4=192.0.2.10"
[IO.File]::WriteAllText($inputFile,$base,(New-Object Text.UTF8Encoding($true)))
Check 'default_locked' {Assert ((Get-V11Status $area).mode -eq 'Locked') 'not_locked'}
Check 'password_special_characters_are_data' {Assert ((Read-V11Input $base).VM_PASSWORD -ceq $fake) 'password_changed'}
Check 'duplicate_field_rejected' {Reject {Read-V11Input ($base+"`nVM_PASSWORD=duplicate")} 'INPUT_FIELD_DUPLICATE'}
Check 'unknown_field_rejected' {Reject {Read-V11Input ($base+"`nUNKNOWN=x")} 'INPUT_FIELD_UNKNOWN'}
Check 'invalid_ipv4_rejected' {Reject {Read-V11Input ($base.Replace('192.0.2.10','not-an-ip'))} 'EXPECTED_IPV4_INVALID'}
Check 'execution_place_required' {Reject {Read-V11Input ($base.Replace('GuestLocal','Unknown'))} 'EXECUTION_PLACE_REQUIRED'}
Check 'blank_vm_name_rejected' {Reject {Read-V11Input ($base.Replace('TARGET_VM_NAME=FixtureVM','TARGET_VM_NAME='))} 'TARGET_VM_NAME_REQUIRED'}
Check 'input_expressions_are_not_executed' {
    $marker=Join-Path $area 'must-not-exist.txt'
    $expression='$(New-Item -ItemType File -Path "'+$marker+'")'
    $d=Read-V11Input ($base.Replace($fake,$expression))
    Assert ($d.VM_PASSWORD -ceq $expression) 'expression_changed'
    Assert (-not(Test-Path -LiteralPath $marker)) 'expression_executed'
}
Check 'encrypted_import_and_source_clear' {
    $r=Import-V11Data $area $inputFile
    Assert ($r.imported -and $r.sourceCleared) 'import_failed'
    $cipher=Get-Content -LiteralPath (Join-Path $area 'runtime-private\credentials.dpapi') -Raw
    Assert (-not $cipher.Contains($fake)) 'plaintext_in_store'
    Assert (-not ([IO.File]::ReadAllText($inputFile)).Contains($fake)) 'plaintext_left_in_source'
}
Check 'private_acl_restricted' {
    $acl=Get-Acl -LiteralPath (Join-Path $area 'runtime-private')
    Assert $acl.AreAccessRulesProtected 'inheritance_not_disabled'
    $ours=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    foreach($rule in $acl.Access){Assert ($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -in @($ours,'S-1-5-18')) 'unexpected_acl_identity'}
}
Check 'explicit_start_required' {Reject {Start-V11Session $area ''} 'EXPLICIT_START_REQUIRED'}
Check 'open_single_task' {Assert ((Start-V11Session $area 'START').mode -eq 'Deploying') 'not_open'}
Check 'duplicate_open_rejected' {Reject {Start-V11Session $area 'START'} 'SESSION_ALREADY_OPEN'}
$fixture=Join-Path $area 'work\fixture.ps1'
$body='param([hashtable]$DeploymentData); if($DeploymentData.ContainsKey("VM_PASSWORD")){throw "unrequested_field_received"}; Write-Output $DeploymentData.PROXY_SUBSCRIPTION_URL; Write-Host $DeploymentData.PROXY_SUBSCRIPTION_URL; Write-Output "fixture-ok"'
[IO.File]::WriteAllText($fixture,$body,(New-Object Text.UTF8Encoding($true)))
Check 'script_containment' {Assert ((Resolve-V11Script $area $fixture) -eq $fixture) 'correct_script_rejected';Reject {Resolve-V11Script $area $inputFile} 'SCRIPT_OUTSIDE_TASK_WORKSPACE'}
Check 'junction_escape_rejected' {
    $outside=Join-Path $area 'outside';New-Item -ItemType Directory -Path $outside | Out-Null
    [IO.File]::WriteAllText((Join-Path $outside 'x.ps1'),'param([hashtable]$DeploymentData)')
    New-Item -ItemType Junction -Path (Join-Path $area 'work\link') -Target $outside | Out-Null
    Reject {Resolve-V11Script $area (Join-Path $area 'work\link\x.ps1')} 'SCRIPT_REPARSE_POINT'
}
Check 'guestlocal_job_fields_and_redaction' {
    $before=@(Get-Job).Count
    $output=Invoke-V11Task -Root $area -ScriptPath $fixture -Fields @('PROXY_SUBSCRIPTION_URL') -TimeoutSeconds 30 | Out-String
    Assert ($output.Contains('fixture-ok')) 'script_did_not_run'
    Assert (-not $output.Contains('token=fixture-only')) 'secret_in_output'
    Assert (@(Get-Job).Count -eq $before) 'job_not_cleaned'
}
Check 'unapproved_field_rejected' {Reject {Invoke-V11Task -Root $area -ScriptPath $fixture -Fields @('UNKNOWN')} 'REQUESTED_FIELD_UNKNOWN'}
Check 'failed_script_does_not_echo_exception_secret' {
    $f=Join-Path $area 'work\fail.ps1';[IO.File]::WriteAllText($f,'param([hashtable]$DeploymentData); throw $DeploymentData.VM_PASSWORD')
    Reject {Invoke-V11Task -Root $area -ScriptPath $f -Fields @('VM_PASSWORD') -TimeoutSeconds 30} 'TASK_FAILED'
}
Check 'timeout_stops_and_cleans_job' {
    $f=Join-Path $area 'work\slow.ps1';[IO.File]::WriteAllText($f,'param([hashtable]$DeploymentData); Start-Sleep -Seconds 20')
    $before=@(Get-Job).Count
    Reject {Invoke-V11Task -Root $area -ScriptPath $f -TimeoutSeconds 1} 'TASK_TIMEOUT'
    Assert (@(Get-Job).Count -eq $before) 'timeout_job_not_cleaned'
}
Check 'hyperv_parameters_and_output_mock_only' {
    $remote=Join-Path $area 'remote-fixture'
    New-Item -ItemType Directory -Path (Join-Path $remote 'work') -Force | Out-Null
    $remoteInput=Join-Path $remote 'input.txt'
    [IO.File]::WriteAllText($remoteInput,($base.Replace('GuestLocal','HyperVHost')),(New-Object Text.UTF8Encoding($true)))
    Import-V11Data $remote $remoteInput | Out-Null
    Start-V11Session $remote 'START' | Out-Null
    $f=Join-Path $remote 'work\fixture.ps1';[IO.File]::WriteAllText($f,$body,(New-Object Text.UTF8Encoding($true)))
    & (Get-Module Deployment) {
        function script:Invoke-Command {
            [CmdletBinding()]
            param([string]$VMName,[Management.Automation.PSCredential]$Credential,[scriptblock]$ScriptBlock,[object[]]$ArgumentList,[switch]$AsJob)
            $script:V11Captured=[pscustomobject]@{vm=$VMName;isCredential=($Credential -is [Management.Automation.PSCredential]);argumentCount=$ArgumentList.Count;asJob=[bool]$AsJob}
            Start-Job -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList
        }
    }
    $out=Invoke-V11Task -Root $remote -ScriptPath $f -Fields @('PROXY_SUBSCRIPTION_URL') -TimeoutSeconds 30 | Out-String
    $captured=& (Get-Module Deployment) {$script:V11Captured}
    Assert ($captured.vm -eq 'FixtureVM' -and $captured.isCredential -and $captured.argumentCount -eq 2 -and $captured.asJob) 'hyperv_argument_binding_failed'
    Assert ($out.Contains('fixture-ok') -and -not $out.Contains('token=fixture-only')) 'hyperv_capture_or_redaction_failed'
    Stop-V11Session $remote | Out-Null
}
Check 'hyperv_start_requires_guest_credentials' {
    $r=Join-Path $area 'missing-guest-credentials';New-Item -ItemType Directory -Path $r -Force | Out-Null
    $f=Join-Path $r 'input.txt';[IO.File]::WriteAllText($f,"EXECUTION_PLACE=HyperVHost`nTARGET_VM_NAME=FixtureVM")
    Import-V11Data $r $f | Out-Null
    Reject {Start-V11Session $r 'START'} 'VM_CREDENTIALS_REQUIRED_FOR_POWERSHELL_DIRECT'
    Stop-V11Session $r | Out-Null
}
Check 'password_whitespace_preserved' {
    $d=Read-V11Input ($base.Replace($fake,'  spaced-password  '));Assert ($d.VM_PASSWORD -ceq '  spaced-password  ') 'password_whitespace_changed'
}
Check 'machine_binding' {
    $p=Join-Path $area 'runtime-private\state.json';$saved=[IO.File]::ReadAllText($p)
    try{$s=$saved | ConvertFrom-Json;$s.computer='OTHER-COMPUTER';$s|ConvertTo-Json|Set-Content -LiteralPath $p -Encoding UTF8;Reject {Assert-V11Open $area} 'SESSION_MACHINE_OR_USER_MISMATCH'}finally{[IO.File]::WriteAllText($p,$saved)}
}
Check 'lock_removes_credentials' {Assert ((Stop-V11Session $area).mode -eq 'Locked') 'not_locked';Assert (-not(Get-V11Status $area).credentialsPresent) 'credential_still_present'}
Check 'locked_runner_denies_execution' {Reject {Invoke-V11Task -Root $area -ScriptPath $fixture} 'DEPLOYMENT_LOCKED'}
Check 'locked_task_needs_new_user_material' {Reject {Start-V11Session $area 'START'} 'LOCAL_CREDENTIALS_MISSING'}
Check 'lock_idempotent' {Assert ((Stop-V11Session $area).mode -eq 'Locked') 'second_lock_failed'}
Check 'corrupt_state_fails_closed' {
    $p=Join-Path $area 'runtime-private\state.json';$saved=[IO.File]::ReadAllText($p)
    try{[IO.File]::WriteAllText($p,'broken-json');Reject {Assert-V11Open $area} 'STATE_INVALID'}finally{[IO.File]::WriteAllText($p,$saved)}
}
Check 'lock_accepts_mac_instruction_state' {
    $p=Join-Path $area 'runtime-private\state.json'
    [IO.File]::WriteAllText($p,'{"version":11,"mode":"Deploying","executionPlace":"MacHostInstructionsOnly"}')
    Assert ((Stop-V11Session $area).mode -eq 'Locked') 'foreign_platform_close_failed'
}
Check 'lock_recovers_corrupt_state_safely' {
    $p=Join-Path $area 'runtime-private\state.json';[IO.File]::WriteAllText($p,'broken-json')
    Assert ((Stop-V11Session $area).mode -eq 'Locked') 'corrupt_state_close_failed'
    Assert ((Get-V11Status $area).mode -eq 'Locked') 'state_not_repaired_as_locked'
}
$chineseBase="【执行位置：】Windows虚拟机`n【虚拟机名称：】FixtureVM`n【虚拟机账号：】fixture-user`n【虚拟机密码：】$fake`n【代理订阅链接：】https://example.invalid/sub?token=fixture-only`n【预期出口IPv4：】192.0.2.10"
Check 'customer_template_uses_chinese_bracket_labels' {
    $template=Get-V11Template
    Assert ($template.Contains('【虚拟机账号：】') -and $template.Contains('【虚拟机密码：】') -and -not $template.Contains('VM_USERNAME=')) 'template_not_converted'
    Assert ((Read-V11Input $template).EXECUTION_PLACE -eq 'HyperVHost') 'default_template_invalid'
}
Check 'all_chinese_labels_map_to_existing_fields' {
    $text=$chineseBase+"`n【节点名称：】fixture-node`n【AdsPower账号：】adsp-user`n【AdsPower密码：】adsp-pass`n【Claude账号：】claude-user`n【Claude密码：】claude-pass`n【VPS服务器地址：】server.example.invalid`n【VPS端口：】2222`n【VPS账号：】vps-user`n【VPS密码：】vps-pass"
    $d=Read-V11Input $text
    $expected=@{EXECUTION_PLACE='GuestLocal';TARGET_VM_NAME='FixtureVM';VM_USERNAME='fixture-user';VM_PASSWORD=$fake;PROXY_SUBSCRIPTION_URL='https://example.invalid/sub?token=fixture-only';EXPECTED_EXIT_IPV4='192.0.2.10';PROXY_NODE_NAME='fixture-node';ADSPOWER_USERNAME='adsp-user';ADSPOWER_PASSWORD='adsp-pass';CLAUDE_USERNAME='claude-user';CLAUDE_PASSWORD='claude-pass';VPS_HOST='server.example.invalid';VPS_PORT='2222';VPS_USERNAME='vps-user';VPS_PASSWORD='vps-pass'}
    foreach($key in $expected.Keys){Assert ($d[$key] -ceq $expected[$key]) 'chinese_field_mapping_failed'}
}
Check 'fullwidth_and_ascii_colons_accepted' {Assert ((Read-V11Input ($chineseBase.Replace('：',':'))).VM_PASSWORD -ceq $fake) 'ascii_colon_rejected'}
Check 'malformed_chinese_bracket_rejected' {Reject {Read-V11Input ($chineseBase.Replace('【虚拟机账号：】','【虚拟机账号】'))} 'INPUT_FORMAT_INVALID'}
Check 'brackets_equals_and_password_spaces_preserved' {
    $password='  pass=#$【：】  '
    Assert ((Read-V11Input ($chineseBase.Replace($fake,$password))).VM_PASSWORD -ceq $password) 'bracket_password_changed'
}
Check 'mixed_format_duplicate_rejected' {Reject {Read-V11Input ($chineseBase+"`nVM_PASSWORD=duplicate")} 'INPUT_FIELD_DUPLICATE'}
Check 'chinese_duplicate_rejected' {Reject {Read-V11Input ($chineseBase+"`n【虚拟机账号：】duplicate")} 'INPUT_FIELD_DUPLICATE'}
Check 'unknown_chinese_label_rejected' {Reject {Read-V11Input ($chineseBase+"`n【随机账号：】unknown")} 'INPUT_FIELD_UNKNOWN'}
Check 'chinese_execution_location_normalized' {Assert ((Read-V11Input ($chineseBase.Replace('Windows虚拟机','Windows主机'))).EXECUTION_PLACE -eq 'HyperVHost') 'chinese_execution_place_invalid'}
Check 'chinese_import_execute_and_lock_roundtrip' {
    $r=Join-Path $area 'chinese-end-to-end'
    New-Item -ItemType Directory -Path (Join-Path $r 'work') -Force | Out-Null
    $input=Join-Path $r 'input.txt';[IO.File]::WriteAllText($input,$chineseBase,(New-Object Text.UTF8Encoding($true)))
    Import-V11Data $r $input | Out-Null
    Assert (-not ([IO.File]::ReadAllText($input)).Contains($fake)) 'chinese_source_not_cleared'
    Start-V11Session $r 'START' | Out-Null
    $f=Join-Path $r 'work\fixture.ps1';[IO.File]::WriteAllText($f,$body,(New-Object Text.UTF8Encoding($true)))
    $out=Invoke-V11Task -Root $r -ScriptPath $f -Fields @('PROXY_SUBSCRIPTION_URL') -TimeoutSeconds 30 | Out-String
    Assert ($out.Contains('fixture-ok') -and -not $out.Contains('token=fixture-only')) 'chinese_runtime_failed'
    Stop-V11Session $r | Out-Null
    Reject {Invoke-V11Task -Root $r -ScriptPath $f} 'DEPLOYMENT_LOCKED'
}
$report=[ordered]@{version=11;testType='Windows local runtime and simulated guest inputs; no real VM/network deployment';completedUtc=[DateTime]::UtcNow.ToString('o');passed=@($results|Where-Object status -eq 'PASS').Count;failed=@($results|Where-Object status -eq 'FAIL').Count;tests=$results.ToArray()}
$reportPath=Join-Path $OutputRoot 'V11-session-test-results.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
$results | Format-Table -AutoSize
Write-Host ('Report: '+$reportPath)
if($report.failed -gt 0){exit 1}
