using System.Text;
using System.Text.Json;
using Mfr.Protocol.Enums;
using Mfr.Protocol.Netty;
using Xunit;

namespace Mfr.Protocol.Tests;

public sealed class MessageCodecTests
{
    private static readonly Guid SampleGuid = new("0eb4916f-3d2c-4a1b-9f0e-8d7c6b5a4321");

    private static string EncodeText(Message message) =>
        Encoding.UTF8.GetString(MessageCodec.Encode(message));

    [Fact]
    public void RequestGameFiles_EncodesWithDuplicateId()
    {
        var json = EncodeText(new RequestGameFilesMessage(SampleGuid, [45, 46, 47]));

        Assert.Equal(@"{""id"":""1"",""id"":""0eb4916f-3d2c-4a1b-9f0e-8d7c6b5a4321"",""f"":[45,46,47]}", json);
    }

    [Fact]
    public void RequestLauncherFiles_EncodesWithDuplicateId()
    {
        var json = EncodeText(new RequestLauncherFilesMessage(SampleGuid, SystemType.WINDOWS));

        Assert.Equal(@"{""id"":""5"",""id"":""0eb4916f-3d2c-4a1b-9f0e-8d7c6b5a4321"",""type"":""WINDOWS""}", json);
    }

    [Fact]
    public void RequestChangeState_Encodes()
    {
        Assert.Equal(@"{""id"":""7"",""active"":true}", EncodeText(new RequestChangeState(true)));
        Assert.Equal(@"{""id"":""7"",""active"":false}", EncodeText(new RequestChangeState(false)));
    }

    [Fact]
    public void UploadFile_OmitsDefaultFields()
    {
        // position 0, empty data, lastFrame false → all omitted, as Jackson NON_DEFAULT does
        Assert.Equal(@"{""id"":""2"",""id"":45}", EncodeText(new UploadFileMessage(45, 0, [], false)));

        Assert.Equal(
            @"{""id"":""2"",""id"":45,""p"":1048576,""d"":""AQID"",""l"":1}",
            EncodeText(new UploadFileMessage(45, 1_048_576, [1, 2, 3], true)));
    }

    [Fact]
    public void ServerException_OmitsNulls()
    {
        Assert.Equal(@"{""id"":""4""}", EncodeText(new ServerExceptionMessage()));

        var json = EncodeText(new ServerExceptionMessage("disk full", SampleGuid));
        Assert.Equal(@"{""id"":""4"",""text"":""disk full"",""uuid"":""0eb4916f-3d2c-4a1b-9f0e-8d7c6b5a4321""}", json);
    }

    [Fact]
    public void EmptyMessages_EncodeBareDiscriminator()
    {
        Assert.Equal(@"{""id"":""3""}", EncodeText(new EndSessionMessage()));
        Assert.Equal(@"{""id"":""6""}", EncodeText(new ServerMaintenanceMessage()));
    }

    [Fact]
    public void UploadFile_DecodesDuplicateIdAndNumberBoolean()
    {
        var message = (UploadFileMessage)MessageCodec.Decode(
            Encoding.UTF8.GetBytes(@"{""id"":""2"",""id"":45,""p"":10,""d"":""AQID"",""l"":1}"));

        Assert.Equal(45, message.FileId);
        Assert.Equal(10, message.Position);
        Assert.Equal(new byte[] { 1, 2, 3 }, message.Data);
        Assert.True(message.LastFrame);
    }

    [Fact]
    public void UploadFile_DecodesWithOmittedDefaults()
    {
        var message = (UploadFileMessage)MessageCodec.Decode(
            Encoding.UTF8.GetBytes(@"{""id"":""2"",""id"":7,""d"":""AQID""}"));

        Assert.Equal(7, message.FileId);
        Assert.Equal(0, message.Position);
        Assert.Equal(new byte[] { 1, 2, 3 }, message.Data);
        Assert.False(message.LastFrame);
    }

    [Fact]
    public void ServerException_DecodesWithNulls()
    {
        var message = (ServerExceptionMessage)MessageCodec.Decode(Encoding.UTF8.GetBytes(@"{""id"":""4""}"));
        Assert.Null(message.Text);
        Assert.Null(message.Uuid);
    }

    [Fact]
    public void RequestGameFiles_RoundTrips()
    {
        var original = new RequestGameFilesMessage(SampleGuid, [1, 2, 3, 68541]);
        var decoded = (RequestGameFilesMessage)MessageCodec.Decode(MessageCodec.Encode(original));

        Assert.Equal(original.ClientId, decoded.ClientId);
        Assert.Equal(original.Files, decoded.Files);
    }

    [Fact]
    public void RequestLauncherFiles_RoundTrips()
    {
        var original = new RequestLauncherFilesMessage(SampleGuid, SystemType.WINDOWS);
        var decoded = (RequestLauncherFilesMessage)MessageCodec.Decode(MessageCodec.Encode(original));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void UnknownDiscriminator_Throws()
    {
        Assert.ThrowsAny<JsonException>(() =>
            MessageCodec.Decode(Encoding.UTF8.GetBytes(@"{""id"":""9""}")));
    }

    [Fact]
    public void MissingLeadingDiscriminator_Throws()
    {
        Assert.ThrowsAny<JsonException>(() =>
            MessageCodec.Decode(Encoding.UTF8.GetBytes(@"{""f"":[1],""id"":""1""}")));
    }
}
