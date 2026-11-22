#requires -Version 7.0
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Manufacturer -ne 'Microsoft Corporation' -or (Get-CimInstance Win32_ComputerSystem).Model -ne 'Virtual Machine'){exit 1}
if(-not(Test-Path -LiteralPath 'C:\ProgramData\ClaudeBootstrap\identity.json')){exit 1}
$guard = 'ClaudomeBootstrap-WinRM-Guard'
$listenerUri = 'winrm/config/listener'
$loopback = @('IP:127.0.0.1', 'IP:::1')
$created = [Collections.Generic.List[string]]::new()
try {
    # Keep WinRM blocked while Enable-PSRemoting provisions the named endpoint.
    $rule = Get-NetFirewallRule -Name $guard -ErrorAction SilentlyContinue
    if ($rule) { $rule | Enable-NetFirewallRule }
    else {
        New-NetFirewallRule -Name $guard -DisplayName $guard -Direction Inbound -Action Block -Protocol TCP -LocalPort 5985,5986 | Out-Null
    }
    $parameters='HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters'
    foreach($field in @('HostName','PhysicalHostName','PhysicalHostNameFullyQualified','VirtualMachineName')){Remove-ItemProperty -LiteralPath $parameters -Name $field -ErrorAction SilentlyContinue}
    Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name DefaultPassword -ErrorAction SilentlyContinue
    @('C:\Windows\Panther\unattend.xml','C:\Windows\Panther\Unattend\unattend.xml') | ForEach-Object {if(Test-Path -LiteralPath $_){Remove-Item -LiteralPath $_ -Force}}
    Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name AutoAdminLogon -Value '0'
    Enable-PSRemoting -Force -SkipNetworkProfileCheck *> $null
    Get-NetFirewallRule -Name 'WINRM*' -ErrorAction SilentlyContinue | Disable-NetFirewallRule
    Get-ChildItem WSMan:\localhost\Listener | Remove-Item -Recurse -Force
    # Named PowerShell Direct sessions use local WinRM inside the guest.
    foreach ($address in $loopback) {
        New-WSManInstance -ResourceURI $listenerUri -SelectorSet @{Address=$address;Transport='HTTP'} -ValueSet @{Port='5985';Enabled='true';URLPrefix='wsman'} | Out-Null
        $created.Add($address)
    }
    $listeners = @(Get-WSManInstance -ResourceURI $listenerUri -Enumerate)
    $invalid = @($listeners | Where-Object {
        $_.Address -notin $loopback -or $_.Transport -ne 'HTTP' -or $_.Enabled -ne 'true' -or
        @($_.ListeningOn | Where-Object { $_ -and $_ -notin '127.0.0.1','::1' }).Count -gt 0
    })
    $addresses = @($listeners | ForEach-Object { [string]$_.Address })
    if ($listeners.Count -ne $loopback.Count -or $invalid.Count -or
        $addresses -notcontains $loopback[0] -or $addresses -notcontains $loopback[1]) { throw 'GUEST_WINRM_SCOPE' }
    Remove-NetFirewallRule -Name $guard
    $null = Test-WSMan -ComputerName localhost
} catch {
    foreach ($address in $created) {
        Remove-WSManInstance -ResourceURI $listenerUri -SelectorSet @{Address=$address;Transport='HTTP'} -ErrorAction SilentlyContinue
    }
    Get-NetFirewallRule -Name 'WINRM*' -ErrorAction SilentlyContinue | Disable-NetFirewallRule -ErrorAction SilentlyContinue
    Get-ChildItem WSMan:\localhost\Listener -ErrorAction SilentlyContinue |
        Where-Object { $_.Keys -notcontains 'Address=IP:127.0.0.1' -and $_.Keys -notcontains 'Address=IP:::1' } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}
