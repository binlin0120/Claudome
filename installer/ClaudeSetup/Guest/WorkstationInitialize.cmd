@echo off
setlocal
set "CLAUDE_SEED=%~dp0"
set "CLAUDE_BOOT=C:\ProgramData\ClaudeBootstrap"
if not exist "%CLAUDE_BOOT%" mkdir "%CLAUDE_BOOT%"
copy /y "%CLAUDE_SEED%identity.json" "%CLAUDE_BOOT%\identity.json" >nul
if not exist "%CLAUDE_BOOT%\pwsh" mkdir "%CLAUDE_BOOT%\pwsh"
tar.exe -xf "%CLAUDE_SEED%pwsh.zip" -C "%CLAUDE_BOOT%\pwsh"
if errorlevel 1 exit /b 1
for %%D in (D E F G H I J K L M N O P Q R S T U V W X Y Z) do if exist "%%D:\setup64.exe" "%%D:\setup64.exe" /S /v"/qn REBOOT=R"
"%CLAUDE_BOOT%\pwsh\pwsh.exe" -NoLogo -NoProfile -Command "if((Get-CimInstance Win32_ComputerSystem).Manufacturer -notmatch 'VMware'){exit 1};if(-not(Get-Service VMTools -ErrorAction SilentlyContinue)){exit 1};Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name DefaultPassword -ErrorAction SilentlyContinue;Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name AutoAdminLogon -Value '0';@{ready=$true}|ConvertTo-Json|Set-Content -LiteralPath 'C:\ProgramData\ClaudeBootstrap\ready.json' -Encoding utf8NoBOM"
endlocal
