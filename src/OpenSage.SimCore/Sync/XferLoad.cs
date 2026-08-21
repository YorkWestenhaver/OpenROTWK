// Load visitor: reads canonical byte images back in walk order and assigns through the refs.
// Symmetric with XferSave by sharing XferPrimitives for every encoding; the shadow-copy base
// test (Save -> Load -> CRC == live CRC) is what enforces walk completeness per module.

using System;
using System.Collections.Generic;
using System.IO;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Ticking;

namespace OpenSage.SimCore.Sync;

public sealed class XferLoad : IXfer, IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    public XferLoad(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    public XferMode Mode => XferMode.Load;

    public void BeginModule(in XferModuleId id)
    {
    }

    public void EndModule()
    {
    }

    public void XferFix64(string name, ref Fix64 value, Tolerance tol = Tolerance.Exact)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfFix64];
        Fill(b);
        value = XferPrimitives.ReadFix64(b);
    }

    public void XferFixVector3(string name, ref FixVector3 value, Tolerance tol = Tolerance.Exact)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfFixVector3];
        Fill(b);
        value = XferPrimitives.ReadFixVector3(b);
    }

    public void XferInt(string name, ref int value, Tolerance tol = Tolerance.Exact)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfInt];
        Fill(b);
        value = (int)XferPrimitives.ReadUInt32(b);
    }

    public void XferUInt(string name, ref uint value, Tolerance tol = Tolerance.Exact)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfUInt];
        Fill(b);
        value = XferPrimitives.ReadUInt32(b);
    }

    public void XferBool(string name, ref bool value)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfBool];
        Fill(b);
        value = b[0] != 0;
    }

    public void XferFrame(string name, ref LogicFrame value, Tolerance tol = Tolerance.Quantum)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfUInt];
        Fill(b);
        value = new LogicFrame(XferPrimitives.ReadUInt32(b));
    }

    public void XferFrameSpan(string name, ref LogicFrameSpan value, Tolerance tol = Tolerance.Quantum)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfUInt];
        Fill(b);
        value = new LogicFrameSpan(XferPrimitives.ReadUInt32(b));
    }

    public void XferObjectId(string name, ref ObjectId value)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfUInt];
        Fill(b);
        value = new ObjectId(XferPrimitives.ReadUInt32(b));
    }

    public void XferEnum<T>(string name, ref T value) where T : struct, Enum
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfEnum];
        Fill(b);
        value = XferPrimitives.Int64ToEnum<T>(XferPrimitives.ReadInt64(b));
    }

    public void XferBitArray(string name, ref BitArray512 value)
    {
        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfBitArray512];
        Fill(b);
        value = XferPrimitives.ReadBitArray512(b);
    }

    /// <summary>
    /// Reads the stored count, resizes the list (new slots take default(T) - a reference-
    /// or wrapper-typed T must be constructed by the item callback when it sees a default
    /// slot in Load mode), then runs the item walk per element.
    /// </summary>
    public void XferList<T>(string name, List<T> list, XferItem<T> item)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(item);

        Span<byte> b = stackalloc byte[XferPrimitives.SizeOfInt];
        Fill(b);
        var count = (int)XferPrimitives.ReadUInt32(b);
        if (count < 0)
        {
            throw new InvalidDataException("Malformed xfer stream: negative list count.");
        }

        while (list.Count > count)
        {
            list.RemoveAt(list.Count - 1);
        }
        while (list.Count < count)
        {
            list.Add(default!);
        }

        for (var i = 0; i < count; i++)
        {
            var element = list[i];
            item(this, ref element);
            list[i] = element;
        }
    }

    /// <summary>
    /// Reads the stored version and returns it; the caller branches its walk on the result
    /// (the StatePersister.PersistVersion pattern). A stored version NEWER than the code
    /// reading it is malformed input.
    /// </summary>
    public byte XferVersion(byte currentVersion)
    {
        Span<byte> b = stackalloc byte[1];
        Fill(b);
        if (b[0] > currentVersion)
        {
            throw new InvalidDataException(
                "Xfer stream version is newer than this build understands.");
        }
        return b[0];
    }

    private void Fill(Span<byte> buffer)
    {
        _stream.ReadExactly(buffer);
    }

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}
