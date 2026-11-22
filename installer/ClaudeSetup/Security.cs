using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeSetup;

public static class SecretStore
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static void RestrictDirectory(string path)
    {
        Paths.RejectLinks(path);
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    public static byte[] Protect(byte[] data, bool decrypt = false)
    {
        var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptProtectData(ref input, "Claude setup / current user", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new SetupException("本机加密资料不可用，请使用保存时的 Windows 账号，或重新填写。");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (int i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
    private static string CredentialsPath(string directory, string revision)
    {
        if (revision.Length == 0) return Paths.Under(directory, "credentials.dpapi");
        if (!Guid.TryParseExact(revision, "N", out _)) throw new SetupException("来宾资料版本无效。");
        return Paths.Under(directory, $"credentials-{revision}.dpapi");
    }
    public static void Save(string directory, Secrets secrets, string revision = "")
    {
        RestrictDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(secrets);
        try
        {
            var target = CredentialsPath(directory, revision);
            var cipher = Protect(plain);
            var verify = Protect(cipher, true);
            try { if (!plain.AsSpan().SequenceEqual(verify)) throw new SetupException("加密读回校验失败。"); } finally { CryptographicOperations.ZeroMemory(verify); }
            var temporary = Paths.Under(directory, $"credentials-{Guid.NewGuid():N}.new");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(cipher);
                    stream.Flush(true);
                }
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static void SaveMerged(string directory, Secrets incoming, bool preserveHiddenNetwork, bool preserveUnloadedSecrets = false, string revision = "")
    {
        Secrets? existing = null;
        if (preserveHiddenNetwork || preserveUnloadedSecrets)
        {
            try { existing = Load(directory, revision); } catch (SetupException) { existing = null; }
        }
        if (existing is not null)
        {
            string Pick(string current, string saved, bool preserve) => preserve && string.IsNullOrEmpty(current) ? saved : current;
            incoming = new Secrets
            {
                VmPassword = Pick(incoming.VmPassword, existing.VmPassword, preserveUnloadedSecrets),
                VmMicrosoftAddress = Pick(incoming.VmMicrosoftAddress, existing.VmMicrosoftAddress,
                    preserveUnloadedSecrets && string.IsNullOrEmpty(incoming.VmPassword)),
                AdsPowerApiKey = Pick(incoming.AdsPowerApiKey, existing.AdsPowerApiKey, preserveUnloadedSecrets),
                ExpectedExitIp = Pick(incoming.ExpectedExitIp, existing.ExpectedExitIp, preserveHiddenNetwork || preserveUnloadedSecrets),
                SubscriptionUrl = Pick(incoming.SubscriptionUrl, existing.SubscriptionUrl, preserveHiddenNetwork || preserveUnloadedSecrets),
                NetworkText = Pick(incoming.NetworkText, existing.NetworkText, preserveHiddenNetwork || preserveUnloadedSecrets),
                SelectedNodeJson = Pick(incoming.SelectedNodeJson, existing.SelectedNodeJson, preserveHiddenNetwork || preserveUnloadedSecrets),
                UpstreamNodeJson = Pick(incoming.UpstreamNodeJson, existing.UpstreamNodeJson, preserveHiddenNetwork || preserveUnloadedSecrets),
                UpstreamSubscriptionUrl = Pick(incoming.UpstreamSubscriptionUrl, existing.UpstreamSubscriptionUrl, preserveHiddenNetwork || preserveUnloadedSecrets),
                UpstreamNetworkText = Pick(incoming.UpstreamNetworkText, existing.UpstreamNetworkText, preserveHiddenNetwork || preserveUnloadedSecrets),
                BackupRoutes = incoming.BackupRoutes.Count == 0 ? existing.BackupRoutes : incoming.BackupRoutes
            };
        }
        Save(directory, incoming, revision);
    }
    public static Secrets? Load(SessionState state) => Load(state.Options.Private, state.CredentialsRevision);
    public static Secrets? Load(string directory, string revision = "")
    {
        var path = CredentialsPath(directory, revision);
        if (!File.Exists(path)) return null;
        var plain = Protect(File.ReadAllBytes(path), true);
        try { return JsonSerializer.Deserialize<Secrets>(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static void PruneRevisions(SessionState committed, SessionState previous)
    {
        var o = committed.Options;
        if (!Directory.Exists(o.Private)) return;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (o.Remember)
        {
            keep.Add(CredentialsPath(o.Private, committed.CredentialsRevision));
            if (previous.Options.Remember) keep.Add(CredentialsPath(o.Private, previous.CredentialsRevision));
        }
        foreach (var file in Directory.EnumerateFiles(o.Private, "credentials*.dpapi"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name != "credentials" && !(name.StartsWith("credentials-") &&
                Guid.TryParseExact(name[12..], "N", out _))) continue;
            Paths.RejectLinks(file);
            if (!keep.Contains(file)) File.Delete(file);
        }
    }

    public static void Forget(SessionState state) => Forget(state.Options.Private, state.CredentialsRevision);
    public static void Forget(string directory, string revision = "")
    {
        var file = CredentialsPath(directory, revision);
        if (File.Exists(file)) File.Delete(file);
    }
}
