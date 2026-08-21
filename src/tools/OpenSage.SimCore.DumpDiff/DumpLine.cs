// Parsed representation of one line of an "opensage-deepdump" file. See Program.cs for the
// full format spec and the divergence-report contract. This file only knows how to turn a raw
// text line into a structured record -- it has no opinion about comparison.

using System.Collections.Generic;

namespace OpenSage.SimCore.DumpDiff;

/// <summary>
/// The kind of a parsed dump line. <see cref="Comment"/> lines (anything starting with "#"
/// other than the version header) are never part of the lockstep body walk -- they are
/// stripped out and folded into <see cref="DumpFile.Metadata"/> instead.
/// </summary>
public enum DumpLineKind
{
    Header,
    Comment,
    Frame,
    ChannelBegin,
    Record,
    ChannelEnd,
    Vector,
    Malformed,
}

/// <summary>
/// One parsed body line (F / C / R / E / V) plus enough of the raw text to reproduce it in a
/// report. Fields not relevant to a given <see cref="Kind"/> are left at their default value.
/// </summary>
public sealed class DumpLine
{
    public required DumpLineKind Kind { get; init; }
    public required string Raw { get; init; }
    public required int LineNumber { get; init; } // 1-based, matching a text editor's line count

    // Frame
    public uint Frame { get; init; }

    // ChannelBegin / ChannelEnd
    public int ChannelOrdinal { get; init; }
    public string ChannelName { get; init; } = "";
    public string ChannelCrc { get; init; } = "";

    // Record (R)
    public string ObjectId { get; init; } = "";
    public string ModuleIndex { get; init; } = "";
    public string Tag { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string FieldName { get; init; } = "";
    public string Tolerance { get; init; } = "";
    public string? Type { get; init; } // null for a v1 record (no type token)
    public string HexBytes { get; init; } = "";

    // Vector (V)
    public string Combined { get; init; } = "";
    public IReadOnlyList<string> ChannelCrcs { get; init; } = System.Array.Empty<string>();

    // Malformed
    public string? Reason { get; init; }
}
