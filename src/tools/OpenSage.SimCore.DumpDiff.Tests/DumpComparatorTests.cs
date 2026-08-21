using System.Text.Json;
using OpenSage.SimCore.DumpDiff;
using Xunit;

namespace OpenSage.SimCore.DumpDiff.Tests;

/// <summary>
/// Fixture dumps are hand-built strings in the exact "opensage-deepdump" shape (see the header
/// comment in Program.cs), not generated via DeepCrcWriter -- this test project deliberately
/// has no reference to OpenSage.SimCore, matching the tool's own zero-dependency stance.
/// </summary>
public class DumpComparatorTests
{
    private static string Join(params string[] lines) => string.Join("\n", lines) + "\n";

    private static readonly string GoodBody = Join(
        "# opensage-deepdump v2",
        "F 0",
        "C 0 Sim",
        "R 1 0 tagX ClassX fieldX E i32 0001",
        "E 0 aabbccdd",
        "V 0 aabbccdd aabbccdd");

    private static CompareOptions Options(bool requireCrossArch = false) => new()
    {
        LabelA = "A",
        LabelB = "B",
        RequireCrossArch = requireCrossArch,
    };

    [Fact]
    public void Identical_ReportsExitZero()
    {
        var report = DumpComparator.Compare(GoodBody, GoodBody, Options());

        Assert.Equal(DivergenceKind.None, report.Kind);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal("0", report.LastCommonFrame);
    }

    [Fact]
    public void RecordDivergence_ReportsFullFieldDetail()
    {
        var a = GoodBody;
        var b = Join(
            "# opensage-deepdump v2",
            "F 0",
            "C 0 Sim",
            "R 1 0 tagX ClassX fieldX E i32 0002", // differs only in the trailing hex byte
            "E 0 aabbccdd",
            "V 0 aabbccdd aabbccdd");

        var report = DumpComparator.Compare(a, b, Options());

        Assert.Equal(DivergenceKind.RecordDivergence, report.Kind);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal("0", report.LastCommonFrame);
        Assert.Equal("0", report.DivergenceFrame);
        Assert.Equal(0, report.ChannelOrdinal);
        Assert.Equal("Sim", report.ChannelName);
        Assert.Equal("1", report.ObjectId);
        Assert.Equal("0", report.ModuleIndex);
        Assert.Equal("tagX", report.ModuleTag);
        Assert.Equal("ClassX", report.ModuleClass);
        Assert.Equal("fieldX", report.FieldName);
        Assert.Equal("E", report.Tolerance);
        Assert.Equal("i32", report.Type);
        Assert.Equal("0001", report.HexA);
        Assert.Equal("0002", report.HexB);
    }

    [Fact]
    public void VectorDivergence_ReportsFullVectorFromBothSides()
    {
        var a = GoodBody;
        var b = Join(
            "# opensage-deepdump v2",
            "F 0",
            "C 0 Sim",
            "R 1 0 tagX ClassX fieldX E i32 0001",
            "E 0 aabbccdd",
            "V 0 deadbeef aabbccdd"); // combined crc differs

        var report = DumpComparator.Compare(a, b, Options());

        Assert.Equal(DivergenceKind.VectorDivergence, report.Kind);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal("0", report.DivergenceFrame);
        Assert.Equal("aabbccdd", report.VectorCombinedA);
        Assert.Equal("deadbeef", report.VectorCombinedB);
        // Full vector is reported even though only the combined entry differs.
        Assert.Equal(new[] { "aabbccdd" }, report.VectorChannelCrcsA);
        Assert.Equal(new[] { "aabbccdd" }, report.VectorChannelCrcsB);
    }

    [Fact]
    public void PrefixTruncation_IsReportedAsDivergenceNotPass()
    {
        var longer = GoodBody + "F 1\nC 0 Sim\nE 0 aabbccdd\nV 1 aabbccdd aabbccdd\n";
        var shorter = GoodBody;

        var report = DumpComparator.Compare(longer, shorter, Options());

        Assert.Equal(DivergenceKind.LengthMismatch, report.Kind);
        Assert.Equal(1, report.ExitCode); // never a pass
        Assert.Contains("A", report.Summary);
    }

    [Fact]
    public void EmptyVsEmpty_NeverReportsSuccess()
    {
        var report = DumpComparator.Compare("", "", Options());

        Assert.NotEqual(0, report.ExitCode);
        Assert.Equal(DivergenceKind.BadHeader, report.Kind); // no header at all
    }

    [Fact]
    public void HeaderOnly_ZeroCheckpointLines_IsMalformedNotSuccess()
    {
        var headerOnly = "# opensage-deepdump v2\n";

        var report = DumpComparator.Compare(headerOnly, headerOnly, Options());

        Assert.Equal(DivergenceKind.NoCheckpoints, report.Kind);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public void OneLegHasNoVectorLines_IsMalformed()
    {
        var withVector = GoodBody;
        var withoutVector = Join(
            "# opensage-deepdump v2",
            "F 0",
            "C 0 Sim",
            "R 1 0 tagX ClassX fieldX E i32 0001",
            "E 0 aabbccdd"); // no V line

        var report = DumpComparator.Compare(withVector, withoutVector, Options());

        Assert.Equal(DivergenceKind.NoCheckpoints, report.Kind);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public void BadHeader_ExitsTwo()
    {
        var bad = "# not-a-deepdump-file\nF 0\n";

        var report = DumpComparator.Compare(bad, GoodBody, Options());

        Assert.Equal(DivergenceKind.BadHeader, report.Kind);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public void V1VsV2_RefusesToCompareAcrossVersions()
    {
        var v1 = Join(
            "# opensage-deepdump v1",
            "F 0",
            "C 0 Sim",
            "R 1 0 tagX ClassX fieldX E 0001", // 8 fields: no type token
            "E 0 aabbccdd",
            "V 0 aabbccdd aabbccdd");
        var v2 = GoodBody;

        var report = DumpComparator.Compare(v1, v2, Options());

        Assert.Equal(DivergenceKind.VersionMismatch, report.Kind);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public void CommentLines_AreStrippedFromTheWalk_ButHarvestedAsMetadata()
    {
        var withComment = Join(
            "# opensage-deepdump v2",
            "# arch=Arm64",
            "F 0",
            "C 0 Sim",
            "R 1 0 tagX ClassX fieldX E i32 0001",
            "E 0 aabbccdd",
            "V 0 aabbccdd aabbccdd");
        var withoutComment = GoodBody;

        var report = DumpComparator.Compare(withComment, withoutComment, Options());

        // The comment line must not throw off the lockstep walk.
        Assert.Equal(DivergenceKind.None, report.Kind);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal("Arm64", report.ArchA);
        Assert.Null(report.ArchB);
    }

    [Fact]
    public void ExcludeMetadata_IsHarvestedFromComments()
    {
        var withExclude = Join(
            "# opensage-deepdump v2",
            "# exclude=ChannelA,ChannelB",
            "F 0",
            "C 0 Sim",
            "R 1 0 tagX ClassX fieldX E i32 0001",
            "E 0 aabbccdd",
            "V 0 aabbccdd aabbccdd");

        var report = DumpComparator.Compare(withExclude, GoodBody, Options());

        Assert.Equal("ChannelA,ChannelB", report.ExclusionA);
        Assert.Null(report.ExclusionB);
    }

    [Fact]
    public void ExcludeOverride_TakesPrecedenceOverMetadata()
    {
        var options = new CompareOptions
        {
            LabelA = "A",
            LabelB = "B",
            ExcludeOverrideA = "Override",
        };

        var report = DumpComparator.Compare(GoodBody, GoodBody, options);

        Assert.Equal("Override", report.ExclusionA);
    }

    [Fact]
    public void RequireCrossArch_MissingBothArches_ExitsTwo()
    {
        var report = DumpComparator.Compare(GoodBody, GoodBody, Options(requireCrossArch: true));

        Assert.Equal(DivergenceKind.CrossArchRequirementUnmet, report.Kind);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public void RequireCrossArch_SameArchOnBothLegs_ExitsTwo()
    {
        var armLeg = Join(
            "# opensage-deepdump v2",
            "# arch=Arm64",
            "F 0", "C 0 Sim", "R 1 0 tagX ClassX fieldX E i32 0001", "E 0 aabbccdd", "V 0 aabbccdd aabbccdd");

        var report = DumpComparator.Compare(armLeg, armLeg, Options(requireCrossArch: true));

        Assert.Equal(DivergenceKind.CrossArchRequirementUnmet, report.Kind);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public void RequireCrossArch_OneArm64OneX64_Passes()
    {
        var armLeg = Join(
            "# opensage-deepdump v2",
            "# arch=Arm64",
            "F 0", "C 0 Sim", "R 1 0 tagX ClassX fieldX E i32 0001", "E 0 aabbccdd", "V 0 aabbccdd aabbccdd");
        var x64Leg = Join(
            "# opensage-deepdump v2",
            "# arch=X64",
            "F 0", "C 0 Sim", "R 1 0 tagX ClassX fieldX E i32 0001", "E 0 aabbccdd", "V 0 aabbccdd aabbccdd");

        var report = DumpComparator.Compare(armLeg, x64Leg, Options(requireCrossArch: true));

        Assert.Equal(DivergenceKind.None, report.Kind);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal("Arm64", report.ArchA);
        Assert.Equal("X64", report.ArchB);
    }

    [Fact]
    public void MachineReport_IsValidJsonAndCarriesTheSameFacts()
    {
        var a = GoodBody;
        var b = Join(
            "# opensage-deepdump v2",
            "F 0", "C 0 Sim", "R 1 0 tagX ClassX fieldX E i32 0002", "E 0 aabbccdd", "V 0 aabbccdd aabbccdd");

        var report = DumpComparator.Compare(a, b, Options());
        var json = report.RenderMachineJson();

        using var doc = JsonDocument.Parse(json); // throws if not valid single-line JSON
        var root = doc.RootElement;
        Assert.Equal("RecordDivergence", root.GetProperty("kind").GetString());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("0001", root.GetProperty("hexA").GetString());
        Assert.Equal("0002", root.GetProperty("hexB").GetString());
        Assert.DoesNotContain('\n', json);
    }

    // --- Stream-only dumps (DeepCrcWriter's streamOnly mode) contain only the header, optional
    // comments, and V lines -- no F/C/R/E lines at all (see DeepCrcWriter.BeginFrame /
    // BeginChannel, which return early under streamOnly). DumpParser still populates
    // DumpLine.Frame for Vector lines, so the comparator must track lastCommonFrame off V lines
    // too, or a stream-only comparison can never attribute a frame to its result. ---

    [Fact]
    public void StreamOnly_IdenticalPair_ReportsLastFrameFromVectorLines()
    {
        var streamOnly = Join(
            "# opensage-deepdump v2",
            "V 0 aabbccdd aabbccdd",
            "V 1 aabbccdd aabbccdd",
            "V 2 aabbccdd aabbccdd");

        var report = DumpComparator.Compare(streamOnly, streamOnly, Options());

        Assert.Equal(DivergenceKind.None, report.Kind);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal("2", report.LastCommonFrame);
        Assert.DoesNotContain("(none)", report.Summary);
    }

    [Fact]
    public void StreamOnly_Divergence_NamesTheCorrectFrame()
    {
        var a = Join(
            "# opensage-deepdump v2",
            "V 0 aabbccdd aabbccdd",
            "V 1 aabbccdd aabbccdd",
            "V 2 aabbccdd aabbccdd");
        var b = Join(
            "# opensage-deepdump v2",
            "V 0 aabbccdd aabbccdd",
            "V 1 aabbccdd aabbccdd",
            "V 2 deadbeef aabbccdd"); // combined crc differs at frame 2

        var report = DumpComparator.Compare(a, b, Options());

        Assert.Equal(DivergenceKind.VectorDivergence, report.Kind);
        Assert.Equal(1, report.ExitCode);
        // Frames 0 and 1 matched, so the last identical frame is 1 and the divergence is at 2.
        Assert.Equal("1", report.LastCommonFrame);
        Assert.Equal("2", report.DivergenceFrame);
        Assert.Contains("frame 2", report.Summary);
    }

    [Fact]
    public void HumanReport_OrdersFactsPerContract()
    {
        var a = GoodBody;
        var b = Join(
            "# opensage-deepdump v2",
            "F 0", "C 0 Sim", "R 1 0 tagX ClassX fieldX E i32 0002", "E 0 aabbccdd", "V 0 aabbccdd aabbccdd");

        var report = DumpComparator.Compare(a, b, Options());
        var human = report.RenderHuman();

        var legAIndex = human.IndexOf("leg A", System.StringComparison.Ordinal);
        var lastFrameIndex = human.IndexOf("last identical frame", System.StringComparison.Ordinal);
        var divergenceFrameIndex = human.IndexOf("divergence frame", System.StringComparison.Ordinal);
        var channelIndex = human.IndexOf("channel:", System.StringComparison.Ordinal);
        var fieldIndex = human.IndexOf("field fieldX", System.StringComparison.Ordinal);
        var exclusionIndex = human.IndexOf("exclusion set A", System.StringComparison.Ordinal);

        Assert.True(legAIndex < lastFrameIndex);
        Assert.True(lastFrameIndex < divergenceFrameIndex);
        Assert.True(divergenceFrameIndex < channelIndex);
        Assert.True(channelIndex < fieldIndex);
        Assert.True(fieldIndex < exclusionIndex);
    }
}
