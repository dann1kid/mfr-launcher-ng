using Mfr.Protocol.Netty;
using Xunit;

namespace Mfr.Protocol.Tests;

public sealed class FrameCodecTests
{
    [Fact]
    public void Frame_HasThreeByteBigEndianLengthPrefix()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var frame = FrameCodec.WrapFrame(payload);

        Assert.Equal(3 + 5, frame.Length);
        Assert.Equal(0, frame[0]); // 5 = 0x000005
        Assert.Equal(0, frame[1]);
        Assert.Equal(5, frame[2]);
        Assert.Equal(payload, frame[3..]);
    }

    [Fact]
    public void LargePayload_UsesHighByte()
    {
        var payload = new byte[70_000]; // 0x011170 — needs the first prefix byte
        var frame = FrameCodec.WrapFrame(payload);

        Assert.Equal(0x01, frame[0]);
        Assert.Equal(70_000, (frame[0] << 16) | (frame[1] << 8) | frame[2]);
    }

    [Fact]
    public void OversizedFrame_IsRejected()
    {
        var payload = new byte[FrameCodec.MaxFrameLength + 1];
        Assert.Throws<InvalidOperationException>(() => FrameCodec.WrapFrame(payload));
    }

    [Fact]
    public void Reader_YieldsFramedByParts()
    {
        var reader = new FrameReader();
        var first = FrameCodec.WrapFrame(new byte[] { 1, 2, 3 });
        var second = FrameCodec.WrapFrame(new byte[] { 4, 5 });

        reader.Append(first.AsSpan(0, 2)); // incomplete prefix+payload
        Assert.Null(reader.TryReadFrame());

        reader.Append(first.AsSpan(2)); // first frame is now complete
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.TryReadFrame());
        Assert.Null(reader.TryReadFrame());

        reader.Append(second); // second frame arrives whole
        Assert.Equal(new byte[] { 4, 5 }, reader.TryReadFrame());
        Assert.Null(reader.TryReadFrame());
    }

    [Fact]
    public void Reader_RejectsOversizedAnnouncedFrame()
    {
        var reader = new FrameReader();
        reader.Append(new byte[] { 0xFF, 0xFF, 0xFF }); // announces ~16 MiB

        Assert.Throws<InvalidOperationException>(() => reader.TryReadFrame());
    }

    [Fact]
    public void FullProtocolStack_MessageInFrameRoundTrip()
    {
        var reader = new FrameReader();
        var message = new UploadFileMessage(68541, 65_536, new byte[] { 9, 9, 9 }, true);
        reader.Append(FrameCodec.WrapFrame(MessageCodec.Encode(message)));

        var payload = reader.TryReadFrame();
        Assert.NotNull(payload);
        var decoded = (UploadFileMessage)MessageCodec.Decode(payload!);
        Assert.Equal(message.FileId, decoded.FileId);
        Assert.Equal(message.Position, decoded.Position);
        Assert.Equal(message.Data, decoded.Data);
        Assert.Equal(message.LastFrame, decoded.LastFrame);
    }
}
