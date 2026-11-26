param([Parameter(Mandatory)][ValidateSet('ReadEnabled','Enable','Disable')][string]$Mode)
$ErrorActionPreference = 'Stop'

$policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
$expected = @{
    fDisableClip = [int]($Mode -eq 'Disable')
    fDisableCdm = 1
    fDisableLPT = 1
    fDisableCam = 1
    fDisablePNPRedir = 1
    fDisableAudio = 1
    fDisableAudioCapture = 1
    fDisableCcm = 1
}

if ($Mode -ne 'ReadEnabled') {
    if (-not (Test-Path -LiteralPath $policy)) { New-Item -Path $policy -Force | Out-Null }
    foreach ($entry in $expected.GetEnumerator()) {
        New-ItemProperty -LiteralPath $policy -Name $entry.Key -PropertyType DWord -Value $entry.Value -Force | Out-Null
    }
}

if ($Mode -eq 'Disable') {
    $processes = @(Get-Process -Name rdpclip -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        Stop-Process -InputObject $process -Force
    }
    if (Get-Process -Name rdpclip -ErrorAction SilentlyContinue) { return 'CLIP_POLICY_MISMATCH' }
}

if (-not (Test-Path -LiteralPath $policy)) { return 'CLIP_POLICY_MISMATCH' }
$values = Get-ItemProperty -LiteralPath $policy
foreach ($entry in $expected.GetEnumerator()) {
    $value = $values.PSObject.Properties[$entry.Key]
    if ($null -eq $value -or $value.Value -isnot [int] -or $value.Value -ne $entry.Value) {
        return 'CLIP_POLICY_MISMATCH'
    }
}
'CLIP_POLICY_OK'
