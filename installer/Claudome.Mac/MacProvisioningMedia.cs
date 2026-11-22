using System.Security;
using System.Text;
using System.Text.Json;

namespace ClaudeSetup;

/// <summary>
/// Generates ARM64/x64 Windows 11 unattended installation media (Autounattend.xml and
/// guest bootstrap scripts) and exposes Mac/ARM64 resource metadata.
/// </summary>
public static class MacProvisioningMedia
{
    public sealed record MacGuestAsset(string Id, string Name, string Architecture, string FileName, string Url, bool ChecksumVerifiedOnMac, string Notes);

    /// <summary>Official guest component targets for Windows 11 ARM64 and x64 on macOS hosts.</summary>
    public static readonly IReadOnlyList<MacGuestAsset> GuestAssets =
    [
        new("pwsh-arm64", "PowerShell 7.7.0 (ARM64)", "arm64", "PowerShell-7.7.0-win-arm64.zip",
            "https://github.com/PowerShell/PowerShell/releases/download/v7.7.0/PowerShell-7.7.0-win-arm64.zip",
            false, "需在 Mac 真机阶段核定官方 SHA-256 后启用自动校验下载"),
        new("clash-arm64", "Clash Verge Rev 2.5.10 (ARM64)", "arm64", "Clash.Verge_2.5.10_arm64-setup.exe",
            "https://github.com/clash-verge-rev/clash-verge-rev/releases/download/v2.5.10/Clash.Verge_2.5.10_arm64-setup.exe",
            false, "原生 Windows ARM64 客户端"),
        new("adspower-x64", "AdsPower 8.9.23 (Win64 Prism 转译)", "x64", "AdsPower-Global-8.9.23-x64.exe",
            "https://version-v2.adspower.net/software/win64-global/8.9.23/AdsPower-Global-8.9.23-x64.exe",
            true, "尚无官方 Windows ARM64 版，在 ARM64 来宾中依赖 Windows Prism x64 转译，需真机实测")
    ];

    /// <summary>Builds a compliant UEFI/GPT Autounattend.xml for either ARM64 or x64 Windows 11 guests.</summary>
    public static string BuildAutounattendXml(SetupOptions options, string vmPassword, string guestArch)
    {
        if (guestArch is not ("arm64" or "amd64"))
            throw new SetupException("来宾系统架构仅支持 arm64 或 amd64。");
        var escapedUser = SecurityElement.Escape(options.UserName) ?? "ClaudeUser";
        var escapedPassword = SecurityElement.Escape(vmPassword) ?? "";
        var escapedZone = SecurityElement.Escape(options.TimeZoneId) ?? "Pacific Standard Time";
        var escapedVmName = SecurityElement.Escape(options.VmName) ?? "Claude-Workspace";

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
              <settings pass="windowsPE">
                <component name="Microsoft-Windows-International-Core-WinPE" processorArchitecture="{guestArch}" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  <SetupUILanguage>
                    <UILanguage>zh-CN</UILanguage>
                  </SetupUILanguage>
                  <InputLocale>0804:00000804</InputLocale>
                  <SystemLocale>zh-CN</SystemLocale>
                  <UILanguage>zh-CN</UILanguage>
                  <UserLocale>zh-CN</UserLocale>
                </component>
                <component name="Microsoft-Windows-Setup" processorArchitecture="{guestArch}" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  <DiskConfiguration>
                    <Disk wcm:action="add">
                      <DiskID>0</DiskID>
                      <WillWipeDisk>true</WillWipeDisk>
                      <CreatePartitions>
                        <CreatePartition wcm:action="add"><Order>1</Order><Type>EFI</Type><Size>260</Size></CreatePartition>
                        <CreatePartition wcm:action="add"><Order>2</Order><Type>MSR</Type><Size>16</Size></CreatePartition>
                        <CreatePartition wcm:action="add"><Order>3</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
                      </CreatePartitions>
                      <ModifyPartitions>
                        <ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>FAT32</Format><Label>System</Label></ModifyPartition>
                        <ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>2</PartitionID></ModifyPartition>
                        <ModifyPartition wcm:action="add"><Order>3</Order><PartitionID>3</PartitionID><Format>NTFS</Format><Letter>C</Letter><Label>Windows</Label></ModifyPartition>
                      </ModifyPartitions>
                    </Disk>
                  </DiskConfiguration>
                  <ImageInstall>
                    <OSImage>
                      <InstallFrom>
                        <MetaData wcm:action="add">
                          <Key>/IMAGE/INDEX</Key>
                          <Value>{options.WindowsImageIndex}</Value>
                        </MetaData>
                      </InstallFrom>
                      <InstallTo><DiskID>0</DiskID><PartitionID>3</PartitionID></InstallTo>
                    </OSImage>
                  </ImageInstall>
                  <UserData>
                    <AcceptEula>true</AcceptEula>
                  </UserData>
                </component>
              </settings>
              <settings pass="specialize">
                <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="{guestArch}" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  <ComputerName>{escapedVmName}</ComputerName>
                  <TimeZone>{escapedZone}</TimeZone>
                </component>
              </settings>
              <settings pass="oobeSystem">
                <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="{guestArch}" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
                  <UserAccounts>
                    <LocalAccounts>
                      <LocalAccount wcm:action="add">
                        <Name>{escapedUser}</Name>
                        <Group>Administrators</Group>
                        <Password><Value>{escapedPassword}</Value><PlainText>true</PlainText></Password>
                      </LocalAccount>
                    </LocalAccounts>
                  </UserAccounts>
                  <AutoLogon>
                    <Enabled>true</Enabled>
                    <Username>{escapedUser}</Username>
                    <Password><Value>{escapedPassword}</Value><PlainText>true</PlainText></Password>
                    <LogonCount>1</LogonCount>
                  </AutoLogon>
                  <OOBE>
                    <HideEULAPage>true</HideEULAPage>
                    <HideLocalAccountScreen>true</HideLocalAccountScreen>
                    <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
                    <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
                    <ProtectYourPC>3</ProtectYourPC>
                  </OOBE>
                  <FirstLogonCommands>
                    <SynchronousCommand wcm:action="add">
                      <Order>1</Order>
                      <CommandLine>cmd /c for %d in (D E F G) do if exist %d:\ClaudeBootstrap\Initialize.cmd call %d:\ClaudeBootstrap\Initialize.cmd</CommandLine>
                      <Description>Claudome Mac Guest Bootstrap</Description>
                    </SynchronousCommand>
                  </FirstLogonCommands>
                </component>
              </settings>
            </unattend>
            """;
    }

    /// <summary>Creates a macOS seed directory and hybrid ISO (via hdiutil on macOS) for unattended guest setup.</summary>
    public static async Task<string> EnsureSeedIsoAsync(SessionState state, Secrets secrets, bool isArm64, ICommandRunner runner, CancellationToken token)
    {
        var seedIso = Paths.Under(state.Options.Private, "mac-seed.iso");
        if (File.Exists(seedIso)) return seedIso;

        var stage = Paths.Under(state.Options.Private, "mac-seed");
        HostPlatform.Environment.RestrictPrivateDirectory(stage);
        var arch = isArm64 ? "arm64" : "amd64";
        await File.WriteAllTextAsync(Paths.Under(stage, "Autounattend.xml"),
            BuildAutounattendXml(state.Options, secrets.VmPassword, arch), new UTF8Encoding(false), token);

        var bootstrapDir = Paths.Under(stage, "ClaudeBootstrap");
        Directory.CreateDirectory(bootstrapDir);
        await File.WriteAllTextAsync(Paths.Under(bootstrapDir, "identity.json"),
            JsonSerializer.Serialize(new { vmId = state.VmId, workspaceId = state.Options.WorkspaceId, guestArch = arch }, JsonFile.Format),
            new UTF8Encoding(false), token);
        await File.WriteAllTextAsync(Paths.Under(bootstrapDir, "Initialize.cmd"),
            """
            @echo off
            setlocal
            if not exist "C:\ProgramData\Claudome" mkdir "C:\ProgramData\Claudome"
            copy /y "%~dp0identity.json" "C:\ProgramData\Claudome\identity.json" >nul
            """,
            new UTF8Encoding(false), token);

        if (OperatingSystem.IsMacOS())
        {
            await runner.Run("hdiutil", ["makehybrid", "-o", seedIso, stage, "-iso", "-joliet", "-default-volume-name", "OEMDRV"], token, 60);
        }
        return seedIso;
    }
}
