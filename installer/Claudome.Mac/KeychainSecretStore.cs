using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSetup;

/// <summary>
/// Persists workspace secrets on macOS using AES-256-GCM authenticated encryption
/// keyed by a per-workspace 256-bit key stored in the user's macOS Keychain.
/// </summary>
public sealed class KeychainSecretStore(ICommandRunner? runner = null) : ISecretStore
{
    private const string KeychainService = "com.claudome.workspace";
    private readonly ICommandRunner commandRunner = runner ?? new CommandRunner();

    internal static byte[] ProtectWithKey(byte[] plaintext, byte[] key)
    {
        if (key.Length != 32) throw new SetupException("密钥长度无效。");
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var envelope = new byte[4 + nonce.Length + tag.Length + ciphertext.Length];
        Encoding.ASCII.GetBytes("CLM1", envelope.AsSpan(0, 4));
        nonce.CopyTo(envelope, 4);
        tag.CopyTo(envelope, 4 + nonce.Length);
        ciphertext.CopyTo(envelope, 4 + nonce.Length + tag.Length);
        return envelope;
    }

    internal static byte[] UnprotectWithKey(byte[] envelope, byte[] key)
    {
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        var headerSize = 4 + nonceSize + tagSize;
        if (key.Length != 32 || envelope.Length < headerSize || !envelope.AsSpan(0, 4).SequenceEqual("CLM1"u8))
            throw new SetupException("本机加密资料格式无效或已损坏。");

        var nonce = envelope.AsSpan(4, nonceSize);
        var tag = envelope.AsSpan(4 + nonceSize, tagSize);
        var ciphertext = envelope.AsSpan(headerSize);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, tagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new SetupException("本机 Keychain 加密资料校验失败，请确认使用保存时的 macOS 账号或重新填写。");
        }
    }

    private static string CredentialsPath(string directory, string revision)
    {
        if (revision.Length == 0) return Paths.Under(directory, "credentials.mac");
        if (!Guid.TryParseExact(revision, "N", out _)) throw new SetupException("来宾资料版本无效。");
        return Paths.Under(directory, $"credentials-{revision}.mac");
    }

    private static string KeychainAccount(string directory, string revision)
    {
        var canonical = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16].ToLowerInvariant();
        return string.IsNullOrEmpty(revision) ? hash : $"{hash}:{revision}";
    }

    private byte[] GetOrCreateMasterKey(string directory, string revision)
    {
        if (!OperatingSystem.IsMacOS())
            throw new SetupException("macOS Keychain 仅在 macOS 主机可用。");
        var account = KeychainAccount(directory, revision);
        var existing = TryReadKeychainHex(account);
        if (existing is not null) return existing;

        var key = RandomNumberGenerator.GetBytes(32);
        var hex = Convert.ToHexString(key).ToLowerInvariant();
        try
        {
            commandRunner.Run("security",
                ["add-generic-password", "-U", "-a", account, "-s", KeychainService, "-w", hex],
                CancellationToken.None, 15).GetAwaiter().GetResult();
            var verified = TryReadKeychainHex(account) ?? throw new SetupException("Keychain 密钥回读校验失败。");
            if (!CryptographicOperations.FixedTimeEquals(key, verified))
                throw new SetupException("Keychain 密钥回读不一致。");
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[]? TryReadKeychainHex(string account)
    {
        try
        {
            var raw = commandRunner.Run("security",
                ["find-generic-password", "-a", account, "-s", KeychainService, "-w"],
                CancellationToken.None, 15).GetAwaiter().GetResult().Trim();
            if (!Regex.IsMatch(raw, "^[0-9a-fA-F]{64}$")) return null;
            return Convert.FromHexString(raw);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string directory, Secrets secrets, string revision = "")
    {
        HostPlatform.Environment.RestrictPrivateDirectory(directory);
        var key = GetOrCreateMasterKey(directory, revision);
        var plain = JsonSerializer.SerializeToUtf8Bytes(secrets);
        try
        {
            var target = CredentialsPath(directory, revision);
            var cipher = ProtectWithKey(plain, key);
            var verify = UnprotectWithKey(cipher, key);
            try { if (!plain.AsSpan().SequenceEqual(verify)) throw new SetupException("Keychain 加密读回校验失败。"); }
            finally { CryptographicOperations.ZeroMemory(verify); }

            var temporary = Paths.Under(directory, $"credentials-{Guid.NewGuid():N}.new");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(cipher);
                    stream.Flush(true);
                }
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public Secrets? Load(string directory, string revision = "")
    {
        var path = CredentialsPath(directory, revision);
        if (!File.Exists(path)) return null;
        if (!OperatingSystem.IsMacOS()) return null;
        var key = TryReadKeychainHex(KeychainAccount(directory, revision))
            ?? throw new SetupException("未在 macOS Keychain 中找到此任务的加密密钥，请重新填写资料。");
        try
        {
            var plain = UnprotectWithKey(File.ReadAllBytes(path), key);
            try { return JsonSerializer.Deserialize<Secrets>(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void SaveMerged(string directory, Secrets incoming, bool preserveHiddenNetwork, bool preserveUnloadedSecrets = false, string revision = "")
    {
        Secrets? existing = null;
        if (preserveHiddenNetwork || preserveUnloadedSecrets)
        {
            try { existing = Load(directory, revision); } catch (SetupException) { existing = null; }
        }
        if (existing is not null)
        {
            static string Pick(string current, string saved, bool preserve) => preserve && string.IsNullOrEmpty(current) ? saved : current;
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

    public void Forget(string directory, string revision = "")
    {
        var file = CredentialsPath(directory, revision);
        if (File.Exists(file)) File.Delete(file);
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                commandRunner.Run("security",
                    ["delete-generic-password", "-a", KeychainAccount(directory, revision), "-s", KeychainService],
                    CancellationToken.None, 10).GetAwaiter().GetResult();
            }
            catch { }
        }
    }

    public void PruneRevisions(SessionState committed, SessionState previous)
    {
        var o = committed.Options;
        if (!Directory.Exists(o.Private)) return;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (o.Remember)
        {
            keep.Add(CredentialsPath(o.Private, committed.CredentialsRevision));
            if (previous.Options.Remember) keep.Add(CredentialsPath(o.Private, previous.CredentialsRevision));
        }
        foreach (var file in Directory.EnumerateFiles(o.Private, "credentials*.mac"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name != "credentials" && !(name.StartsWith("credentials-") &&
                Guid.TryParseExact(name[12..], "N", out _))) continue;
            Paths.RejectLinks(file);
            if (!keep.Contains(file)) File.Delete(file);
        }
    }
}
