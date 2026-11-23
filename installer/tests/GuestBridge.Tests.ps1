#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'

function Invoke-Fixture([string]$Mode, [string]$Directory, [string]$Source) {
    $root = Join-Path $Directory 'component'
    $inputPath = Join-Path $Directory 'bridge-input.json'
    $identityPath = Join-Path $Directory 'identity.json'
    $sourceExe = Join-Path $Directory 'ClaudomeGuestNetwork.exe'
    $targetExe = Join-Path $root 'ClaudomeGuestNetwork.exe'
    $targetConfig = Join-Path $root 'bridge.json'
    $statusPath = Join-Path $root 'status.json'
    $vm = '11111111-1111-1111-1111-111111111111'
    $workspace = '22222222-2222-2222-2222-222222222222'
    $trace = [Collections.Generic.List[string]]::new()
    [IO.Directory]::CreateDirectory($root) | Out-Null
    [IO.File]::WriteAllText($sourceExe, 'new component')
    [IO.File]::WriteAllText($targetExe, 'original component')
    [IO.File]::WriteAllText($targetConfig, 'original configuration')
    @{ vmId = $vm; workspaceId = $workspace } | ConvertTo-Json | Set-Content -LiteralPath $identityPath
    @{ spec = @{ VmId = $vm; WorkspaceId = $workspace; Platform = 'hyperv' }; executableSha256 = (Get-FileHash -LiteralPath $sourceExe).Hash } |
        ConvertTo-Json | Set-Content -LiteralPath $inputPath
    $mock = @{ Task = 'old'; Running = $true }
    $held = $null
    try {
        if ($Mode -eq 'locked-target') {
            $held = [IO.File]::Open($targetExe, 'Open', 'Read', 'Read')
        }
        function Get-CimInstance { param($ClassName)
            [pscustomobject]@{ Manufacturer = 'Microsoft Corporation'; Model = 'Virtual Machine' }
        }
        function Set-Acl { param($LiteralPath, $AclObject) }
        function Get-Process { param($Name, $ErrorAction) }
        function Get-ScheduledTask { param($TaskName, $ErrorAction)
            if ($mock.Task) {
                [pscustomobject]@{ State = if ($mock.Running) { 'Running' } else { 'Ready' } }
            }
        }
        function Export-ScheduledTask { param($TaskName) 'original-task-definition' }
        function Stop-ScheduledTask { param($TaskName) $trace.Add("stop-$($mock.Task)"); $mock.Running = $false }
        function New-ScheduledTaskAction { param($Execute, $WorkingDirectory) 'test-action' }
        function New-ScheduledTaskTrigger { param([switch]$AtStartup) 'test-trigger' }
        function New-ScheduledTaskPrincipal { param($UserId, $LogonType, $RunLevel) 'test-principal' }
        function New-ScheduledTaskSettingsSet {
            param($ExecutionTimeLimit, $RestartCount, $RestartInterval, $MultipleInstances,
                [switch]$AllowStartIfOnBatteries, [switch]$DontStopIfGoingOnBatteries)
            'test-settings'
        }
        function Register-ScheduledTask { param($TaskName, $Action, $Trigger, $Principal, $Settings, $Xml, [switch]$Force)
            if ($Xml -eq 'original-task-definition') { $mock.Task = 'old' }
            else { $mock.Task = 'new' }
            $trace.Add("register-$($mock.Task)")
        }
        function Start-ScheduledTask { param($TaskName)
            $trace.Add("start-$($mock.Task)")
            $mock.Running = $true
            if ($mock.Task -eq 'new' -and $Mode -eq 'success') {
                @{ vmId = $vm; workspaceId = $workspace; connected = $true; timeUtc = [datetime]::UtcNow } |
                    ConvertTo-Json | Set-Content -LiteralPath $statusPath
            }
        }
        function Unregister-ScheduledTask { param($TaskName, [switch]$Confirm)
            $mock.Task = ''; $mock.Running = $false
        }
        function Start-Sleep { param($Seconds) }
        function Copy-Item { param($LiteralPath, $Destination)
            if ($Mode -eq 'staging-fails' -and $LiteralPath -eq $sourceExe) { throw [IO.IOException]::new('test staging error') }
            Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination
        }

        # Run the production installer against real disposable files and mock task operations.
        $fixture = $Source.Replace("'C:\ProgramData\ClaudomeNetwork'", '$root').
            Replace("'C:\ProgramData\ClaudeBootstrap\identity.json'", '$identityPath').
            Replace('$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)', '$admin = $true').
            Replace('exit $exitCode', 'return $exitCode')
        if ($fixture.Contains('C:\ProgramData\') -or !$fixture.Contains('$admin = $true') -or !$fixture.Contains('return $exitCode')) {
            throw 'Fixture isolation could not be applied.'
        }
        $result = & ([scriptblock]::Create($fixture)) -InputPath $inputPath
    } finally {
        if ($held) { $held.Dispose() }
    }
    [pscustomobject]@{
        Result = $result
        Actions = $trace -join ','
        Exe = [IO.File]::ReadAllText($targetExe)
        Config = [IO.File]::ReadAllText($targetConfig)
        Running = $mock.Running
    }
}

$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../ClaudeSetup/Guest/BridgeInstall.ps1') -Raw
$results = [Collections.Generic.List[object]]::new()
$fixtures = Join-Path $OutputDirectory ('bridge-fixtures-' + [guid]::NewGuid().ToString('N'))
foreach ($mode in @('staging-fails','locked-target','handshake-fails','success')) {
    $result = Invoke-Fixture $mode (Join-Path $fixtures $mode) $source
    $passed = switch ($mode) {
        'staging-fails' { $result.Result -eq 21 -and !$result.Actions -and $result.Exe -eq 'original component' -and $result.Running }
        'locked-target' { $result.Result -eq 23 -and $result.Actions -eq 'stop-old,start-old' -and $result.Exe -eq 'original component' -and $result.Config -eq 'original configuration' -and $result.Running }
        'handshake-fails' { $result.Result -eq 26 -and $result.Actions -eq 'stop-old,register-new,start-new,stop-new,register-old,start-old' -and $result.Exe -eq 'original component' -and $result.Config -eq 'original configuration' -and $result.Running }
        'success' { $result.Result -eq 0 -and $result.Actions -eq 'stop-old,register-new,start-new' -and $result.Exe -eq 'new component' -and $result.Running }
    }
    $results.Add([pscustomobject]@{ name = $mode; passed = $passed; result = $result })
}
$failed = @($results | Where-Object { !$_.passed }).Count
@{ passed = $results.Count - $failed; failed = $failed; tests = $results } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'guest-bridge-tests.json')
"Guest bridge passed=$($results.Count - $failed) failed=$failed"
if ($failed) { $results | ConvertTo-Json -Depth 5; exit 1 }
