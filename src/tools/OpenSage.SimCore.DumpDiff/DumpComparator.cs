// The comparator itself: lockstep-walks two parsed dumps and produces the single
// DivergenceReport that Program.cs prints and that a caller can also drive directly (this is
// the entry point the unit tests exercise). See Program.cs for the format spec and the
// divergence-report contract this class fulfils.

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenSage.SimCore.DumpDiff;

public sealed class CompareOptions
{
    public string LabelA { get; init; } = "A";
    public string LabelB { get; init; } = "B";

    /// <summary>Overrides the "exclude" metadata key harvested from leg A's comments, if the
    /// caller already knows the exclusion set out of band (contract point 4: "must be passed
    /// in" when not recoverable from the dump itself).</summary>
    public string? ExcludeOverrideA { get; init; }
    public string? ExcludeOverrideB { get; init; }

    /// <summary>When set, the comparator refuses (exit 2) unless the "arch" metadata harvested
    /// from the two legs, taken together, covers every entry in <see cref="RequiredArches"/>.
    /// This absorbs the cross-arch assertion that would otherwise live inline in CI: a run
    /// that accidentally compares two same-arch dumps proves nothing about crossplay and must
    /// not be allowed to report a plain pass.</summary>
    public bool RequireCrossArch { get; init; }

    public IReadOnlyList<string> RequiredArches { get; init; } = new[] { "Arm64", "X64" };
}

public static class DumpComparator
{
    public static DivergenceReport Compare(string textA, string textB, CompareOptions options)
    {
        var a = DumpParser.Parse(textA);
        var b = DumpParser.Parse(textB);

        var archA = a.Metadata.TryGetValue("arch", out var aArch) ? aArch : null;
        var archB = b.Metadata.TryGetValue("arch", out var bArch) ? bArch : null;
        var exclA = options.ExcludeOverrideA ?? (a.Metadata.TryGetValue("exclude", out var aExcl) ? aExcl : null);
        var exclB = options.ExcludeOverrideB ?? (b.Metadata.TryGetValue("exclude", out var bExcl) ? bExcl : null);

        // --- Format-error gate: bad header (exit 2). Checked before anything else, because a
        // dump that isn't the declared format at all can't be lockstep-walked meaningfully. ---
        if (a.Version == null)
        {
            return Error(DivergenceKind.BadHeader, options, archA, archB, exclA, exclB,
                $"{options.LabelA} is not a recognized opensage-deepdump file (header line: '{a.HeaderRaw}')",
                rawA: a.HeaderRaw);
        }
        if (b.Version == null)
        {
            return Error(DivergenceKind.BadHeader, options, archA, archB, exclA, exclB,
                $"{options.LabelB} is not a recognized opensage-deepdump file (header line: '{b.HeaderRaw}')",
                rawB: b.HeaderRaw);
        }

        // --- Version mismatch (exit 2): v1 R records have 8 fields, v2 have 9 (extra <type>
        // token) -- comparing across that boundary field-by-field would be meaningless, so
        // this is refused as a setup error rather than reported as a wall of R divergences. ---
        if (a.Version != b.Version)
        {
            return Error(DivergenceKind.VersionMismatch, options, archA, archB, exclA, exclB,
                $"{options.LabelA} is deepdump {a.Version} but {options.LabelB} is deepdump {b.Version} -- refusing to diff across format versions",
                rawA: a.HeaderRaw, rawB: b.HeaderRaw);
        }

        // --- No-checkpoints gate (exit 2): a dump with zero V lines produced no evidence.
        // This also catches empty-vs-empty (both legs trivially have zero V lines), so an
        // empty comparison can never silently report success. ---
        if (a.VectorLineCount == 0 || b.VectorLineCount == 0)
        {
            var culprit = a.VectorLineCount == 0 && b.VectorLineCount == 0
                ? $"both {options.LabelA} and {options.LabelB}"
                : a.VectorLineCount == 0 ? options.LabelA : options.LabelB;
            return Error(DivergenceKind.NoCheckpoints, options, archA, archB, exclA, exclB,
                $"{culprit} produced zero checkpoint (V) lines -- refusing to report a comparison result with no evidence");
        }

        // --- Cross-arch requirement (exit 2): a gate that's supposed to prove two different
        // architectures agree must actually have run on two different architectures. ---
        if (options.RequireCrossArch)
        {
            var archSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (archA != null) archSet.Add(archA);
            if (archB != null) archSet.Add(archB);
            var missing = options.RequiredArches.Where(req => !archSet.Contains(req)).ToList();
            if (missing.Count > 0)
            {
                return Error(DivergenceKind.CrossArchRequirementUnmet, options, archA, archB, exclA, exclB,
                    $"--require-cross-arch was set but the combined arch stamps ({(archSet.Count == 0 ? "none" : string.Join(", ", archSet))}) do not cover required {{{string.Join(", ", missing)}}}");
            }
        }

        // --- Lockstep body walk. Comment lines are already stripped out by the parser, so
        // this only ever sees F/C/R/E/V/Malformed lines, in file order. ---
        string? lastCommonFrame = null;
        int? channelOrdinal = null;
        string? channelName = null;

        var n = Math.Min(a.Body.Count, b.Body.Count);
        for (var i = 0; i < n; i++)
        {
            var la = a.Body[i];
            var lb = b.Body[i];

            if (la.Raw == lb.Raw)
            {
                // Stream-only dumps (DeepCrcWriter's streamOnly mode) omit every F/C/R/E line,
                // leaving only V lines to attribute a frame from -- DumpParser still populates
                // DumpLine.Frame for Vector lines, so track it here too, or a stream-only
                // comparison can never report anything but "last frame (none)".
                if (la.Kind == DumpLineKind.Frame || la.Kind == DumpLineKind.Vector)
                {
                    lastCommonFrame = la.Frame.ToString();
                }
                else if (la.Kind == DumpLineKind.ChannelBegin)
                {
                    channelOrdinal = la.ChannelOrdinal;
                    channelName = la.ChannelName;
                }
                continue;
            }

            return BuildBodyDivergence(la, lb, options, archA, archB, exclA, exclB, lastCommonFrame, channelOrdinal, channelName);
        }

        if (a.Body.Count != b.Body.Count)
        {
            var longerLabel = a.Body.Count > b.Body.Count ? options.LabelA : options.LabelB;
            var nextLine = a.Body.Count > n ? a.Body[n] : b.Body[n];
            return new DivergenceReport
            {
                Kind = DivergenceKind.LengthMismatch,
                ExitCode = 1,
                LabelA = options.LabelA,
                LabelB = options.LabelB,
                ArchA = archA,
                ArchB = archB,
                ExclusionA = exclA,
                ExclusionB = exclB,
                LastCommonFrame = lastCommonFrame,
                DivergenceFrame = lastCommonFrame,
                ChannelOrdinal = channelOrdinal,
                ChannelName = channelName,
                LineNumber = nextLine.LineNumber,
                RawA = a.Body.Count > n ? a.Body[n].Raw : null,
                RawB = b.Body.Count > n ? b.Body[n].Raw : null,
                Summary = $"DIVERGENCE: {longerLabel} continues for {Math.Abs(a.Body.Count - b.Body.Count)} more line(s) after the other ends -- one leg's run stopped early",
            };
        }

        return new DivergenceReport
        {
            Kind = DivergenceKind.None,
            ExitCode = 0,
            LabelA = options.LabelA,
            LabelB = options.LabelB,
            ArchA = archA,
            ArchB = archB,
            ExclusionA = exclA,
            ExclusionB = exclB,
            LastCommonFrame = lastCommonFrame,
            Summary = $"identical: {n} body line(s), last frame {lastCommonFrame ?? "(none)"}",
        };
    }

    private static DivergenceReport BuildBodyDivergence(
        DumpLine la, DumpLine lb, CompareOptions options,
        string? archA, string? archB, string? exclA, string? exclB,
        string? lastCommonFrame, int? channelOrdinal, string? channelName)
    {
        if (la.Kind == DumpLineKind.Record && lb.Kind == DumpLineKind.Record)
        {
            return new DivergenceReport
            {
                Kind = DivergenceKind.RecordDivergence,
                ExitCode = 1,
                LabelA = options.LabelA,
                LabelB = options.LabelB,
                ArchA = archA,
                ArchB = archB,
                ExclusionA = exclA,
                ExclusionB = exclB,
                LastCommonFrame = lastCommonFrame,
                DivergenceFrame = lastCommonFrame,
                ChannelOrdinal = channelOrdinal,
                ChannelName = channelName,
                LineNumber = la.LineNumber,
                ObjectId = la.ObjectId,
                ModuleIndex = la.ModuleIndex,
                ModuleTag = la.Tag,
                ModuleClass = la.ClassName,
                FieldName = la.FieldName,
                Tolerance = la.Tolerance,
                Type = la.Type,
                HexA = la.HexBytes,
                HexB = lb.HexBytes,
                RawA = la.Raw,
                RawB = lb.Raw,
                Summary = $"DIVERGENCE: field record differs at line {la.LineNumber} (frame {lastCommonFrame ?? "?"}, channel {channelName ?? "?"})",
            };
        }

        if (la.Kind == DumpLineKind.Vector && lb.Kind == DumpLineKind.Vector)
        {
            return new DivergenceReport
            {
                Kind = DivergenceKind.VectorDivergence,
                ExitCode = 1,
                LabelA = options.LabelA,
                LabelB = options.LabelB,
                ArchA = archA,
                ArchB = archB,
                ExclusionA = exclA,
                ExclusionB = exclB,
                LastCommonFrame = lastCommonFrame,
                DivergenceFrame = la.Frame.ToString(),
                ChannelOrdinal = channelOrdinal,
                ChannelName = channelName,
                LineNumber = la.LineNumber,
                VectorCombinedA = la.Combined,
                VectorCombinedB = lb.Combined,
                VectorChannelCrcsA = la.ChannelCrcs,
                VectorChannelCrcsB = lb.ChannelCrcs,
                RawA = la.Raw,
                RawB = lb.Raw,
                Summary = $"DIVERGENCE: checkpoint vector differs at line {la.LineNumber} (frame {la.Frame})",
            };
        }

        // Generic case: F/C lines differ, or the two sides parsed to different Kinds entirely
        // (e.g. one side's line is Malformed), or anything else not covered above.
        var divergenceFrame = la.Kind == DumpLineKind.Frame || lb.Kind == DumpLineKind.Frame
            ? $"A={(la.Kind == DumpLineKind.Frame ? la.Frame.ToString() : "?")} B={(lb.Kind == DumpLineKind.Frame ? lb.Frame.ToString() : "?")}"
            : lastCommonFrame;

        return new DivergenceReport
        {
            Kind = DivergenceKind.LineDivergence,
            ExitCode = 1,
            LabelA = options.LabelA,
            LabelB = options.LabelB,
            ArchA = archA,
            ArchB = archB,
            ExclusionA = exclA,
            ExclusionB = exclB,
            LastCommonFrame = lastCommonFrame,
            DivergenceFrame = divergenceFrame,
            ChannelOrdinal = channelOrdinal,
            ChannelName = channelName,
            LineNumber = la.LineNumber,
            RawA = la.Raw,
            RawB = lb.Raw,
            Summary = $"DIVERGENCE at line {la.LineNumber} (frame {lastCommonFrame ?? "?"}, channel {channelName ?? "?"})"
                      + (la.Kind == DumpLineKind.Malformed ? $" -- A: {la.Reason}" : "")
                      + (lb.Kind == DumpLineKind.Malformed ? $" -- B: {lb.Reason}" : ""),
        };
    }

    private static DivergenceReport Error(
        DivergenceKind kind, CompareOptions options,
        string? archA, string? archB, string? exclA, string? exclB,
        string summary, string? rawA = null, string? rawB = null)
    {
        return new DivergenceReport
        {
            Kind = kind,
            ExitCode = 2,
            LabelA = options.LabelA,
            LabelB = options.LabelB,
            ArchA = archA,
            ArchB = archB,
            ExclusionA = exclA,
            ExclusionB = exclB,
            RawA = rawA,
            RawB = rawB,
            Summary = summary,
        };
    }
}
