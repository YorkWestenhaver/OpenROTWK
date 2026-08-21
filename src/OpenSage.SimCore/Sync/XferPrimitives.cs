// The one canonical wire form for every IXfer primitive, shared by all four visitors so that
// Save, Load, Crc and DeepDump see byte-identical images by construction (api-freeze-v1 S4:
// the CRC visitor folds each primitive call independently; the deep CRC folds the identical
// bytes it streams). Everything is explicit little-endian - the F7 fold consumes native-LE
// words, and an explicit composition keeps the image identical on every architecture.
//
// These are OUR canonical encodings (self-consistency is the only requirement - Target A);
// byte-parity with the original's save layout is a non-goal by ruling (F9 / B2).

using System;
using System.Runtime.CompilerServices;
using OpenSage.SimCore.Numerics;

namespace OpenSage.SimCore.Sync;

internal static class XferPrimitives
{
    // Canonical sizes, used by visitors to stackalloc exact-size buffers.
    public const int SizeOfInt = 4;
    public const int SizeOfUInt = 4;
    public const int SizeOfBool = 1;
    public const int SizeOfFix64 = 8;
    public const int SizeOfFixVector3 = 24;
    public const int SizeOfEnum = 8;   // underlying value widened to int64
    public const int SizeOfBitArray512 = 4 + 64; // int length + 8 words

    public static void WriteUInt32(Span<byte> b, uint v)
    {
        b[0] = (byte)v;
        b[1] = (byte)(v >> 8);
        b[2] = (byte)(v >> 16);
        b[3] = (byte)(v >> 24);
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> b)
    {
        return (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);
    }

    public static void WriteInt64(Span<byte> b, long v)
    {
        WriteUInt32(b, (uint)v);
        WriteUInt32(b.Slice(4), (uint)((ulong)v >> 32));
    }

    public static long ReadInt64(ReadOnlySpan<byte> b)
    {
        return (long)(ReadUInt32(b) | ((ulong)ReadUInt32(b.Slice(4)) << 32));
    }

    public static void WriteFix64(Span<byte> b, in Fix64 v) => WriteInt64(b, v.RawValue);

    public static Fix64 ReadFix64(ReadOnlySpan<byte> b) => Fix64.FromRaw(ReadInt64(b));

    public static void WriteFixVector3(Span<byte> b, in FixVector3 v)
    {
        WriteFix64(b, v.X);
        WriteFix64(b.Slice(8), v.Y);
        WriteFix64(b.Slice(16), v.Z);
    }

    public static FixVector3 ReadFixVector3(ReadOnlySpan<byte> b)
    {
        return new FixVector3(ReadFix64(b), ReadFix64(b.Slice(8)), ReadFix64(b.Slice(16)));
    }

    public static void WriteBitArray512(Span<byte> b, in BitArray512 v)
    {
        WriteUInt32(b, (uint)v.Length);
        Span<ulong> words = stackalloc ulong[8];
        v.CopyWordsTo(words);
        for (var i = 0; i < 8; i++)
        {
            WriteInt64(b.Slice(4 + i * 8), (long)words[i]);
        }
    }

    public static BitArray512 ReadBitArray512(ReadOnlySpan<byte> b)
    {
        var length = (int)ReadUInt32(b);
        Span<ulong> words = stackalloc ulong[8];
        for (var i = 0; i < 8; i++)
        {
            words[i] = (ulong)ReadInt64(b.Slice(4 + i * 8));
        }
        return BitArray512.FromWords(length, words);
    }

    /// <summary>
    /// Canonical enum image: the underlying integral value, sign-extended to int64.
    /// Widening (rather than folding the underlying width) keeps the image stable if an
    /// enum's declared underlying type ever changes - a save-format kindness that costs
    /// four extra bytes per enum field. Enum.GetValues/boxing are avoided (banned surface).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long EnumToInt64<T>(in T value) where T : struct, Enum
    {
        var v = value;
        return Unsafe.SizeOf<T>() switch
        {
            1 => Unsafe.As<T, sbyte>(ref v),
            2 => Unsafe.As<T, short>(ref v),
            4 => Unsafe.As<T, int>(ref v),
            _ => Unsafe.As<T, long>(ref v),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Int64ToEnum<T>(long value) where T : struct, Enum
    {
        switch (Unsafe.SizeOf<T>())
        {
            case 1: { var t = (sbyte)value; return Unsafe.As<sbyte, T>(ref t); }
            case 2: { var t = (short)value; return Unsafe.As<short, T>(ref t); }
            case 4: { var t = (int)value; return Unsafe.As<int, T>(ref t); }
            default: return Unsafe.As<long, T>(ref value);
        }
    }
}
