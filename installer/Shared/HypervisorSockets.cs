using System.Buffers.Binary;
using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Claudome.Transport;

public sealed class HyperVEndPoint(Guid vmId, Guid serviceId) : EndPoint
{
    public Guid VmId { get; } = vmId;
    public Guid ServiceId { get; } = serviceId;
    public override AddressFamily AddressFamily => (AddressFamily)34;
    public override SocketAddress Serialize()
    {
        var address = new SocketAddress(AddressFamily, 36); var vm = VmId.ToByteArray(); var service = ServiceId.ToByteArray();
        for (var i = 0; i < 16; i++) { address[4 + i] = vm[i]; address[20 + i] = service[i]; } return address;
    }
    public override EndPoint Create(SocketAddress address)
    {
        if (address.Size != 36 || address.Family != AddressFamily) throw new InvalidDataException("hyperv_address_invalid");
        var vm = new byte[16]; var service = new byte[16]; for (var i = 0; i < 16; i++) { vm[i] = address[4 + i]; service[i] = address[20 + i]; } return new HyperVEndPoint(new Guid(vm), new Guid(service));
    }
}
public sealed class VmciEndPoint(int family, uint contextId, uint port) : EndPoint
{
    public uint ContextId { get; } = contextId;
    public uint Port { get; } = port;
    public override AddressFamily AddressFamily => (AddressFamily)family;
    public override SocketAddress Serialize()
    {
        var address = new SocketAddress(AddressFamily, 16); var cid = BitConverter.GetBytes(ContextId); var number = BitConverter.GetBytes(Port);
        for (var i = 0; i < 4; i++) { address[4 + i] = number[i]; address[8 + i] = cid[i]; } return address;
    }
    public override EndPoint Create(SocketAddress address)
    {
        if (address.Size != 16 || address.Family != AddressFamily) throw new InvalidDataException("vmci_address_invalid");
        var port = new byte[4]; var cid = new byte[4]; for (var i = 0; i < 4; i++) { port[i] = address[4 + i]; cid[i] = address[8 + i]; } return new VmciEndPoint(family, BitConverter.ToUInt32(cid), BitConverter.ToUInt32(port));
    }
}

public enum HypervisorListenerStep { Create, Bind, Listen }

public sealed class HypervisorListenerException(HypervisorListenerStep step, SocketException cause)
    : IOException("hypervisor_listener_failed", cause)
{
    public HypervisorListenerStep Step { get; } = step;
    public SocketError SocketErrorCode { get; } = cause.SocketErrorCode;
    public int NativeErrorCode { get; } = cause.NativeErrorCode;
}

public static class HypervisorSockets
{
    // Interface constants/struct layouts from Microsoft SOCKADDR_HV and VMware
    // open-vm-tools vmci_sockets.h. No third-party DLL is fetched from DLL sites.
    internal static readonly Guid Children = new("90db8b89-0d35-4f79-8ce9-49ea0ac8b7cd");
    public static readonly Guid Parent = new("a42e7cda-d03f-480c-9cc2-a4de20abb878");
    [DllImport("Ws2_32.dll", EntryPoint = "getpeername", ExactSpelling = true)]
    private static extern int GetPeerName(SafeSocketHandle socket, [Out] byte[] address, ref int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref uint input, uint inputBytes, out uint output, uint outputBytes, out uint returned, IntPtr overlapped);
    public static int VmciFamily()
    {
        using var device = CreateFile(@"\\.\VMCI", 0x80000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero); uint input = uint.MaxValue;
        if (device.IsInvalid || !DeviceIoControl(device, 0x81032068, ref input, 4, out var family, 4, out var returned, IntPtr.Zero) || returned != 4 || family is < 1 or > 65535) throw new IOException("vmci_driver_unavailable");
        return (int)family;
    }
    internal static HyperVEndPoint HyperVHostListenEndpoint(Guid serviceId) => new(Children, serviceId);

    internal static bool IsExpectedHyperVPeer(Socket socket, Guid expectedVm)
    {
        // Query the connected peer from Winsock rather than matching against
        // the listener's children wildcard or an identity sent by the peer.
        var address = new byte[36];
        var length = address.Length;
        return GetPeerName(socket.SafeHandle, address, ref length) == 0 &&
            length == address.Length && IsExpectedHyperVPeer(address, expectedVm);
    }

    internal static bool IsExpectedHyperVPeer(ReadOnlySpan<byte> address, Guid expectedVm) =>
        expectedVm != Guid.Empty && address.Length == 36 &&
        BinaryPrimitives.ReadUInt16LittleEndian(address) == 34 &&
        new Guid(address.Slice(4, 16)) == expectedVm;

    public static Socket Listen(BridgeSpec spec)
    {
        spec.Validate();
        var hyperv = spec.Platform == "hyperv";
        var family = hyperv ? (AddressFamily)34 : (AddressFamily)VmciFamily();
        EndPoint endpoint = hyperv
            ? HyperVHostListenEndpoint(Guid.Parse(spec.ServiceId))
            : new VmciEndPoint((int)family, uint.MaxValue, spec.VmciPort);
        var protocol = hyperv ? (ProtocolType)1 : ProtocolType.Unspecified;
        var step = HypervisorListenerStep.Create;
        Socket? socket = null;
        try
        {
            socket = new(family, SocketType.Stream, protocol);
            step = HypervisorListenerStep.Bind;
            socket.Bind(endpoint);
            step = HypervisorListenerStep.Listen;
            socket.Listen(4);
            return socket;
        }
        catch (SocketException error)
        {
            socket?.Dispose();
            throw new HypervisorListenerException(step, error);
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
    }
    public static async Task<Socket> Connect(BridgeSpec spec, CancellationToken token)
    {
        spec.Validate(); Socket socket; EndPoint endpoint;
        if (spec.Platform == "hyperv") { socket = new((AddressFamily)34, SocketType.Stream, (ProtocolType)1); endpoint = new HyperVEndPoint(Parent, Guid.Parse(spec.ServiceId)); }
        else { var family = VmciFamily(); socket = new((AddressFamily)family, SocketType.Stream, ProtocolType.Unspecified); endpoint = new VmciEndPoint(family, 2, spec.VmciPort); }
        try
        {
            if (endpoint is HyperVEndPoint)
            {
                // AF_HYPERV rejects the ConnectEx path on some Windows builds.
                token.ThrowIfCancellationRequested();
                using var interrupt = token.UnsafeRegister(static state => ((Socket)state!).Dispose(), socket);
                socket.Connect(endpoint);
            }
            else
                await socket.ConnectAsync(endpoint, token);

            token.ThrowIfCancellationRequested();
            return socket;
        }
        catch
        {
            socket.Dispose();
            token.ThrowIfCancellationRequested();
            throw;
        }
    }
}
