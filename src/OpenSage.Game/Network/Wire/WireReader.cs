using System.Buffers.Binary;

namespace OpenSage.Network.Wire;

/// <summary>
/// A forward-only little-endian byte reader over a borrowed span. Every <c>TryRead*</c>
/// method returns <see langword="false"/> instead of throwing when the span does not hold
/// enough bytes - a truncated wire buffer is malformed input (F6), not a bug, and must not
/// crash the peer that receives it. Multi-byte reads go through <see cref="BinaryPrimitives"/>'s
/// explicit *LittleEndian reader, so decode is little-endian regardless of host endianness.
/// </summary>
internal ref struct WireReader
{
    private readonly System.ReadOnlySpan<byte> _data;
    private int _position;

    public WireReader(System.ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public readonly int Position => _position;

    public readonly int Remaining => _data.Length - _position;

    public bool TryReadByte(out byte value)
    {
        if (Remaining < sizeof(byte))
        {
            value = 0;
            return false;
        }

        value = _data[_position];
        _position += sizeof(byte);
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        if (!BinaryPrimitives.TryReadUInt16LittleEndian(_data[_position..], out value))
        {
            value = 0;
            return false;
        }

        _position += sizeof(ushort);
        return true;
    }

    public bool TryReadInt32(out int value)
    {
        if (!BinaryPrimitives.TryReadInt32LittleEndian(_data[_position..], out value))
        {
            value = 0;
            return false;
        }

        _position += sizeof(int);
        return true;
    }

    public bool TryReadUInt32(out uint value)
    {
        if (!BinaryPrimitives.TryReadUInt32LittleEndian(_data[_position..], out value))
        {
            value = 0;
            return false;
        }

        _position += sizeof(uint);
        return true;
    }
}
