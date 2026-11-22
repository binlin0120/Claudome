using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Claudome.Transport;

public sealed record BridgeSpec(string Platform, string VmId, string WorkspaceId, string ServiceId, uint VmciPort, string GuestMac, string Key)
{
    public void Validate()
    {
        if (Platform is not ("hyperv" or "workstation") || !Guid.TryParseExact(VmId, "D", out _) || !Guid.TryParseExact(WorkspaceId, "N", out _) ||
            !Guid.TryParseExact(ServiceId, "D", out _) || VmciPort < 1024 || VmciPort == uint.MaxValue || Convert.FromBase64String(Key).Length != 32 ||
            !System.Text.RegularExpressions.Regex.IsMatch(GuestMac, "^02(?::[0-9a-fA-F]{2}){5}$")) throw new InvalidDataException("invalid_bridge_identity");
    }
}

// Fixed length, mutually authenticated, fresh challenge per connection. This is
// a packet transport only: no command execution, host path or resolver messages.
public static class BridgeProtocol
{
    public const int MaximumPacket = 1500;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CLDNET01");
    private static byte[] Proof(BridgeSpec spec, ReadOnlySpan<byte> server, ReadOnlySpan<byte> client, string role)
    {
        var context = Encoding.ASCII.GetBytes(role + ":" + spec.Platform + ":" + spec.VmId + ":" + spec.WorkspaceId + ":" + spec.ServiceId + ":" + spec.VmciPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + spec.GuestMac);
        var input = new byte[context.Length + server.Length + client.Length]; context.CopyTo(input, 0); server.CopyTo(input.AsSpan(context.Length)); client.CopyTo(input.AsSpan(context.Length + server.Length));
        var key = Convert.FromBase64String(spec.Key);
        try { return HMACSHA256.HashData(key, input); } finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(input); }
    }
    public static async Task AuthenticateServer(Stream stream, BridgeSpec spec, CancellationToken token)
    {
        spec.Validate(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        var challenge = RandomNumberGenerator.GetBytes(32); await stream.WriteAsync(Magic, ct); await stream.WriteAsync(challenge, ct); await stream.FlushAsync(ct);
        var response = new byte[64]; await stream.ReadExactlyAsync(response, ct);
        if (!CryptographicOperations.FixedTimeEquals(response.AsSpan(32), Proof(spec, challenge, response.AsSpan(0, 32), "client"))) throw new InvalidDataException("bridge_authentication_failed");
        await stream.WriteAsync(Proof(spec, challenge, response.AsSpan(0, 32), "server"), ct); await stream.FlushAsync(ct);
    }
    public static async Task AuthenticateClient(Stream stream, BridgeSpec spec, CancellationToken token)
    {
        spec.Validate(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        var hello = new byte[40]; await stream.ReadExactlyAsync(hello, ct); if (!hello.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("bridge_protocol_mismatch");
        var nonce = RandomNumberGenerator.GetBytes(32); await stream.WriteAsync(nonce, ct); await stream.WriteAsync(Proof(spec, hello.AsSpan(8), nonce, "client"), ct); await stream.FlushAsync(ct);
        var proof = new byte[32]; await stream.ReadExactlyAsync(proof, ct);
        if (!CryptographicOperations.FixedTimeEquals(proof, Proof(spec, hello.AsSpan(8), nonce, "server"))) throw new InvalidDataException("bridge_authentication_failed");
    }
    public static async ValueTask<byte[]> ReadPacket(Stream stream, CancellationToken token)
    {
        var size = new byte[2]; await stream.ReadExactlyAsync(size, token); var length = BinaryPrimitives.ReadUInt16BigEndian(size);
        if (length is < 20 or > MaximumPacket) throw new InvalidDataException("bridge_packet_size");
        var packet = new byte[length]; await stream.ReadExactlyAsync(packet, token);
        if (!ValidIPv4(packet)) throw new InvalidDataException("bridge_packet_invalid"); return packet;
    }
    public static async ValueTask WritePacket(Stream stream, ReadOnlyMemory<byte> packet, CancellationToken token)
    {
        if (!ValidIPv4(packet.Span)) throw new InvalidDataException("bridge_packet_invalid");
        var size = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)packet.Length);
        await stream.WriteAsync(size, token); await stream.WriteAsync(packet, token);
    }
    public static bool ValidIPv4(ReadOnlySpan<byte> packet) => packet.Length is >= 20 and <= MaximumPacket && packet[0] == 0x45 &&
        BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]) == packet.Length && (BinaryPrimitives.ReadUInt16BigEndian(packet[6..8]) & 0x3fff) == 0;
    public static byte[] Ethernet(ReadOnlySpan<byte> packet, string mac)
    {
        if (!ValidIPv4(packet) || !packet.Slice(12, 4).SequenceEqual(new byte[] { 198, 19, 250, 10 })) throw new InvalidDataException("bridge_source_invalid");
        var frame = new byte[14 + packet.Length]; new byte[] { 2, 0x43, 0x45, 0, 0, 1 }.CopyTo(frame, 0); Convert.FromHexString(mac.Replace(":", "")).CopyTo(frame, 6); frame[12] = 8; packet.CopyTo(frame.AsSpan(14)); return frame;
    }
    public static byte[] IPv4FromEthernet(ReadOnlySpan<byte> frame, string mac)
    {
        if (frame.Length < 34 || frame[12] != 8 || frame[13] != 0 || !frame[..6].SequenceEqual(Convert.FromHexString(mac.Replace(":", "")))) throw new InvalidDataException("bridge_frame_invalid");
        var size = BinaryPrimitives.ReadUInt16BigEndian(frame[16..18]);
        if (size < 20 || size > MaximumPacket || frame.Length < 14 + size || !ValidIPv4(frame.Slice(14, size)) ||
            !frame.Slice(6,6).SequenceEqual(new byte[] { 2, 0x43, 0x45, 0, 0, 1 }) || !frame.Slice(30,4).SequenceEqual(new byte[] { 198, 19, 250, 10 })) throw new InvalidDataException("bridge_frame_invalid");
        return frame.Slice(14, size).ToArray();
    }
}
