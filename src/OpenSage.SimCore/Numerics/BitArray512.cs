// Moved from OpenSage.Mathematics in scaffolding step 5: the frozen IXfer surface
// (api-freeze-v1 S4) takes `ref BitArray512`, and SimCore may not reference the float-bearing
// OpenSage.Mathematics assembly, so the type itself is sim substrate. It is pure integer bit
// storage; the move swapped its three banned-surface calls (BitOperations.PopCount, Math.Max,
// HashCode.Combine) for in-assembly deterministic equivalents and changed nothing else.
// Consumers keep compiling via `global using BitArray512 = ...` bridge aliases, the step-4
// LogicFrame pattern.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OpenSage.SimCore.Numerics;

[StructLayout(LayoutKind.Sequential)]
public struct BitArray512
{
    // We use individual fields instead of an array to avoid an extra allocation.
    // Fixed size buffers are only usable in unsafe structs.
    private ulong _a0;
    private ulong _a1;
    private ulong _a2;
    private ulong _a3;
    private ulong _a4;
    private ulong _a5;
    private ulong _a6;
    private ulong _a7;

    /// <summary>
    /// Lazily computed number of 1 bits. -1 is a special value, which indicates the cache is invalid.
    /// </summary>
    private int _setBits;

    public readonly int Length { get; }

    /// <summary>
    /// Is any bit set to 1?
    /// </summary>
    public bool AnyBitSet
    {
        get => NumBitsSet > 0;
    }

    /// <summary>
    /// NUmber of 1 bits in this array.
    /// </summary>
    public int NumBitsSet
    {
        get
        {
            // Refresh the cache if required.
            if (_setBits == -1)
            {
                _setBits = CountSetBits();
            }

            return _setBits;
        }
    }

    public BitArray512(int length) : this()
    {
        if (length < 0 || length > 512)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                $"Length must between 0 and 512."
            );
        }

        Length = length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int bit, bool value)
    {
        if (bit < 0 || bit >= Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bit));
        }

        var offset = bit >> 6; // bit / 64
        var mask = (ulong)1 << bit;

        unsafe
        {
            var pointer = (ulong*)Unsafe.AsPointer(ref this);
            if (value)
            {
                *(pointer + offset) |= mask;
            }
            else
            {
                *(pointer + offset) &= ~mask;
            }
        }

        // Mark cache as invalid.
        _setBits = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int bit)
    {
        if (bit < 0 || bit >= Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bit));
        }

        var offset = bit >> 6; // bit / 64
        var mask = (ulong)1 << bit;

        unsafe
        {
            var pointer = (ulong*)Unsafe.AsPointer(ref this);
            return (pointer[offset] & mask) != 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Clear()
    {
        _a0 = 0;
        _a1 = 0;
        _a2 = 0;
        _a3 = 0;
        _a4 = 0;
        _a5 = 0;
        _a6 = 0;
        _a7 = 0;
        _setBits = 0;
    }

    public void SetAll(bool value)
    {
        // If we're clearing the bits, just set all the fields to 0.
        if (!value)
        {
            Clear();
            return;
        }

        // However, if we're setting the bits to 1 we can't just set all the fields to ulong.Maxvalue,
        // because otherwise we would get 1 bits outside of the actual length of the array,
        // which would be incorrectly counted by CountSetBits().

        var byteOffset = 0;
        var remainingBits = Length;

        unsafe
        {
            var fieldsPointer = (byte*)Unsafe.AsPointer(ref this);

            // Set as many bits at a time as you can.

            while (remainingBits >= 64)
            {
                *(ulong*)(fieldsPointer + byteOffset) = ulong.MaxValue;
                byteOffset += 8;
                remainingBits -= 64;
            }

            if (remainingBits >= 32)
            {
                *(uint*)(fieldsPointer + byteOffset) = uint.MaxValue;
                byteOffset += 4;
                remainingBits -= 32;
            }

            if (remainingBits >= 16)
            {
                *(ushort*)(fieldsPointer + byteOffset) = ushort.MaxValue;
                byteOffset += 2;
                remainingBits -= 16;
            }

            if (remainingBits >= 8)
            {
                *(fieldsPointer + byteOffset) = byte.MaxValue;
                byteOffset += 1;
                remainingBits -= 8;
            }

            // This is a mask consisting of `remainingBits´ ones.
            var remainingMask = (1 << remainingBits) - 1;
            *(fieldsPointer + byteOffset) |= (byte)remainingMask;
        }

        _setBits = Length;
    }

    public void CopyFrom(in BitArray512 other)
    {
        if (Length != other.Length)
        {
            throw new ArgumentException(nameof(other), "Both BitArrays must have the same length.");
        }

        _a0 = other._a0;
        _a1 = other._a1;
        _a2 = other._a2;
        _a3 = other._a3;
        _a4 = other._a4;
        _a5 = other._a5;
        _a6 = other._a6;
        _a7 = other._a7;
        _setBits = other._setBits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CountSetBits()
    {
        return PopCount(_a0) +
               PopCount(_a1) +
               PopCount(_a2) +
               PopCount(_a3) +
               PopCount(_a4) +
               PopCount(_a5) +
               PopCount(_a6) +
               PopCount(_a7);
    }

    /// <summary>
    /// SWAR population count. System.Numerics is on the SimCore banned surface (SIMCORE002),
    /// so the count is computed in plain integer arithmetic; it is branch-free and
    /// bit-identical on every architecture.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PopCount(ulong v)
    {
        v -= (v >> 1) & 0x5555555555555555ul;
        v = (v & 0x3333333333333333ul) + ((v >> 2) & 0x3333333333333333ul);
        v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0Ful;
        return (int)((v * 0x0101010101010101ul) >> 56);
    }

    public BitArray512 And(in BitArray512 other)
    {
        return new BitArray512(FixMath.Max(Length, other.Length))
        {
            _a0 = _a0 & other._a0,
            _a1 = _a1 & other._a1,
            _a2 = _a2 & other._a2,
            _a3 = _a3 & other._a3,
            _a4 = _a4 & other._a4,
            _a5 = _a5 & other._a5,
            _a6 = _a6 & other._a6,
            _a7 = _a7 & other._a7,
            _setBits = -1
        };
    }

    public BitArray512 Or(in BitArray512 other)
    {
        return new BitArray512(FixMath.Max(Length, other.Length))
        {
            _a0 = _a0 | other._a0,
            _a1 = _a1 | other._a1,
            _a2 = _a2 | other._a2,
            _a3 = _a3 | other._a3,
            _a4 = _a4 | other._a4,
            _a5 = _a5 | other._a5,
            _a6 = _a6 | other._a6,
            _a7 = _a7 | other._a7,
            _setBits = -1,
        };
    }

    public bool Equals(in BitArray512 other)
    {
        return
            Length == other.Length &&
            _a0 == other._a0 &&
            _a1 == other._a1 &&
            _a2 == other._a2 &&
            _a3 == other._a3 &&
            _a4 == other._a4 &&
            _a5 == other._a5 &&
            _a6 == other._a6 &&
            _a7 == other._a7;
    }

    public override int GetHashCode()
    {
        // System.HashCode is randomized per process (SIMCORE005); chain the words through the
        // assembly's deterministic FNV-1a fold instead.
        var h = DeterministicHash.Begin();
        h = DeterministicHash.Add(h, (long)_a0);
        h = DeterministicHash.Add(h, (long)_a1);
        h = DeterministicHash.Add(h, (long)_a2);
        h = DeterministicHash.Add(h, (long)_a3);
        h = DeterministicHash.Add(h, (long)_a4);
        h = DeterministicHash.Add(h, (long)_a5);
        h = DeterministicHash.Add(h, (long)_a6);
        h = DeterministicHash.Add(h, (long)_a7);
        h = DeterministicHash.Add(h, Length);
        return DeterministicHash.Finish(h);
    }

    /// <summary>
    /// Copies the raw 64-byte word image into <paramref name="destination"/> (8 ulongs,
    /// low word first). Internal serialization hook for the Sync xfer visitors.
    /// </summary>
    internal readonly void CopyWordsTo(Span<ulong> destination)
    {
        destination[0] = _a0;
        destination[1] = _a1;
        destination[2] = _a2;
        destination[3] = _a3;
        destination[4] = _a4;
        destination[5] = _a5;
        destination[6] = _a6;
        destination[7] = _a7;
    }

    /// <summary>
    /// Rebuilds a value from its raw word image; inverse of <see cref="CopyWordsTo"/>.
    /// Internal deserialization hook for the Sync xfer visitors.
    /// </summary>
    internal static BitArray512 FromWords(int length, ReadOnlySpan<ulong> words)
    {
        return new BitArray512(length)
        {
            _a0 = words[0],
            _a1 = words[1],
            _a2 = words[2],
            _a3 = words[3],
            _a4 = words[4],
            _a5 = words[5],
            _a6 = words[6],
            _a7 = words[7],
            _setBits = -1,
        };
    }
}
