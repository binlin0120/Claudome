using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Claudome.Transport;

namespace ClaudeSetup;

/// <summary>
/// Route B seamless bidirectional plain-text Ctrl+C / Ctrl+V direct clipboard sync
/// over a task-scoped, mutually authenticated AF_HYPERV HvSocket channel (CLDCLP01).
/// Strictly Unicode plain text only (CF_UNICODETEXT, <= 16,384 chars); never forwards
/// files (CF_HDROP), images, or rich formats; never persists clipboard content.
/// </summary>
internal sealed class HyperVClipboardDirectSync : IAsyncDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;

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

    private readonly Dispatcher dispatcher;
    private readonly string taskId;
    private readonly string vmId;
    private readonly Guid serviceId;
    private readonly byte[] key;
    private readonly long generation;
    private readonly ClipboardSyncDeduplicator dedup = new();
    private readonly CancellationTokenSource cts = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);

    private HwndSource? messageWindow;
    private Socket? listener;
    private NetworkStream? activeStream;
    private Task? acceptLoop;
    private long outSequence;
    private int disposed;

    public bool IsConnected => activeStream is not null && Volatile.Read(ref disposed) == 0;
    public event Action<bool>? ConnectionChanged;

    public HyperVClipboardDirectSync(Dispatcher dispatcher, string taskId, string vmId, Guid serviceId, byte[] key, long generation)
    {
        this.dispatcher = dispatcher;
        this.taskId = taskId;
        this.vmId = vmId;
        this.serviceId = serviceId;
        this.key = [.. key];
        this.generation = generation;
        dedup.Reset(taskId, generation, GetClipboardSequenceNumber());
    }

    public void Start()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        dispatcher.Invoke(() =>
        {
            if (messageWindow is not null) return;
            var parameters = new HwndSourceParameters("ClaudomeClipboardListener")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = new IntPtr(-3) // HWND_MESSAGE
            };
            messageWindow = new HwndSource(parameters);
            messageWindow.AddHook(WndProc);
            AddClipboardFormatListener(messageWindow.Handle);
        });

        listener = HypervisorSockets.ListenHyperV(serviceId);
        acceptLoop = Task.Run(() => RunAcceptLoopAsync(cts.Token));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && Volatile.Read(ref disposed) == 0)
            HandleLocalClipboardUpdate();
        return IntPtr.Zero;
    }

    private void HandleLocalClipboardUpdate()
    {
        var stream = activeStream;
        if (stream is null) return;
        var seq = GetClipboardSequenceNumber();
        if (!TryReadHostPlainText(out var text)) return;
        if (!dedup.ShouldSendLocalChange(taskId, generation, seq, text, DateTime.UtcNow)) return;
        var outSeq = Interlocked.Increment(ref outSequence);
        _ = SendTextAsync(stream, outSeq, text);
    }

    private async Task SendTextAsync(NetworkStream stream, long seq, string text)
    {
        try
        {
            await sendGate.WaitAsync(cts.Token);
            try
            {
                if (!ReferenceEquals(activeStream, stream) || Volatile.Read(ref disposed) != 0) return;
                await ClipboardProtocol.WriteFrameAsync(stream, new(ClipboardFrameType.TextUpdate, seq, generation, text), cts.Token);
            }
            finally { sendGate.Release(); }
        }
        catch { }
    }

    private async Task RunAcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener is not null)
        {
            Socket? peer = null;
            try
            {
                peer = await listener.AcceptAsync(token);
                if (!HypervisorSockets.IsExpectedHyperVPeer(peer, vmId))
                {
                    peer.Dispose();
                    continue;
                }
                using var stream = new NetworkStream(peer, ownsSocket: true);
                peer = null;
                await ClipboardProtocol.AuthenticateAsync(stream, key, server: true, token);
                activeStream = stream;
                NotifyConnection(true);

                while (!token.IsCancellationRequested)
                {
                    var frame = await ClipboardProtocol.ReadFrameAsync(stream, token);
                    if (frame is null || frame.Value.Type == ClipboardFrameType.Stop) break;
                    if (frame.Value.Type != ClipboardFrameType.TextUpdate) continue;
                    if (frame.Value.Generation != generation) continue;
                    var text = frame.Value.Text;
                    if (!dedup.TryBeginApplyRemote(taskId, generation, text, DateTime.UtcNow, out var hash))
                        continue;
                    await dispatcher.InvokeAsync(() =>
                    {
                        if (Volatile.Read(ref disposed) != 0) return;
                        if (TryWriteHostPlainText(text))
                        {
                            var newSeq = GetClipboardSequenceNumber();
                            dedup.CompleteApplyRemote(taskId, generation, newSeq, hash, DateTime.UtcNow);
                        }
                    }, DispatcherPriority.Send, token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
            finally
            {
                peer?.Dispose();
                if (activeStream is not null)
                {
                    activeStream = null;
                    NotifyConnection(false);
                }
            }
        }
    }

    private void NotifyConnection(bool connected)
    {
        try { dispatcher.BeginInvoke(() => ConnectionChanged?.Invoke(connected)); } catch { }
    }

    internal static bool TryReadHostPlainText(out string plainText)
    {
        plainText = "";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(20);
                continue;
            }
            try
            {
                var hasFiles = IsClipboardFormatAvailable(CF_HDROP);
                var hasText = IsClipboardFormatAvailable(CF_UNICODETEXT);
                if (hasFiles || !hasText) return false;
                var handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero) return false;
                var ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) return false;
                try
                {
                    var raw = Marshal.PtrToStringUni(ptr);
                    return ClipboardProtocol.ShouldForwardClipboardSnapshot(false, true, raw, out plainText);
                }
                finally { GlobalUnlock(handle); }
            }
            finally { CloseClipboard(); }
        }
        return false;
    }

    internal static bool TryWriteHostPlainText(string text)
    {
        if (!ClipboardProtocol.TryValidatePlainText(text, out var valid)) return false;
        var bytes = Encoding.Unicode.GetBytes(valid + "\0");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(20);
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

    public static async Task LaunchGuestAgentAsync(
        HyperV backend,
        SessionState state,
        Secrets secrets,
        Guid serviceId,
        byte[] key,
        long generation,
        CancellationToken token)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var localAppData = $@"C:\Users\{state.Options.UserName}\AppData\Local";
        var guestDir = localAppData + @"\ClaudeEnvironmentSetup\clipboard-sync";
        var guestPwsh = localAppData + @"\ClaudeEnvironmentSetup\pwsh\pwsh.exe";
        var guestScript = guestDir + @"\ClipboardSyncAgent.ps1";
        var guestBootstrap = guestDir + $@"\bootstrap-{sessionId}.json";

        using var scriptFile = new PrivateFile(state.Options.Private, Catalog.EmbeddedText("ClipboardSyncAgent.ps1"), ".ps1");
        using var bootFile = new PrivateFile(state.Options.Private, JsonSerializer.Serialize(new
        {
            serviceId = serviceId.ToString("D"),
            key = Convert.ToBase64String(key),
            generation
        }), ".json");

        await backend.CopyTo(state, secrets, scriptFile.FileName, guestScript, token);
        await backend.CopyTo(state, secrets, bootFile.FileName, guestBootstrap, token);
        await backend.GuestRun(state, secrets, guestPwsh,
            ["-NoLogo", "-NoProfile", "-File", guestScript, "-UserName", state.Options.UserName, "-LocalAppData", localAppData, "-SessionId", sessionId],
            token, 20);
    }

    public static async Task StopGuestAgentAsync(HyperV backend, SessionState state, Secrets secrets, CancellationToken token)
    {
        var localAppData = $@"C:\Users\{state.Options.UserName}\AppData\Local";
        var guestDir = localAppData + @"\ClaudeEnvironmentSetup\clipboard-sync";
        var guestPwsh = localAppData + @"\ClaudeEnvironmentSetup\pwsh\pwsh.exe";
        var guestScript = guestDir + @"\ClipboardSyncAgent.ps1";
        try
        {
            await backend.GuestRun(state, secrets, guestPwsh,
                ["-NoLogo", "-NoProfile", "-File", guestScript, "-UserName", state.Options.UserName, "-LocalAppData", localAppData, "-SessionId", Guid.NewGuid().ToString("N"), "-Stop"],
                token, 15);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        dedup.Reset("", 0);
        try
        {
            if (activeStream is { } stream)
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                await ClipboardProtocol.WriteFrameAsync(stream, new(ClipboardFrameType.Stop, 0, generation, ""), stopCts.Token);
            }
        }
        catch { }
        try { cts.Cancel(); } catch { }
        try { listener?.Dispose(); } catch { }
        if (acceptLoop is not null)
        {
            try { await acceptLoop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (messageWindow is not null)
                {
                    try { RemoveClipboardFormatListener(messageWindow.Handle); } catch { }
                    messageWindow.RemoveHook(WndProc);
                    messageWindow.Dispose();
                    messageWindow = null;
                }
            });
        }
        catch { }
        Array.Clear(key, 0, key.Length);
        cts.Dispose();
        sendGate.Dispose();
    }
}
