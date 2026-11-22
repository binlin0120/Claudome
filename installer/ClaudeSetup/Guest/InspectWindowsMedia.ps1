#requires -Version 7.0
param([Parameter(Mandatory)][string]$InputPath)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
# Host-side read-only ISO inspection. No image application, disk partitioning,
# installation, registry changes, optional features or VM operations are used.
$inputData=$null;$mounted=$null;$createdMount=$false
try{
    if((Get-Item -LiteralPath $InputPath).Length -gt 16384){throw 'MEDIA_INPUT'}
    $inputData=Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
    $private=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($InputPath))
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath([string]$inputData.outputPath)) -ne $private -or $inputData.runId -notmatch '^[a-f0-9]{32}$' -or $inputData.sha256 -notmatch '^[a-f0-9]{64}$'){throw 'MEDIA_INPUT'}
    $iso=[IO.Path]::GetFullPath([string]$inputData.isoPath)
    if($iso.StartsWith('\\') -or [IO.Path]::GetExtension($iso) -ne '.iso'){throw 'MEDIA_INPUT'}
    foreach($path in @($iso,[string]$inputData.outputPath)){
        $walk=$path;while($walk){if((Test-Path -LiteralPath $walk) -and ((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'MEDIA_INPUT'};$walk=[IO.Path]::GetDirectoryName($walk)}
    }
    if((Get-FileHash -LiteralPath $iso -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputData.sha256){throw 'MEDIA_CHANGED'}
    $mounted=Get-DiskImage -ImagePath $iso
    if(-not $mounted.Attached){$mounted=Mount-DiskImage -ImagePath $iso -Access ReadOnly -PassThru;$createdMount=$true}
    $volumes=@($mounted | Get-Volume | Where-Object DriveLetter)
    if($volumes.Count -ne 1){throw 'MEDIA_FORMAT'}
    $base=([string]$volumes[0].DriveLetter)+':\'
    if(-not(Test-Path -LiteralPath (Join-Path $base 'efi\boot\bootx64.efi'))){throw 'MEDIA_ARCHITECTURE'}
    $sources=@('sources\install.wim','sources\install.esd') | ForEach-Object {Join-Path $base $_} | Where-Object {Test-Path -LiteralPath $_}
    if(@($sources).Count -ne 1){throw 'MEDIA_FORMAT'}
    $source=@($sources)[0];$indexes=@(Get-WindowsImage -ImagePath $source)
    if($indexes.Count -lt 1 -or $indexes.Count -gt 20){throw 'MEDIA_FORMAT'}
    $images=@(foreach($entry in $indexes){
        $image=Get-WindowsImage -ImagePath $source -Index $entry.ImageIndex
        if([string]$image.ImageName -match '\bWindows\s+11\b' -and [string]$image.Architecture -in @('x64','9') -and [string]$image.InstallationType -eq 'Client' -and ([version]$image.Version).Major -eq 10 -and ([version]$image.Version).Build -ge 22000){
            @{index=[int]$image.ImageIndex;name=[string]$image.ImageName;architecture='x64';version=[string]$image.Version;installationType=[string]$image.InstallationType;edition=[string]$image.EditionId}
        }
    })
    if($images.Count -eq 0){throw 'MEDIA_UNSUPPORTED'}
    $response=@{ok=$true;runId=[string]$inputData.runId;identity=@{isoPath=$iso;sha256=[string]$inputData.sha256;bytes=(Get-Item -LiteralPath $iso).Length;images=$images}}
}catch{$response=@{ok=$false;runId=[string]$inputData.runId;code='MEDIA_NOT_VERIFIED'}}
finally{
    if($createdMount -and $mounted){try{Dismount-DiskImage -ImagePath $mounted.ImagePath | Out-Null}catch{$response=@{ok=$false;runId=[string]$inputData.runId;code='MEDIA_UNMOUNT_FAILED'}}}
}
if($inputData -and [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath([string]$inputData.outputPath)) -eq [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($InputPath))){
    $response | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $inputData.outputPath -Encoding utf8NoBOM
}
if(-not $response.ok){exit 1}
