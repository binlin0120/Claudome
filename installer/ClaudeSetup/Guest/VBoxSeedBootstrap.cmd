@echo off
setlocal
reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultPassword /f >nul 2>&1
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoAdminLogon /t REG_SZ /d 0 /f >nul 2>&1
for %%D in (D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
    if exist "%%D:\VBoxWindowsAdditions.exe" (
        if exist "%%D:\cert\VBoxCertUtil.exe" (
            "%%D:\cert\VBoxCertUtil.exe" add-trusted-publisher "%%D:\cert\vbox*.cer" --root "%%D:\cert\vbox*.cer" >nul 2>&1
        )
        "%%D:\VBoxWindowsAdditions.exe" /S
        exit /b 0
    )
)
endlocal
