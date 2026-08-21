// The divergence-report contract. See the "THE DIVERGENCE REPORT CONTRACT" comment block at
// the top of Program.cs for the ordering rule this class implements -- this file is the data
// model plus the two renderers (human text, machine JSON); Program.cs only decides *when* to
// build one and prints what this class produces.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OpenSage.SimCore.DumpDiff;

public enum DivergenceKind
{
    /// <summary>No divergence: the two dumps are identical (exit 0).</summary>
    None,

    /// <summary>A dump's header line is missing or not a recognized version (exit 2).</summary>
    BadHeader,

    /// <summary>The two legs are different deepdump versions, so field cardinality differs
    /// structurally and a field-by-field diff would be meaningless (exit 2).</summary>
    VersionMismatch,

    /// <summary>A leg has zero V (checkpoint vector) lines -- the run produced no evidence,
    /// so this is refused as malformed rather than silently passed (exit 2).</summary>
    NoCheckpoints,

    /// <summary>--require-cross-arch was set and the combined arch metadata across both legs
    /// does not cover the required set (exit 2).</summary>
    CrossArchRequirementUnmet,

    /// <summary>An R (field) record line differs between legs (exit 1).</summary>
    RecordDivergence,

    /// <summary>A V (checkpoint vector) line differs between legs (exit 1).</summary>
    VectorDivergence,

    /// <summary>An F or C line differs, or a line's shape itself is malformed/unrecognized,
    /// or a body line otherwise doesn't compare equal outside the two structured cases above
    /// (exit 1).</summary>
    LineDivergence,

    /// <summary>The two bodies have different lengths and the shorter is a clean prefix of
    /// the longer -- one leg's run stopped early. Reported as a divergence, never a pass
    /// (exit 1).</summary>
    LengthMismatch,
}

public sealed class DivergenceReport
{
    public required DivergenceKind Kind { get; init; }
    public required int ExitCode { get; init; }

    public required string LabelA { get; init; }
    public required string LabelB { get; init; }

    public string? ArchA { get; init; }
    public string? ArchB { get; init; }
    public string? ExclusionA { get; init; }
    public string? ExclusionB { get; init; }

    /// <summary>Last frame (as text) at which the two streams were still identical, or null
    /// if they diverged before any F line was seen.</summary>
    public string? LastCommonFrame { get; init; }

    /// <summary>Frame at which the divergence was detected (the F line in effect when the
    /// first differing body line was reached), or null if not yet in any frame.</summary>
    public string? DivergenceFrame { get; init; }

    public int? ChannelOrdinal { get; init; }
    public string? ChannelName { get; init; }

    public int? LineNumber { get; init; }

    // Record (R) divergence detail.
    public string? ObjectId { get; init; }
    public string? ModuleIndex { get; init; }
    public string? ModuleTag { get; init; }
    public string? ModuleClass { get; init; }
    public string? FieldName { get; init; }
    public string? Tolerance { get; init; }
    public string? Type { get; init; }
    public string? HexA { get; init; }
    public string? HexB { get; init; }

    // Vector (V) divergence detail: the FULL vector from both sides, per contract point 6,
    // so the reader can see which channels held even though only one differed.
    public string? VectorCombinedA { get; init; }
    public string? VectorCombinedB { get; init; }
    public IReadOnlyList<string>? VectorChannelCrcsA { get; init; }
    public IReadOnlyList<string>? VectorChannelCrcsB { get; init; }

    public string? RawA { get; init; }
    public string? RawB { get; init; }

    public required string Summary { get; init; }

    public string RenderHuman()
    {
        var lines = new List<string>();

        if (Kind == DivergenceKind.None)
        {
            lines.Add($"identical: {LabelA} == {LabelB}");
            if (LastCommonFrame != null)
            {
                lines.Add($"  last frame: {LastCommonFrame}");
            }
            AppendMeta(lines);
            return string.Join('\n', lines);
        }

        lines.Add(Summary);

        // Contract point 1: leg labels first.
        lines.Add($"  leg A: {LabelA}");
        lines.Add($"  leg B: {LabelB}");

        if (ExitCode == 2)
        {
            // A format/setup error (bad header, version mismatch, no checkpoints, unmet
            // cross-arch requirement): there is no lockstep position to report, so the
            // frame/channel/record section of the contract does not apply -- just the raw
            // lines involved (if any) and the metadata.
            if (RawA != null)
            {
                lines.Add($"  A: {RawA}");
            }
            if (RawB != null)
            {
                lines.Add($"  B: {RawB}");
            }
            AppendMeta(lines);
            return string.Join('\n', lines);
        }

        // Contract points 2-3: last common frame, then divergence frame.
        lines.Add($"  last identical frame: {LastCommonFrame ?? "(none -- diverged before first F line)"}");
        lines.Add($"  divergence frame: {DivergenceFrame ?? "(unknown -- diverged before first F line)"}");

        // Contract point 4: channel.
        if (ChannelOrdinal != null)
        {
            lines.Add($"  channel: {ChannelOrdinal} ({ChannelName})");
        }

        if (LineNumber != null)
        {
            lines.Add($"  line: {LineNumber}");
        }

        // Contract point 5: R-record detail.
        if (Kind == DivergenceKind.RecordDivergence)
        {
            lines.Add($"  object {ObjectId} module {ModuleIndex} ({ModuleClass}, tag {ModuleTag}) field {FieldName} [tol {Tolerance}, {Type ?? "(v1: untyped)"}]");
            lines.Add($"    A: {HexA}");
            lines.Add($"    B: {HexB}");
        }

        // Contract point 6: full per-channel vector from both sides.
        if (Kind == DivergenceKind.VectorDivergence)
        {
            lines.Add($"  A vector: combined={VectorCombinedA} channels=[{string.Join(", ", VectorChannelCrcsA ?? Array.Empty<string>())}]");
            lines.Add($"  B vector: combined={VectorCombinedB} channels=[{string.Join(", ", VectorChannelCrcsB ?? Array.Empty<string>())}]");
        }

        if (Kind is DivergenceKind.LineDivergence or DivergenceKind.LengthMismatch)
        {
            if (RawA != null)
            {
                lines.Add($"  A: {RawA}");
            }
            if (RawB != null)
            {
                lines.Add($"  B: {RawB}");
            }
        }

        // Contract point 7: exclusion set each leg ran with.
        AppendMeta(lines);

        return string.Join('\n', lines);
    }

    private void AppendMeta(List<string> lines)
    {
        lines.Add($"  exclusion set A: {ExclusionA ?? "unspecified"}");
        lines.Add($"  exclusion set B: {ExclusionB ?? "unspecified"}");
        lines.Add($"  arch A: {ArchA ?? "unspecified"}");
        lines.Add($"  arch B: {ArchB ?? "unspecified"}");
    }

    /// <summary>
    /// The machine-readable twin of <see cref="RenderHuman"/>: single-line JSON with the same
    /// facts, keyed for programmatic assertions (contract point: "a future job can assert on
    /// it"). Field presence mirrors the human report -- absent facts are omitted (not null),
    /// so a consumer can check ContainsKey rather than parse a sentinel string.
    /// </summary>
    public string RenderMachineJson()
    {
        var obj = new Dictionary<string, object?>
        {
            ["kind"] = Kind.ToString(),
            ["exitCode"] = ExitCode,
            ["labelA"] = LabelA,
            ["labelB"] = LabelB,
        };

        void AddIfNotNull(string key, object? value)
        {
            if (value != null)
            {
                obj[key] = value;
            }
        }

        AddIfNotNull("archA", ArchA);
        AddIfNotNull("archB", ArchB);
        AddIfNotNull("exclusionA", ExclusionA);
        AddIfNotNull("exclusionB", ExclusionB);
        AddIfNotNull("lastCommonFrame", LastCommonFrame);
        AddIfNotNull("divergenceFrame", DivergenceFrame);
        AddIfNotNull("channelOrdinal", ChannelOrdinal);
        AddIfNotNull("channelName", ChannelName);
        AddIfNotNull("lineNumber", LineNumber);
        AddIfNotNull("objectId", ObjectId);
        AddIfNotNull("moduleIndex", ModuleIndex);
        AddIfNotNull("moduleTag", ModuleTag);
        AddIfNotNull("moduleClass", ModuleClass);
        AddIfNotNull("fieldName", FieldName);
        AddIfNotNull("tolerance", Tolerance);
        AddIfNotNull("type", Type);
        AddIfNotNull("hexA", HexA);
        AddIfNotNull("hexB", HexB);
        AddIfNotNull("vectorCombinedA", VectorCombinedA);
        AddIfNotNull("vectorCombinedB", VectorCombinedB);
        AddIfNotNull("vectorChannelCrcsA", VectorChannelCrcsA?.ToArray());
        AddIfNotNull("vectorChannelCrcsB", VectorChannelCrcsB?.ToArray());
        AddIfNotNull("rawA", RawA);
        AddIfNotNull("rawB", RawB);
        obj["summary"] = Summary;

        return JsonSerializer.Serialize(obj);
    }
}
