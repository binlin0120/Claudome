using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeSetup;

public enum ClipboardFrameType : byte
{
    TextUpdate = 1,
    Heartbeat = 2,
    Stop = 3
}

public readonly record struct ClipboardDirectMessage(ClipboardFrameType Type, long Sequence, long Generation, string Text);

public static class ClipboardProtocol
{
    public const int MaxTextLength = 16_384;
    public const int MaxPayloadBytes = 65_536;
    private static readonly byte[] Magic = "CLDCLP01"u8.ToArray();

    public static Guid DeriveServiceId(Guid busServiceId)
    {
        var input = new byte[16 + 19];
        busServiceId.ToByteArray().CopyTo(input, 0);
        Encoding.ASCII.GetBytes("ClaudomeClipboardV1").CopyTo(input, 16);
        var hash = SHA256.HashData(input);
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    public static bool TryValidatePlainText(string? text, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Length > MaxTextLength) return false;
        if (text.Contains('\0')) return false;
        normalized = text;
        return true;
    }

    public static bool ShouldForwardClipboardSnapshot(bool hasFileDropList, bool hasUnicodeText, string? unicodeText, out string plainText)
    {
        plainText = "";
        if (hasFileDropList) return false;
        if (!hasUnicodeText) return false;
        return TryValidatePlainText(unicodeText, out plainText);
    }

    public static string ComputeTextHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static async Task AuthenticateAsync(Stream stream, byte[] key, bool server, CancellationToken token)
    {
        var localNonce = RandomNumberGenerator.GetBytes(16);
        var sendRole = (server ? "clip-server"u8 : "clip-client"u8).ToArray();
        var receiveRole = (server ? "clip-client"u8 : "clip-server"u8).ToArray();
        if (!server)
        {
            var hello = new byte[Magic.Length + 16];
            Magic.CopyTo(hello, 0);
            localNonce.CopyTo(hello, Magic.Length);
            await stream.WriteAsync(hello, token);
            await stream.FlushAsync(token);

            var reply = new byte[48];
            await ReadExactAsync(stream, reply, token);
            var remoteNonce = reply[..16];
            if (!CryptographicOperations.FixedTimeEquals(reply[16..], Mac(key, receiveRole, localNonce, remoteNonce)))
                throw new InvalidDataException("CLIPBOARD_AUTH_FAILED");

            await stream.WriteAsync(Mac(key, sendRole, remoteNonce, localNonce), token);
            await stream.FlushAsync(token);
            return;
        }

        var clientHello = new byte[Magic.Length + 16];
        await ReadExactAsync(stream, clientHello, token);
        if (!clientHello.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("CLIPBOARD_PROTOCOL_MISMATCH");
        var clientNonce = clientHello[Magic.Length..];

        var serverHello = new byte[48];
        localNonce.CopyTo(serverHello, 0);
        Mac(key, sendRole, clientNonce, localNonce).CopyTo(serverHello, 16);
        await stream.WriteAsync(serverHello, token);
        await stream.FlushAsync(token);

        var proof = new byte[32];
        await ReadExactAsync(stream, proof, token);
        if (!CryptographicOperations.FixedTimeEquals(proof, Mac(key, receiveRole, localNonce, clientNonce)))
            throw new InvalidDataException("CLIPBOARD_AUTH_FAILED");
    }

    public static async Task WriteFrameAsync(Stream stream, ClipboardDirectMessage message, CancellationToken token)
    {
        byte[] textBytes = message.Type == ClipboardFrameType.TextUpdate
            ? Encoding.UTF8.GetBytes(TryValidatePlainText(message.Text, out var valid) ? valid : throw new InvalidDataException("CLIPBOARD_TEXT_INVALID"))
            : [];
        var payloadLength = message.Type == ClipboardFrameType.TextUpdate ? 16 + textBytes.Length : 0;
        if (payloadLength > MaxPayloadBytes) throw new InvalidDataException("CLIPBOARD_TEXT_TOO_LARGE");

        var frame = new byte[5 + payloadLength];
        frame[0] = (byte)message.Type;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payloadLength);
        if (message.Type == ClipboardFrameType.TextUpdate)
        {
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(5, 8), message.Sequence);
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(13, 8), message.Generation);
            textBytes.CopyTo(frame, 21);
        }
        await stream.WriteAsync(frame, token);
        await stream.FlushAsync(token);
    }

    public static async Task<ClipboardDirectMessage?> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[5];
        var read = 0;
        while (read < header.Length)
        {
            var n = await stream.ReadAsync(header.AsMemory(read, header.Length - read), token);
            if (n == 0) return null;
            read += n;
        }
        var type = (ClipboardFrameType)header[0];
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
        if (length < 0 || length > MaxPayloadBytes)
            throw new InvalidDataException("CLIPBOARD_FRAME_INVALID");
        if (type is ClipboardFrameType.Heartbeat or ClipboardFrameType.Stop)
        {
            if (length > 0)
            {
                var skip = new byte[length];
                await ReadExactAsync(stream, skip, token);
            }
            return new(type, 0, 0, "");
        }
        if (type != ClipboardFrameType.TextUpdate || length < 16)
            throw new InvalidDataException("CLIPBOARD_FRAME_INVALID");
        var payload = new byte[length];
        await ReadExactAsync(stream, payload, token);
        var seq = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(0, 8));
        var gen = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(8, 8));
        var text = Encoding.UTF8.GetString(payload, 16, length - 16);
        if (!TryValidatePlainText(text, out var valid))
            throw new InvalidDataException("CLIPBOARD_TEXT_INVALID");
        return new(type, seq, gen, valid);
    }

    private static byte[] Mac(byte[] key, ReadOnlySpan<byte> role, byte[] first, byte[] second)
    {
        var data = new byte[role.Length + first.Length + second.Length];
        role.CopyTo(data);
        first.CopyTo(data, role.Length);
        second.CopyTo(data, role.Length + first.Length);
        return HMACSHA256.HashData(key, data);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
            if (n == 0) throw new EndOfStreamException("CLIPBOARD_EOF");
            offset += n;
        }
    }
}

public sealed class ClipboardSyncDeduplicator
{
    private readonly object gate = new();
    private string activeTaskId = "";
    private long activeGeneration;
    private uint lastObservedLocalSequence;
    private uint lastAppliedLocalSequence;
    private string lastAppliedRemoteHash = "";
    private DateTime lastAppliedRemoteUtc = DateTime.MinValue;
    private string lastSentLocalHash = "";

    public void Reset(string taskId, long generation, uint currentLocalSequence = 0)
    {
        lock (gate)
        {
            activeTaskId = taskId ?? "";
            activeGeneration = generation;
            lastObservedLocalSequence = currentLocalSequence;
            lastAppliedLocalSequence = 0;
            lastAppliedRemoteHash = "";
            lastAppliedRemoteUtc = DateTime.MinValue;
            lastSentLocalHash = "";
        }
    }

    public bool ShouldSendLocalChange(string taskId, long generation, uint localSequence, string text, DateTime nowUtc)
    {
        if (!ClipboardProtocol.TryValidatePlainText(text, out var valid)) return false;
        var hash = ClipboardProtocol.ComputeTextHash(valid);
        lock (gate)
        {
            if (taskId != activeTaskId || generation != activeGeneration || string.IsNullOrEmpty(activeTaskId))
                return false;
            if (localSequence != 0 && localSequence == lastObservedLocalSequence)
                return false;
            lastObservedLocalSequence = localSequence;
            if (localSequence != 0 && localSequence == lastAppliedLocalSequence)
                return false;
            if (!string.IsNullOrEmpty(lastAppliedRemoteHash) &&
                hash == lastAppliedRemoteHash &&
                (nowUtc - lastAppliedRemoteUtc) <= TimeSpan.FromSeconds(2))
                return false;
            lastSentLocalHash = hash;
            return true;
        }
    }

    public bool TryBeginApplyRemote(string taskId, long generation, string text, DateTime nowUtc, out string hash)
    {
        hash = "";
        if (!ClipboardProtocol.TryValidatePlainText(text, out var valid)) return false;
        hash = ClipboardProtocol.ComputeTextHash(valid);
        lock (gate)
        {
            if (taskId != activeTaskId || generation != activeGeneration || string.IsNullOrEmpty(activeTaskId))
                return false;
            if (hash == lastSentLocalHash && (nowUtc - lastAppliedRemoteUtc) <= TimeSpan.FromMilliseconds(200))
                return false;
            lastAppliedRemoteHash = hash;
            lastAppliedRemoteUtc = nowUtc;
            return true;
        }
    }

    public void CompleteApplyRemote(string taskId, long generation, uint resultingLocalSequence, string hash, DateTime nowUtc)
    {
        lock (gate)
        {
            if (taskId != activeTaskId || generation != activeGeneration) return;
            lastAppliedLocalSequence = resultingLocalSequence;
            lastObservedLocalSequence = resultingLocalSequence;
            lastAppliedRemoteHash = hash;
            lastAppliedRemoteUtc = nowUtc;
        }
    }
}
