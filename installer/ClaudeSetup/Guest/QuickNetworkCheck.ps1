$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$inputRecord=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($input64)) | ConvertFrom-Json
$report=[ordered]@{
    schema=2;runId=$inputRecord.runId;vmId='';workspaceId='';bootUtc=''
    runtimeCode='';exitCode='';clashRuntimePassed=$false;networkPassed=$false;browserOpened=$false
}
try {
    $model=Get-CimInstance Win32_ComputerSystem
    $product=Get-CimInstance Win32_ComputerSystemProduct
    if($model.Model -notmatch 'VirtualBox' -or ([guid]$product.UUID) -ne ([guid]$inputRecord.vmId)){throw 'IDENTITY_MISMATCH'}
    $report.vmId=$inputRecord.vmId;$report.workspaceId=$inputRecord.workspaceId
    $report.bootUtc=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('O')
    $profile=Join-Path $env:APPDATA 'io.github.clash-verge-rev.clash-verge-rev\profiles\claude-environment-setup.yaml'
    $profiles=@(Get-NetFirewallProfile -PolicyStore ActiveStore)
    $profileMatches=(Test-Path -LiteralPath $profile -PathType Leaf) -and
        (Get-FileHash -LiteralPath $profile -Algorithm SHA256).Hash -ieq $inputRecord.configHash
    if(-not $profileMatches -or (Get-Service clash_verge_service).Status -ne 'Running' -or
        @(Get-NetAdapter -Name ClaudeTun -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up').Count -ne 1 -or
        $profiles.Count -ne 3 -or @($profiles | Where-Object {-not $_.Enabled -or $_.DefaultOutboundAction -ne 'Block'}).Count){
        $report.runtimeCode='MISMATCH'
        throw 'RUNTIME_MISMATCH'
    }
    $report.clashRuntimePassed=$true
    foreach($attempt in 1..2) {
        try {
            $response=Invoke-WebRequest -Uri 'https://claude.ai/cdn-cgi/trace' -NoProxy -MaximumRedirection 0 -ConnectionTimeoutSeconds 8 -OperationTimeoutSeconds 10 -Headers @{'Cache-Control'='no-cache'}
            $fields=@{}
            foreach($line in ($response.Content -split "`n")){
                if($line -match '^(ip|loc)=(.*)$'){$fields[$Matches[1]]=$Matches[2].Trim()}
            }
            $observed=$null
            if($response.StatusCode -ne 200 -or -not [Net.IPAddress]::TryParse([string]$fields.ip,[ref]$observed) -or
                $fields.loc -cnotmatch '^[A-Z]{2}$'){throw 'TRACE_INVALID'}
            if($fields.loc -ne $inputRecord.country){$report.exitCode='COUNTRY_MISMATCH'}
            elseif($inputRecord.expectedExitIp -and $fields.ip -ne $inputRecord.expectedExitIp){$report.exitCode='IP_MISMATCH'}
            else{$report.networkPassed=$true}
            break
        }catch{if($attempt -eq 1){Start-Sleep -Milliseconds 500}}
    }
}catch{}finally{
    Write-Output ('CLAUDOME_NET_CHECK:'+($report | ConvertTo-Json -Compress))
}
