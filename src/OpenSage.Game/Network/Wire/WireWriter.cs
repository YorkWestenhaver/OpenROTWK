using System;
using System.Buffers;
using System.Buffers.Binary;

namespace OpenSage.Network.Wire;

/// <summary>
/// A growable little-endian byte writer. Every multi-byte value goes through
/// <see cref="BinaryPrimitives"/>'s explicit *LittleEndian writer, one primitive field at a
/// time - never <c>MemoryMarshal</c> over a struct, so there is no host struct-layout or
/// padding to leak onto the wire (design-netcode.md §5.2's "struct layout / sizeof on the
/// wire" hazard). The output is little-endian regardless of host endianness by construction.
/// </summary>
internal sealed class WireWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public int Length => _buffer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

    public void WriteByte(byte value)
    {
        var span = _buffer.GetSpan(sizeof(byte));
        span[0] = value;
        _buffer.Advance(sizeof(byte));
    }

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value)
    {
        var span = _buffer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        _buffer.Advance(sizeof(ushort));
    }

    public void WriteInt32(int value)
    {
        var span = _buffer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        _buffer.Advance(sizeof(int));
    }

    public void WriteUInt32(uint value)
    {
        var span = _buffer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        _buffer.Advance(sizeof(uint));
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        var span = _buffer.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        _buffer.Advance(bytes.Length);
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();
}
