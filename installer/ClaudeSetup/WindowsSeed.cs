using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClaudeSetup;

public static class WindowsSeed
{
    public static string? DetectIsoUiLanguage(string? isoPath)
    {
        if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath)) return null;
        try
        {
            using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var marker = Encoding.ASCII.GetBytes("[Available UI Languages]");
            var window = (int)Math.Min(64L * 1024 * 1024, stream.Length);
            var buffer = new byte[window];
            foreach (var offset in stream.Length > window ? new[] { 0L, stream.Length - window } : new[] { 0L })
            {
                stream.Position = offset;
                var read = stream.Read(buffer, 0, window);
                var pos = buffer.AsSpan(0, read).IndexOf(marker);
                if (pos < 0) continue;
                var snippet = Encoding.ASCII.GetString(buffer, pos, Math.Min(512, read - pos));
                var sectionEnd = snippet.IndexOf('[', marker.Length);
                if (sectionEnd > 0) snippet = snippet[..sectionEnd];
                string? fallback = null;
                foreach (Match m in Regex.Matches(snippet, @"^\s*(?<tag>[a-zA-Z]{2,3}-[a-zA-Z0-9-]{2,12})\s*=\s*(?<kind>[0-9]+)", RegexOptions.Multiline))
                {
                    var normalized = NormalizeCultureTag(m.Groups["tag"].Value);
                    if (normalized is null) continue;
                    fallback ??= normalized;
                    if (m.Groups["kind"].Value == "3") return normalized;
                }
                if (fallback is not null) return fallback;
            }
        }
        catch { }
        return null;
    }

    private static string? NormalizeCultureTag(string raw)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(raw.Trim()).Name;
            return Regex.IsMatch(name, "^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})+$") ? name : null;
        }
        catch { return null; }
    }

    // Windows Setup sees only the new VM's disk 0; this is never run on the host.
    public static string Answer(SetupOptions o, string password, string? uiLanguage = null)
    {
        XNamespace ns = "urn:schemas-microsoft-com:unattend", wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
        XElement E(string name, params object[] children) => new(ns + name, children);
        XElement Component(string name, params object[] children) => E("component", new XAttribute("name", name), new XAttribute("processorArchitecture", "amd64"), new XAttribute("publicKeyToken", "31bf3856ad364e35"), new XAttribute("language", "neutral"), new XAttribute("versionScope", "nonSxS"), children);
        XElement Action(string name, params object[] children) => E(name, new XAttribute(wcm + "action", "add"), children);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(password + "Password"));
        XElement Password() => E("Password", E("Value", encoded), E("PlainText", "false"));
        var firstLogon = "cmd.exe /d /c for %D in (D E F G H I J K L M N O P Q R S T U V W X Y Z) do @if exist %D:\\ClaudeBootstrap\\Initialize.cmd call %D:\\ClaudeBootstrap\\Initialize.cmd";
        // Convert the C# source literal into ordinary Windows path separators.
        firstLogon = firstLogon.Replace("\\\\", "\\");
        var create = E("CreatePartitions",
            Action("CreatePartition", E("Order", "1"), E("Type", "EFI"), E("Size", "260")),
            Action("CreatePartition", E("Order", "2"), E("Type", "MSR"), E("Size", "16")),
            Action("CreatePartition", E("Order", "3"), E("Type", "Primary"), E("Extend", "true")));
        var modify = E("ModifyPartitions",
            Action("ModifyPartition", E("Order", "1"), E("PartitionID", "1"), E("Format", "FAT32"), E("Label", "SYSTEM")),
            Action("ModifyPartition", E("Order", "2"), E("PartitionID", "3"), E("Format", "NTFS"), E("Label", "Windows"), E("Letter", "C")));
        var disks = E("DiskConfiguration", Action("Disk", E("DiskID", "0"), E("WillWipeDisk", "true"), create, modify));
        var install = E("ImageInstall", E("OSImage",
            E("InstallFrom", Action("MetaData", E("Key", "/IMAGE/INDEX"), E("Value", o.WindowsImageIndex.ToString()))),
            E("InstallTo", E("DiskID", "0"), E("PartitionID", "3"))));
        var bypass = E("RunSynchronous",
            Action("RunSynchronousCommand", E("Order", "1"), E("Path", @"reg.exe ADD HKLM\SYSTEM\Setup\LabConfig /v BypassCPUCheck /t REG_DWORD /d 1 /f")),
            Action("RunSynchronousCommand", E("Order", "2"), E("Path", @"reg.exe ADD HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f")),
            Action("RunSynchronousCommand", E("Order", "3"), E("Path", @"reg.exe ADD HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f")),
            Action("RunSynchronousCommand", E("Order", "4"), E("Path", @"reg.exe ADD HKLM\SYSTEM\Setup\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f")),
            Action("RunSynchronousCommand", E("Order", "5"), E("Path", @"reg.exe ADD HKLM\SYSTEM\Setup\LabConfig /v BypassStorageCheck /t REG_DWORD /d 1 /f")));
        var setup = Component("Microsoft-Windows-Setup", bypass, disks, install, E("UserData", E("AcceptEula", "true")));
        var winPeComponents = new List<object>();
        var detectedLanguage = NormalizeCultureTag(uiLanguage ?? "") ?? DetectIsoUiLanguage(o.IsoPath);
        if (!string.IsNullOrEmpty(detectedLanguage))
        {
            winPeComponents.Add(Component("Microsoft-Windows-International-Core-WinPE",
                E("SetupUILanguage", E("UILanguage", detectedLanguage)),
                E("InputLocale", detectedLanguage),
                E("SystemLocale", detectedLanguage),
                E("UILanguage", detectedLanguage),
                E("UserLocale", detectedLanguage)));
        }
        winPeComponents.Add(setup);

        var specializeShellChildren = new List<object> { E("ComputerName", "ClaudeWorkspace") };
        if (o.NetworkSource != "稍后配置" && !string.IsNullOrWhiteSpace(o.TimeZoneId))
            specializeShellChildren.Add(E("TimeZone", o.TimeZoneId));
        var specializeDeploy = Component("Microsoft-Windows-Deployment",
            E("RunSynchronous",
                Action("RunSynchronousCommand", E("Order", "1"), E("Path", @"reg.exe ADD HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE /v BypassNRO /t REG_DWORD /d 1 /f")),
                Action("RunSynchronousCommand", E("Order", "2"), E("Path", @"reg.exe ADD ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"" /v EnableFirstLogonAnimation /t REG_DWORD /d 0 /f")),
                Action("RunSynchronousCommand", E("Order", "3"), E("Path", @"reg.exe ADD ""HKLM\SYSTEM\CurrentControlSet\Control\Network\NewNetworkWindowOff"" /f"))));

        var accounts = E("UserAccounts", E("LocalAccounts", Action("LocalAccount", Password(), E("Name", o.UserName), E("DisplayName", o.UserName), E("Group", "Administrators"))));
        var logon = E("AutoLogon", Password(), E("Enabled", "true"), E("LogonCount", "1"), E("Username", o.UserName));
        var commands = E("FirstLogonCommands", Action("SynchronousCommand", E("Order", "1"), E("Description", "Install guest tools"), E("CommandLine", firstLogon)));
        var oobe = E("OOBE",
            E("ProtectYourPC", "3"),
            E("HideEULAPage", "true"),
            E("HideOnlineAccountScreens", "true"),
            E("HideWirelessSetupInOOBE", "true"),
            E("HideLocalAccountScreen", "true"));
        var shell = Component("Microsoft-Windows-Shell-Setup", oobe, accounts, logon, commands);
        var answer = E("unattend", new XAttribute(XNamespace.Xmlns + "wcm", wcm),
            E("settings", new XAttribute("pass", "windowsPE"), winPeComponents.ToArray()),
            E("settings", new XAttribute("pass", "specialize"), Component("Microsoft-Windows-Shell-Setup", specializeShellChildren.ToArray()), specializeDeploy),
            E("settings", new XAttribute("pass", "oobeSystem"), shell));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), answer).ToString();
    }
    public static void CreateIso(string directory, string destination)
    {
        Paths.RejectLinks(directory); Paths.RejectLinks(destination);
        if (File.Exists(destination)) throw new SetupException("安装介质已存在，未覆盖。请继续原任务，或检查是否仍被虚拟机占用。");
        dynamic image = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage") ?? throw new SetupException("当前 Windows 缺少光盘映像组件，无法自动制作安装介质。"))!;
        object? result = null, streamObject = null;
        try {
            image.FileSystemsToCreate = 3; image.VolumeName = "CLAUDESETUP"; image.Root.AddTree(directory, false);
            result = image.CreateResultImage(); streamObject = ((dynamic)result).ImageStream;
            var stream = (IStream)streamObject; var countPointer = Marshal.AllocCoTaskMem(4);
            try {
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read); var buffer = new byte[1024 * 1024];
                while (true) { stream.Read(buffer, buffer.Length, countPointer); var count = Marshal.ReadInt32(countPointer); if (count <= 0) break; output.Write(buffer, 0, count); }
            } finally { Marshal.FreeCoTaskMem(countPointer); }
        } finally {
            if (streamObject is not null && Marshal.IsComObject(streamObject)) Marshal.ReleaseComObject(streamObject);
            if (result is not null && Marshal.IsComObject(result)) Marshal.ReleaseComObject(result);
            if (Marshal.IsComObject(image)) Marshal.ReleaseComObject(image);
        }
    }
}
