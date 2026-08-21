// The frozen typed xfer surface (api-freeze-v1 S4, verbatim; design-module-api §3.1 as
// amended). One Xfer walk per module, four consumers: XferSave, XferLoad, XferCrcVisitor,
// XferDeepDump. Names and tolerances are consumed by DeepDump / the harness only and are never
// folded into any checksum, matching the original engine, whose type tags never reach the
// accumulator (crc-byteorder §2).

using System.Collections.Generic;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Ticking;

namespace OpenSage.SimCore.Sync;

/// <summary>Which of the four visitors is executing the walk.</summary>
public enum XferMode : byte
{
    Save,
    Load,
    Crc,
    DeepDump,
}

/// <summary>
/// Target-B conformance class of a field, declared at the xfer call site
/// (xfer-conformance-strategy §3; design-module-api §4.1). Consumed by the harness
/// comparator; invisible to Save/Load/Crc.
/// </summary>
public enum Tolerance : byte
{
    /// <summary>Existence, IDs, flags, lifecycle - byte-equal or fail (ch.1/6).</summary>
    Exact,
    /// <summary>Health/resources/XP/timers - |delta| within 1 representational quantum (ch.2).</summary>
    Quantum,
    /// <summary>Kinematics - |delta| within the per-field epsilon, drift-tracked (ch.3).</summary>
    Band,
    /// <summary>Excluded from pointwise diff; checked by harness-side outcome comparators (ch.4).</summary>
    Outcome,
    /// <summary>RNG accounting fields, never real state (ch.5).</summary>
    DrawCount,
}

/// <summary>
/// Module identity for dump labels and update-queue tie-breaks. IXfer cannot take a
/// BehaviorModule - the module framework lives above SimCore (S1) - so the identity tuple
/// crosses the seam instead.
/// </summary>
public readonly record struct XferModuleId(uint ObjectId, int ModuleIndex, string Tag, string ClassName);

/// <summary>Per-item walk callback for <see cref="IXfer.XferList{T}"/>.</summary>
public delegate void XferItem<T>(IXfer xfer, ref T item);

public interface IXfer
{
    XferMode Mode { get; }

    void BeginModule(in XferModuleId id);
    void EndModule();

    void XferFix64(string name, ref Fix64 value, Tolerance tol = Tolerance.Exact);
    void XferFixVector3(string name, ref FixVector3 value, Tolerance tol = Tolerance.Exact);
    void XferInt(string name, ref int value, Tolerance tol = Tolerance.Exact);
    void XferUInt(string name, ref uint value, Tolerance tol = Tolerance.Exact);
    void XferBool(string name, ref bool value);
    void XferFrame(string name, ref LogicFrame value, Tolerance tol = Tolerance.Quantum);
    void XferFrameSpan(string name, ref LogicFrameSpan value, Tolerance tol = Tolerance.Quantum);
    void XferObjectId(string name, ref ObjectId value);
    void XferEnum<T>(string name, ref T value) where T : struct, System.Enum;
    void XferBitArray(string name, ref BitArray512 value);
    void XferList<T>(string name, List<T> list, XferItem<T> item);
    byte XferVersion(byte currentVersion);
}
