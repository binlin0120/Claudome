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
    public sealed record MacGuestAsset(
        string Id,
        string ResourceId,
        string Name,
        string Architecture,
        string FileName,
        string Url,
        string Sha256,
        long Size,
        string License,
        bool ChecksumVerifiedOnMac,
        string Notes);

    /// <summary>Official guest component targets for Windows 11 ARM64 and x64 on macOS hosts.</summary>
    public static readonly IReadOnlyList<MacGuestAsset> GuestAssets =
    [
        new("pwsh-arm64", "pwsh", "PowerShell 7.7.0 (ARM64)", "arm64", "PowerShell-7.7.0-win-arm64.zip",
            "https://github.com/PowerShell/PowerShell/releases/download/v7.7.0/PowerShell-7.7.0-win-arm64.zip",
            "5dc7440cd98f0ba786a44d7db9205df5058474fd4db143caf7171e8375f34f75", 107817704,
            "MIT; https://github.com/PowerShell/PowerShell", true, "已核验 GitHub 官方发行摘要（Apple Silicon Win11 ARM64）"),
        new("pwsh-x64", "pwsh", "PowerShell 7.7.0 (x64)", "x64", "PowerShell-7.7.0-win-x64.zip",
            "https://github.com/PowerShell/PowerShell/releases/download/v7.7.0/PowerShell-7.7.0-win-x64.zip",
            "7c74e03b20a75b59715847439d046a6214b1863ee79b187872980f139bb4a05f", 115330228,
            "MIT; https://github.com/PowerShell/PowerShell", true, "已核验 GitHub 官方发行摘要（Intel Mac Win11 x64）"),
        new("clash-arm64", "clash", "Clash Verge Rev 2.5.10 (ARM64)", "arm64", "Clash.Verge_2.5.10_arm64-setup.exe",
            "https://github.com/clash-verge-rev/clash-verge-rev/releases/download/v2.5.10/Clash.Verge_2.5.10_arm64-setup.exe",
            "800a2f49103c1aa2695db5a8e90644f96053db9ad2cc618e0bbc4d76c3cc7333", 53759493,
            "GPL-3.0; https://github.com/clash-verge-rev/clash-verge-rev", true, "原生 Windows ARM64 客户端（已核验官方 SHA-256）"),
        new("clash-x64", "clash", "Clash Verge Rev 2.5.10 (x64)", "x64", "Clash.Verge_2.5.10_x64-setup.exe",
            "https://github.com/clash-verge-rev/clash-verge-rev/releases/download/v2.5.10/Clash.Verge_2.5.10_x64-setup.exe",
            "929da9303b643b80ce309ab29f618e37a5562f515d1fa55ebf5b86c822504450", 60045702,
            "GPL-3.0; https://github.com/clash-verge-rev/clash-verge-rev", true, "原生 Windows x64 客户端（已核验官方 SHA-256）"),
        new("vcredist-x64", "vcredist", "Microsoft Visual C++ x64 运行库", "x64", "vc_redist.x64.exe",
            "https://download.visualstudio.microsoft.com/download/pr/e9604e21-4b79-4096-a4b3-1b63d4b55b4d/CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B/VC_redist.x64.exe",
            "cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b", 25635768,
            "Microsoft redistributable license; https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist", true, "AdsPower x64 及来宾组件运行前置（ARM64 经 Prism 转译兼容）"),
        new("adspower-x64", "adspower", "AdsPower 8.9.23 (x64 / Prism 转译)", "x64", "AdsPower-Global-8.9.23-x64.exe",
            "https://version-v2.adspower.net/software/win64-global/8.9.23/AdsPower-Global-8.9.23-x64.exe",
            "d50080db8f810a78513c079541774162195ce844fa14067f5dc3dc7e4aaf0649", 364817424,
            "Proprietary; https://www.adspower.com/", true, "尚无官方 Windows ARM64 版，在 ARM64 来宾依赖 Prism x64 转译，需真机实测")
    ];

    /// <summary>Returns Downloader-compatible resources for the target Mac host architecture.</summary>
    public static IReadOnlyList<Resource> GuestResources(bool isAppleSilicon)
    {
        var arch = isAppleSilicon ? "arm64" : "x64";
        return GuestAssets
            .Where(a => a.Architecture == arch || a.ResourceId is "vcredist" or "adspower")
            .Select(a => new Resource(a.ResourceId, a.Name, a.FileName, a.Url, a.Sha256, a.Size, a.License))
            .ToList();
    }

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
            if not exist "C:\ProgramData\ClaudeBootstrap" mkdir "C:\ProgramData\ClaudeBootstrap"
            copy /y "%~dp0identity.json" "C:\ProgramData\ClaudeBootstrap\identity.json" >nul
            """,
            new UTF8Encoding(false), token);

        if (OperatingSystem.IsMacOS())
        {
            await runner.Run("hdiutil", ["makehybrid", "-o", seedIso, stage, "-iso", "-joliet", "-default-volume-name", "OEMDRV"], token, 60);
        }
        return seedIso;
    }
}
