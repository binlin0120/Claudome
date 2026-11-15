#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Prepare','Import','Begin','Status','Run','Lock')][string]$Action,
    [string]$InputPath,
    [string]$ScriptPath,
    [string[]]$Fields=@(),
    [ValidateRange(1,1800)][int]$TimeoutSeconds=300
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $PSScriptRoot 'Deployment.psm1') -Force -DisableNameChecking
try{
    if(-not $InputPath){$InputPath=Join-Path ([Environment]::GetFolderPath('Desktop')) '虚拟电脑部署资料.txt'}
    switch($Action){
        'Prepare'{
            if(-not (Test-Path -LiteralPath $InputPath)){
                [IO.File]::WriteAllText([IO.Path]::GetFullPath($InputPath),(Get-V11Template),(New-Object Text.UTF8Encoding($true)))
            }
            Write-Host '资料文件已准备。请本人填写；不要把文件内容发到聊天或网盘。'
            Write-Host $InputPath
            Write-Host 'Hyper-V新虚拟机尚无账号时，先填已有项目；账号创建后只补一次，再开始来宾代码配置。'
            if($Host.Name -eq 'ConsoleHost'){Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\notepad.exe') -ArgumentList @('"'+$InputPath+'"')}
        }
        'Import'{Import-V11Data -Root $root -InputPath $InputPath | ConvertTo-Json -Compress}
        'Begin'{
            $status=Get-V11Status $root
            if($status.mode -eq 'Deploying'){$status | ConvertTo-Json -Compress;break}
            Write-Host '仅本次任务：允许在指定虚拟机配置、检查与回退。不会修改Codex平台权限，也不会跳过工具拒绝。'
            $confirmation=Read-Host '本人确认请输入 START；其他输入取消'
            if($confirmation -cne 'START'){throw 'EXPLICIT_START_REQUIRED'}
            if(-not $status.credentialsPresent){Import-V11Data -Root $root -InputPath $InputPath | ConvertTo-Json -Compress}
            Start-V11Session -Root $root -Confirmation $confirmation | ConvertTo-Json -Compress
        }
        'Status'{Get-V11Status $root | ConvertTo-Json -Compress}
        'Run'{
            if(-not $ScriptPath){throw 'SCRIPT_PATH_REQUIRED'}
            Invoke-V11Task -Root $root -ScriptPath $ScriptPath -Fields $Fields -TimeoutSeconds $TimeoutSeconds
        }
        'Lock'{
            Stop-V11Session $root | ConvertTo-Json -Compress
            Write-Host '本包执行入口已锁定，临时加密资料已清除。此结果不代表网络验收通过。'
            Write-Host '锁定不终止此前已启动的脚本；收尾前要确认本次脚本全部结束。'
            Write-Host '其他聊天、管理员或其他工具不受此脚本强制控制；请退出本次配置会话。'
        }
    }
}catch{
    $known=@('INPUT_FORMAT_INVALID','INPUT_FIELD_UNKNOWN','INPUT_FIELD_DUPLICATE','EXECUTION_PLACE_REQUIRED','TARGET_VM_NAME_REQUIRED','EXPECTED_IPV4_INVALID','WINDOWS_REQUIRED','WORKSPACE_NOT_FOUND','PRIVATE_DIRECTORY_REPARSE_POINT','STATE_INVALID','DEPLOYMENT_LOCKED','SESSION_MACHINE_OR_USER_MISMATCH','ENCRYPTION_ROUNDTRIP_FAILED','CREDENTIAL_READBACK_FAILED','LOCAL_CREDENTIALS_MISSING','LOCAL_CREDENTIALS_UNAVAILABLE','EXPLICIT_START_REQUIRED','SESSION_ALREADY_OPEN','VM_CREDENTIALS_REQUIRED_FOR_POWERSHELL_DIRECT','SCRIPT_OUTSIDE_TASK_WORKSPACE','SCRIPT_NOT_FOUND','SCRIPT_REPARSE_POINT','REQUESTED_FIELD_UNKNOWN','TASK_FAILED','TASK_TIMEOUT','SCRIPT_PATH_REQUIRED','GUEST_LOCAL_REQUIRES_VIRTUAL_MACHINE')
    $code=$_.Exception.Message
    if($known -notcontains $code){$code='LOCAL_OPERATION_FAILED'}
    [Console]::Error.WriteLine('V11: '+$code+'。未输出密码或原始命令。请查阅 docs/第11版使用方法.md 的错误说明。')
    exit 1
}
