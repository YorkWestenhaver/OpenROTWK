// Shared plumbing for the three write-direction visitors (Save, Crc, DeepDump): every typed
// primitive is rendered to its canonical byte image (XferPrimitives) and handed to Consume as
// ONE buffer per call - which is exactly the F7 per-call granularity contract. Names and
// tolerances ride alongside for the DeepDump consumer and never enter the byte image.

using System;
using System.Collections.Generic;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Ticking;

namespace OpenSage.SimCore.Sync;

public abstract class XferWriteVisitorBase : IXfer
{
    private static readonly XferModuleId NoModule = new(0, -1, "~", "~");

    private XferModuleId _currentModule = NoModule;

    public abstract XferMode Mode { get; }

    protected XferModuleId CurrentModule => _currentModule;

    /// <summary>Receives one primitive call's canonical byte image. Never a concatenation.
    /// <paramref name="kind"/> is dump metadata only (harness deep-dump schema `t`); like
    /// names and tolerances it never enters the byte image.</summary>
    protected abstract void Consume(string name, Tolerance tol, XferValueKind kind, ReadOnlySpan<byte> bytes);

    public virtual void BeginModule(in XferModuleId id) => _currentModule = id;

    public virtual void EndModule() => _currentModule = NoModule;

    public void XferFix64(string name, ref Fix64 value, Tolerance tol = Tolerance.Exact)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfFix64];
        XferPrimitives.WriteFix64(b, value);
        Consume(name, tol, XferValueKind.Fix64, b);
    }

    public void XferFixVector3(string name, ref FixVector3 value, Tolerance tol = Tolerance.Exact)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfFixVector3];
        XferPrimitives.WriteFixVector3(b, value);
        Consume(name, tol, XferValueKind.FixVector3, b);
    }

    public void XferInt(string name, ref int value, Tolerance tol = Tolerance.Exact)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfInt];
        XferPrimitives.WriteUInt32(b, (uint)value);
        Consume(name, tol, XferValueKind.Int, b);
    }

    public void XferUInt(string name, ref uint value, Tolerance tol = Tolerance.Exact)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfUInt];
        XferPrimitives.WriteUInt32(b, value);
        Consume(name, tol, XferValueKind.UInt, b);
    }

    public void XferBool(string name, ref bool value)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfBool];
        b[0] = value ? (byte)1 : (byte)0;
        Consume(name, Tolerance.Exact, XferValueKind.Bool, b);
    }

    public void XferFrame(string name, ref LogicFrame value, Tolerance tol = Tolerance.Quantum)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfUInt];
        XferPrimitives.WriteUInt32(b, value.Value);
        Consume(name, tol, XferValueKind.Frame, b);
    }

    public void XferFrameSpan(string name, ref LogicFrameSpan value, Tolerance tol = Tolerance.Quantum)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfUInt];
        XferPrimitives.WriteUInt32(b, value.Value);
        Consume(name, tol, XferValueKind.FrameSpan, b);
    }

    public void XferObjectId(string name, ref ObjectId value)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfUInt];
        XferPrimitives.WriteUInt32(b, value.Index);
        Consume(name, Tolerance.Exact, XferValueKind.ObjectId, b);
    }

    public void XferEnum<T>(string name, ref T value) where T : struct, Enum
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfEnum];
        XferPrimitives.WriteInt64(b, XferPrimitives.EnumToInt64(value));
        Consume(name, Tolerance.Exact, XferValueKind.Enum, b);
    }

    public void XferBitArray(string name, ref BitArray512 value)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfBitArray512];
        XferPrimitives.WriteBitArray512(b, value);
        Consume(name, Tolerance.Exact, XferValueKind.BitArray512, b);
    }

    public void XferList<T>(string name, List<T> list, XferItem<T> item)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(item);

        // The count participates: a length divergence must trip the CRC even when the
        // common prefix matches.
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfInt];
        XferPrimitives.WriteUInt32(b, (uint)list.Count);
        Consume(name, Tolerance.Exact, XferValueKind.UInt, b);

        for (var i = 0; i < list.Count; i++)
        {
            var element = list[i];
            item(this, ref element);
            list[i] = element;
        }
    }

    public virtual byte XferVersion(byte currentVersion)
    {
        Span<byte> b = stackalloc byte[1];
        b[0] = currentVersion;
        Consume("__version", Tolerance.Exact, XferValueKind.Bytes, b);
        return currentVersion;
    }
}
