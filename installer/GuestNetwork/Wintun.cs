using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Claudome.GuestNetwork;

internal sealed class Wintun : IDisposable
{
    private IntPtr adapter, session;
    private readonly EventWaitHandle ready;
    [DllImport("wintun.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr WintunCreateAdapter(string name, string type, ref Guid guid);
    [DllImport("wintun.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr WintunOpenAdapter(string name);
    [DllImport("wintun.dll")] private static extern void WintunGetAdapterLUID(IntPtr adapter, out ulong luid);
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceLuidToGuid(ref ulong luid, out Guid guid);
    [DllImport("wintun.dll")] private static extern void WintunCloseAdapter(IntPtr adapter);
    [DllImport("wintun.dll", SetLastError = true)] private static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);
    [DllImport("wintun.dll")] private static extern void WintunEndSession(IntPtr session);
    [DllImport("wintun.dll")] private static extern IntPtr WintunGetReadWaitEvent(IntPtr session);
    [DllImport("wintun.dll", SetLastError = true)] private static extern IntPtr WintunReceivePacket(IntPtr session, out uint size);
    [DllImport("wintun.dll")] private static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);
    [DllImport("wintun.dll", SetLastError = true)] private static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint size);
    [DllImport("wintun.dll")] private static extern void WintunSendPacket(IntPtr session, IntPtr packet);
    public static void LoadOfficialLibrary(string root)
    {
        using var data = Assembly.GetExecutingAssembly().GetManifestResourceStream("Claudome.Wintun.dll")!;
        using var memory = new MemoryStream(); data.CopyTo(memory); var bytes = memory.ToArray(); var file = Path.Combine(root, "wintun.dll");
        if (File.Exists(file)) {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(file)))) throw new InvalidDataException("wintun_hash_mismatch");
        } else File.WriteAllBytes(file, bytes);
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) => name == "wintun.dll" ? NativeLibrary.Load(file) : IntPtr.Zero);
    }
    public Wintun(Guid id)
    {
        adapter = WintunOpenAdapter("ClaudomeBridge"); if (adapter == IntPtr.Zero) adapter = WintunCreateAdapter("ClaudomeBridge", "Claudome", ref id);
        if (adapter == IntPtr.Zero) throw new IOException("wintun_adapter_failed");
        WintunGetAdapterLUID(adapter, out var luid);
        if (ConvertInterfaceLuidToGuid(ref luid, out var actual) != 0 || actual != id) { WintunCloseAdapter(adapter); adapter = IntPtr.Zero; throw new IOException("wintun_adapter_identity_mismatch"); }
        session = WintunStartSession(adapter, 0x400000); if (session == IntPtr.Zero) { WintunCloseAdapter(adapter); adapter = IntPtr.Zero; throw new IOException("wintun_session_failed"); }
        ready = new EventWaitHandle(false, EventResetMode.ManualReset); ready.SafeWaitHandle = new SafeWaitHandle(WintunGetReadWaitEvent(session), false);
    }
    public byte[] Receive(CancellationToken token)
    {
        while (true) {
            token.ThrowIfCancellationRequested(); var packet = WintunReceivePacket(session, out var size);
            if (packet == IntPtr.Zero) {
                if (Marshal.GetLastWin32Error() != 259) throw new IOException("wintun_receive_failed");
                if (WaitHandle.WaitAny([ready, token.WaitHandle]) != 0) token.ThrowIfCancellationRequested(); continue;
            }
            try { if (size is < 20 or > 1500) continue; var bytes = new byte[size]; Marshal.Copy(packet, bytes, 0, bytes.Length); return bytes; }
            finally { WintunReleaseReceivePacket(session, packet); }
        }
    }
    public void Send(byte[] packet)
    {
        var destination = WintunAllocateSendPacket(session, (uint)packet.Length);
        if (destination == IntPtr.Zero) { if (Marshal.GetLastWin32Error() == 111) return; throw new IOException("wintun_send_failed"); }
        Marshal.Copy(packet, 0, destination, packet.Length); WintunSendPacket(session, destination);
    }
    public void Dispose()
    {
        ready.Dispose(); if (session != IntPtr.Zero) { WintunEndSession(session); session = IntPtr.Zero; }
        if (adapter != IntPtr.Zero) { WintunCloseAdapter(adapter); adapter = IntPtr.Zero; }
    }
}
