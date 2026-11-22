#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidatePattern('^[\p{L}\p{N}](?:[\p{L}\p{N}_.-]| [\p{L}\p{N}_.-]){0,19}$')][string]$UserName,
    [ValidatePattern('\AC:\\Users\\[\p{L}\p{N}_][\p{L}\p{M}\p{N}_. -]{0,62}\\AppData\\Local\z')][string]$LocalAppData,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$RunId,
    [ValidateSet('send','receive')][string]$Direction,
    [switch]$Interactive,
    [switch]$Cleanup
)
$ErrorActionPreference = 'Stop'
$profileDirectory = if ($LocalAppData) { $LocalAppData } else { 'C:\Users\' + $UserName + '\AppData\Local' }
$baseDirectory = $profileDirectory + '\ClaudeEnvironmentSetup'
$folder = $baseDirectory + '\clipboard-' + $RunId
$exe = $baseDirectory + '\pwsh\pwsh.exe'
if ($PSScriptRoot -ne $folder -or $PSCommandPath -ne (Join-Path $folder 'ClipboardTransfer.ps1')) { throw 'CLIPBOARD_SCOPE' }
for ($item = [IO.DirectoryInfo]::new($folder); $null -ne $item; $item = $item.Parent) {
    if ($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'CLIPBOARD_SCOPE' }
}

$request = Join-Path $folder 'request.json'
$result = Join-Path $folder 'result.json'
$account = [Security.Principal.NTAccount]::new($env:COMPUTERNAME, $UserName)
$taskName = 'ClaudomeClipboard-' + $RunId
function Write-ClipboardResult([string]$status, [string]$text = '') {
    @{ runId = $RunId; status = $status; text = $text } | ConvertTo-Json -Compress |
        Set-Content -LiteralPath ($result + '.new') -Encoding utf8NoBOM
    [IO.File]::Move($result + '.new', $result, $true)
}
function Clear-StaleClipboardState([string]$BaseDir, [string]$ActiveRunId = '', [int]$MaxAgeSeconds = 120, [DateTime]$NowUtc = [DateTime]::UtcNow) {
    if (-not [IO.Directory]::Exists($BaseDir)) { return }
    $baseInfo = [IO.DirectoryInfo]::new($BaseDir)
    if (($baseInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
    $cutoff = $NowUtc.AddSeconds(-$MaxAgeSeconds)
    foreach ($dir in $baseInfo.EnumerateDirectories('clipboard-*')) {
        if ($dir.Name -notmatch '^clipboard-([a-f0-9]{32})$') { continue }
        $id = $Matches[1]
        if ($id -eq $ActiveRunId -or ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        $latest = $dir.LastWriteTimeUtc
        $unsafeEntry = $false
        $files = @()
        try {
            foreach ($entry in $dir.EnumerateFileSystemInfos()) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $entry -is [IO.DirectoryInfo] -or
                    $entry.Name -notin @('request.json', 'result.json', 'result.json.new', 'ClipboardTransfer.ps1')) {
                    $unsafeEntry = $true
                    break
                }
                if ($entry.LastWriteTimeUtc -gt $latest) { $latest = $entry.LastWriteTimeUtc }
                $files += $entry.FullName
            }
        } catch { continue }
        if ($unsafeEntry -or $latest -gt $cutoff) { continue }
        $staleTask = 'ClaudomeClipboard-' + $id
        Stop-ScheduledTask -TaskName $staleTask -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $staleTask -Confirm:$false -ErrorAction SilentlyContinue
        try {
            foreach ($file in $files) {
                if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
            }
            [IO.Directory]::Delete($dir.FullName, $false)
        } catch { }
    }
    foreach ($task in @(Get-ScheduledTask -TaskName 'ClaudomeClipboard-*' -ErrorAction SilentlyContinue)) {
        if ($task.TaskName -notmatch '^ClaudomeClipboard-([a-f0-9]{32})$') { continue }
        $id = $Matches[1]
        if ($id -eq $ActiveRunId -or $task.State -eq 'Running') { continue }
        $taskFolder = Join-Path $BaseDir ('clipboard-' + $id)
        if (-not [IO.Directory]::Exists($taskFolder) -or ([IO.DirectoryInfo]::new($taskFolder)).LastWriteTimeUtc -le $cutoff) {
            Unregister-ScheduledTask -TaskName $task.TaskName -Confirm:$false -ErrorAction SilentlyContinue
        }
    }
}

if ($Cleanup) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    foreach ($path in @($request, $result, ($result + '.new'), $PSCommandPath)) {
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
    [IO.Directory]::Delete($folder, $false)
    Clear-StaleClipboardState $baseDirectory $RunId
    return
}

if ($Interactive) {
    $status = 'unavailable'
    $text = ''
    try {
        $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
        $actual = [Security.Principal.WindowsIdentity]::GetCurrent().User
        if ($session -eq 0 -or $actual -ne $account.Translate([Security.Principal.SecurityIdentifier])) {
            $status = 'no-desktop'
            return
        }
        for ($attempt = 0; $attempt -lt 5; ++$attempt) {
            try {
                if ($Direction -eq 'send') {
                    if ((Get-Item -LiteralPath $request).Length -gt 128kb) { $status = 'too-long'; break }
                    $inputData = Get-Content -LiteralPath $request -Raw | ConvertFrom-Json
                    $text = $inputData.text
                    if ($text -isnot [string] -or !$text -or $text.Contains([char]0)) { $status = 'no-text'; break }
                    if ($text.Length -gt 16384) { $status = 'too-long'; break }
                    Set-Clipboard -Value $text -Verbose:$false -WhatIf:$false -Confirm:$false
                    if (![string]::Equals((Get-Clipboard -Raw), $text, [StringComparison]::Ordinal)) {
                        $status = 'unavailable'
                        break
                    }
                } else {
                    $text = Get-Clipboard -Raw
                    if (!$text -or $text.Contains([char]0)) { $status = 'no-text'; break }
                    if ($text.Length -gt 16384) { $status = 'too-long'; break }
                }
                $status = 'ok'
                break
            } catch {
                if ($_.Exception.GetBaseException() -isnot [Runtime.InteropServices.ExternalException]) { throw }
                $status = 'busy'
                Start-Sleep -Milliseconds 100
            }
        }
    } finally {
        if ([IO.File]::Exists($request)) { [IO.File]::Delete($request) }
        if ($status -eq 'ok' -and $Direction -eq 'receive') { Write-ClipboardResult $status $text }
        else { Write-ClipboardResult $status }
    }
    return
}

Clear-StaleClipboardState $baseDirectory $RunId
$registered = $false
try {
    $taskArguments = '-NoProfile -STA -WindowStyle Hidden -File "' + $PSCommandPath + '" -UserName "' + $UserName +
            '" -RunId ' + $RunId + ' -Direction ' + $Direction + ' -Interactive'
    if ($LocalAppData) { $taskArguments += ' -LocalAppData "' + $LocalAppData + '"' }
    $action = New-ScheduledTaskAction -Execute $exe -Argument $taskArguments -WorkingDirectory $folder
    $principal = New-ScheduledTaskPrincipal -UserId $account.Value -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromSeconds(18)) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings | Out-Null
    $registered = $true
    try { Start-ScheduledTask -TaskName $taskName }
    catch { Write-ClipboardResult 'no-desktop'; return }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 15) {
        if ([IO.File]::Exists($result)) { return }
        Start-Sleep -Milliseconds 250
    }
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if (![IO.File]::Exists($result)) { Write-ClipboardResult 'no-desktop' }
} finally {
    if ($registered) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue }
    if ([IO.File]::Exists($request)) { [IO.File]::Delete($request) }
}
