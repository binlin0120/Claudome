#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidatePattern('^[\p{L}\p{N}](?:[\p{L}\p{N}_.-]| [\p{L}\p{N}_.-]){0,19}$')][string]$UserName,
    [ValidatePattern('\AC:\\Users\\[\p{L}\p{N}_][\p{L}\p{M}\p{N}_. -]{0,62}\\AppData\\Local\z')][string]$LocalAppData,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$SessionId,
    [switch]$Interactive,
    [switch]$Stop
)
$ErrorActionPreference = 'Stop'
$profileDirectory = if ($LocalAppData) { $LocalAppData } else { 'C:\Users\' + $UserName + '\AppData\Local' }
$baseDirectory = $profileDirectory + '\ClaudeEnvironmentSetup'
$folder = $baseDirectory + '\clipboard-sync'
$exe = $baseDirectory + '\pwsh\pwsh.exe'
if ($PSScriptRoot -ne $folder -or $PSCommandPath -ne (Join-Path $folder 'ClipboardSyncAgent.ps1')) { throw 'CLIPBOARD_SCOPE' }
for ($item = [IO.DirectoryInfo]::new($folder); $null -ne $item; $item = $item.Parent) {
    if ($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'CLIPBOARD_SCOPE' }
}

$account = [Security.Principal.NTAccount]::new($env:COMPUTERNAME, $UserName)
$taskName = 'ClaudomeClipboardSync'
$bootstrapFile = Join-Path $folder ('bootstrap-' + $SessionId + '.json')

if ($Stop) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $folder -Filter 'bootstrap-*.json' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    return
}

if ($Interactive) {
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $actual = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($session -eq 0 -or $actual -ne $account.Translate([Security.Principal.SecurityIdentifier])) {
        if ([IO.File]::Exists($bootstrapFile)) { [IO.File]::Delete($bootstrapFile) }
        return
    }
    if (-not [IO.File]::Exists($bootstrapFile)) { return }
    $raw = Get-Content -LiteralPath $bootstrapFile -Raw -Encoding utf8
    # Immediately remove bootstrap file so key never remains on disk.
    try { [IO.File]::Delete($bootstrapFile) } catch { }
    $cfg = $raw | ConvertFrom-Json
    $serviceId = [Guid]::Parse([string]$cfg.serviceId)
    $keyBytes = [Convert]::FromBase64String([string]$cfg.key)
    $generation = [long]$cfg.generation

    Add-Type -Language CSharp -ReferencedAssemblies @(
        'System.Runtime',
        'System.Net.Sockets',
        'System.Net.Primitives',
        'System.Security.Cryptography',
        'System.Windows.Forms',
        'System.Drawing',
        'System.ComponentModel.Primitives',
        'System.Memory',
        'System.Buffers'
    ) -TypeDefinition @'
using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public static class ClaudomeGuestClipboardBridge
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int MaxTextLength = 16384;
    private const int MaxPayloadBytes = 65536;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CLDCLP01");
    private static readonly Guid ParentVmId = new Guid("a42e7cda-d03f-480c-9cc2-a4de20abb878");

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);

    private sealed class HvSockEndPoint : EndPoint
    {
        private readonly SocketAddress addr;
        public HvSockEndPoint(Guid vmId, Guid serviceId)
        {
            addr = new SocketAddress((AddressFamily)34, 36);
            var vmBytes = vmId.ToByteArray();
            var svcBytes = serviceId.ToByteArray();
            for (int i = 0; i < 16; i++) addr[4 + i] = vmBytes[i];
            for (int i = 0; i < 16; i++) addr[20 + i] = svcBytes[i];
        }
        public override AddressFamily AddressFamily => (AddressFamily)34;
        public override SocketAddress Serialize() => addr;
        public override EndPoint Create(SocketAddress socketAddress) => this;
    }

    private sealed class ListenerWindow : Form
    {
        private readonly Action onUpdate;
        public ListenerWindow(Action onUpdate)
        {
            this.onUpdate = onUpdate;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;
            Width = 1;
            Height = 1;
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            AddClipboardFormatListener(Handle);
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            try { RemoveClipboardFormatListener(Handle); } catch { }
            base.OnHandleDestroyed(e);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                try { onUpdate(); } catch { }
            }
            base.WndProc(ref m);
        }
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static bool TryReadPlainText(out string text)
    {
        text = "";
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(25);
                continue;
            }
            try
            {
                if (IsClipboardFormatAvailable(CF_HDROP)) return false;
                if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return false;
                var h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero) return false;
                var p = GlobalLock(h);
                if (p == IntPtr.Zero) return false;
                try
                {
                    var value = Marshal.PtrToStringUni(p);
                    if (string.IsNullOrEmpty(value) || value.Length > MaxTextLength || value.IndexOf('\0') >= 0)
                        return false;
                    text = value;
                    return true;
                }
                finally { GlobalUnlock(h); }
            }
            finally { CloseClipboard(); }
        }
        return false;
    }

    private static bool TryWritePlainText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength || text.IndexOf('\0') >= 0) return false;
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(25);
                continue;
            }
            try
            {
                if (!EmptyClipboard()) return false;
                var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (hGlobal == IntPtr.Zero) return false;
                var ptr = GlobalLock(hGlobal);
                if (ptr == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
                try { Marshal.Copy(bytes, 0, ptr, bytes.Length); }
                finally { GlobalUnlock(hGlobal); }
                if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    GlobalFree(hGlobal);
                    return false;
                }
                return true;
            }
            finally { CloseClipboard(); }
        }
        return false;
    }

    private static byte[] Mac(byte[] key, string role, byte[] first, byte[] second)
    {
        var r = Encoding.ASCII.GetBytes(role);
        var buf = new byte[r.Length + first.Length + second.Length];
        Buffer.BlockCopy(r, 0, buf, 0, r.Length);
        Buffer.BlockCopy(first, 0, buf, r.Length, first.Length);
        Buffer.BlockCopy(second, 0, buf, r.Length + first.Length, second.Length);
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(buf);
    }

    private static void ReadExact(Stream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = s.Read(buf, off, buf.Length - off);
            if (n <= 0) throw new EndOfStreamException();
            off += n;
        }
    }

    private static void WriteFrame(Stream s, object sendLock, byte type, long seq, long gen, string text)
    {
        byte[] textBytes = type == 1 ? Encoding.UTF8.GetBytes(text) : Array.Empty<byte>();
        int payloadLen = type == 1 ? 16 + textBytes.Length : 0;
        if (payloadLen > MaxPayloadBytes) return;
        var frame = new byte[5 + payloadLen];
        frame[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payloadLen);
        if (type == 1)
        {
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(5, 8), seq);
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(13, 8), gen);
            Buffer.BlockCopy(textBytes, 0, frame, 21, textBytes.Length);
        }
        lock (sendLock)
        {
            s.Write(frame, 0, frame.Length);
            s.Flush();
        }
    }

    public static void Run(Guid serviceId, byte[] key, long generation)
    {
        using var socket = new Socket((AddressFamily)34, SocketType.Stream, (ProtocolType)1);
        socket.Connect(new HvSockEndPoint(ParentVmId, serviceId));
        using var stream = new NetworkStream(socket, ownsSocket: false);

        // Mutual HMAC handshake (CLDCLP01)
        var clientNonce = RandomNumberGenerator.GetBytes(16);
        var hello = new byte[Magic.Length + 16];
        Buffer.BlockCopy(Magic, 0, hello, 0, Magic.Length);
        Buffer.BlockCopy(clientNonce, 0, hello, Magic.Length, 16);
        stream.Write(hello, 0, hello.Length);
        stream.Flush();

        var reply = new byte[48];
        ReadExact(stream, reply);
        var serverNonce = new byte[16];
        var serverMac = new byte[32];
        Buffer.BlockCopy(reply, 0, serverNonce, 0, 16);
        Buffer.BlockCopy(reply, 16, serverMac, 0, 32);
        var expectedMac = Mac(key, "clip-server", clientNonce, serverNonce);
        if (!CryptographicOperations.FixedTimeEquals(serverMac, expectedMac))
            throw new InvalidOperationException("CLIPBOARD_AUTH_FAILED");

        var clientProof = Mac(key, "clip-client", serverNonce, clientNonce);
        stream.Write(clientProof, 0, clientProof.Length);
        stream.Flush();

        var sendLock = new object();
        var stateLock = new object();
        uint lastObservedSeq = GetClipboardSequenceNumber();
        uint lastAppliedSeq = 0;
        string lastAppliedHash = "";
        DateTime lastAppliedUtc = DateTime.MinValue;
        long outSeq = 0;

        using var form = new ListenerWindow(() =>
        {
            uint seq = GetClipboardSequenceNumber();
            string text;
            if (!TryReadPlainText(out text)) return;
            string hash = HashText(text);
            bool shouldSend;
            lock (stateLock)
            {
                if (seq != 0 && seq == lastObservedSeq) return;
                lastObservedSeq = seq;
                if (seq != 0 && seq == lastAppliedSeq) return;
                if (!string.IsNullOrEmpty(lastAppliedHash) && hash == lastAppliedHash && (DateTime.UtcNow - lastAppliedUtc) <= TimeSpan.FromSeconds(2))
                    return;
                shouldSend = true;
            }
            if (shouldSend)
            {
                try { WriteFrame(stream, sendLock, 1, Interlocked.Increment(ref outSeq), generation, text); }
                catch { try { form.BeginInvoke((Action)(() => form.Close())); } catch { } }
            }
        });

        form.Shown += (_, _) => form.Hide();
        Task.Run(() =>
        {
            try
            {
                var header = new byte[5];
                while (true)
                {
                    ReadExact(stream, header);
                    byte type = header[0];
                    int len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
                    if (len < 0 || len > MaxPayloadBytes) break;
                    if (type == 3) break;
                    if (type == 2)
                    {
                        if (len > 0) { var skip = new byte[len]; ReadExact(stream, skip); }
                        continue;
                    }
                    if (type != 1 || len < 16) break;
                    var payload = new byte[len];
                    ReadExact(stream, payload);
                    long frameGen = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(8, 8));
                    if (frameGen != generation) continue;
                    var text = Encoding.UTF8.GetString(payload, 16, len - 16);
                    if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength || text.IndexOf('\0') >= 0) continue;
                    var hash = HashText(text);
                    if (!form.IsDisposed && form.IsHandleCreated)
                    {
                        form.BeginInvoke((Action)(() =>
                        {
                            lock (stateLock)
                            {
                                lastAppliedHash = hash;
                                lastAppliedUtc = DateTime.UtcNow;
                            }
                            if (TryWritePlainText(text))
                            {
                                uint newSeq = GetClipboardSequenceNumber();
                                lock (stateLock)
                                {
                                    lastAppliedSeq = newSeq;
                                    lastObservedSeq = newSeq;
                                    lastAppliedHash = hash;
                                    lastAppliedUtc = DateTime.UtcNow;
                                }
                            }
                        }));
                    }
                }
            }
            catch { }
            finally
            {
                try { if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke((Action)(() => form.Close())); } catch { }
            }
        });

        Application.Run(form);
    }
}
'@

    [ClaudomeGuestClipboardBridge]::Run($serviceId, $keyBytes, $generation)
    return
}

# Launcher mode: register & start interactive desktop user session task (replaces any previous sync agent).
Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
$taskArguments = '-NoProfile -STA -WindowStyle Hidden -File "' + $PSCommandPath + '" -UserName "' + $UserName + '" -SessionId ' + $SessionId + ' -Interactive'
if ($LocalAppData) { $taskArguments += ' -LocalAppData "' + $LocalAppData + '"' }
$action = New-ScheduledTaskAction -Execute $exe -Argument $taskArguments -WorkingDirectory $folder
$principal = New-ScheduledTaskPrincipal -UserId $account.Value -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $taskName
