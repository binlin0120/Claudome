#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'

$tokens = $null; $errors = $null
$worker = Join-Path $PSScriptRoot '..\ClaudeSetup\Guest\HyperVWorker.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($worker, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'WORKER_PARSE_FAILED' }
foreach ($name in 'Get-GuestSessionFailure', 'Get-HyperVGuestStatus', 'Read-GuestBootstrap', 'Copy-VerifiedGuestResource', 'Send-VerifiedGuestResource') {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (!$definition) { throw "Missing production function $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$root = Join-Path $OutputDirectory ('initialization-fixture-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$vm = [pscustomobject]@{ Id = [guid]::NewGuid() }
$o = [pscustomobject]@{ WorkspaceId = [guid]::NewGuid().ToString('N'); UserName = 'FixtureUser' }
$identity = @{ vmId = [string]$vm.Id; workspaceId = $o.WorkspaceId }
$data = @{ vmId = [string]$vm.Id }
$statusPath = Join-Path $root 'initialize-status.json'
$lockPath = Join-Path $root 'initialize.lock'
$identity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'identity.json')
$controls = @{ Setup = 0; Oobe = 0; Named = 'OK'; Fallback = 'OK'; Attempts = 0; Closed = 0; Legacy = $false; CopyFault = ''; Copies = 0 }
$rows = [Collections.Generic.List[object]]::new()

function Target-VM($Data) {
    if ($Data.vmId -ne [string]$vm.Id) { throw 'VM_SCOPE' }
    return $vm
}
function Guest-Session($Data, [switch]$Default) {
    $controls.Attempts++
    $mode = if ($Default) { $controls.Fallback } else { $controls.Named }
    switch ($mode) {
        'OK' { return [pscustomobject]@{ State = 'Opened' } }
        'WrongPassword' { throw [ComponentModel.Win32Exception]::new(1326) }
        'Denied' { Write-Error -Message 'synthetic access denied' -Category PermissionDenied }
        default { throw 'Endpoint did not start' }
    }
}
function Remove-PSSession($Session) { $controls.Closed++ }
function Invoke-Command($Session, $ScriptBlock, [object[]]$ArgumentList) {
    $scope = $ScriptBlock.ToString().Replace('C:\ProgramData\ClaudeBootstrap', $root.Replace("'", "''"))
    $scope = $scope.Replace('"C:\Users\$user\AppData\Local\ClaudeEnvironmentSetup"', ("'" + $root.Replace("'", "''") + "'"))
    & ([scriptblock]::Create($scope)) @ArgumentList
}
function Copy-Item($LiteralPath, $Destination, $ToSession, [switch]$Force) {
    $controls.Copies++
    switch ($controls.CopyFault) {
        'Transport' {
            $controls.CopyFault = ''
            $ToSession.State = 'Broken'
            [IO.File]::WriteAllText($Destination, 'incomplete')
            throw 'injected transport failure'
        }
        'Denied' { throw [UnauthorizedAccessException]::new('injected file denial') }
        'Hash' { [IO.File]::WriteAllText($Destination, 'unexpected file bytes'); return }
    }
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
}
function Get-ItemProperty($Path) { [pscustomobject]@{ SystemSetupInProgress = $controls.Setup; OOBEInProgress = $controls.Oobe } }
function Get-CimInstance($ClassName, $Filter, $ErrorAction) {
    if ($controls.Legacy) {
        [pscustomobject]@{ ExecutablePath = (Join-Path $root 'pwsh\pwsh.exe'); CommandLine = (Join-Path $root 'Initialize.ps1') }
    }
}
function Write-Status($State, $Phase, $Workspace = $o.WorkspaceId) {
    @{ vmId = $identity.vmId; workspaceId = $Workspace; state = $State; phase = $Phase } |
        ConvertTo-Json | Set-Content -LiteralPath $statusPath
}
function Expect($Actual, $Expected) {
    if ($Actual -cne $Expected) { throw "Expected $Expected, got $Actual" }
}
function Check($Name, [scriptblock]$Test) {
    try { & $Test; $rows.Add(@{ name = $Name; passed = $true }) }
    catch { $rows.Add(@{ name = $Name; passed = $false; detail = $_.Exception.Message }) }
}

Check 'named session authenticates and verifies legacy bootstrap identity' {
    Expect (Get-HyperVGuestStatus $data) 'Ready'
    Expect $controls.Closed 1
}
Check 'Windows setup remains pending even when the fallback is available' {
    $controls.Setup = 1; $controls.Named = 'Missing'
    try {
        Expect (Get-HyperVGuestStatus $data) 'WindowsSetup'
        $controls.Setup = 0; $controls.Oobe = 1
        Expect (Get-HyperVGuestStatus $data) 'WindowsSetup'
    } finally { $controls.Setup = 0; $controls.Oobe = 0; $controls.Named = 'OK' }
}
Check 'credential rejection does not start a fallback session' {
    $controls.Named = 'WrongPassword'; $before = $controls.Attempts
    try {
        Expect (Get-HyperVGuestStatus $data) 'CredentialsRejected'
        Expect ($controls.Attempts - $before) 1
    } finally { $controls.Named = 'OK' }
}
Check 'permission denial does not start a fallback session' {
    $controls.Named = 'Denied'; $before = $controls.Attempts
    try {
        Expect (Get-HyperVGuestStatus $data) 'AccessDenied'
        Expect ($controls.Attempts - $before) 1
    } finally { $controls.Named = 'OK' }
}
Check 'missing named endpoint can be distinguished from a transient interruption' {
    $controls.Named = 'Missing'
    try {
        Expect (Get-HyperVGuestStatus $data) 'EndpointUnavailable'
        $controls.Fallback = 'Missing'
        Expect (Get-HyperVGuestStatus $data) 'SessionUnavailable'
        $controls.Named = 'OK'
        Expect (Get-HyperVGuestStatus $data) 'Ready'
    } finally { $controls.Fallback = 'OK'; $controls.Named = 'OK' }
}
Check 'active older initialization is detected without relaunching it' {
    $controls.Legacy = $true
    try { Expect (Get-HyperVGuestStatus $data) 'Initializing' }
    finally { $controls.Legacy = $false }
}
Check 'resume reports current remoting phase while another initialize process holds the lock' {
    Write-Status 'running' 'remoting'
    $hold = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
    try { Expect (Get-HyperVGuestStatus $data) 'PreparingRemoting' }
    finally { $hold.Dispose() }
    Expect (Get-HyperVGuestStatus $data) 'FailedPreparingRemoting'
}
Check 'failed endpoint initialization is distinguishable from a ready record' {
    Write-Status 'failed' 'endpoint'
    Expect (Get-HyperVGuestStatus $data) 'FailedVerifyingEndpoint'
    Write-Status 'ready' 'endpoint'
    Expect (Get-HyperVGuestStatus $data) 'Ready'
}
Check 'a status record tied to a different task is rejected' {
    Write-Status 'ready' 'endpoint' 'different-workspace'
    $detail = ''
    try { Get-HyperVGuestStatus $data | Out-Null }
    catch { $detail = $_.Exception.Message }
    Expect $detail 'VM_SCOPE'
}
Check 'stale running status without lock is recoverable' {
    Write-Status 'running' 'loopback'
    Expect (Get-HyperVGuestStatus $data) 'FailedPreparingLoopback'
}

$source = Join-Path $root 'sample-source.zip'
$destination = Join-Path $root 'sample-resource.zip'
[IO.File]::WriteAllText($source, 'verified installer fixture')
$transfer = @{ destination = $destination; sha256 = (Get-FileHash -LiteralPath $source).Hash; size = (Get-Item -LiteralPath $source).Length }
Check 'verified resource resumes in a new session after an interrupted transfer' {
    [IO.File]::WriteAllText($destination, 'old installer')
    $controls.CopyFault = 'Transport'; $before = $controls.Attempts
    Send-VerifiedGuestResource $transfer $source
    Expect ($controls.Attempts - $before) 2
    Expect ([IO.File]::ReadAllText($destination)) 'verified installer fixture'
    Expect @(Get-ChildItem -LiteralPath $root -Filter '*.claudome-transfer-*.tmp').Count 0
}
Check 'a matching resource is reused without transferring' {
    $before = $controls.Copies
    Send-VerifiedGuestResource $transfer $source
    Expect ($controls.Copies - $before) 0
}
Check 'permanent write rejection preserves the previous resource without retrying' {
    [IO.File]::WriteAllText($destination, 'old installer')
    $controls.CopyFault = 'Denied'; $before = $controls.Attempts
    $detail = ''
    try { Send-VerifiedGuestResource $transfer $source }
    catch { $detail = $_.Exception.Message }
    finally { $controls.CopyFault = '' }
    Expect $detail 'injected file denial'
    Expect ($controls.Attempts - $before) 1
    Expect ([IO.File]::ReadAllText($destination)) 'old installer'
}
Check 'hash verification rejects corrupt bytes and preserves the previous resource' {
    $controls.CopyFault = 'Hash'; $before = $controls.Attempts
    $detail = ''
    try { Send-VerifiedGuestResource $transfer $source }
    catch { $detail = $_.Exception.Message }
    finally { $controls.CopyFault = '' }
    Expect $detail 'GUEST_COPY_HASH'
    Expect ($controls.Attempts - $before) 1
    Expect ([IO.File]::ReadAllText($destination)) 'old installer'
    Expect @(Get-ChildItem -LiteralPath $root -Filter '*.claudome-transfer-*.tmp').Count 0
}

$failed = @($rows | Where-Object { !$_.passed })
@{ checks = @($rows.ToArray()); passed = $rows.Count - $failed.Count; failed = $failed.Count } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'hyperv-initialization-tests.json')
if ($failed.Count) { throw ('HYPERV_INITIALIZATION_FAILURE: ' + ($failed | ConvertTo-Json -Compress)) }
