// The generic packet frame every wire packet carries (design-netcode.md §3.1: "All packets
// carry (ProtocolVersion, SenderPlayerIndex)"). Reusable across future packet kinds beyond
// Orders (Checkpoint, DeepDumpChunk, etc. per the §3.1 inventory) - this type has no knowledge
// of what its payload means, only how to frame and unframe it.
//
// Layout: uint16 ProtocolVersion, byte SenderPlayerIndex, int32 payload length prefix, then
// that many payload bytes. The length prefix is signed (not unsigned) specifically so a
// corrupted or forged negative value is representable and rejected as
// WireDecodeStatus.LengthPrefixInvalid, distinct from "declared length exceeds what's actually
// in the buffer" (WireDecodeStatus.UnexpectedEndOfData).

using System;

namespace OpenSage.Network.Wire;

internal static class WireFrame
{
    public static byte[] Encode(ushort protocolVersion, byte senderPlayerIndex, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > WireLimits.MaxFramePayloadBytes)
        {
            throw new ArgumentException(
                $"Payload of {payload.Length} bytes exceeds the wire frame cap of {WireLimits.MaxFramePayloadBytes} bytes.",
                nameof(payload));
        }

        var writer = new WireWriter();
        writer.WriteUInt16(protocolVersion);
        writer.WriteByte(senderPlayerIndex);
        writer.WriteInt32(payload.Length);
        writer.WriteBytes(payload);
        return writer.ToArray();
    }

    /// <summary>
    /// Unframes <paramref name="data"/> into its header fields and the payload slice (a view
    /// into <paramref name="data"/>, not a copy). Never throws on malformed input; returns a
    /// <see cref="WireDecodeStatus"/> other than <see cref="WireDecodeStatus.Success"/> instead,
    /// in which case the out parameters are not meaningful.
    /// </summary>
    public static WireDecodeStatus TryDecode(
        ReadOnlySpan<byte> data,
        out ushort protocolVersion,
        out byte senderPlayerIndex,
        out ReadOnlySpan<byte> payload)
    {
        protocolVersion = 0;
        senderPlayerIndex = 0;
        payload = default;

        var reader = new WireReader(data);

        if (!reader.TryReadUInt16(out protocolVersion))
        {
            return WireDecodeStatus.UnexpectedEndOfData;
        }

        if (!reader.TryReadByte(out senderPlayerIndex))
        {
            return WireDecodeStatus.UnexpectedEndOfData;
        }

        if (!reader.TryReadInt32(out var length))
        {
            return WireDecodeStatus.UnexpectedEndOfData;
        }

        if (length < 0 || length > WireLimits.MaxFramePayloadBytes)
        {
            return WireDecodeStatus.LengthPrefixInvalid;
        }

        if (length > reader.Remaining)
        {
            return WireDecodeStatus.UnexpectedEndOfData;
        }

        payload = data.Slice(reader.Position, length);
        return WireDecodeStatus.Success;
    }
}
