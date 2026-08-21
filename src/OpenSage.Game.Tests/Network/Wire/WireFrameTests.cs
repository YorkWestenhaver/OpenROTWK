using System;
using OpenSage.Network.Wire;
using Xunit;

namespace OpenSage.Network.Wire.Tests;

public class WireFrameTests
{
    [Fact]
    public void HeaderAndPayload_RoundTrip()
    {
        byte[] payload = { 1, 2, 3, 4, 5 };
        var bytes = WireFrame.Encode(protocolVersion: 7, senderPlayerIndex: 3, payload);

        var status = WireFrame.TryDecode(bytes, out var protocolVersion, out var senderPlayerIndex, out var decodedPayload);

        Assert.Equal(WireDecodeStatus.Success, status);
        Assert.Equal((ushort)7, protocolVersion);
        Assert.Equal((byte)3, senderPlayerIndex);
        Assert.Equal(payload, decodedPayload.ToArray());
    }

    [Fact]
    public void EmptyPayload_RoundTrips_NotAsNothing()
    {
        var bytes = WireFrame.Encode(protocolVersion: 1, senderPlayerIndex: 0, ReadOnlySpan<byte>.Empty);

        // The frame header alone (2 + 1 + 4 bytes) is still real, decodable bytes - an empty
        // payload is not the same thing as no data at all.
        Assert.Equal(7, bytes.Length);

        var status = WireFrame.TryDecode(bytes, out _, out _, out var payload);
        Assert.Equal(WireDecodeStatus.Success, status);
        Assert.Equal(0, payload.Length);
    }

    [Fact]
    public void TryDecode_EmptyBuffer_ReturnsUnexpectedEndOfData_NeverThrows()
    {
        var status = WireFrame.TryDecode(Array.Empty<byte>(), out _, out _, out _);
        Assert.Equal(WireDecodeStatus.UnexpectedEndOfData, status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void TryDecode_TruncatedHeader_ReturnsUnexpectedEndOfData_NeverThrows(int length)
    {
        var full = WireFrame.Encode(1, 0, new byte[] { 9, 9, 9 });
        var truncated = full.AsSpan(0, length).ToArray();

        var status = WireFrame.TryDecode(truncated, out _, out _, out _);
        Assert.Equal(WireDecodeStatus.UnexpectedEndOfData, status);
    }

    [Fact]
    public void TryDecode_NegativeLengthPrefix_ReturnsLengthPrefixInvalid_NeverThrows()
    {
        var writer = new WireWriter();
        writer.WriteUInt16(1);
        writer.WriteByte(0);
        writer.WriteInt32(-1); // a forged negative length

        var status = WireFrame.TryDecode(writer.WrittenSpan.ToArray(), out _, out _, out _);
        Assert.Equal(WireDecodeStatus.LengthPrefixInvalid, status);
    }

    [Fact]
    public void TryDecode_OversizedLengthPrefix_ReturnsLengthPrefixInvalid_NeverThrows()
    {
        var writer = new WireWriter();
        writer.WriteUInt16(1);
        writer.WriteByte(0);
        writer.WriteInt32(int.MaxValue); // far beyond WireLimits.MaxFramePayloadBytes

        var status = WireFrame.TryDecode(writer.WrittenSpan.ToArray(), out _, out _, out _);
        Assert.Equal(WireDecodeStatus.LengthPrefixInvalid, status);
    }

    [Fact]
    public void TryDecode_LengthPrefixExceedsActualBuffer_ReturnsUnexpectedEndOfData_NeverThrows()
    {
        var writer = new WireWriter();
        writer.WriteUInt16(1);
        writer.WriteByte(0);
        writer.WriteInt32(100); // valid, in-cap length, but the buffer holds none of it

        var status = WireFrame.TryDecode(writer.WrittenSpan.ToArray(), out _, out _, out _);
        Assert.Equal(WireDecodeStatus.UnexpectedEndOfData, status);
    }

    [Fact]
    public void Encode_PayloadOverCap_Throws()
    {
        var oversized = new byte[WireLimits_MaxFramePayloadBytesPlusOne()];
        Assert.Throws<ArgumentException>(() => WireFrame.Encode(1, 0, oversized));
    }

    // WireLimits is internal; this constant is duplicated here deliberately (rather than
    // reaching into WireLimits) so the test fails loudly if the two ever drift instead of
    // silently tracking whatever WireLimits says.
    private static int WireLimits_MaxFramePayloadBytesPlusOne() => 4 * 1024 * 1024 + 1;
}
