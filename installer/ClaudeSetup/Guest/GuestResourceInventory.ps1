$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$inputData=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($inventoryInput)) | ConvertFrom-Json
$report=[ordered]@{schema=2;runId=$inputData.runId;vmId='';workspaceId='';status='unavailable';installed=@();cached=@()}
try {
    $model=Get-CimInstance Win32_ComputerSystem
    if($inputData.platform -eq 'virtualbox'){
        $product=Get-CimInstance Win32_ComputerSystemProduct
        if($model.Model -notmatch 'VirtualBox' -or ([guid]$product.UUID) -ne ([guid]$inputData.vmId)){throw 'IDENTITY'}
    }elseif($inputData.platform -in @('hyperv','workstation')){
        $identity=Get-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json' -Raw | ConvertFrom-Json
        if($identity.vmId -ne $inputData.vmId -or $identity.workspaceId -ne $inputData.workspaceId){throw 'IDENTITY'}
        if($inputData.platform -eq 'hyperv' -and ($model.Manufacturer -ne 'Microsoft Corporation' -or $model.Model -ne 'Virtual Machine')){throw 'IDENTITY'}
        if($inputData.platform -eq 'workstation' -and ($model.Manufacturer -notmatch 'VMware' -or $model.Model -notmatch 'VMware')){throw 'IDENTITY'}
    }else{throw 'IDENTITY'}
    $report.vmId=$inputData.vmId;$report.workspaceId=$inputData.workspaceId
    $folder=Get-Item -LiteralPath $inputData.guestRoot
    if($folder.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'INVALID_DIRECTORY'}
    $entries=@(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue)
    $locations=@{
        clash=@((Join-Path $env:ProgramFiles 'Clash Verge\clash-verge.exe'),(Join-Path $env:ProgramFiles 'Clash Verge\Clash Verge.exe'),(Join-Path $env:LOCALAPPDATA 'Programs\Clash Verge\clash-verge.exe'))
        adspower=@((Join-Path $env:LOCALAPPDATA 'Programs\AdsPower Global\AdsPower Global.exe'),(Join-Path $env:ProgramFiles 'AdsPower Global\AdsPower Global.exe'))
    }
    $names=@{clash='Clash Verge';adspower='AdsPower'}
    foreach($resource in $inputData.resources) {
        if([IO.Path]::GetFileName([string]$resource.fileName) -ne $resource.fileName){throw 'INVALID_NAME'}
        if($names.ContainsKey($resource.id) -and @($entries | Where-Object {$_.DisplayName -match $names[$resource.id]}).Count -and
            @($locations[$resource.id] | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf}).Count){
            $report.installed+=@($resource.id)
            continue
        }
        $path=Join-Path $folder.FullName $resource.fileName
        $file=Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
        if($file -and ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint))){throw 'INVALID_CACHE_ENTRY'}
        if($file -and $file.Length -eq $resource.size -and
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ieq $resource.sha256){
            $report.cached+=@($resource.id)
        }
    }
    $report.status='ready'
}catch{}finally{
    $line='CLAUDOME_INSTALLER_INVENTORY:'+($report | ConvertTo-Json -Depth 4 -Compress)
    if($inputData.guestReply){[IO.File]::WriteAllText($inputData.guestReply,$line)}else{Write-Output $line}
}
