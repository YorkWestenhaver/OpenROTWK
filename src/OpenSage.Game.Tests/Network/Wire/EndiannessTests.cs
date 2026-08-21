using OpenSage.Network.Wire;
using OpenSage.SimCore.Orders;
using Xunit;

namespace OpenSage.Network.Wire.Tests;

/// <summary>
/// Asserts the encoded byte sequence for known values matches a hand-computed
/// little-endian layout exactly. <see cref="System.Buffers.Binary.BinaryPrimitives"/>'s
/// *LittleEndian writers are little-endian by contract regardless of host architecture (both
/// of OpenROTWK's cross-arch targets, arm64 and x64, are themselves little-endian anyway - see
/// design-netcode.md §5.2's endianness hazard row), so this is a meaningful assertion on any
/// host: it pins the wire's byte layout, not the host's.
/// </summary>
public class EndiannessTests
{
    [Fact]
    public void WriteUInt16_IsLittleEndian()
    {
        var writer = new WireWriter();
        writer.WriteUInt16(0xABCD);
        Assert.Equal(new byte[] { 0xCD, 0xAB }, writer.ToArray());
    }

    [Fact]
    public void WriteInt32_IsLittleEndian()
    {
        var writer = new WireWriter();
        writer.WriteInt32(0x01020304);
        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, writer.ToArray());
    }

    [Fact]
    public void WriteInt32_NegativeValue_IsLittleEndianTwosComplement()
    {
        var writer = new WireWriter();
        writer.WriteInt32(-1);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, writer.ToArray());
    }

    [Fact]
    public void WriteUInt32_IsLittleEndian()
    {
        var writer = new WireWriter();
        writer.WriteUInt32(0xAABBCCDDu);
        Assert.Equal(new byte[] { 0xDD, 0xCC, 0xBB, 0xAA }, writer.ToArray());
    }

    [Fact]
    public void IntegerArg_EncodedBytes_AreKindByteThenLittleEndianPayload()
    {
        var writer = new WireWriter();
        SimOrderArgCodec.Encode(writer, SimOrderArg.FromInteger(0x01020304));

        Assert.Equal(
            new byte[] { (byte)SimOrderArgKind.Integer, 0x04, 0x03, 0x02, 0x01 },
            writer.ToArray());
    }

    [Fact]
    public void ObjectIdArg_EncodedBytes_AreKindByteThenLittleEndianPayload()
    {
        var writer = new WireWriter();
        SimOrderArgCodec.Encode(writer, SimOrderArg.FromObjectId(0xAABBCCDDu));

        Assert.Equal(
            new byte[] { (byte)SimOrderArgKind.ObjectId, 0xDD, 0xCC, 0xBB, 0xAA },
            writer.ToArray());
    }

    [Fact]
    public void WireFrameHeader_EncodedBytes_AreLittleEndian()
    {
        var bytes = WireFrame.Encode(protocolVersion: 0xBEEF, senderPlayerIndex: 0x42, new byte[] { 1, 2 });

        Assert.Equal(0xEF, bytes[0]); // ProtocolVersion low byte first
        Assert.Equal(0xBE, bytes[1]); // ProtocolVersion high byte second
        Assert.Equal(0x42, bytes[2]); // SenderPlayerIndex
        Assert.Equal(2, bytes[3]);    // length prefix low byte (2, LE)
        Assert.Equal(0, bytes[4]);
        Assert.Equal(0, bytes[5]);
        Assert.Equal(0, bytes[6]);
        Assert.Equal(1, bytes[7]);    // payload
        Assert.Equal(2, bytes[8]);
    }
}
