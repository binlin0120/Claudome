using System.IO;
using System.Net;
using System.Net.Sockets;
using Claudome.Transport;

namespace ClaudeSetup;

public sealed class BusPacketRelay : IAsyncDisposable
{
    private readonly Socket listener;
    private readonly UdpClient packets;
    private readonly BridgeSpec spec;
    private readonly GatewayBinding binding;
    private readonly Guid? expectedHyperVPeer;
    private readonly CancellationTokenSource stopped = new();
    private readonly Task loop;
    private int connected;
    public bool IsConnected => Volatile.Read(ref connected) == 1 && !stopped.IsCancellationRequested;
    public bool IsAlive => !stopped.IsCancellationRequested && !loop.IsCompleted;
    public BusPacketRelay(BridgeSpec spec, GatewayBinding binding) : this(spec,binding,null) { }
    internal BusPacketRelay(BridgeSpec spec, GatewayBinding binding, Socket? fixtureListener)
    {
        spec.Validate(); this.spec = spec; this.binding = binding;
        expectedHyperVPeer = fixtureListener is null && spec.Platform == "hyperv" ? Guid.Parse(spec.VmId) : null;
        try { packets = new UdpClient(new IPEndPoint(IPAddress.Loopback, binding.GuestPort)); }
        catch (SocketException error) { throw Redaction.SocketStartupError("绑定宿主回环 UDP", error); }
        try { listener = fixtureListener ?? HypervisorSockets.Listen(spec); }
        catch (HypervisorListenerException error)
        {
            packets.Dispose();
            var provider = spec.Platform == "hyperv" ? "Hyper-V" : "VMware";
            var step = error.Step switch
            {
                HypervisorListenerStep.Create => "创建",
                HypervisorListenerStep.Bind => "绑定",
                _ => "监听"
            };
            throw Redaction.SocketStartupError($"{provider} 总线套接字·{step}", error.SocketErrorCode, error.NativeErrorCode);
        }
        catch { packets.Dispose(); throw; }
        loop = Task.Run(Accept);
    }
    private async Task Accept()
    {
        try {
            while (!stopped.IsCancellationRequested) {
                using var socket = await listener.AcceptAsync(stopped.Token);
                if (expectedHyperVPeer is { } vmId && !HypervisorSockets.IsExpectedHyperVPeer(socket, vmId)) continue;
                using var stream = new NetworkStream(socket, false); using var connection = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                try {
                    await BridgeProtocol.AuthenticateServer(stream, spec, connection.Token); Volatile.Write(ref connected, 1);
                    var outbound = Task.Run(async () => {
                        while (!connection.IsCancellationRequested) {
                            var packet = await BridgeProtocol.ReadPacket(stream, connection.Token); var frame = BridgeProtocol.Ethernet(packet, spec.GuestMac);
                            await packets.SendAsync(frame, new IPEndPoint(IPAddress.Loopback, binding.ListenPort), connection.Token);
                        }
                    }, connection.Token);
                    var inbound = Task.Run(async () => {
                        while (!connection.IsCancellationRequested) {
                            var frame = await packets.ReceiveAsync(connection.Token);
                            if (!frame.RemoteEndPoint.Address.Equals(IPAddress.Loopback) || frame.RemoteEndPoint.Port != binding.ListenPort) continue;
                            byte[] packet; try { packet = BridgeProtocol.IPv4FromEthernet(frame.Buffer, spec.GuestMac); } catch (InvalidDataException) { continue; }
                            await BridgeProtocol.WritePacket(stream, packet, connection.Token);
                        }
                    }, connection.Token);
                    await Task.WhenAny(outbound, inbound); connection.Cancel(); socket.Dispose();
                    try { await Task.WhenAll(outbound, inbound); } catch { }
                } catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException) { }
                finally { Volatile.Write(ref connected, 0); }
            }
        } catch { stopped.Cancel(); }
    }
    public async ValueTask DisposeAsync()
    {
        stopped.Cancel(); listener.Dispose(); packets.Dispose(); try { await loop.WaitAsync(TimeSpan.FromSeconds(6)); } catch { } stopped.Dispose();
    }
}
