#requires -Version 5.1
Set-StrictMode -Version 2
$script:V11Fields=@('EXECUTION_PLACE','TARGET_VM_NAME','VM_USERNAME','VM_PASSWORD','PROXY_SUBSCRIPTION_URL','EXPECTED_EXIT_IPV4','PROXY_NODE_NAME','ADSPOWER_USERNAME','ADSPOWER_PASSWORD','CLAUDE_USERNAME','CLAUDE_PASSWORD','VPS_HOST','VPS_PORT','VPS_USERNAME','VPS_PASSWORD')
$script:V11Labels=@{
    '执行位置'='EXECUTION_PLACE';'虚拟机名称'='TARGET_VM_NAME';'虚拟机账号'='VM_USERNAME';'虚拟机密码'='VM_PASSWORD'
    '代理订阅链接'='PROXY_SUBSCRIPTION_URL';'预期出口IPv4'='EXPECTED_EXIT_IPV4';'节点名称'='PROXY_NODE_NAME'
    'AdsPower账号'='ADSPOWER_USERNAME';'AdsPower密码'='ADSPOWER_PASSWORD';'Claude账号'='CLAUDE_USERNAME';'Claude密码'='CLAUDE_PASSWORD'
    'VPS服务器地址'='VPS_HOST';'VPS端口'='VPS_PORT';'VPS账号'='VPS_USERNAME';'VPS密码'='VPS_PASSWORD'
}
function Get-V11Template {return [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'input-template.txt'),[Text.Encoding]::UTF8)}
function Read-V11Input([string]$Text) {
    $data=@{}
    foreach($line in ($Text -split '\r?\n')) {
        if([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')){continue}
        if($line -match '^\s*【(?<label>[^【】：:
]+)[：:]】(?<value>.*)$'){
            $label=$Matches['label'].Trim()
            if(-not $script:V11Labels.ContainsKey($label)){throw 'INPUT_FIELD_UNKNOWN'}
            $key=$script:V11Labels[$label]
            $value=$Matches['value']
        }else{
            # Preserve compatibility with already-filled V11 English key=value files.
            $i=$line.IndexOf('='); if($i -le 0){throw 'INPUT_FORMAT_INVALID'}
            $key=$line.Substring(0,$i).Trim()
            if($script:V11Fields -notcontains $key){throw 'INPUT_FIELD_UNKNOWN'}
            $value=$line.Substring($i+1)
        }
        if($data.ContainsKey($key)){throw 'INPUT_FIELD_DUPLICATE'}
        $data[$key]=$value
    }
    foreach($key in $script:V11Fields){if(-not $data.ContainsKey($key)){$data[$key]=''}}
    if($data.EXECUTION_PLACE.Trim() -eq 'Windows主机'){$data.EXECUTION_PLACE='HyperVHost'}
    elseif($data.EXECUTION_PLACE.Trim() -eq 'Windows虚拟机'){$data.EXECUTION_PLACE='GuestLocal'}
    if($data.EXECUTION_PLACE -notin @('HyperVHost','GuestLocal')){throw 'EXECUTION_PLACE_REQUIRED'}
    if([string]::IsNullOrWhiteSpace($data.TARGET_VM_NAME)){throw 'TARGET_VM_NAME_REQUIRED'}
    if($data.EXPECTED_EXIT_IPV4){
        $ip=$null
        if(-not [Net.IPAddress]::TryParse($data.EXPECTED_EXIT_IPV4,[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw 'EXPECTED_IPV4_INVALID'}
    }
    return $data
}
function Get-V11Identity {return [Security.Principal.WindowsIdentity]::GetCurrent().User.Value}
function Get-V11Private([string]$Root,[switch]$Create) {
    if($env:OS -ne 'Windows_NT'){throw 'WINDOWS_REQUIRED'}
    $rootPath=[IO.Path]::GetFullPath($Root)
    if(-not (Test-Path -LiteralPath $rootPath -PathType Container)){throw 'WORKSPACE_NOT_FOUND'}
    $path=Join-Path $rootPath 'runtime-private'
    if($Create -and -not (Test-Path -LiteralPath $path)) {
        New-Item -ItemType Directory -Path $path -ErrorAction Stop | Out-Null
        $acl=New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true,$false)
        foreach($sidText in @((Get-V11Identity),'S-1-5-18')) {
            $sid=New-Object Security.Principal.SecurityIdentifier($sidText)
            $rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $path -AclObject $acl -ErrorAction Stop
    }
    if(Test-Path -LiteralPath $path){if((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'PRIVATE_DIRECTORY_REPARSE_POINT'}}
    return $path
}
function Write-V11Json([string]$Path,$Value) {
    $json=ConvertTo-Json -InputObject $Value -Depth 12
    $temp=$Path+'.new'
    [IO.File]::WriteAllText($temp,$json,(New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temp -Destination $Path -Force
}
function Get-V11State([string]$Root) {
    $path=Join-Path (Get-V11Private $Root) 'state.json'
    if(-not (Test-Path -LiteralPath $path)){return $null}
    try{return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)}catch{throw 'STATE_INVALID'}
}
function Assert-V11Open([string]$Root) {
    $state=Get-V11State $Root
    if($null -eq $state -or $state.mode -ne 'Deploying'){throw 'DEPLOYMENT_LOCKED'}
    if($state.userSid -ne (Get-V11Identity) -or $state.computer -ne $env:COMPUTERNAME){throw 'SESSION_MACHINE_OR_USER_MISMATCH'}
    return $state
}
function Protect-V11Object($Data) {
    $plain=ConvertTo-Json -InputObject $Data -Depth 8 -Compress
    $secure=ConvertTo-SecureString -String $plain -AsPlainText -Force
    try{return (ConvertFrom-SecureString -SecureString $secure)}finally{$secure.Dispose();$plain=$null}
}
function Unprotect-V11Object([string]$Cipher) {
    $secure=ConvertTo-SecureString -String $Cipher -ErrorAction Stop
    $ptr=[IntPtr]::Zero
    try{
        $ptr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        $plain=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
        $obj=$plain | ConvertFrom-Json
        $result=@{};foreach($p in $obj.PSObject.Properties){$result[$p.Name]=[string]$p.Value}
        return $result
    }finally{if($ptr -ne [IntPtr]::Zero){[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)};$secure.Dispose();$plain=$null}
}
function Import-V11Data([string]$Root,[string]$InputPath) {
    $data=Read-V11Input ([IO.File]::ReadAllText([IO.Path]::GetFullPath($InputPath),[Text.Encoding]::UTF8))
    $private=Get-V11Private $Root -Create
    $cipher=Protect-V11Object $data
    $test=Unprotect-V11Object $cipher
    foreach($k in $script:V11Fields){if($test[$k] -cne $data[$k]){throw 'ENCRYPTION_ROUNDTRIP_FAILED'}}
    $store=Join-Path $private 'credentials.dpapi'
    [IO.File]::WriteAllText($store,$cipher,(New-Object Text.UTF8Encoding($false)))
    $readback=Unprotect-V11Object ([IO.File]::ReadAllText($store))
    foreach($k in $script:V11Fields){if($readback[$k] -cne $data[$k]){throw 'CREDENTIAL_READBACK_FAILED'}}
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($InputPath),("# 已导入本次部署加密资料；锁定时清除加密副本。`r`n"+(Get-V11Template)),(New-Object Text.UTF8Encoding($true)))
    Write-V11Json (Join-Path $private 'credential-metadata.json') @{
        importedUtc=[DateTime]::UtcNow.ToString('o');filledFields=@($script:V11Fields | Where-Object {$data[$_] -ne ''});sourceCleared=$true
    }
    return [pscustomobject]@{imported=$true;sourceCleared=$true;filledFieldCount=@($script:V11Fields | Where-Object {$data[$_] -ne ''}).Count}
}
function Read-V11PrivateData([string]$Root) {
    $path=Join-Path (Get-V11Private $Root) 'credentials.dpapi'
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw 'LOCAL_CREDENTIALS_MISSING'}
    try{return (Unprotect-V11Object ([IO.File]::ReadAllText($path)))}catch{throw 'LOCAL_CREDENTIALS_UNAVAILABLE'}
}
function Start-V11Session([string]$Root,[string]$Confirmation) {
    if($Confirmation -cne 'START'){throw 'EXPLICIT_START_REQUIRED'}
    $private=Get-V11Private $Root -Create
    $old=Get-V11State $Root
    if($null -ne $old -and $old.mode -eq 'Deploying'){throw 'SESSION_ALREADY_OPEN'}
    $data=Read-V11PrivateData $Root
    if($data.EXECUTION_PLACE -eq 'HyperVHost' -and (-not $data.VM_USERNAME -or -not $data.VM_PASSWORD)){throw 'VM_CREDENTIALS_REQUIRED_FOR_POWERSHELL_DIRECT'}
    $state=[ordered]@{
        version=11;taskId=[guid]::NewGuid().ToString();mode='Deploying';computer=$env:COMPUTERNAME;userSid=(Get-V11Identity)
        executionPlace=$data.EXECUTION_PLACE;openedUtc=[DateTime]::UtcNow.ToString('o');closedUtc=$null
    }
    Write-V11Json (Join-Path $private 'state.json') $state
    return [pscustomobject]@{mode='Deploying';taskId=$state.taskId;executionPlace=$state.executionPlace}
}
function Stop-V11Session([string]$Root) {
    $private=Get-V11Private $Root -Create
    # Closing is safe even when a Mac instruction state was copied, or a state file is corrupt.
    $previous=$null;try{$previous=Get-V11State $Root}catch{}
    $id='';$place='';$opened=$null
    if($null -ne $previous){
        if($null -ne $previous.PSObject.Properties['taskId']){$id=[string]$previous.taskId}
        if($null -ne $previous.PSObject.Properties['executionPlace']){$place=[string]$previous.executionPlace}
        if($null -ne $previous.PSObject.Properties['openedUtc']){$opened=$previous.openedUtc}
    }
    $state=[ordered]@{version=11;taskId=$id;mode='Locked';computer=$env:COMPUTERNAME;userSid=(Get-V11Identity);executionPlace=$place;openedUtc=$opened;closedUtc=[DateTime]::UtcNow.ToString('o')}
    Write-V11Json (Join-Path $private 'state.json') $state
    foreach($name in @('credentials.dpapi','credential-metadata.json')) {
        $path=Join-Path $private $name
        if(Test-Path -LiteralPath $path -PathType Leaf){Remove-Item -LiteralPath $path -Force -ErrorAction Stop}
    }
    return [pscustomobject]@{mode='Locked';credentialCopyRemoved=$true;scope='package-runner-and-project-instructions';runningScriptsAutomaticallyKilled=$false}
}
function Get-V11Status([string]$Root) {
    $state=Get-V11State $Root
    $private=Get-V11Private $Root
    $mode='Locked';$place='';if($null -ne $state){$mode=$state.mode;$place=$state.executionPlace}
    return [pscustomobject]@{version=11;mode=$mode;executionPlace=$place;credentialsPresent=(Test-Path -LiteralPath (Join-Path $private 'credentials.dpapi'));scope='package-runner-and-project-instructions'}
}
function Resolve-V11Script([string]$Root,[string]$ScriptPath) {
    $work=[IO.Path]::GetFullPath((Join-Path $Root 'work'))
    $path=[IO.Path]::GetFullPath($ScriptPath)
    if(-not $path.StartsWith($work+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($path) -ine '.ps1'){throw 'SCRIPT_OUTSIDE_TASK_WORKSPACE'}
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw 'SCRIPT_NOT_FOUND'}
    $walk=$path
    while($walk.Length -ge $work.Length){
        if((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'SCRIPT_REPARSE_POINT'}
        if($walk -eq $work){break};$walk=[IO.Path]::GetDirectoryName($walk)
    }
    return $path
}
function Hide-V11Data([string]$Text,$Data) {
    $safe=$Text
    foreach($key in $script:V11Fields){$value=[string]$Data[$key];if($value){$safe=$safe.Replace($value,'[已隐藏]')}}
    return $safe
}
function Invoke-V11Task {
    [CmdletBinding()]
    param([string]$Root,[string]$ScriptPath,[string[]]$Fields=@(),[ValidateRange(1,1800)][int]$TimeoutSeconds=300)
    $state=Assert-V11Open $Root
    $file=Resolve-V11Script $Root $ScriptPath
    $all=Read-V11PrivateData $Root
    $data=@{}
    foreach($name in $Fields){if($script:V11Fields -notcontains $name){throw 'REQUESTED_FIELD_UNKNOWN'};$data[$name]=$all[$name]}
    $job=$null;$secure=$null
    $source=[IO.File]::ReadAllText($file,[Text.Encoding]::UTF8)
    $invokeBlock={param($source,$data);$ErrorActionPreference='Stop';& ([scriptblock]::Create($source)) -DeploymentData $data *>&1 | ForEach-Object {[string]$_}}
    try{
        if($state.executionPlace -eq 'HyperVHost'){
            $secure=ConvertTo-SecureString -String $all.VM_PASSWORD -AsPlainText -Force
            $credential=New-Object Management.Automation.PSCredential($all.VM_USERNAME,$secure)
            $job=Invoke-Command -VMName $all.TARGET_VM_NAME -Credential $credential -ScriptBlock $invokeBlock -ArgumentList @($source,$data) -AsJob -ErrorAction Stop
        }elseif($state.executionPlace -eq 'GuestLocal'){
            $job=Start-Job -ScriptBlock $invokeBlock -ArgumentList $source,$data
        }else{throw 'EXECUTION_PLACE_INVALID'}
        $done=Wait-Job -Job $job -Timeout $TimeoutSeconds
        if($null -eq $done){throw 'TASK_TIMEOUT'}
        if($job.State -ne 'Completed'){throw 'TASK_FAILED'}
        $results=@(Receive-Job -Job $job -ErrorAction Stop *>&1 | Out-String -Stream | Select-Object -First 128)
        foreach($line in $results){Hide-V11Data ([string]$line) $all}
        return [pscustomobject]@{taskCompleted=$true;executionPlace=$state.executionPlace}
    }catch{
        $code='TASK_FAILED';if($_.Exception.Message -eq 'TASK_TIMEOUT'){$code='TASK_TIMEOUT'}
        throw $code
    }finally{
        if($null -ne $job){if($job.State -notin @('Completed','Failed','Stopped')){Stop-Job -Job $job -ErrorAction SilentlyContinue};Remove-Job -Job $job -Force -ErrorAction SilentlyContinue}
        if($null -ne $secure){$secure.Dispose()};$all=$null;$data=$null
    }
}
Export-ModuleMember -Function Get-V11Template,Read-V11Input,Import-V11Data,Start-V11Session,Stop-V11Session,Get-V11Status,Assert-V11Open,Invoke-V11Task,Resolve-V11Script,Hide-V11Data
