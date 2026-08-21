// Parses raw "opensage-deepdump" text into a DumpFile: a version, a metadata dictionary
// harvested from comment lines, and the ordered body of F/C/R/E/V lines that the comparator
// walks in lockstep. See Program.cs for the full format spec.

using System;
using System.Collections.Generic;

namespace OpenSage.SimCore.DumpDiff;

public sealed class DumpFile
{
    /// <summary>"v1", "v2", or null if the header line was missing/unrecognized.</summary>
    public string? Version { get; init; }

    public string HeaderRaw { get; init; } = "";

    /// <summary>
    /// Metadata harvested from "# key=value" comment lines (case-insensitive keys, first
    /// occurrence of a given key wins). ScenarioDriver (n14a-driver-cli) is the emitter side of
    /// this convention -- see the METADATA CONVENTION note in Program.cs and
    /// OpenSage.SimCore.ScenarioDriver.Program's --arch-stamp/--exclude handling, which writes
    /// exactly this shape (one key per comment line; arch/rid are never packed onto one line
    /// together). Absence of a key just means the report shows "unspecified"; it is never
    /// treated as a format error on its own.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>All comment lines verbatim, in file order (for a report to quote if useful).</summary>
    public IReadOnlyList<string> CommentLines { get; init; } = Array.Empty<string>();

    /// <summary>The F/C/R/E/V lines only, in file order -- comment lines are never in here.</summary>
    public IReadOnlyList<DumpLine> Body { get; init; } = Array.Empty<DumpLine>();

    public int VectorLineCount { get; init; }
}

public static class DumpParser
{
    public static DumpFile Parse(string text)
    {
        // A dump is written with a trailing '\n' after every line (DeepCrcWriter), so
        // splitting on '\n' leaves one final empty string; drop it if present.
        var lines = text.Split('\n');
        var lineCount = lines.Length;
        if (lineCount > 0 && lines[lineCount - 1].Length == 0)
        {
            lineCount--;
        }

        string? version = lines.Length > 0 ? HeaderVersion(lines[0]) : null;
        var headerRaw = lines.Length > 0 ? lines[0] : "";

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var comments = new List<string>();
        var body = new List<DumpLine>();
        var vectorCount = 0;

        // Line 0 is the header (or garbage, if version is null); body parsing starts at 1.
        // If the header itself is unrecognized we still parse the rest defensively so a
        // caller can report line numbers sensibly, but Program.cs treats Version == null as
        // an immediate exit-2 and never reaches the comparator.
        for (var i = version != null ? 1 : 0; i < lineCount; i++)
        {
            var raw = lines[i];
            var lineNumber = i + 1;

            if (raw.Length == 0)
            {
                // A stray blank line is not part of any known record shape.
                body.Add(new DumpLine { Kind = DumpLineKind.Malformed, Raw = raw, LineNumber = lineNumber, Reason = "blank line" });
                continue;
            }

            if (raw[0] == '#')
            {
                comments.Add(raw);
                TryHarvestMetadata(raw, metadata);
                continue;
            }

            var parsed = ParseBodyLine(raw, lineNumber);
            if (parsed.Kind == DumpLineKind.Vector)
            {
                vectorCount++;
            }
            body.Add(parsed);
        }

        return new DumpFile
        {
            Version = version,
            HeaderRaw = headerRaw,
            Metadata = metadata,
            CommentLines = comments,
            Body = body,
            VectorLineCount = vectorCount,
        };
    }

    private static string? HeaderVersion(string firstLine) => firstLine switch
    {
        "# opensage-deepdump v1" => "v1",
        "# opensage-deepdump v2" => "v2",
        _ => null,
    };

    /// <summary>
    /// METADATA CONVENTION (adopted by ScenarioDriver's --arch-stamp/--exclude emitter, see
    /// OpenSage.SimCore.ScenarioDriver.Program; cross-tool round-trip coverage lives in
    /// ScenarioDriverCliTests.ArchAndExcludeMetadata_RoundTripsThroughDumpDiffParser):
    /// a comment line of the exact shape "# key=value" (no spaces around '=', key is
    /// [A-Za-z0-9_-]+) is harvested as metadata. Recognized keys this tool acts on:
    ///   arch      RuntimeInformation.ProcessArchitecture of the leg that produced the dump
    ///             (e.g. "Arm64", "X64") -- used by --require-cross-arch.
    ///   os        free-text OS description.
    ///   rid       .NET runtime identifier (e.g. "osx-arm64").
    ///   exclude   comma-separated SyncChecker exclusion set, or "none".
    /// Any other "# ..." line (including a non-matching "# key=value" shape) is still
    /// stripped from the body walk and kept verbatim in CommentLines, but does not populate
    /// Metadata.
    /// </summary>
    private static void TryHarvestMetadata(string commentLine, Dictionary<string, string> metadata)
    {
        // commentLine starts with '#'; a metadata line looks like "# key=value".
        var rest = commentLine.Length > 1 && commentLine[1] == ' '
            ? commentLine.AsSpan(2)
            : commentLine.AsSpan(1);

        var eq = rest.IndexOf('=');
        if (eq <= 0)
        {
            return;
        }

        var key = rest[..eq];
        foreach (var c in key)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '-'))
            {
                return;
            }
        }

        var value = rest[(eq + 1)..].ToString();
        var keyStr = key.ToString();
        if (!metadata.ContainsKey(keyStr))
        {
            metadata[keyStr] = value;
        }
    }

    private static DumpLine ParseBodyLine(string raw, int lineNumber)
    {
        var parts = raw.Split(' ');
        switch (parts[0])
        {
            case "F" when parts.Length == 2 && TryParseUInt(parts[1], out var frame):
                return new DumpLine { Kind = DumpLineKind.Frame, Raw = raw, LineNumber = lineNumber, Frame = frame };

            case "C" when parts.Length == 3 && TryParseInt(parts[1], out var cOrd):
                return new DumpLine
                {
                    Kind = DumpLineKind.ChannelBegin,
                    Raw = raw,
                    LineNumber = lineNumber,
                    ChannelOrdinal = cOrd,
                    ChannelName = parts[2],
                };

            case "E" when parts.Length == 3 && TryParseInt(parts[1], out var eOrd):
                return new DumpLine
                {
                    Kind = DumpLineKind.ChannelEnd,
                    Raw = raw,
                    LineNumber = lineNumber,
                    ChannelOrdinal = eOrd,
                    ChannelCrc = parts[2],
                };

            case "R" when parts.Length == 8: // v1: no type token
                return new DumpLine
                {
                    Kind = DumpLineKind.Record,
                    Raw = raw,
                    LineNumber = lineNumber,
                    ObjectId = parts[1],
                    ModuleIndex = parts[2],
                    Tag = parts[3],
                    ClassName = parts[4],
                    FieldName = parts[5],
                    Tolerance = parts[6],
                    Type = null,
                    HexBytes = parts[7],
                };

            case "R" when parts.Length == 9: // v2: type token present
                return new DumpLine
                {
                    Kind = DumpLineKind.Record,
                    Raw = raw,
                    LineNumber = lineNumber,
                    ObjectId = parts[1],
                    ModuleIndex = parts[2],
                    Tag = parts[3],
                    ClassName = parts[4],
                    FieldName = parts[5],
                    Tolerance = parts[6],
                    Type = parts[7],
                    HexBytes = parts[8],
                };

            case "V" when parts.Length >= 3:
                return new DumpLine
                {
                    Kind = DumpLineKind.Vector,
                    Raw = raw,
                    LineNumber = lineNumber,
                    Frame = TryParseUInt(parts[1], out var vFrame) ? vFrame : 0,
                    Combined = parts[2],
                    ChannelCrcs = parts.Length > 3 ? parts[3..] : Array.Empty<string>(),
                };

            default:
                return new DumpLine
                {
                    Kind = DumpLineKind.Malformed,
                    Raw = raw,
                    LineNumber = lineNumber,
                    Reason = $"unrecognized line shape (first token '{parts[0]}', {parts.Length} fields)",
                };
        }
    }

    private static bool TryParseUInt(string s, out uint value) => uint.TryParse(s, out value);
    private static bool TryParseInt(string s, out int value) => int.TryParse(s, out value);
}
