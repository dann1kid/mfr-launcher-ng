using System.Buffers.Binary;

namespace Mfr.Protocol.Netty;

/// <summary>
/// Frame format of the TCP protocol: each message is prefixed with a 3-byte
/// big-endian unsigned length (Kotlin: LengthFieldPrepender(3) /
/// LengthFieldBasedFrameDecoder(1 MiB, 0, 3, 0, 3)). The length covers the
/// payload only; frames larger than 1 MiB are rejected.
/// </summary>
public static class FrameCodec
{
    public const int MaxFrameLength = 1024 * 1024;
    public const int LengthPrefixSize = 3;

    public static void WriteFrame(Stream stream, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxFrameLength)
            throw new InvalidOperationException($"Frame of {payload.Length} bytes exceeds the {MaxFrameLength}-byte limit.");

        Span<byte> prefix = stackalloc byte[LengthPrefixSize];
        prefix[0] = (byte)(payload.Length >> 16);
        prefix[1] = (byte)(payload.Length >> 8);
        prefix[2] = (byte)payload.Length;
        stream.Write(prefix);
        stream.Write(payload);
    }

    public static byte[] WrapFrame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxFrameLength)
            throw new InvalidOperationException($"Frame of {payload.Length} bytes exceeds the {MaxFrameLength}-byte limit.");

        var frame = new byte[LengthPrefixSize + payload.Length];
        frame[0] = (byte)(payload.Length >> 16);
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(LengthPrefixSize));
        return frame;
    }
}

/// <summary>
/// Accumulates bytes received from a socket and yields complete frames.
/// Usage: <c>reader.Append(buffer); while (reader.TryReadFrame(out var frame)) ...</c>
/// </summary>
public sealed class FrameReader
{
    private byte[] _buffer = new byte[64 * 1024];
    private int _length;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length)
        {
            var grown = new byte[Math.Max(_buffer.Length * 2, _length + data.Length)];
            _buffer.AsSpan(0, _length).CopyTo(grown);
            _buffer = grown;
        }
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    /// <summary>Returns the next complete frame payload, or null if more bytes are needed.</summary>
    public byte[]? TryReadFrame()
    {
        if (_length < FrameCodec.LengthPrefixSize)
            return null;

        var payloadLength = (_buffer[0] << 16) | (_buffer[1] << 8) | _buffer[2];
        if (payloadLength > FrameCodec.MaxFrameLength)
            throw new InvalidOperationException($"Peer sent a frame of {payloadLength} bytes, over the {FrameCodec.MaxFrameLength}-byte limit.");

        if (_length < FrameCodec.LengthPrefixSize + payloadLength)
            return null;

        var payload = _buffer.AsSpan(FrameCodec.LengthPrefixSize, payloadLength).ToArray();
        var remaining = _length - FrameCodec.LengthPrefixSize - payloadLength;
        _buffer.AsSpan(FrameCodec.LengthPrefixSize + payloadLength, remaining).CopyTo(_buffer.AsSpan(0, remaining));
        _length = remaining;
        return payload;
    }
}
